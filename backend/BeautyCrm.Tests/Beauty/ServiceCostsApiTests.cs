using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-763 (§24.6.1, §24.9, §24.10 п. 2, 11, 12): HTTP CRUD собівартості послуг на реальній БД (RLS) через повний конвеєр:
/// ролі (specialist 403 до читання тіла), IDOR, валідація, no-store, аудит без значень, логи без сум, паралельні PUT, ліміт,
/// відсутність собівартості у DTO записів/цін/публічному API, знімок через PATCH completed.
/// </summary>
public sealed class ServiceCostsApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
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

    private sealed record Env(HttpClient Client, LogSink Logs);

    private Env NewEnv(string? writePerMinute = null)
    {
        var logs = new LogSink();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (writePerMinute is not null) b.UseSetting("ServiceCosts:WritePerUserPerMinute", writePerMinute);
            b.ConfigureLogging(l => { l.AddProvider(logs); l.SetMinimumLevel(LogLevel.Debug); });
        });
        _disposables.Add(f);
        return new Env(f.CreateClient(), logs);
    }

    private sealed record Tn(TenantSeed Seed, BeautySeed Beauty, string Owner, string Admin, string Spec)
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
        return new Tn(t, seed, owner.AccessToken, admin.AccessToken, spec.AccessToken);
    }

    private Task<HttpResponseMessage> Call(Env e, HttpMethod m, string path, string? token, object? body = null) =>
        fx.Send(m, "/api/beauty" + path, body, token, client: e.Client);

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    private static void AssertNoStore(HttpResponseMessage r) => Assert.Equal("no-store", r.Headers.CacheControl?.ToString());

    private static string Put(Tn t, Guid? loc = null) => $"/services/{t.Beauty.ServiceId}/costs";

    private async Task<(List<ServiceCost> Rows, List<ServiceCostAuditRow> Audit)> DbAsync(Guid tenantId)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        return (await ctx.ServiceCosts.AsNoTracking().ToListAsync(), await ctx.ServiceCostAudit.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync());
    }

    // ---------- ролі ----------

    [SkippableFact]
    public async Task specialist_gets_403_forbidden_role_on_every_cost_endpoint_even_with_an_invalid_body_and_without_no_store_leaks()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var body = new { locationId = (Guid?)null, costAmount = 10m, masterPercent = (decimal?)null };
        foreach (var r in new[]
                 {
                     await Call(e, HttpMethod.Get, "/service-costs", t.Spec),
                     await Call(e, HttpMethod.Get, Put(t), t.Spec),
                     await Call(e, HttpMethod.Put, Put(t), t.Spec, body),
                     await Call(e, HttpMethod.Put, Put(t), t.Spec, new { costAmount = -5 }), // 403, а не 422: роль перевіряється до тіла
                     await Call(e, HttpMethod.Delete, Put(t), t.Spec),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden_role", await Code(r));
            AssertNoStore(r);
        }
        Assert.Empty((await DbAsync(t.Id)).Rows);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(e, HttpMethod.Get, "/service-costs", null)).StatusCode);
    }

    [SkippableFact]
    public async Task owner_and_admin_can_put_get_list_and_delete_with_no_store()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();

        var put = await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = 120.5m, masterPercent = 33.33m });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        AssertNoStore(put);
        var p = await J(put);
        Assert.Equal(120.5m, p.GetProperty("costAmount").GetDecimal());
        Assert.Equal(33.33m, p.GetProperty("masterPercent").GetDecimal());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("locationId").ValueKind);
        Assert.Equal(t.Seed.OwnerId, p.GetProperty("updatedBy").GetProperty("id").GetGuid());

        var adminPut = await Call(e, HttpMethod.Put, Put(t), t.Admin,
            new { locationId = t.Beauty.LocationId, costAmount = 80, masterPercent = (decimal?)null });
        Assert.Equal(HttpStatusCode.OK, adminPut.StatusCode);

        var get = await Call(e, HttpMethod.Get, Put(t), t.Admin);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertNoStore(get);
        var g = await J(get);
        Assert.Equal(120.5m, g.GetProperty("network").GetProperty("costAmount").GetDecimal());
        var loc = Assert.Single(g.GetProperty("locations").EnumerateArray());
        Assert.Equal(t.Beauty.LocationId, loc.GetProperty("locationId").GetGuid());
        Assert.Equal("Main", loc.GetProperty("locationName").GetString());
        Assert.Equal(JsonValueKind.Null, loc.GetProperty("masterPercent").ValueKind);

        var list = await J(await Call(e, HttpMethod.Get, "/service-costs", t.Owner));
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("Haircut", item.GetProperty("serviceName").GetString());
        Assert.Equal(80m, Assert.Single(item.GetProperty("locations").EnumerateArray()).GetProperty("costAmount").GetDecimal());

        var delLoc = await Call(e, HttpMethod.Delete, $"{Put(t)}?locationId={t.Beauty.LocationId}", t.Admin);
        Assert.Equal(HttpStatusCode.NoContent, delLoc.StatusCode);
        AssertNoStore(delLoc);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(e, HttpMethod.Delete, Put(t), t.Owner)).StatusCode);
        Assert.Empty((await DbAsync(t.Id)).Rows);
        var gone = await Call(e, HttpMethod.Delete, Put(t), t.Owner);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("cost_not_found", await Code(gone));
        AssertNoStore(gone);
    }

    // ---------- валідація ----------

    [SkippableTheory]
    [InlineData("-1", "null", "invalid_cost")]
    [InlineData("1000000.01", "null", "invalid_cost")]
    [InlineData("1.234", "null", "invalid_cost")]
    [InlineData("null", "null", "invalid_cost")]
    [InlineData("10", "101", "invalid_master_percent")]
    [InlineData("10", "-0.01", "invalid_master_percent")]
    [InlineData("10", "10.123", "invalid_master_percent")]
    public async Task invalid_values_are_422_with_contract_codes_and_nothing_is_written(string cost, string percent, string code)
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var json = $$"""{"locationId":null,"costAmount":{{cost}},"masterPercent":{{percent}}}""";
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/beauty" + Put(t))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", t.Owner);
        var r = await e.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(code, await Code(r));
        AssertNoStore(r);
        var db = await DbAsync(t.Id);
        Assert.Empty(db.Rows);
        Assert.Empty(db.Audit);
    }

    [SkippableFact]
    public async Task boundary_values_are_accepted()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        Assert.Equal(HttpStatusCode.OK, (await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = 0, masterPercent = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = 1000000, masterPercent = 100 })).StatusCode);
        var row = Assert.Single((await DbAsync(t.Id)).Rows);
        Assert.Equal((1000000m, 100m), (row.CostAmount, row.MasterPercent));
    }

    // ---------- IDOR ----------

    [SkippableFact]
    public async Task foreign_tenant_service_and_location_are_404_without_disclosure()
    {
        NeedDb();
        var e = NewEnv();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        await Call(e, HttpMethod.Put, Put(a), a.Owner, new { costAmount = 50 });

        foreach (var r in new[]
                 {
                     await Call(e, HttpMethod.Get, Put(a), b.Owner),
                     await Call(e, HttpMethod.Put, Put(a), b.Owner, new { costAmount = 1 }),
                     await Call(e, HttpMethod.Delete, Put(a), b.Owner),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal("service_not_found", await Code(r));
            AssertNoStore(r);
        }

        // свій сервіс + чужий заклад
        var put = await Call(e, HttpMethod.Put, Put(b), b.Owner, new { locationId = a.Beauty.LocationId, costAmount = 1 });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal("location_not_found", await Code(put));
        var del = await Call(e, HttpMethod.Delete, $"{Put(b)}?locationId={a.Beauty.LocationId}", b.Owner);
        Assert.Equal("location_not_found", await Code(del));

        // дані tenant a цілі, у списку b немає чужих
        var rowA = Assert.Single((await DbAsync(a.Id)).Rows);
        Assert.Equal(50m, rowA.CostAmount);
        Assert.Empty((await DbAsync(b.Id)).Rows);
        var list = await J(await Call(e, HttpMethod.Get, "/service-costs", b.Owner));
        Assert.All(list.GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("network").ValueKind));
    }

    // ---------- аудит і логи ----------

    [SkippableFact]
    public async Task audit_records_changed_field_names_only_skips_no_op_puts_and_logs_have_no_amounts()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();

        await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = 123456.78m, masterPercent = 41.37m }); // створення
        await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = 123456.78m, masterPercent = 41.37m }); // без змін
        await Call(e, HttpMethod.Put, Put(t), t.Admin, new { costAmount = 123456.78m, masterPercent = 42.42m }); // лише відсоток
        await Call(e, HttpMethod.Put, Put(t), t.Admin, new { costAmount = 765432.10m, masterPercent = 42.42m }); // лише сума
        await Call(e, HttpMethod.Delete, Put(t), t.Owner);

        var (rows, audit) = await DbAsync(t.Id);
        Assert.Empty(rows);
        Assert.Equal(4, audit.Count);
        Assert.Equal(["cost_amount", "master_percent"], audit[0].ChangedFields.Order());
        Assert.Equal(["master_percent"], audit[1].ChangedFields);
        Assert.Equal(["cost_amount"], audit[2].ChangedFields);
        Assert.Equal("delete", audit[3].Action);
        Assert.Equal([t.Seed.OwnerId, t.Seed.OwnerId], new[] { audit[0].UserId, audit[3].UserId });
        Assert.All(audit, a => Assert.Equal(t.Beauty.ServiceId, a.ServiceId));
        // таблиця не має колонок зі значеннями
        Assert.DoesNotContain(typeof(ServiceCostAuditRow).GetProperties(), p => p.PropertyType == typeof(decimal) || p.PropertyType == typeof(decimal?));

        var logs = e.Logs.All;
        Assert.Contains("service_cost_updated", logs);
        foreach (var secret in new[] { "123456", "41.37", "42.42", "765432" })
            Assert.DoesNotContain(secret, logs);
    }

    // ---------- паралельність і ліміт ----------

    [SkippableFact]
    public async Task parallel_puts_of_the_same_scope_leave_exactly_one_row()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var responses = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            Call(e, HttpMethod.Put, Put(t), i % 2 == 0 ? t.Owner : t.Admin, new { costAmount = i, masterPercent = i })));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var (rows, audit) = await DbAsync(t.Id);
        Assert.Single(rows);
        Assert.InRange(audit.Count, 1, 8);
        Assert.All(audit, a => Assert.Equal("set", a.Action));
    }

    [SkippableFact]
    public async Task writes_are_rate_limited_per_user_with_retry_after()
    {
        NeedDb();
        var e = NewEnv(writePerMinute: "10");
        var t = await NewTenantAsync();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = i })).StatusCode);
        var limited = await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = 99 });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await Code(limited));
        Assert.True(limited.Headers.RetryAfter is not null);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Call(e, HttpMethod.Delete, Put(t), t.Owner)).StatusCode);
        // ліміт на користувача: admin цього tenant не заблокований
        Assert.Equal(HttpStatusCode.OK, (await Call(e, HttpMethod.Put, Put(t), t.Admin, new { costAmount = 1 })).StatusCode);
        // читання не лімітується
        Assert.Equal(HttpStatusCode.OK, (await Call(e, HttpMethod.Get, Put(t), t.Owner)).StatusCode);
    }

    // ---------- знімок через API і відсутність витоку ----------

    private static readonly string[] Forbidden = ["cost", "masterPercent", "masterPayout", "payout", "snapshot", "margin", "netIncome"];

    private static void AssertNoCostFields(string body, string where)
    {
        foreach (var word in Forbidden)
            Assert.False(body.Contains(word, StringComparison.OrdinalIgnoreCase), $"{where} leaks '{word}': {body}");
    }

    [SkippableFact]
    public async Task patch_completed_takes_the_snapshot_second_patch_is_409_and_no_dto_leaks_cost_to_anyone()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        await Call(e, HttpMethod.Put, Put(t), t.Owner, new { costAmount = 70m, masterPercent = 10m });

        var done = await Call(e, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentA}", t.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        AssertNoCostFields(await done.Content.ReadAsStringAsync(), "PATCH response");

        var again = await Call(e, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentA}", t.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("appointment_closed", await Code(again));

        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var a = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == t.Beauty.AppointmentA);
            Assert.Equal((70m, 10m, 50m), (a.CostSnapshot, a.MasterPercentSnapshot, a.MasterPayoutSnapshot));
            Assert.NotNull(a.CostCapturedAt);
            var b = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == t.Beauty.AppointmentB);
            Assert.Null(b.CostSnapshot);
            Assert.Null(b.CostCapturedAt);
        }

        // specialist завершує власний запис: знімок є, у відповіді собівартості немає
        var specDone = await Call(e, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentA}", t.Spec, new { status = "completed" });
        Assert.Equal(HttpStatusCode.Conflict, specDone.StatusCode);

        foreach (var (who, token) in new[] { ("owner", t.Owner), ("specialist", t.Spec) })
        {
            var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("o"));
            var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(10).ToString("o"));
            var list = await Call(e, HttpMethod.Get, $"/appointments?from={from}&to={to}", token);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            AssertNoCostFields(await list.Content.ReadAsStringAsync(), $"GET /appointments ({who})");
            var one = await Call(e, HttpMethod.Get, $"/appointments/{t.Beauty.AppointmentA}", token);
            AssertNoCostFields(await one.Content.ReadAsStringAsync(), $"GET /appointments/id ({who})");
            var prices = await Call(e, HttpMethod.Get, $"/services/{t.Beauty.ServiceId}/prices", token);
            Assert.Equal(HttpStatusCode.OK, prices.StatusCode);
            AssertNoCostFields(await prices.Content.ReadAsStringAsync(), $"GET prices ({who})");
            var services = await Call(e, HttpMethod.Get, "/services", token);
            AssertNoCostFields(await services.Content.ReadAsStringAsync(), $"GET services ({who})");
        }

        var pub = await fx.Send(HttpMethod.Get, $"/api/public/{t.Seed.Slug}/locations/{t.Beauty.LocationId}/services", client: e.Client);
        Assert.Equal(HttpStatusCode.OK, pub.StatusCode);
        AssertNoCostFields(await pub.Content.ReadAsStringAsync(), "public services");
        var pubLoc = await fx.Send(HttpMethod.Get, $"/api/public/{t.Seed.Slug}/locations", client: e.Client);
        AssertNoCostFields(await pubLoc.Content.ReadAsStringAsync(), "public locations");
    }
}
