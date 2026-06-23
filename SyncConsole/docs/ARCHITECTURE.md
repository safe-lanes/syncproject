# SAIL ERP SyncConsole — Architecture

> **Audience:** engineers maintaining or extending the sync engine.
> **Scope:** what the system does, why each component exists, and the contracts between them.
> Source of truth: the eight `.cs` files in `SyncConsole/`. This document describes the design they implement.

---

## 1. Purpose

SyncConsole is a **MySQL-to-MySQL bidirectional, column-level, dump-based sync engine** for a maritime Safety Management System (SMS).

Two MySQL databases are kept in eventual consistency:

| Role | Typical name | Where it runs | Notes |
|---|---|---|---|
| **Online / central** | `<domain>` (e.g., `rsms-db`) | Cloud server | The authoritative server. |
| **Ship / offline** | `<domain>_<imo>` (e.g., `rsms-db_9340415`) | Vessel onboard server | Operates independently of the cloud. |

Direct database links are not assumed. Each side periodically generates a **mysqldump** of its recent changes (typically a 2-day rolling window), ships it across to the other side (e.g., as a `.zip` synced over satellite), and imports it into a sibling database (e.g., `rsms-db_cloud_dump` on the ship). The SyncConsole binary then runs **on the receiving side** and reconciles the imported dump (its **source**) against the live database (its **target**) one column at a time.

The design priorities, in order:

1. **Never silently lose user edits.** Both sides have human users who may edit the same record. Field-level merge keeps as much as possible.
2. **Be table-agnostic.** No table-name branching, no per-table policies in code. Decisions are driven entirely by data + schema metadata.
3. **Be resumable and auditable.** Every decision writes a shadow row, every conflict writes a log row.
4. **Be safe under network/MySQL flakiness.** Long sessions, retries, periodic pings, FK checks off during the run.

---

## 2. Top-level data flow

```
┌─────────────────────────┐                ┌──────────────────────────┐
│   ONLINE side server    │                │      SHIP server         │
│                         │                │                          │
│   live DB: rsms-db      │ ── mysqldump ─►│   imported into          │
│                         │   (2-day win.) │   rsms-db_cloud_dump     │
│                         │                │   ↓                      │
│                         │                │   SyncConsole runs:      │
│                         │                │   src = rsms-db_cloud_dump│
│                         │                │   tgt = rsms-db          │
│                         │                │   direction = online_to_ship│
└─────────────────────────┘                └──────────────────────────┘
         ▲                                                │
         │                                                │
         │            ── mysqldump ──                     │
         │            (2-day window)                      │
         └────────────────────────────────────────────────┘
            imports into rsms-db_offline_dump on cloud
            then SyncConsole runs there with
            direction = ship_to_online
```

The two directions are symmetric **except** for soft-delete semantics (§7) and which side wins on a true conflict.

### Direction nomenclature

| `direction` arg | Source DB (read-only) | Target DB (writes go here) | Conflict winner |
|---|---|---|---|
| `online_to_ship` | `central_db` (= cloud dump on ship) | `ship_db` (= live ship DB) | online (source) |
| `ship_to_online` | `ship_db` (= ship dump on cloud) | `central_db` (= live cloud DB) | online (target) |

**"Online wins" is a constant rule** — the side that physically sits at the cloud always wins on a true conflict, regardless of whether it is currently the source or the target. The engine handles this asymmetry through `GetOnlineValue()` / `GetShipValue()` helpers (`SyncEngine.cs:93–102`).

---

## 3. Process model & lifecycle

A run is a single OS process invocation. There is no daemon. The expected operator pattern is "import a fresh dump, then run sync, then exit."

```
Program.Main
 ├── ParseCli + LoadJsonAsync ───► Settings (CLI overrides JSON)
 ├── Validate
 ├── ConfigureSerilog ─────────► console + rolling file sink
 ├── Db.EnhanceConnectionString ─► sets long timeouts, pooling, AllowZeroDateTime
 ├── new MySqlConnection / OpenAsync
 ├── Db.SetSessionAsync ────────► utf8mb4, FK checks OFF, large net/wait timeouts
 ├── Db.EnsureSyncTablesAsync ──► creates 4 sync tables in TARGET DB (if missing)
 ├── Db.GetActiveTablesAsync ───► reads sails_master.{online_sync_tables|offline_sync_tables}
 ├── for each table:
 │     SyncEngine.SyncTableAsync ─► insert-missing → 3-way merge per column → soft-delete
 ├── Db.EnableForeignKeyChecksAsync
 └── print totals (inserted/updated/deleted/conflicts), exit code 0
```

