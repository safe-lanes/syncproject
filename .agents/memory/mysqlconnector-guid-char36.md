---
name: MySqlConnector CHAR(36) → Guid auto-mapping
description: Why the sync connection string must set GuidFormat=None — CHAR(36) columns are not always UUIDs.
---

# MySqlConnector auto-maps CHAR(36) to System.Guid

By default MySqlConnector reads `CHAR(36)` columns as `System.Guid`. If such a
column holds a value that is not a valid GUID (e.g. a plain string/number like
`"3400037"`), the driver throws
`System.FormatException: Could not parse CHAR(36) value as Guid` while reading
the row (in the column reader, before your code sees the value).

**Why this bites this project:** the sync is generic/table-agnostic and runs
against real schemas where some `CHAR(36)` columns are UUIDs and others are not
("both uuid and string"). Any read path (e.g. loading value triples for the
merge) can hit a non-UUID `CHAR(36)` value and crash.

**Fix / rule:** the connection string builder sets `GuidFormat =
MySqlGuidFormat.None` so NO column type is auto-read as Guid; `CHAR(36)` comes
back as a plain string. Keep this setting — do not remove it or switch to
`Char36`. PK values are already handled as strings throughout the engine, so
string is the correct representation.

**How to apply:** any new connection construction must go through the shared
connection-string builder (so it inherits `GuidFormat=None`). If you ever add a
second builder or a raw `new MySqlConnection(connString)` without the enhanced
string, replicate this setting or you will reintroduce the crash.
