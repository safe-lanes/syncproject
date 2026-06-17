# SAIL ERP SyncConsole

## Overview
SyncConsole is a .NET 8 console application (a one-shot batch CLI) that performs
field-level, bidirectional synchronization between a ship-side MySQL database and
an online/cloud MySQL database. It is part of a larger production dump/transfer
pipeline; see `SyncConsole/docs/ARCHITECTURE.md` and `SyncConsole/docs/USAGE.md`
for the full design and operator guide.

## Tech Stack
- **Language/Runtime:** C# on .NET 8 (`net8.0`)
- **Key libraries:** Dapper, MySqlConnector, Serilog
- **Database:** MySQL 8.0+ (MariaDB 10.11 is used locally in this environment)

## Project Layout
- `SyncConsole/` — the .NET project root
  - `Program.cs` — CLI entry point, config merge, logging setup
  - `Db.cs` — all MySQL access and value normalization (hot zone)
  - `SyncEngine.cs` — the merge algorithm (hottest zone)
  - `appsettings.json` — local/dev config (CLI flags override; prod uses flags)
  - `docs/` — ARCHITECTURE.md and USAGE.md
  - `scripts/start-mysql.sh` — starts the local MariaDB used in this environment

## Running in Replit
Two workflows are configured:
- **MariaDB** — starts a local MariaDB instance on `127.0.0.1:3306` with data under
  `SyncConsole/.db/`. On first run it initializes the data directory; the root
  password and the databases referenced by `appsettings.json` are provisioned
  separately (`sails_master`, the central DB, and the ship DB, plus the
  `online_sync_tables` / `offline_sync_tables` config tables).
- **SyncConsole** — runs the sync once (`dotnet run -c Release` from the
  `SyncConsole/` directory so `appsettings.json` is found) and exits.

This is a batch CLI, not a web service — there is no frontend or HTTP port.

With the default `appsettings.json` (empty sync-table config), a run connects,
creates its sync metadata tables in the target DB, finds 0 active tables, and
exits 0. To perform a real sync, populate `sails_master.online_sync_tables` /
`offline_sync_tables` and load the source/target data, or pass overrides via CLI
flags (see `dotnet run -- --help`).

## Notes
- The `NU1903` warning about `System.Text.Json 8.0.4` is known and accepted (see
  `docs/USAGE.md` §B.2). Do not bump it without testing.
- `appsettings.json` should not carry real production credentials — production
  runs supply connection details via CLI flags.

## User preferences
(none recorded yet)