If `--diagnose` is passed, `RunDiagnosticsAsync` is called instead — it reads schema and shadow state but performs no writes.

Failure modes:
- **Validation error** → exit 2, structured error per missing arg.
- **MySQL/IO error during sync** → caught at top level, exit 1, full stack trace logged.
- **Shadow upsert verification mismatch** → throws `InvalidOperationException` to stop the run mid-table; this is intentional because a desynced shadow corrupts later runs (§6).

---

## 4. Source layout

```
SyncConsole/
├── Program.cs        ← CLI entry, config merge, Serilog setup, top-level orchestration
├── Config.cs         ← (legacy) JSON config loader; not used by the current Program.cs flow
├── Models.cs         ← TableMeta, DiffRow, Decision, SyncRule
├── Policies.cs       ← Constant strings naming conflict-resolution policies
├── SqlText.cs        ← Reusable parameterized SQL fragments (insert-missing, propagate-deletes…)
├── Db.cs             ← All schema/MySQL helpers, hash function, connection enhancement
├── SyncEngine.cs     ← Per-table sync orchestration: insert, 3-way merge, soft-delete
├── DiagnosticTool.cs ← Read-only schema + shadow analysis used by --diagnose
├── appsettings.json  ← Default configuration (overridden by CLI)
└── SyncConsole.csproj
```

Roughly 3,300 LoC. Two thirds of it is `Db.cs` and `SyncEngine.cs`. The other files are tiny and exist for cleanliness.

**Two parallel hash implementations exist by accident.** `Db.ComputeHash` (canonical, used by the merge) and `SyncEngine.HashValue` (private, currently unused at runtime) compute SHA-256 differently. Treat `Db.ComputeHash` as the single source of truth and ignore `SyncEngine.HashValue`. (Removing the dead one is a future cleanup; doing so today is a behaviour-neutral refactor.)

---

## 5. The hash-on-the-fly design

The engine stores **the last-seen source value** in a shadow table — not its hash. Hashes are computed on demand when comparing.

