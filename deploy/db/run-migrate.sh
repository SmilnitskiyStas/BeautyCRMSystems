#!/bin/sh
# Одноразовий job: EF idempotent SQL -> post-migrate.sql. Підключення під beautycrm_owner (PG* змінні з compose).
set -eu
: "${PGHOST:?}" "${PGUSER:?}" "${PGPASSWORD:?}" "${PGDATABASE:?}"
echo "[migrate] waiting for ${PGHOST}..."
i=0; until pg_isready -q -h "$PGHOST" -d "$PGDATABASE" -U "$PGUSER"; do i=$((i+1)); [ $i -gt 60 ] && { echo "[migrate] database not ready"; exit 1; }; sleep 2; done
echo "[migrate] applying EF migrations (idempotent script) as ${PGUSER}"
psql -X -v ON_ERROR_STOP=1 -q -f /opt/migrate/migrate.sql
echo "[migrate] applying post-migrate grants"
psql -X -v ON_ERROR_STOP=1 -v app_role="${BEAUTY_APP_ROLE:-beautycrm_app}" -f /opt/migrate/post-migrate.sql
echo "[migrate] done"
