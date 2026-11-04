using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-820 (§27.9, ADR-018): POST /api/beauty/feedback на реальній БД (RLS FORCE): ролі, валідація, ліміти 5/год і 30/добу,
/// ізоляція tenant, ігнорування tenant/user з тіла, призупинений tenant, логи без тексту заявки.
/// </summary>
public sealed class PlatformFeedbackApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Url = "/api/beauty/feedback";
    private const string Text = "Календар не показує запис після оновлення сторінки";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed class LogSink : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        public string All { get { lock (_lines) return string.Join("\n", _lines); } }
        public ILogger CreateLogger(string categoryName) => new L(this);
        public void Dispose() { }

        private sealed class L(LogSink o) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (o._lines) o._lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
            }
        }
    }

    private sealed record Tn(TenantSeed Seed, string Owner, string Admin, string Spec, Guid AdminId, Guid SpecId)
    {
        public Guid Id => Seed.TenantId;
    }

    private async Task<Tn> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        return new Tn(t, owner.AccessToken, admin.AccessToken, spec.AccessToken, admin.User.Id, spec.User.Id);
    }

    private Task<HttpResponseMessage> Post(string token, object? body, HttpClient? client = null) =>
        fx.Send(HttpMethod.Post, Url, body, token, client: client);

    private static object Valid(string type = "bug", string? severity = null, string? message = null, string? pageUrl = "/beauty/calendar") =>
        new { type, severity, message = message ?? Text, pageUrl };

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    private async Task<List<PlatformFeedback>> RowsAsync(Guid tenantId)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        return await ctx.PlatformFeedbacks.AsNoTracking().OrderBy(f => f.CreatedAt).ToListAsync();
    }

    private async Task SeedRowsAsync(Guid tenantId, Guid userId, int count, TimeSpan age)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        for (var i = 0; i < count; i++)
            ctx.PlatformFeedbacks.Add(new PlatformFeedback
            {
                Id = Guid.NewGuid(), UserId = userId, Type = "improvement", Message = "Seeded feedback row number " + i,
                CreatedAt = DateTimeOffset.UtcNow - age, NextAttemptAt = DateTimeOffset.UtcNow - age,
            });
        await ctx.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ happy path / ролі

    [SkippableTheory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("specialist")]
    public async Task every_tenant_role_gets_202_and_a_pending_row(string role)
    {
        NeedDb();
        var t = await NewTenantAsync();
        var (token, userId) = role switch { "owner" => (t.Owner, t.Seed.OwnerId), "admin" => (t.Admin, t.AdminId), _ => (t.Spec, t.SpecId) };

        var r = await Post(token, Valid("improvement", "high"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var id = (await J(r)).GetProperty("id").GetGuid();

        var row = Assert.Single(await RowsAsync(t.Id));
        Assert.Equal(id, row.Id);
        Assert.Equal(t.Id, row.TenantId);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("improvement", row.Type);
        Assert.Equal("high", row.Severity);
        Assert.Equal(Text, row.Message);
        Assert.Equal("/beauty/calendar", row.PageUrl);
        Assert.Equal("pending", row.DeliveryStatus);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LastError);
        Assert.Null(row.DeliveredAt);
    }

    [SkippableFact]
    public async Task severity_defaults_to_normal_and_page_url_is_optional_and_message_trimmed()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var r = await Post(t.Owner, new { type = "cooperation", message = $"   {Text}  \n" });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var row = Assert.Single(await RowsAsync(t.Id));
        Assert.Equal("normal", row.Severity);
        Assert.Null(row.PageUrl);
        Assert.Equal(Text, row.Message);
    }

    [SkippableFact]
    public async Task unauthenticated_gets_401_and_platform_key_is_not_enough()
    {
        NeedDb();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(null!, Valid())).StatusCode);
        var withKey = await fx.Send(HttpMethod.Post, Url, Valid(), platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.Unauthorized, withKey.StatusCode);
    }

    // ------------------------------------------------------------------ сервер не довіряє тілу

    [SkippableFact]
    public async Task tenant_and_user_from_body_are_ignored_and_taken_from_jwt()
    {
        NeedDb();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var r = await Post(a.Spec, new
        {
            type = "bug", message = Text, tenantId = b.Id, tenant_id = b.Id, userId = b.Seed.OwnerId, user_id = b.Seed.OwnerId,
            id = Guid.Empty, deliveryStatus = "delivered", attempts = 9, externalLeadId = "x", createdAt = "2000-01-01T00:00:00Z",
        });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var newId = (await J(r)).GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, newId);

        Assert.Empty(await RowsAsync(b.Id));
        var row = Assert.Single(await RowsAsync(a.Id));
        Assert.Equal(a.Id, row.TenantId);
        Assert.Equal(a.SpecId, row.UserId);
        Assert.Equal("pending", row.DeliveryStatus);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.ExternalLeadId);
        Assert.True(row.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [SkippableFact]
    public async Task tenant_isolation_rows_of_one_tenant_are_invisible_to_another()
    {
        NeedDb();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await Post(a.Owner, Valid())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(b.Owner, Valid("cooperation"))).StatusCode);

        var rowsA = await RowsAsync(a.Id);
        var rowsB = await RowsAsync(b.Id);
        Assert.Equal("bug", Assert.Single(rowsA).Type);
        Assert.Equal("cooperation", Assert.Single(rowsB).Type);
        Assert.NotEqual(rowsA[0].Id, rowsB[0].Id);
    }

    // ------------------------------------------------------------------ валідація

    [SkippableTheory]
    [InlineData(null, "invalid_type")]
    [InlineData("", "invalid_type")]
    [InlineData("BUG", "invalid_type")]
    [InlineData("feature", "invalid_type")]
    public async Task invalid_type_is_422(string? type, string code)
    {
        NeedDb();
        var t = await NewTenantAsync();
        var r = await Post(t.Owner, new { type, message = Text });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(code, await Code(r));
        Assert.Empty(await RowsAsync(t.Id));
    }

    [SkippableTheory]
    [InlineData("urgent")]
    [InlineData("HIGH")]
    [InlineData("")]
    public async Task invalid_severity_is_422(string severity)
    {
        NeedDb();
        var t = await NewTenantAsync();
        var r = await Post(t.Owner, new { type = "bug", severity, message = Text });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("invalid_severity", await Code(r));
        Assert.Empty(await RowsAsync(t.Id));
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("         ")]
    [InlineData("too short")] // 9 символів
    [InlineData("bad\0nul-byte-in-message")]
    [InlineData("bad\u0007bell-in-message")]
    public async Task invalid_message_is_422(string? message)
    {
        NeedDb();
        var t = await NewTenantAsync();
        var r = await Post(t.Owner, new { type = "bug", message });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("invalid_message", await Code(r));
        Assert.Empty(await RowsAsync(t.Id));
    }

    [SkippableFact]
    public async Task message_length_bounds_are_10_and_2000_characters()
    {
        NeedDb();
        var t = await NewTenantAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, new { type = "bug", message = new string('a', 10) })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, new { type = "bug", message = new string('b', 2000) })).StatusCode);
        // 10 символів Unicode поза BMP (суррогатні пари = 20 UTF-16 одиниць) - рахуються як 10.
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, new { type = "bug", message = string.Concat(Enumerable.Repeat("\U0001F600", 10)) })).StatusCode);
        var over = await Post(t.Owner, new { type = "bug", message = new string('c', 2001) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal("invalid_message", await Code(over));
        Assert.Equal(3, (await RowsAsync(t.Id)).Count);
    }

    [SkippableTheory]
    [InlineData("calendar")]
    [InlineData("//evil.example/x")]
    [InlineData("/beauty/calendar?x=1")]
    [InlineData("/beauty/calendar#frag")]
    [InlineData("https://evil.example/")]
    [InlineData("/beauty/cal endar")]
    [InlineData("/beauty/\\evil")]
    [InlineData("/beauty/<script>")]
    public async Task invalid_page_url_is_422(string pageUrl)
    {
        NeedDb();
        var t = await NewTenantAsync();
        var r = await Post(t.Owner, new { type = "bug", message = Text, pageUrl });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("invalid_page_url", await Code(r));
        Assert.Empty(await RowsAsync(t.Id));
    }

    [SkippableFact]
    public async Task page_url_longer_than_500_is_422_and_exactly_500_is_accepted()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var ok = "/" + new string('a', 499);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, new { type = "bug", message = Text, pageUrl = ok })).StatusCode);
        var bad = await Post(t.Owner, new { type = "bug", message = Text, pageUrl = ok + "a" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("invalid_page_url", await Code(bad));
    }

    [SkippableFact]
    public async Task oversized_body_is_rejected_and_nothing_is_written()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var r = await Post(t.Owner, new { type = "bug", message = new string('x', 40_000) });
        Assert.NotEqual(HttpStatusCode.Accepted, r.StatusCode);
        Assert.True((int)r.StatusCode is >= 400 and < 500);
        Assert.Empty(await RowsAsync(t.Id));
    }

    // ------------------------------------------------------------------ ліміти

    [SkippableFact]
    public async Task sixth_request_within_an_hour_is_429_with_retry_after_and_limit_is_per_user()
    {
        NeedDb();
        var t = await NewTenantAsync();
        for (var i = 0; i < PlatformFeedbackService.PerUserPerHour; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, Valid())).StatusCode);

        var limited = await Post(t.Owner, Valid());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await Code(limited));
        var retry = int.Parse(limited.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retry, 1, 3600);
        Assert.Equal(5, (await RowsAsync(t.Id)).Count);

        // Інший користувач того ж tenant не страждає від ліміту власника.
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Admin, Valid())).StatusCode);
    }

    [SkippableFact]
    public async Task user_limit_counts_only_the_last_hour()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await SeedRowsAsync(t.Id, t.Seed.OwnerId, 5, TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, Valid())).StatusCode);

        var t2 = await NewTenantAsync();
        await SeedRowsAsync(t2.Id, t2.Seed.OwnerId, 5, TimeSpan.FromMinutes(59));
        var limited = await Post(t2.Owner, Valid());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single()), 1, 120);
    }

    [SkippableFact]
    public async Task tenant_limit_is_30_per_day_across_users_and_does_not_leak_to_other_tenants()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var other = await NewTenantAsync();
        // 29 рядків (різні користувачі, поза годинним вікном) -> ще одна заявка проходить, наступна - ні.
        await SeedRowsAsync(t.Id, t.AdminId, 15, TimeSpan.FromHours(3));
        await SeedRowsAsync(t.Id, t.SpecId, 14, TimeSpan.FromHours(3));
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, Valid())).StatusCode);
        var limited = await Post(t.Spec, Valid());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await Code(limited));
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Equal(30, (await RowsAsync(t.Id)).Count);

        Assert.Equal(HttpStatusCode.Accepted, (await Post(other.Owner, Valid())).StatusCode);
    }

    [SkippableFact]
    public async Task tenant_limit_counts_only_the_last_24_hours()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await SeedRowsAsync(t.Id, t.AdminId, 30, TimeSpan.FromHours(25));
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t.Owner, Valid())).StatusCode);
    }

    [SkippableFact]
    public async Task parallel_requests_cannot_exceed_the_user_limit()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Post(t.Owner, Valid())));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(5, (await RowsAsync(t.Id)).Count);
    }

    // ------------------------------------------------------------------ стан tenant / користувача

    [SkippableFact]
    public async Task suspended_tenant_gets_403_and_nothing_is_written()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var suspend = await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{t.Id}", new { status = "suspended" }, platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);

        var r = await Post(t.Owner, Valid());
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("tenant_suspended", await Code(r));
        Assert.Empty(await RowsAsync(t.Id));
    }

    [SkippableFact]
    public async Task deactivated_user_with_a_live_token_gets_403()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var off = await fx.Send(HttpMethod.Patch, $"/api/users/{t.SpecId}/status", new { isActive = false }, t.Owner);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var r = await Post(t.Spec, Valid());
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(await RowsAsync(t.Id));
    }

    // ------------------------------------------------------------------ логи / PII

    [SkippableFact]
    public async Task logs_do_not_contain_message_page_url_or_email()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var logs = new LogSink();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b => b.ConfigureLogging(l => { l.AddProvider(logs); l.SetMinimumLevel(LogLevel.Debug); }));
        _disposables.Add(f);
        var client = f.CreateClient();

        const string secret = "ТАЄМНИЙ-ТЕКСТ-заявки-7f3a9c";
        var r = await Post(t.Owner, new { type = "bug", message = secret, pageUrl = "/beauty/secret-page-7f3a9c" }, client);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        // Невалідна заявка теж не потрапляє в лог.
        await Post(t.Owner, new { type = "nope", message = secret + "-invalid" }, client);

        var all = logs.All;
        Assert.DoesNotContain("7f3a9c", all);
        Assert.DoesNotContain(t.Seed.OwnerEmail, all);
        Assert.Contains("Platform feedback", all);
    }
}
