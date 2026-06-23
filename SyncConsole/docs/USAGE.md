# SAIL ERP SyncConsole — Usage & Implementation Guide

> **Audience:** ops engineers running the binary; .NET engineers extending the codebase.
> Read [`ARCHITECTURE.md`](./ARCHITECTURE.md) first if you need context on what the engine does.

This guide is split into two halves:

- **Part A (Operator)** — how to install, configure, run, and observe SyncConsole in production.
- **Part B (Developer)** — how to build, modify, and extend the codebase safely.

---

# Part A — Operator's Guide

## A.1 Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| MySQL server | 8.0+ | Both sides must be reachable from where the binary runs. The engine creates 4 metadata tables in the target DB on first run. |
| .NET runtime | 8.0 (framework-dependent) | The published binary is **not** self-contained. Install ASP.NET Core 8 runtime on the host. |
| Disk for logs | ~50 MB / month | Daily-rolling logs, 30-day retention. |

The published binary set is in `publish/`:

```
publish/
├── SyncConsole          ← Linux/macOS launcher (chmod +x)
├── SyncConsole.exe      ← Windows launcher
├── SyncConsole.dll      ← actual program
├── SyncConsole.deps.json
├── SyncConsole.runtimeconfig.json
└── …8.0 framework refs and Dapper/MySqlConnector/Serilog DLLs
```

You can invoke either `./SyncConsole` (preferred) or `dotnet SyncConsole.dll`.

## A.2 Configuration

There are three input layers, applied in this order (CLI wins):

1. **CLI flags** — see `--help` for the canonical list.
2. **`appsettings.json`** in the working directory — optional.
3. **Hard-coded defaults** — `direction=ship_to_online`, `env=online`, `batch=200`, `logLevel=Information`.

### Required parameters

| Flag | JSON key | Meaning |
|---|---|---|
| `--connection` | `connection` | Full ADO.NET MySQL connection string. Must include `AllowUserVariables=true`. The engine sets timeouts and pooling automatically — your string only needs Server/User/Password. |
| `--central_db` | `centralDb` | The **online (cloud) DB** name on the local MySQL server. On the ship server this is the *imported cloud dump* DB. |
| `--ship_db` | `shipDb` | The **ship DB** name on the local MySQL server. On the cloud server this is the *imported ship dump* DB. |
| `--domain` | `domain` | Free-form tenant identifier (e.g., `rsms`, `maran`). Used in metadata only. |
| `--ship_imo` | `shipImo` | Vessel IMO number (e.g., `9340415`). Used in metadata only. |
| `--direction` | `direction` | `online_to_ship` or `ship_to_online`. **This decides which side is source vs target.** |
| `--env` | `environment` | `online` or `ship`. Currently used only for logging clarity. |

### Optional parameters

| Flag | Default | Notes |
|---|---|---|
| `--batch` | `200` | PKs read per merge batch. Higher = fewer round-trips but bigger MySQL temp tables. 200 is a good default. |
| `--logLevel` | `Information` | One of `Trace, Debug, Information, Warning, Error, Critical, None`. |
| `--diagnose` | (off) | Run read-only schema/shadow analysis instead of a real sync. No writes. |

### Environment variables

