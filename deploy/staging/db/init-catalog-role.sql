-- Роль beautycrm_catalog для публічного каталогу закладів (ADR-022). Виконується СУПЕРКОРИСТУВАЧЕМ Postgres у БД beautycrm
-- ДО міграції каталогу (20261109075625): beautycrm_owner не має CREATEROLE, тож створити роль сама міграція не може.
--   docker compose ... exec -T db psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 < db/init-catalog-role.sql
-- Ідемпотентний. Роль NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; членство owner у ній INHERIT FALSE, SET TRUE:
-- політики `TO beautycrm_catalog` діють на всіх, хто успадковує роль, тож INHERIT TRUE небезпечний (DbRoleGuard це перевіряє).

SELECT 'CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT'
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_catalog') \gexec

-- PG16: передача власності функцій ролі каталогу потребує членства з опцією SET.
GRANT beautycrm_catalog TO beautycrm_owner WITH INHERIT FALSE, SET TRUE;

-- Перевірка: рівно один рядок, rolcanlogin=f, rolsuper=f, rolbypassrls=f, rolinherit=f
SELECT rolname, rolcanlogin, rolsuper, rolbypassrls, rolinherit FROM pg_roles WHERE rolname = 'beautycrm_catalog';
-- Перевірка членства: inherit_option=f, set_option=t
SELECT m.inherit_option, m.set_option
  FROM pg_auth_members m
  JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_catalog'
  JOIN pg_roles g ON g.oid = m.member AND g.rolname = 'beautycrm_owner';
