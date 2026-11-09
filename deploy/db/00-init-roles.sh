#!/bin/sh
# Обгортка для /docker-entrypoint-initdb.d: передає паролі/ім'я БД у init-roles.sql як psql-змінні.
# Виконується лише при ПЕРШІЙ ініціалізації порожнього тому PGDATA (стандартна поведінка образу postgres).
set -eu
: "${BEAUTY_DB_OWNER_PASSWORD:?BEAUTY_DB_OWNER_PASSWORD is required}"
: "${BEAUTY_DB_APP_PASSWORD:?BEAUTY_DB_APP_PASSWORD is required}"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v db="${POSTGRES_DB:-beautycrm}" \
  -v owner_pw="$BEAUTY_DB_OWNER_PASSWORD" \
  -v app_pw="$BEAUTY_DB_APP_PASSWORD" \
  -f /opt/beauty-db/init-roles.sql
