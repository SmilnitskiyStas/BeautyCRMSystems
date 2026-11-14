using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    // Hand-written part of beauty_public_location_catalog (ADR-022): things EF does not generate.
    // Frozen together with the migration - add a new migration instead of editing after it was applied.
    //
    // PROVISIONING NOTE (prod): the migration creates the NOLOGIN role `beautycrm_catalog` if it is missing
    // (idempotent), which needs CREATEROLE (or superuser) for the migrator. If ops prefer to create the role
    // themselves, run before the migration (as a role with CREATEROLE):
    //     CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
    //     GRANT beautycrm_catalog TO <migrator_role> WITH INHERIT FALSE, SET TRUE;  -- hand function ownership (PG16)
    // INHERIT FALSE is mandatory (the TO beautycrm_catalog policies apply to every inheriting member); the migration
    // re-grants the migrator's own membership with these options and DbRoleGuard fails on any inheriting member.
    // The migration fails loudly if the role exists but is LOGIN / SUPERUSER / BYPASSRLS, or if the migrator
    // is not a member of it. EXECUTE is granted to the runtime role `beautycrm_app` if that role exists;
    // otherwise a NOTICE is raised and ops must run the GRANT EXECUTE statements (see docs/database.md).
    public partial class beauty_public_location_catalog
    {
        private static void UpPrelude(MigrationBuilder mb)
        {
            // Crockford Base32 code generator (no I, L, O, U). CSPRNG-backed: gen_random_uuid() bytes (version/variant
            // bytes 6 and 8 are skipped, the other bytes are fully random); 5 bits per char. VOLATILE: evaluated per
            // row, so it also backfills existing rows (ALTER TABLE ... ADD COLUMN with a volatile DEFAULT rewrites
            // the table and calls the function for every row). A collision (p ~ n^2 / 2^41) makes the unique index
            // creation fail and the whole migration roll back; re-running draws new codes.
            mb.Sql("""
                CREATE OR REPLACE FUNCTION public.beauty_generate_public_code() RETURNS text
                    LANGUAGE sql VOLATILE PARALLEL SAFE SET search_path = pg_catalog, pg_temp AS $fn$
                    SELECT string_agg(substr('0123456789ABCDEFGHJKMNPQRSTVWXYZ', (get_byte(s.b, u.i) & 31) + 1, 1), '' ORDER BY u.n)
                    FROM (SELECT decode(replace(gen_random_uuid()::text, '-', ''), 'hex') AS b) AS s,
                         unnest(ARRAY[0, 1, 2, 3, 4, 5, 7, 9]) WITH ORDINALITY AS u(i, n)
                $fn$;
                """);
        }

        private static void DownPrelude(MigrationBuilder mb)
        {
            mb.Sql("DROP FUNCTION IF EXISTS public.beauty_generate_public_code();");
        }

        private static void UpCatalog(MigrationBuilder mb)
        {
            // Partial expression index for the by-city search.
            mb.Sql("""
                CREATE INDEX ix_beauty_locations_discoverable_city ON beauty_locations (lower(city))
                    WHERE is_discoverable AND is_active AND city IS NOT NULL;
                """);

            // ---- Role (idempotent) ----
            mb.Sql("""
                DO $do$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_catalog') THEN
                        BEGIN
                            CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
                        EXCEPTION
                            WHEN duplicate_object THEN NULL; -- concurrent creation by another migration
                            WHEN insufficient_privilege THEN
                            RAISE EXCEPTION 'Role beautycrm_catalog does not exist and % cannot create it (needs CREATEROLE). Create it as ops: CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; GRANT beautycrm_catalog TO %;', current_user, current_user;
                        END;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_catalog'
                               AND (rolcanlogin OR rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'Role beautycrm_catalog must be NOLOGIN NOSUPERUSER NOBYPASSRLS (ADR-022)';
                    END IF;

                    -- The migrator must be able to hand ownership of the functions to the role (needs the SET option,
                    -- PG16). The membership MUST be INHERIT FALSE: the role-scoped policies are TO beautycrm_catalog, and
                    -- a policy applies to every role that inherits the privileges of that role - so an inheriting
                    -- migrator would see the catalog rows directly (security review M1).
                    IF NOT EXISTS (SELECT 1
                                   FROM pg_auth_members m
                                   JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_catalog'
                                   JOIN pg_roles u ON u.oid = m.member AND u.rolname = current_user
                                   WHERE m.set_option AND NOT m.inherit_option) THEN
                        BEGIN
                            EXECUTE format('GRANT beautycrm_catalog TO %I WITH INHERIT FALSE, SET TRUE', current_user);
                        EXCEPTION WHEN insufficient_privilege THEN
                            RAISE EXCEPTION 'The migrator % must be a member of beautycrm_catalog WITH INHERIT FALSE, SET TRUE and cannot change it. Ops: GRANT beautycrm_catalog TO % WITH INHERIT FALSE, SET TRUE;', current_user, current_user;
                        END;
                    END IF;
                END
                $do$;
                """);

            // ---- Column-level grants (public fields only) + role-scoped read policies ----
            mb.Sql("GRANT USAGE ON SCHEMA public TO beautycrm_catalog;");
            mb.Sql("""
                GRANT SELECT (id, tenant_id, name, address, city, timezone, is_active,
                              public_code, latitude, longitude, is_discoverable)
                    ON beauty_locations TO beautycrm_catalog;
                """);
            mb.Sql("GRANT SELECT (id, slug, status, modules) ON tenants TO beautycrm_catalog;");

            mb.Sql("DROP POLICY IF EXISTS public_catalog_read ON beauty_locations;");
            mb.Sql("""
                CREATE POLICY public_catalog_read ON beauty_locations
                    FOR SELECT TO beautycrm_catalog
                    USING (is_active);
                """);
            mb.Sql("DROP POLICY IF EXISTS public_catalog_read ON tenants;");
            mb.Sql("""
                CREATE POLICY public_catalog_read ON tenants
                    FOR SELECT TO beautycrm_catalog
                    USING (status = 'active' AND 'beauty_booking' = ANY (modules));
                """);

            // ---- Functions (STABLE, SECURITY DEFINER, fixed search_path, schema-qualified, fixed output) ----
            mb.Sql("""
                CREATE OR REPLACE FUNCTION public.beauty_public_resolve_location(p_code text)
                    RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                                   city text, timezone text)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                    SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text
                    FROM public.beauty_locations AS l
                    JOIN public.tenants AS t ON t.id = l.tenant_id
                    WHERE l.public_code = translate(upper(regexp_replace(coalesce(p_code, ''), '[\s-]', '', 'g')), 'OIL', '011')
                      AND l.is_active
                      AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
                    LIMIT 1
                $fn$;
                """);

            mb.Sql("""
                CREATE OR REPLACE FUNCTION public.beauty_public_nearby_locations(
                        p_lat double precision, p_lng double precision, p_radius_km double precision, p_limit integer)
                    RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                                   city text, timezone text, distance_km numeric)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                    WITH q AS (
                        -- invalid / NaN / out-of-range coordinates -> no rows; input is rounded to 3 decimals (~110 m);
                        -- radius is clamped to (0, 30] km (default 30), limit to 1..10
                        SELECT round(p_lat::numeric, 3)::double precision AS lat,
                               round(p_lng::numeric, 3)::double precision AS lng,
                               CASE WHEN p_radius_km IS NULL OR p_radius_km <= 0 THEN 30.0
                                    ELSE least(p_radius_km, 30.0) END AS r,
                               greatest(1, least(coalesce(p_limit, 5), 10)) AS lim
                        WHERE p_lat BETWEEN -90 AND 90 AND p_lng BETWEEN -180 AND 180
                    ),
                    b AS (
                        SELECT q.lat, q.lng, q.r, q.lim,
                               q.r / 111.195 AS dlat,
                               least(180.0, q.r / (111.195 * greatest(cos(radians(q.lat)), 0.01))) AS dlng
                        FROM q
                    )
                    SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text,
                           round(d.km::numeric, 1)
                    FROM b
                    JOIN public.beauty_locations AS l
                      ON l.is_discoverable AND l.is_active AND l.latitude IS NOT NULL AND l.longitude IS NOT NULL
                     AND l.latitude BETWEEN b.lat - b.dlat AND b.lat + b.dlat
                     AND (l.longitude BETWEEN b.lng - b.dlng AND b.lng + b.dlng
                          OR b.lng - b.dlng < -180 OR b.lng + b.dlng > 180)
                    JOIN public.tenants AS t
                      ON t.id = l.tenant_id AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
                    CROSS JOIN LATERAL (
                        SELECT 2 * 6371.0 * asin(least(1.0, sqrt(
                                   sin(radians(l.latitude - b.lat) / 2) ^ 2
                                   + cos(radians(b.lat)) * cos(radians(l.latitude)) * sin(radians(l.longitude - b.lng) / 2) ^ 2)))
                    ) AS d(km)
                    WHERE d.km <= b.r
                    ORDER BY d.km, l.public_code
                    LIMIT (SELECT lim FROM b)
                $fn$;
                """);

            mb.Sql("""
                CREATE OR REPLACE FUNCTION public.beauty_public_search_locations_by_city(p_city text, p_limit integer)
                    RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                                   city text, timezone text)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                    WITH q AS (
                        -- exact case-insensitive match (no wildcards, so it cannot be used for prefix scraping);
                        -- 2..100 characters; limit clamped to 1..10
                        SELECT lower(btrim(p_city)) AS c, greatest(1, least(coalesce(p_limit, 5), 10)) AS lim
                        WHERE p_city IS NOT NULL AND char_length(btrim(p_city)) BETWEEN 2 AND 100
                    )
                    SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text
                    FROM q
                    JOIN public.beauty_locations AS l
                      ON lower(l.city) = q.c AND l.is_discoverable AND l.is_active
                    JOIN public.tenants AS t
                      ON t.id = l.tenant_id AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
                    ORDER BY l.name, l.public_code
                    LIMIT (SELECT lim FROM q)
                $fn$;
                """);

            // NB: EXECUTE is set BEFORE the ownership transfer: afterwards the migrator is no longer the owner and
            // REVOKE/GRANT would silently do nothing (the ACL entries move to the new owner with ALTER OWNER).
            // ---- EXECUTE: nobody but the runtime role ----
            mb.Sql("REVOKE ALL ON FUNCTION public.beauty_public_resolve_location(text) FROM PUBLIC;");
            mb.Sql("REVOKE ALL ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) FROM PUBLIC;");
            mb.Sql("REVOKE ALL ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) FROM PUBLIC;");
            mb.Sql("""
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_app') THEN
                        GRANT EXECUTE ON FUNCTION public.beauty_public_resolve_location(text) TO beautycrm_app;
                        GRANT EXECUTE ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) TO beautycrm_app;
                        GRANT EXECUTE ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) TO beautycrm_app;
                    ELSE
                        RAISE NOTICE 'Role beautycrm_app not found: GRANT EXECUTE on beauty_public_* functions to the runtime role manually (ADR-022).';
                    END IF;
                END
                $do$;
                """);

            // ---- Ownership (the role needs CREATE on the schema only for the duration of ALTER ... OWNER) ----
            mb.Sql("GRANT CREATE ON SCHEMA public TO beautycrm_catalog;");
            mb.Sql("ALTER FUNCTION public.beauty_public_resolve_location(text) OWNER TO beautycrm_catalog;");
            mb.Sql("ALTER FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) OWNER TO beautycrm_catalog;");
            mb.Sql("ALTER FUNCTION public.beauty_public_search_locations_by_city(text, integer) OWNER TO beautycrm_catalog;");
            mb.Sql("REVOKE CREATE ON SCHEMA public FROM beautycrm_catalog;");

            // ---- public_code is immutable (L9): published codes/QRs must never change after creation ----
            mb.Sql("""
                CREATE OR REPLACE FUNCTION public.beauty_forbid_public_code_change() RETURNS trigger
                    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
                BEGIN
                    RAISE EXCEPTION 'beauty_locations.public_code is immutable' USING ERRCODE = 'restrict_violation';
                END
                $fn$;
                """);
            mb.Sql("""
                CREATE TRIGGER trg_beauty_locations_public_code_immutable
                    BEFORE UPDATE OF public_code ON beauty_locations
                    FOR EACH ROW WHEN (OLD.public_code IS DISTINCT FROM NEW.public_code)
                    EXECUTE FUNCTION public.beauty_forbid_public_code_change();
                """);
        }

        private static void DownCatalog(MigrationBuilder mb)
        {
            mb.Sql("DROP TRIGGER IF EXISTS trg_beauty_locations_public_code_immutable ON beauty_locations;");
            mb.Sql("DROP FUNCTION IF EXISTS public.beauty_forbid_public_code_change();");
            mb.Sql("DROP FUNCTION IF EXISTS public.beauty_public_search_locations_by_city(text, integer);");
            mb.Sql("DROP FUNCTION IF EXISTS public.beauty_public_nearby_locations(double precision, double precision, double precision, integer);");
            mb.Sql("DROP FUNCTION IF EXISTS public.beauty_public_resolve_location(text);");
            mb.Sql("DROP POLICY IF EXISTS public_catalog_read ON tenants;");
            mb.Sql("DROP POLICY IF EXISTS public_catalog_read ON beauty_locations;");
            mb.Sql("DROP INDEX IF EXISTS ix_beauty_locations_discoverable_city;");
            // The cluster-level role beautycrm_catalog is intentionally NOT dropped (may be shared by other databases
            // on the cluster); only its table privileges from this migration are revoked.
            mb.Sql("""
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_catalog') THEN
                        REVOKE SELECT ON beauty_locations FROM beautycrm_catalog;
                        REVOKE SELECT ON tenants FROM beautycrm_catalog;
                        REVOKE USAGE ON SCHEMA public FROM beautycrm_catalog;
                    END IF;
                END
                $do$;
                """);
        }
    }
}
