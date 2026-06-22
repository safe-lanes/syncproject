---
name: soft-delete semantics
description: Directional rules and rationale for the soft-delete (is_deleted) flag in SyncEngine — why delete vs un-delete behave asymmetrically.
---

# Soft-delete (is_deleted) propagation rules

The soft-delete flag is handled ONLY by the dedicated soft-delete step in
`SyncEngine` (Step 6), never by the generic field merge. The delete column is
deliberately excluded from the field merge's compare columns so the generic
3-way merge can never flip the flag.

Direction mapping: `ship_to_online` => source=ship, target=online;
`online_to_ship` => source=online, target=ship. "Online wins" is the
conflict philosophy.

Current rules:
- Delete `0→1`, `ship_to_online`: timestamp-guarded — propagate only if
  `target(online).updatedAt <= source(ship).updatedAt`; falls back to
  unconditional if there is no timestamp column.
- Delete `0→1`, `online_to_ship`: unconditional (online wins).
- Un-delete `1→0`: ONLY in `online_to_ship`, and ONLY when a timestamp column
  exists AND `target(ship).updatedAt <= source(online).updatedAt`. No un-delete
  in `ship_to_online`.

**Why:** Deletes from the authoritative cloud are treated as safe; restores
(resurrecting a soft-deleted row) are dangerous, so un-delete is gated behind a
strict timestamp guard to ensure a more recent ship-side delete is never
resurrected by a stale online row. NULL/missing timestamps mean "don't restore"
(conservative) for un-delete, unlike the delete guard which treats NULL as
"go ahead". `ship_to_online` has no un-delete because the online side's deleted
state is authoritative and the ship must not override it.

## Business-key duplicate resolution (online wins)

For tables that have BOTH a configured business key and a soft-delete column, the
soft-delete step also retires a ship-originated duplicate when the same logical
record was independently created on both sides (same business key, different
auto-increment PKs):
- Retire condition: ship row active, an *active* online row shares the business
  key, AND no online row has the ship row's PK (PK-absence ⇒ ship-originated, so
  the genuinely-synced canonical row is never retired; conservative on PK
  collisions).
- The retired (deleted) ship row is then allowed to propagate to online because a
  soft-deleted *source* row is exempted from the insert business-key dedup — it
  inserts on online as a deleted row and the DBs converge (usually two runs).
- **NULL-key contract:** only retire when ALL configured key columns are non-NULL.
  `<=>` treats NULL=NULL as equal, so an all-NULL key would over-retire unrelated
  empty-key rows.
**Why:** the user's stated policy — if online has the record, the ship's separate
copy must stop showing; deleted rows never render, so syncing the deletion to
online is harmless and keeps both sides consistent.

**How to apply:** Any future change to delete-flag handling must stay inside the
dedicated soft-delete step (do not re-add the flag to the field merge), and must
keep the asymmetry above unless the product owner explicitly changes the
"online wins" / "deletes are safe, restores are guarded" contract. Keep
`docs/ARCHITECTURE.md` §7 (Step 6) and invariant #8 in sync with any change.
