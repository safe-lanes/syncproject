---
name: Soft-delete column name detection
description: The recognized soft-delete column names live in TWO spots that must stay in sync; an unrecognized name silently disables all delete handling for a table.
---

The engine recognizes a table's soft-delete column by matching its name against a
fixed allow-list. That allow-list is duplicated in **two** places in `Db.cs` and
both must be kept in sync:

1. The `DeleteColumnNames` HashSet (excludes the column from field-level comparison).
2. The hardcoded `IN (...)` list in the SQL of `GetDeletedFlagColumnAsync`
   (drives `meta.HasDeleted`, i.e. retirement/convergence + delete propagation).

**Why it matters:** if a table's soft-delete column name is in *neither* list, the
engine concludes the table has no soft-delete column. Consequences, all silent:
- the business-key duplicate-retirement / convergence step is skipped (because it
  is gated on `meta.HasDeleted`), so two independently-created rows with the same
  business key but different PKs just deadlock — neither inserts (mutually blocked)
  and the PK-based merge can't pair them;
- deletes for that table never propagate;
- the delete flag is treated as an ordinary data field in the merge.

If the name is in only ONE list it is worse than neither: only-SQL → delete step
runs but the flag is also compared as a normal field (merge fights propagation);
only-HashSet → flag excluded from comparison but `HasDeleted` stays false so the
delete is neither compared nor propagated (deletes vanish).

**How to apply:** when onboarding tables whose soft-delete column uses a new spelling
(e.g. SAIL's JHA tables use `isDelete`, vs the previously-recognized `is_deleted` /
`isdeleted` / `deleted`), add the lowercased name to BOTH lists together. Blast
radius: the match is case-insensitive and global, so every synced table with a
column of that name becomes soft-delete-managed. The delete step assumes the
monotonic `0=active, 1=deleted` convention.
