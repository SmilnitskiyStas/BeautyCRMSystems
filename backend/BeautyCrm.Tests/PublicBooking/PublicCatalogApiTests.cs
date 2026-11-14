using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.PublicBooking;

/// <summary>
/// ADR-022: публічний каталог (resolve / nearby / search) і адмін-API закладу (public_code, координати) через повний HTTP-конвеєр
/// на реальному PostgreSQL (роль без BYPASSRLS, definer-функції). Без БД пропускаються; BEAUTY_TEST_REQUIRE_DB=1 -> помилка.
/// </summary>
public sealed class PublicCatalogApiTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private static readonly Regex CodeFormat = new("^[0-9A-HJKMNP-TV-Z]{8}$");
    private readonly AuthApiFixture _fx;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _client = null!;

    public PublicCatalogApiTests(AuthApiFixture fx)
    {
        _fx = fx;
        if (fx.SkipReason is null) _client = NewClient();
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private HttpClient NewClient(Action<IWebHostBuilder>? configure = null, int permit = 10_000)
    {
        var f = _fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
        {
            foreach (var k in new[] { "Read", "Token", "Write", "Nearby", "Search" })
                b.UseSetting($"PublicBooking:RateLimit:{k}Permit", permit.ToString());
            configure?.Invoke(b);
        });
        _factories.Add(f);
        return f.CreateClient();
    }

    private static string Inv(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Seeded(Guid TenantId, string Slug, Guid LocationId, string Code);

    /// <summary>Tenant + заклад напряму під admin-роллю (public_code видає DB default).</summary>
    private async Task<Seeded> SeedAsync(string name = "Salon", double? lat = null, double? lng = null, string? city = null,
        bool discoverable = false, bool active = true, string status = "active", string modules = "{beauty_booking}", Guid? tenantId = null,
        string? slug = null)
    {
        var tid = tenantId ?? Guid.NewGuid();
        var lid = Guid.NewGuid();
        slug ??= "cat-" + tid.ToString("N")[..20];
        await using var admin = new NpgsqlConnection(_fx.Db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            (tenantId is null ? "INSERT INTO tenants (id, name, slug, status, modules) VALUES (@t, 'T', @s, @st, @m::text[]); " : "") +
            "INSERT INTO beauty_locations (id, tenant_id, name, address, phone, timezone, is_active, latitude, longitude, city, is_discoverable) " +
            "VALUES (@l, @t, @n, 'Addr 1', '+380441112233', 'Europe/Kyiv', @a, @lat, @lng, @c, @d) RETURNING public_code", admin);
        cmd.Parameters.AddWithValue("t", tid);
        cmd.Parameters.AddWithValue("s", slug);
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.AddWithValue("m", modules);
        cmd.Parameters.AddWithValue("l", lid);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("a", active);
        cmd.Parameters.AddWithValue("lat", (object?)lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lng", (object?)lng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", (object?)city ?? DBNull.Value);
        cmd.Parameters.AddWithValue("d", discoverable);
        var code = (string)(await cmd.ExecuteScalarAsync())!;
        return new Seeded(tid, slug, lid, code);
    }

    private async Task SetCodeAsync(Guid locationId, string code)
    {
        await using var admin = new NpgsqlConnection(_fx.Db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            // public_code is immutable (trigger); the test fixes a known code, so the trigger is disabled for this one statement batch
            "ALTER TABLE beauty_locations DISABLE TRIGGER trg_beauty_locations_public_code_immutable; " +
            "UPDATE beauty_locations SET public_code = @c WHERE id = @l; " +
            "ALTER TABLE beauty_locations ENABLE TRIGGER trg_beauty_locations_public_code_immutable;", admin);
        cmd.Parameters.AddWithValue("c", code);
        cmd.Parameters.AddWithValue("l", locationId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<HttpResponseMessage> Nearby(HttpClient c, object body) => c.PostAsJsonAsync("/api/public/locations/nearby", body, Json);

    private static Task<HttpResponseMessage> Search(HttpClient c, object body) => c.PostAsJsonAsync("/api/public/locations/search", body, Json);

    private static async Task<JsonElement> Body(HttpResponseMessage r) => await Read<JsonElement>(r);

    private static async Task<string> RawBody(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    private static readonly string[] RefKeys = ["address", "city", "locationId", "name", "publicCode", "tenantSlug", "timezone"];

    private static string[] Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ---------- resolve ----------

    [SkippableFact]
    public async Task resolve_returns_public_fields_only_and_normalizes_the_code()
    {
        NeedDb();
        var s = await SeedAsync("Resolve Me", city: "Lviv");
        await SetCodeAsync(s.LocationId, "AB01CD1E");

        var r = await _client.GetAsync("/api/public/resolve/AB01CD1E");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var j = await Body(r);
        Assert.Equal(RefKeys, Keys(j)); // жодних tenantId/phone/is_active/координат
        Assert.Equal("AB01CD1E", j.GetProperty("publicCode").GetString());
        Assert.Equal(s.Slug, j.GetProperty("tenantSlug").GetString());
        Assert.Equal(s.LocationId, j.GetProperty("locationId").GetGuid());
        Assert.Equal("Resolve Me", j.GetProperty("name").GetString());
        Assert.Equal("Lviv", j.GetProperty("city").GetString());
        Assert.Equal("Europe/Kyiv", j.GetProperty("timezone").GetString());

        // нормалізація: регістр, дефіс, пробіл (url-encoded), аліаси O->0, I/L->1
        foreach (var variant in new[] { "ab01cd1e", "AB01-CD1E", "ab01-cd1e", "ABO1-CDLE", "abOI-cdIe", "ABOl-CDlE" })
        {
            var hit = await _client.GetAsync("/api/public/resolve/" + variant);
            Assert.True(hit.StatusCode == HttpStatusCode.OK, $"variant {variant} -> {hit.StatusCode}");
            Assert.Equal(s.LocationId, (await Body(hit)).GetProperty("locationId").GetGuid());
        }
    }

    [SkippableFact]
    public async Task resolve_failures_are_one_indistinguishable_404()
    {
        NeedDb();
        var inactive = await SeedAsync(active: false);
        var suspended = await SeedAsync(status: "suspended");
        var noModule = await SeedAsync(modules: "{}");
        var ok = await SeedAsync();

        var codes = new[]
        {
            inactive.Code, suspended.Code, noModule.Code, "ZZZZZZZZ", "ZZZZ-ZZZZ", // неактивний / призупинений / без модуля / невідомий
            "short", "ABCDEFGHI", "UUUUUUUU", "!!!!!!!!", "ABCD-EFG%20", "AB-CD-EF-GH", new string('A', 200),
        };
        var bodies = new List<string>();
        foreach (var code in codes)
        {
            var r = await _client.GetAsync("/api/public/resolve/" + code);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            bodies.Add(await RawBody(r));
        }
        Assert.Single(bodies.Distinct());
        var err = JsonDocument.Parse(bodies[0]).RootElement;
        Assert.Equal("not_found", err.GetProperty("code").GetString());

        // ... і це те саме тіло, що й у решті публічного API для невідомого slug
        var unknownSlug = await _client.GetAsync("/api/public/no-such-slug-xyz/locations");
        Assert.Equal(bodies[0], await RawBody(unknownSlug));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/public/resolve/" + ok.Code)).StatusCode);
    }

    [SkippableFact]
    public async Task resolve_does_not_depend_on_discoverable_and_pins_tenant_and_location_per_code()
    {
        NeedDb();
        var a = await SeedAsync("A-loc", discoverable: false);
        var b = await SeedAsync("B-loc", discoverable: true);
        var ja = await Body(await _client.GetAsync("/api/public/resolve/" + a.Code));
        var jb = await Body(await _client.GetAsync("/api/public/resolve/" + b.Code));
        Assert.Equal((a.Slug, a.LocationId), (ja.GetProperty("tenantSlug").GetString(), ja.GetProperty("locationId").GetGuid()));
        Assert.Equal((b.Slug, b.LocationId), (jb.GetProperty("tenantSlug").GetString(), jb.GetProperty("locationId").GetGuid()));
        // ізоляція: slug одного tenant-а не відкриває заклад іншого у tenant-scoped API
        var list = await Body(await _client.GetAsync($"/api/public/{a.Slug}/locations"));
        Assert.Equal([a.LocationId], list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToArray());
    }

    // ---------- nearby ----------

    [SkippableFact]
    public async Task nearby_returns_only_discoverable_active_in_radius_sorted_without_leaking_fields()
    {
        NeedDb();
        const double lat = 10.0, lng = 100.0; // «острів» координат, не перетинається з іншими тестами
        var near = await SeedAsync("Near", lat + 0.01, lng, "Nearville", discoverable: true);       // ~1.1 km
        var far = await SeedAsync("Far", lat + 0.1, lng, "Nearville", discoverable: true);          // ~11 km
        await SeedAsync("Hidden", lat + 0.02, lng, discoverable: false);
        await SeedAsync("Inactive", lat + 0.02, lng, discoverable: true, active: false);
        await SeedAsync("Suspended", lat + 0.02, lng, discoverable: true, status: "suspended");
        await SeedAsync("NoModule", lat + 0.02, lng, discoverable: true, modules: "{}");
        await SeedAsync("TooFar", lat + 0.5, lng, discoverable: true);                             // ~55 km, поза 30

        var r = await Nearby(_client, new { lat, lng });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var j = await Body(r);
        Assert.Equal(["items"], Keys(j));
        var items = j.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([near.LocationId, far.LocationId], items.Select(e => e.GetProperty("locationId").GetGuid()).ToArray());
        var withDistance = RefKeys.Append("distanceKm").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.All(items, e => Assert.Equal(withDistance, Keys(e)));
        Assert.InRange(items[0].GetProperty("distanceKm").GetDecimal(), 1.0m, 1.3m);
        Assert.InRange(items[1].GetProperty("distanceKm").GetDecimal(), 10.5m, 11.5m);
        Assert.Equal(near.Slug, items[0].GetProperty("tenantSlug").GetString());
        Assert.Equal(far.Slug, items[1].GetProperty("tenantSlug").GetString());
        Assert.Matches(CodeFormat, items[0].GetProperty("publicCode").GetString()!);
        var raw = j.GetRawText();
        Assert.DoesNotContain("tenantId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+38044", raw);
        Assert.DoesNotContain("phone", raw, StringComparison.OrdinalIgnoreCase);

        // radiusKm звужує, а понад 30 км обрізається до 30
        var narrow = await Body(await Nearby(_client, new { lat, lng, radiusKm = 5 }));
        Assert.Single(narrow.GetProperty("items").EnumerateArray());
        var wide = await Body(await Nearby(_client, new { lat, lng, radiusKm = 5000 }));
        Assert.Equal(2, wide.GetProperty("items").GetArrayLength());

        // порожня відповідь — валідна
        var empty = await Nearby(_client, new { lat = -45.0, lng = -120.0 });
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(0, (await Body(empty)).GetProperty("items").GetArrayLength());
    }

    [SkippableFact]
    public async Task nearby_caps_results_at_10_across_tenants()
    {
        NeedDb();
        const double lat = 20.0, lng = 110.0;
        for (var i = 0; i < 12; i++) await SeedAsync($"Cap{i}", lat + i * 0.001, lng, discoverable: true);
        var items = (await Body(await Nearby(_client, new { lat, lng }))).GetProperty("items");
        Assert.Equal(10, items.GetArrayLength());
        Assert.Equal(10, items.EnumerateArray().Select(e => e.GetProperty("tenantSlug").GetString()).Distinct().Count()); // різні tenant-и
    }

    [SkippableFact]
    public async Task nearby_validates_input_with_422()
    {
        NeedDb();
        async Task Bad(HttpResponseMessage r, string code)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal(code, (await Body(r)).GetProperty("code").GetString());
        }
        await Bad(await Nearby(_client, new { lat = 90.5, lng = 10 }), "invalid_coordinates");
        await Bad(await Nearby(_client, new { lat = -91, lng = 10 }), "invalid_coordinates");
        await Bad(await Nearby(_client, new { lat = 10, lng = 180.01 }), "invalid_coordinates");
        await Bad(await Nearby(_client, new { lat = 10 }), "invalid_coordinates");
        await Bad(await Nearby(_client, new { }), "invalid_coordinates");
        await Bad(await Nearby(_client, new { lat = 10, lng = 10, radiusKm = 0 }), "invalid_radius");
        await Bad(await Nearby(_client, new { lat = 10, lng = 10, radiusKm = -3 }), "invalid_radius");
        var wrongType = await _client.PostAsync("/api/public/locations/nearby",
            new StringContent("""{"lat":"abc","lng":1}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongType.StatusCode);
        var noBody = await _client.PostAsync("/api/public/locations/nearby", new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noBody.StatusCode);
        // межові значення валідні
        Assert.Equal(HttpStatusCode.OK, (await Nearby(_client, new { lat = 90, lng = 180 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Nearby(_client, new { lat = -90, lng = -180 })).StatusCode);
    }

    // ---------- search by city ----------

    [SkippableFact]
    public async Task search_by_city_exact_case_insensitive_discoverable_only_without_distance()
    {
        NeedDb();
        var city = "Testopolis" + Guid.NewGuid().ToString("N")[..6];
        var a = await SeedAsync("Cs-A", city: city, discoverable: true);
        await SeedAsync("Cs-hidden", city: city, discoverable: false);
        await SeedAsync("Cs-inactive", city: city, discoverable: true, active: false);
        await SeedAsync("Cs-susp", city: city, discoverable: true, status: "suspended");
        await SeedAsync("Cs-other", city: "Elsewhere", discoverable: true);

        var r = await Search(_client, new { city = "  " + city.ToUpperInvariant() + " " });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Body(r);
        Assert.Equal(["items"], Keys(j));
        var items = j.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(RefKeys, Keys(items[0])); // без distanceKm, tenantId, phone
        Assert.Equal(a.LocationId, items[0].GetProperty("locationId").GetGuid());

        // префікс/шаблон не працюють як скрейпінг
        Assert.Equal(0, (await Body(await Search(_client, new { city = city[..6] }))).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await Body(await Search(_client, new { city = "%%" }))).GetProperty("items").GetArrayLength());
    }

    [SkippableFact]
    public async Task search_validates_city_with_422()
    {
        NeedDb();
        foreach (var city in new object?[] { "", "a", "  ", new string('x', 101), "abcd", null })
        {
            var r = await Search(_client, new { city });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("invalid_city", (await Body(r)).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Search(_client, new { })).StatusCode);
        // L5: GET-варіанта (місто в URL/логах) більше немає
        Assert.NotEqual(HttpStatusCode.OK, (await _client.GetAsync("/api/public/locations/search?city=Lviv")).StatusCode);
    }

    // ---------- маршрути й зарезервовані slug-и ----------

    [SkippableFact]
    public async Task reserved_slugs_cannot_be_created_and_do_not_shadow_catalog_routes()
    {
        NeedDb();
        foreach (var slug in new[] { "resolve", "locations", "RESOLVE", "integrity", "Integrity" })
        {
            var r = await _fx.Send(HttpMethod.Post, "/api/platform/tenants", new
            {
                name = "X", slug, owner = new { email = $"o@{Guid.NewGuid():N}.test", fullName = "O", password = Password },
            }, platformKey: PlatformKey);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("slug_reserved", (await Body(r)).GetProperty("code").GetString());
        }
        // L6: у БД немає tenant-ів із зарезервованим slug (seed/міграції їх не створюють)
        await using (var admin = new NpgsqlConnection(_fx.Db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM tenants WHERE lower(slug) = ANY(@s)", admin);
            cmd.Parameters.AddWithValue("s", BeautyCrm.Application.Features.BeautyPublicBooking.PublicTenantResolver.ReservedSlugs.ToArray());
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/public/resolve/locations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Search(_client, new { city = "nowhere" })).StatusCode);
    }

    // ---------- rate limits ----------

    [SkippableFact]
    public async Task catalog_endpoints_have_their_own_rate_limit_policies()
    {
        NeedDb();
        var c = NewClient(b =>
        {
            b.UseSetting("PublicBooking:RateLimit:TokenPermit", "2");
            b.UseSetting("PublicBooking:RateLimit:NearbyPermit", "3");
            b.UseSetting("PublicBooking:RateLimit:SearchPermit", "1");
        });
        // resolve = anti-guessing (public-token)
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/public/resolve/ZZZZZZZZ")).StatusCode);
        var limited = await c.GetAsync("/api/public/resolve/ZZZZZZZZ");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        // nearby має власний лічильник (не з'їдений resolve)
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await Nearby(c, new { lat = 1.0, lng = 1.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Nearby(c, new { lat = 1.0, lng = 1.0 })).StatusCode);
        // search — окремо від nearby
        Assert.Equal(HttpStatusCode.OK, (await Search(c, new { city = "none" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Search(c, new { city = "none" })).StatusCode);
    }

    // ---------- координати не потрапляють у логи ----------

    private sealed class CaptureProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Enqueue(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> kv) foreach (var p in kv) Lines.Enqueue($"{p.Key}={p.Value}");
            if (exception is not null) Lines.Enqueue(exception.ToString());
        }
    }

    [SkippableFact]
    public async Task nearby_coordinates_never_appear_in_logs_or_responses()
    {
        NeedDb();
        var log = new CaptureProvider();
        var c = NewClient(b => b.ConfigureLogging(l => { l.ClearProviders(); l.SetMinimumLevel(LogLevel.Trace); l.AddProvider(log); }));
        // валідний запит, запит поза діапазоном, зіпсований JSON — усі з упізнаваним числом у тілі
        await Nearby(c, new { lat = 48.123456, lng = 31.654321 });
        await Nearby(c, new { lat = 91.987654, lng = 31.654321 });
        await c.PostAsync("/api/public/locations/nearby", new StringContent("""{"lat":48.123456,"lng":"31.654321x"}""", System.Text.Encoding.UTF8, "application/json"));
        var all = string.Join('\n', log.Lines);
        Assert.NotEmpty(log.Lines); // перехоплювач працює (запити логуються самим хостом)
        foreach (var needle in new[] { "48.123", "31.654", "91.987", "48,123", "31,654", "Secretopolis" })
            Assert.DoesNotContain(needle, all);
    }

    // ---------- адмін-API закладу ----------

    private async Task<(TenantSeed T, string Token)> OwnerAsync()
    {
        var t = await _fx.CreateTenantAsync();
        return (t, (await _fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken);
    }

    [SkippableFact]
    public async Task admin_api_accepts_geo_fields_returns_immutable_public_code_and_keeps_fields_when_omitted()
    {
        NeedDb();
        var (t, token) = await OwnerAsync();
        var created = await _fx.Send(HttpMethod.Post, "/api/beauty/locations", new
        {
            name = "Geo", address = "Khreshchatyk 1", timezone = "Europe/Kyiv",
            latitude = 50.4501, longitude = 30.5234, city = "  Kyiv ", isDiscoverable = true,
            publicCode = "HACKHACK", // ігнорується
        }, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var j = await Body(created);
        var code = j.GetProperty("publicCode").GetString()!;
        Assert.Matches(CodeFormat, code);
        Assert.NotEqual("HACKHACK", code);
        Assert.Equal(50.4501, j.GetProperty("latitude").GetDouble());
        Assert.Equal(30.5234, j.GetProperty("longitude").GetDouble());
        Assert.Equal("Kyiv", j.GetProperty("city").GetString());
        Assert.True(j.GetProperty("isDiscoverable").GetBoolean());
        var id = j.GetProperty("id").GetGuid();

        // PUT без гео-полів (стара форма): нічого не затирає; publicCode у тілі ігнорується
        var put = await _fx.Send(HttpMethod.Put, $"/api/beauty/locations/{id}",
            new { name = "Geo 2", address = "Khreshchatyk 1", timezone = "Europe/Kyiv", publicCode = "HACKHACK" }, token);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var p = await Body(put);
        Assert.Equal(code, p.GetProperty("publicCode").GetString());
        Assert.Equal(50.4501, p.GetProperty("latitude").GetDouble());
        Assert.Equal("Kyiv", p.GetProperty("city").GetString());
        Assert.True(p.GetProperty("isDiscoverable").GetBoolean());

        // оновлення значень, вимкнення opt-in, очищення city
        var upd = await Body(await _fx.Send(HttpMethod.Put, $"/api/beauty/locations/{id}", new
        {
            name = "Geo 2", timezone = "Europe/Kyiv", latitude = 49.84, longitude = 24.03, city = "", isDiscoverable = false,
        }, token));
        Assert.Equal(49.84, upd.GetProperty("latitude").GetDouble());
        Assert.Equal(JsonValueKind.Null, upd.GetProperty("city").ValueKind);
        Assert.False(upd.GetProperty("isDiscoverable").GetBoolean());
        Assert.Equal(code, upd.GetProperty("publicCode").GetString());

        // очищення координат
        var cleared = await Body(await _fx.Send(HttpMethod.Put, $"/api/beauty/locations/{id}",
            new { name = "Geo 2", timezone = "Europe/Kyiv", clearCoordinates = true }, token));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("latitude").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("longitude").ValueKind);

        // список віддає publicCode
        var list = await Body(await _fx.Send(HttpMethod.Get, "/api/beauty/locations", bearer: token));
        Assert.Contains(list.EnumerateArray(), e => e.GetProperty("publicCode").GetString() == code);
        _ = t;
    }

    [SkippableFact]
    public async Task admin_api_validates_geo_fields_and_new_location_is_resolvable_via_public_code()
    {
        NeedDb();
        var (t, token) = await OwnerAsync();
        async Task Bad(object body, string code)
        {
            var r = await _fx.Send(HttpMethod.Post, "/api/beauty/locations", body, token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal(code, (await Body(r)).GetProperty("code").GetString());
        }
        await Bad(new { name = "X1", timezone = "Europe/Kyiv", latitude = 91, longitude = 10 }, "invalid_coordinates");
        await Bad(new { name = "X2", timezone = "Europe/Kyiv", latitude = 10, longitude = 181 }, "invalid_coordinates");
        await Bad(new { name = "X3", timezone = "Europe/Kyiv", latitude = 10 }, "invalid_coordinates");
        await Bad(new { name = "X4", timezone = "Europe/Kyiv", longitude = 10 }, "invalid_coordinates");
        await Bad(new { name = "X5", timezone = "Europe/Kyiv", city = new string('c', 101) }, "invalid_city");

        var ok = await _fx.Send(HttpMethod.Post, "/api/beauty/locations", new { name = "Plain", timezone = "Europe/Kyiv" }, token);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var j = await Body(ok);
        Assert.False(j.GetProperty("isDiscoverable").GetBoolean()); // opt-in: за замовчуванням не в каталозі
        var code = j.GetProperty("publicCode").GetString()!;
        var resolved = await Body(await _client.GetAsync("/api/public/resolve/" + code));
        Assert.Equal(t.Slug, resolved.GetProperty("tenantSlug").GetString());
        Assert.Equal(j.GetProperty("id").GetGuid(), resolved.GetProperty("locationId").GetGuid());
    }

    [SkippableFact]
    public async Task admin_geo_fields_are_tenant_isolated_and_specialist_cannot_write()
    {
        NeedDb();
        var (ta, tokenA) = await OwnerAsync();
        var (tb, tokenB) = await OwnerAsync();
        var a = await Body(await _fx.Send(HttpMethod.Post, "/api/beauty/locations",
            new { name = "A", timezone = "Europe/Kyiv", latitude = 30.0, longitude = 30.0, isDiscoverable = true }, tokenA));
        var idA = a.GetProperty("id").GetGuid();
        // tenant B не бачить і не змінює заклад tenant-а A
        var put = await _fx.Send(HttpMethod.Put, $"/api/beauty/locations/{idA}",
            new { name = "Hijack", timezone = "Europe/Kyiv", latitude = 1.0, longitude = 1.0, isDiscoverable = false }, tokenB);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        var listB = await Body(await _fx.Send(HttpMethod.Get, "/api/beauty/locations", bearer: tokenB));
        Assert.DoesNotContain(listB.EnumerateArray(), e => e.GetProperty("id").GetGuid() == idA);
        var listA = await Body(await _fx.Send(HttpMethod.Get, "/api/beauty/locations", bearer: tokenA));
        var stillA = listA.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == idA);
        Assert.Equal(30.0, stillA.GetProperty("latitude").GetDouble());
        Assert.True(stillA.GetProperty("isDiscoverable").GetBoolean());
        _ = (ta, tb);
    }

    // ---------- openapi ----------

    [SkippableFact]
    public async Task openapi_document_lists_catalog_routes_and_can_be_exported()
    {
        NeedDb();
        var f = _fx.CreateFactory(permitLimit: 1000).WithWebHostBuilder(b => b.UseEnvironment("Development"));
        _factories.Add(f);
        var r = await f.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/public/resolve/{publicCode}", out _));
        Assert.True(paths.TryGetProperty("/api/public/locations/nearby", out _));
        Assert.True(paths.TryGetProperty("/api/public/locations/search", out _));
        // експорт: BEAUTY_EXPORT_OPENAPI=<шлях до openapi.json> dotnet test --filter openapi_document
        if (Environment.GetEnvironmentVariable("BEAUTY_EXPORT_OPENAPI") is { Length: > 0 } target)
            await File.WriteAllTextAsync(target, JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
