using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Tests.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-865 (ADR-024, §31.7, §31.9, §31.12, §31.15 п. 7, 15, 21): forgot (202 до роботи з БД, in-process черга, тихий ліміт пошти)
/// і reset (одноразовість, TTL, weak_password не споживає токен, відкликання сесій, зняття блокування). Токен у БД ставить воркер,
/// тож тут його імітує прямий UPDATE token_hash (API сирого токена не бачить).
/// </summary>
public sealed class PasswordResetTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string NewPassword = "Another-Strong-Pass-1";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    // ---------- інфраструктура ----------

    private sealed class FakeResetStore : IPasswordResetStore
    {
        public int Entered;
        public readonly List<(string Email, int KeyLength, DateTimeOffset Expires)> Issued = [];
        public SemaphoreSlim? Gate;
        public async Task IssueAsync(string email, byte[] key, DateTimeOffset expires, CancellationToken ct)
        {
            Interlocked.Increment(ref Entered);
            if (Gate is not null) await Gate.WaitAsync(ct);
            lock (Issued) Issued.Add((email, key.Length, expires));
        }
        public Task<Guid?> ConsumeAsync(byte[] h, string p, DateTimeOffset n, CancellationToken ct) => throw new InvalidOperationException();
    }

    /// <summary>Будь-який виклик у потоці запиту forgot = помилка: store ідентичностей і tenant не мають торкатися БД.</summary>
    private sealed class ForbiddenIdentityStore : IIdentityStore
    {
        public int Calls;
        private Task<T> Hit<T>() { Interlocked.Increment(ref Calls); throw new InvalidOperationException("DB touched in forgot request flow"); }
        public Task<IdentityRecord?> FindAsync(string e, CancellationToken ct) => Hit<IdentityRecord?>();
        public Task<bool> IsLockedAsync(byte[] k, DateTimeOffset n, CancellationToken ct) => Hit<bool>();
        public Task<bool> RegisterFailedAsync(byte[] k, Guid? i, int m, int w, int l, DateTimeOffset n, CancellationToken ct) => Hit<bool>();
        public Task ResetAttemptsAsync(byte[] k, CancellationToken ct) => Hit<bool>();
        public Task LoginSucceededAsync(Guid i, DateTimeOffset n, CancellationToken ct) => Hit<bool>();
        public Task<IReadOnlyList<MembershipRow>> MembershipsAsync(Guid i, CancellationToken ct) => Hit<IReadOnlyList<MembershipRow>>();
        public Task<bool> IsActiveAsync(Guid i, CancellationToken ct) => Hit<bool>();
        public Task<bool> EmailInUseAsync(string e, CancellationToken ct) => Hit<bool>();
        public Task IssueTicketAsync(Guid i, byte[] h, DateTimeOffset e, CancellationToken ct) => Hit<bool>();
        public Task<Guid?> ConsumeTicketAsync(byte[] h, Guid t, DateTimeOffset n, CancellationToken ct) => Hit<Guid?>();
    }

    private sealed class CapturingProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string category) => new L(lines);
        public void Dispose() { }
        private sealed class L(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines) lines.Add(formatter(state, exception) + exception);
            }
        }
    }

    private HttpClient Client(Action<IWebHostBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:PasswordForgot:PerIpPermit", "1000");
            configure?.Invoke(b);
            if (services is not null) b.ConfigureTestServices(services);
        });
        _disposables.Add(f);
        var c = f.CreateClient();
        _disposables.Add(c);
        return c;
    }

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
    private static string NewEmail(string p = "r") => $"{p}-{Guid.NewGuid():N}@reset.test";

    private static async Task<(HttpResponseMessage Resp, string Text, JsonElement Body)> Call(HttpClient c, HttpMethod m, string url, object? body = null, string? bearer = null)
    {
        var resp = await c.SendAsync(Req(m, url, body, bearer));
        var text = await resp.Content.ReadAsStringAsync();
        return (resp, text, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? Code(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static Task<(HttpResponseMessage Resp, string Text, JsonElement Body)> Forgot(HttpClient c, string? email) =>
        Call(c, HttpMethod.Post, "/api/auth/password/forgot", new { email });

    private static Task<(HttpResponseMessage Resp, string Text, JsonElement Body)> Reset(HttpClient c, string? token, string? password) =>
        Call(c, HttpMethod.Post, "/api/auth/password/reset", new { token, password });

    private static async Task WaitAsync(Func<Task<bool>> cond, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await cond()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for: " + what);
    }

    private Task<long> RowCountAsync(string email) =>
        AdminScalarAsync<long>($"SELECT count(*) FROM auth_password_resets r JOIN identities i ON i.id = r.identity_id WHERE i.email = '{Lit(email)}'");

    /// <summary>Черга FIFO з одним читачем: коли з'явився рядок маркера, усі попередні запити вже оброблено.</summary>
    private async Task FlushAsync(HttpClient c)
    {
        var t = await fx.CreateTenantAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(c, t.OwnerEmail)).Resp.StatusCode);
        await WaitAsync(async () => await RowCountAsync(t.OwnerEmail) == 1, "queue flush marker");
    }

    /// <summary>Імітація воркера: ставить відомий токен на живий запит ідентичності (у БД лише sha256).</summary>
    private async Task<string> AttachTokenAsync(string email)
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var n = await AdminScalarAsync<long>(
            $"WITH u AS (UPDATE auth_password_resets SET token_hash = '\\x{Convert.ToHexString(SHA256.HashData(raw))}' " +
            $"WHERE identity_id = (SELECT id FROM identities WHERE email = '{Lit(email)}') AND superseded_at IS NULL AND consumed_at IS NULL RETURNING 1) SELECT count(*) FROM u");
        Assert.Equal(1, n);
        return token;
    }

    private async Task<string> RequestTokenAsync(HttpClient c, string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(c, email)).Resp.StatusCode);
        await WaitAsync(async () => await AdminScalarAsync<long>(
            $"SELECT count(*) FROM auth_password_resets r JOIN identities i ON i.id = r.identity_id WHERE i.email = '{Lit(email)}' AND r.superseded_at IS NULL AND r.consumed_at IS NULL") == 1, "reset row");
        return await AttachTokenAsync(email);
    }

    // ============================== forgot ==============================

    [SkippableFact]
    public async Task Forgot_is_202_without_body_for_existing_unknown_and_pending_but_creates_a_row_only_for_active()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync();
        var pending = NewEmail("pending");
        await AdminExecAsync($"INSERT INTO identities (email, state) VALUES ('{Lit(pending)}', 'pending')");
        var unknown = NewEmail("unknown");

        var results = new[] { await Forgot(c, t.OwnerEmail), await Forgot(c, pending), await Forgot(c, unknown) };
        foreach (var r in results)
        {
            Assert.Equal(HttpStatusCode.Accepted, r.Resp.StatusCode);
            Assert.Equal("", r.Text);
        }
        await FlushAsync(c);
        Assert.Equal(1, await RowCountAsync(t.OwnerEmail));
        Assert.Equal(0, await RowCountAsync(pending));
        Assert.Equal(0, await AdminScalarAsync<long>($"SELECT count(*) FROM identities WHERE email = '{Lit(unknown)}'"));
    }

    [SkippableTheory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Forgot_with_syntactically_invalid_email_is_422_invalid_email(string? email)
    {
        NeedDb();
        var (resp, _, body) = await Forgot(Client(), email);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("invalid_email", Code(body));
    }

    [SkippableFact]
    public async Task Forgot_with_email_over_320_chars_is_422_invalid_email()
    {
        NeedDb();
        var (resp, _, body) = await Forgot(Client(), new string('a', 320) + "@x.test");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("invalid_email", Code(body));
    }

    [SkippableFact]
    public async Task Forgot_ip_limit_is_429_with_retry_after_and_email_limit_is_a_silent_202_without_queueing()
    {
        NeedDb();
        var ip = Client(b => b.UseSetting("Auth:PasswordForgot:PerIpPermit", "2"));
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(ip, NewEmail())).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(ip, NewEmail())).Resp.StatusCode);
        var limited = await Forgot(ip, NewEmail());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Resp.StatusCode);
        Assert.Equal("rate_limited", Code(limited.Body));
        Assert.True(limited.Resp.Headers.RetryAfter is not null);

        var store = new FakeResetStore();
        var c = Client(services: s => { s.RemoveAll<IPasswordResetStore>(); s.AddSingleton<IPasswordResetStore>(store); });
        var email = NewEmail("throttled");
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Accepted, (await Forgot(c, i % 2 == 0 ? email : "  " + email.ToUpperInvariant() + " ")).Resp.StatusCode);
        await WaitAsync(() => Task.FromResult(store.Entered >= 3), "three issues");
        await Task.Delay(300);
        Assert.Equal(3, store.Entered); // 3 / год на пошту, регістр і пробіли не обходять ліміт; решта тихо без постановки
    }

    [SkippableFact]
    public async Task Forgot_returns_202_before_any_store_is_touched_and_issue_runs_once_per_request_with_normalized_email()
    {
        NeedDb();
        var fake = new FakeResetStore { Gate = new SemaphoreSlim(0) };
        var forbidden = new ForbiddenIdentityStore();
        var c = Client(services: s =>
        {
            s.RemoveAll<IPasswordResetStore>();
            s.AddSingleton<IPasswordResetStore>(fake);
            s.RemoveAll<IIdentityStore>();
            s.AddScoped<IIdentityStore>(_ => forbidden);
        });
        var existing = (await fx.CreateTenantAsync()).OwnerEmail;

        // обидва запити (наявна й невідома пошта) завершено, коли обробник заблоковано на семафорі: 202 не чекає на БД
        var a = await Forgot(c, "  " + existing.ToUpperInvariant() + " ");
        var b = await Forgot(c, NewEmail("nobody"));
        Assert.Equal(HttpStatusCode.Accepted, a.Resp.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, b.Resp.StatusCode);
        Assert.Equal(0, fake.Issued.Count);
        Assert.Equal(0, forbidden.Calls);

        fake.Gate.Release(10);
        await WaitAsync(() => Task.FromResult(fake.Issued.Count == 2), "two issues");
        await Task.Delay(200);
        Assert.Equal(2, fake.Issued.Count); // рівно один виклик на запит
        Assert.Equal(existing.ToLowerInvariant(), fake.Issued[0].Email);
        Assert.All(fake.Issued, i => Assert.Equal(32, i.KeyLength));
        var ttl = fake.Issued[0].Expires - DateTimeOffset.UtcNow;
        Assert.InRange(ttl.TotalMinutes, 29, 30.1); // TTL 30 хв за замовчуванням
        Assert.Equal(0, forbidden.Calls);
    }

    [SkippableFact]
    public async Task Forgot_with_full_queue_is_still_202_and_counts_dropped()
    {
        NeedDb();
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == AuthMetrics.MeterName && i.Name == "auth_password_forgot") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            foreach (var t in tags) if (t.Key == "outcome" && (string?)t.Value == "dropped") Interlocked.Add(ref dropped, v);
        });
        listener.Start();

        var fake = new FakeResetStore { Gate = new SemaphoreSlim(0) };
        var c = Client(b => b.UseSetting("Auth:PasswordForgot:QueueCapacity", "1"), s => { s.RemoveAll<IPasswordResetStore>(); s.AddSingleton<IPasswordResetStore>(fake); });
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(c, NewEmail())).Resp.StatusCode);
        await WaitAsync(() => Task.FromResult(fake.Entered == 1), "worker busy"); // перший запис забрано й заблоковано
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(c, NewEmail())).Resp.StatusCode); // заповнив чергу
        var overflow = await Forgot(c, NewEmail());
        Assert.Equal(HttpStatusCode.Accepted, overflow.Resp.StatusCode);
        Assert.Equal("", overflow.Text);
        Assert.True(Interlocked.Read(ref dropped) >= 1);
        fake.Gate.Release(10);
    }

    [SkippableFact]
    public async Task Forgot_with_forged_host_headers_is_202_without_body()
    {
        NeedDb();
        var c = Client();
        var req = Req(HttpMethod.Post, "/api/auth/password/forgot", new { email = NewEmail() });
        req.Headers.Add("X-Forwarded-Host", "evil.example");
        req.Headers.Host = "evil.example";
        var resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Issue_function_returns_void()
    {
        NeedDb();
        Assert.Equal("void", await AdminScalarAsync<string>("SELECT prorettype::regtype::text FROM pg_proc WHERE proname = 'auth_password_reset_issue'"));
    }

    // ============================== reset ==============================

    [SkippableFact]
    public async Task Weak_password_is_422_and_does_not_consume_the_token_then_the_same_token_works_once()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync();
        var token = await RequestTokenAsync(c, t.OwnerEmail);

        var weak = await Reset(c, token, "short");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.Resp.StatusCode);
        Assert.Equal("weak_password", Code(weak.Body));

        Assert.Equal(HttpStatusCode.NoContent, (await Reset(c, token, NewPassword)).Resp.StatusCode);
        var again = await Reset(c, token, NewPassword);
        Assert.Equal(HttpStatusCode.Gone, again.Resp.StatusCode);
        Assert.Equal("reset_invalid", Code(again.Body));
    }

    [SkippableFact]
    public async Task Every_unusable_token_gives_the_same_410_reset_invalid()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync();
        var expired = await RequestTokenAsync(c, t.OwnerEmail);
        await AdminExecAsync($"UPDATE auth_password_resets SET expires_at = now() - interval '1 minute' WHERE identity_id = (SELECT id FROM identities WHERE email = '{Lit(t.OwnerEmail)}')");

        var unknown = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var bodies = new List<string>();
        foreach (var tok in new string?[] { expired, unknown, "short", new string('!', 43), null, "" })
        {
            var r = await Reset(c, tok, NewPassword);
            Assert.Equal(HttpStatusCode.Gone, r.Resp.StatusCode);
            Assert.Equal("reset_invalid", Code(r.Body));
            bodies.Add(r.Text);
        }
        Assert.Single(bodies.Distinct());
    }

    [SkippableFact]
    public async Task A_new_request_invalidates_the_previous_token()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync();
        var first = await RequestTokenAsync(c, t.OwnerEmail);
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(c, t.OwnerEmail)).Resp.StatusCode);
        await WaitAsync(async () => await RowCountAsync(t.OwnerEmail) == 2, "second request row");
        var second = await AttachTokenAsync(t.OwnerEmail);

        Assert.Equal(HttpStatusCode.Gone, (await Reset(c, first, NewPassword)).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(c, second, NewPassword)).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Reset_changes_password_for_all_businesses_clears_lockout_and_revokes_every_session()
    {
        NeedDb();
        var logs = new List<string>();
        var c = Client(services: s => s.AddLogging(b => b.AddProvider(new CapturingProvider(logs))));
        var a = await fx.CreateTenantAsync();
        var b = await fx.CreateTenantAsync([BeautyModules.All[0]]);
        await using (var conn = new NpgsqlConnection(fx.Db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await IdentitySeed.InsertUserAsync(conn, b.TenantId, a.OwnerEmail, "Member", BCrypt.Net.BCrypt.EnhancedHashPassword(Password, 4), Roles.Admin);
        }
        var sessionA = await fx.LoginAsync(a.Slug, a.OwnerEmail);
        var sessionB = await fx.LoginAsync(b.Slug, a.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Get, "/api/auth/me", bearer: sessionA.AccessToken)).Resp.StatusCode);

        // блокування: 5 невдалих входів
        for (var i = 0; i < 5; i++) await Call(c, HttpMethod.Post, "/api/auth/login", new { email = a.OwnerEmail, password = "Wrong-Password-123" });
        Assert.Equal(HttpStatusCode.Locked, (await Call(c, HttpMethod.Post, "/api/auth/login", new { email = a.OwnerEmail, password = Password })).Resp.StatusCode);

        var token = await RequestTokenAsync(c, a.OwnerEmail);
        await Task.Delay(1100); // iat має секундну точність: sessions_valid_after суворо пізніше за iat
        var reset = await Reset(c, token, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, reset.Resp.StatusCode);
        Assert.Equal("", reset.Text);

        // блокування знято, новий пароль діє для входу без коду бізнесу, старий - ні
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(c, HttpMethod.Post, "/api/auth/login", new { email = a.OwnerEmail, password = Password })).Resp.StatusCode);
        var login = await Call(c, HttpMethod.Post, "/api/auth/login", new { email = a.OwnerEmail, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.Resp.StatusCode);
        Assert.True(login.Body.GetProperty("selectionRequired").GetBoolean()); // обидва бізнеси: той самий пароль

        // refresh усіх членств відкликано, старі access-токени не діють уже на наступному запиті
        foreach (var s in new[] { sessionA, sessionB })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(c, HttpMethod.Post, "/api/auth/refresh", new { refreshToken = s.RefreshToken })).Resp.StatusCode);
        var revoked = await Call(c, HttpMethod.Get, "/api/auth/me", bearer: sessionA.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.Resp.StatusCode);
        Assert.Equal("session_revoked", Code(revoked.Body));
        Assert.Equal(0, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_login_attempts"  + " WHERE failed_count > 0 AND locked_until > now()"));

        // dual-write, квитки, аудит без пошти
        var identityId = await AdminScalarAsync<Guid>($"SELECT id FROM identities WHERE email = '{Lit(a.OwnerEmail)}'");
        Assert.Equal(2, await AdminScalarAsync<long>($"SELECT count(*) FROM users WHERE identity_id = '{identityId}' AND sessions_valid_after IS NOT NULL"));
        Assert.True(BCrypt.Net.BCrypt.EnhancedVerify(NewPassword, await AdminScalarAsync<string>($"SELECT password_hash FROM users WHERE tenant_id = '{b.TenantId}' AND identity_id = '{identityId}'")));
        var audit = await AdminScalarAsync<string>($"SELECT details::text FROM platform_audit_log WHERE action = 'identity.password_reset_completed' AND actor_id = '{identityId}'");
        Assert.Contains(identityId.ToString(), audit);
        Assert.DoesNotContain(a.OwnerEmail, audit);
        Assert.Equal("pending", await AdminScalarAsync<string>($"SELECT notice_status FROM auth_password_resets WHERE identity_id = '{identityId}' AND consumed_at IS NOT NULL"));

        // токен, пошта й паролі не потрапляють у логи API
        string all;
        lock (logs) all = string.Join("\n", logs);
        Assert.DoesNotContain(token, all);
        Assert.DoesNotContain(a.OwnerEmail, all);
        Assert.DoesNotContain(NewPassword, all);
    }

    [SkippableFact]
    public async Task Changed_notice_is_queued_exactly_once_for_a_successful_reset_and_never_for_a_failed_one()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync();
        var token = await RequestTokenAsync(c, t.OwnerEmail);
        var identityId = await AdminScalarAsync<Guid>($"SELECT id FROM identities WHERE email = '{Lit(t.OwnerEmail)}'");
        var pending = $"SELECT count(*) FROM auth_password_resets WHERE identity_id = '{identityId}' AND notice_status = 'pending'";

        // невдалі спроби (слабкий пароль, невідомий токен) листа не ставлять
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Reset(c, token, "short")).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await Reset(c, new string('A', 43), NewPassword)).Resp.StatusCode);
        Assert.Equal(0, await AdminScalarAsync<long>(pending));

        Assert.Equal(HttpStatusCode.NoContent, (await Reset(c, token, NewPassword)).Resp.StatusCode);
        Assert.Equal(1, await AdminScalarAsync<long>(pending));

        // повторне використання токена - 410 і другого листа немає
        Assert.Equal(HttpStatusCode.Gone, (await Reset(c, token, NewPassword)).Resp.StatusCode);
        Assert.Equal(1, await AdminScalarAsync<long>(pending));
        Assert.Equal(1, await AdminScalarAsync<long>($"SELECT count(*) FROM auth_password_resets WHERE identity_id = '{identityId}' AND notice_status <> 'none'"));
    }

    [SkippableFact]
    public async Task Reset_for_an_identity_that_is_no_longer_active_is_410()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync();
        var token = await RequestTokenAsync(c, t.OwnerEmail);
        await AdminExecAsync($"UPDATE identities SET state = 'pending', password_hash = NULL WHERE email = '{Lit(t.OwnerEmail)}'");
        Assert.Equal(HttpStatusCode.Gone, (await Reset(c, token, NewPassword)).Resp.StatusCode);
    }

    [SkippableFact]
    public async Task Concurrent_resets_of_one_token_give_exactly_one_204()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync();
        var token = await RequestTokenAsync(c, t.OwnerEmail);
        var codes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => (await Reset(c, token, NewPassword)).Resp.StatusCode));
        Assert.Equal(1, codes.Count(x => x == HttpStatusCode.NoContent));
        Assert.Equal(5, codes.Count(x => x == HttpStatusCode.Gone));
    }
}
