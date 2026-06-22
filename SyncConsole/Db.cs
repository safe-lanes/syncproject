using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace SyncConsole;

public static class Db
{
    // ═══════════════════════════════════════════════════════════════════════
    // SYSTEM COLUMNS - Excluded from field-level comparison
    // These columns are metadata/timestamps that should NOT trigger conflicts
    //
    // IMPORTANT: createdBy/updatedBy are BUSINESS DATA (who created the record)
    // They MUST be synced! Only exclude TIMESTAMP columns, not user reference columns.
    // ═══════════════════════════════════════════════════════════════════════
    private static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        // Created TIMESTAMP metadata (NOT createdBy - that's business data!)
        "created_at", "createdat", "created_on", "createdon",
        "inserted_at", "insertedat",

        // Updated TIMESTAMP metadata (NOT updatedBy - that's business data!)
        "updated_at", "updatedat", "updated_on", "updatedon",
        "modified_at", "modifiedat",

        // Sync metadata
        "sync_created_at", "sync_updated_at",
        "sync_guid", "sync_batch_id", "sync_ts", "sync_version"
    };

    // ═══════════════════════════════════════════════════════════════════════
    // SOFT-DELETE COLUMNS - Excluded from field-level comparison
    // These are handled exclusively by the dedicated soft-delete propagation
    // step (monotonic 0→1, direction- and timestamp-aware). Keeping the name
    // set here as the single source of truth (also used by the sync engine).
    // ═══════════════════════════════════════════════════════════════════════
    public static readonly HashSet<string> DeleteColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "is_deleted", "isdeleted", "deleted"
    };

    // ═══════════════════════════════════════════════════════════════════════
    // HASH-ON-THE-FLY SYNC DESIGN
    // Single source of truth for hash computation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute deterministic hash for sync comparison.
    /// CRITICAL: This function must be EXACTLY the same on ship and online!
    /// 
    /// Rules:
    /// - null → empty string
    /// - trim() required
    /// - string conversion required
    /// - UTF-8 encoding
    /// 
    /// DO NOT MODIFY without updating both ship and online sync engines!
    /// </summary>
    public static string ComputeHash(object? value)
    {
        // ═══════════════════════════════════════════════════════════════════
        // CRITICAL: Normalize value to canonical string BEFORE hashing
        // This ensures the SAME logical value always produces the SAME hash
        // regardless of how MySQL/C# returns the data type.
        //
        // Without normalization:
        //   bool true → "True", sbyte 1 → "1"  → different hashes!
        //   MySqlDateTime → "01/02/2025 ..." vs DateTime → "2025-01-02T..."
        //
        // With normalization:
        //   bool true → "1", sbyte 1 → "1"  → same hash ✓
        //   MySqlDateTime → "2025-01-02T00:00:00" = DateTime → same hash ✓
        // ═══════════════════════════════════════════════════════════════════
        var normalized = NormalizeValue(value);

        // Compute SHA256 hash
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        var hashBytes = sha256.ComputeHash(bytes);

        // Convert to base64 for storage efficiency
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Normalize any MySQL/C# value to a deterministic canonical string.
    /// CRITICAL: This must produce identical output for logically equivalent values
    /// regardless of how MySqlConnector returns the CLR type.
    ///
    /// Rules:
    ///   null / DBNull         → ""
    ///   bool true             → "1"   (matches MySQL TINYINT(1) = 1)
    ///   bool false            → "0"   (matches MySQL TINYINT(1) = 0)
    ///   sbyte/byte/short etc  → invariant integer string
    ///   DateTime              → "yyyy-MM-dd HH:mm:ss" (MySQL canonical format)
    ///   MySqlDateTime valid   → "yyyy-MM-dd HH:mm:ss"
    ///   MySqlDateTime invalid → "" (zero dates like 0000-00-00)
    ///   decimal/float/double  → invariant culture string
    ///   byte[]                → Base64
    ///   everything else       → ToString().Trim()
    /// </summary>
    public static string NormalizeValue(object? value)
    {
        if (value == null || value is DBNull)
            return "";

        // ── Boolean / TINYINT(1) ─────────────────────────────────────────
        // MySqlConnector may return bool OR sbyte for TINYINT(1)
        // Normalize both to "0" / "1" so hashes always match
        if (value is bool b)
            return b ? "1" : "0";

        // sbyte is what MySqlConnector returns for TINYINT when TreatTinyAsBoolean=false
        if (value is sbyte sb)
            return sb.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ── Integer types ────────────────────────────────────────────────
        if (value is byte byteVal)
            return byteVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is short shortVal)
            return shortVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is ushort ushortVal)
            return ushortVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is int intVal)
            return intVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is uint uintVal)
            return uintVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is long longVal)
            return longVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is ulong ulongVal)
            return ulongVal.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ── Floating point ───────────────────────────────────────────────
        if (value is decimal decVal)
            return decVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is float floatVal)
            return floatVal.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        if (value is double dblVal)
            return dblVal.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);

        // ── DateTime types ───────────────────────────────────────────────
        // Use MySQL canonical format so shadow string and live value hash the same
        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss");

        if (value is MySqlConnector.MySqlDateTime mdt)
        {
            if (mdt.IsValidDateTime)
                return mdt.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss");
            return ""; // zero dates → same as NULL
        }

        if (value is DateTimeOffset dto)
            return dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        // ── Binary data ──────────────────────────────────────────────────
        if (value is byte[] bytes)
            return Convert.ToBase64String(bytes);

        // ── Guid ─────────────────────────────────────────────────────────
        if (value is Guid guid)
            return guid.ToString("D"); // lowercase with dashes

        // ── Fallback ─────────────────────────────────────────────────────
        return (value.ToString() ?? "").Trim();
    }

    [Obsolete("Sync engine is now table-agnostic. No table-name branching.")]
    public static bool IsHistoryTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return false;
        var lowerName = tableName.ToLowerInvariant();
        return lowerName.Contains("_history") || lowerName.Contains("history_") ||
               lowerName.Contains("_audit") || lowerName.Contains("audit_") ||
               lowerName.Contains("_log") || lowerName.Contains("log_") ||
               lowerName.EndsWith("history") || lowerName.EndsWith("audit") ||
               lowerName.EndsWith("log") || lowerName.StartsWith("history") ||
               lowerName.StartsWith("audit") || lowerName.StartsWith("log");
    }
    // ═══════════════════════════════════════════════════════════════════════
    // CONNECTION STRING BUILDER - Ensures proper timeout settings
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a connection string with proper timeout settings for long-running sync operations.
    /// Call this to enhance any existing connection string.
    /// </summary>
    public static string EnhanceConnectionString(string baseConnectionString)
    {
        var builder = new MySqlConnectionStringBuilder(baseConnectionString)
        {
            // Connection timeout (seconds to wait for connection)
            ConnectionTimeout = 300,

            // Command timeout (seconds to wait for query completion)
            DefaultCommandTimeout = 3600,  // 1 hour for large tables

            // Keep connection alive
            Keepalive = 60,

            // Allow multiple statements
            AllowUserVariables = true,

            // Connection pooling settings
            Pooling = true,
            MinimumPoolSize = 1,
            MaximumPoolSize = 10,
            ConnectionLifeTime = 0,  // Don't expire connections
            ConnectionIdleTimeout = 300,

            // Allow LOAD DATA LOCAL INFILE if needed
            AllowLoadLocalInfile = true,

            // SSL settings (adjust based on your environment)
            SslMode = MySqlSslMode.Preferred,

            // FIX: Handle MySQL zero datetime values  
            AllowZeroDateTime = true,
            ConvertZeroDateTime = false,  // Don't convert - Dapper handles nulls better this way

            // FIX: Do NOT auto-map CHAR(36) columns to System.Guid. These columns may
            // legitimately hold non-UUID strings (e.g. "3400037"), which makes the
            // default Guid parsing throw FormatException. Reading them as plain strings
            // is correct for a generic, table-agnostic sync.
            GuidFormat = MySqlGuidFormat.None
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Configure MySQL session for long-running sync operations.
    /// Sets aggressive timeout values to prevent connection drops.
    /// CRITICAL: Disables foreign key checks to allow out-of-order table sync.
    /// </summary>
    public static async Task SetSessionAsync(MySqlConnection cnn, ILogger log)
    {
        // ═══════════════════════════════════════════════════════════════════
        // ENHANCED TIMEOUT SETTINGS for long-running sync operations
        // ═══════════════════════════════════════════════════════════════════
        var sql = """
            SET NAMES utf8mb4 COLLATE utf8mb4_unicode_ci;
            SET SESSION collation_connection = 'utf8mb4_unicode_ci';

            -- CRITICAL: Disable foreign key checks for sync session
            -- This allows child records to be inserted before parent records
            -- FK constraints will be re-enabled after sync completes
            SET SESSION foreign_key_checks = 0;

            -- Network timeouts (in seconds)
            SET SESSION net_read_timeout = 3600;
            SET SESSION net_write_timeout = 3600;

            -- Connection timeouts (in seconds)
            SET SESSION wait_timeout = 28800;
            SET SESSION interactive_timeout = 28800;

            -- Lock wait timeout (in seconds)
            SET SESSION innodb_lock_wait_timeout = 600;

            -- Disable query execution time limit (0 = unlimited)
            SET SESSION max_execution_time = 0;

            -- Transaction isolation (read committed is usually best for sync)
            SET SESSION transaction_isolation = 'READ-COMMITTED';

            -- Increase sort buffer for large result sets
            SET SESSION sort_buffer_size = 4194304;

            -- Increase join buffer
            SET SESSION join_buffer_size = 4194304;
            """;

        try
        {
            await cnn.ExecuteAsync(sql);
            log.LogInformation("✓ MySQL session configured: FK checks OFF, extended timeouts (net_read/write=3600s, wait=28800s)");
        }
        catch (Exception ex)
        {
            // Some settings might fail on certain MySQL versions, log but continue
            log.LogWarning("Some session settings failed (this is usually OK): {Message}", ex.Message);

            // Try minimal settings
            var minimalSql = """
                SET NAMES utf8mb4;
                SET SESSION net_read_timeout = 3600;
                SET SESSION net_write_timeout = 3600;
                SET SESSION wait_timeout = 28800;
                """;
            await cnn.ExecuteAsync(minimalSql);
            log.LogInformation("✓ MySQL session configured with minimal timeout settings");
        }
    }

    /// <summary>
    /// Ensure shadow table exists in the TARGET database (not sails_master).
    /// Each client database has its own shadow table for isolation and scalability.
    /// Creates table if missing, adds columns if needed (backward compatible).
    /// Safe to call multiple times - checks if columns exist first.
    /// </summary>
    public static async Task EnsureShadowSchemaAsync(MySqlConnection cnn, string targetDb, ILogger log)
    {
        try
        {
            // Create shadow table in TARGET database (ship_db or online_db)
            // Each database has its own shadow - no sharing, no growth issues!
            var createTableSql = $@"
                CREATE TABLE IF NOT EXISTS `{targetDb}`.sync_shadow_columns (
                    table_name VARCHAR(128) NOT NULL,
                    column_name VARCHAR(128) NOT NULL,
                    record_pk VARCHAR(128) NOT NULL,
                    value_hash VARCHAR(88),
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                    last_synced_at DATETIME NULL COMMENT 'Timestamp when this column was last successfully synced',
                    PRIMARY KEY (table_name, column_name, record_pk),
                    INDEX idx_shadow_sync (table_name, column_name, record_pk, last_synced_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

            await cnn.ExecuteAsync(createTableSql);
            log.LogDebug("Shadow table ensured in {TargetDb}", targetDb);

            // Check if last_synced_at exists (for backward compatibility with old schemas)
            var checkLastSyncedSql = $@"
                SELECT COUNT(*) 
                FROM information_schema.COLUMNS 
                WHERE TABLE_SCHEMA = '{targetDb}' 
                  AND TABLE_NAME = 'sync_shadow_columns' 
                  AND COLUMN_NAME = 'last_synced_at';";

            var hasLastSynced = await cnn.ExecuteScalarAsync<int>(checkLastSyncedSql) > 0;

            if (!hasLastSynced)
            {
                log.LogInformation("🔧 Adding last_synced_at column to {TargetDb}.sync_shadow_columns...", targetDb);

                await cnn.ExecuteAsync($@"
                    ALTER TABLE `{targetDb}`.sync_shadow_columns
                    ADD COLUMN last_synced_at DATETIME NULL
                    COMMENT 'Timestamp when this column was last successfully synced';");

                log.LogInformation("✓ Shadow table updated in {TargetDb}", targetDb);
            }

            log.LogDebug("Shadow table schema is up to date in {TargetDb}", targetDb);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to ensure shadow table schema in {TargetDb} - sync may fail!", targetDb);
            throw;
        }
    }

    /// <summary>
    /// Re-enable foreign key checks after sync completes.
    /// Call this at the end of sync to restore normal FK constraint behavior.
    /// </summary>
    public static async Task EnableForeignKeyChecksAsync(MySqlConnection cnn, ILogger log)
    {
        try
        {
            await cnn.ExecuteAsync("SET SESSION foreign_key_checks = 1;");
            log.LogInformation("✓ Foreign key checks re-enabled");
        }
        catch (Exception ex)
        {
            log.LogWarning("Failed to re-enable FK checks: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Ping the connection to keep it alive during long operations.
    /// Call this periodically between large batches.
    /// </summary>
    public static async Task<bool> PingConnectionAsync(MySqlConnection cnn, ILogger log)
    {
        try
        {
            await cnn.ExecuteScalarAsync<int>("SELECT 1");
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning("Connection ping failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Ensure connection is open and healthy, reconnect if needed.
    /// </summary>
    public static async Task EnsureConnectionAsync(MySqlConnection cnn, ILogger log)
    {
        if (cnn.State != ConnectionState.Open)
        {
            log.LogWarning("Connection was closed, reopening...");
            await cnn.OpenAsync();
            await SetSessionAsync(cnn, log);
            log.LogInformation("✓ Connection reopened successfully");
        }
        else
        {
            // Verify connection is still alive
            if (!await PingConnectionAsync(cnn, log))
            {
                log.LogWarning("Connection ping failed, reconnecting...");
                await cnn.CloseAsync();
                await cnn.OpenAsync();
                await SetSessionAsync(cnn, log);
                log.LogInformation("✓ Connection reconnected successfully");
            }
        }
    }

    /// <summary>
    /// Create all sync metadata tables in TARGET database (not sails_master).
    /// Each client database has its own sync tracking tables.
    /// This prevents centralized table growth and provides natural isolation.
    /// </summary>
    public static async Task EnsureSyncTablesAsync(MySqlConnection cnn, string targetDb, ILogger log)
    {
        try
        {
            log.LogInformation("Ensuring sync tables in {TargetDb}...", targetDb);

            // Shadow table - stores LAST SYNCED VALUE, not hash!
            // Hash is computed on-the-fly for comparison
            var createShadow = $"""
            CREATE TABLE IF NOT EXISTS `{targetDb}`.sync_shadow_columns (
              table_name   VARCHAR(128) NOT NULL,
              column_name  VARCHAR(128) NOT NULL,
              record_pk    VARCHAR(128) NOT NULL,
              `last_value`   LONGTEXT NULL COMMENT 'Last synced value - hash computed on-the-fly',
              last_synced_at DATETIME NULL COMMENT 'When this value was last synced',
              PRIMARY KEY (table_name, column_name, record_pk),
              INDEX idx_shadow_sync (table_name, column_name, record_pk, last_synced_at)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

            // Conflict log - stores conflicts detected during sync
            var createConflicts = $"""
            CREATE TABLE IF NOT EXISTS `{targetDb}`.sync_conflict_log_columns (
              id                BIGINT NOT NULL AUTO_INCREMENT,
              sync_batch_id     VARCHAR(64)  NOT NULL,
              table_name        VARCHAR(128) NOT NULL,
              record_pk         VARCHAR(128) NOT NULL,
              column_name       VARCHAR(128) NOT NULL,
              conflict_type     VARCHAR(64)  NOT NULL,
              local_value       JSON         NULL,
              source_value      JSON         NULL,
              chosen_value      JSON         NULL,
              policy_applied    VARCHAR(128) NOT NULL,
              manual_required   TINYINT(1)   NOT NULL DEFAULT 1,
              resolution_status VARCHAR(64)  NOT NULL DEFAULT 'queued_manual',
              detected_at       DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
              source_db         VARCHAR(128) NULL,
              sync_direction    VARCHAR(32)  NULL,
              PRIMARY KEY (id),
              INDEX idx_batch (sync_batch_id),
              INDEX idx_table (table_name, column_name),
              INDEX idx_pk (table_name, record_pk),
              INDEX idx_detected (detected_at)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

            // Merge audit - tracks all merge decisions for debugging
            var createAudit = $"""
            CREATE TABLE IF NOT EXISTS `{targetDb}`.sync_merge_audit (
              id             BIGINT NOT NULL AUTO_INCREMENT,
              sync_batch_id  VARCHAR(64)  NOT NULL,
              table_name     VARCHAR(128) NOT NULL,
              record_pk      VARCHAR(128) NOT NULL,
              column_name    VARCHAR(128) NOT NULL,
              policy_applied VARCHAR(128) NOT NULL,
              local_old      JSON         NULL,
              chosen_new     JSON         NULL,
              redo_sql       TEXT         NULL,
              created_at     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
              source_db      VARCHAR(128) NULL,
              sync_direction VARCHAR(32)  NULL,
              PRIMARY KEY (id),
              INDEX idx_batch (sync_batch_id),
              INDEX idx_table (table_name, column_name),
              INDEX idx_created (created_at)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

            // Checkpoints - tracks sync progress for large tables
            var createCheckpoints = $"""
            CREATE TABLE IF NOT EXISTS `{targetDb}`.sync_checkpoints (
              table_name  VARCHAR(128) NOT NULL,
              last_pk     VARCHAR(128) NULL,
              updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
              PRIMARY KEY (table_name),
              INDEX idx_updated (updated_at)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

            await cnn.ExecuteAsync(createShadow);
            await cnn.ExecuteAsync(createConflicts);
            await cnn.ExecuteAsync(createAudit);
            await cnn.ExecuteAsync(createCheckpoints);

            // Check if old value_hash column exists and migrate if needed
            var checkOldHashCol = $"""
                SELECT COUNT(*) 
                FROM information_schema.COLUMNS 
                WHERE TABLE_SCHEMA = '{targetDb}' 
                  AND TABLE_NAME = 'sync_shadow_columns' 
                  AND COLUMN_NAME = 'value_hash';
                """;

            var hasOldHash = await cnn.ExecuteScalarAsync<int>(checkOldHashCol) > 0;

            if (hasOldHash)
            {
                log.LogInformation("🔧 Migrating shadow table from hash storage to value storage in {TargetDb}...", targetDb);

                // Add last_value if it doesn't exist
                var checkLastValue = $"""
                    SELECT COUNT(*) 
                    FROM information_schema.COLUMNS 
                    WHERE TABLE_SCHEMA = '{targetDb}' 
                      AND TABLE_NAME = 'sync_shadow_columns' 
                      AND COLUMN_NAME = 'last_value';
                    """;

                var hasLastValue = await cnn.ExecuteScalarAsync<int>(checkLastValue) > 0;

                if (!hasLastValue)
                {
                    await cnn.ExecuteAsync($"""
                        ALTER TABLE `{targetDb}`.sync_shadow_columns
                        ADD COLUMN `last_value` LONGTEXT NULL COMMENT 'Last synced value - hash computed on-the-fly';
                        """);
                }

                // Drop old value_hash column
                await cnn.ExecuteAsync($"""
                    ALTER TABLE `{targetDb}`.sync_shadow_columns
                    DROP COLUMN value_hash;
                    """);

                log.LogInformation("✓ Shadow table migrated to hash-on-the-fly design in {TargetDb}", targetDb);
            }

            log.LogInformation("✓ Sync tables ensured in {TargetDb}", targetDb);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create sync tables in {TargetDb}", targetDb);
            throw;
        }
    }

    /// <summary>
    /// [DEPRECATED] Old method that created tables in sails_master.
    /// Kept for backward compatibility but does nothing.
    /// All sync tables are now in each client database.
    /// </summary>
    [Obsolete("Sync tables are now created in each client database via EnsureSyncTablesAsync")]
    public static async Task EnsureMetaTablesAsync(MySqlConnection central, ILogger log)
    {
        // This method is deprecated - sync tables are now in each client database
        // Keeping this method to avoid breaking existing calls
        log.LogDebug("EnsureMetaTablesAsync is deprecated - sync tables are now per-database");
        await Task.CompletedTask;
    }



    /// <summary>
    /// Get list of active tables to sync based on direction.
    /// ship_to_online: Use offline_sync_tables (ship's table list)
    /// online_to_ship: Use online_sync_tables (online's table list)
    /// </summary>
    public static async Task<List<string>> GetActiveTablesAsync(MySqlConnection central, string direction, ILogger log)
    {
        var sql = direction?.ToLower() switch
        {
            "ship_to_online" => "SELECT tablename FROM sails_master.offline_sync_tables WHERE isActive=1 AND COALESCE(isMasterTable,0)=0 ORDER BY tablename",
            "online_to_ship" => "SELECT tablename FROM sails_master.online_sync_tables WHERE isActive=1 AND COALESCE(isMasterTable,0)=0 ORDER BY tablename",
            _ => "SELECT tablename FROM sails_master.online_sync_tables WHERE isActive=1 AND COALESCE(isMasterTable,0)=0 ORDER BY tablename"
        };

        log.LogInformation("📋 Loading table list for direction={Direction}", direction);
        var list = (await central.QueryAsync<string>(sql)).ToList();
        log.LogInformation("✓ Found {Count} active tables for direction={Direction}", list.Count, direction);

        if (list.Count == 0)
        {
            log.LogWarning("⚠️ No tables found! Check your sync_tables configuration.");
        }

        return list;
    }

    /// <summary>
    /// Get table metadata including PK, updated column, and comparison columns
    /// CRITICAL: Uses SystemColumns to exclude timestamp/metadata fields from comparison
    /// </summary>
    public static async Task<TableMeta?> GetTableMetaAsync(MySqlConnection central, string table, string localDb)
    {
        var cols = await central.QueryAsync<(string COLUMN_NAME, string DATA_TYPE, string COLUMN_KEY)>(
            """
            SELECT COLUMN_NAME, DATA_TYPE, COLUMN_KEY
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """,
            new { db = localDb, table });

        var colList = cols.ToList();
        if (colList.Count == 0) return null;

        // Find primary key
        var pk = colList.FirstOrDefault(c => c.COLUMN_KEY == "PRI").COLUMN_NAME ?? "";
        if (string.IsNullOrEmpty(pk)) return null;

        // Find updated timestamp column (for LWW reference, not comparison)
        var updated = colList.FirstOrDefault(c =>
            new[] { "updated_at", "updatedat", "modified_at", "modifiedat" }
                .Contains(c.COLUMN_NAME.ToLower())
        ).COLUMN_NAME;

        // Check for soft-delete column
        var hasDeleted = colList.Any(c => DeleteColumnNames.Contains(c.COLUMN_NAME));

        // ═══════════════════════════════════════════════════════════════════
        // CRITICAL FIX: Use SystemColumns HashSet to exclude ALL system columns
        // This prevents updatedAt/createdAt from causing false conflict detection
        //
        // Also exclude the soft-delete column: it is handled exclusively by the
        // dedicated soft-delete propagation step (monotonic 0→1, direction- and
        // timestamp-aware). Including it in the field-level merge would let the
        // generic 3-way merge un-delete rows (1→0) and bypass that step's guard.
        // ═══════════════════════════════════════════════════════════════════
        var compareCols = colList
            .Where(c => c.COLUMN_KEY != "PRI"
                && !SystemColumns.Contains(c.COLUMN_NAME.ToLower())
                && !DeleteColumnNames.Contains(c.COLUMN_NAME))
            .Select(c => c.COLUMN_NAME)
            .ToList();

        return new TableMeta(pk, updated, hasDeleted, compareCols);
    }

    /// <summary>
    /// Get list of column names for a table in a specific database
    /// Used for Ship DB column preference filtering
    /// </summary>
    public static async Task<HashSet<string>> GetTableColumnsAsync(MySqlConnection cnn, string database, string table)
    {
        var cols = await cnn.QueryAsync<string>(
            """
            SELECT COLUMN_NAME
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
            """,
            new { db = database, table });

        return new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get business key columns for a table (columns that form a logical unique key).
    /// Used to prevent duplicate inserts when same record has different PKs on ship vs online.
    ///
    /// GENERIC SOLUTION - Works for ALL tables automatically:
    /// 1. First, check for UNIQUE indexes (excluding PK) - these are explicit business keys
    /// 2. Then, check for FOREIGN KEY columns - these typically form part of business key
    /// 3. Finally, use all non-PK, non-system columns as business key (most conservative)
    /// </summary>
    public static async Task<List<string>> GetBusinessKeyColumnsAsync(MySqlConnection cnn, string db, string table)
    {
        var result = new List<string>();

        try
        {
            // ═══════════════════════════════════════════════════════════════
            // Strategy 1: Check for UNIQUE indexes (most reliable)
            // If table has a UNIQUE constraint, use those columns
            // ═══════════════════════════════════════════════════════════════
            var uniqueIndexCols = await cnn.QueryAsync<string>($@"
                SELECT DISTINCT COLUMN_NAME
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = @db
                  AND TABLE_NAME = @table
                  AND NON_UNIQUE = 0
                  AND INDEX_NAME != 'PRIMARY'
                ORDER BY SEQ_IN_INDEX",
                new { db, table });

            var uniqueCols = uniqueIndexCols.ToList();
            if (uniqueCols.Count > 0)
            {
                return uniqueCols;
            }

            // ═══════════════════════════════════════════════════════════════
            // Strategy 2: Get ALL Foreign Key columns from this table
            // FK columns typically form the business key (parentId + childId)
            // ═══════════════════════════════════════════════════════════════
            var fkColumns = await cnn.QueryAsync<string>($@"
                SELECT DISTINCT COLUMN_NAME
                FROM information_schema.KEY_COLUMN_USAGE
                WHERE TABLE_SCHEMA = @db
                  AND TABLE_NAME = @table
                  AND REFERENCED_TABLE_NAME IS NOT NULL
                ORDER BY ORDINAL_POSITION",
                new { db, table });

            var fkCols = fkColumns.ToList();
            if (fkCols.Count > 0)
            {
                // FK columns form the business key
                return fkCols;
            }

            // ═══════════════════════════════════════════════════════════════
            // Strategy 3: Auto-detect columns ending in "Id" (naming convention)
            // Most FK columns follow the pattern: parentTableId, entityId, etc.
            // ═══════════════════════════════════════════════════════════════
            var idColumns = await cnn.QueryAsync<string>($@"
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db
                  AND TABLE_NAME = @table
                  AND COLUMN_KEY != 'PRI'
                  AND (
                      COLUMN_NAME LIKE '%Id'
                      OR COLUMN_NAME LIKE '%_id'
                      OR COLUMN_NAME LIKE '%ID'
                  )
                  AND DATA_TYPE IN ('int', 'bigint', 'varchar', 'char')
                ORDER BY ORDINAL_POSITION",
                new { db, table });

            var idCols = idColumns.ToList();
            if (idCols.Count > 0)
            {
                return idCols;
            }

            // ═══════════════════════════════════════════════════════════════
            // Strategy 4 (FALLBACK): Use ALL non-PK, non-system columns
            // This is the most conservative approach - prevents ANY duplicate data
            // Only triggered if no FK/Id columns found
            // ═══════════════════════════════════════════════════════════════
            var allCols = await cnn.QueryAsync<string>($@"
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db
                  AND TABLE_NAME = @table
                  AND COLUMN_KEY != 'PRI'
                  AND COLUMN_NAME NOT IN (
                      'created_at', 'createdat', 'created_on', 'createdon',
                      'updated_at', 'updatedat', 'updated_on', 'updatedon',
                      'modified_at', 'modifiedat', 'inserted_at', 'insertedat',
                      'createdBy', 'created_by', 'updatedBy', 'updated_by',
                      'sync_created_at', 'sync_updated_at', 'sync_guid',
                      'sync_batch_id', 'sync_ts', 'sync_version'
                  )
                  AND DATA_TYPE NOT IN ('blob', 'longblob', 'mediumblob', 'tinyblob')
                ORDER BY ORDINAL_POSITION
                LIMIT 5",
                new { db, table });

            var allColsList = allCols.ToList();
            if (allColsList.Count > 0)
            {
                return allColsList;
            }
        }
        catch
        {
            // If detection fails, return empty list (no business key check)
        }

        return result;
    }

    /// <summary>
    /// Get the soft-delete flag column name for a table
    /// </summary>
    public static async Task<string?> GetDeletedFlagColumnAsync(MySqlConnection cnn, string db, string table)
    {
        var col = await cnn.ExecuteScalarAsync<string?>(
            """
            SELECT COLUMN_NAME
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table
              AND LOWER(COLUMN_NAME) IN ('is_deleted','isdeleted','deleted')
            LIMIT 1
            """, new { db, table });
        return col;
    }

    /// <summary>
    /// Read a batch of primary keys that exist on both source and target
    /// </summary>
    public static async Task<List<string>> ReadBatchKeysAsync(
        MySqlConnection cnn, string targetDb, string sourceDb, string table, string pkCol, int offset, int batch)
    {
        var sql = $@"
SELECT CAST(t.`{pkCol}` AS CHAR) AS pk
FROM `{targetDb}`.`{table}` t
JOIN `{sourceDb}`.`{table}` s ON s.`{pkCol}` = t.`{pkCol}`
ORDER BY t.`{pkCol}`
LIMIT {batch} OFFSET {offset};";
        return (await cnn.QueryAsync<string>(sql)).ToList();
    }

    /// <summary>
    /// Load target value, source value, and timestamps for a batch of PKs and single column
    /// </summary>
    /// <summary>
    /// Load target value, source value, and timestamps for a batch of PKs and single column
    /// </summary>
    public static async Task<Dictionary<string, (object? tval, object? sval, DateTime? tt, DateTime? ts)>> LoadValueTriplesAsync(
        MySqlConnection cnn, string targetDb, string sourceDb, string table, string pkCol,
        IEnumerable<string> pks, string column, string? updatedCol)
    {
        var pkList = pks.ToList();
        if (pkList.Count == 0) return new();

        var inList = string.Join(",", pkList.Select(p => $"'{MySqlHelper.EscapeString(p)}'"));
        string selectTs = string.IsNullOrEmpty(updatedCol)
            ? "NULL AS tt, NULL AS ts"
            : $"t.`{updatedCol}` AS tt, s.`{updatedCol}` AS ts";

        //        var sql = $@"
        //SELECT CAST(t.`{pkCol}` AS CHAR) AS pk, t.`{column}` AS tval, s.`{column}` AS sval, {selectTs}
        //FROM `{targetDb}`.`{table}` t
        //JOIN `{sourceDb}`.`{table}` s ON s.`{pkCol}`=t.`{pkCol}`
        //WHERE t.`{pkCol}` IN ({inList});";

        var sql = $@"
SELECT
    CAST(t.`{pkCol}` AS CHAR) AS pk,
    CAST(t.`{column}` AS CHAR) AS tval,
    CAST(s.`{column}` AS CHAR) AS sval,
    {selectTs}
FROM `{targetDb}`.`{table}` t
JOIN `{sourceDb}`.`{table}` s
    ON s.`{pkCol}` = t.`{pkCol}`
WHERE t.`{pkCol}` IN ({inList});";

        // Use dynamic to handle MySqlDateTime conversion issues with zero dates
        var rows = await cnn.QueryAsync<dynamic>(sql);
        var result = new Dictionary<string, (object? tval, object? sval, DateTime? tt, DateTime? ts)>();

        foreach (var row in rows)
        {
            var rowDict = (IDictionary<string, object?>)row;
            string pk = rowDict["pk"]?.ToString() ?? "";
            object? tval = rowDict["tval"];
            object? sval = rowDict["sval"];
            DateTime? tt = SafeConvertToDateTime(rowDict["tt"]);
            DateTime? ts = SafeConvertToDateTime(rowDict["ts"]);

            result[pk] = (tval, sval, tt, ts);
        }

        return result;
    }

    /// <summary>
    /// Safely convert MySqlDateTime or other date types to DateTime?
    /// Handles zero dates (0000-00-00) by returning null
    /// </summary>
    private static DateTime? SafeConvertToDateTime(object? value)
    {
        if (value == null || value == DBNull.Value)
            return null;

        // Handle MySqlDateTime (which may have zero dates)
        if (value is MySqlDateTime mySqlDateTime)
        {
            // Check if it's a valid date (not 0000-00-00)
            if (mySqlDateTime.IsValidDateTime)
                return mySqlDateTime.GetDateTime();
            else
                return null; // Zero date, return null
        }

        // Handle regular DateTime
        if (value is DateTime dateTime)
            return dateTime;

        // Try to parse string representation
        if (value is string strValue && DateTime.TryParse(strValue, out var parsed))
            return parsed;

        return null;
    }

    /// <summary>
    /// Load shadow LAST VALUES (not hashes) from TARGET database.
    /// Hash is computed on-the-fly for comparison.
    /// Each database has its own shadow table - no centralization, no growth issues.
    /// </summary>
    /// <summary>
    /// Load shadow LAST VALUES (not hashes) from TARGET database.
    /// Hash is computed on-the-fly for comparison.
    /// Each database has its own shadow table - no centralization, no growth issues.
    /// FIX: Handle MySqlDateTime zero datetime values manually
    /// </summary>
    public static async Task<Dictionary<string, (string? lastValue, DateTime? lastSyncedAt)>> LoadShadowMapAsync(
        MySqlConnection cnn, string targetDb, string table, string column, IEnumerable<string> pks, ILogger? log = null)
    {
        var pkList = pks.ToList();
        if (pkList.Count == 0) return new();

        var inList = string.Join(",", pkList.Select(p => $"'{MySqlHelper.EscapeString(p)}'"));
        var sql = $@"
SELECT record_pk, `last_value`, last_synced_at
FROM `{targetDb}`.sync_shadow_columns
WHERE table_name=@table 
  AND column_name=@col
  AND record_pk IN ({inList});";

        try
        {
            // FIX: Use dynamic to handle MySqlDateTime values
            var rows = await cnn.QueryAsync(sql, new { table, col = column });

            var result = new Dictionary<string, (string? lastValue, DateTime? lastSyncedAt)>();

            foreach (var row in rows)
            {
                string recordPk = row.record_pk;
                string? lastValue = row.last_value;

                // Handle MySqlDateTime conversion manually
                DateTime? lastSyncedAt = null;
                var dateTimeValue = row.last_synced_at;

                if (dateTimeValue != null)
                {
                    // Check if it's MySqlDateTime (zero datetime)
                    if (dateTimeValue is MySqlConnector.MySqlDateTime mySqlDt)
                    {
                        if (mySqlDt.IsValidDateTime)
                        {
                            lastSyncedAt = mySqlDt.GetDateTime();
                        }
                        // else: zero datetime (0000-00-00) - leave as null
                    }
                    else if (dateTimeValue is DateTime dt)
                    {
                        lastSyncedAt = dt;
                    }
                }

                result[recordPk] = (lastValue, lastSyncedAt);
            }

            return result;
        }
        catch (MySqlException ex) when (ex.Message.Contains("doesn't exist"))
        {
            // Shadow table doesn't exist yet - will be created
            log?.LogDebug("Shadow table doesn't exist yet in {TargetDb} - will be created", targetDb);
            return new();
        }
        catch (MySqlException ex)
        {
            log?.LogError(ex, "Failed to load shadow from {TargetDb}/{Table}/{Column}: {Error}",
                targetDb, table, column, ex.Message);
            return new();
        }
    }

    /// <summary>
    /// Upsert shadow LAST VALUES (not hashes) to TARGET database.
    /// Hash is computed on-the-fly only for comparison, never stored.
    /// Each database has its own shadow table - prevents growth issues.
    /// With retry logic for transient failures.
    /// CRITICAL: Throws MySqlException if upsert fails after all retries.
    /// SyncEngine MUST catch this exception and ABORT sync to prevent corruption.
    /// Updates last_synced_at to current timestamp (UTC_TIMESTAMP) on successful sync.
    /// </summary>
    public static async Task UpsertShadowAsync(
        MySqlConnection cnn, string targetDb, string table, string column,
        IEnumerable<(string pk, string? value)> rows, ILogger? log = null, int maxRetries = 3)
    {
        const int CHUNK = 200; // Reduced chunk size for stability
        var list = rows.ToList();

        if (list.Count == 0) return;

        int totalUpserted = 0;
        for (int i = 0; i < list.Count; i += CHUNK)
        {
            var slice = list.Skip(i).Take(CHUNK).ToList();
            if (slice.Count == 0) continue;

            var vals = string.Join(",",
                slice.Select(r =>
                {
                    // ═══════════════════════════════════════════════════════════════
                    // CRITICAL FIX: NEVER store NULL in last_value
                    // Normalize null → "" (empty string) to prevent "corrupt shadow" warnings
                    // ═══════════════════════════════════════════════════════════════
                    var normalizedValue = r.value?.ToString() ?? "";  // NULL → ""
                    var escapedValue = $"'{MySqlHelper.EscapeString(normalizedValue)}'";
                    return $"(@t,@c,'{MySqlHelper.EscapeString(r.pk)}',{escapedValue},UTC_TIMESTAMP())";
                }));

            var sql = $@"
INSERT INTO `{targetDb}`.sync_shadow_columns(table_name, column_name, record_pk, `last_value`, last_synced_at)
VALUES {vals}
ON DUPLICATE KEY UPDATE 
    `last_value`=VALUES(`last_value`), 
    last_synced_at=VALUES(last_synced_at);";

            // ADDED: Detect and warn about NULL values in shadow

            // Retry logic for transient failures
            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    await cnn.ExecuteAsync(sql, new { t = table, c = column });
                    totalUpserted += slice.Count;
                    break; // Success, exit retry loop
                }
                catch (MySqlException ex) when (retry < maxRetries - 1 && IsTransientError(ex))
                {
                    log?.LogWarning("Shadow upsert failed for {TargetDb}/{Table}/{Col} (attempt {Attempt}/{Max}): {Error}. Retrying...",
                        targetDb, table, column, retry + 1, maxRetries, ex.Message);
                    await Task.Delay(1000 * (retry + 1)); // Exponential backoff

                    // Try to ensure connection is still alive
                    if (cnn.State != ConnectionState.Open)
                    {
                        await cnn.OpenAsync();
                    }
                }
                catch (MySqlException ex)
                {
                    // Final attempt failed or non-transient error
                    log?.LogError(ex, "❌ Shadow upsert FAILED for {TargetDb}/{Table}/{Col} after {Retries} retries: {Error}",
                        targetDb, table, column, maxRetries, ex.Message);
                    throw;
                }
            }
        }

        log?.LogDebug("✓ Shadow upserted: {Count} rows for {TargetDb}/{Table}/{Col}",
            totalUpserted, targetDb, table, column);
    }


    /// <summary>
    /// Bulk update column values using temporary table + JOIN.
    /// CRITICAL PERFORMANCE: Replaces N individual UPDATEs with 1 bulk operation.
    /// 
    /// Performance comparison:
    /// - Per-row UPDATE: 1000 rows = 1000 queries = 20-25 minutes
    /// - Bulk UPDATE: 1000 rows = 1 query = < 10 seconds
    /// 
    /// Works by:
    /// 1. Creating temp table with (pk, value) pairs
    /// 2. Bulk inserting all updates into temp table
    /// 3. Single UPDATE ... JOIN to apply all changes
    /// 4. Cleanup temp table
    /// </summary>
    /// <param name="conn">MySQL connection</param>
    /// <param name="targetDb">Target database name</param>
    /// <param name="table">Table name</param>
    /// <param name="column">Column to update</param>
    /// <param name="pkColumn">Primary key column name</param>
    /// <param name="updates">List of (pk, value) pairs to update</param>
    /// <param name="log">Logger for diagnostics</param>
    /// <returns>Number of rows actually updated</returns>
    public static async Task<int> BulkUpdateColumnAsync(
        MySqlConnection conn,
        string targetDb,
        string table,
        string column,
        string pkColumn,
        IEnumerable<(string pk, object? value)> updates,
        ILogger? log = null)
    {
        var list = updates.ToList();
        if (list.Count == 0) return 0;

        var tempTable = $"tmp_upd_{Guid.NewGuid():N}";

        try
        {
            await conn.ExecuteAsync($"USE `{targetDb}`");
            // Create temporary table (MEMORY engine for speed)
            await conn.ExecuteAsync($@"
            CREATE TEMPORARY TABLE `{tempTable}` (
                `pk` VARCHAR(255) NOT NULL,
                `val` LONGTEXT,
                PRIMARY KEY (`pk`)
            ) ENGINE=InnoDB");  // FIX #3: InnoDB supports TEXT/BLOB

            // Bulk insert into temp table (chunked for safety)
            const int CHUNK_SIZE = 1000;
            int totalInserted = 0;

            for (int i = 0; i < list.Count; i += CHUNK_SIZE)
            {
                var chunk = list.Skip(i).Take(CHUNK_SIZE).ToList();
                var values = string.Join(",", chunk.Select(x =>
                {
                    var escapedPk = MySqlHelper.EscapeString(x.pk);
                    // FIX: Use NormalizeValue for consistent type conversion
                    // This ensures bool true → "1" (not "True" which MySQL treats as 0)
                    var normalizedVal = NormalizeValue(x.value);
                    if (x.value == null || x.value is DBNull)
                        return $"('{escapedPk}', NULL)";
                    var escapedVal = $"'{MySqlHelper.EscapeString(normalizedVal)}'";
                    return $"('{escapedPk}', {escapedVal})";
                }));

                await conn.ExecuteAsync($"INSERT INTO `{tempTable}` (pk, val) VALUES {values}");
                totalInserted += chunk.Count;
            }

            log?.LogDebug("Inserted {Count} updates into temp table for {Table}.{Column}",
                totalInserted, table, column);

            // Single bulk UPDATE via JOIN
            var sql = $@"
            UPDATE `{targetDb}`.`{table}` t
            INNER JOIN `{tempTable}` tmp ON t.`{pkColumn}` = tmp.pk
            SET t.`{column}` = tmp.val";

            var affected = await conn.ExecuteAsync(sql);

            log?.LogDebug("✓ Bulk updated {Affected}/{Total} rows in {Table}.{Column}",
                affected, totalInserted, table, column);

            return affected;
        }
        catch (MySqlException ex)
        {
            // FIX #4: Fallback to safe update - NEVER abort sync
            log?.LogWarning("Bulk update failed for {Table}.{Column}: {Error} - using fallback",
                table, column, ex.Message);

            return await SafeUpdateColumnAsync(conn, targetDb, table, column, pkColumn, list, log);
        }
        finally
        {
            // Always cleanup temp table
            try
            {
                await conn.ExecuteAsync($"DROP TEMPORARY TABLE IF EXISTS `{tempTable}`");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
    /// <summary>
    /// FIX #4: Safe fallback when bulk update fails
    /// Uses CASE-WHEN - never aborts sync
    /// </summary>
    public static async Task<int> SafeUpdateColumnAsync(
        MySqlConnection conn,
        string targetDb,
        string table,
        string column,
        string pkColumn,
        IEnumerable<(string pk, object? value)> updates,
        ILogger? log = null)
    {
        var list = updates.ToList();
        if (list.Count == 0) return 0;

        log?.LogWarning("⚠️ Using fallback CASE-WHEN update for {Table}.{Column} ({Count} rows)",
            table, column, list.Count);

        try
        {
            var caseWhen = string.Join(" ", list.Select(u =>
            {
                var escapedPk = MySqlHelper.EscapeString(u.pk);
                // FIX: Use NormalizeValue for consistent type conversion
                if (u.value == null || u.value is DBNull)
                    return $"WHEN '{escapedPk}' THEN NULL";
                var normalizedValue = NormalizeValue(u.value);
                var escapedVal = MySqlHelper.EscapeString(normalizedValue);
                return $"WHEN '{escapedPk}' THEN '{escapedVal}'";
            }));

            var pks = string.Join(",", list.Select(u => $"'{MySqlHelper.EscapeString(u.pk)}'"));

            var sql = $@"
                UPDATE `{targetDb}`.`{table}`
                SET `{column}` = CASE `{pkColumn}`
                    {caseWhen}
                    ELSE `{column}`
                END
                WHERE `{pkColumn}` IN ({pks})";

            var affected = await conn.ExecuteAsync(sql);

            log?.LogInformation("✓ Fallback update completed: {Affected}/{Total} rows",
                affected, list.Count);

            return affected;
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "❌ Fallback update failed for {Table}.{Column}", table, column);
            throw;
        }
    }
    /// <summary>
    /// Check if a MySQL exception is transient (retryable)
    /// </summary>
    private static bool IsTransientError(MySqlException ex)
    {
        // Common transient error codes
        return ex.Number switch
        {
            1205 => true,  // Lock wait timeout
            1213 => true,  // Deadlock
            2006 => true,  // MySQL server has gone away
            2013 => true,  // Lost connection during query
            2055 => true,  // Lost connection to MySQL server
            _ => false
        };
    }

    /// <summary>
    /// Save sync checkpoint for resumable sync in TARGET database
    /// </summary>
    public static async Task SaveCheckpointAsync(MySqlConnection cnn, string targetDb, string table, string? lastPk)
    {
        await cnn.ExecuteAsync(
            $"""
            INSERT INTO `{targetDb}`.sync_checkpoints(table_name, last_pk)
            VALUES (@table, @lastPk)
            ON DUPLICATE KEY UPDATE last_pk = VALUES(last_pk)
            """, new { table, lastPk });
    }

    /// <summary>
    /// Load sync checkpoint for resumable sync from TARGET database
    /// </summary>
    public static async Task<string?> LoadCheckpointAsync(MySqlConnection cnn, string targetDb, string table)
    {
        return await cnn.ExecuteScalarAsync<string?>(
            $"""
            SELECT last_pk
            FROM `{targetDb}`.sync_checkpoints
            WHERE table_name = @table
            """, new { table });
    }
}