| Var | Purpose |
|---|---|
| `SAIL_SYNC_LOG_DIR` | Override the log directory. |
| `SAIL_SYNC_HEADLESS=1` | Suppress the console sink (use when launching from a Node.js parent process or a service manager that doesn't want stdout). |

## A.3 Wiring sync into the broader pipeline

SyncConsole is one stage in a 4-step loop. It expects the previous stages to have done their work; if the dump isn't fresh, the engine has nothing to sync.

```
┌── on the SOURCE side ──────────────────────────┐
│ 1. mysqldump --where="date(updatedAt)          │
│    BETWEEN DATE_SUB(CURDATE(), INTERVAL 2 DAY) │
│    AND CURDATE()" ...                          │
│ 2. zip + transfer (sat link, S3, anything)     │
└────────────────────────────────────────────────┘
                       │
                       ▼
┌── on the TARGET side ──────────────────────────┐
│ 3. unzip + import into <target>_<otherside>_dump DB  │
│ 4. ./SyncConsole --direction <dir> ...         │
└────────────────────────────────────────────────┘
```

Steps 1–3 are typically shell scripts maintained outside this repo (`create_ship_dump_<imo>.sh`, `extractserver_dump_<imo>.sh`, `importserver_dump_<imo>.sh` in production). Step 4 is SyncConsole.

**Dump-window pitfall.** The cloud-side mysqldump usually has `WHERE date(updatedAt) BETWEEN today-2 AND today` on every table (sometimes joined through `vesselId`). If a record was last edited 3+ days ago, the dump will not include it and SyncConsole will not sync it — even though both DBs have the row. This is a pipeline issue, not an engine bug. Adjust the dump SQL or the run cadence if this bites you.

## A.4 First-run checklist

On a fresh target database the engine will need to create its 4 metadata tables. Verify after the first run:

```sql
SHOW TABLES FROM `<target_db>` LIKE 'sync_%';
-- expected: sync_shadow_columns, sync_conflict_log_columns,
--           sync_merge_audit, sync_checkpoints
```

If `sails_master.online_sync_tables` (or `offline_sync_tables` for the reverse direction) is empty, the engine logs:

```
⚠️ No tables found! Check your sync_tables configuration.
```

Both lists should be populated with the table names you want synced (`isActive=1`, `isMasterTable=0` for tables that participate in field-level sync).

## A.5 Running

### Standard production invocation

```bash
cd /home/sailapp/socket_sync

./SyncConsole \
  --connection "Server=localhost;User Id=sailadmin;Password=*****;AllowUserVariables=true;Database=sails_master;" \
  --central_db "rsms-db_cloud_dump" \
  --ship_db    "rsms-db" \
  --domain     "rsms" \
  --ship_imo   "9340415" \
  --direction  "online_to_ship" \
  --env        "ship" \
  --batch      200 \
  --logLevel   "Information"
```

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Sync completed (may still have logged conflicts — check the conflict table). |
| `1` | Runtime error (MySQL down, schema mismatch, shadow integrity check failed). |
| `2` | Configuration error before sync started. |

### Diagnostic mode (read-only)

```bash
./SyncConsole --diagnose --connection "..." --central_db "..." --ship_db "..." --domain rsms --ship_imo 9340415 --direction online_to_ship --env ship
```

Reports schema discrepancies, shadow coverage, observation-comment duplicates, and a few QA-known column issues. Useful when QA reports "X doesn't sync" — start here before changing code.

## A.6 What success looks like

A run finishes with a summary block on stdout (and in the file log):

```
14:24:45 [INF] ✅ Sync finished!
14:24:45 [INF] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
14:24:45 [INF] Domain:        rsms
14:24:45 [INF] Ship:          9340415
14:24:45 [INF] Direction:     online_to_ship
14:24:45 [INF] Environment:   ship
14:24:45 [INF] Tables Synced: 315
14:24:45 [INF] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
14:24:45 [INF] Records Inserted:  0
14:24:45 [INF] Records Updated:   162
14:24:45 [INF] Records Deleted:   0
14:24:45 [INF] Conflicts Logged:  0
14:24:45 [INF] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

Re-running the same sync immediately should produce **0 / 0 / 0 / 0** — that's how you know convergence is stable.

## A.7 Verifying a specific record synced

```sql
-- 1. Are live and dump now equal?
SELECT t.id,
       t.<col>  AS live_value,
       s.<col>  AS dump_value,
       t.<col> <=> s.<col> AS equal
FROM   `<target_db>`.<table> t
JOIN   `<source_db>`.<table> s ON s.id = t.id
WHERE  t.id = '<pk>';

-- 2. Did the engine record this in the shadow?
SELECT column_name, SUBSTRING(`last_value`,1,80) AS shadow_val, last_synced_at
FROM   `<target_db>`.sync_shadow_columns
WHERE  table_name = '<table>'
   AND record_pk  = '<pk>'
ORDER  BY column_name;
```

If `equal = 1` and `last_synced_at` is recent, the engine handled the row. If `equal = 0`, look at the next sections.

## A.8 Reading conflicts

```sql
SELECT detected_at, table_name, record_pk, column_name,
       JSON_UNQUOTE(JSON_EXTRACT(local_value,'$.value'))  AS local,
       JSON_UNQUOTE(JSON_EXTRACT(source_value,'$.value')) AS source,
       JSON_UNQUOTE(JSON_EXTRACT(chosen_value,'$.value')) AS chosen,
       policy_applied, sync_direction
FROM   `<target_db>`.sync_conflict_log_columns
ORDER  BY detected_at DESC
LIMIT  50;
```

Every row here represents a **true parallel edit** (Case 5): both sides changed the same column to different values. The `chosen_value` is what the engine picked (online by default; ship if online was empty and ship had data — `SHIP_FILLS_GAP`). These are deliberate engine decisions, not errors.

## A.9 Observability

### Log files

Default location:

| OS | Path |
|---|---|
| Windows | `C:\SAIL\Logs\Sync\sync-YYYYMMDD.log` |
| Linux | `/var/log/sail/sync/sync-YYYYMMDD.log` (fallback `/tmp/sail/logs/sync/`) |
| macOS | same as Linux |

Daily rotation, 30-day retention, INFO level by default.

### Useful debug filters

When `--logLevel Debug` is on you'll see one `HASH-CHECK` line and one `3-WAY` line per (table, column, PK) — that's millions of lines for a full run. Use `grep` aggressively:

```bash
# Trace a specific PK end-to-end
grep -E "(HASH-CHECK|3-WAY|Case[1-5]|First sync|CONFLICT).*PK=64921" sync-YYYYMMDD.log

# How many of each case fired?
grep -oE "Case[1-5]-(PRESERVE|PROPAGATE)|CONFLICT_(ONLINE_WINS|SHIP_FILLS_GAP)" sync-YYYYMMDD.log | sort | uniq -c

# Convergence check — which tables had updates this run?
grep "✔ " sync-YYYYMMDD.log | grep -v "ins=0 upd=0 del=0 conf=0"
```

## A.10 Troubleshooting playbook

### Symptom: "Records Updated: 0" but QA says values aren't synced

Almost always a dump pipeline issue, not an engine bug. Verify:

1. The record QA edited has `updatedAt` within the dump's window (typically 2 days).
2. The record satisfies the dump's other filters (e.g., `vesselId='24'`).
3. The dump zip's mtime is newer than QA's edit.
4. After import, `SELECT COUNT(*) FROM <source_db>.<table> WHERE id = '<pk>'` returns 1.

If the row is in the dump but still not syncing, run with `--logLevel Debug` and grep for the PK.

### Symptom: 1st sync correct, 2nd sync reverts the value

This is the Bug 8 / Bug 7 / Bug 6 family. Verify the deployed DLL contains the §6 invariant:

```bash
# Dump the .dll's SyncEngine.SyncTableAsync IL or check the build hash
md5sum SyncConsole.dll          # compare to the known-good build hash
ls -la SyncConsole.dll.bak_*    # check there's a recent backup
```

If running an older build, redeploy from `publish/` (see Part B).

### Symptom: "Shadow upsert verification FAILED"

Hard error — the engine wrote a shadow row but reading it back produced a different hash. Causes seen in production:

- MySQL collation mismatch between `sync_shadow_columns.last_value` (utf8mb4_unicode_ci) and the source column (utf8mb4_0900_ai_ci). Cosmetic for hashing but trips raw SQL joins. Use `COLLATE` clauses in diagnostic queries.
- Disk space exhausted on the MySQL server.
- A concurrent process modifying the shadow table.

### Symptom: Duplicate rows in `nmnearmiss*` / similar tables

These are pre-existing duplicates in the source data, not engine artifacts. The engine inserts by PK with `INSERT IGNORE`; it cannot create duplicate UUID PKs. Confirm with:

```sql
SELECT id, COUNT(*) FROM `<table>` GROUP BY id HAVING COUNT(*) > 1;
-- always returns 0

SELECT <fk_a>, <fk_b>, COUNT(*) FROM `<table>` GROUP BY <fk_a>, <fk_b> HAVING COUNT(*) > 1;
-- may return many — those are app-layer duplicates with distinct PKs
```

Fix at the application layer (UPSERT instead of INSERT) or add a UNIQUE constraint on the business key (then the engine's UNIQUE-key dedup will kick in for inserts).

### Symptom: "Skipping <table>: no matching columns found in both databases"

The two schemas have diverged enough that the column intersection is empty. Usually means the local copy of the table has only system columns the engine excludes. Apply pending DDL migrations on whichever side is behind.

### Symptom: Connection drops mid-run

The session is set up with 8-hour timeouts, but the network or proxy may have its own limit. Reduce `--batch` or split very large tables out and run them separately. The engine's checkpoint table records progress per table for resumability.

---

# Part B — Developer's Guide

## B.1 Repository layout

See `ARCHITECTURE.md §4`. The salient files for editing:

| File | When you edit it |
|---|---|
| `Program.cs` | Adding/removing CLI flags; changing log/exit behaviour. |
| `Db.cs` | Anything that touches MySQL or normalization. **Hot zone.** |
| `SyncEngine.cs` | The merge algorithm. **Hottest zone.** |
| `Models.cs` | Adding new DTOs the engine will consume. |
| `SqlText.cs` | If you need a new reusable SQL fragment. |
| `Policies.cs` | Adding a new conflict-resolution policy name. |
| `DiagnosticTool.cs` | Adding a new read-only check. |

Avoid editing `appsettings.json` and committing real credentials — production uses CLI flags.

## B.2 Building

### Local development build

```bash
cd C:/Users/GhaziAnwer/synccode/SyncConsole
dotnet build -c Release
# → bin/Release/net8.0/SyncConsole.dll
```

### Production publish (linux-x64, framework-dependent)

```bash
cd C:/Users/GhaziAnwer/synccode/SyncConsole
dotnet publish -c Release -r linux-x64 --self-contained false -o publish
# → publish/SyncConsole.dll  (165 KB, deploys with publish/SyncConsole launcher)
```

The `NU1903` warning about `System.Text.Json 8.0.4` is known and accepted (transient dependency from `Microsoft.Extensions.Configuration`). Don't bump it without testing.

### Editing tips for `SyncEngine.cs`

The file uses CRLF line endings and contains Unicode box-drawing characters (`─`, `═`, `▼`, etc.) in comments. Some IDE/editor setups will silently rewrite these. **The Edit tool will fail on Unicode-heavy regions** — for big rewrites use `node`/scripted text replacement so you don't accidentally re-encode characters.

## B.3 Deploying to a server

The standard recipe (production server is `13.212.138.227`):

```bash
# 1. Build & publish
cd C:/Users/GhaziAnwer/synccode/SyncConsole
dotnet publish -c Release -r linux-x64 --self-contained false -o publish

# 2. Backup current DLL on server
ssh -i <key> ubuntu@<host> \
  "cp /home/sailapp/socket_sync/SyncConsole.dll /home/sailapp/socket_sync/SyncConsole.dll.bak_$(date +%Y%m%d_%H%M)"

# 3. Copy
scp -i <key> publish/SyncConsole.dll \
   ubuntu@<host>:/home/sailapp/socket_sync/SyncConsole.dll

# 4. Smoke test
ssh -i <key> ubuntu@<host> \
  "/home/sailapp/socket_sync/SyncConsole --diagnose ..."
```

Always backup before deploying. The engine writes to `<target_db>.sync_shadow_columns`; an incorrect deploy that corrupts the shadow can take days to surface.

## B.4 Extending the engine — common scenarios

### B.4.1 Add a new column to the SystemColumns exclusion list

`SystemColumns` (`Db.cs:17–30`) is the set of columns the engine **never compares**. Add timestamp/metadata-style columns here. **Do not** add business columns; those should sync.

```csharp
private static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase)
{
    "created_at", "createdat", ...
    "your_new_metadata_col_here",
};
```

Caveat: if a column was previously synced and you add it to `SystemColumns`, its existing shadow rows become orphans (they just won't be used). They cause no harm but can be cleaned up with:

```sql
DELETE FROM `<target_db>`.sync_shadow_columns
WHERE column_name = 'your_new_metadata_col_here';
```

### B.4.2 Change the conflict policy from "online wins" to something else

Today the engine implements only online-wins (first-sync and Case-5) plus empty-gap-fill. To add a real policy plug-in:

1. Define the policy name as a constant in `Policies.cs`.
2. Extend `Models.SyncRule` if you need parameters.
3. Add a per-table or per-column lookup in `Db` that returns the rule.
4. In `SyncEngine.cs` Case 5 (`SyncEngine.cs:593–633`), branch on the rule before the current `winnerValue = onlineValue` line.

Important: **whatever the policy chooses, the shadow MUST end up = the source value (`Db.NormalizeValue(sval)`)**. This is the §6 invariant. Storing the winner instead of the source was Bug 8 — three places had this defect simultaneously, all causing convergence reverts.

### B.4.3 Support a third sync direction (e.g., `cloud_to_cloud`)

Don't. The codebase is structurally bidirectional (online↔ship) and many helpers (`GetOnlineValue`, `GetShipValue`, soft-delete asymmetry) are hardwired to that pair. Adding a new direction means revisiting all of those. If you really need it, file a design doc first.

### B.4.4 Add a new sync metadata table

Pattern to follow (matches `EnsureSyncTablesAsync` in `Db.cs:405–539`):

1. Write the `CREATE TABLE IF NOT EXISTS` SQL in `EnsureSyncTablesAsync`.
2. Use `<target_db>` as the schema, never `sails_master` (we deliberately moved off central tables).
3. Add an InnoDB engine, utf8mb4 unicode_ci collation, and a sensible primary key.
4. Add helper methods in `Db.cs` for read/write.
5. Wire up writes in `SyncEngine.SyncTableAsync` if needed.

## B.5 Hard rules — things you must not break

| Rule | Why |
|---|---|
| `Db.NormalizeValue` MUST produce identical strings for both sides on every supported CLR/MySQL type | Hashes diverge → false conflicts, infinite update loops |
| Every `toShadow.Add(...)` writes `Db.NormalizeValue(sval)` (the source value) | Otherwise the §6 invariant breaks → ship edits revert next sync (Bug 6/7/8) |
| `EnsureSyncTablesAsync` is idempotent | Called every run; must be safe to re-run |
| FK checks are off only inside the engine session | Re-enable in `Program.RunAsync.finally`. Long-lived FK-off connections corrupt unrelated work |
| `last_value` is always backticked in raw SQL | MySQL 8 reserved word (window function) |
| Shadow rows always populate `last_synced_at = UTC_TIMESTAMP()` | Runtime detects NULL `last_synced_at` as "corrupt shadow" and rebuilds |
| Don't add table-name special cases | The codebase is intentionally table-agnostic. The previous era of `IsHistoryTable`-style branching produced bugs and was deleted. The deprecated method is still in `Db.cs:158–169` as a tombstone — don't resurrect it |
| The two hash functions (`Db.ComputeHash`, `SyncEngine.HashValue`) MUST not both be reached at runtime | They normalize differently. Only `Db.ComputeHash` is canonical. The other is dead code we kept for now |

## B.6 Adding a regression test for the merge

There is no automated test framework wired up yet. The standard way to validate a merge change is the procedure used for Bug 8:

1. Pick a real PK that exists in both DBs (e.g., `observationdetails.64921`).
2. Capture baseline state (live + dump + shadow row by row).
3. Manipulate live, dump, and shadow to set up the case you want to test.
4. Run `./SyncConsole` once with `--logLevel Debug`, grep for the PK.
5. Verify post-state.
6. Run `./SyncConsole` 2–4 more times with no further data changes — every run after the first should report **0 inserts / 0 updates / 0 conflicts**. This is the "stable convergence" check, and it catches the entire Bug 6/7/8 family.
7. Restore baseline.

If you find yourself testing this often, write a small `bash` runner that does steps 2–7 for a list of (table, PK, column, scenario) tuples. The previous QA cycle pioneered this pattern with `s3_fix_*` scripts.

## B.7 Where the bodies are buried

The codebase carries scars from real production bugs. Read these before touching the merge:

- **Bug 6** (`project_bug6_field_level_merge.md`) — over-aggressive online-wins erased ship edits on fields online never touched. Fix: per-column 3-way merge with proper Case 2/3/5.
- **Bug 7** (`project_bug7_firstsync_lww.md`) — first sync (no shadow) blindly picked online; introduced LWW with `updatedAt`.
- **Bug 8** (`project_bug8_shadow_invariant.md`) — three sites stored *winner* in shadow when ship won, causing 2nd-sync reverts. Fix: shadow always tracks source.

If your change touches `SyncEngine.cs:418–641` or `Db.UpsertShadowAsync`, re-read all three before merging.

## B.8 Code-level conventions

- **Async everywhere.** Every DB method returns `Task` or `Task<T>`. No sync-over-async, no `.Result`, no `.Wait()`.
- **Logging.** Use `ILogger` parameter passed in, never `Console.WriteLine` for runtime info. The exception is `PrintProgress` which uses `\r` for the in-place progress bar.
- **No dynamic SQL string concat for user input.** Use Dapper parameter binding (`@table`, `@col`, etc.) for values; backticked identifiers are interpolated only from internal config. Never put a column or table name into `string.Format` from external input.
- **Defensive normalization.** `Db.NormalizeValue` handles every CLR type MySqlConnector might return. If you add a new type (say `TimeOnly`), update both branches of the normalizer.
- **No table-name branching.** Decisions come from `information_schema` and the merge matrix, not from `if (table == "foo")`.
- **MySQL reserved words.** Always backtick `last_value`, `groups`, `roles`, etc. Even safe-looking names like `state` should be backticked when they appear in raw SQL. Test all new SQL on MySQL 8.0.

## B.9 Pre-merge checklist

Before opening a PR that touches `SyncEngine.cs` or `Db.cs`:

- [ ] Run `dotnet build -c Release` clean (only NU1903 warning is acceptable).
- [ ] Run `dotnet publish -c Release -r linux-x64 --self-contained false -o publish` clean.
- [ ] Manually walk through the §B.6 4-sync convergence test against at least one PK on a non-prod DB.
- [ ] Verify shadow's `last_synced_at` updates on every run.
- [ ] Re-run with `--logLevel Debug` and confirm the cases you expect actually fire (`grep "Case[1-5]-"` and `grep "First sync"`).
- [ ] Update `CLAUDE.md` if your change introduces a new operational quirk.
- [ ] If you touched the merge, add a memory page in `~/.claude/projects/.../memory/` documenting the rationale.

## B.10 Reference: minimal working invocation for ad-hoc testing

```bash
# Local dev, both DBs on localhost
dotnet run --project SyncConsole -- \
  --connection "Server=localhost;User Id=root;Password=secret;AllowUserVariables=true;Database=sails_master;" \
  --central_db "rsms_main" \
  --ship_db    "rsms_9340415" \
  --domain     rsms \
  --ship_imo   9340415 \
  --direction  online_to_ship \
  --env        ship \
  --batch      50 \
  --logLevel   Debug
