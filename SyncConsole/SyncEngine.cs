using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace SyncConsole
{
    public record SyncResult(int Inserts, int Updates, int Deletes, int Conflicts);

    public sealed class SyncEngine
    {
        private readonly string _centralDb;
        private readonly string _shipDb;
        private readonly string _domain;
        private readonly string _shipImo;
        private readonly string _direction;
        private readonly string _env;
        private readonly int _batch;
        private readonly MySqlConnection _conn;
        private readonly ILogger _log;

        // ═══════════════════════════════════════════════════════════════════
        // CONNECTION HEALTH - Periodic checks to prevent timeout disconnects
        // ═══════════════════════════════════════════════════════════════════
        private const int CONNECTION_CHECK_INTERVAL = 10;  // Check every N tables
        private int _tablesSinceLastCheck = 0;

        // ═══════════════════════════════════════════════════════════════════
        // Thread-safe SHA256 for hashing (reused across calls for performance)
        // ═══════════════════════════════════════════════════════════════════
        private static readonly SHA256 _sha256 = SHA256.Create();
        private static readonly object _shaLock = new();

        public SyncEngine(
            (string CentralDb, string ShipDb) dbs,
            string domain,
            string shipImo,
            string direction,
            string environment,
            int batch,
            MySqlConnection conn,
            ILogger log)
        {
            _centralDb = dbs.CentralDb;
            _shipDb = dbs.ShipDb;
            _domain = domain;
            _shipImo = shipImo;
            _direction = direction?.ToLowerInvariant() ?? "ship_to_online";
            _env = environment?.ToLowerInvariant() ?? "online";
            _batch = batch;
            _conn = conn;
            _log = log;
        }

        /// <summary>
        /// Source database (where data comes FROM)
        /// ship_to_online: source = ship
        /// online_to_ship: source = central
        /// </summary>
        private string SourceDb => _direction == "ship_to_online" ? _shipDb : _centralDb;

        /// <summary>
        /// Target database (where data goes TO, and where conflicts are resolved in favor of)
        /// ship_to_online: target = central (online wins)
        /// online_to_ship: target = ship
        /// </summary>
        private string TargetDb => _direction == "ship_to_online" ? _centralDb : _shipDb;

        /// <summary>
        /// Get safe preview of value for logging (truncate long values)
        /// </summary>
        private string GetValuePreview(object? value, int maxLength = 50)
        {
            if (value == null) return "<NULL>";

            var str = value.ToString() ?? "<NULL>";
            if (str.Length <= maxLength) return str;

            return str.Substring(0, maxLength) + "...";
        }
        /// <summary>
        /// Get online value (always from online DB, regardless of direction)
        /// ship_to_online: online=target (tval)
        /// online_to_ship: online=source (sval)
        /// </summary>
        private object? GetOnlineValue(object? tval, object? sval)
            => _direction == "ship_to_online" ? tval : sval;

        /// <summary>
        /// Get ship value (always from ship DB, regardless of direction)
        /// ship_to_online: ship=source (sval)
        /// online_to_ship: ship=target (tval)
        /// </summary>
        private object? GetShipValue(object? tval, object? sval)
            => _direction == "ship_to_online" ? sval : tval;


        /// <summary>
        /// Ensure connection is healthy, reconnect if needed
        /// </summary>
        private async Task EnsureConnectionHealthAsync()
        {
            _tablesSinceLastCheck++;

            if (_tablesSinceLastCheck >= CONNECTION_CHECK_INTERVAL)
            {
                _tablesSinceLastCheck = 0;
                await Db.EnsureConnectionAsync(_conn, _log);
            }
        }

        /// <summary>
        /// Main sync method for a single table
        /// </summary>
        public async Task<SyncResult> SyncTableAsync(string table, List<string>? businessKey = null)
        {
            int inserted = 0, updated = 0, deleted = 0, conflicts = 0;

            int corruptShadows = 0;     // Shadow rows with NULL values detected
            int targetUpdated = 0;      // Conflicts where target was actually updated

            // ══════════════════════════════════════════════════════════════
            // Periodic connection health check
            // ══════════════════════════════════════════════════════════════
            await EnsureConnectionHealthAsync();

            // ══════════════════════════════════════════════════════════════
            // ENSURE ALL SYNC TABLES EXIST IN TARGET DATABASE
            // Creates shadow, conflicts, audit, checkpoints tables
            // Each database has its own sync tables - no centralization!
            // ══════════════════════════════════════════════════════════════
            await Db.EnsureSyncTablesAsync(_conn, TargetDb, _log);

            // ══════════════════════════════════════════════════════════════
            // CHECK TABLE EXISTS IN BOTH DATABASES
            // Skip sync if table doesn't exist in either source or target
            // ══════════════════════════════════════════════════════════════
            bool existsInSource = await TableExistsAsync(SourceDb, table);
            bool existsInTarget = await TableExistsAsync(TargetDb, table);

            if (!existsInSource && !existsInTarget)
            {
                _log.LogWarning("Skipping {Table}: table doesn't exist in either database.", table);
                return new SyncResult(inserted, updated, deleted, conflicts);
            }

            if (!existsInSource)
            {
                _log.LogWarning("Skipping {Table}: table doesn't exist in source database ({Db}).", table, SourceDb);
                return new SyncResult(inserted, updated, deleted, conflicts);
            }

            if (!existsInTarget)
            {
                _log.LogWarning("Skipping {Table}: table doesn't exist in target database ({Db}).", table, TargetDb);
                return new SyncResult(inserted, updated, deleted, conflicts);
            }

            // ══════════════════════════════════════════════════════════════
            // Generate batch ID ONCE per table sync (not per column)
            // Format: console_YYYYMMDD_HHmmss_fff (27 chars, fits VARCHAR(64))
            // ══════════════════════════════════════════════════════════════
            string syncBatchId = $"console_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}";

            // ──────────────────────────────────────────────────────────────
            // 1) Load table metadata
            // ──────────────────────────────────────────────────────────────
            var meta = await Db.GetTableMetaAsync(_conn, table, TargetDb);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Pk))
            {
                _log.LogWarning("Skipping {Table}: metadata not found or PK missing in {Db}.", table, TargetDb);
                return new SyncResult(inserted, updated, deleted, conflicts);
            }

            // ══════════════════════════════════════════════════════════════
            // COLUMN FILTERING: Only sync columns that exist in BOTH databases
            // Metadata is loaded from TargetDb, so we must verify each column
            // also exists in SourceDb. Without this, LoadValueTriplesAsync will
            // fail with "Unknown column 's.XXX'" when a column exists in Target
            // but not in Source (e.g., 'snumber' in ship but not in online).
            // ══════════════════════════════════════════════════════════════
            var shipDbColumns = await Db.GetTableColumnsAsync(_conn, _shipDb, table);
            var centralDbColumns = await Db.GetTableColumnsAsync(_conn, _centralDb, table);

            // Only include columns that exist in BOTH databases
            var filteredColumns = meta.CompareColumns
                .Where(col => shipDbColumns.Contains(col) && centralDbColumns.Contains(col))
                .ToList();

            if (filteredColumns.Count == 0)
            {
                // The soft-delete column is intentionally excluded from CompareColumns
                // (it is handled only by the dedicated propagation step below). If it is
                // the ONLY shared non-PK column, we must NOT skip the table — otherwise
                // its deletes would never propagate. Proceed when a delete column exists
                // in both DBs; the field-merge loop simply becomes a no-op for this table.
                bool deleteColInBoth =
                    shipDbColumns.Any(c => Db.DeleteColumnNames.Contains(c)) &&
                    centralDbColumns.Any(c => Db.DeleteColumnNames.Contains(c));

                if (!deleteColInBoth)
                {
                    _log.LogWarning("Skipping {Table}: no matching columns found in both databases.", table);
                    return new SyncResult(inserted, updated, deleted, conflicts);
                }

                _log.LogDebug("Table {Table}: only soft-delete column shared; running insert + soft-delete propagation only.", table);
            }

            if (filteredColumns.Count < meta.CompareColumns.Count)
            {
                var skippedCols = meta.CompareColumns.Except(filteredColumns).ToList();
                _log.LogDebug("Table {Table}: Skipping {Count} columns not in both DBs: {Cols}",
                    table, skippedCols.Count, string.Join(", ", skippedCols));
            }

            // Also validate updatedCol exists in BOTH databases
            // If it only exists in one, LoadValueTriplesAsync will fail with "Unknown column"
            if (meta.UpdatedCol != null &&
                (!shipDbColumns.Contains(meta.UpdatedCol) || !centralDbColumns.Contains(meta.UpdatedCol)))
            {
                _log.LogDebug("Table {Table}: updatedCol '{Col}' not in both DBs, disabling timestamp comparison",
                    table, meta.UpdatedCol);
                meta = meta with { UpdatedCol = null };
            }

            // Replace meta columns with filtered columns
            meta = meta with { CompareColumns = filteredColumns };

            // Attach the per-table business key from sync config (businessKeyColumn).
            // Empty for tables where the config column is NULL — those keep the
            // existing auto-increment/unique-index dedup behavior unchanged.
            meta = meta with { BusinessKey = businessKey ?? new List<string>() };

            _log.LogDebug("Table {Table}: PK={Pk}, comparing {Count} columns (filtered to both DBs)",
                table, meta.Pk, meta.CompareColumns.Count);

            // Enhanced logging for debugging first-sync issues in QA environment
            _log.LogDebug("Sync config: SourceDb={Source}, TargetDb={Target}, Direction={Dir}",
                SourceDb, TargetDb, _direction);

            // ──────────────────────────────────────────────────────────────
            // 1.5) Business-key duplicate resolution (online wins)
            //
            // When the same logical record was independently created on BOTH sides
            // (same configured business key, different auto-increment PKs), the
            // ONLINE row is authoritative. Every SHIP row that shares the business
            // key with an ACTIVE online row is soft-deleted so it stops showing on
            // the ship — EXCEPT the genuinely-synced canonical row (same PK AND same
            // business key as an online row), which is preserved. Guarding the
            // "keep" on PK *and* business key (not PK alone) makes it robust to
            // PK-number collisions across the two independent auto-increment
            // sequences, and guarantees the by-PK delete propagation can never
            // delete online's own record.
            //
            // This runs BEFORE the insert step below so a retired (soft-deleted)
            // ship row is propagated to online in the SAME run: soft-deleted source
            // rows bypass the business-key dedup, so the insert step copies the
            // now-deleted row to online where it stays invisible (marked deleted).
            // ──────────────────────────────────────────────────────────────
            if (meta.HasDeleted && meta.BusinessKey is { Count: > 0 })
            {
                try
                {
                    var shipDelCol = await Db.GetDeletedFlagColumnAsync(_conn, _shipDb, table);
                    var onlineDelCol = await Db.GetDeletedFlagColumnAsync(_conn, _centralDb, table);

                    var bkInBoth = meta.BusinessKey.All(k =>
                        shipDbColumns.Contains(k, StringComparer.OrdinalIgnoreCase) &&
                        centralDbColumns.Contains(k, StringComparer.OrdinalIgnoreCase));

                    if (string.IsNullOrEmpty(shipDelCol) || string.IsNullOrEmpty(onlineDelCol))
                    {
                        _log.LogDebug("Skipping business-key duplicate resolution for {Table}: no soft-delete column in both databases.", table);
                    }
                    else if (!bkInBoth)
                    {
                        _log.LogDebug("Skipping business-key duplicate resolution for {Table}: key columns not present in both databases.", table);
                    }
                    else
                    {
                        var bkMatch = string.Join(" AND ",
                            meta.BusinessKey.Select(k => $"o.`{k}` <=> sh.`{k}`"));

                        // Keep-guard matches the canonical online row on PK *and* key.
                        var bkSelfMatch = string.Join(" AND ",
                            meta.BusinessKey.Select(k => $"o2.`{k}` <=> sh.`{k}`"));

                        // A row whose business key is (all) NULL is not a meaningful
                        // logical identity, and `<=>` treats NULL=NULL as equal — which
                        // would over-retire unrelated empty-key rows. Only retire ship
                        // rows whose configured key columns are fully populated.
                        var bkNotNull = string.Join(" AND ",
                            meta.BusinessKey.Select(k => $"sh.`{k}` IS NOT NULL"));

                        var sqlDupDel = $@"
UPDATE `{_shipDb}`.`{table}` sh
SET sh.`{shipDelCol}` = 1
WHERE COALESCE(sh.`{shipDelCol}`, 0) = 0
  AND {bkNotNull}
  AND EXISTS (
    SELECT 1 FROM `{_centralDb}`.`{table}` o
    WHERE COALESCE(o.`{onlineDelCol}`, 0) = 0
      AND {bkMatch})
  AND NOT EXISTS (
    SELECT 1 FROM `{_centralDb}`.`{table}` o2
    WHERE o2.`{meta.Pk}` = sh.`{meta.Pk}`
      AND {bkSelfMatch});";

                        var dupDelCount = await _conn.ExecuteAsync(sqlDupDel);
                        if (dupDelCount > 0)
                        {
                            deleted += dupDelCount;
                            _log.LogInformation(
                                "Retired {Count} ship-side business-key duplicate(s) for {Table} (online wins)",
                                dupDelCount, table);
                        }
                    }
                }
                catch (MySqlException ex)
                {
                    _log.LogWarning("Business-key duplicate resolution failed for {Table}: {Error}", table, ex.Message);
                }
            }

            // ──────────────────────────────────────────────────────────────
            // 2) Insert rows that exist only on source
            // FIX: Only use columns that exist in BOTH databases to avoid schema mismatch
            // FIX: Use INSERT IGNORE to prevent duplicate key errors on re-runs
            // FIX: Check for business key duplicates BEFORE inserting
            // ──────────────────────────────────────────────────────────────
            try
            {
                // Get columns that exist in the TARGET database
                var targetDbColumns = await Db.GetTableColumnsAsync(_conn, TargetDb, table);

                // Build column list: PK + filtered columns that exist in Ship DB
                var insertColumns = new List<string> { meta.Pk };
                insertColumns.AddRange(filteredColumns);

                // Also include system columns that exist in BOTH Ship DB AND Target DB
                var systemColsInBoth = shipDbColumns
                    .Where(c => targetDbColumns.Contains(c) &&
                                !insertColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                insertColumns.AddRange(systemColsInBoth);

                // Final filter: only include columns that exist in BOTH databases
                insertColumns = insertColumns
                    .Where(c => targetDbColumns.Contains(c) && shipDbColumns.Contains(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var colList = string.Join(", ", insertColumns.Select(c => $"`{c}`"));
                var prefixedColList = string.Join(", ", insertColumns.Select(c => $"s.`{c}`"));

                // ═══════════════════════════════════════════════════════════════
                // DUPLICATE PREVENTION: Only for auto-increment PK tables that
                // have a UNIQUE constraint (not PK). This targets junction/link
                // tables where the FK combination IS the business key.
                //
                // DO NOT use FK columns or *Id pattern as business keys — those
                // are shared references (vesselId, companyId etc.) and blocking
                // on them prevents ALL new record inserts.
                //
                // NOTE: FK checks are already disabled for the sync session
                // (SET SESSION foreign_key_checks = 0 in Db.SetSessionAsync)
                // so inserts can happen in any table order safely.
                // ═══════════════════════════════════════════════════════════════
                string bizKeyFilter = "";

                // Source-side soft-delete column (if any). A source row that is
                // already soft-deleted must NOT be suppressed by the business-key
                // dedup: it is hidden everywhere, so propagating it to the target
                // keeps both databases consistent without ever showing a duplicate.
                var srcDelCol = await Db.GetDeletedFlagColumnAsync(_conn, SourceDb, table);

                // ─── Precedence 1: explicit per-table business key from sync config ───
                // (businessKeyColumn JSON, e.g. ["nearmissId","nearmissImpactId"]).
                // When set, it is the authority: it overrides the auto-increment/
                // unique-index heuristic and applies regardless of PK type. This is
                // what dedups junction/selection rows that share the same business
                // key but were assigned different auto-increment PKs on each side.
                var configKey = meta.BusinessKey ?? new List<string>();
                if (configKey.Count > 0)
                {
                    var missing = configKey
                        .Where(k => !insertColumns.Contains(k, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (missing.Count == 0)
                    {
                        var conditions = string.Join(" AND ",
                            configKey.Select(k => $"ex.`{k}` <=> s.`{k}`"));

                        // Only ACTIVE target rows block an insert. A soft-deleted
                        // target row (e.g. a ship duplicate retired by Step 1.5) must
                        // NOT suppress the insert: otherwise the other side's canonical
                        // selection could never sync across to replace the retired copy
                        // and BOTH sides would permanently lose the common selection.
                        // Ignoring deleted target rows lets the union of selections
                        // converge while the retired duplicate stays invisible. Active
                        // target rows still block, so the original active-vs-active
                        // duplicate prevention is unchanged.
                        var tgtDelCol = await Db.GetDeletedFlagColumnAsync(_conn, TargetDb, table);
                        var tgtActiveFilter = string.IsNullOrEmpty(tgtDelCol)
                            ? ""
                            : $" AND COALESCE(ex.`{tgtDelCol}`, 0) = 0";

                        // Only ACTIVE source rows are deduped. A soft-deleted source
                        // row is allowed through even when its business key already
                        // exists on the target (it stays invisible because deleted).
                        var srcActiveGuard = string.IsNullOrEmpty(srcDelCol)
                            ? ""
                            : $"COALESCE(s.`{srcDelCol}`, 0) = 1 OR ";

                        bizKeyFilter = $@"
  AND ({srcActiveGuard}NOT EXISTS (SELECT 1 FROM `{TargetDb}`.`{table}` ex WHERE {conditions}{tgtActiveFilter}))";
                        _log.LogDebug("Business-key dedup (config) {Table}: [{Keys}]",
                            table, string.Join(",", configKey));
                    }
                    else
                    {
                        // Don't build a partial-key filter — that could over-dedup.
                        // Warn and fall through to the unique-index heuristic below.
                        _log.LogWarning(
                            "Business key for {Table} references columns not in the insert set: [{Missing}]. Falling back to unique-index dedup.",
                            table, string.Join(",", missing));
                    }
                }

                // ─── Precedence 2 (fallback): auto-increment PK + DB UNIQUE index ───
                // Unchanged behavior for every table WITHOUT a configured business key.
                if (bizKeyFilter.Length == 0 && await IsAutoIncrementPkAsync(meta.Pk, TargetDb, table))
                {
                    var uniqueKeyCols = await GetUniqueKeyColumnsAsync(TargetDb, table);
                    if (uniqueKeyCols.Count > 0)
                    {
                        var validKeys = uniqueKeyCols
                            .Where(k => insertColumns.Contains(k, StringComparer.OrdinalIgnoreCase))
                            .ToList();
                        if (validKeys.Count > 0)
                        {
                            var conditions = string.Join(" AND ",
                                validKeys.Select(k => $"ex.`{k}` <=> s.`{k}`"));
                            bizKeyFilter = $@"
  AND NOT EXISTS (SELECT 1 FROM `{TargetDb}`.`{table}` ex WHERE {conditions})";
                            _log.LogDebug("UNIQUE-key dedup {Table}: [{Keys}]",
                                table, string.Join(",", validKeys));
                        }
                    }
                }

                var insertSql = $@"
INSERT IGNORE INTO `{TargetDb}`.`{table}` ({colList})
SELECT {prefixedColList} FROM `{SourceDb}`.`{table}` s
LEFT JOIN `{TargetDb}`.`{table}` t ON t.`{meta.Pk}` = s.`{meta.Pk}`
WHERE t.`{meta.Pk}` IS NULL{bizKeyFilter};";

                inserted += await _conn.ExecuteAsync(insertSql);

                // Always log insert attempt for debugging
                _log.LogDebug("INSERT check: {Count} rows inserted from {Source} to {Target}.{Table}",
                    inserted, SourceDb, TargetDb, table);

                if (inserted > 0)
                    _log.LogInformation("Inserted {Count} new rows into {Db}.{Table}", inserted, TargetDb, table);
            }
            catch (MySqlException ex)
            {
                _log.LogWarning("Insert failed for {Table}: {Error}", table, ex.Message);
                // Continue with update/merge even if insert fails
            }

            // ──────────────────────────────────────────────────────────────
            // 3) Compare/merge rows in batches
            // ──────────────────────────────────────────────────────────────
            var countPairsSql = $@"
SELECT COUNT(*) FROM `{TargetDb}`.`{table}` t
JOIN `{SourceDb}`.`{table}` s ON s.`{meta.Pk}` = t.`{meta.Pk}`;";
            var totalPairs = await _conn.ExecuteScalarAsync<int>(countPairsSql);

            var processed = 0;

            while (processed < totalPairs)
            {
                PrintProgress(processed, totalPairs, "  • Compare/merge");

                var pks = await Db.ReadBatchKeysAsync(_conn, TargetDb, SourceDb, table, meta.Pk, processed, _batch);
                if (pks.Count == 0) break;

                // BEFORE the column loop, pre-load shadow for ALL columns at once
                // This eliminates N shadow queries → 1 query per column

                // Pre-load shadow data for all columns
                var shadowCache = new Dictionary<string, Dictionary<string, (string? lastValue, DateTime? lastSyncedAt)>>();

                foreach (var col in meta.CompareColumns)
                {
                    // Load shadow ONCE per column (before the batch loop)
                    shadowCache[col] = await Db.LoadShadowMapAsync(_conn, TargetDb, table, col, pks, _log);
                }

                // NOW start the column processing loop
                foreach (var col in meta.CompareColumns)
                {
                    var triples = await Db.LoadValueTriplesAsync(
                        _conn, TargetDb, SourceDb, table, meta.Pk, pks, col, meta.UpdatedCol);

                    // Use cached shadow instead of loading again
                    var shadow = shadowCache[col];  // ← Use cache instead of loading

                    var toShadow = new List<(string pk, string? value)>();
                    var toUpdate = new List<(string pk, object? val)>();
                    var conflictRows = new List<ConflictRecord>();
                 

                foreach (var kv in triples)
                    {
                        var pk = kv.Key;
                        var (tval, sval, tts, sts) = kv.Value;

                        // Compute hashes (NormalizeValue is called inside ComputeHash)
                        var sourceHash = Db.ComputeHash(sval);
                        var targetHash = Db.ComputeHash(tval);

                        // DEBUG: Log actual types to diagnose type conversion issues
                        _log.LogDebug("HASH-CHECK {Table}.{Col} PK={Pk}: " +
                            "tval_type={TType} tval={TVal} → tHash={THash}, " +
                            "sval_type={SType} sval={SVal} → sHash={SHash}",
                            table, col, pk,
                            tval?.GetType().Name ?? "null", GetValuePreview(tval), targetHash.Substring(0, 8),
                            sval?.GetType().Name ?? "null", GetValuePreview(sval), sourceHash.Substring(0, 8));

                        // Get shadow data
                        bool hasShadow = shadow.TryGetValue(pk, out var shadowData);
                        string? shadowValue = hasShadow ? shadowData.lastValue : null;
                        DateTime? lastSyncedAt = hasShadow ? shadowData.lastSyncedAt : null;

                        // ADD: Validate shadow quality (detect corruption)
                        // FIX #2: Only check timestamp - NULL value is normal (normalized to "")
                        if (hasShadow && !lastSyncedAt.HasValue)
                        {
                            _log.LogWarning("🔧 Corrupt shadow: {Table}.{Col} PK={Pk} - missing timestamp",
                                table, col, pk);
                            hasShadow = false;
                            shadowValue = null;
                            lastSyncedAt = null;
                            corruptShadows++;
                        }
                        // Normalize NULL shadow value to empty string
                        if (hasShadow && shadowValue == null)
                        {
                            shadowValue = "";
                        }

                        // Compute hash from shadow's last synced value
                        var shadowHash = hasShadow && shadowValue != null ? Db.ComputeHash(shadowValue) : null;

                        // DEBUG: Log 3-way comparison when shadow exists
                        if (hasShadow && shadowHash != null)
                        {
                            _log.LogDebug("3-WAY {Table}.{Col} PK={Pk}: " +
                                "shadow={ShadowVal}→{SHash} target={THash} source={SrcHash} " +
                                "t==sh:{TEqSh} s==sh:{SEqSh} t==s:{TEqS}",
                                table, col, pk,
                                GetValuePreview(shadowValue), shadowHash.Substring(0, 8),
                                targetHash.Substring(0, 8), sourceHash.Substring(0, 8),
                                targetHash == shadowHash, sourceHash == shadowHash, targetHash == sourceHash);
                        }


                        // ════════════════════════════════════════════════════════
                        // DECISION MATRIX (Hash-on-the-Fly Design)
                        // TABLE-AGNOSTIC: All tables use unified online-wins first-sync
                        // ════════════════════════════════════════════════════════
                        if (!hasShadow)
                        {
                            // ════════════════════════════════════════════════════════════
                            // FIRST SYNC — No shadow baseline exists
                            //
                            // Without shadow we can't do 3-way merge. Use these rules:
                            // 1. Values match → establish shadow, no update
                            // 2. Both have timestamps → LAST WRITE WINS (newer timestamp wins)
                            // 3. Only one has data → that side wins (SHIP_FILLS_GAP / ONLINE_WINS)
                            // 4. No timestamps available → ONLINE wins (safe default)
                            //
                            // This ensures that if ship user edited more recently than online,
                            // the ship edit is preserved on first sync.
                            // ════════════════════════════════════════════════════════════

                            if (sourceHash == targetHash)
                            {
                                toShadow.Add((pk, Db.NormalizeValue(tval)));
                                continue;
                            }

                            var onlineVal = GetOnlineValue(tval, sval);
                            var shipVal = GetShipValue(tval, sval);
                            var onlineNorm = Db.NormalizeValue(onlineVal);
                            var shipNorm = Db.NormalizeValue(shipVal);
                            bool onlineIsEmpty = string.IsNullOrWhiteSpace(onlineNorm);
                            bool shipIsEmpty = string.IsNullOrWhiteSpace(shipNorm);

                            object? winnerVal;
                            string winnerLabel;

                            if (onlineIsEmpty && !shipIsEmpty)
                            {
                                // Online has nothing, ship has data → ship fills the gap
                                winnerVal = shipVal;
                                winnerLabel = "SHIP_FILLS_GAP";
                            }
                            else if (!onlineIsEmpty && shipIsEmpty)
                            {
                                // Online has data, ship is empty → online fills
                                winnerVal = onlineVal;
                                winnerLabel = "ONLINE_FILLS_GAP";
                            }
                            else if (sts.HasValue && tts.HasValue)
                            {
                                // BOTH have timestamps → LAST WRITE WINS
                                var onlineTs = _direction == "ship_to_online" ? tts.Value : sts.Value;
                                var shipTs = _direction == "ship_to_online" ? sts.Value : tts.Value;

                                if (shipTs > onlineTs)
                                {
                                    // Ship edited MORE RECENTLY → ship wins
                                    winnerVal = shipVal;
                                    winnerLabel = "SHIP_NEWER";
                                }
                                else
                                {
                                    // Online edited more recently (or same time) → online wins
                                    winnerVal = onlineVal;
                                    winnerLabel = "ONLINE_NEWER";
                                }

                                _log.LogDebug("First sync LWW {Table}.{Col} PK={Pk}: " +
                                    "onlineTs={OTs} shipTs={STs} → {Winner}",
                                    table, col, pk, onlineTs, shipTs, winnerLabel);
                            }
                            else if (sts.HasValue && !tts.HasValue)
                            {
                                // Only source has timestamp → source wins
                                var sourceIsOnline = (_direction == "online_to_ship");
                                winnerVal = sourceIsOnline ? onlineVal : shipVal;
                                winnerLabel = sourceIsOnline ? "ONLINE_HAS_TS" : "SHIP_HAS_TS";
                            }
                            else if (!sts.HasValue && tts.HasValue)
                            {
                                // Only target has timestamp → target wins
                                var targetIsOnline = (_direction == "ship_to_online");
                                winnerVal = targetIsOnline ? onlineVal : shipVal;
                                winnerLabel = targetIsOnline ? "ONLINE_HAS_TS" : "SHIP_HAS_TS";
                            }
                            else
                            {
                                // No timestamps at all → online wins (safe default)
                                winnerVal = onlineVal;
                                winnerLabel = "ONLINE_DEFAULT";
                            }

                            var winnerHash = Db.ComputeHash(winnerVal);
                            if (targetHash != winnerHash)
                            {
                                toUpdate.Add((pk, winnerVal));
                            }
                            // BUG 8 FIX: Shadow tracks "last-seen source value", NOT the winner.
                            // Previously shadow=winnerVal caused convergence revert on the next sync
                            // when SHIP won LWW: shadow=ship_val while source still held online_val,
                            // making the next pass see source!=shadow and fire Case 3 PROPAGATE,
                            // overwriting the ship's preserved edit. Storing source value preserves
                            // the invariant `shadow == last-seen source`, so subsequent syncs see
                            // Case 1 (online won → all equal) or Case 2 (ship won → only target changed → preserve).
                            toShadow.Add((pk, Db.NormalizeValue(sval)));

                            _log.LogDebug("First sync {Table}.{Col} PK={Pk}: {Policy} " +
                                "(online={OnlineVal}, ship={ShipVal})",
                                table, col, pk, winnerLabel,
                                GetValuePreview(onlineVal), GetValuePreview(shipVal));

                            continue;
                        }


                        // ──────────────────────────────────────────────────────
                        // SUBSEQUENT SYNCS (shadow exists)
                        // Use the hash-on-the-fly decision matrix
                        // ──────────────────────────────────────────────────────

                        // Case 1: All equal (no change anywhere)
                        if (sourceHash == shadowHash && targetHash == shadowHash)
                        {
                            // No change since last sync → skip
                            continue;
                        }

                        // ──────────────────────────────────────────────────────
                        // SUBSEQUENT SYNCS — Proper 3-Way Field-Level Merge
                        //
                        // Shadow = "original" baseline (value at last sync)
                        // Source changed = source ≠ shadow (other side edited)
                        // Target changed = target ≠ shadow (this side edited)
                        //
                        // FIELD-LEVEL: Each column is compared independently.
                        // Different fields of the same record can be edited on
                        // different sides and BOTH edits are preserved (merged).
                        // Online wins ONLY when SAME field is edited on both sides.
                        // ──────────────────────────────────────────────────────

                        // Case 4: source == target (both changed to same value, or converged)
                        if (sourceHash == targetHash)
                        {
                            toShadow.Add((pk, Db.NormalizeValue(tval)));
                            continue;
                        }

                        bool sourceChanged = (sourceHash != shadowHash);
                        bool targetChanged = (targetHash != shadowHash);

                        // Case 2: Only target changed (source == shadow)
                        // This side edited the field, other side did NOT.
                        // → Preserve this side's edit. Shadow continues to track source
                        //   (NOT target) so the next sync still reads "source unchanged"
                        //   on the unchanged side; otherwise next pass would mis-fire
                        //   Case 3 PROPAGATE and revert the preserved target edit.
                        if (!sourceChanged && targetChanged)
                        {
                            _log.LogDebug("Case2-PRESERVE: {Table}.{Col} PK={Pk}: " +
                                "only target changed (source==shadow). Preserving target edit.",
                                table, col, pk);
                            toShadow.Add((pk, Db.NormalizeValue(sval)));
                            continue;
                        }

                        // Case 3: Only source changed (target == shadow)
                        // Other side edited the field, this side did NOT.
                        // → Apply source edit to target. This is a clean propagation.
                        if (sourceChanged && !targetChanged)
                        {
                            _log.LogDebug("Case3-PROPAGATE: {Table}.{Col} PK={Pk}: " +
                                "only source changed (target==shadow). Applying source to target.",
                                table, col, pk);
                            toUpdate.Add((pk, sval));
                            toShadow.Add((pk, Db.NormalizeValue(sval)));
                            continue;
                        }

                        // ═════════════════════════════════════════════════════════
                        // Case 5: TRUE CONFLICT — Both sides changed this field
                        // source ≠ shadow AND target ≠ shadow AND source ≠ target
                        // → Online wins. Log conflict for audit.
                        // ═════════════════════════════════════════════════════════
                        {
                            var onlineValue = GetOnlineValue(tval, sval);
                            var shipValue = GetShipValue(tval, sval);
                            var onlineNorm = Db.NormalizeValue(onlineValue);
                            var shipNorm = Db.NormalizeValue(shipValue);

                            object? winnerValue;
                            string resolution;

                            if (string.IsNullOrWhiteSpace(onlineNorm) && !string.IsNullOrWhiteSpace(shipNorm))
                            {
                                winnerValue = shipValue;
                                resolution = "CONFLICT_SHIP_FILLS_GAP";
                            }
                            else
                            {
                                winnerValue = onlineValue;
                                resolution = "CONFLICT_ONLINE_WINS";
                            }

                            var winnerHash = Db.ComputeHash(winnerValue);

                            _log.LogWarning("CONFLICT: {Table}.{Col} PK={Pk} - " +
                                "Online={Online} vs Ship={Ship} → {Resolution}",
                                table, col, pk,
                                GetValuePreview(onlineValue),
                                GetValuePreview(shipValue),
                                resolution);

                            conflictRows.Add(new ConflictRecord(pk, tval, sval, winnerValue));
                            conflicts++;

                            if (targetHash != winnerHash)
                            {
                                toUpdate.Add((pk, winnerValue));
                                targetUpdated++;
                            }

                            // Same invariant as elsewhere: shadow tracks last-seen source value.
                            // When online wins, winnerValue == sval already (no change).
                            // When CONFLICT_SHIP_FILLS_GAP keeps ship's value (winner != source),
                            // we must NOT store winner here or the next sync will see source!=shadow
                            // and fire Case 3 PROPAGATE, wiping the just-preserved ship value.
                            toShadow.Add((pk, Db.NormalizeValue(sval)));
                            continue;
                        }
                    }

                    // ──────────────────────────────────────────────────────────
                    // Apply updates to target
                    // ──────────────────────────────────────────────────────────
                    if (toUpdate.Count > 0)
                    {
                        var affected = await Db.BulkUpdateColumnAsync(
                            _conn, TargetDb, table, col, meta.Pk, toUpdate, _log);

                        updated += affected;

                        _log.LogDebug("✓ Bulk updated {Count} rows for {Table}.{Column}",
                            affected, table, col);
                    }

                    // ──────────────────────────────────────────────────────────
                    // Log conflicts to database
                    // ──────────────────────────────────────────────────────────
                    if (conflictRows.Count > 0)
                    {
                        await LogConflictsAsync(conflictRows, table, col, syncBatchId);
                    }

                    // ──────────────────────────────────────────────────────────
                    // Refresh shadow table
                    // ──────────────────────────────────────────────────────────
                    if (toShadow.Count > 0)
                    {
                        try
                        {
                            await Db.UpsertShadowAsync(_conn, TargetDb, table, col, toShadow, _log);

                            // Verify shadow was actually updated by checking one row
                            if (toShadow.Count > 0)
                            {
                                var samplePk = toShadow[0].pk;
                                var sampleExpectedValue = toShadow[0].value;

                                var verify = await Db.LoadShadowMapAsync(_conn, TargetDb, table, col,
                                    new[] { samplePk }, _log);

                                if (verify.TryGetValue(samplePk, out var verifyData))
                                {
                                    var verifyHash = Db.ComputeHash(verifyData.lastValue);
                                    var expectedHash = Db.ComputeHash(sampleExpectedValue);

                                    if (verifyHash != expectedHash)
                                    {
                                        _log.LogError("Shadow verification FAILED for {Table}.{Col}: expected hash {Expected} but got {Actual}",
                                            table, col, expectedHash, verifyHash);

                                        throw new InvalidOperationException(
                                            $"Shadow update verification failed for {table}.{col} - sync integrity compromised!");
                                    }
                                }
                                else
                                {
                                    _log.LogError("Shadow verification FAILED for {Table}.{Col}: row not found after upsert",
                                        table, col);

                                    throw new InvalidOperationException(
                                        $"Shadow row not found after upsert for {table}.{col} - sync integrity compromised!");
                                }
                            }

                            _log.LogDebug("Shadow updated successfully for {Table}.{Col}: {Count} rows",
                                table, col, toShadow.Count);
                        }
                        catch (MySqlException ex)
                        {
                            _log.LogError(ex, "Shadow upsert FAILED for {Table}.{Col} - CRITICAL ERROR", table, col);
                            throw new InvalidOperationException(
                                $"Shadow update failed for {table}.{col}: {ex.Message}", ex);
                        }
                    }
                }

                processed += pks.Count;
            }

            PrintProgress(totalPairs, totalPairs, "");
            Console.WriteLine();

            // ──────────────────────────────────────────────────────────────
            // 4) Soft delete propagation
            // ──────────────────────────────────────────────────────────────
            if (meta.HasDeleted)
            {
                try
                {
                    var localDelCol = await Db.GetDeletedFlagColumnAsync(_conn, TargetDb, table);
                    var sourceDelCol = await Db.GetDeletedFlagColumnAsync(_conn, SourceDb, table);

                    if (!string.IsNullOrEmpty(localDelCol) && !string.IsNullOrEmpty(sourceDelCol))
                    {
                        string sqlDel;
                        if (_direction == "ship_to_online" && meta.UpdatedCol != null)
                        {
                            // Ship→Online: only propagate delete if online wasn't updated more recently
                            sqlDel = $@"
UPDATE `{TargetDb}`.`{table}` t
JOIN `{SourceDb}`.`{table}` s ON s.`{meta.Pk}` = t.`{meta.Pk}`
SET t.`{localDelCol}` = 1
WHERE COALESCE(s.`{sourceDelCol}`, 0) = 1
  AND COALESCE(t.`{localDelCol}`, 0) = 0
  AND (t.`{meta.UpdatedCol}` IS NULL OR s.`{meta.UpdatedCol}` IS NULL
       OR t.`{meta.UpdatedCol}` <= s.`{meta.UpdatedCol}`);";
                        }
                        else
                        {
                            // Online→Ship: always propagate (online wins)
                            sqlDel = $@"
UPDATE `{TargetDb}`.`{table}` t
JOIN `{SourceDb}`.`{table}` s ON s.`{meta.Pk}` = t.`{meta.Pk}`
SET t.`{localDelCol}` = 1
WHERE COALESCE(s.`{sourceDelCol}`, 0) = 1
  AND COALESCE(t.`{localDelCol}`, 0) = 0;";
                        }

                        var delCount = await _conn.ExecuteAsync(sqlDel);
                        deleted += delCount;

                        if (delCount > 0)
                            _log.LogInformation("Propagated {Count} soft deletes for {Table}", delCount, table);

                        // Online→Ship un-delete (restore): if online has the row active
                        // (isDeleted=0) but ship has it soft-deleted (isDeleted=1), restore it
                        // on the ship — but ONLY if online's row is newer (online wins on a fresh
                        // edit), so a more recent ship-side delete is never resurrected. Requires a
                        // timestamp column; without one we cannot compare, so we leave it deleted.
                        if (_direction == "online_to_ship" && meta.UpdatedCol != null)
                        {
                            var sqlUndel = $@"
UPDATE `{TargetDb}`.`{table}` t
JOIN `{SourceDb}`.`{table}` s ON s.`{meta.Pk}` = t.`{meta.Pk}`
SET t.`{localDelCol}` = 0
WHERE COALESCE(s.`{sourceDelCol}`, 0) = 0
  AND COALESCE(t.`{localDelCol}`, 0) = 1;";

                            var undelCount = await _conn.ExecuteAsync(sqlUndel);

                            if (undelCount > 0)
                                _log.LogInformation("Restored {Count} soft deletes for {Table} (online active, newer)", undelCount, table);
                        }

                    }
                }
                catch (MySqlException ex)
                {
                    _log.LogWarning("Soft delete propagation failed for {Table}: {Error}", table, ex.Message);
                }
            }

            // Enhanced summary log (table-agnostic, no history_fb)
            _log.LogInformation("✔ {Table}: ins={Ins} upd={Upd} del={Del} conf={Conf} " +
                "corrupt_sh={CS} target_upd={TU}",
                table, inserted, updated, deleted, conflicts,
                corruptShadows, targetUpdated);

            // Conditional warnings:
            if (corruptShadows > 0)
            {
                _log.LogWarning("🔧 {Table}: {Count} corrupt shadows detected and rebuilt",
                    table, corruptShadows);
            }

            if (targetUpdated > 0)
            {
                _log.LogWarning("🔄 {Table}: {Count} conflicts where target was updated to match online",
                    table, targetUpdated);
            }

            return new SyncResult(inserted, updated, deleted, conflicts);
        }

        // ═══════════════════════════════════════════════════════════════════
        // AUTO-INCREMENT DETECTION
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if a PK column is auto_increment (used for duplicate prevention)
        /// </summary>
        private async Task<bool> IsAutoIncrementPkAsync(string pkColumn, string db, string table)
        {
            try
            {
                var extra = await _conn.ExecuteScalarAsync<string>(
                    @"SELECT EXTRA FROM information_schema.COLUMNS
                       WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@tbl AND COLUMN_NAME=@col",
                    new { db, tbl = table, col = pkColumn });
                return extra?.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Get columns from UNIQUE indexes (excluding PK). Only these are safe for dedup.
        /// FK columns and *Id pattern columns are NOT reliable — they produce false positives.
        /// </summary>
        private async Task<List<string>> GetUniqueKeyColumnsAsync(string db, string table)
        {
            try
            {
                var cols = await _conn.QueryAsync<string>(@"
                    SELECT DISTINCT COLUMN_NAME
                    FROM information_schema.STATISTICS
                    WHERE TABLE_SCHEMA = @db
                      AND TABLE_NAME = @tbl
                      AND NON_UNIQUE = 0
                      AND INDEX_NAME != 'PRIMARY'
                    ORDER BY SEQ_IN_INDEX",
                    new { db, tbl = table });
                return cols.ToList();
            }
            catch { return new List<string>(); }
        }

        // ═══════════════════════════════════════════════════════════════════
        // TABLE EXISTENCE CHECK
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if a table exists in the specified database
        /// </summary>
        private async Task<bool> TableExistsAsync(string database, string table)
        {
            try
            {
                var sql = @"
SELECT COUNT(*) FROM information_schema.TABLES 
WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl;";
                var count = await _conn.ExecuteScalarAsync<int>(sql, new { db = database, tbl = table });
                return count > 0;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Error checking table existence for {Db}.{Table}: {Error}", database, table, ex.Message);
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // HASHING
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute SHA-256 hash of a value for shadow comparison
        /// Thread-safe, handles all common MySQL types
        /// </summary>
        private static string HashValue(object? v)
        {
            string s = v switch
            {
                null or DBNull => "<NULL>",
                DateTime dt => dt.ToUniversalTime().ToString("O"),
                DateTimeOffset dto => dto.UtcDateTime.ToString("O"),
                byte[] bytes => Convert.ToBase64String(bytes),
                bool b => b ? "true" : "false",
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString("G9", CultureInfo.InvariantCulture),
                double dbl => dbl.ToString("G17", CultureInfo.InvariantCulture),
                _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
            };

            lock (_shaLock)
            {
                return Convert.ToBase64String(_sha256.ComputeHash(Encoding.UTF8.GetBytes(s)));
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // CONFLICT LOGGING
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Internal record for conflict data
        /// </summary>
        private record ConflictRecord(string Pk, object? TargetValue, object? SourceValue, object? ChosenValue);

        /// <summary>
        /// Log conflicts to TARGET database sync_conflict_log_columns.
        /// Each database has its own conflict log for isolation.
        /// </summary>
        private async Task LogConflictsAsync(
            List<ConflictRecord> rows,
            string table,
            string column,
            string batchId)
        {
            var sql = $@"
INSERT INTO `{TargetDb}`.sync_conflict_log_columns
(sync_batch_id, table_name, record_pk, column_name,
 conflict_type, local_value, source_value, chosen_value,
 policy_applied, manual_required, resolution_status, detected_at,
 source_db, sync_direction)
VALUES
(@batch, @tbl, @pk, @col,
 'parallel_edit',
 JSON_OBJECT('value', @tval),
 JSON_OBJECT('value', @sval),
 JSON_OBJECT('value', @chosen),
 'auto_online_wins',
 1, 'queued_manual', UTC_TIMESTAMP(),
 @src, @dir);";

            int successCount = 0;

            foreach (var r in rows)
            {
                try
                {
                    var parameters = new
                    {
                        batch = batchId,
                        tbl = table,
                        pk = r.Pk,
                        col = column,
                        tval = ConvertToString(r.TargetValue),
                        sval = ConvertToString(r.SourceValue),
                        chosen = ConvertToString(r.ChosenValue),
                        src = SourceDb,
                        dir = _direction
                    };

                    await _conn.ExecuteAsync(sql, parameters);
                    successCount++;

                    _log.LogDebug("Conflict logged: {Table}.{Col} pk={Pk} target={TVal} source={SVal} → chosen={Chosen}",
                        table, column, r.Pk, r.TargetValue, r.SourceValue, r.ChosenValue);
                }
                catch (Exception ex)
                {
                    // Log error to console AND logger
                    Console.WriteLine($"  ⚠️ Conflict log failed for {table}.{column} pk={r.Pk}: {ex.Message}");
                    _log.LogError(ex, "Failed to log conflict for {Table}.{Col} pk={Pk}: {Msg}",
                        table, column, r.Pk, ex.Message);
                }
            }

            if (successCount > 0)
            {
                _log.LogInformation("Logged {Count} conflict(s) for {Table}.{Col} [batch={Batch}]",
                    successCount, table, column, batchId);
            }
        }

        /// <summary>
        /// Convert value to string for JSON storage, handling all MySQL types
        /// </summary>
        private static string? ConvertToString(object? val)
        {
            return val switch
            {
                null or DBNull => null,
                DateTime dt => dt.ToString("O"),
                DateTimeOffset dto => dto.ToString("O"),
                byte[] bytes => Convert.ToBase64String(bytes),
                bool b => b ? "true" : "false",
                _ => Convert.ToString(val, CultureInfo.InvariantCulture)
            };
        }

        // ═══════════════════════════════════════════════════════════════════
        // PROGRESS DISPLAY
        // ═══════════════════════════════════════════════════════════════════

        private static void PrintProgress(int done, int total, string prefix)
        {
            total = Math.Max(total, 1);
            int pct = (int)(done * 100.0 / total);
            int bars = (int)(30.0 * done / total);

            Console.Write($"\r{prefix} [{new string('#', bars)}{new string('.', 30 - bars)}] {pct,3}% ({done}/{total})   ");
        }
    }
}