---
name: SyncConsole Replit setup
description: How the SyncConsole .NET CLI is wired to run in the Replit environment.
---

SyncConsole is a .NET 8 **console batch CLI** (MySQL field-level sync), not a web
app — no frontend, no HTTP port, no port 5000.

**Why MariaDB instead of just building:** the tool requires a reachable MySQL with
specific databases (`sails_master` holding `online_sync_tables`/`offline_sync_tables`,
plus the central/ship DBs named in `appsettings.json`). To make config genuinely
"work" rather than fail on connect, a local MariaDB 10.11 is provisioned.

**How to apply / gotchas:**
- `appsettings.json` is read from the **current working directory**, so the workflow
  must `cd SyncConsole` first (`bash -c 'cd SyncConsole && dotnet run -c Release'`).
- MariaDB runs on `127.0.0.1:3306` (not a Replit-monitored port — configure the
  workflow with NO `waitForPort`, or configureWorkflow rejects 3306).
- Backgrounding a server with `&` inside the bash tool gets killed by the sandbox;
  run persistent servers as a workflow instead. First-run DB setup (root password +
  CREATE DATABASE) is done by connecting over the unix socket after the server is up.
- Project targets `net8.0`; install the `dotnet-8.0` module (repo's `.replit`
  originally declared only `dotnet-7.0`).
- The `NU1903` System.Text.Json warning is known/accepted — do not bump.