```

`--batch 50` is small for fast iteration; production uses 200.

---

## Appendix — Quick reference card

```
DIRECTION              SOURCE              TARGET              CONFLICT WINNER
online_to_ship         central_db          ship_db             online (=source)
ship_to_online         ship_db             central_db          online (=target)

DECISION MATRIX (subsequent sync)
-------------------------------------------------------------------------
case  predicate                              action               new shadow
1     s == sh && t == sh                     skip                  unchanged
4     s == t                                 rewrite               source value
2     !sourceChanged && targetChanged        preserve target       source value
3     sourceChanged && !targetChanged        propagate source      source value
5     s != t, both differ from sh            online wins (or       source value
                                             SHIP_FILLS_GAP)

FIRST SYNC (no shadow) PRIORITY
1. equal values          → skip, write shadow=source
2. one side empty        → non-empty wins (SHIP_FILLS_GAP/ONLINE_FILLS_GAP)
3. both have data, differ → online wins (ONLINE_WINS)
   (updatedAt is NOT consulted; no Last-Write-Wins fallback)

EXIT CODES
0 success    1 runtime error    2 config error

LOG LOCATIONS
Windows  C:\SAIL\Logs\Sync\sync-YYYYMMDD.log
Linux    /var/log/sail/sync/sync-YYYYMMDD.log  (fallback /tmp/sail/logs/sync/)
```
