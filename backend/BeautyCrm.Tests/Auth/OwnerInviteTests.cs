using System.Net;
using System.Net.Http.Json;
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
/// TASK-819 (ADR-018, §27.8): POST /api/auth/invite/inspect і /accept. Повний API поверх реального PostgreSQL з роллю БЕЗ BYPASSRLS.
/// Tenant і запрошення створюються справжнім control plane (HMAC), токен береться з посилання у відповіді.
/// </summary>
public sealed class OwnerInviteTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private const string Kid = "cp-tests";
    private const string AppUrl = "https://app.example.test";
    private const string NewPassword = "Brand-New-Passw0rd!";
    private static readonly byte[] Secret = Enumerable.Range(0, 48).Select(i => (byte)(i * 7 + 1)).ToArray();
    private static readonly object Actor = new { id = "sg-admin-7", displayName = "Admin Sg" };

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed class Host(WebApplicationFactory<Program> factory, CapturingLoggerProvider log) : IDisposable
    {
        public CapturingLoggerProvider Log { get; } = log;
        public HttpClient Client { get; } = factory.CreateClient();
        public void Dispose() { Client.Dispose(); factory.Dispose(); }
    }

    /// <summary>Ліміти за замовчуванням високі (щоб тести не заважали один одному); тести лімітів задають свої.</summary>
    private Host Build(int ipLimit = 1000, int tokenLimit = 1000)
    {
        var log = new CapturingLoggerProvider();
        var f = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Secret", Convert.ToBase64String(Secret));
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Status", "active");
            b.UseSetting("ControlPlane:PublicAppUrl", AppUrl);
            b.UseSetting("Auth:RateLimit:InvitePermitLimit", ipLimit.ToString());
            b.UseSetting("Auth:RateLimit:InvitePerTokenPermit", tokenLimit.ToString());
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(log));
        });
        return new Host(f, log);
    }

    private static HttpRequestMessage Signed(HttpMethod method, string path, object? body = null)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = "N" + Guid.NewGuid().ToString("N");
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
        return r;
    }

    private sealed record Created(Guid TenantId, string ExternalRef, string Slug, string Email, Guid OwnerId, string Token);

    private static string TokenOf(string url) => url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

    private async Task<Created> CreateAsync(Host h, string email = "Owner@Salon.Test")
    {
        var ext = "sg-" + Guid.NewGuid().ToString("N")[..16];
        var slug = "inv-" + Guid.NewGuid().ToString("N")[..20];
        var r = await h.Client.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", new
        {
            externalRef = ext, name = "Salon Invite", slug, owner = new { email, fullName = "Owner Name" }, actor = Actor,
        }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return new Created(j.GetProperty("tenantId").GetGuid(), ext, slug, email.ToLowerInvariant(),
            j.GetProperty("owner").GetProperty("userId").GetGuid(), TokenOf(j.GetProperty("invite").GetProperty("url").GetString()!));
    }

    private static Task<HttpResponseMessage> Inspect(Host h, string? token) =>
        h.Client.PostAsJsonAsync("/api/auth/invite/inspect", new { token }, Json);

    private static Task<HttpResponseMessage> Accept(Host h, string? token, string? password = NewPassword) =>
        h.Client.PostAsJsonAsync("/api/auth/invite/accept", new { token, password }, Json);

    private static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

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

    private Task<HttpResponseMessage> Login(Host h, Created c, string password) =>
        h.Client.PostAsJsonAsync("/api/auth/login", new { tenant = c.Slug, email = c.Email, password }, Json);

    private static async Task AssertInvalid(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal("invite_invalid", (await J(r)).GetProperty("code").GetString());
    }

    // ---------- inspect ----------

    [SkippableFact]
    public async Task inspect_returns_tenant_name_masked_email_and_expiry_only_and_does_not_consume_the_token()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);
        var r = await Inspect(h, c.Token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var j = await J(r);
        Assert.Equal(["tenantName", "email", "expiresAt"], j.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Salon Invite", j.GetProperty("tenantName").GetString());
        Assert.Equal("o***@salon.test", j.GetProperty("email").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", j.GetProperty("expiresAt").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await Inspect(h, c.Token)).StatusCode); // повторно можна: inspect не гасить
        Assert.Null(await Q<DateTime?>("SELECT used_at FROM platform_owner_invites WHERE tenant_id = @t", ("t", c.TenantId)));
    }

    // ---------- accept: happy path ----------

    [SkippableFact]
    public async Task accept_sets_password_activates_owner_returns_login_shaped_session_and_audits()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);

        // до accept вхід неможливий (401), з будь-яким паролем
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(h, c, NewPassword)).StatusCode);

        var r = await Accept(h, c.Token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var j = await J(r);
        Assert.False(string.IsNullOrEmpty(j.GetProperty("accessToken").GetString()));
        Assert.True(j.GetProperty("expiresInSeconds").GetInt32() > 0);
        var refresh = j.GetProperty("refreshToken").GetString()!;
        Assert.Equal(c.OwnerId, j.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal("owner", j.GetProperty("user").GetProperty("role").GetString());

        // БД: власник active з паролем, запрошення погашене, refresh-токен створено, аудит без токена/пароля/email
        Assert.Equal("active", await Q<string>("SELECT state FROM users WHERE id = @u", ("u", c.OwnerId)));
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM users WHERE id = @u AND password_hash IS NOT NULL AND password_hash <> ''", ("u", c.OwnerId)));
        Assert.NotNull(await Q<DateTime?>("SELECT used_at FROM platform_owner_invites WHERE tenant_id = @t", ("t", c.TenantId)));
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM refresh_tokens WHERE user_id = @u", ("u", c.OwnerId)));
        var audit = await Q<string>("SELECT row_to_json(a)::text FROM platform_audit_log a WHERE tenant_id = @t AND action = 'owner_invite.accepted'", ("t", c.TenantId));
        Assert.NotNull(audit);
        var a = JsonDocument.Parse(audit).RootElement;
        Assert.Equal("owner", a.GetProperty("actor_type").GetString());
        Assert.Equal(c.OwnerId.ToString(), a.GetProperty("actor_id").GetString());
        Assert.DoesNotContain(c.Token, audit);
        Assert.DoesNotContain(NewPassword, audit);
        Assert.DoesNotContain("salon.test", audit);

        // вхід після accept працює; видані токени придатні (me, refresh)
        Assert.Equal(HttpStatusCode.OK, (await Login(h, c, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(h, c, Password)).StatusCode);
        var me = await h.Client.SendAsync(Req(HttpMethod.Get, "/api/auth/me", bearer: j.GetProperty("accessToken").GetString()));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var refreshed = await h.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh }, Json);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // Токен/пароль не потрапили в логи і в жодну з таблиць запрошень/аудиту
        var logs = string.Join("\n", h.Log.Lines);
        Assert.DoesNotContain(c.Token, logs);
        Assert.DoesNotContain(NewPassword, logs);
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM platform_owner_invites i WHERE i::text LIKE '%' || @t || '%'", ("t", c.Token)));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM platform_audit_log a WHERE a::text LIKE '%' || @t || '%'", ("t", c.Token)));
        Assert.Equal(0, await Q<int>("SELECT count(*)::int FROM users u WHERE u::text LIKE '%' || @t || '%' OR u::text LIKE '%' || @p || '%'", ("t", c.Token), ("p", NewPassword)));
    }

    // ---------- непридатні токени: один код ----------

    [SkippableFact]
    public async Task reused_token_is_410_and_indistinguishable_from_unknown_and_malformed_tokens()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);
        Assert.Equal(HttpStatusCode.OK, (await Accept(h, c.Token)).StatusCode);

        var unknown = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var bodies = new List<string>();
        foreach (var token in new string?[] { c.Token, unknown, "short", "", null, new string('a', 500), "бла-бла-бла-" + new string('x', 40) })
        {
            foreach (var r in new[] { await Inspect(h, token), await Accept(h, token, "Another-Strong-Pass-1") })
            {
                await AssertInvalid(r);
                bodies.Add(await r.Content.ReadAsStringAsync());
            }
        }
        Assert.Single(bodies.Distinct()); // жодної різниці у відповіді залежно від причини

        // порожнє тіло теж 410, не 422/400
        var empty = await h.Client.PostAsync("/api/auth/invite/inspect", null);
        await AssertInvalid(empty);

        // повторний accept не змінив пароль
        Assert.Equal(HttpStatusCode.OK, (await Login(h, c, NewPassword)).StatusCode);
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM platform_audit_log WHERE tenant_id = @t AND action = 'owner_invite.accepted'", ("t", c.TenantId)));
    }

    [SkippableFact]
    public async Task expired_token_is_410_for_inspect_and_accept_and_owner_stays_invited()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);
        await Exec($"UPDATE platform_owner_invites SET created_at = now() - interval '2 hours', expires_at = now() - interval '1 hour' WHERE tenant_id = '{c.TenantId}'");
        await AssertInvalid(await Inspect(h, c.Token));
        await AssertInvalid(await Accept(h, c.Token));
        Assert.Equal("invited", await Q<string>("SELECT state FROM users WHERE id = @u", ("u", c.OwnerId)));
        Assert.Null(await Q<DateTime?>("SELECT used_at FROM platform_owner_invites WHERE tenant_id = @t", ("t", c.TenantId)));
    }

    [SkippableFact]
    public async Task reissued_invite_revokes_the_previous_token_and_the_new_one_works()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);
        var re = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{c.ExternalRef}/owner-invite", new { actor = Actor }));
        Assert.Equal(HttpStatusCode.Created, re.StatusCode);
        var newToken = TokenOf((await J(re)).GetProperty("url").GetString()!);
        Assert.NotEqual(c.Token, newToken);

        await AssertInvalid(await Inspect(h, c.Token));
        await AssertInvalid(await Accept(h, c.Token));
        Assert.Equal(HttpStatusCode.OK, (await Inspect(h, newToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Accept(h, newToken)).StatusCode);
    }

    [SkippableFact]
    public async Task suspended_tenant_gives_403_for_a_valid_token_without_consuming_it_and_410_for_unknown_ones()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);
        var sus = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{c.ExternalRef}/suspend", new { reason = "test", actor = Actor }));
        Assert.Equal(HttpStatusCode.OK, sus.StatusCode);

        foreach (var r in new[] { await Inspect(h, c.Token), await Accept(h, c.Token) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("tenant_suspended", (await J(r)).GetProperty("code").GetString());
        }
        await AssertInvalid(await Inspect(h, "A" + c.Token[1..])); // невідомий токен не розкриває статус
        Assert.Null(await Q<DateTime?>("SELECT used_at FROM platform_owner_invites WHERE tenant_id = @t", ("t", c.TenantId)));
        Assert.Equal("invited", await Q<string>("SELECT state FROM users WHERE id = @u", ("u", c.OwnerId)));

        var res = await h.Client.SendAsync(Signed(HttpMethod.Post, $"/api/control/v1/tenants/{c.ExternalRef}/resume", new { actor = Actor }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Accept(h, c.Token)).StatusCode);
    }

    // ---------- пароль ----------

    [SkippableFact]
    public async Task weak_password_is_422_and_does_not_consume_the_token()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);
        foreach (var weak in new string?[] { "short", "", null, new string('x', 129) })
        {
            var r = await Accept(h, c.Token, weak);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("weak_password", (await J(r)).GetProperty("code").GetString());
        }
        Assert.Null(await Q<DateTime?>("SELECT used_at FROM platform_owner_invites WHERE tenant_id = @t", ("t", c.TenantId)));
        Assert.Equal("invited", await Q<string>("SELECT state FROM users WHERE id = @u", ("u", c.OwnerId)));
        Assert.Equal(HttpStatusCode.OK, (await Accept(h, c.Token)).StatusCode); // той самий токен ще чинний
    }

    // ---------- конкурентність ----------

    [SkippableFact]
    public async Task parallel_accepts_of_the_same_token_succeed_exactly_once()
    {
        NeedDb();
        using var h = Build();
        var c = await CreateAsync(h);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Accept(h, c.Token, $"Parallel-Passw0rd-{i:D2}!")));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(9, results.Count(r => r.StatusCode == HttpStatusCode.Gone));
        foreach (var r in results.Where(r => r.StatusCode == HttpStatusCode.Gone)) await AssertInvalid(r);

        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM refresh_tokens WHERE user_id = @u", ("u", c.OwnerId)));
        Assert.Equal(1, await Q<int>("SELECT count(*)::int FROM platform_audit_log WHERE tenant_id = @t AND action = 'owner_invite.accepted'", ("t", c.TenantId)));
        Assert.Equal("active", await Q<string>("SELECT state FROM users WHERE id = @u", ("u", c.OwnerId)));
    }

    // ---------- rate limit ----------

    [SkippableFact]
    public async Task ip_rate_limit_returns_429_with_no_store_after_the_limit()
    {
        NeedDb();
        using var h = Build(ipLimit: 4);
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last = await Inspect(h, "garbage-" + i);
            statuses.Add(last.StatusCode);
        }
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.Gone));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal("no-store", last!.Headers.CacheControl?.ToString());
        Assert.Equal("rate_limited", (await J(last)).GetProperty("code").GetString());
        // ліміт спільний для inspect і accept
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Accept(h, "x")).StatusCode);
    }

    [SkippableFact]
    public async Task per_token_limit_stops_bruteforce_of_one_token_but_not_other_tokens()
    {
        NeedDb();
        using var h = Build(tokenLimit: 3);
        var guess = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await Inspect(h, guess)).StatusCode);
        Assert.Equal([HttpStatusCode.Gone, HttpStatusCode.Gone, HttpStatusCode.Gone, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
        var tooMany = await Accept(h, guess);
        Assert.Equal(HttpStatusCode.TooManyRequests, tooMany.StatusCode); // і для accept, незалежно від пароля
        Assert.Equal("no-store", tooMany.Headers.CacheControl?.ToString());

        var c = await CreateAsync(h);
        Assert.Equal(HttpStatusCode.OK, (await Inspect(h, c.Token)).StatusCode); // інший токен не зачеплено
    }

    // ---------- ізоляція tenant ----------

    [SkippableFact]
    public async Task accepting_one_invite_does_not_touch_another_tenants_owner()
    {
        NeedDb();
        using var h = Build();
        var a = await CreateAsync(h, "a@salon.test");
        var b = await CreateAsync(h, "b@salon.test");
        Assert.Equal(HttpStatusCode.OK, (await Accept(h, a.Token)).StatusCode);
        Assert.Equal("invited", await Q<string>("SELECT state FROM users WHERE id = @u", ("u", b.OwnerId)));
        Assert.Null(await Q<DateTime?>("SELECT used_at FROM platform_owner_invites WHERE tenant_id = @t", ("t", b.TenantId)));
        Assert.Equal(HttpStatusCode.OK, (await Accept(h, b.Token)).StatusCode);
    }
}
