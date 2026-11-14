using Microsoft.EntityFrameworkCore.Infrastructure;
using BeautyCrm.Infrastructure.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>ADR-022: public_code, cross-tenant public catalog (definer functions of beautycrm_catalog) and role invariants.</summary>
public sealed class PublicCatalogRlsTests : IClassFixture<PostgresRlsFixture>
{
    private static readonly Regex CodeFormat = new("^[0-9A-HJKMNP-TV-Z]{8}$");

    private readonly PostgresRlsFixture _db;

    public PublicCatalogRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seeded(Guid TenantId, string Slug, Guid LocationId, string Code);

    private async Task<NpgsqlConnection> AdminAsync()
    {
        var c = new NpgsqlConnection(_db.AdminConnectionString);
        await c.OpenAsync();
        return c;
    }

    private static async Task<object?> Scalar(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync();
    }

    private static string Inv(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Seeds a tenant + location as admin (public_code comes from the DB default).</summary>
    private async Task<Seeded> SeedAsync(string name, double? lat = null, double? lng = null, string? city = null,
        bool discoverable = false, bool active = true, string status = "active", string modules = "{beauty_booking}",
        string? publicCode = null)
    {
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var slug = "cat-" + tenantId.ToString("N")[..20];
        await using var admin = await AdminAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO tenants (id, name, slug, status, modules) VALUES (@t, 'T', @s, @st, @m::text[]); " +
            "INSERT INTO beauty_locations (id, tenant_id, name, address, timezone, is_active, latitude, longitude, city, is_discoverable" +
            (publicCode is null ? "" : ", public_code") + ") " +
            "VALUES (@l, @t, @n, 'Addr 1', 'Europe/Kyiv', @a, @lat, @lng, @c, @d" + (publicCode is null ? "" : ", @pc") + ")", admin);
        if (publicCode is not null) cmd.Parameters.AddWithValue("pc", publicCode);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("s", slug);
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.AddWithValue("m", modules);
        cmd.Parameters.AddWithValue("l", locationId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("a", active);
        cmd.Parameters.AddWithValue("lat", (object?)lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lng", (object?)lng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", (object?)city ?? DBNull.Value);
        cmd.Parameters.AddWithValue("d", discoverable);
        await cmd.ExecuteNonQueryAsync();
        var code = (string)(await Scalar(admin, $"SELECT public_code FROM beauty_locations WHERE id = '{locationId}'"))!;
        return new Seeded(tenantId, slug, locationId, code);
    }

    private async Task<List<string[]>> AppQueryAsync(string sql, params (string, object?)[] args)
    {
        await using var conn = new NpgsqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await r.ReadAsync())
            rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
        return rows;
    }

    // ---------- public_code ----------

    [SkippableFact]
    public async Task Public_code_is_generated_valid_unique_and_immutable()
    {
        SkipIfNoDb();
        var a = await SeedAsync("A");
        var b = await SeedAsync("B");
        Assert.Matches(CodeFormat, a.Code);
        Assert.Matches(CodeFormat, b.Code);
        Assert.NotEqual(a.Code, b.Code);

        // unique across tenants / format check (inserts: UPDATE of public_code is blocked by the immutability trigger)
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SeedAsync("Dup", publicCode: a.Code));
        Assert.Equal("23505", ex.SqlState);
        ex = await Assert.ThrowsAsync<PostgresException>(() => SeedAsync("Bad", publicCode: "ABCDEFGI")); // I is not Crockford-valid
        Assert.Equal("23514", ex.SqlState);

