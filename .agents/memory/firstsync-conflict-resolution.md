---
name: First-sync conflict resolution (no shadow baseline)
description: How SyncEngine resolves a field with no shadow baseline; why first-sync now uses online-wins instead of Last-Write-Wins.
---

# First-sync (no-shadow) field resolution

When a field has **no shadow baseline** (`!hasShadow`), the engine cannot do a true
3-way merge. The resolution order is now:

1. values equal → establish shadow, no update
2. exactly one side empty → the side with data fills the gap (SHIP_FILLS_GAP / ONLINE_FILLS_GAP)
3. **both sides have data and differ → ONLINE wins** (label `ONLINE_WINS`)

The `updatedAt` column is **not** consulted for field resolution anymore (it is still
used by the soft-delete timestamp guard, a separate step).

**Why:** the previous first-sync fallback was Last-Write-Wins (newer `updatedAt` wins).
Real-world failure: a record was created + synced while an optional field was empty on
both sides (so no baseline was ever recorded for that field), then the field was filled
with different values on each side, online first then ship. ship→online had no online-side
baseline → first-sync path → ship was newer → ship overwrote online; the next direction
then converged both sides onto the **ship/offline** value. Operators expect "online wins"
for a same-field both-populated conflict regardless of edit recency.

**How to apply:** first-sync both-populated conflicts must mirror the subsequent-sync
Case-5 outcome (online wins). Do not reintroduce a timestamp tiebreaker for field values
without an explicit product-owner decision. Gap-fill (one side empty) is intentionally
preserved — emptiness never overwrites real data, on either path.

**Caveat — why the baseline can be missing:** the shadow is established only by the
compare/merge step for rows present on both sides, and only after a sync has actually
run while the field held its then-current value. A field that was empty at the row's
first sync and populated afterward may hit the first-sync path the first time real
values appear, which is exactly when this rule matters.
