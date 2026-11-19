#!/bin/bash
# Виконується офіційним образом postgres ОДИН раз, при ініціалізації порожнього тому (/docker-entrypoint-initdb.d).
# Файл змонтовано без біта виконання, тож entrypoint його source-ить; тому тіло в підоболонці (set -eu не просочується).
# Створює ролі й БД. Паролі беруться зі змінних середовища контейнера db (db.env), у файлах не зберігаються.
# Міграції НЕ застосовує (human gate); GRANT-и на таблиці - окремо через grants.sql після міграції.
(
  set -eu
  : "${DB_OWNER_PASSWORD:?}" "${DB_APP_PASSWORD:?}" "${DB_WORKER_PASSWORD:?}"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
    -v owner_pw="$DB_OWNER_PASSWORD" -v app_pw="$DB_APP_PASSWORD" -v worker_pw="$DB_WORKER_PASSWORD" <<'SQL'
CREATE ROLE beautycrm_owner  LOGIN PASSWORD :'owner_pw'  NOSUPERUSER NOBYPASSRLS;
CREATE ROLE beautycrm_app    LOGIN PASSWORD :'app_pw'    NOSUPERUSER NOBYPASSRLS;
CREATE ROLE beautycrm_worker LOGIN PASSWORD :'worker_pw' NOSUPERUSER NOBYPASSRLS;
CREATE DATABASE beautycrm OWNER beautycrm_owner;
GRANT CONNECT ON DATABASE beautycrm TO beautycrm_app, beautycrm_worker;
SQL
) || exit 1
