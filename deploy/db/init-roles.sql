-- Ініціалізація ролей Beauty CRM. Виконується СУПЕРКОРИСТУВАЧЕМ Postgres один раз, при створенні порожнього кластера
-- (через 00-init-roles.sh у /docker-entrypoint-initdb.d), або вручну на наявному кластері:
--   psql -U postgres -v ON_ERROR_STOP=1 -v db=beautycrm -v owner_pw='...' -v app_pw='...' -f init-roles.sql
-- Ідемпотентний: повторний запуск нічого не ламає й не змінює паролі наявних ролей.
-- Ролі: beautycrm_owner (власник схеми, міграції) | beautycrm_app (runtime API/worker, NOSUPERUSER NOBYPASSRLS)
--       beautycrm_catalog (NOLOGIN NOBYPASSRLS NOINHERIT; власник SECURITY DEFINER-функцій каталогу, ADR-011).

SELECT format('CREATE ROLE beautycrm_owner LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L', :'owner_pw')
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_owner') \gexec
SELECT format('CREATE ROLE beautycrm_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L', :'app_pw')
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_app') \gexec
SELECT 'CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT'
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_catalog') \gexec

-- Власник схеми може віддати власність функцій каталогу ролі (потрібна опція SET, PG16);
-- INHERIT FALSE обов'язково: політики `TO beautycrm_catalog` діють на всіх, хто успадковує роль (DbRoleGuard це перевіряє).
GRANT beautycrm_catalog TO beautycrm_owner WITH INHERIT FALSE, SET TRUE;

-- База: створюється образом postgres (POSTGRES_DB) від суперкористувача; передаємо власнику.
SELECT format('CREATE DATABASE %I OWNER beautycrm_owner', :'db')
 WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') \gexec
SELECT format('ALTER DATABASE %I OWNER TO beautycrm_owner', :'db') \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'db') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO beautycrm_owner, beautycrm_app', :'db') \gexec

-- Схема public: у PG16 нею володіє pg_database_owner (тобто beautycrm_owner); CREATE для PUBLIC вже заборонено.
\connect :db
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO beautycrm_app;