### Why store values, not hashes
- Hashes alone are useless if the algorithm ever changes (you can't recompute from history).
- Storing the value lets us debug "why did the engine make decision X" by reading the shadow.
- For most fields, the value is small (<200 bytes), so storage isn't an issue. `LONGTEXT` for safety.

### `Db.NormalizeValue` (`Db.cs:92–156`) — single source of truth

This function turns any CLR/MySQL value into a deterministic canonical string. Both sides MUST use the exact same normalizer or hashes will diverge.

| Input | Normalized form |
|---|---|
| `null` / `DBNull` | `""` |
| `bool true` | `"1"` |
| `bool false` | `"0"` |
| `sbyte` (TINYINT) | invariant integer string |
| all integer types | invariant integer string |
| `decimal` | invariant culture, full precision |
| `float` | `G9` invariant |
| `double` | `G17` invariant |
| `DateTime` | `yyyy-MM-dd HH:mm:ss` |
| `MySqlDateTime` valid | same; invalid (zero date) → `""` |
| `byte[]` | Base64 |
| `Guid` | lowercase with dashes |
| anything else | `ToString().Trim()` |

**Critical invariant:** `NormalizeValue(null) == NormalizeValue("")`. This means NULL and empty string are functionally equal in the engine — which matches MySQL `<=>` semantics for our use cases. Several places in the code explicitly normalize NULL → `""` before storing in the shadow's `last_value` column.

`Db.ComputeHash(value) = base64(SHA256(UTF8(NormalizeValue(value))))`.

---

## 6. Shadow tables — the 3-way merge baseline

Every sync run reads a per-database shadow table to do a **3-way comparison**: source value, target value, and the value the source had at last sync (the shadow). All four sync metadata tables live **in the target database**, not in `sails_master`. This was a deliberate move — centralizing was a source of unbounded growth and tenant cross-contamination.

```sql
CREATE TABLE `<target_db>`.sync_shadow_columns (
  table_name      VARCHAR(128) NOT NULL,
  column_name     VARCHAR(128) NOT NULL,
  record_pk       VARCHAR(128) NOT NULL,
  `last_value`    LONGTEXT NULL  COMMENT 'Last synced value - hash computed on-the-fly',
  last_synced_at  DATETIME NULL  COMMENT 'When this value was last synced',
  PRIMARY KEY (table_name, column_name, record_pk),
  INDEX idx_shadow_sync (table_name, column_name, record_pk, last_synced_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

`last_value` is reserved in MySQL 8 (window function); always backtick it in raw SQL.

The other three target-DB tables:

| Table | Purpose |
|---|---|
| `sync_shadow_columns` | last-seen source value per (table, column, PK) |
| `sync_conflict_log_columns` | every Case 5 conflict (true parallel edit) — stored as JSON |
| `sync_merge_audit` | broader merge decisions (currently underused; was for redo SQL) |
| `sync_checkpoints` | last-seen PK per table for resumable scans |

The shadow is created lazily by `Db.EnsureSyncTablesAsync` on the first sync.

### The shadow invariant

> **`shadow == last-seen source value`** for every (table, column, PK) the engine has ever processed.

If this invariant holds, the engine's per-column 3-way decision matrix in §7 produces correct results in every case. Three previous bugs (Bug 6, 7, 8 in `CLAUDE.md`) all violated this invariant in different paths and all caused ship edits to silently revert on the next sync. Read `project_bug8_shadow_invariant.md` if you're touching shadow writes.

---

## 7. The merge algorithm

`SyncEngine.SyncTableAsync` is the only orchestrator. Per-table:

### Step 1 — Existence checks

If the table is missing on either side, log warning and return zeros. Never attempt to sync something that doesn't exist on both sides.

### Step 2 — Metadata

`Db.GetTableMetaAsync` reads `information_schema.COLUMNS` to find:
- the **primary key** (must exist; tables without PK are skipped)
- an optional **`updatedAt`-style column** (used for first-sync LWW only — never for comparison)
- whether the table has a soft-delete column (`is_deleted` / `isDeleted` / `deleted`)
- the list of **comparable columns** = all non-PK columns minus the `SystemColumns` exclusion set

The `SystemColumns` set (`Db.cs:17–30`) excludes only **timestamp metadata**:
```
created_at, createdat, created_on, createdon, inserted_at, insertedat,
updated_at, updatedat, updated_on, updatedon, modified_at, modifiedat,
sync_*
```
Note that `createdBy` and `updatedBy` are **business data, not system columns** — they are synced. This was a previously-fixed bug.

### Step 3 — Column intersection

The schemas of source and target may differ slightly (e.g., a column added on online but not yet on ship). The engine intersects the two column sets and only compares columns that exist in both. Columns missing from either side are silently skipped with a debug log.

### Step 4 — Insert-missing

```sql
INSERT IGNORE INTO `<target_db>`.`<table>` (col, col, …)
SELECT s.col, s.col, …
FROM `<source_db>`.`<table>` s
LEFT JOIN `<target_db>`.`<table>` t ON t.<pk> = s.<pk>
WHERE t.<pk> IS NULL
  [AND NOT EXISTS (… UNIQUE-key dedup …)]
```

The optional `bizKeyFilter` is **only applied** when (a) the PK is auto-increment and (b) the table has a real UNIQUE constraint other than PK. This was deliberate: junction tables with auto-increment IDs and a UNIQUE constraint on the FK pair benefit from dedup; FK columns alone are NOT used because they are shared references and would block legitimate inserts. (Bug 4 fix.)

### Step 5 — Per-column 3-way merge

For each column in the intersection, the engine processes target rows in batches of `--batch` PKs. Per (table, column, batch):

1. `Db.LoadValueTriplesAsync` → for every PK in the batch, read `(tval, sval, tt, ts)` — target value, source value, target updatedAt, source updatedAt.
2. `Db.LoadShadowMapAsync` → read existing `(last_value, last_synced_at)` for this column from the shadow.
3. For each PK in the batch, compute `(targetHash, sourceHash, shadowHash)` via `Db.ComputeHash`.
4. Apply the **decision matrix** below, accumulate `toUpdate` and `toShadow` lists.
5. After the batch loop: `Db.BulkUpdateColumnAsync` (one bulk UPDATE … JOIN tmp_table ON pk) and `Db.UpsertShadowAsync`.

### Decision matrix

The `hasShadow` boolean splits behaviour into two sub-matrices.

**A. First sync (`!hasShadow`)** — there is no baseline, so we cannot know who changed what. Use a Last-Write-Wins fallback hierarchy (`SyncEngine.cs:418–525`):

```
1. sourceHash == targetHash               → already equal, write shadow=source, no update
2. online empty + ship has data           → SHIP_FILLS_GAP (ship value wins)
3. online has data + ship empty           → ONLINE_FILLS_GAP (online value wins)
4. both have updatedAt                    → SHIP_NEWER if ship.updatedAt > online.updatedAt,
                                             else ONLINE_NEWER
5. only one has updatedAt                 → that side wins
6. neither has updatedAt                  → ONLINE_DEFAULT (online wins)
```

In ALL six branches, the **shadow is set to the source value** (`Db.NormalizeValue(sval)`). This preserves the §6 invariant. Storing the *winner* here was the bug fixed as Bug 8 — when the winner was ship and the source was online, the next sync saw `source != shadow` and incorrectly fired Case 3.

**B. Subsequent sync (shadow exists)** — apply the 3-way decision (`SyncEngine.cs:528–641`):

| Case | Predicate | Meaning | Action | New shadow |
|:-:|---|---|---|---|
| 1 | `s == sh && t == sh` | nothing changed | skip | unchanged |
| 4 | `s == t` | both sides converged to same value | rewrite shadow | source value |
| 2 | `!sourceChanged && targetChanged` | only target side edited | **PRESERVE target** | source value |
| 3 | `sourceChanged && !targetChanged` | only source side edited | **PROPAGATE source to target** | source value |
| 5 | `s != t && both differ from shadow` | true conflict (parallel edit) | **online wins** (with `SHIP_FILLS_GAP` exception when source is empty) | source value |

Case 5 is the only place a conflict log row is written.

**Why "shadow always tracks source":** §6. The invariant is what makes Cases 1, 2, 3 stable across consecutive syncs. Without it, Case 2 would update shadow to the target value, the next sync would see `s != sh` → Case 3 → revert.

### Step 6 — Soft-delete propagation

If both sides have an `is_deleted`-shaped column:

- **`online_to_ship`** (delete, `0→1`): any row where `source.deleted=1 AND target.deleted=0` is marked deleted unconditionally (online wins).
- **`ship_to_online`** (delete, `0→1`): same, **but only if** `target.updatedAt <= source.updatedAt`. This prevents propagating an old ship-side delete over a more recent online-side edit. (Bug 2 fix.)
- **`online_to_ship`** (un-delete / restore, `1→0`): a row that is active online (`source.deleted=0`) but soft-deleted on the ship (`target.deleted=1`) is **restored on the ship**, **unconditionally** — online wins, with **no** timestamp comparison. An active online row always resurrects the ship's soft-deleted copy (intentional product-owner decision: restores follow the same "online wins" rule as deletes, not a newer-timestamp guard). This restore currently runs only for tables that **have** a timestamp column — the column's *existence* still gates the step, though its *value* is no longer compared. This is the only place the engine flips a delete flag back to `0`. There is intentionally **no** un-delete in `ship_to_online` — the online side's deleted state is authoritative and the ship cannot restore it.

#### Business-key duplicate resolution (online wins)

For tables with a configured `businessKeyColumn` (§Insert-missing dedup) **and** a
soft-delete column on both sides, the engine handles selection/junction tables
where **both sides may contribute** rows and the same logical row can be created
independently on each side (same business key, different auto-increment PKs). The
goal is for both databases to converge on the **union** of the two selections,
while removing the ship's redundant duplicate of any row the two share. Online is
authoritative for the shared rows.

Worked example — online has `{1,2,3,4}` and the ship has `{3,4,5}` for the same
parent. Final converged state on **both** sides: active `{1,2,3,4,5}`.

- **Retire the ship's duplicate of a shared row.** A ship row that shares its
  business key with an **active** online row is soft-deleted (`0→1`) — **except**
  the genuinely-synced canonical row, identified by **PK *and* business key** both
  matching an online row. Matching the keep-guard on PK **and** key (not PK alone)
  makes it robust to PK-number collisions across the two independent auto-increment
  sequences and guarantees the by-PK delete propagation can never delete online's
  own record. In the example, the ship's `3` and `4` are retired; ship-only `5` is
  kept and flows up to online.
- **Same-run propagation of the retire.** This step runs **before** insert-missing,
  so the now-deleted ship row propagates to online in the same run: a soft-deleted
  source row is intentionally **not** suppressed by the business-key dedup, so the
  insert step copies it to online as a deleted (invisible) row.
- **The union converges because the dedup ignores *deleted* target rows.** The
  insert-missing business-key dedup blocks an insert only when an **active** target
  row already has that key. A soft-deleted target row (e.g. the ship duplicate just
  retired) does **not** block. So on the reverse direction online's canonical `3`
  and `4` insert onto the ship as active rows (the retired ship copies stay
  soft-deleted underneath), and the ship ends up showing online's clean copy of
  every shared row plus its own `5`. Active-vs-active dedup is unchanged, so the
  original duplicate-insert prevention still holds.
- Tables **without** a configured business key are unaffected; active-row dedup
  behavior is unchanged.

> **Accumulation note:** each side keeps the retired duplicate as a soft-deleted
> row (invisible) in addition to the surviving canonical. This is harmless but
> means the physical row count can exceed the visible selection. The converged
> state is stable: subsequent runs make no further inserts or retires.

> **NULL-key contract:** a row is only retired when **all** configured key columns
> are non-NULL. An all-NULL business key is not a meaningful logical identity, and
> NULL-safe equality (`<=>`) would otherwise treat empty keys as equal and
> over-retire unrelated rows. Configure `businessKeyColumn` only on columns that
> are reliably populated for the records you intend to dedup.

---

## 8. Performance design

### Bulk update via temp table

The naive approach — one UPDATE per row — measured at ~20 minutes per 1,000 rows on the production server. The current approach (`Db.BulkUpdateColumnAsync`) creates an InnoDB `TEMPORARY TABLE` in MEMORY-style mode, bulk-inserts `(pk, val)` pairs in 1,000-row chunks, then runs a single `UPDATE … JOIN tmp ON pk = pk SET col = val`. ~10 seconds per 1,000 rows. If the bulk path raises a MySQL error, the engine falls back to a CASE-WHEN update via `SafeUpdateColumnAsync` to make sure no batch is silently dropped.

### Connection longevity

Long sync runs ran into MySQL `wait_timeout` and `net_read_timeout` cutoffs. `Db.EnhanceConnectionString` and `Db.SetSessionAsync` set:

- Connection timeout: 300 s
- Default command timeout: 3,600 s
- Keepalive: 60 s
- Pool 1–10
- `wait_timeout` / `interactive_timeout`: 28,800 s (8 h)
- `net_read_timeout` / `net_write_timeout`: 3,600 s
- `innodb_lock_wait_timeout`: 600 s
- `max_execution_time`: 0 (unlimited)

`SyncEngine.EnsureConnectionHealthAsync` pings the connection every 10 tables and reopens if necessary.

### FK checks off

`SET SESSION foreign_key_checks = 0` is set for the duration of the sync. Without this, child tables can't be inserted before parents, which would force a topological sort of the table list. With it off, table order doesn't matter and the engine can proceed alphabetically. Re-enabled at the end via `EnableForeignKeyChecksAsync`.

### Shadow upsert with retries

`Db.UpsertShadowAsync` retries on transient errors (`1205` lock timeout, `1213` deadlock, `2006/2013/2055` lost connection) with exponential backoff up to 3 attempts, then throws to abort the sync. A failed shadow write that goes silently corrupts the §6 invariant; aborting is correct.

---

## 9. Configuration & operator surface

### Inputs

Every parameter has three layers, highest precedence first:

1. **CLI flags** (`Program.ParseCli`) — `--connection`, `--central_db`, `--ship_db`, `--domain`, `--ship_imo`, `--direction`, `--env`, `--batch`, `--logLevel`, `--diagnose`.
2. **`appsettings.json`** in the working directory.
3. Hard-coded defaults (`direction=ship_to_online`, `env=online`, `batch=200`, `logLevel=Information`).

The CLI is the canonical interface for production. JSON exists for local development and is intentionally ignored if missing.

### Environment variables

| Var | Effect |
|---|---|
| `SAIL_SYNC_LOG_DIR` | Override default log directory |
| `SAIL_SYNC_HEADLESS` | Set to `1` to suppress console sink (used when invoked from a Node.js child process) |

### Default log path

| Platform | Path |
|---|---|
| Windows | `C:\SAIL\Logs\Sync` |
| Linux/macOS | `/var/log/sail/sync` (fallback `/tmp/sail/logs/sync`, then `Path.GetTempPath()/SAIL/Logs/Sync`, finally `cwd`) |

Files are `sync-YYYYMMDD.log`, daily rolling, 30-day retention.

### Outputs

- **stdout** — progress bar + summary block (suppressed in headless mode).
- **`sync-YYYYMMDD.log`** — full structured log; INFO and below default, DEBUG with `--logLevel Debug`.
- **`<target_db>.sync_conflict_log_columns`** — every Case 5 conflict, queryable for QA review.
- **Process exit code** — `0` success, `1` runtime error, `2` config error.

---

## 10. Operational invariants

These statements should be true at all times. If any is violated, sync correctness is at risk.

| # | Invariant | Enforced by |
|:-:|---|---|
| 1 | `Db.NormalizeValue` on both sides produces identical strings for any logically equivalent value | shared codebase + careful CLR-type handling |
| 2 | `shadow.last_value` always equals the *last-seen source value* for a synced (table, column, PK) | every `toShadow.Add(...)` call uses `Db.NormalizeValue(sval)` |
| 3 | `shadow.last_synced_at` is non-NULL whenever `last_value` is set | `UpsertShadowAsync` writes `UTC_TIMESTAMP()`; runtime checks NULL → corrupt-shadow rebuild |
| 4 | A column is synced only if it exists in BOTH databases | `SyncEngine.SyncTableAsync` step 3 (column intersection) |
| 5 | A row is inserted only if its PK is missing in target (and optionally a UNIQUE-key dedup passes) | `INSERT IGNORE … LEFT JOIN … WHERE t.pk IS NULL` |
| 6 | FK constraints are off during sync, and re-enabled after | `Db.SetSessionAsync` + `Db.EnableForeignKeyChecksAsync` in `Program.RunAsync.finally` |
| 7 | Online wins on Case 5 conflict (with `SHIP_FILLS_GAP` exception if source value is empty) | `SyncEngine.cs:593–633` |
| 8 | Soft-delete delete (`0→1`) is timestamp-guarded on ship→online **when a timestamp column exists** (falls back to unconditional if none), and unconditional on online→ship; un-delete (`1→0`) happens only on online→ship and only when a timestamp column exists and `target.updatedAt <= source.updatedAt` | `SyncEngine.cs` §Step 6 soft-delete block |

---

## 11. Known boundaries & non-goals

- **Schema migrations are out of scope.** If the cloud adds a column, the dump will include it; the ship's table won't have it; the engine will skip that column for sync until DDL is applied on ship.
- **Hard deletes are not propagated.** Only soft deletes (via `is_deleted` flag) are. Rows that are physically `DELETE`d on one side will simply re-appear next sync (the insert-missing step will recreate them from the dump).
- **No concurrent runs.** Two SyncConsole processes running against the same target DB simultaneously are not safe — both will read shadow, both will compute updates, the second one's shadow upserts may overwrite the first one's. Operators should guard against double invocation at the calling layer (typical: a single cron job or a backend-API queue).
- **The 2-day dump window is a pipeline assumption, not an engine choice.** The engine will faithfully sync whatever is in the dump. If QA edits a record on Apr 1 and the dump runs on Apr 5, the record won't be in the dump and won't sync. This was the dominant cause of QA's "doesn't sync" reports.
- **`Policies.cs` constants are not yet wired up.** Today the engine implements only LWW + online-wins. Policy plumbing exists in the model layer (`Models.SyncRule`) but has no runtime path. Expanding to `numeric_add`, `set_union`, etc., is a future feature.

---

## 12. Glossary

| Term | Meaning |
|---|---|
| **Source** | The DB the engine reads (the dump from the other side). |
| **Target** | The DB the engine writes (the live local DB). |
| **Online** | The cloud-side live database, regardless of direction. |
| **Ship** | The vessel-side live database, regardless of direction. |
| **Shadow** | `<target_db>.sync_shadow_columns` — the per-(table,column,PK) baseline. |
| **First sync** | A (table, column, PK) row that has no shadow entry yet. |
| **Subsequent sync** | A (table, column, PK) row that has a shadow entry. |
| **Case 1–5** | The five branches of the subsequent-sync 3-way merge matrix. |
| **LWW** | Last-Write-Wins (timestamp-based tiebreaker, used in first-sync only). |
| **Bug 6 / 7 / 8** | Documented historical regressions; see `CLAUDE.md` and the per-bug memory pages. |
| **Dump window** | The `WHERE date(updatedAt) BETWEEN DATE_SUB(CURDATE(), INTERVAL N DAY) AND CURDATE()` filter applied by the cloud-side `mysqldump` script (typically N=2). |
