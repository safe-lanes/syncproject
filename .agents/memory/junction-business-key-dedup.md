---
name: Business-key dedup for junction/selection tables
description: How the sync prevents duplicate INSERTs for tables whose logical key differs from their auto-increment PK.
---

# Business-key dedup (config-driven)

Junction/selection tables (e.g. checkbox link rows) often have an auto-increment
PK assigned **independently** on ship vs online, so the same logical row has a
different PK on each side. The insert-missing step matches by PK, so without
another key it would insert a second copy of an already-present row.

**The contract:** an optional config column `businessKeyColumn` on
`sails_master.offline_sync_tables` / `online_sync_tables` holds a JSON array of
column names, e.g. `["nearmissId","nearmissImpactId"]`. NULL/blank means "no
override" and the table keeps the legacy behavior.

**Dedup precedence in the INSERT step:**
1. Configured business key (when present) — authoritative; used for the
   `NOT EXISTS` anti-join regardless of PK type. If ANY configured column is not
   in the insert set, it warns and falls back rather than building a partial-key
   predicate (a partial key would over-dedup and silently drop good rows).
2. Fallback (no configured key): the original auto-increment-PK + DB `UNIQUE`
   index heuristic. Tables without a business key behave exactly as before.

**Insert-only scope:** the dedup stops the duplicate *insert*. It does NOT fix
the deeper risk that the compare/merge step still joins by PK, so the same
auto-increment id can map to different logical rows across sides — that requires
matching the merge step by business key too (still deferred).

**Duplicate resolution when both sides already created the row** (online wins):
for business-key tables that also have a soft-delete column, a pre-insert step
retires EVERY ship row whose business key matches an *active* online row, except
the true synced canonical (matched on PK **and** business key — never PK alone,
which breaks on cross-side auto-increment collisions). It runs BEFORE
insert-missing, so the retired (deleted) row propagates to online in the SAME
run via the dedup's soft-deleted-source exemption. See soft-delete-semantics.md
for the full contract and the bidirectional nuance.

**Gotchas / rules:**
- The column must exist in **both** config tables (`offline_sync_tables` AND
  `online_sync_tables`) to work in both directions; the loader probes
  `information_schema` and degrades to "dedup disabled + warning" if a config
  table lacks the column, so a one-sided migration won't crash the sync.
- Business-key names are interpolated into SQL as identifiers. They are validated
  at parse time (`^[A-Za-z0-9_]+$`) AND allowlisted against the table's real
  columns before use. Keep both checks if you refactor — do not interpolate raw
  config text into SQL.
