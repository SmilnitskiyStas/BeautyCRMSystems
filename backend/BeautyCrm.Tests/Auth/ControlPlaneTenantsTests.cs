using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeautyCrm.Api.Auth.ControlPlane;
using BeautyCrm.Application.Features.BeautyControlPlane;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-818 + TASK-831 (ADR-018, §27.3-§27.7): /api/control/v1/tenants/*. Повний API (WebApplicationFactory) поверх реального
/// PostgreSQL з роллю БЕЗ BYPASSRLS; кожен запит підписаний ControlPlaneHmac зі свіжим nonce і реальним годинником.
/// </summary>
public sealed class ControlPlaneTenantsTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private const string Kid = "cp-tests";
    private const string AppUrl = "https://app.example.test";
    private static readonly byte[] Secret = Enumerable.Range(0, 48).Select(i => (byte)(i * 5 + 3)).ToArray();
    private static readonly object Actor = new { id = "sg-admin-7", displayName = "Admin Sg" };

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed class Host(WebApplicationFactory<Program> factory, CapturingLoggerProvider log) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public CapturingLoggerProvider Log { get; } = log;
        public HttpClient Client { get; } = factory.CreateClient();
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }
    }

    private Host Build(Action<IWebHostBuilder>? configure = null)
    {
        var log = new CapturingLoggerProvider();
        var f = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Secret", Convert.ToBase64String(Secret));
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Status", "active");
            b.UseSetting("ControlPlane:PublicAppUrl", AppUrl);
            configure?.Invoke(b);
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(log));
        });
        return new Host(f, log);
    }

    private static HttpRequestMessage Signed(HttpMethod method, string path, object? body = null, string? requestId = null, string? nonce = null,
        long? ts = null)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var timestamp = (ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        nonce ??= "N" + Guid.NewGuid().ToString("N");
        var sig = ControlPlaneSignature.Sign(Secret, ControlPlaneSignature.Canonical(Kid, timestamp, nonce, method.Method, path, "", bytes));
        var r = new HttpRequestMessage(method, path);
        if (bytes.Length > 0)
        {
            r.Content = new ByteArrayContent(bytes);
            r.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        }
        r.Headers.Add(ControlPlaneSignature.KeyIdHeader, Kid);
        r.Headers.Add(ControlPlaneSignature.TimestampHeader, timestamp);
        r.Headers.Add(ControlPlaneSignature.NonceHeader, nonce);
        r.Headers.Add(ControlPlaneSignature.SignatureHeader, sig);
        if (requestId is not null) r.Headers.Add(ControlPlaneSignature.RequestIdHeader, requestId);
        return r;
    }

    private static string NewRef() => "sg-" + Guid.NewGuid().ToString("N")[..16];

    private static object CreateBody(string externalRef, string? slug = null, string email = "owner@salon.test", object? extra = null, string[]? modules = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["externalRef"] = externalRef, ["name"] = "Salon X", ["slug"] = slug ?? ("cp-" + Guid.NewGuid().ToString("N")[..20]),
            ["owner"] = new { email, fullName = "Ім'я Прізвище" }, ["actor"] = Actor,
        };
        if (modules is not null) d["modules"] = modules;
        if (extra is IDictionary<string, object?> more) foreach (var (k, v) in more) d[k] = v;
        return d;
    }

    private static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    private static string TokenOf(string url) => url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

    private async Task<T> Q<T>(string sql, params (string, object)[] ps)
    {
        await using var c = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        var o = await cmd.ExecuteScalarAsync();
        return o is null or DBNull ? default! : (T)o;
    }

    private async Task Exec(string sql)
    {
        await using var c = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<(string Action, string Details, string? ActorId, string? KeyId, string? RequestId)>> AuditOf(Guid tenantId)
    {
        await using var c = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT action, details::text, actor_id, key_id, request_id FROM platform_audit_log WHERE tenant_id = @t ORDER BY id", c);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<(string, string, string?, string?, string?)>();
        while (await r.ReadAsync())
            list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    private async Task<(Guid TenantId, string ExternalRef, string Slug, string Email, string Token, JsonElement Body)> CreateViaControlAsync(Host h, string? slug = null)
    {
        var ext = NewRef();
        var body = CreateBody(ext, slug);
        var r = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", body));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await J(r);
        return (j.GetProperty("tenantId").GetGuid(), ext, j.GetProperty("slug").GetString()!, "owner@salon.test",
            TokenOf(j.GetProperty("invite").GetProperty("url").GetString()!), j);
    }

    /// <summary>Tenant з активним власником (пароль відомий) і прив'язкою до ShelfGuard: platform API + external_ref через admin-з'єднання.</summary>
    private async Task<(AuthApiFixture.TenantSeed Seed, string ExternalRef)> ActiveTenantWithRefAsync(string[]? modules = null)
    {
        var seed = await fx.CreateTenantAsync(modules);
        var ext = NewRef();
        await Exec($"UPDATE tenants SET external_ref = '{ext}', external_source = 'shelfguard' WHERE id = '{seed.TenantId}'");
        return (seed, ext);
    }

    // ---------- create ----------

    [SkippableFact]
    public async Task create_returns_201_with_invited_owner_and_one_time_link_and_audit_without_token()
    {
        NeedDb();
        using var h = Build();
        var ext = NewRef();
        var t0 = DateTimeOffset.UtcNow;
        var r = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(ext, "Cp-Salon-" + ext[3..9], "Owner@Salon.Test"), requestId: "req-create-1"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.False(r.Headers.Contains("Idempotent-Replayed"));
        var j = await J(r);
        var tenantId = j.GetProperty("tenantId").GetGuid();
        Assert.Equal(ext, j.GetProperty("externalRef").GetString());
        Assert.Equal("cp-salon-" + ext[3..9], j.GetProperty("slug").GetString()); // slug нормалізується
        Assert.Equal("active", j.GetProperty("status").GetString());
        Assert.Equal(6, j.GetProperty("modules").GetArrayLength()); // modules відсутній -> усі beauty-модулі (як у platform)
        Assert.Equal("invited", j.GetProperty("owner").GetProperty("state").GetString());
        Assert.Equal("owner@salon.test", j.GetProperty("owner").GetProperty("email").GetString());

        var url = j.GetProperty("invite").GetProperty("url").GetString()!;
        Assert.StartsWith(AppUrl + "/invite#t=", url);
        var token = TokenOf(url);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token); // 32 байти base64url без padding
        var exp = DateTimeOffset.Parse(j.GetProperty("invite").GetProperty("expiresAt").GetString()!);
        Assert.InRange((exp - t0).TotalHours, 71.9, 72.1);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", j.GetProperty("invite").GetProperty("expiresAt").GetString()!);

        // БД: tenant прив'язаний, власник invited без пароля, у запрошенні лише sha256
        Assert.Equal(ext, await Q<string>("SELECT external_ref FROM tenants WHERE id = @t", ("t", tenantId)));
        Assert.Equal("shelfguard", await Q<string>("SELECT external_source FROM tenants WHERE id = @t", ("t", tenantId)));
        Assert.Equal("invited", await Q<string>("SELECT state FROM users WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM users WHERE tenant_id = @t AND password_hash IS NOT NULL", ("t", tenantId)));
        Assert.Equal("owner", await Q<string>("SELECT role FROM users WHERE tenant_id = @t", ("t", tenantId)));
        var stored = await Q<byte[]>("SELECT token_hash FROM platform_owner_invites WHERE tenant_id = @t", ("t", tenantId));
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(token)), stored);
        Assert.Equal("shelfguard:sg-admin-7", await Q<string>("SELECT created_by_actor FROM platform_owner_invites WHERE tenant_id = @t", ("t", tenantId)));

        // аудит: дві дії з актором, kid і request id; жодного сліду токена
        var audit = await AuditOf(tenantId);
        Assert.Equal(["tenant.created", "owner_invite.issued"], audit.Select(a => a.Action));
        Assert.All(audit, a => { Assert.Equal("sg-admin-7", a.ActorId); Assert.Equal(Kid, a.KeyId); Assert.Equal("req-create-1", a.RequestId); });
        Assert.DoesNotContain(token, string.Join("|", audit.Select(a => a.Details)));
        Assert.DoesNotContain("owner@salon.test", string.Join("|", audit.Select(a => a.Details))); // email -> лише user_id
        Assert.Contains("user_id", audit[1].Details);
        Assert.DoesNotContain(token, await Q<string>("SELECT (t.*)::text FROM tenants t WHERE id = @t", ("t", tenantId)));
        Assert.DoesNotContain(token, await Q<string>("SELECT string_agg((i.*)::text, '|') FROM platform_owner_invites i WHERE tenant_id = @t", ("t", tenantId)));
        Assert.DoesNotContain(token, await Q<string>("SELECT string_agg((u.*)::text, '|') FROM users u WHERE tenant_id = @t", ("t", tenantId)));
        Assert.DoesNotContain(token, string.Join("\n", h.Log.Lines));
        Assert.DoesNotContain("invite#t=", string.Join("\n", h.Log.Lines));

        // власник без пароля не входить
        var login = await h.Client.PostAsync("/api/auth/login", System.Net.Http.Json.JsonContent.Create(
            new { tenant = j.GetProperty("slug").GetString(), email = "owner@salon.test", password = Password }));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [SkippableFact]
    public async Task create_is_idempotent_by_external_ref_and_conflicts_on_mismatch()
    {
        NeedDb();
        using var h = Build();
        var ext = NewRef();
        var slug = "cp-" + Guid.NewGuid().ToString("N")[..20];
        var first = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(ext, slug)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var tenantId = (await J(first)).GetProperty("tenantId").GetGuid();

        // повтор (email у іншому регістрі) -> 200 + Idempotent-Replayed, invite null, той самий tenantId
        var replay = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(ext, slug.ToUpperInvariant(), "OWNER@salon.test")));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        var rj = await J(replay);
        Assert.Equal(tenantId, rj.GetProperty("tenantId").GetGuid());
        Assert.Equal(JsonValueKind.Null, rj.GetProperty("invite").ValueKind);
        Assert.Equal("invited", rj.GetProperty("owner").GetProperty("state").GetString());

        // розбіжності -> 409 external_ref_conflict
        var otherSlug = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(ext, slug + "-x")));
        Assert.Equal(HttpStatusCode.Conflict, otherSlug.StatusCode);
        Assert.Equal("external_ref_conflict", await Code(otherSlug));
        var otherEmail = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(ext, slug, "other@salon.test")));
        Assert.Equal("external_ref_conflict", await Code(otherEmail));

        // slug чужого tenant -> 409 tenant_slug_taken (інший externalRef)
        var taken = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(NewRef(), slug)));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("tenant_slug_taken", await Code(taken));

        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM tenants WHERE external_ref = @e", ("e", ext)));
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM platform_owner_invites WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(["tenant.created", "owner_invite.issued", "tenant.create_replayed"], (await AuditOf(tenantId)).Select(a => a.Action));
    }

    [SkippableFact]
    public async Task parallel_duplicate_creates_yield_one_tenant_one_201_and_the_rest_200()
    {
        NeedDb();
        using var h = Build();
        var ext = NewRef();
        var body = CreateBody(ext, "cp-" + Guid.NewGuid().ToString("N")[..20]);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", body)))));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM tenants WHERE external_ref = @e", ("e", ext)));
        var ids = new HashSet<Guid>();
        foreach (var r in results) ids.Add((await J(r)).GetProperty("tenantId").GetGuid());
        Assert.Single(ids);
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM platform_owner_invites i JOIN tenants t ON t.id = i.tenant_id WHERE t.external_ref = @e", ("e", ext)));
    }

    [SkippableTheory]
    [InlineData("owner.password", "password_not_allowed")]
    [InlineData("slug", "invalid_slug")]
    [InlineData("modules", "unknown_module")]
    [InlineData("email", "invalid_email")]
    [InlineData("fullName", "invalid_name")]
    [InlineData("name", "invalid_name")]
    [InlineData("externalRef", "invalid_external_ref")]
    [InlineData("actor", "actor_required")]
    public async Task create_validation_returns_422_and_creates_nothing(string broken, string code)
    {
        NeedDb();
        using var h = Build();
        var ext = NewRef();
        var slug = "cp-" + Guid.NewGuid().ToString("N")[..20];
        var d = new Dictionary<string, object?>
        {
            ["externalRef"] = ext, ["name"] = "Salon", ["slug"] = slug, ["owner"] = new Dictionary<string, object?> { ["email"] = "o@x.test", ["fullName"] = "O" },
            ["actor"] = Actor,
        };
        switch (broken)
        {
            case "owner.password": d["owner"] = new { email = "o@x.test", fullName = "O", password = "Whatever-123456" }; break;
            case "slug": d["slug"] = "A_B"; break;
            case "modules": d["modules"] = new[] { "beauty_booking", "nope" }; break;
            case "email": d["owner"] = new { email = "not-an-email", fullName = "O" }; break;
            case "fullName": d["owner"] = new { email = "o@x.test", fullName = "  " }; break;
            case "name": d["name"] = ""; break;
            case "externalRef": d["externalRef"] = "bad ref!"; break;
            case "actor": d["actor"] = new { id = "", displayName = "x" }; break;
        }
        var r = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", d));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(code, await Code(r));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM tenants WHERE slug = @s OR external_ref = @e", ("s", slug), ("e", ext)));
    }

    [SkippableFact]
    public async Task create_with_explicit_modules_uses_them()
    {
        NeedDb();
        using var h = Build();
        var r = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants",
            CreateBody(NewRef(), modules: ["beauty_booking", "BEAUTY_CLIENTS", "beauty_booking"])));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(["beauty_booking", "beauty_clients"], (await J(r)).GetProperty("modules").EnumerateArray().Select(x => x.GetString()));
    }

    [SkippableFact]
    public async Task create_without_public_app_url_fails_before_any_write()
    {
        NeedDb();
        using var h = Build(b => b.UseSetting("ControlPlane:PublicAppUrl", ""));
        var ext = NewRef();
        // неналаштоване середовище = необроблений виняток (500 у проді; TestServer пробрасує його) ДО будь-якого запису
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(ext))));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM tenants WHERE external_ref = @e", ("e", ext)));
    }

    // ---------- get ----------

    [SkippableFact]
    public async Task get_returns_state_without_business_data_or_link_and_404_for_unknown()
    {
        NeedDb();
        using var h = Build();
        var t = await CreateViaControlAsync(h);
        var r = await h.Client.SendAsync(Signed(HttpMethod.Get, $"/api/control/v1/tenants/{t.ExternalRef}"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var raw = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(t.Token, raw);
        Assert.DoesNotContain("invite#t", raw);
        var j = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(t.TenantId, j.GetProperty("tenantId").GetGuid());
        Assert.Equal("Salon X", j.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("suspendedAt").ValueKind);
        var owner = j.GetProperty("owner");
        Assert.Equal("invited", owner.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, owner.GetProperty("inviteExpiresAt").ValueKind);
        Assert.Equal(new[] { "tenantId", "externalRef", "slug", "name", "status", "modules", "suspendedAt", "owner", "createdAt" }.Order(),
            j.EnumerateObject().Select(p => p.Name).Order());

        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.SendAsync(Signed(HttpMethod.Get, "/api/control/v1/tenants/sg-nope"))).StatusCode);
        var bad = await h.Client.SendAsync(Signed(HttpMethod.Get, "/api/control/v1/tenants/bad.ref"));
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
        Assert.Equal("tenant_not_found", await Code(bad));
        Assert.Equal(0, (await AuditOf(t.TenantId)).Count(a => a.Action.Contains("get")));
    }

    // ---------- suspend / resume ----------

    [SkippableFact]
    public async Task suspend_blocks_login_refresh_api_and_public_booking_and_resume_restores_login_only()
    {
        NeedDb();
        using var h = Build();
        var (seed, ext) = await ActiveTenantWithRefAsync();
        var owner = await fx.LoginAsync(seed.Slug, seed.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, "/api/beauty/appointments", bearer: owner.AccessToken, client: h.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync($"/api/public/{seed.Slug}/locations")).StatusCode);

        var s = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/suspend", new { reason = "  неоплата  ", actor = Actor }, requestId: "req-s"));
        Assert.Equal(HttpStatusCode.OK, s.StatusCode);
        var sj = await J(s);
        Assert.Equal("suspended", sj.GetProperty("status").GetString());
        var suspendedAt = sj.GetProperty("suspendedAt").GetString()!;

        Assert.Equal("suspended", await Q<string>("SELECT status FROM tenants WHERE id = @t", ("t", seed.TenantId)));
        Assert.Equal("неоплата", await Q<string>("SELECT suspended_reason FROM tenants WHERE id = @t", ("t", seed.TenantId)));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM refresh_tokens WHERE tenant_id = @t AND revoked_at IS NULL", ("t", seed.TenantId)));

        // вхід 401, refresh 401, API 403 (module_disabled - чинний код), публічний запис 403 tenant_suspended
        var login = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = seed.Slug, email = seed.OwnerEmail, password = Password }, client: h.Client);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = owner.RefreshToken }, client: h.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Send(HttpMethod.Get, "/api/beauty/appointments", bearer: owner.AccessToken, client: h.Client)).StatusCode);
        foreach (var path in new[] { "locations", "slots?locationId=" + Guid.NewGuid() + "&serviceId=" + Guid.NewGuid() + "&date=2026-12-01" })
        {
            var pub = await h.Client.GetAsync($"/api/public/{seed.Slug}/{path}");
            Assert.Equal(HttpStatusCode.Forbidden, pub.StatusCode);
            Assert.Equal("tenant_suspended", await Code(pub));
        }
        var pubPost = await h.Client.PostAsync($"/api/public/{seed.Slug}/appointments", System.Net.Http.Json.JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Forbidden, pubPost.StatusCode);
        Assert.Equal("tenant_suspended", await Code(pubPost));

        // повтор suspend: 200 з тим самим suspendedAt, аудит noop
        var again = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/suspend", new { reason = "інша причина", actor = Actor }));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(suspendedAt, (await J(again)).GetProperty("suspendedAt").GetString());
        Assert.Equal("неоплата", await Q<string>("SELECT suspended_reason FROM tenants WHERE id = @t", ("t", seed.TenantId))); // не перезаписано

        var get = await J(await h.Client.SendAsync(Signed(HttpMethod.Get, $"/api/control/v1/tenants/{ext}")));
        Assert.Equal("suspended", get.GetProperty("status").GetString());
        Assert.Equal(suspendedAt, get.GetProperty("suspendedAt").GetString());

        // resume: очищає suspended_*; вхід працює, старі сесії - ні
        var res = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/resume", new { actor = Actor }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("active", (await J(res)).GetProperty("status").GetString());
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM tenants WHERE id = @t AND (suspended_at IS NOT NULL OR suspended_reason IS NOT NULL)", ("t", seed.TenantId)));
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = seed.Slug, email = seed.OwnerEmail, password = Password }, client: h.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = owner.RefreshToken }, client: h.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync($"/api/public/{seed.Slug}/locations")).StatusCode);
        var resumeAgain = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/resume", new { actor = Actor }));
        Assert.Equal(HttpStatusCode.OK, resumeAgain.StatusCode);

        var audit = await AuditOf(seed.TenantId);
        Assert.Equal(["tenant.suspended", "tenant.suspended", "tenant.resumed", "tenant.resumed"], audit.Select(a => a.Action));
        Assert.DoesNotContain("noop", audit[0].Details);
        Assert.Contains("\"noop\": true", audit[1].Details);
        Assert.Contains("неоплата", audit[0].Details);
        Assert.DoesNotContain("noop", audit[2].Details);
        Assert.Contains("\"noop\": true", audit[3].Details);
        Assert.All(audit, a => { Assert.Equal("sg-admin-7", a.ActorId); Assert.Equal(Kid, a.KeyId); });
        Assert.Equal("req-s", audit[0].RequestId);
    }

    [SkippableFact]
    public async Task suspended_tenant_with_valid_token_gets_403_tenant_suspended_on_non_module_endpoints_and_resume_restores()
    {
        NeedDb();
        using var h = Build();
        var (seed, ext) = await ActiveTenantWithRefAsync();
        var owner = await fx.LoginAsync(seed.Slug, seed.OwnerEmail);
        string[] urls = ["/api/auth/me", "/api/users", "/api/invites"];
        foreach (var u in urls)
            Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, u, bearer: owner.AccessToken, client: h.Client)).StatusCode);

        await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/suspend", new { reason = "x", actor = Actor }));
        // M3 (TASK-826): глобальний TenantActiveFilter - чинний access-токен не дає доступу до /api/auth/me, /api/users, /api/invites.
        foreach (var u in urls)
        {
            var r = await fx.Send(HttpMethod.Get, u, bearer: owner.AccessToken, client: h.Client);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("tenant_suspended", await Code(r));
        }

        await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/resume", new { actor = Actor }));
        foreach (var u in urls)
            Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, u, bearer: owner.AccessToken, client: h.Client)).StatusCode);
    }

    [SkippableFact]
    public async Task suspend_and_resume_validation_order_404_then_422()
    {
        NeedDb();
        using var h = Build();
        var t = await CreateViaControlAsync(h);
        // 404 раніше за 422 (немає actor)
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants/sg-nope/suspend", new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants/sg-nope/resume", new { }))).StatusCode);

        var noReason = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/suspend", new { reason = " ", actor = Actor }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal("reason_required", await Code(noReason));
        var longReason = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/suspend", new { reason = new string('x', 501), actor = Actor }));
        Assert.Equal("reason_required", await Code(longReason));
        var noActor = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/suspend", new { reason = "ok" }));
        Assert.Equal("actor_required", await Code(noActor));
        var noActorResume = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/resume", new { actor = new { id = new string('a', 65), displayName = "x" } }));
        Assert.Equal("actor_required", await Code(noActorResume));
        Assert.Equal("active", await Q<string>("SELECT status FROM tenants WHERE id = @t", ("t", t.TenantId)));
        Assert.DoesNotContain(await AuditOf(t.TenantId), a => a.Action is "tenant.suspended" or "tenant.resumed");
    }

    // ---------- modules (TASK-831) ----------

    [SkippableFact]
    public async Task modules_put_sets_full_set_noop_audit_before_after_and_works_for_suspended()
    {
        NeedDb();
        using var h = Build();
        var (seed, ext) = await ActiveTenantWithRefAsync(["beauty_booking", "beauty_catalog"]);
        var owner = await fx.LoginAsync(seed.Slug, seed.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, "/api/beauty/services", bearer: owner.AccessToken, client: h.Client)).StatusCode);

        var set = await h.Client.SendAsync(Signed(HttpMethod.Put, $"/api/control/v1/tenants/{ext}/modules",
            new { modules = new[] { "Beauty_Booking", "beauty_clients", "beauty_booking" }, actor = Actor }, requestId: "req-m"));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(["beauty_booking", "beauty_clients"], (await J(set)).GetProperty("modules").EnumerateArray().Select(x => x.GetString()));

        // вимкнений модуль -> 403 module_disabled у staff API
        var disabled = await fx.Send(HttpMethod.Get, "/api/beauty/services", bearer: owner.AccessToken, client: h.Client);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("module_disabled", await Code(disabled));

        // той самий набір в іншому порядку -> 200 noop
        var noop = await h.Client.SendAsync(Signed(HttpMethod.Put, $"/api/control/v1/tenants/{ext}/modules",
            new { modules = new[] { "beauty_clients", "beauty_booking" }, actor = Actor }));
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(["beauty_booking", "beauty_clients"], (await J(noop)).GetProperty("modules").EnumerateArray().Select(x => x.GetString()).Order());

        // GET бачить поточний набір; та сама доменна операція, що PATCH /api/platform/tenants/{id}
        var patch = await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{seed.TenantId}", new { modules = new[] { "beauty_catalog" } }, platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        var got = await J(await h.Client.SendAsync(Signed(HttpMethod.Get, $"/api/control/v1/tenants/{ext}")));
        Assert.Equal(["beauty_catalog"], got.GetProperty("modules").EnumerateArray().Select(x => x.GetString()));

        // призупинений tenant: зміна дозволена, стан не міняється
        await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/suspend", new { reason = "x", actor = Actor }));
        var susp = await h.Client.SendAsync(Signed(HttpMethod.Put, $"/api/control/v1/tenants/{ext}/modules", new { modules = new[] { "beauty_marketing" }, actor = Actor }));
        Assert.Equal(HttpStatusCode.OK, susp.StatusCode);
        Assert.Equal("suspended", await Q<string>("SELECT status FROM tenants WHERE id = @t", ("t", seed.TenantId)));

        var audit = (await AuditOf(seed.TenantId)).Where(a => a.Action == "tenant.modules_changed").ToList();
        Assert.Equal(3, audit.Count); // set, noop, suspended-set (PATCH platform не пише control-аудит)
        Assert.Contains("\"before\": [\"beauty_booking\", \"beauty_catalog\"]", audit[0].Details);
        Assert.Contains("\"after\": [\"beauty_booking\", \"beauty_clients\"]", audit[0].Details);
        Assert.Equal("req-m", audit[0].RequestId);
        Assert.Equal("sg-admin-7", audit[0].ActorId);
        Assert.Contains("\"noop\": true", audit[1].Details);
        Assert.DoesNotContain("noop", audit[2].Details);
    }

    [SkippableTheory]
    [InlineData("unknown", "unknown_module")]
    [InlineData("empty", "modules_required")]
    [InlineData("missing", "modules_required")]
    [InlineData("actor", "actor_required")]
    public async Task modules_put_validation_422_and_404(string kind, string code)
    {
        NeedDb();
        using var h = Build();
        var (seed, ext) = await ActiveTenantWithRefAsync(["beauty_booking"]);
        object body = kind switch
        {
            "unknown" => new { modules = new[] { "beauty_booking", "nope" }, actor = Actor },
            "empty" => new { modules = Array.Empty<string>(), actor = Actor },
            "missing" => new { actor = Actor },
            _ => new { modules = new[] { "beauty_clients" } },
        };
        var r = await h.Client.SendAsync(Signed(HttpMethod.Put, $"/api/control/v1/tenants/{ext}/modules", body));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(code, await Code(r));
        Assert.Equal(["beauty_booking"], await Q<string[]>("SELECT modules FROM tenants WHERE id = @t", ("t", seed.TenantId)));
        Assert.Empty(await AuditOf(seed.TenantId));

        var nf = await h.Client.SendAsync(Signed(HttpMethod.Put, "/api/control/v1/tenants/sg-nope/modules", new { modules = new[] { "beauty_booking" }, actor = Actor }));
        Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);
        Assert.Equal("tenant_not_found", await Code(nf));
    }

    // ---------- owner-invite ----------

    [SkippableFact]
    public async Task owner_invite_reissue_revokes_old_returns_new_link_once_and_audits()
    {
        NeedDb();
        using var h = Build();
        var t = await CreateViaControlAsync(h);
        var r = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/owner-invite", new { actor = Actor }, requestId: "req-i"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await J(r);
        var token2 = TokenOf(j.GetProperty("url").GetString()!);
        Assert.StartsWith(AppUrl + "/invite#t=", j.GetProperty("url").GetString());
        Assert.NotEqual(t.Token, token2);

        Assert.Equal(2, await Q<int>("SELECT count(*)::int FROM platform_owner_invites WHERE tenant_id = @t", ("t", t.TenantId)));
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM platform_owner_invites WHERE tenant_id = @t AND revoked_at IS NULL AND used_at IS NULL", ("t", t.TenantId)));
        var oldHash = SHA256.HashData(Encoding.ASCII.GetBytes(t.Token));
        Assert.True(await Q<bool>("SELECT revoked_at IS NOT NULL FROM platform_owner_invites WHERE token_hash = @h", ("h", oldHash)));
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(token2)),
            await Q<byte[]>("SELECT token_hash FROM platform_owner_invites WHERE tenant_id = @t AND revoked_at IS NULL", ("t", t.TenantId)));

        var audit = await AuditOf(t.TenantId);
        Assert.Equal(["tenant.created", "owner_invite.issued", "owner_invite.revoked", "owner_invite.issued"], audit.Select(a => a.Action));
        Assert.DoesNotContain(token2, string.Join("|", audit.Select(a => a.Details)));
        Assert.DoesNotContain(token2, string.Join("\n", h.Log.Lines));
        Assert.DoesNotContain(t.Token, string.Join("\n", h.Log.Lines));

        // GET показує термін нового запрошення, але не посилання
        var get = await J(await h.Client.SendAsync(Signed(HttpMethod.Get, $"/api/control/v1/tenants/{t.ExternalRef}")));
        Assert.Equal(j.GetProperty("expiresAt").GetString(), get.GetProperty("owner").GetProperty("inviteExpiresAt").GetString());
    }

    [SkippableFact]
    public async Task owner_invite_errors_404_422_409_owner_active_and_suspended()
    {
        NeedDb();
        using var h = Build();
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants/sg-nope/owner-invite", new { actor = Actor }))).StatusCode);

        var t = await CreateViaControlAsync(h);
        var noActor = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/owner-invite", new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noActor.StatusCode);
        Assert.Equal("actor_required", await Code(noActor));

        await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/suspend", new { reason = "x", actor = Actor }));
        var susp = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{t.ExternalRef}/owner-invite", new { actor = Actor }));
        Assert.Equal(HttpStatusCode.Conflict, susp.StatusCode);
        Assert.Equal("tenant_suspended", await Code(susp));
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM platform_owner_invites WHERE tenant_id = @t", ("t", t.TenantId)));

        var (seed, ext) = await ActiveTenantWithRefAsync(); // власник з паролем = state active
        var active = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{ext}/owner-invite", new { actor = Actor }));
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal("owner_already_active", await Code(active));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM platform_owner_invites WHERE tenant_id = @t", ("t", seed.TenantId)));
    }

    // ---------- автентифікація, replay, cross-tenant ----------

    [SkippableFact]
    public async Task only_signed_requests_are_accepted_jwt_and_platform_key_do_not_open_the_group()
    {
        NeedDb();
        using var h = Build();
        var (seed, ext) = await ActiveTenantWithRefAsync();
        var owner = await fx.LoginAsync(seed.Slug, seed.OwnerEmail);
        var path = $"/api/control/v1/tenants/{ext}/suspend";

        foreach (var req in new[]
        {
            Req(HttpMethod.Post, path, new { reason = "x", actor = Actor }, bearer: owner.AccessToken),
            Req(HttpMethod.Post, path, new { reason = "x", actor = Actor }, platformKey: PlatformKey),
            Req(HttpMethod.Post, path, new { reason = "x", actor = Actor }),
            Req(HttpMethod.Post, "/api/control/v1/tenants", new { }, platformKey: PlatformKey),
            Req(HttpMethod.Get, $"/api/control/v1/tenants/{ext}", bearer: owner.AccessToken),
        })
        {
            var r = await h.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("control_auth_failed", await Code(r));
        }
        Assert.Equal("active", await Q<string>("SELECT status FROM tenants WHERE id = @t", ("t", seed.TenantId)));

        // підпис, у якому змінено тіло після підпису, відхиляється
        var tampered = Signed(HttpMethod.Post, path, new { reason = "x", actor = Actor });
        tampered.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { reason = "y", actor = Actor }, Json));
        tampered.Content.Headers.ContentType = new("application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(tampered)).StatusCode);
        Assert.Empty(await AuditOf(seed.TenantId));
    }

    [SkippableFact]
    public async Task replayed_signed_request_is_rejected_and_has_no_second_effect()
    {
        NeedDb();
        using var h = Build();
        var (seed, ext) = await ActiveTenantWithRefAsync(["beauty_booking"]);
        var nonce = "N" + Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new { modules = new[] { "beauty_clients" }, actor = Actor };
        var first = await h.Client.SendAsync(Signed(HttpMethod.Put, $"/api/control/v1/tenants/{ext}/modules", body, nonce: nonce, ts: ts));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // перехоплений запит відтворено байт-у-байт (той самий nonce і підпис)
        var replay = await h.Client.SendAsync(Signed(HttpMethod.Put, $"/api/control/v1/tenants/{ext}/modules", body, nonce: nonce, ts: ts));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("control_auth_failed", await Code(replay));
        Assert.Single(await AuditOf(seed.TenantId));

        // replay create не дублює tenant/запрошення/аудит
        var ext2 = NewRef();
        var createBody = CreateBody(ext2);
        var n2 = "N" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", createBody, nonce: n2, ts: ts))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", createBody, nonce: n2, ts: ts))).StatusCode);
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM platform_audit_log a JOIN tenants t ON t.id = a.tenant_id WHERE t.external_ref = @e AND a.action = 'tenant.created'", ("e", ext2)));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM platform_audit_log a JOIN tenants t ON t.id = a.tenant_id WHERE t.external_ref = @e AND a.action = 'tenant.create_replayed'", ("e", ext2)));
    }

    [SkippableFact]
    public async Task operations_are_scoped_to_one_tenant_and_only_shelfguard_bound_tenants_are_reachable()
    {
        NeedDb();
        using var h = Build();
        var (a, extA) = await ActiveTenantWithRefAsync(["beauty_booking"]);
        var (b, extB) = await ActiveTenantWithRefAsync(["beauty_booking"]);
        var manual = await fx.CreateTenantAsync(); // без external_ref (створений вручну)

        await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{extA}/suspend", new { reason = "x", actor = Actor }));
        await h.Client.SendAsync(Signed(HttpMethod.Put, $"/api/control/v1/tenants/{extA}/modules", new { modules = new[] { "beauty_clients" }, actor = Actor }));
        Assert.Equal("active", await Q<string>("SELECT status FROM tenants WHERE id = @t", ("t", b.TenantId)));
        Assert.Equal(["beauty_booking"], await Q<string[]>("SELECT modules FROM tenants WHERE id = @t", ("t", b.TenantId)));
        Assert.Empty(await AuditOf(b.TenantId));
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = b.Slug, email = b.OwnerEmail, password = Password }, client: h.Client)).StatusCode);

        // tenant без external_ref недосяжний (політика lookup лише для external_source = 'shelfguard'); slug/uuid як externalRef не працюють
        foreach (var probe in new[] { manual.Slug, manual.TenantId.ToString(), extB.ToUpperInvariant() })
            Assert.Equal(HttpStatusCode.NotFound, (await h.Client.SendAsync(Signed(HttpMethod.Get, $"/api/control/v1/tenants/{probe}"))).StatusCode);
        Assert.Equal("active", await Q<string>("SELECT status FROM tenants WHERE id = @t", ("t", manual.TenantId)));

        // create з externalRef існуючого чужого tenant не виявляє і не змінює чужі дані
        var clash = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", CreateBody(extB, "cp-" + Guid.NewGuid().ToString("N")[..20])));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("external_ref_conflict", await Code(clash));
    }

    [SkippableFact]
    public async Task requests_run_without_bypassrls_and_leave_no_session_tenant_state()
    {
        NeedDb();
        using var h = Build();
        var t = await CreateViaControlAsync(h);
        // роль застосунку не superuser/BYPASSRLS (AddBeautyDbRoleGuard перевіряє на старті), а сесійний app.tenant_id після запитів порожній
        await using var c = new NpgsqlConnection(fx.Db.AppConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = current_user", c);
        Assert.False((bool)(await cmd.ExecuteScalarAsync())!);
        await using var cmd2 = new NpgsqlCommand("SELECT count(*) FROM tenants", c);
        Assert.Equal(0L, (long)(await cmd2.ExecuteScalarAsync())!); // без app.tenant_id / app.tenant_external_ref жодного рядка
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(Signed(HttpMethod.Get, $"/api/control/v1/tenants/{t.ExternalRef}"))).StatusCode);
    }
}
