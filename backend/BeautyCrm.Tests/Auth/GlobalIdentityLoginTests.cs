using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Auth;
using BeautyCrm.Tests.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-863 (ADR-024, §31.2-31.6, §31.15 п. 4-12, 20, 24, 26, 30): вхід без коду бізнесу, квиток вибору, memberships, switch,
/// блокування в БД, однаковість відповідей, legacy-заголовки, sessions_valid_after. Реальний PostgreSQL + RLS (WebApplicationFactory).
/// </summary>
public sealed class GlobalIdentityLoginTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    // ---------- інфраструктура тесту ----------

    /// <summary>Лічильники викликів і хук між кроками switch (TOCTOU): обгортка над реальним <see cref="EfIdentityStore"/>.</summary>
    private sealed class Spy
    {
        public int Verifies;
        public int Failed;
        public Func<Task>? AfterMemberships;
        public void Reset() { Verifies = 0; Failed = 0; }
    }

    private sealed class CountingHasher(IPasswordHasher inner, Spy spy) : IPasswordHasher
    {
        public string Hash(string password) => inner.Hash(password);
        public bool Verify(string password, string hash)
        {
            Interlocked.Increment(ref spy.Verifies);
            return inner.Verify(password, hash);
        }
    }

    private sealed class SpyIdentityStore(IIdentityStore inner, Spy spy) : IIdentityStore
    {
        public Task<IdentityRecord?> FindAsync(string e, CancellationToken ct) => inner.FindAsync(e, ct);
        public Task<bool> IsLockedAsync(byte[] k, DateTimeOffset n, CancellationToken ct) => inner.IsLockedAsync(k, n, ct);
        public Task<bool> RegisterFailedAsync(byte[] k, Guid? i, int max, int w, int l, DateTimeOffset n, CancellationToken ct)
        {
            Interlocked.Increment(ref spy.Failed);
            return inner.RegisterFailedAsync(k, i, max, w, l, n, ct);
        }
        public Task ResetAttemptsAsync(byte[] k, CancellationToken ct) => inner.ResetAttemptsAsync(k, ct);
        public Task LoginSucceededAsync(Guid i, DateTimeOffset n, CancellationToken ct) => inner.LoginSucceededAsync(i, n, ct);
        public async Task<IReadOnlyList<MembershipRow>> MembershipsAsync(Guid i, CancellationToken ct)
        {
            var rows = await inner.MembershipsAsync(i, ct);
            if (spy.AfterMemberships is { } hook) await hook();
            return rows;
        }
        public Task<bool> IsActiveAsync(Guid i, CancellationToken ct) => inner.IsActiveAsync(i, ct);
        public Task<bool> EmailInUseAsync(string e, CancellationToken ct) => inner.EmailInUseAsync(e, ct);
        public Task IssueTicketAsync(Guid i, byte[] h, DateTimeOffset e, CancellationToken ct) => inner.IssueTicketAsync(i, h, e, ct);
        public Task<Guid?> ConsumeTicketAsync(byte[] h, Guid t, DateTimeOffset n, CancellationToken ct) => inner.ConsumeTicketAsync(h, t, n, ct);
    }

    /// <summary>Годинник із зсувом від реального: лише для тестів вікна й спливу блокування (токени з майбутнім nbf тут не перевіряються).</summary>
    private sealed class OffsetClock : TimeProvider
    {
        public TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }

    private static IPasswordHasher RealHasher() =>
        new BcryptPasswordHasher(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:BcryptWorkFactor"] = "4" }).Build());

    private HttpClient Client(Action<IWebHostBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            configure?.Invoke(b);
            if (services is not null) b.ConfigureTestServices(services);
        });
        _disposables.Add(f);
        var c = f.CreateClient();
        _disposables.Add(c);
        return c;
    }

    private HttpClient SpyClient(Spy spy, OffsetClock? clock = null) => Client(services: s =>
    {
        s.RemoveAll<IPasswordHasher>();
        s.AddSingleton<IPasswordHasher>(new CountingHasher(RealHasher(), spy));
        s.AddScoped<IIdentityStore>(sp => new SpyIdentityStore(new EfIdentityStore(sp.GetRequiredService<IConfiguration>()), spy));
        if (clock is not null) s.AddSingleton<TimeProvider>(clock);
    });

    private async Task AdminExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> AdminScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default! : (T)v;
    }

    private static string Lit(string s) => s.Replace("'", "''");

    private static string NewEmail(string prefix = "p") => $"{prefix}-{Guid.NewGuid():N}@id.test";

    private static async Task<(HttpResponseMessage Resp, JsonElement Body)> Call(
        HttpClient c, HttpMethod m, string url, object? body = null, string? bearer = null)
    {
        var resp = await c.SendAsync(Req(m, url, body, bearer));
        var text = await resp.Content.ReadAsStringAsync();
        return (resp, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? Code(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static Task<(HttpResponseMessage Resp, JsonElement Body)> LoginCall(HttpClient c, string email, string password = Password, string? tenant = null) =>
        Call(c, HttpMethod.Post, "/api/auth/login", tenant is null ? new { email, password } : new { email, password, tenant });

    private static Task<(HttpResponseMessage Resp, JsonElement Body)> SwitchCall(HttpClient c, string access, Guid tenantId, string? refresh) =>
        Call(c, HttpMethod.Post, "/api/auth/switch", new { tenantId, refreshToken = refresh }, access);

    /// <summary>Людина з власником tenant A і другим членством (admin) у tenant B. Назви: A = «Beta…», B = «alpha…» (перевірка сортування без регістру).</summary>
    private async Task<(TenantSeed A, TenantSeed B, string Email)> TwoBusinessesAsync()
    {
        var a = await fx.CreateTenantAsync();
        var b = await fx.CreateTenantAsync([BeautyModules.All[0]]);
        await AdminExecAsync($"UPDATE tenants SET name = 'Beta {a.TenantId:N}' WHERE id = '{a.TenantId}'; UPDATE tenants SET name = 'alpha {b.TenantId:N}' WHERE id = '{b.TenantId}'");
        await AddMembershipAsync(b, a.OwnerEmail, Roles.Admin);
        return (a, b, a.OwnerEmail);
    }

    private async Task AddMembershipAsync(TenantSeed t, string email, string role)
    {
        await using var conn = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await IdentitySeed.InsertUserAsync(conn, t.TenantId, email, "Member", BCrypt.Net.BCrypt.EnhancedHashPassword(Password, 4), role);
    }

    private Task<Guid> UserIdAsync(TenantSeed t, string email) =>
        AdminScalarAsync<Guid>($"SELECT id FROM users WHERE tenant_id = '{t.TenantId}' AND email = '{Lit(email)}'");

    private static JsonWebToken Jwt(string token) => new JsonWebTokenHandler().ReadJsonWebToken(token);

    // ============================== 4. Логін 0/1/N ==============================

    [SkippableFact]
    public async Task Login_without_tenant_one_membership_returns_token_response_and_no_deprecation_headers()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var (resp, body) = await LoginCall(fx.Client, t.OwnerEmail);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        Assert.False(body.TryGetProperty("selectionRequired", out _));
        Assert.Equal(t.TenantId.ToString(), Jwt(body.GetProperty("accessToken").GetString()!).GetClaim("tenant_id").Value);
        Assert.Equal("owner", body.GetProperty("user").GetProperty("role").GetString());
        Assert.StartsWith(t.TenantId.ToString("N") + ".", body.GetProperty("refreshToken").GetString());
        Assert.False(resp.Headers.Contains("Deprecation"));
        Assert.False(resp.Headers.Contains("Sunset"));
    }

    [SkippableFact]
    public async Task Login_two_memberships_returns_selection_sorted_without_slug_or_user_id()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        var (resp, body) = await LoginCall(fx.Client, email);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        Assert.True(body.GetProperty("selectionRequired").GetBoolean());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.Matches(@"^[A-Za-z0-9_-]{43}$", body.GetProperty("loginTicket").GetString()!);
        Assert.Equal(180, body.GetProperty("ticketExpiresInSeconds").GetInt32());
        var list = body.GetProperty("businesses").EnumerateArray().ToList();
        Assert.Equal([b.TenantId, a.TenantId], list.Select(x => x.GetProperty("tenantId").GetGuid())); // «alpha» перед «Beta»
        Assert.Equal(["admin", "owner"], list.Select(x => x.GetProperty("role").GetString()));
        Assert.All(list, x => Assert.Equal(["tenantId", "name", "role"], x.EnumerateObject().Select(p => p.Name)));
        Assert.DoesNotContain(a.Slug, body.ToString());
        Assert.DoesNotContain((await UserIdAsync(a, email)).ToString(), body.ToString());
    }

    [SkippableFact]
    public async Task Suspended_tenant_and_disabled_membership_are_not_listed_so_login_falls_back_to_one_membership()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{b.TenantId}", new { status = "suspended" }, platformKey: PlatformKey);
        var (_, suspended) = await LoginCall(fx.Client, email);
        Assert.Equal(a.TenantId.ToString(), Jwt(suspended.GetProperty("accessToken").GetString()!).GetClaim("tenant_id").Value);

        await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{b.TenantId}", new { status = "active" }, platformKey: PlatformKey);
        var (_, both) = await LoginCall(fx.Client, email);
        Assert.True(both.GetProperty("selectionRequired").GetBoolean());

        await AdminExecAsync($"UPDATE users SET is_active = false WHERE tenant_id = '{b.TenantId}' AND email = '{Lit(email)}'");
        var (_, disabled) = await LoginCall(fx.Client, email);
        Assert.Equal(a.TenantId.ToString(), Jwt(disabled.GetProperty("accessToken").GetString()!).GetClaim("tenant_id").Value);
    }

    [SkippableFact]
    public async Task Correct_password_with_zero_active_memberships_is_the_same_401_as_a_wrong_password()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        await AdminExecAsync($"UPDATE users SET is_active = false WHERE tenant_id = '{t.TenantId}'");
        var (zero, zeroBody) = await LoginCall(fx.Client, t.OwnerEmail);
        var (wrong, wrongBody) = await LoginCall(fx.Client, t.OwnerEmail, "Wrong-Password-123456");

        Assert.Equal(HttpStatusCode.Unauthorized, zero.StatusCode);
        Assert.Equal(wrong.StatusCode, zero.StatusCode);
        Assert.Equal("invalid_credentials", Code(zeroBody));
        Assert.Equal(wrongBody.ToString(), zeroBody.ToString());
    }

    // ============================== 5. Однаковість ==============================

    [SkippableFact]
    public async Task Every_failure_path_has_same_response_exactly_one_bcrypt_verify_and_one_failed_attempt()
    {
        NeedDb();
        var spy = new Spy();
        var c = SpyClient(spy);

        var other = await fx.CreateTenantAsync();
        var noMembership = await fx.CreateTenantAsync();
        await AdminExecAsync($"UPDATE users SET is_active = false WHERE tenant_id = '{noMembership.TenantId}'");
        var legacy = await fx.CreateTenantAsync();
        var pending = NewEmail("pending");
        await AdminExecAsync($"INSERT INTO identities (email, password_hash, state) VALUES ('{pending}', NULL, 'pending')");
        var real = await fx.CreateTenantAsync();

        var cases = new (string Name, string Email, string Password, string? Tenant)[]
        {
            ("no email", NewEmail("ghost"), Password, null),
            ("pending", pending, Password, null),
            ("wrong password", real.OwnerEmail, "Wrong-Password-123456", null),
            ("0 memberships", noMembership.OwnerEmail, Password, null),
            ("legacy without membership", legacy.OwnerEmail, Password, other.Slug),
        };
        var bodies = new List<string>();
        foreach (var (name, email, password, tenant) in cases)
        {
            spy.Reset();
            var (resp, body) = await LoginCall(c, email, password, tenant);
            Assert.True(HttpStatusCode.Unauthorized == resp.StatusCode, name);
            Assert.True(spy.Verifies == 1, $"{name}: Verify calls = {spy.Verifies}");
            Assert.True(spy.Failed == 1, $"{name}: failed-attempt calls = {spy.Failed}");
            Assert.Equal("invalid_credentials", Code(body));
            bodies.Add(body.ToString());
        }
        Assert.Single(bodies.Distinct());
    }

    [SkippableFact]
    public async Task Unknown_pending_and_known_email_lock_with_identical_sequence_after_five_failures()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var pending = NewEmail("pending");
        await AdminExecAsync($"INSERT INTO identities (email, password_hash, state) VALUES ('{pending}', NULL, 'pending')");
        var emails = new[] { t.OwnerEmail, NewEmail("ghost"), pending };
        var sequences = new List<string>();
        var bodies = new List<string>();
        foreach (var email in emails)
        {
            var seq = new List<int>();
            for (var i = 0; i < 6; i++)
            {
                var (resp, body) = await LoginCall(fx.Client, email, "Wrong-Password-123456");
                seq.Add((int)resp.StatusCode);
                if (i == 5) bodies.Add(body.ToString());
            }
            sequences.Add(string.Join(",", seq));
        }
        Assert.Equal(["401,401,401,401,401,423"], sequences.Distinct());
        Assert.Single(bodies.Distinct()); // однакове тіло 423
    }

    // ============================== 6, 20. Блокування ==============================

    [SkippableFact]
    public async Task Five_failures_lock_every_business_of_the_person_even_for_the_correct_password_and_legacy_tenant()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginCall(fx.Client, email, "Wrong-Password-123456")).Resp.StatusCode);

        foreach (var tenant in new string?[] { null, a.Slug, b.Slug })
        {
            var (resp, body) = await LoginCall(fx.Client, email, Password, tenant);
            Assert.Equal((HttpStatusCode)423, resp.StatusCode);
            Assert.Equal("account_locked", Code(body));
        }
    }

    [SkippableFact]
    public async Task Q_C1_zero_membership_and_legacy_slug_without_membership_count_as_failed_attempts_like_a_wrong_password()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var other = await fx.CreateTenantAsync();
        for (var i = 0; i < 5; i++)
        {
            var (resp, body) = await LoginCall(fx.Client, t.OwnerEmail, Password, other.Slug); // правильний пароль, чужий slug
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("invalid_credentials", Code(body));
        }
        Assert.Equal((HttpStatusCode)423, (await LoginCall(fx.Client, t.OwnerEmail, Password, t.Slug)).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Successful_login_deletes_the_counter_row_and_the_key_is_an_hmac_without_the_email()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var email = t.OwnerEmail;
        var hmac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Pepper), Encoding.UTF8.GetBytes(email));
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(email));
        string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        for (var i = 0; i < 2; i++) await LoginCall(fx.Client, email, "Wrong-Password-123456");
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_attempts WHERE login_key = decode('{Hex(hmac)}', 'hex')"));
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_attempts WHERE login_key = decode('{Hex(sha)}', 'hex')")); // не sha256(email)
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_attempts a WHERE (a.*)::text ILIKE '%{Lit(email)}%'"));

        Assert.Equal(HttpStatusCode.OK, (await LoginCall(fx.Client, email)).Resp.StatusCode);
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_attempts WHERE login_key = decode('{Hex(hmac)}', 'hex')"));
    }

    [SkippableFact]
    public async Task Locking_writes_identity_locked_audit_once_without_the_email_and_only_for_existing_identities()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var ghost = NewEmail("ghost");
        for (var i = 0; i < 5; i++)
        {
            await LoginCall(fx.Client, t.OwnerEmail, "Wrong-Password-123456");
            await LoginCall(fx.Client, ghost, "Wrong-Password-123456");
        }
        var id = await AdminScalarAsync<Guid>($"SELECT id FROM identities WHERE email = '{Lit(t.OwnerEmail)}'");
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM platform_audit_log WHERE action = 'identity.locked' AND details ->> 'identityId' = '{id}'"));
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM platform_audit_log WHERE details::text ILIKE '%{Lit(t.OwnerEmail)}%' OR details::text ILIKE '%{Lit(ghost)}%'"));
        // невідома пошта блокується так само (рядок лічильника є), але в аудиті її немає
        var ghostKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Pepper), Encoding.UTF8.GetBytes(ghost));
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_attempts WHERE login_key = decode('{Convert.ToHexString(ghostKey)}', 'hex') AND locked_until > now()"));
    }

    [SkippableFact]
    public async Task Lock_expires_and_the_window_resets_the_counter_for_existing_and_unknown_email()
    {
        NeedDb();
        var clock = new OffsetClock();
        var c = SpyClient(new Spy(), clock);
        var t = await fx.CreateTenantAsync();
        var ghost = NewEmail("ghost");
        const string wrong = "Wrong-Password-123456";

        // блокування спливає (15 хв): лічильник з нуля
        for (var i = 0; i < 5; i++) await LoginCall(c, t.OwnerEmail, wrong);
        Assert.Equal((HttpStatusCode)423, (await LoginCall(c, t.OwnerEmail)).Resp.StatusCode);
        clock.Offset = TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(2);
        Assert.Equal(HttpStatusCode.OK, (await LoginCall(c, t.OwnerEmail)).Resp.StatusCode);

        // спроби, розділені більш ніж вікном (900 с), не накопичуються
        clock.Offset = TimeSpan.Zero;
        var t2 = await fx.CreateTenantAsync();
        foreach (var email in new[] { t2.OwnerEmail, ghost })
        {
            clock.Offset = TimeSpan.Zero;
            for (var i = 0; i < 4; i++) await LoginCall(c, email, wrong);
            clock.Offset = TimeSpan.FromSeconds(901);
            for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await LoginCall(c, email, wrong)).Resp.StatusCode);
            // 8 спроб загалом, але лише 4 у новому вікні: ще не заблоковано (правильний пароль проходить / невідома пошта дає 401, а не 423)
            var (resp, _) = await LoginCall(c, email, email == ghost ? wrong : Password);
            Assert.Equal(email == ghost ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Lock_survives_an_api_restart_and_is_shared_by_two_instances()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var first = Client();
        var second = Client(); // окремий WebApplicationFactory на тій самій БД = інший інстанс/рестарт
        for (var i = 0; i < 3; i++) await LoginCall(first, t.OwnerEmail, "Wrong-Password-123456");
        for (var i = 0; i < 2; i++) await LoginCall(second, t.OwnerEmail, "Wrong-Password-123456"); // лічильник спільний
        var third = Client();
        Assert.Equal((HttpStatusCode)423, (await LoginCall(third, t.OwnerEmail)).Resp.StatusCode);
    }

    // ============================== 7. Ліміт пошти ==============================

    [SkippableFact]
    public async Task Email_throttle_returns_429_with_retry_after_for_known_and_unknown_email_alike()
    {
        NeedDb();
        var c = Client(b => b.UseSetting("Auth:LoginEmailThrottle:PermitLimit", "3"));
        var t = await fx.CreateTenantAsync();
        foreach (var email in new[] { t.OwnerEmail, NewEmail("ghost") })
        {
            for (var i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await LoginCall(c, email, "Wrong-Password-123456")).Resp.StatusCode);
            var (resp, body) = await LoginCall(c, email, "Wrong-Password-123456");
            Assert.Equal((HttpStatusCode)429, resp.StatusCode);
            Assert.Equal("rate_limited", Code(body));
            Assert.True(resp.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        }
    }

    // ============================== 9. Legacy tenant ==============================

    [SkippableFact]
    public async Task Legacy_tenant_returns_token_response_even_with_two_memberships_and_deprecation_headers_on_200_and_401()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        var (ok, body) = await LoginCall(fx.Client, email, Password, b.Slug.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(body.TryGetProperty("selectionRequired", out _));
        Assert.Equal(b.TenantId.ToString(), Jwt(body.GetProperty("accessToken").GetString()!).GetClaim("tenant_id").Value);

        var (bad, _) = await LoginCall(fx.Client, email, "Wrong-Password-123456", a.Slug);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        foreach (var r in new[] { ok, bad })
        {
            Assert.Equal("@" + DeprecatedAt.ToUnixTimeSeconds(), r.Headers.GetValues("Deprecation").Single());
            Assert.Equal(SunsetAt.ToString("R"), r.Headers.GetValues("Sunset").Single());
        }
        // порожнє tenant (після Trim) = нова форма: заголовків немає
        var (blank, _) = await LoginCall(fx.Client, email, Password, "   ");
        Assert.False(blank.Headers.Contains("Deprecation"));
        Assert.False(blank.Headers.Contains("Sunset"));
    }

    [SkippableTheory]
    [InlineData(null, "Auth:LoginAttemptKeyPepper")]
    [InlineData("short-pepper", "at least 32 bytes")]
    public void Outside_development_startup_requires_a_valid_login_key_pepper(string? pepper, string expected)
    {
        NeedDb();
        var ex = Assert.ThrowsAny<Exception>(() => Client(b => b.UseSetting("Auth:LoginAttemptKeyPepper", pepper)));
        Assert.Contains(expected, ex.ToString());
    }

    [SkippableTheory]
    [InlineData("Auth:LoginAttemptWindowSeconds", "59", "LoginAttemptWindowSeconds")]
    [InlineData("Auth:LoginAttemptWindowSeconds", "86401", "LoginAttemptWindowSeconds")]
    [InlineData("Auth:LoginAttemptWindowSeconds", "0", "LoginAttemptWindowSeconds")]
    [InlineData("Auth:MaxFailedAttempts", "0", "MaxFailedAttempts")]
    [InlineData("Auth:MaxFailedAttempts", "-1", "MaxFailedAttempts")]
    public void Startup_rejects_out_of_range_lockout_options_R4(string key, string value, string expected)
    {
        NeedDb();
        var ex = Assert.ThrowsAny<Exception>(() => Client(b => b.UseSetting(key, value)));
        Assert.Contains(expected, ex.ToString());
    }

    [SkippableTheory]
    [InlineData("Auth:LoginAttemptWindowSeconds", "60")]
    [InlineData("Auth:LoginAttemptWindowSeconds", "86400")]
    [InlineData("Auth:MaxFailedAttempts", "1")]
    public void Startup_accepts_boundary_lockout_options_R4(string key, string value)
    {
        NeedDb();
        Client(b => b.UseSetting(key, value));
    }

    [Theory]
    [InlineData(59, 5, false)]
    [InlineData(60, 5, true)]
    [InlineData(86400, 1, true)]
    [InlineData(86401, 5, false)]
    [InlineData(900, 0, false)]
    public void ValidateIdentityOptions_enforces_window_and_attempt_bounds_R4(int window, int attempts, bool ok)
    {
        var o = new AuthOptions
        {
            LoginAttemptKeyPepper = new string('p', 32), LoginAttemptWindowSeconds = window, MaxFailedAttempts = attempts,
        };
        if (ok) Assert.Same(o, BeautyCrm.Api.Auth.AuthSetup.ValidateIdentityOptions(o, isDevelopment: true));
        else Assert.Throws<InvalidOperationException>(() => BeautyCrm.Api.Auth.AuthSetup.ValidateIdentityOptions(o, isDevelopment: true));
    }

    [SkippableTheory]
    [InlineData("Auth:LegacyTenantLogin:DeprecatedAt")]
    [InlineData("Auth:LegacyTenantLogin:SunsetAt")]
    public void Outside_development_startup_requires_both_legacy_login_dates(string missingKey)
    {
        NeedDb();
        var ex = Assert.ThrowsAny<Exception>(() => Client(b => b.UseSetting(missingKey, null)));
        Assert.Contains("LegacyTenantLogin", ex.ToString());
    }

    // ============================== 3. Квиток входу / select ==============================

    private async Task<(TenantSeed A, TenantSeed B, string Email, string Ticket)> TicketAsync()
    {
        var (a, b, email) = await TwoBusinessesAsync();
        var (_, body) = await LoginCall(fx.Client, email);
        return (a, b, email, body.GetProperty("loginTicket").GetString()!);
    }

    private static byte[] TicketHash(string ticket) => SHA256.HashData(Convert.FromBase64String(ticket.Replace('-', '+').Replace('_', '/') + "="));

    private Task<(HttpResponseMessage Resp, JsonElement Body)> Select(string ticket, Guid tenantId) =>
        Call(fx.Client, HttpMethod.Post, "/api/auth/login/select", new { loginTicket = ticket, tenantId });

    [SkippableFact]
    public async Task Select_with_a_valid_ticket_returns_tokens_for_the_chosen_business_and_stores_only_the_hash()
    {
        NeedDb();
        var (a, b, email, ticket) = await TicketAsync();
        var hex = Convert.ToHexString(TicketHash(ticket)).ToLowerInvariant();
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_tickets WHERE token_hash = decode('{hex}', 'hex')"));
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_tickets t WHERE (t.*)::text LIKE '%{ticket}%'"));

        var (resp, body) = await Select(ticket, b.TenantId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        var jwt = Jwt(body.GetProperty("accessToken").GetString()!);
        Assert.Equal(b.TenantId.ToString(), jwt.GetClaim("tenant_id").Value);
        Assert.Equal((await UserIdAsync(b, email)).ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal("admin", body.GetProperty("user").GetProperty("role").GetString());
        Assert.NotNull(await AdminScalarAsync<DateTime?>($"SELECT last_login_at FROM users WHERE tenant_id = '{b.TenantId}' AND email = '{Lit(email)}'"));

        var (replay, replayBody) = await Select(ticket, b.TenantId); // одноразовий
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("login_ticket_invalid", Code(replayBody));
    }

    [SkippableFact]
    public async Task Select_with_a_foreign_tenant_fails_and_still_consumes_the_ticket_one_attempt_per_ticket()
    {
        NeedDb();
        var (_, b, _, ticket) = await TicketAsync();
        var stranger = await fx.CreateTenantAsync();
        var (resp, body) = await Select(ticket, stranger.TenantId);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("login_ticket_invalid", Code(body));
        // Q-C5: чинний квиток погашено навіть невдалою спробою
        Assert.Equal(HttpStatusCode.Unauthorized, (await Select(ticket, b.TenantId)).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Select_with_an_empty_tenant_id_is_401_and_still_consumes_a_valid_ticket_R6()
    {
        NeedDb();
        var (_, b, _, ticket) = await TicketAsync();
        var hex = Convert.ToHexString(TicketHash(ticket)).ToLowerInvariant();
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_tickets WHERE token_hash = decode('{hex}', 'hex') AND consumed_at IS NULL"));

        var (resp, body) = await Select(ticket, Guid.Empty);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("login_ticket_invalid", Code(body));
        // Q-C5: квиток погашено, хоча tenantId порожній; повтор із правильним бізнесом уже не працює
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_tickets WHERE token_hash = decode('{hex}', 'hex') AND consumed_at IS NOT NULL"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Select(ticket, b.TenantId)).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Select_rejects_expired_superseded_and_disabled_membership_tickets_with_the_same_answer()
    {
        NeedDb();
        var answers = new List<string>();

        var (_, b1, _, expired) = await TicketAsync();
        await AdminExecAsync($"UPDATE auth_login_tickets SET expires_at = now() - interval '1 second' WHERE token_hash = decode('{Convert.ToHexString(TicketHash(expired))}', 'hex')");
        answers.Add((await Select(expired, b1.TenantId)).Body.ToString());

        var (_, b2, email2, first) = await TicketAsync();
        await LoginCall(fx.Client, email2); // нова видача гасить попередній квиток
        answers.Add((await Select(first, b2.TenantId)).Body.ToString());

        var (_, b3, email3, disabled) = await TicketAsync();
        await AdminExecAsync($"UPDATE users SET is_active = false WHERE tenant_id = '{b3.TenantId}' AND email = '{Lit(email3)}'"); // вимкнено між кроками
        answers.Add((await Select(disabled, b3.TenantId)).Body.ToString());

        var (_, b4, _, suspended) = await TicketAsync();
        await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{b4.TenantId}", new { status = "suspended" }, platformKey: PlatformKey);
        answers.Add((await Select(suspended, b4.TenantId)).Body.ToString());

        Assert.Single(answers.Distinct());
        Assert.Equal("login_ticket_invalid", JsonDocument.Parse(answers[0]).RootElement.GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Select_with_malformed_input_is_401_for_a_bad_ticket_and_422_for_a_bad_body()
    {
        NeedDb();
        var tenant = Guid.NewGuid();
        foreach (var ticket in new[] { "abc", new string('A', 43) + "A", new string('!', 43), new string('A', 43) })
        {
            var (resp, body) = await Select(ticket, tenant);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("login_ticket_invalid", Code(body));
        }
        Assert.Equal((HttpStatusCode)422, (await Call(fx.Client, HttpMethod.Post, "/api/auth/login/select", new { })).Resp.StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Call(fx.Client, HttpMethod.Post, "/api/auth/login/select", new { loginTicket = new string('A', 43), tenantId = "not-a-guid" })).Resp.StatusCode);
    }

    // ============================== 11. memberships ==============================

    [SkippableFact]
    public async Task Memberships_lists_only_the_identitys_active_businesses_sorted_with_current_flag_and_no_store()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        var stranger = await fx.CreateTenantAsync();
        var token = (await fx.LoginAsync(a.Slug, email)).AccessToken;

        var (resp, body) = await Call(fx.Client, HttpMethod.Get, "/api/auth/memberships", bearer: token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        var items = body.EnumerateArray().ToList();
        Assert.Equal([b.TenantId, a.TenantId], items.Select(x => x.GetProperty("tenantId").GetGuid()));
        Assert.Equal([false, true], items.Select(x => x.GetProperty("current").GetBoolean()));
        Assert.All(items, x => Assert.Equal(["tenantId", "name", "role", "current"], x.EnumerateObject().Select(p => p.Name)));
        Assert.DoesNotContain(stranger.TenantId, items.Select(x => x.GetProperty("tenantId").GetGuid()));

        await AdminExecAsync($"UPDATE users SET is_active = false WHERE tenant_id = '{b.TenantId}' AND email = '{Lit(email)}'");
        var (_, after) = await Call(fx.Client, HttpMethod.Get, "/api/auth/memberships", bearer: token);
        Assert.Equal([a.TenantId], after.EnumerateArray().Select(x => x.GetProperty("tenantId").GetGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(fx.Client, HttpMethod.Get, "/api/auth/memberships")).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Inactive_specialist_profile_does_not_block_memberships_or_switch()
    {
        NeedDb();
        var (a, b, _) = await TwoBusinessesAsync();
        var seed = await fx.SeedBeautyAsync(a.TenantId);
        var spec = await fx.CreateUserAsync(a, Roles.Specialist, seed.SpecialistA);
        await AddMembershipAsync(b, spec.Email, Roles.Admin);
        await AdminExecAsync($"UPDATE beauty_specialists SET is_active = false WHERE id = '{seed.SpecialistA}'");

        Assert.Equal(HttpStatusCode.OK, (await Call(fx.Client, HttpMethod.Get, "/api/auth/memberships", bearer: spec.Tokens.AccessToken)).Resp.StatusCode);
        var (resp, body) = await SwitchCall(fx.Client, spec.Tokens.AccessToken, b.TenantId, spec.Tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(b.TenantId.ToString(), Jwt(body.GetProperty("accessToken").GetString()!).GetClaim("tenant_id").Value);
    }

    // ============================== 10. switch ==============================

    [SkippableFact]
    public async Task Switch_issues_tokens_for_the_target_membership_with_its_own_rights_and_revokes_the_old_refresh()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        var current = await fx.LoginAsync(a.Slug, email);

        var (resp, body) = await SwitchCall(fx.Client, current.AccessToken, b.TenantId, current.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        var newAccess = body.GetProperty("accessToken").GetString()!;
        var jwt = Jwt(newAccess);
        Assert.Equal(b.TenantId.ToString(), jwt.GetClaim("tenant_id").Value);
        Assert.Equal((await UserIdAsync(b, email)).ToString(), jwt.GetClaim("sub").Value);
        Assert.StartsWith(b.TenantId.ToString("N") + ".", body.GetProperty("refreshToken").GetString());

        // права й accessStamp - цільового членства (ADR-023): admin у tenant B з одним модулем
        var (meResp, me) = await Call(fx.Client, HttpMethod.Get, "/api/auth/me", bearer: newAccess);
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        Assert.Equal("admin", me.GetProperty("role").GetString());
        Assert.Equal([BeautyModules.All[0]], me.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(me.GetProperty("accessStamp").GetString(), meResp.Headers.GetValues("X-Access-Version").Single());

        // старий refresh відкликано; повторний switch з ним - 401
        var (replay, replayBody) = await SwitchCall(fx.Client, current.AccessToken, b.TenantId, current.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_token", Code(replayBody));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(fx.Client, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = current.RefreshToken })).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Switch_to_a_non_member_disabled_suspended_or_missing_tenant_is_one_identical_404()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        var disabled = await fx.CreateTenantAsync();
        await AddMembershipAsync(disabled, email, Roles.Admin);
        await AdminExecAsync($"UPDATE users SET is_active = false WHERE tenant_id = '{disabled.TenantId}' AND email = '{Lit(email)}'");
        var stranger = await fx.CreateTenantAsync();
        var current = await fx.LoginAsync(a.Slug, email);
        await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{b.TenantId}", new { status = "suspended" }, platformKey: PlatformKey);

        var bodies = new List<string>();
        foreach (var target in new[] { stranger.TenantId, disabled.TenantId, b.TenantId, Guid.NewGuid() })
        {
            var (resp, body) = await SwitchCall(fx.Client, current.AccessToken, target, current.RefreshToken);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("membership_not_found", Code(body));
            bodies.Add(body.ToString());
        }
        Assert.Single(bodies.Distinct());
        // поточний refresh не відкликано: refresh працює
        Assert.Equal(HttpStatusCode.OK, (await Call(fx.Client, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = current.RefreshToken })).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Switch_with_missing_foreign_revoked_or_other_tenants_refresh_is_401_without_cascade_and_same_tenant_is_422()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        var current = await fx.LoginAsync(a.Slug, email);
        var colleague = await fx.CreateUserAsync(a, Roles.Admin); // чужий refresh того самого tenant
        var other = await fx.LoginAsync(b.Slug, email); // refresh іншого tenant (B) у сесії A
        var revoked = await fx.LoginAsync(a.Slug, email);
        await Call(fx.Client, HttpMethod.Post, "/api/auth/logout", new { refreshToken = revoked.RefreshToken });

        foreach (var refresh in new string?[] { null, "", "garbage", colleague.Tokens.RefreshToken, other.RefreshToken, revoked.RefreshToken })
        {
            var (resp, body) = await SwitchCall(fx.Client, current.AccessToken, b.TenantId, refresh);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("invalid_token", Code(body));
        }
        var (same, sameBody) = await SwitchCall(fx.Client, current.AccessToken, a.TenantId, current.RefreshToken);
        Assert.Equal((HttpStatusCode)422, same.StatusCode);
        Assert.Equal("same_tenant", Code(sameBody));
        // без каскадного відкликання: власна сесія жива
        Assert.Equal(HttpStatusCode.OK, (await Call(fx.Client, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = current.RefreshToken })).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(fx.Client, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = colleague.Tokens.RefreshToken })).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Switch_rereads_the_target_membership_in_its_own_scope_so_a_disable_between_steps_gives_404_and_keeps_the_refresh()
    {
        NeedDb();
        var spy = new Spy();
        var c = SpyClient(spy);
        var (a, b, email) = await TwoBusinessesAsync();
        var current = await fx.LoginAsync(a.Slug, email);

        // членство цілі вимикається ПІСЛЯ кроку 5 (список членств) і ДО кроку 6 (перечитування під RLS цілі)
        spy.AfterMemberships = () => AdminExecAsync($"UPDATE users SET is_active = false WHERE tenant_id = '{b.TenantId}' AND email = '{Lit(email)}'");
        var (resp, body) = await SwitchCall(c, current.AccessToken, b.TenantId, current.RefreshToken);
        spy.AfterMemberships = null;

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("membership_not_found", Code(body));
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = current.RefreshToken })).Resp.StatusCode);
    }

    // ============================== 12, 24. refresh і sessions_valid_after ==============================

    [SkippableFact]
    public async Task Refresh_is_401_when_the_identity_is_not_active()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var session = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        await AdminExecAsync($"UPDATE identities SET state = 'pending', password_hash = NULL WHERE email = '{Lit(t.OwnerEmail)}'");
        var (resp, body) = await Call(fx.Client, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("invalid_token", Code(body));
    }

    [SkippableFact]
    public async Task Sessions_valid_after_revokes_older_access_tokens_and_refresh_but_not_newly_issued_ones()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var old = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await Call(fx.Client, HttpMethod.Get, "/api/auth/me", bearer: old.AccessToken)).Resp.StatusCode); // NULL = як раніше

        await Task.Delay(1100); // iat і sessions_valid_after порівнюються в секундах
        await AdminExecAsync($"UPDATE users SET sessions_valid_after = now() WHERE tenant_id = '{t.TenantId}'");

        var (meResp, me) = await Call(fx.Client, HttpMethod.Get, "/api/auth/me", bearer: old.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, meResp.StatusCode); // на першому ж запиті, а не через 15 хв
        Assert.Equal("session_revoked", Code(me));
        var (refreshResp, refreshBody) = await Call(fx.Client, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = old.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);
        Assert.Equal("invalid_token", Code(refreshBody));

        var fresh = await fx.LoginAsync(t.Slug, t.OwnerEmail); // виданий після позначки: працює
        Assert.Equal(HttpStatusCode.OK, (await Call(fx.Client, HttpMethod.Get, "/api/auth/me", bearer: fresh.AccessToken)).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(fx.Client, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = fresh.RefreshToken })).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Switch_is_401_when_the_current_refresh_token_was_created_before_sessions_valid_after_R8()
    {
        NeedDb();
        var (a, b, email) = await TwoBusinessesAsync();
        var current = await fx.LoginAsync(a.Slug, email);
        var userId = await UserIdAsync(a, email);
        // свіжий access-токен проходить middleware (iat пізніше за позначку), але refresh створено до неї
        await AdminExecAsync($"UPDATE refresh_tokens SET created_at = now() - interval '2 hours' WHERE user_id = '{userId}'");
        await AdminExecAsync($"UPDATE users SET sessions_valid_after = now() - interval '1 hour' WHERE id = '{userId}'");

        var (resp, body) = await SwitchCall(fx.Client, current.AccessToken, b.TenantId, current.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("invalid_token", Code(body));
        Assert.False(body.TryGetProperty("accessToken", out _));
        // refresh не відкликано switch-ом (відмова до кроку 7), але refresh-ендпоінт теж його відхиляє за тією ж ознакою
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM refresh_tokens WHERE user_id = '{userId}' AND revoked_at IS NOT NULL"));

        // контроль: позначка у минулому відносно створення токена не заважає
        await AdminExecAsync($"UPDATE users SET sessions_valid_after = now() - interval '3 hours' WHERE id = '{userId}'");
        Assert.Equal(HttpStatusCode.OK, (await SwitchCall(fx.Client, current.AccessToken, b.TenantId, current.RefreshToken)).Resp.StatusCode);
    }

    // ============================== §31.12: аудит створення ідентичності ==============================

    [SkippableFact]
    public async Task Owner_provisioning_through_the_platform_writes_identity_created_with_tenant_and_via_but_no_email()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var row = await AdminScalarAsync<string>(
            $"SELECT details::text FROM platform_audit_log WHERE action = 'identity.created' AND tenant_id = '{t.TenantId}'");
        Assert.Contains("\"via\": \"platform\"", row);
        Assert.DoesNotContain(t.OwnerEmail, row);
        Assert.Equal(1L, await AdminScalarAsync<long>($"SELECT count(*) FROM platform_audit_log WHERE action = 'identity.created' AND tenant_id = '{t.TenantId}'"));
    }

    // ============================== 26, 30. DTO, JWT, фіктивний хеш ==============================

    [Fact]
    public void No_auth_dto_exposes_an_identity_id_and_the_jwt_has_no_identity_claim()
    {
        var types = new[]
        {
            typeof(LoginRequest), typeof(LoginSelectRequest), typeof(SwitchRequest), typeof(RefreshRequest), typeof(LoginSelectionResponse),
            typeof(BusinessOption), typeof(MembershipDto), typeof(TokenResponse), typeof(UserDto),
        };
        foreach (var type in types)
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Access_token_has_no_identity_claim()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var jwt = Jwt((await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("4", "$2a$04$")]
    [InlineData("6", "$2a$06$")]
    public void Dummy_hash_uses_the_same_bcrypt_cost_as_the_configured_hasher(string workFactor, string prefix)
    {
        var hasher = new BcryptPasswordHasher(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:BcryptWorkFactor"] = workFactor }).Build());
        var dummy = new LoginDummyHash(hasher).Value;
        Assert.StartsWith(prefix, dummy);
        Assert.StartsWith(prefix, hasher.Hash("anything"));
        Assert.Matches(new Regex(@"^\$2[abxy]\$\d\d\$"), dummy);
    }

    [Fact]
    public void Login_key_is_a_32_byte_hmac_that_depends_on_the_pepper_and_hides_the_email()
    {
        var k1 = LoginKey.Compute(Pepper, "a@x.test");
        Assert.Equal(32, k1.Length);
        Assert.Equal(k1, LoginKey.Compute(Pepper, "a@x.test"));
        Assert.NotEqual(k1, LoginKey.Compute(Pepper + "x", "a@x.test"));
        Assert.NotEqual(k1, SHA256.HashData(Encoding.UTF8.GetBytes("a@x.test")));
        Assert.False(LoginKey.IsValidPepper(null));
        Assert.False(LoginKey.IsValidPepper(new string('p', 31)));
        Assert.True(LoginKey.IsValidPepper(new string('p', 32)));
    }
}