        await using var admin = await AdminAsync();
        ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await Scalar(admin, $"UPDATE beauty_locations SET latitude = 10 WHERE id = '{b.LocationId}'")); // lat without lng
        Assert.Equal("23514", ex.SqlState);
    }

    [SkippableFact]
    public async Task Public_code_update_is_rejected_by_trigger_even_for_the_owner_but_noop_updates_pass()
    {
        SkipIfNoDb();
        var s = await SeedAsync("Immutable");
        await using var admin = await AdminAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await Scalar(admin, $"UPDATE beauty_locations SET public_code = '00000000' WHERE id = '{s.LocationId}'"));
        Assert.Equal("23001", ex.SqlState);
        Assert.Contains("immutable", ex.MessageText);
        // writing the same value (or other columns) is fine
        await Scalar(admin, $"UPDATE beauty_locations SET public_code = public_code, name = 'Renamed' WHERE id = '{s.LocationId}'");
        Assert.Equal(s.Code, (string)(await Scalar(admin, $"SELECT public_code FROM beauty_locations WHERE id = '{s.LocationId}'"))!);
    }

    [SkippableFact]
    public async Task Generator_alphabet_has_no_ambiguous_characters_and_is_not_constant()
    {
        SkipIfNoDb();
        await using var admin = await AdminAsync();
        var distinct = (long)(await Scalar(admin,
            "SELECT count(DISTINCT public.beauty_generate_public_code()) FROM generate_series(1, 200)"))!;
        Assert.True(distinct >= 199);
        var bad = (long)(await Scalar(admin,
            "SELECT count(*) FROM generate_series(1, 500) g WHERE public.beauty_generate_public_code() !~ '^[0-9A-HJKMNP-TV-Z]{8}$'"))!;
        Assert.Equal(0, bad);
    }

    [SkippableFact]
    public async Task Public_code_cannot_be_changed_through_the_ef_model()
    {
        SkipIfNoDb();
        var s = await SeedAsync("EF");
        await using var ctx = _db.CreateContext(s.TenantId);
        var loc = await ctx.Locations.SingleAsync(l => l.Id == s.LocationId);
        Assert.Equal(s.Code, loc.PublicCode);
        loc.PublicCode = "00000000";
        loc.Name = "Renamed";
        await ctx.SaveChangesAsync();
        await using var admin = await AdminAsync();
        Assert.Equal(s.Code, (string)(await Scalar(admin, $"SELECT public_code FROM beauty_locations WHERE id = '{s.LocationId}'"))!);
    }

    // ---------- isolation ----------

    [SkippableFact]
    public async Task App_role_sees_no_foreign_rows_directly_only_via_functions()
    {
        SkipIfNoDb();
        var s = await SeedAsync("Direct", 50.45, 30.52, "Kyiv", discoverable: true);

        await using var none = _db.CreateContext(null);
        Assert.Empty(await none.Locations.ToListAsync());
        Assert.Empty(await none.Tenants.ToListAsync());

        // another tenant context still sees nothing of this one
        await using var other = _db.CreateContext(Guid.NewGuid());
        Assert.Empty(await other.Locations.Where(l => l.Id == s.LocationId).ToListAsync());

        var viaFn = await AppQueryAsync("SELECT location_id::text FROM public.beauty_public_resolve_location(@c)", ("c", s.Code));
        Assert.Single(viaFn);
        Assert.Equal(s.LocationId.ToString(), viaFn[0][0]);
    }

    [SkippableFact]
    public async Task Functions_return_only_the_public_columns()
    {
        SkipIfNoDb();
        var s = await SeedAsync("Cols", 50.45, 30.52, "Kyiv", discoverable: true);
        await using var conn = new NpgsqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT * FROM public.beauty_public_resolve_location(@c)", conn);
        cmd.Parameters.AddWithValue("c", s.Code);
        await using var r = await cmd.ExecuteReaderAsync();
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        Assert.Equal(["public_code", "tenant_slug", "location_id", "name", "address", "city", "timezone"], cols);
        Assert.DoesNotContain("tenant_id", cols);

        await r.CloseAsync();
        await using var cmd2 = new NpgsqlCommand("SELECT * FROM public.beauty_public_nearby_locations(50.45, 30.52, 5, 5)", conn);
        await using var r2 = await cmd2.ExecuteReaderAsync();
        var cols2 = Enumerable.Range(0, r2.FieldCount).Select(r2.GetName).ToArray();
        Assert.Equal(["public_code", "tenant_slug", "location_id", "name", "address", "city", "timezone", "distance_km"], cols2);
    }

    // ---------- resolve ----------

    [SkippableFact]
    public async Task Resolve_normalizes_input_and_hides_ineligible_locations()
    {
        SkipIfNoDb();
        var ok = await SeedAsync("Ok");
        var inactive = await SeedAsync("Inactive", active: false);
        var suspended = await SeedAsync("Suspended", status: "suspended");
        var noModule = await SeedAsync("NoModule", modules: "{}");

        const string q = "SELECT tenant_slug FROM public.beauty_public_resolve_location(@c)";
        var spaced = ok.Code[..4].ToLowerInvariant() + "-" + ok.Code[4..].ToLowerInvariant();
        Assert.Equal(ok.Slug, (await AppQueryAsync(q, ("c", spaced)))[0][0]);
        Assert.Equal(ok.Slug, (await AppQueryAsync(q, ("c", " " + ok.Code + " ")))[0][0]);
        // Crockford aliases: O -> 0, I/L -> 1 (only when the real code has 0/1 in that place; construct via SQL)
        Assert.Empty(await AppQueryAsync(q, ("c", "ZZZZZZZZ")));
        Assert.Empty(await AppQueryAsync(q, ("c", "")));
        Assert.Empty(await AppQueryAsync(q, ("c", null)));
        Assert.Empty(await AppQueryAsync(q, ("c", inactive.Code)));
        Assert.Empty(await AppQueryAsync(q, ("c", suspended.Code)));
        Assert.Empty(await AppQueryAsync(q, ("c", noModule.Code)));
    }

    [SkippableFact]
    public async Task Resolve_accepts_crockford_aliases()
    {
        SkipIfNoDb();
        // fixed code: pick a code nobody else has (public_code is immutable and unique)
        var s = await SeedAsync("Alias", publicCode: "A0B1C0D1");
        var rows = await AppQueryAsync("SELECT location_id::text FROM public.beauty_public_resolve_location(@c)", ("c", "aob-lcod-1".ToUpperInvariant()));
        Assert.Single(rows);
        Assert.Equal(s.LocationId.ToString(), rows[0][0]);
    }

    // ---------- nearby ----------

    [SkippableFact]
    public async Task Nearby_returns_only_discoverable_active_in_radius_sorted_and_clamped()
    {
        SkipIfNoDb();
        // Unique region far from other tests' data: lat 10..11, lng 100..101 (1 deg lat ~ 111 km)
        var near = await SeedAsync("Near", 10.0100, 100.0100, "X", discoverable: true);
        var mid = await SeedAsync("Mid", 10.1000, 100.0100, "X", discoverable: true);   // ~10 km
        var far = await SeedAsync("Far", 10.5000, 100.0100, "X", discoverable: true);   // ~54 km (outside 30)
        var hidden = await SeedAsync("Hidden", 10.0100, 100.0100, "X", discoverable: false);
        var inactive = await SeedAsync("Inactive", 10.0100, 100.0100, "X", discoverable: true, active: false);
        var suspended = await SeedAsync("Susp", 10.0100, 100.0100, "X", discoverable: true, status: "suspended");
        var noCoords = await SeedAsync("NoCoords", null, null, "X", discoverable: true);

        var rows = await AppQueryAsync("SELECT location_id::text, distance_km::text FROM public.beauty_public_nearby_locations(10.0, 100.0, 1000, 100)");
        var ids = rows.Select(r => r[0]).ToList();
        Assert.Contains(near.LocationId.ToString(), ids);
        Assert.Contains(mid.LocationId.ToString(), ids);
        Assert.DoesNotContain(far.LocationId.ToString(), ids);       // radius clamped to 30 km
        foreach (var x in new[] { hidden, inactive, suspended, noCoords })
            Assert.DoesNotContain(x.LocationId.ToString(), ids);
        Assert.True(rows.Count <= 10);
        Assert.True(ids.IndexOf(near.LocationId.ToString()) < ids.IndexOf(mid.LocationId.ToString())); // by distance

        // limit clamp (1..10) and small radius
        Assert.Single(await AppQueryAsync("SELECT 1 FROM public.beauty_public_nearby_locations(10.0, 100.0, 1000, 0)"));
        var small = await AppQueryAsync("SELECT location_id::text FROM public.beauty_public_nearby_locations(10.0, 100.0, 5, 10)");
        Assert.DoesNotContain(mid.LocationId.ToString(), small.Select(r => r[0]));
    }

    [SkippableFact]
    public async Task Nearby_rounds_input_clamps_limit_and_rejects_bad_coordinates()
    {
        SkipIfNoDb();
        for (var i = 0; i < 12; i++) await SeedAsync($"Crowd{i}", -40.0 + i * 0.001, 170.0, "Crowd", discoverable: true);
        var all = await AppQueryAsync("SELECT 1 FROM public.beauty_public_nearby_locations(-40.0, 170.0, 30, 999)");
        Assert.Equal(10, all.Count);

        // 3-decimal rounding of the input: distance 0 for a point that is 0.0004 deg away from the stored one
        var exact = await SeedAsync("Round", -41.0, 171.0, "R", discoverable: true);
        var rows = await AppQueryAsync("SELECT distance_km::text FROM public.beauty_public_nearby_locations(-41.0004, 171.0004, 1, 1)");
        Assert.Single(rows);
        Assert.Equal("0.0", rows[0][0]);

        foreach (var (lat, lng) in new[] { (91.0, 0.0), (0.0, 181.0), (double.NaN, 0.0), (double.PositiveInfinity, 0.0) })
            Assert.Empty(await AppQueryAsync("SELECT 1 FROM public.beauty_public_nearby_locations(@a, @b, 30, 5)",
                ("a", lat), ("b", lng)));
        Assert.Empty(await AppQueryAsync("SELECT 1 FROM public.beauty_public_nearby_locations(NULL, NULL, 30, 5)"));
        _ = exact;
    }

    // ---------- by city ----------

    [SkippableFact]
    public async Task City_search_is_exact_case_insensitive_and_only_discoverable()
    {
        SkipIfNoDb();
        var city = "Lviv" + Guid.NewGuid().ToString("N")[..6];
        var ok = await SeedAsync("CityOk", city: city, discoverable: true);
        var hidden = await SeedAsync("CityHidden", city: city, discoverable: false);
        var inactive = await SeedAsync("CityInactive", city: city, discoverable: true, active: false);
        var noModule = await SeedAsync("CityNoModule", city: city, discoverable: true, modules: "{}");

        const string q = "SELECT location_id::text FROM public.beauty_public_search_locations_by_city(@c, 10)";
        var rows = await AppQueryAsync(q, ("c", "  " + city.ToUpperInvariant() + " "));
        Assert.Equal([ok.LocationId.ToString()], rows.Select(r => r[0]).ToArray());
        Assert.Empty(await AppQueryAsync(q, ("c", city[..^2])));      // no prefix matching
        Assert.Empty(await AppQueryAsync(q, ("c", "%")));
        Assert.Empty(await AppQueryAsync(q, ("c", "a")));             // too short
        Assert.Empty(await AppQueryAsync(q, ("c", null)));
        _ = (hidden, inactive, noModule);
    }


    // ---------- migration ----------

    [SkippableFact]
    public async Task Migration_down_up_backfills_unique_valid_codes_for_existing_rows()
    {
        SkipIfNoDb();
        const string previous = "20261010135605_beauty_location_working_hours";
        var opts = new DbContextOptionsBuilder<BeautyCrm.Infrastructure.Data.BeautyDbContext>();
        opts.UseBeautyNpgsql(_db.AdminConnectionString);
        await using var ctx = new BeautyCrm.Infrastructure.Data.BeautyDbContext(opts.Options, new BeautyCrm.Infrastructure.Data.Tenancy.TenantContext());
        var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync(previous); // Down

        try
        {
            await using var admin = await AdminAsync();
            Assert.Equal(0L, await Scalar(admin, "SELECT count(*) FROM pg_proc WHERE proname LIKE 'beauty\\_public\\_%' OR proname = 'beauty_generate_public_code'"));
            var t = Guid.NewGuid();
            await Scalar(admin, $"INSERT INTO tenants (id, name, slug, modules) VALUES ('{t}', 'T', 'mig-{t.ToString("N")[..20]}', '{{beauty_booking}}')");
            for (var i = 0; i < 50; i++)
                await Scalar(admin, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{t}', 'L{i}', 'UTC', true)");
        }
        finally
        {
            await migrator.MigrateAsync(); // Up (always restore the schema for the other tests of this class)
            // the test app role is not `beautycrm_app`, so the migration did not grant it: repeat the ops step
            var appRole = new NpgsqlConnectionStringBuilder(_db.AppConnectionString).Username;
            await using var g = await AdminAsync();
            await Scalar(g, $"GRANT EXECUTE ON FUNCTION public.beauty_public_resolve_location(text) TO {appRole}");
            await Scalar(g, $"GRANT EXECUTE ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) TO {appRole}");
            await Scalar(g, $"GRANT EXECUTE ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) TO {appRole}");
        }

        await using var a2 = await AdminAsync();
        Assert.Equal(0L, await Scalar(a2, "SELECT count(*) FROM beauty_locations WHERE public_code !~ '^[0-9A-HJKMNP-TV-Z]{8}$'"));
        Assert.Equal(await Scalar(a2, "SELECT count(*) FROM beauty_locations"), await Scalar(a2, "SELECT count(DISTINCT public_code) FROM beauty_locations"));
        Assert.Equal(false, await Scalar(a2, "SELECT bool_or(is_discoverable) FROM beauty_locations"));
        Assert.Equal(3L, await Scalar(a2, "SELECT count(*) FROM pg_proc WHERE proname LIKE 'beauty\\_public\\_%'"));
    }

    // ---------- roles and grants ----------

    [SkippableFact]
    public async Task Catalog_role_is_nologin_not_bypassrls_and_owns_the_functions()
    {
        SkipIfNoDb();
        await using var admin = await AdminAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT rolcanlogin, rolsuper, rolbypassrls, rolinherit FROM pg_roles WHERE rolname = 'beautycrm_catalog'", admin);
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.False(r.GetBoolean(0));
            Assert.False(r.GetBoolean(1));
            Assert.False(r.GetBoolean(2));
            Assert.False(r.GetBoolean(3));
        }

        await using var fns = new NpgsqlCommand(
            "SELECT p.proname, pg_get_userbyid(p.proowner), p.prosecdef, has_function_privilege('public', p.oid, 'EXECUTE'), " +
            "COALESCE(array_to_string(p.proconfig, ','), '') FROM pg_proc p WHERE p.proname LIKE 'beauty\\_public\\_%'", admin);
        var n = 0;
        await using (var r = await fns.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                n++;
                Assert.Equal("beautycrm_catalog", r.GetString(1));
                Assert.True(r.GetBoolean(2));
                Assert.False(r.GetBoolean(3));
                Assert.Contains("search_path=pg_catalog, pg_temp", r.GetString(4));
            }
        Assert.Equal(3, n);
    }

    [SkippableFact]
    public async Task App_role_cannot_assume_catalog_role_or_read_tables_through_it()
    {
        SkipIfNoDb();
        await using var conn = new NpgsqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();

        await using (var member = new NpgsqlCommand("SELECT pg_has_role(current_user, 'beautycrm_catalog', 'MEMBER')", conn))
            Assert.False((bool)(await member.ExecuteScalarAsync())!);

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var set = new NpgsqlCommand("SET ROLE beautycrm_catalog", conn);
            await set.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", ex.SqlState);

        await using var admin = await AdminAsync();
        // column-level: catalog cannot read non-whitelisted columns (phone, working_hours) nor other tables
        Assert.False((bool)(await Scalar(admin, "SELECT has_column_privilege('beautycrm_catalog', 'beauty_locations', 'phone', 'SELECT')"))!);
        Assert.False((bool)(await Scalar(admin, "SELECT has_column_privilege('beautycrm_catalog', 'beauty_locations', 'working_hours', 'SELECT')"))!);
        Assert.True((bool)(await Scalar(admin, "SELECT has_column_privilege('beautycrm_catalog', 'beauty_locations', 'public_code', 'SELECT')"))!);
        Assert.False((bool)(await Scalar(admin, "SELECT has_column_privilege('beautycrm_catalog', 'tenants', 'name', 'SELECT')"))!);
        Assert.False((bool)(await Scalar(admin, "SELECT has_table_privilege('beautycrm_catalog', 'beauty_clients', 'SELECT')"))!);
        Assert.False((bool)(await Scalar(admin, "SELECT has_table_privilege('beautycrm_catalog', 'beauty_appointments', 'SELECT')"))!);
    }

    [SkippableFact]
    public async Task Catalog_policies_are_scoped_to_the_catalog_role_only()
    {
        SkipIfNoDb();
        await using var admin = await AdminAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename, roles::text FROM pg_policies WHERE policyname = 'public_catalog_read' ORDER BY tablename", admin);
        var rows = new List<(string, string)>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetString(1)));
        Assert.Equal([("beauty_locations", "{beautycrm_catalog}"), ("tenants", "{beautycrm_catalog}")], rows.ToArray());
    }
}
