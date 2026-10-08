using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Security;

/// <summary>Підміняє IP з'єднання (TestServer його не має); значення береться із заголовка X-Test-Remote.</summary>
internal sealed class TestRemoteIpFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, nxt) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ctx.Request.Headers[Header].FirstOrDefault() ?? "203.0.113.10");
            return nxt();
        });
        next(app);
    };
}

internal sealed class TestEnv(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "test";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>
/// TASK-689 (аудит TASK-682): H1 (роль БД), H2 (mock-адаптери), H3 (довірені проксі, rate limit, CORS), M3 (lockout без розкриття акаунта),
/// M4 (Multiplexing), L8 (діапазон), security headers. Реальний PostgreSQL з роллю NOSUPERUSER NOBYPASSRLS; без БД пропускаються.
/// </summary>
public sealed class HardeningTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private WebApplicationFactory<Program> Host(Action<IWebHostBuilder> configure)
    {
        var f = fx.Factory!.WithWebHostBuilder(configure);
        _factories.Add(f);
        return f;
    }

    private static IConfiguration Cfg(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private Task StartGuard(string connectionString, string env = "Production", bool allowPrivileged = false) =>
        new DbRoleGuard(
            Cfg(("ConnectionStrings:Default", connectionString), (DbRoleGuard.AllowPrivilegedKey, allowPrivileged ? "true" : "false")),
            new TestEnv(env), NullLogger<DbRoleGuard>.Instance).StartAsync(default);

    // ---------- H1: роль БД ----------

    [SkippableFact]
    public async Task dbRoleGuard_accepts_unprivileged_runtime_role()
    {
        NeedDb();
        await StartGuard(fx.Db.AppConnectionString);
    }

    [SkippableFact]
    public async Task dbRoleGuard_rejects_superuser_in_production_even_with_allow_flag()
    {
        NeedDb();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(fx.Db.AdminConnectionString));
        Assert.Contains("SUPERUSER", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(fx.Db.AdminConnectionString, "Production", allowPrivileged: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(fx.Db.AdminConnectionString, "Staging", allowPrivileged: true));
    }

    [SkippableFact]
    public async Task dbRoleGuard_allows_superuser_only_in_development_with_explicit_flag()
    {
        NeedDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(fx.Db.AdminConnectionString, "Development", allowPrivileged: false));
        await StartGuard(fx.Db.AdminConnectionString, "Development", allowPrivileged: true);
    }

    [SkippableFact]
    public async Task dbRoleGuard_rejects_bypassrls_role_that_is_not_superuser()
    {
        NeedDb();
        var role = "beauty_bypass_" + Guid.NewGuid().ToString("N")[..10];
        const string password = "Pw-bypass-123";
        await using (var admin = new NpgsqlConnection(fx.Db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER BYPASSRLS", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(fx.Db.AdminConnectionString) { Username = role, Password = password }.ConnectionString;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(cs));
            Assert.Contains("BYPASSRLS", ex.Message);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fx.Db.AdminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP ROLE IF EXISTS {role}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task dbRoleGuard_rejects_npgsql_multiplexing_because_tenant_context_is_per_connection()
    {
        NeedDb();
        var cs = new NpgsqlConnectionStringBuilder(fx.Db.AppConnectionString) { Multiplexing = true }.ConnectionString;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(cs));
        Assert.Contains("Multiplexing", ex.Message);
    }

    [Fact]
    public async Task dbRoleGuard_skips_check_when_connection_string_is_absent()
    {
        await new DbRoleGuard(Cfg(), new TestEnv("Production"), NullLogger<DbRoleGuard>.Instance).StartAsync(default);
    }

    [SkippableFact]
    public void host_does_not_start_with_privileged_database_role()
    {
        NeedDb();
        var f = Host(b => b.UseSetting("ConnectionStrings:Default", fx.Db.AdminConnectionString));
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    [SkippableFact]
    public void host_starts_in_development_with_privileged_role_only_when_explicitly_allowed()
    {
        NeedDb();
        var denied = Host(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Default", fx.Db.AdminConnectionString);
        });
        Assert.ThrowsAny<Exception>(() => denied.CreateClient());

        var allowed = Host(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Default", fx.Db.AdminConnectionString);
            b.UseSetting("Beauty:AllowPrivilegedDbRole", "true");
        });
        using var client = allowed.CreateClient();
    }

    // ---------- H2: mock-адаптери лише в Development ----------

    [SkippableFact]
    public void real_channel_adapters_are_default_and_mocks_are_not_registered_in_production()
    {
        NeedDb();
        var registry = fx.Factory!.Services.GetRequiredService<ChannelRegistry>();
        Assert.IsType<TelegramAdapter>(registry.Find("telegram"));
        Assert.IsType<InstagramAdapter>(registry.Find("instagram"));
    }

    [SkippableFact]
    public void useMocks_true_outside_development_fails_startup()
    {
        NeedDb();
        var f = Host(b => b.UseSetting("Channels:UseMocks", "true"));
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    [SkippableFact]
    public void mock_adapters_are_registered_in_development_only_when_explicitly_enabled()
    {
        NeedDb();
        var devDefault = Host(b => b.UseEnvironment("Development"));
        Assert.IsType<TelegramAdapter>(devDefault.Services.GetRequiredService<ChannelRegistry>().Find("telegram"));

        var devMocks = Host(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Channels:UseMocks", "true");
        });
        Assert.IsType<MockTelegramAdapter>(devMocks.Services.GetRequiredService<ChannelRegistry>().Find("telegram"));
        Assert.IsType<MockInstagramAdapter>(devMocks.Services.GetRequiredService<ChannelRegistry>().Find("instagram"));
    }

    // ---------- H3: довірені проксі + rate limit ----------

    private static StringContent LoginBody(string email = "nobody@x.test") =>
        new(JsonSerializer.Serialize(new { tenant = "no-such-tenant", email, password = "Pw-123456789012" }), System.Text.Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> Login(HttpClient c, string remote, string? xff = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = LoginBody(Guid.NewGuid().ToString("N")[..8] + "@x.test") };
        req.Headers.Add(TestRemoteIpFilter.Header, remote);
        if (xff is not null) req.Headers.Add("X-Forwarded-For", xff);
        return c.SendAsync(req);
    }

    private HttpClient RateLimited(string? trustedProxies, int permit = 2, Action<IWebHostBuilder>? more = null) =>
        Host(b =>
        {
            b.UseSetting("Auth:RateLimit:PermitLimit", permit.ToString());
            if (trustedProxies is not null) b.UseSetting("Beauty:TrustedProxies", trustedProxies);
            b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, TestRemoteIpFilter>());
            more?.Invoke(b);
        }).CreateClient();

    [SkippableFact]
    public async Task rateLimit_uses_client_ip_from_trusted_proxy_chain()
    {
        NeedDb();
        var c = RateLimited("10.0.0.0/8");
        // два клієнти за одним довіреним проксі 10.1.2.3 мають окремі кошики
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "10.1.2.3", "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "10.1.2.3", "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(c, "10.1.2.3", "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "10.1.2.3", "198.51.100.2")).StatusCode);
        // зліва підроблений адрес не допомагає: береться перший недовірений справа (198.51.100.1 уже вичерпав ліміт)
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(c, "10.1.2.3", "9.9.9.9, 198.51.100.1")).StatusCode);
        // ланцюг із кількох довірених проксі
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(c, "10.1.2.3", "198.51.100.1, 10.5.5.5")).StatusCode);
    }

    [SkippableFact]
    public async Task rateLimit_ignores_spoofed_forwarded_for_when_no_proxies_are_trusted()
    {
        NeedDb();
        var c = RateLimited(trustedProxies: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "203.0.113.50", "1.1.1.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "203.0.113.50", "2.2.2.2")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(c, "203.0.113.50", "3.3.3.3")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(c, "203.0.113.50")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "203.0.113.51")).StatusCode); // інший справжній IP
    }

    [SkippableFact]
    public async Task rateLimit_ignores_forwarded_for_from_untrusted_peer_even_if_proxies_are_configured()
    {
        NeedDb();
        var c = RateLimited("10.0.0.0/8");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "192.0.2.50", "1.1.1.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "192.0.2.50", "2.2.2.2")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(c, "192.0.2.50", "3.3.3.3")).StatusCode);
    }

    [SkippableFact]
    public async Task trustedProxies_accepts_single_ip_entries_and_ipv6_and_rejects_garbage()
    {
        NeedDb();
        var single = RateLimited("10.1.2.3, 2001:db8::/32");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(single, "10.1.2.3", "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(single, "10.1.2.3", "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(single, "10.1.2.3", "198.51.100.1")).StatusCode);
        Assert.ThrowsAny<Exception>(() => RateLimited("not-an-ip"));
        Assert.ThrowsAny<Exception>(() => RateLimited("10.0.0.0/99"));
    }

    private static string RefreshToken(Guid? tenant = null) =>
        $"{(tenant ?? Guid.NewGuid()):N}.{Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')}0123";

    private static Task<HttpResponseMessage> Refresh(HttpClient c, string token, string remote = "203.0.113.10")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh") { Content = JsonContent.Create(new { refreshToken = token }) };
        req.Headers.Add(TestRemoteIpFilter.Header, remote);
        return c.SendAsync(req);
    }

    [SkippableFact]
    public async Task refresh_has_wider_ip_limit_than_login_and_does_not_consume_the_login_bucket()
    {
        NeedDb();
        var c = RateLimited(null, permit: 2, b => b.UseSetting("Auth:RateLimit:RefreshPermitLimit", "6"));
        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(c, RefreshToken())).StatusCode); // 6 > login-ліміт 2
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Refresh(c, RefreshToken())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "203.0.113.10")).StatusCode); // login-кошик цілий
    }

    [SkippableFact]
    public async Task refresh_is_throttled_per_token_and_per_tenant()
    {
        NeedDb();
        var c = RateLimited(null, permit: 100, b =>
        {
            b.UseSetting("Auth:RateLimit:RefreshPermitLimit", "1000");
            b.UseSetting("Auth:RateLimit:RefreshPerTenantPermit", "8");
        });
        var token = RefreshToken();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(c, token)).StatusCode);
        var limited = await Refresh(c, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(c, RefreshToken())).StatusCode); // інший токен іншого tenant

        // ліміт на tenant: різні токени одного tenant
        var tenant = Guid.NewGuid();
        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(c, RefreshToken(tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Refresh(c, RefreshToken(tenant))).StatusCode);
    }

    // ---------- CORS ----------

    private static async Task<HttpResponseMessage> Preflight(HttpClient c, string origin, string url = "/api/beauty/locations")
    {
        var req = new HttpRequestMessage(HttpMethod.Options, url);
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", "GET");
        req.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        return await c.SendAsync(req);
    }

    [SkippableFact]
    public async Task cors_allows_only_configured_origins_allows_authorization_header_and_no_credentials()
    {
        NeedDb();
        var c = Host(b => b.UseSetting("Beauty:AllowedOrigins", "https://admin.example.com, https://other.example.com/")).CreateClient();

        var ok = await Preflight(c, "https://admin.example.com");
        Assert.True(ok.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);
        Assert.Equal("https://admin.example.com", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("authorization", string.Join(",", ok.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
        Assert.False(ok.Headers.Contains("Access-Control-Allow-Credentials"));

        var second = await Preflight(c, "https://other.example.com");
        Assert.Equal("https://other.example.com", second.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var evil = await Preflight(c, "https://evil.example.com");
        Assert.False(evil.Headers.Contains("Access-Control-Allow-Origin"));
        var wrongScheme = await Preflight(c, "http://admin.example.com");
        Assert.False(wrongScheme.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [SkippableFact]
    public async Task cors_adds_headers_to_actual_responses_and_is_off_without_configuration()
    {
        NeedDb();
        var on = Host(b => b.UseSetting("Beauty:AllowedOrigins", "https://admin.example.com")).CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        req.Headers.Add("Origin", "https://admin.example.com");
        var resp = await on.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("https://admin.example.com", resp.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var off = fx.Factory!.CreateClient();
        Assert.False((await Preflight(off, "https://admin.example.com")).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [SkippableFact]
    public void cors_rejects_wildcard_and_malformed_origins_at_startup()
    {
        NeedDb();
        Assert.ThrowsAny<Exception>(() => Host(b => b.UseSetting("Beauty:AllowedOrigins", "*")).CreateClient());
        Assert.ThrowsAny<Exception>(() => Host(b => b.UseSetting("Beauty:AllowedOrigins", "admin.example.com")).CreateClient());
        Assert.ThrowsAny<Exception>(() => Host(b => b.UseSetting("Beauty:AllowedOrigins", "https://a.example.com/path")).CreateClient());
    }

    // ---------- security headers ----------

    private HttpClient Https(string env)
    {
        var f = Host(b => b.UseEnvironment(env));
        return f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    }

    [SkippableFact]
    public async Task securityHeaders_include_nosniff_and_hsts_over_https_outside_development()
    {
        NeedDb();
        var prod = await Https("Production").GetAsync("/api/auth/me");
        Assert.Equal("nosniff", prod.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", prod.Headers.GetValues("Referrer-Policy").Single());
        Assert.StartsWith("max-age=", prod.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Contains("default-src 'none'", prod.Headers.GetValues("Content-Security-Policy").Single());

        // HSTS не додається до простого http (браузери його ігнорують за специфікацією)
        var http = await fx.Factory!.CreateClient().GetAsync("/api/auth/me");
        Assert.Equal("nosniff", http.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(http.Headers.Contains("Strict-Transport-Security"));

        var dev = await Https("Development").GetAsync("/api/auth/me");
        Assert.Equal("nosniff", dev.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(dev.Headers.Contains("Strict-Transport-Security"));
    }

    [SkippableFact]
    public async Task securityHeaders_are_present_on_error_and_anonymous_responses_too()
    {
        NeedDb();
        var c = fx.Factory!.CreateClient();
        foreach (var url in new[] { "/api/does-not-exist", "/api/beauty/slots", "/api/public/nope/locations" })
            Assert.Equal("nosniff", (await c.GetAsync(url)).Headers.GetValues("X-Content-Type-Options").Single());
    }

    // ---------- M3: lockout не розкриває існування акаунта ----------

    private async Task<(HttpStatusCode Status, JsonElement Body)> TryLogin(string slug, string email, string password)
    {
        var r = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = slug, email, password });
        return (r.StatusCode, await r.Content.ReadFromJsonAsync<JsonElement>());
    }

    [SkippableFact]
    public async Task lockout_for_unknown_email_looks_identical_to_lockout_of_a_real_account()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        const string wrong = "Wrong-Password-123456";
        var ghost = $"ghost-{Guid.NewGuid():N}@{t.Slug}.test";

        // справжній акаунт і неіснуючий: однакова послідовність 5 x 401, далі 423
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(t.Slug, t.OwnerEmail, wrong)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(t.Slug, ghost, wrong)).Status);
        }
        var real = await TryLogin(t.Slug, t.OwnerEmail, Password); // навіть із правильним паролем
        var unknown = await TryLogin(t.Slug, ghost, wrong);
        Assert.Equal(HttpStatusCode.Locked, real.Status);
        Assert.Equal(HttpStatusCode.Locked, unknown.Status);
        Assert.Equal(real.Body.GetProperty("code").GetString(), unknown.Body.GetProperty("code").GetString());
        Assert.Equal(real.Body.GetProperty("message").GetString(), unknown.Body.GetProperty("message").GetString());
        Assert.Equal(real.Body.ToString(), unknown.Body.ToString());

        // лічильник за ключем slug+email: інший email і інший slug не заблоковані
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(t.Slug, $"other-{Guid.NewGuid():N}@x.test", wrong)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin("no-such-tenant", ghost, wrong)).Status);
    }

    [SkippableFact]
    public async Task lockout_applies_to_unknown_tenant_slug_and_case_variants_of_the_same_email()
    {
        NeedDb();
        var slug = "ghost-" + Guid.NewGuid().ToString("N")[..10];
        var email = $"nobody-{Guid.NewGuid():N}@x.test";
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(slug, i % 2 == 0 ? email : email.ToUpperInvariant(), "Wrong-Password-123456")).Status);
        Assert.Equal(HttpStatusCode.Locked, (await TryLogin(slug.ToUpperInvariant(), email, "Wrong-Password-123456")).Status);
    }

    // ---------- L8: діапазон GET /appointments ----------

    [SkippableFact]
    public async Task appointments_list_rejects_ranges_longer_than_62_days()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken;
        var from = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        string Url(DateTimeOffset f, DateTimeOffset to) =>
            $"/api/beauty/appointments?from={Uri.EscapeDataString(f.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}";

        var ok = await fx.Send(HttpMethod.Get, Url(from, from.AddDays(62)), bearer: owner);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var big = await fx.Send(HttpMethod.Get, Url(from, from.AddDays(62).AddMinutes(1)), bearer: owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, big.StatusCode);
        Assert.Equal("range_too_large", (await Read<JsonElement>(big)).GetProperty("code").GetString());

        var huge = await fx.Send(HttpMethod.Get, Url(from.AddYears(-10), from.AddYears(10)), bearer: owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, huge.StatusCode);

        var inverted = await fx.Send(HttpMethod.Get, Url(from, from.AddDays(-1)), bearer: owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inverted.StatusCode);
        Assert.Equal("invalid_range", (await Read<JsonElement>(inverted)).GetProperty("code").GetString());

        // значення за замовчуванням (7 днів) працює
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, "/api/beauty/appointments", bearer: owner)).StatusCode);
    }
}
