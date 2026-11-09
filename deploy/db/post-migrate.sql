-- Права runtime-ролі після міграцій. Виконується під beautycrm_owner (власник схеми), ІДЕМПОТЕНТНО; запускати після
-- КОЖНОГО застосування міграцій (run-migrate.sh робить це автоматично).
-- Змінна psql: app_role (за замовчуванням beautycrm_app).
\if :{?app_role}
\else
\set app_role beautycrm_app
\endif

GRANT USAGE ON SCHEMA public TO :"app_role";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO :"app_role";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"app_role";
-- Runtime-роль не мігрує: журнал міграцій їй не потрібен.
REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM :"app_role";
-- Нові таблиці/послідовності, створені власником у майбутньому, теж доступні runtime-ролі.
ALTER DEFAULT PRIVILEGES FOR ROLE beautycrm_owner IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app_role";
ALTER DEFAULT PRIVILEGES FOR ROLE beautycrm_owner IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO :"app_role";

-- Функції публічного каталогу належать beautycrm_catalog: змінювати їхні ACL може лише їхній власник.
-- Власник схеми має SET-членство (init-roles.sql) -> тимчасово SET ROLE.
-- НЕ покладаємося на те, що PUBLIC втратив EXECUTE: явно відкликаємо й перевіряємо.
SET ROLE beautycrm_catalog;
REVOKE ALL ON FUNCTION public.beauty_public_resolve_location(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.beauty_public_resolve_location(text) TO :"app_role";
GRANT EXECUTE ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) TO :"app_role";
GRANT EXECUTE ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) TO :"app_role";
RESET ROLE;

-- Перевірка інваріантів: інакше job падає (і API все одно відмовиться стартувати через DbRoleGuard).
DO $chk$
DECLARE bad text;
BEGIN
    SELECT string_agg(p.proname, ', ') INTO bad
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'public' AND p.proname LIKE 'beauty\_public\_%' AND has_function_privilege('public', p.oid, 'EXECUTE');
    IF bad IS NOT NULL THEN RAISE EXCEPTION 'PUBLIC still has EXECUTE on: %', bad; END IF;
    IF (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE n.nspname = 'public' AND p.proname LIKE 'beauty\_public\_%') <> 3 THEN
        RAISE EXCEPTION 'expected exactly 3 beauty_public_* functions';
    END IF;
END
$chk$;
