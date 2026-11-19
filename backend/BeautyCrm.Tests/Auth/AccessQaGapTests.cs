using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-856 (QA, §30.13): прогалини покриття, знайдені під час звірки п.1-15 з наявними тестами.
/// M3 end-to-end (збіг за телефоном поза scope + подія client_scope_expanded), матриця deputy (§30.3/§30.6),
/// GET/approve ШІ за правами того, хто діє (п.14), повний ланцюг порядку фільтрів (п.11), обмеження в effective на реальних ендпоінтах (п.12).
/// </summary>
public sealed class AccessQaGapTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private HttpClient Client(Action<IWebHostBuilder>? configure = null, bool probe = false)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (probe) b.ConfigureTestServices(s => s.AddControllers().AddApplicationPart(typeof(AccessProbeController).Assembly));
            configure?.Invoke(b);
        });
        _disposables.Add(f);
        var c = f.CreateClient();
        _disposables.Add(c);
        return c;
    }

    private static async Task<(HttpResponseMessage R, string Raw, JsonElement B)> Call(HttpClient c, HttpMethod m, string url, string token, object? body = null)
    {
        var r = await c.SendAsync(Req(m, url, body, token));
        var text = await r.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (text.Length > 0)
        {
            try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { /* не JSON */ }
        }
        return (r, text, json);
    }

    private static string? Code(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    private static Dictionary<string, string> Perms(string role, params (string Key, string Level)[] over)
    {
        var d = new Dictionary<string, string>(PermissionPresets.For(role));
        foreach (var (k, l) in over) d[k] = l;
        return d;
    }

    // ============================================================ §30.13 п.8: M3 end-to-end

    [SkippableFact]
    public async Task M3_phone_match_outside_scope_does_not_disclose_stored_data_and_emits_client_scope_expanded()
    {
        NeedDb();
        const string phone = "+380507770011";
        var log = new CapturingLoggerProvider();
        long metric = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == "BeautyCrm.Access" && i.Name == "beauty.access.client_scope_expanded") l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref metric, m));
        listener.Start();

        var c = Client(b => b.ConfigureLogging(l => { l.SetMinimumLevel(LogLevel.Information); l.AddProvider(log); }));
        var salon = await new RegressionHarness(fx).CreateSalonAsync();
        var day = DateTime.UtcNow.Date.AddDays(3);
        DateTimeOffset At(int hour) => new(day.AddHours(hour), TimeSpan.Zero);

        // існуючий клієнт із збереженими даними (запис від імені директора, поза scope майстра)
        var first = await Call(c, HttpMethod.Post, "/api/beauty/appointments", salon.OwnerToken, new
        {
            locationId = salon.LocationId, specialistId = salon.SpecialistId, serviceId = salon.ServiceId, startsAt = At(10),
            client = new { name = "Stored Secret Name", phone }, reminder = "none", paymentMethod = "cash",
        });
        Assert.Equal(HttpStatusCode.Created, first.R.StatusCode);
        var storedClientId = first.B.GetProperty("clientId").GetGuid();
        await using (var db = fx.Db.CreateContext(salon.Tenant.TenantId))
            await db.Clients.Where(x => x.Id == storedClientId).ExecuteUpdateAsync(x => x.SetProperty(k => k.Email, "stored.secret@client.test"));
        Assert.Equal(0, Volatile.Read(ref metric)); // директор у scope: подія не пишеться
        Assert.DoesNotContain(log.Lines.ToArray(), l => l.Contains("client_scope_expanded"));

        // майстер без clients.view створює запис за тим самим телефоном, але з іншим ім'ям
        var spec = await fx.CreateUserAsync(salon.Tenant, Roles.Specialist, salon.SpecialistId, Perms(Roles.Specialist));
        Assert.Equal("none", PermissionPresets.For(Roles.Specialist)[PermissionKeys.ClientsView]);
        var second = await Call(c, HttpMethod.Post, "/api/beauty/appointments", spec.Tokens.AccessToken, new
        {
            locationId = salon.LocationId, specialistId = salon.SpecialistId, serviceId = salon.ServiceId, startsAt = At(12),
            client = new { name = "Typed By Actor", phone }, reminder = "none", paymentMethod = "cash",
        });
        Assert.Equal(HttpStatusCode.Created, second.R.StatusCode);
        // відповідь: передані актором дані, збережене ім'я/email/телефон не розкриваються
        Assert.Equal("Typed By Actor", second.B.GetProperty("clientName").GetString());
        Assert.DoesNotContain("Stored Secret Name", second.Raw);
        Assert.DoesNotContain("stored.secret@client.test", second.Raw);
        Assert.DoesNotContain(phone, second.Raw);
        Assert.DoesNotContain("507770011", second.Raw);
        // а GET того ж запису (деталь) теж не віддає збережених даних через ім'я клієнта? Зафіксовано нижче окремо (див. звіт)
        var apptId = second.B.GetProperty("id").GetGuid();

        // запис прив'язано до існуючого клієнта (дубліката не створено), автор записаний
        await using (var db = fx.Db.CreateContext(salon.Tenant.TenantId))
        {
            var row = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == apptId);
            Assert.Equal(storedClientId, row.ClientId);
            Assert.Equal(spec.UserId, row.CreatedByUserId);
            Assert.Equal(1, await db.Clients.CountAsync(x => x.Phone == phone));
        }

        // подія безпеки: структурований лог + метрика, без ПІІ
        var events = log.Lines.ToArray().Where(l => l.Contains("client_scope_expanded")).ToList();
        var ev = Assert.Single(events);
        Assert.Contains(spec.UserId.ToString(), ev, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(storedClientId.ToString(), ev, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(apptId.ToString(), ev, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stored Secret Name", ev);
        Assert.DoesNotContain("Typed By Actor", ev);
        Assert.DoesNotContain("507770011", ev);
        Assert.Equal(1, Volatile.Read(ref metric));

        // телефон без збігу (новий клієнт): scope не розширюється, події немає
        var third = await Call(c, HttpMethod.Post, "/api/beauty/appointments", spec.Tokens.AccessToken, new
        {
            locationId = salon.LocationId, specialistId = salon.SpecialistId, serviceId = salon.ServiceId, startsAt = At(14),
            client = new { name = "Brand New", phone = "+380507770099" }, reminder = "none", paymentMethod = "cash",
        });
        Assert.Equal(HttpStatusCode.Created, third.R.StatusCode);
        Assert.Equal(1, Volatile.Read(ref metric));
        Assert.Single(log.Lines.ToArray().Where(l => l.Contains("client_scope_expanded")));
    }

    // ============================================================ §30.13 п.12: constraints у effective на реальних ендпоінтах

    [SkippableFact]
    public async Task Effective_constraints_hold_on_real_endpoints_when_rows_are_inserted_directly()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);

        // deputy з team.manage = manage (стеля ролі): ендпоінти команди закриті
        var dep = await fx.CreateUserAsync(t, Roles.Deputy, null, Perms(Roles.Deputy, (PermissionKeys.TeamManage, "manage")));
        var (r1, _, b1) = await Call(c, HttpMethod.Get, "/api/users", dep.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);
        Assert.Equal("forbidden_role", Code(b1));
        var me = (await Call(c, HttpMethod.Get, "/api/auth/me", dep.Tokens.AccessToken)).B;
        Assert.Equal("none", me.GetProperty("permissions").GetProperty(PermissionKeys.TeamManage).GetString());

        // revenue.costs = edit за revenue.view = none (requires)
        var costs = await fx.CreateUserAsync(t, Roles.Admin, null, Perms(Roles.Admin, (PermissionKeys.RevenueView, "none")));
        var (r2, _, b2) = await Call(c, HttpMethod.Get, "/api/beauty/service-costs", costs.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);
        Assert.Equal("forbidden_role", Code(b2));

        // marketing.campaigns = edit за clients.view = own (requires clients.view = all)
        var mkt = await fx.CreateUserAsync(t, Roles.Admin, null, Perms(Roles.Admin, (PermissionKeys.ClientsView, "own")));
        var (r3, _, b3) = await Call(c, HttpMethod.Get, "/api/beauty/campaigns", mkt.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, r3.StatusCode);
        Assert.Equal("forbidden_role", Code(b3));

        // вимкнення beauty_clients знімає marketing.campaigns у effective (рядки лишаються)
        var adm = await fx.CreateUserAsync(t, Roles.Admin);
        var before = (await Call(c, HttpMethod.Get, "/api/auth/me", adm.Tokens.AccessToken)).B;
        Assert.Equal("edit", before.GetProperty("permissions").GetProperty(PermissionKeys.MarketingCampaigns).GetString());
        await fx.SetModulesAsync(t.TenantId, [.. BeautyModules.Known.Where(m => m != "beauty_clients")]);
        var after = (await Call(c, HttpMethod.Get, "/api/auth/me", adm.Tokens.AccessToken)).B;
        Assert.Equal("none", after.GetProperty("permissions").GetProperty(PermissionKeys.MarketingCampaigns).GetString());
        Assert.Equal("none", after.GetProperty("permissions").GetProperty(PermissionKeys.ClientsView).GetString());
        await using var db = fx.Db.CreateContext(t.TenantId);
        Assert.Equal("edit", (await db.UserPermissions.AsNoTracking().SingleAsync(p => p.UserId == adm.UserId && p.Permission == PermissionKeys.MarketingCampaigns)).Level);
    }

    // ============================================================ §30.13 п.11: повний ланцюг фільтрів

    [SkippableFact]
    public async Task Filter_chain_marketing_disabled_then_422_then_specialist_inactive()
    {
        NeedDb();
        var c = Client(b => b.UseSetting("Marketing:Enabled", "false"), probe: true);
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        var seed = await fx.SeedBeautyAsync(t.TenantId);

        // forbidden_role раніше за marketing_disabled
        var noMkt = await fx.CreateUserAsync(t, Roles.Deputy);
        Assert.Equal("forbidden_role", Code((await Call(c, HttpMethod.Get, "/api/beauty/coupons", noMkt.Tokens.AccessToken)).B));
        // право є, інстанс вимкнув маркетинг: marketing_disabled
        var mkt = await fx.CreateUserAsync(t, Roles.Admin);
        Assert.Equal("marketing_disabled", Code((await Call(c, HttpMethod.Get, "/api/beauty/coupons", mkt.Tokens.AccessToken)).B));

        // 422 раніше за specialist_inactive; після валідного тіла - specialist_inactive
        var spec = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistA, Perms(Roles.Specialist, (PermissionKeys.ClientsEdit, "all"), (PermissionKeys.ClientsView, "all")));
        await using (var db = fx.Db.CreateContext(t.TenantId))
            await db.Specialists.Where(s => s.Id == seed.SpecialistA).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        var bad = await Call(c, HttpMethod.Post, "/api/__probe/body", spec.Tokens.AccessToken, new { name = "toolongname" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.R.StatusCode);
        var ok = await Call(c, HttpMethod.Post, "/api/__probe/body", spec.Tokens.AccessToken, new { name = "ok" });
        Assert.Equal(HttpStatusCode.Forbidden, ok.R.StatusCode);
        Assert.Equal("specialist_inactive", Code(ok.B));
    }

    /// <summary>Конверт журналу beauty_ai_actions (EfAiActionJournal): статус AI-шару живе в jsonb result.</summary>
    private static string Envelope(string target) =>
        JsonSerializer.Serialize(new { Target = target, Status = "pending_confirmation", Revertible = false, Mode = "Confirm" });

    // ============================================================ §30.13 п.14: ШІ за правами того, хто діє

    [SkippableFact]
    public async Task Ai_actions_list_hides_tools_without_right_and_approve_is_checked_against_the_approver()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        Guid proposed, priced;
        await using (var db = fx.Db.CreateContext(t.TenantId))
        {
            var a = new AiAction { ToolName = "suggest_promotion", Payload = "{}", Status = AiActionStatus.Proposed, Result = Envelope("promo") };
            var b = new AiAction { ToolName = "get_prices", Payload = "{}", Status = AiActionStatus.Proposed, Result = Envelope("prices") };
            db.AddRange(a, b);
            await db.SaveChangesAsync();
            proposed = a.Id;
            priced = b.Id;
        }
        var owner = (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken;
        var deputy = await fx.CreateUserAsync(t, Roles.Deputy); // marketing.campaigns = none, services.catalog = view

        var ownerList = (await Call(c, HttpMethod.Get, "/api/beauty/ai/actions?status=pending_confirmation", owner)).B.EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(proposed, ownerList);
        Assert.Contains(priced, ownerList);

        var depList = await Call(c, HttpMethod.Get, "/api/beauty/ai/actions?status=pending_confirmation", deputy.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, depList.R.StatusCode);
        var depIds = depList.B.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(priced, depIds);
        Assert.DoesNotContain(proposed, depIds); // прихована, а не 403

        // approve дії, на tool якої немає права у того, хто підтверджує: 403 {permission, required}, дія лишається pending
        var (r, _, body) = await Call(c, HttpMethod.Post, $"/api/beauty/ai/actions/{proposed}/approve", deputy.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("forbidden_role", Code(body));
        Assert.Equal(PermissionKeys.MarketingCampaigns, body.GetProperty("permission").GetString());
        Assert.Equal("edit", body.GetProperty("required").GetString());
        await using var db2 = fx.Db.CreateContext(t.TenantId);
        Assert.Equal(AiActionStatus.Proposed, (await db2.AiActions.AsNoTracking().SingleAsync(a => a.Id == proposed)).Status);
    }

    // ============================================================ матриця deputy (§30.3 + §30.6)

    private const string RoleDeny = "403:forbidden_role";

    /// <summary>Очікування для deputy за пресетом §30.3 і картою §30.6; де права достатньо, збігається з колонкою admin.</summary>
    private static string DeputyExpected(AuthzBaselineMatrixTests.Row r)
    {
        var route = r.Route;
        bool Starts(string p) => route.StartsWith(p, StringComparison.Ordinal);
        var write = r.Method != "GET";

        if (route == "api/users/access-audit") return "403"; // owner-політика: порожнє тіло
        if (Starts("api/users") || Starts("api/invites")) return RoleDeny; // team.manage недоступне deputy
        if (route is "api/beauty/ai/settings" or "api/beauty/ai/models" or "api/beauty/ai/usage") return RoleDeny; // ai.settings = none
        if (Starts("api/beauty/campaigns") || Starts("api/beauty/marketing")
            || (Starts("api/beauty/coupons") && route != "api/beauty/coupons/check")) return RoleDeny; // marketing.campaigns = none
        if (Starts("api/beauty/channels") || Starts("api/beauty/analytics") || Starts("api/beauty/service-costs")
            || route.EndsWith("/costs", StringComparison.Ordinal) || route == "api/beauty/me/revenue"
            || Starts("api/beauty/settings/reminders")) return RoleDeny;
        if (write && Starts("api/beauty/inventory/services/") && route.EndsWith("/materials", StringComparison.Ordinal)) return RoleDeny; // §32.16: норми матеріалів лише owner/admin
        if (write && (Starts("api/beauty/services") || Starts("api/beauty/promotions"))) return RoleDeny; // services.catalog = view
        if (write && Starts("api/beauty/locations")) return RoleDeny; // locations.access = all, не edit
        if (write && Starts("api/beauty/specialists") && !route.Contains("/absences", StringComparison.Ordinal)) return RoleDeny; // staff.access = all, не edit
        if (route.EndsWith("/approve", StringComparison.Ordinal) && Starts("api/beauty/absences")) return RoleDeny;
        if (route.EndsWith("/reject", StringComparison.Ordinal) && Starts("api/beauty/absences")) return RoleDeny;
        return r.Admin;
    }

    [SkippableFact]
    public async Task Deputy_matrix_follows_preset_for_every_tenant_endpoint()
    {
        NeedDb();
        var c = Client(b =>
        {
            b.UseSetting("Marketing:Enabled", "true");
            b.UseSetting("Marketing:LegalGateApproved", "true");
        });
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        await fx.SeedBeautyAsync(t.TenantId);
        var deputy = (await fx.CreateDeputyAsync(t)).Tokens.AccessToken;
        var fake = Guid.Parse("0f0f0f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f");

        var failures = new List<string>();
        foreach (var row in AuthzBaselineMatrixTests.Matrix)
        {
            var url = "/" + System.Text.RegularExpressions.Regex.Replace(row.Route, @"\{[A-Za-z]+:guid\}", fake.ToString());
            url = url.Replace("{provider}", "openai");
            var req = new HttpRequestMessage(new HttpMethod(row.Method), url);
            if (row.Method is "POST" or "PUT" or "PATCH") req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deputy);
            using var resp = await c.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            string? code = null;
            if (text.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.String) code = cd.GetString();
                }
                catch (JsonException) { /* не JSON */ }
            }

            var expected = DeputyExpected(row);
            var parts = expected.Split(':', 2);
            var ok = int.Parse(parts[0]) == (int)resp.StatusCode
                && (parts.Length == 2 ? parts[1] == code : (int)resp.StatusCode != 403 || code is null);
            if (!ok) failures.Add($"deputy {row.Method,-6} {row.Route}: expected {expected}, got {(int)resp.StatusCode}{(code is null ? "" : ":" + code)}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} з {AuthzBaselineMatrixTests.Matrix.Count} розбіжностей:\n" + string.Join("\n", failures));
    }
}
