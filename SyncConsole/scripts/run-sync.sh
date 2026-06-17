#!/usr/bin/env bash
set -euo pipefail

# Runs the SyncConsole batch sync once, after waiting for the local MariaDB
# to accept connections. Run from the SyncConsole project directory so that
# appsettings.json resolves correctly.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

HOST="127.0.0.1"
PORT="3306"

echo "Waiting for MariaDB at ${HOST}:${PORT}..."
for _ in $(seq 1 60); do
  if (exec 3<>"/dev/tcp/${HOST}/${PORT}") 2>/dev/null; then
    exec 3>&- 2>/dev/null || true
    echo "MariaDB is reachable."
    break
  fi
  sleep 1
done

exec dotnet run -c Release
