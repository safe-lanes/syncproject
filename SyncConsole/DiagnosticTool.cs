using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace SyncConsole;

/// <summary>
/// Diagnostic tool to identify first-time sync issues
/// </summary>
public static class DiagnosticTool
{
    public static async Task RunDiagnosticsAsync(MySqlConnection conn, string onlineDb, string shipDb, ILogger log)
    {
        log.LogInformation("═══════════════════════════════════════════════════════════════");
        log.LogInformation("DIAGNOSTIC: First-Time Sync Issues Analysis");
        log.LogInformation("═══════════════════════════════════════════════════════════════");

        // Tables to check (from QA issues)
        var tables = new[]
        {
            // QA Issue tables
            "preparationchecklist",      // Issue #1: createdBy/updatedBy not updating
            "action",                    // Issue #2: action data not syncing (FK: observationDetailId)
            "inspectionmaster",          // Issue #3, #7: auditorId, drugAlcoholScope, inspectorId
            "inspectionauditscope",      // Issue #4: not syncing (FK: inspectionMasterId, auditScopeId)
            "inspectioncrew",            // Issue #5: not syncing (FK: inspectionRecordId)
            "inspection_office_representative", // Issue #6: green tick, Issued By (FK: observationId)
            "observationdetail",         // Parent of action table
            "observationcomment",        // Issue #8: duplicate entries

            // Previously checked tables
            "auditor",
            "preparation",
            "nmnearmissimmediatecause",
            "nmnearmissrootcause",
            "nearmiss"
        };

        foreach (var table in tables)
        {
            await CheckTableAsync(conn, onlineDb, shipDb, table, log);
        }

        // Check shadow status
        await CheckShadowStatusAsync(conn, onlineDb, tables, log);

        // Check shadow status in the other DB too
        log.LogInformation("\n=== SHADOW TABLE STATUS (Ship DB) ===");
        await CheckShadowStatusAsync(conn, shipDb, tables, log);

        // Check specific columns mentioned in QA issues
        log.LogInformation("\n═══════════════════════════════════════════════════════════════");
        log.LogInformation("DIAGNOSTIC: QA-Specific Column Checks");
        log.LogInformation("═══════════════════════════════════════════════════════════════");
        await CheckQASpecificColumnsAsync(conn, onlineDb, shipDb, log);

        // Check observation comment duplicates (QA Issue #8)
        await CheckObservationCommentDuplicatesAsync(conn, onlineDb, shipDb, log);
    }

