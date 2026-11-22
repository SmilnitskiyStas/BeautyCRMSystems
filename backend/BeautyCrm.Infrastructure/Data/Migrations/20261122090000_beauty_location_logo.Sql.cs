using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    // Hand-written part of beauty_location_logo: the three ADR-022 catalog functions have a fixed RETURNS TABLE, so adding
    // the trailing `logo_storage_key text` column means DROP + CREATE (CREATE OR REPLACE cannot change the return type) and
    // re-applying EXACTLY the hardening of 20261109075625_beauty_public_location_catalog: REVOKE FROM PUBLIC, EXECUTE only to
    // beautycrm_app, SECURITY DEFINER + fixed search_path, ownership by beautycrm_catalog, column-level SELECT for the role.
    // Frozen together with the migration - add a new migration instead of editing after it was applied.
    //
    // DROP needs ownership. The migrator is a member of beautycrm_catalog WITH INHERIT FALSE (so it does not inherit the
    // owner's privileges, ADR-022 M1) but has the SET option, hence DROP runs under SET ROLE beautycrm_catalog and the
    // role is reset right after. The migration runs in a transaction: a failure rolls everything back.
    public partial class beauty_location_logo
    {
        private const string ResolveSql = """
                CREATE OR REPLACE FUNCTION public.beauty_public_resolve_location(p_code text)
                    RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                                   city text, timezone text@@OUT@@)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                    SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text@@SEL@@
                    FROM public.beauty_locations AS l
                    JOIN public.tenants AS t ON t.id = l.tenant_id
                    WHERE l.public_code = translate(upper(regexp_replace(coalesce(p_code, ''), '[\s-]', '', 'g')), 'OIL', '011')
                      AND l.is_active
                      AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
                    LIMIT 1
                $fn$;
                """;

        private const string NearbySql = """
                CREATE OR REPLACE FUNCTION public.beauty_public_nearby_locations(
                        p_lat double precision, p_lng double precision, p_radius_km double precision, p_limit integer)
                    RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                                   city text, timezone text, distance_km numeric@@OUT@@)
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
                           round(d.km::numeric, 1)@@SEL@@
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
                """;

        private const string SearchSql = """
                CREATE OR REPLACE FUNCTION public.beauty_public_search_locations_by_city(p_city text, p_limit integer)
                    RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                                   city text, timezone text@@OUT@@)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                    WITH q AS (
                        -- exact case-insensitive match (no wildcards, so it cannot be used for prefix scraping);
                        -- 2..100 characters; limit clamped to 1..10
                        SELECT lower(btrim(p_city)) AS c, greatest(1, least(coalesce(p_limit, 5), 10)) AS lim
                        WHERE p_city IS NOT NULL AND char_length(btrim(p_city)) BETWEEN 2 AND 100
                    )
                    SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text@@SEL@@
                    FROM q
                    JOIN public.beauty_locations AS l
                      ON lower(l.city) = q.c AND l.is_discoverable AND l.is_active
                    JOIN public.tenants AS t
                      ON t.id = l.tenant_id AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
                    ORDER BY l.name, l.public_code
                    LIMIT (SELECT lim FROM q)
                $fn$;
                """;

        private static string Fn(string template, bool withLogo) => template
            .Replace("@@OUT@@", withLogo ? ", logo_storage_key text" : "")
            .Replace("@@SEL@@", withLogo ? ", l.logo_storage_key::text" : "");

        private static void DropCatalogFunctions(MigrationBuilder mb)
        {
            // One statement on purpose: in the idempotent script (`migrations script --idempotent`) every mb.Sql becomes its own
            // DO block guarded by a SELECT on __EFMigrationsHistory, which beautycrm_catalog cannot read - so the role must be
            // reset inside the same block.
            mb.Sql("""
                SET LOCAL ROLE beautycrm_catalog;
                DROP FUNCTION IF EXISTS public.beauty_public_search_locations_by_city(text, integer);
                DROP FUNCTION IF EXISTS public.beauty_public_nearby_locations(double precision, double precision, double precision, integer);
                DROP FUNCTION IF EXISTS public.beauty_public_resolve_location(text);
                RESET ROLE;
                """);
        }

        private static void CreateCatalogFunctions(MigrationBuilder mb, bool withLogo)
        {
            mb.Sql(Fn(ResolveSql, withLogo));
            mb.Sql(Fn(NearbySql, withLogo));
            mb.Sql(Fn(SearchSql, withLogo));

            // NB: EXECUTE is set BEFORE the ownership transfer (afterwards the migrator is no longer the owner).
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

            mb.Sql("GRANT CREATE ON SCHEMA public TO beautycrm_catalog;");
            mb.Sql("ALTER FUNCTION public.beauty_public_resolve_location(text) OWNER TO beautycrm_catalog;");
            mb.Sql("ALTER FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) OWNER TO beautycrm_catalog;");
            mb.Sql("ALTER FUNCTION public.beauty_public_search_locations_by_city(text, integer) OWNER TO beautycrm_catalog;");
            mb.Sql("REVOKE CREATE ON SCHEMA public FROM beautycrm_catalog;");
        }

        private static void UpCatalogLogo(MigrationBuilder mb)
        {
            // Column grant first, kept next to the functions that read it (privileges are checked at execution; the order is for readability).
            mb.Sql("GRANT SELECT (logo_storage_key) ON beauty_locations TO beautycrm_catalog;");
            DropCatalogFunctions(mb);
            CreateCatalogFunctions(mb, withLogo: true);
        }

        private static void DownCatalogLogo(MigrationBuilder mb)
        {
            DropCatalogFunctions(mb);
            CreateCatalogFunctions(mb, withLogo: false);
            mb.Sql("REVOKE SELECT (logo_storage_key) ON beauty_locations FROM beautycrm_catalog;");
        }
    }
}
