#!/usr/bin/env bash
set -euo pipefail

# Local MariaDB instance for running SyncConsole in the Replit environment.
# Data lives under the project so it persists across restarts.
# On first init it bootstraps the root password and the databases referenced
# by appsettings.json so a fresh environment is reproducible without manual SQL.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DB_DIR="$ROOT_DIR/.db"
DATA_DIR="$DB_DIR/data"
SOCKET="$DB_DIR/mysql.sock"
PORT="3306"
ROOT_PASS="Abeer@7860"
CENTRAL_DB="46df6g9x-klpm2-68hp-47kl-180734rsms"
SHIP_DB="46df6g9x-klpm2-68hp-47kl-180734rsms_9340415"

mkdir -p "$DB_DIR"

if [ ! -d "$DATA_DIR/mysql" ]; then
  echo "Initializing MariaDB data directory..."
  mariadb-install-db \
    --datadir="$DATA_DIR" \
    --auth-root-authentication-method=normal \
    --skip-test-db >/dev/null

  echo "Bootstrapping root password and databases..."
  mariadbd --datadir="$DATA_DIR" --socket="$SOCKET" --skip-networking &
  TMP_PID=$!

  for _ in $(seq 1 60); do
    [ -S "$SOCKET" ] && mariadb --socket="$SOCKET" -u root -e "SELECT 1" >/dev/null 2>&1 && break
    sleep 1
  done

  mariadb --socket="$SOCKET" -u root <<SQL
ALTER USER 'root'@'localhost' IDENTIFIED BY '${ROOT_PASS}';
CREATE DATABASE IF NOT EXISTS sails_master;
CREATE DATABASE IF NOT EXISTS \`${CENTRAL_DB}\`;
CREATE DATABASE IF NOT EXISTS \`${SHIP_DB}\`;
USE sails_master;
CREATE TABLE IF NOT EXISTS online_sync_tables (
  tablename     VARCHAR(255) NOT NULL PRIMARY KEY,
  isActive      TINYINT(1) NOT NULL DEFAULT 1,
  isMasterTable TINYINT(1) NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS offline_sync_tables (
  tablename     VARCHAR(255) NOT NULL PRIMARY KEY,
  isActive      TINYINT(1) NOT NULL DEFAULT 1,
  isMasterTable TINYINT(1) NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
FLUSH PRIVILEGES;
SQL

  mariadb-admin --socket="$SOCKET" -u root -p"${ROOT_PASS}" shutdown
  wait "$TMP_PID" 2>/dev/null || true
  echo "MariaDB bootstrap complete."
fi

echo "Starting MariaDB on 127.0.0.1:${PORT}..."
exec mariadbd \
  --datadir="$DATA_DIR" \
  --socket="$SOCKET" \
  --bind-address=127.0.0.1 \
  --port="$PORT"