    /// <summary>
    /// Check for duplicate observation comments (QA Issue #8)
    /// </summary>
    private static async Task CheckObservationCommentDuplicatesAsync(MySqlConnection conn, string onlineDb, string shipDb, ILogger log)
    {
        log.LogInformation("\n=== OBSERVATION COMMENT DUPLICATES (QA Issue #8) ===");

        try
        {
            // Check if table exists
            var tableExists = await conn.ExecuteScalarAsync<int>($@"
                SELECT COUNT(*) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'observationcomment'",
                new { db = onlineDb });

            if (tableExists == 0)
            {
                log.LogWarning("observationcomment table not found in {Db}", onlineDb);
                return;
            }

            // Get PK column
            var pkCol = await conn.ExecuteScalarAsync<string>($@"
                SELECT COLUMN_NAME FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'observationcomment' AND COLUMN_KEY = 'PRI' LIMIT 1",
                new { db = onlineDb });

            // Get business key columns (exclude PK, timestamps)
            var cols = await conn.QueryAsync<string>($@"
                SELECT COLUMN_NAME FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'observationcomment'
                  AND COLUMN_KEY != 'PRI'
                  AND COLUMN_NAME NOT IN ('created_at', 'updated_at', 'createdAt', 'updatedAt', 'id')
                  AND DATA_TYPE IN ('varchar', 'text', 'int', 'bigint')
                ORDER BY ORDINAL_POSITION
                LIMIT 5",
                new { db = onlineDb });

            var bizCols = cols.ToList();
            if (bizCols.Count == 0)
            {
                log.LogWarning("No business columns found for duplicate check");
                return;
            }

            var groupCols = string.Join(", ", bizCols.Select(c => $"`{c}`"));

            // Check ONLINE for duplicates
            var dupOnlineSql = $@"
                SELECT {groupCols}, COUNT(*) as cnt, GROUP_CONCAT(`{pkCol}`) as pks
                FROM `{onlineDb}`.`observationcomment`
                GROUP BY {groupCols}
                HAVING COUNT(*) > 1
                LIMIT 10";
            var dupOnline = (await conn.QueryAsync(dupOnlineSql)).ToList();

            if (dupOnline.Count > 0)
            {
                log.LogWarning("⚠️ ONLINE observationcomment duplicates found: {Count} groups", dupOnline.Count);
                foreach (var dup in dupOnline.Take(5))
                {
                    var dict = (IDictionary<string, object>)dup;
                    log.LogWarning("  PKs: {PKs}, Count: {Count}", dict["pks"], dict["cnt"]);
                }
            }
            else
            {
                log.LogInformation("✓ No duplicates in ONLINE observationcomment");
            }

            // Check SHIP for duplicates
            var tableExistsShip = await conn.ExecuteScalarAsync<int>($@"
                SELECT COUNT(*) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'observationcomment'",
                new { db = shipDb });

            if (tableExistsShip > 0)
            {
                var dupShipSql = $@"
                    SELECT {groupCols}, COUNT(*) as cnt, GROUP_CONCAT(`{pkCol}`) as pks
                    FROM `{shipDb}`.`observationcomment`
                    GROUP BY {groupCols}
                    HAVING COUNT(*) > 1
                    LIMIT 10";
                var dupShip = (await conn.QueryAsync(dupShipSql)).ToList();

                if (dupShip.Count > 0)
                {
                    log.LogWarning("⚠️ SHIP observationcomment duplicates found: {Count} groups", dupShip.Count);
                }
                else
                {
                    log.LogInformation("✓ No duplicates in SHIP observationcomment");
                }
            }

            // Compare row counts
            var onlineCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{onlineDb}`.`observationcomment`");
            var shipCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{shipDb}`.`observationcomment`");
            log.LogInformation("Row counts: ONLINE={Online}, SHIP={Ship}", onlineCount, shipCount);

            if (onlineCount != shipCount)
            {
                log.LogWarning("⚠️ Row count mismatch! Diff={Diff}", Math.Abs(onlineCount - shipCount));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error checking observation comment duplicates");
        }
    }

    /// <summary>
    /// Check specific columns mentioned in QA issues for value mismatches
    /// </summary>
    private static async Task CheckQASpecificColumnsAsync(MySqlConnection conn, string onlineDb, string shipDb, ILogger log)
    {
        // QA Issue columns to check
        var qaColumns = new[]
        {
            ("preparationchecklist", "createdBy"),
            ("preparationchecklist", "updatedBy"),
            ("inspectionmaster", "auditorId"),
            ("inspectionmaster", "drugAlcoholScope"),
            ("inspectionmaster", "drugAlcoholScopeDesc"),
            ("inspectionmaster", "inspectorId"),
            ("inspection_office_representative", "issuedBy"),
            ("inspection_office_representative", "greenTick")
        };

        foreach (var (table, column) in qaColumns)
        {
            try
            {
                // Check if column exists in both DBs
                var existsOnline = await conn.ExecuteScalarAsync<int>($@"
                    SELECT COUNT(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table AND COLUMN_NAME = @col",
                    new { db = onlineDb, table, col = column });

                var existsShip = await conn.ExecuteScalarAsync<int>($@"
                    SELECT COUNT(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table AND COLUMN_NAME = @col",
                    new { db = shipDb, table, col = column });

                if (existsOnline == 0 || existsShip == 0)
                {
                    log.LogWarning("⚠️ {Table}.{Column}: Column missing! Online={Online}, Ship={Ship}",
                        table, column, existsOnline > 0, existsShip > 0);
                    continue;
                }

                // Get PK column
                var pkCol = await conn.ExecuteScalarAsync<string>($@"
                    SELECT COLUMN_NAME FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table AND COLUMN_KEY = 'PRI' LIMIT 1",
                    new { db = onlineDb, table });

                if (string.IsNullOrEmpty(pkCol))
                {
                    log.LogWarning("⚠️ {Table}.{Column}: No PK found", table, column);
                    continue;
                }

                // Count differences
                var diffSql = $@"
                    SELECT COUNT(*) FROM `{onlineDb}`.`{table}` t
                    JOIN `{shipDb}`.`{table}` s ON s.`{pkCol}` = t.`{pkCol}`
                    WHERE COALESCE(CAST(t.`{column}` AS CHAR), '') != COALESCE(CAST(s.`{column}` AS CHAR), '')";
                var diffCount = await conn.ExecuteScalarAsync<int>(diffSql);

                if (diffCount > 0)
                {
                    log.LogWarning("⚠️ {Table}.{Column}: {Count} value differences between Online and Ship",
                        table, column, diffCount);

                    // Show sample differences
                    var sampleSql = $@"
                        SELECT t.`{pkCol}` as pk, t.`{column}` as online_val, s.`{column}` as ship_val
                        FROM `{onlineDb}`.`{table}` t
                        JOIN `{shipDb}`.`{table}` s ON s.`{pkCol}` = t.`{pkCol}`
                        WHERE COALESCE(CAST(t.`{column}` AS CHAR), '') != COALESCE(CAST(s.`{column}` AS CHAR), '')
                        LIMIT 3";
                    var samples = await conn.QueryAsync(sampleSql);
                    foreach (var sample in samples)
                    {
                        var dict = (IDictionary<string, object>)sample;
                        log.LogInformation("    PK={Pk}: Online='{Online}' vs Ship='{Ship}'",
                            dict["pk"], dict["online_val"], dict["ship_val"]);
                    }
                }
                else
                {
                    log.LogInformation("✓ {Table}.{Column}: Values match in both databases", table, column);
                }
            }
            catch (Exception ex)
            {
                log.LogDebug("Error checking {Table}.{Column}: {Error}", table, column, ex.Message);
            }
        }
    }

    private static async Task CheckTableAsync(MySqlConnection conn, string onlineDb, string shipDb, string table, ILogger log)
    {
        try
        {
            log.LogInformation("\n=== {Table} ===", table.ToUpper());

            // Get PK column
            var pkCol = await conn.ExecuteScalarAsync<string>($@"
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table AND COLUMN_KEY = 'PRI'
                LIMIT 1", new { db = onlineDb, table });

            if (string.IsNullOrEmpty(pkCol))
            {
                log.LogWarning("  Table {Table} not found or has no primary key", table);
                return;
            }

            // Row counts
            var onlineCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{onlineDb}`.`{table}`");
            var shipCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{shipDb}`.`{table}`");
            log.LogInformation("  Row counts: ONLINE={Online}, SHIP={Ship}", onlineCount, shipCount);

            // Records on SHIP but NOT on ONLINE (should be inserted during ship_to_online)
            var shipOnlySql = $@"
                SELECT COUNT(*) FROM `{shipDb}`.`{table}` s
                LEFT JOIN `{onlineDb}`.`{table}` t ON t.`{pkCol}` = s.`{pkCol}`
                WHERE t.`{pkCol}` IS NULL";
            var shipOnlyCount = await conn.ExecuteScalarAsync<int>(shipOnlySql);

            if (shipOnlyCount > 0)
            {
                log.LogWarning("  ⚠️ SHIP-ONLY records (not synced to ONLINE): {Count}", shipOnlyCount);

                // Get sample PKs
                var samplePks = await conn.QueryAsync<string>($@"
                    SELECT s.`{pkCol}` FROM `{shipDb}`.`{table}` s
                    LEFT JOIN `{onlineDb}`.`{table}` t ON t.`{pkCol}` = s.`{pkCol}`
                    WHERE t.`{pkCol}` IS NULL
                    LIMIT 5");
                log.LogInformation("    Sample PKs: {PKs}", string.Join(", ", samplePks));
            }
            else
            {
                log.LogInformation("  ✓ All SHIP records exist on ONLINE");
            }

            // Records on ONLINE but NOT on SHIP (should be inserted during online_to_ship)
            var onlineOnlySql = $@"
                SELECT COUNT(*) FROM `{onlineDb}`.`{table}` t
                LEFT JOIN `{shipDb}`.`{table}` s ON s.`{pkCol}` = t.`{pkCol}`
                WHERE s.`{pkCol}` IS NULL";
            var onlineOnlyCount = await conn.ExecuteScalarAsync<int>(onlineOnlySql);

            if (onlineOnlyCount > 0)
            {
                log.LogWarning("  ⚠️ ONLINE-ONLY records (not synced to SHIP): {Count}", onlineOnlyCount);
            }
            else
            {
                log.LogInformation("  ✓ All ONLINE records exist on SHIP");
            }

            // Check for value differences (data mismatch despite same PK)
            var cols = await conn.QueryAsync<string>($@"
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
                  AND COLUMN_KEY != 'PRI'
                  AND COLUMN_NAME NOT IN ('created_at','updated_at','createdAt','updatedAt','sync_created_at','sync_updated_at')
                ORDER BY ORDINAL_POSITION", new { db = onlineDb, table });

            var colList = cols.ToList();
            int totalDiffs = 0;

            // Check ALL columns for differences
            foreach (var col in colList)
            {
                try
                {
                    var diffSql = $@"
                        SELECT COUNT(*) FROM `{onlineDb}`.`{table}` t
                        JOIN `{shipDb}`.`{table}` s ON s.`{pkCol}` = t.`{pkCol}`
                        WHERE COALESCE(CAST(t.`{col}` AS CHAR), '') != COALESCE(CAST(s.`{col}` AS CHAR), '')";
                    var diffCount = await conn.ExecuteScalarAsync<int>(diffSql);

                    if (diffCount > 0)
                    {
                        totalDiffs += diffCount;
                        log.LogWarning("    Column '{Col}': {Count} value differences", col, diffCount);
                    }
                }
                catch
                {
                    // Skip columns that can't be compared
                }
            }

            if (totalDiffs == 0)
            {
                log.LogInformation("  ✓ All {Count} column values match", colList.Count);
            }
            else
            {
                log.LogWarning("  ⚠️ Total value differences found: {Count} across {Cols} columns", totalDiffs, colList.Count);
            }

            // Check for potential duplicates based on business key (for B2 tables)
            if (table.Contains("immediate") || table.Contains("root"))
            {
                await CheckForDuplicatesAsync(conn, onlineDb, shipDb, table, pkCol, log);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error checking table {Table}", table);
        }
    }

    private static async Task CheckForDuplicatesAsync(MySqlConnection conn, string onlineDb, string shipDb, string table, string pkCol, ILogger log)
    {
        try
        {
            // Get all non-PK columns to check for potential business key duplicates
            var cols = await conn.QueryAsync<string>($@"
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
                  AND COLUMN_KEY != 'PRI'
                  AND DATA_TYPE IN ('varchar', 'int', 'bigint', 'tinyint')
                ORDER BY ORDINAL_POSITION
                LIMIT 5", new { db = onlineDb, table });

            var colList = cols.ToList();
            if (colList.Count == 0) return;

            // Check if there are rows with same data but different PKs
            var groupCols = string.Join(", ", colList.Select(c => $"`{c}`"));

            // Check ONLINE for duplicates
            var dupOnlineSql = $@"
                SELECT COUNT(*) as dups FROM (
                    SELECT {groupCols}, COUNT(*) as cnt
                    FROM `{onlineDb}`.`{table}`
                    GROUP BY {groupCols}
                    HAVING COUNT(*) > 1
                ) sub";
            var dupOnlineCount = await conn.ExecuteScalarAsync<int>(dupOnlineSql);

            // Check SHIP for duplicates
            var dupShipSql = $@"
                SELECT COUNT(*) as dups FROM (
                    SELECT {groupCols}, COUNT(*) as cnt
                    FROM `{shipDb}`.`{table}`
                    GROUP BY {groupCols}
                    HAVING COUNT(*) > 1
                ) sub";
            var dupShipCount = await conn.ExecuteScalarAsync<int>(dupShipSql);

            if (dupOnlineCount > 0 || dupShipCount > 0)
            {
                log.LogWarning("  ⚠️ POTENTIAL DUPLICATES: ONLINE={Online}, SHIP={Ship} (based on columns: {Cols})",
                    dupOnlineCount, dupShipCount, string.Join(", ", colList));
            }
            else
            {
                log.LogInformation("  ✓ No duplicate business records detected");
            }
        }
        catch (Exception ex)
        {
            log.LogDebug("Duplicate check skipped for {Table}: {Error}", table, ex.Message);
        }
    }

    private static async Task CheckShadowStatusAsync(MySqlConnection conn, string targetDb, string[] tables, ILogger log)
    {
        log.LogInformation("\n=== SHADOW TABLE STATUS ===");

        try
        {
            var shadowSql = $@"
                SELECT table_name, COUNT(DISTINCT record_pk) as pk_count, COUNT(*) as total_entries
                FROM `{targetDb}`.sync_shadow_columns
                WHERE table_name IN @tables
                GROUP BY table_name
                ORDER BY table_name";

            var results = await conn.QueryAsync<(string table_name, int pk_count, int total_entries)>(
                shadowSql, new { tables });

            foreach (var (tableName, pkCount, totalEntries) in results)
            {
                log.LogInformation("  {Table}: {PKs} PKs, {Entries} total shadow entries",
                    tableName, pkCount, totalEntries);
            }

            // Check for tables with NO shadow entries
            var tablesWithShadow = results.Select(r => r.table_name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tablesWithoutShadow = tables.Where(t => !tablesWithShadow.Contains(t)).ToList();

            if (tablesWithoutShadow.Any())
            {
                log.LogWarning("  ⚠️ Tables with NO shadow entries: {Tables}",
                    string.Join(", ", tablesWithoutShadow));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error checking shadow status");
        }
    }
}
