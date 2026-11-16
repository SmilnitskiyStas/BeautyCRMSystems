using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Regression;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-852 (ADR-023, §30.6.2-30.6.6, §30.13 п.8-11): група A на реальному API + PostgreSQL/RLS - календар (scope own/all, IDOR),
/// ціни (M4), overview, аналітика й собівартість, /me/revenue, created_by_user_id, scope клієнта, staff/locations, D7.
/// </summary>
public sealed class AccessGroupATests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private static string Day(int ahead) => DateTime.UtcNow.Date.AddDays(ahead).ToString("yyyy-MM-dd");

    private async Task<(HttpResponseMessage R, JsonElement B)> Call(HttpMethod m, string url, string token, object? body = null)
    {
        var r = await fx.Send(m, url, body, token);
        var text = await r.Content.ReadAsStringAsync();
        return (r, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? Code(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static Dictionary<string, string> Perms(string role, params (string Key, string Level)[] over)
    {
        var d = new Dictionary<string, string>(PermissionPresets.For(role));
        foreach (var (k, l) in over) d[k] = l;
        return d;
    }

    private sealed record World(TenantSeed T, BeautySeed S, string Owner);

    private async Task<World> WorldAsync()
    {
        var t = await fx.CreateTenantAsync();
        var s = await fx.SeedBeautyAsync(t.TenantId);
        return new World(t, s, (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken);
    }

    private async Task<string> TokenAsync(World w, string role, Guid? specialist = null, params (string, string)[] over) =>
        (await fx.CreateUserAsync(w.T, role, specialist, Perms(role, over))).Tokens.AccessToken;

    private static IEnumerable<string?> Ids(JsonElement list) => list.EnumerateArray().Select(x => x.GetProperty("id").GetString());

    private static string ListUrl => $"/api/beauty/appointments?from={Day(0)}T00:00:00Z&to={Day(10)}T00:00:00Z&includeCancelled=true";

    // ---------- календар: scope і IDOR ----------

    [SkippableFact]
    public async Task Own_calendar_scope_lists_only_own_and_other_appointments_are_404()
    {
        NeedDb();
        var w = await WorldAsync();
        var spec = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA);

        var (r, list) = await Call(HttpMethod.Get, ListUrl + $"&specialistId={w.S.SpecialistB}", spec);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([w.S.AppointmentA.ToString()], Ids(list)); // specialistId підміняється власним

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/beauty/appointments/{w.S.AppointmentA}", spec)).R.StatusCode);
        foreach (var (m, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"/api/beauty/appointments/{w.S.AppointmentB}", null),
                     (HttpMethod.Patch, $"/api/beauty/appointments/{w.S.AppointmentB}", new { status = "completed" }),
                     (HttpMethod.Post, $"/api/beauty/appointments/{w.S.AppointmentB}/cancel", null),
                 })
        {
            var (rr, b) = await Call(m, url, spec, body);
            Assert.Equal(HttpStatusCode.NotFound, rr.StatusCode);
            Assert.Equal("appointment_not_found", Code(b));
        }

        // без прив'язки до профілю: порожня вибірка
        var nobody = await TokenAsync(w, Roles.Specialist);
        Assert.Empty((await Call(HttpMethod.Get, ListUrl, nobody)).B.EnumerateArray());

        // всі: бачать обидва
        var deputy = await TokenAsync(w, Roles.Deputy);
        Assert.Equal(2, (await Call(HttpMethod.Get, ListUrl, deputy)).B.GetArrayLength());
    }

    [SkippableFact]
    public async Task Manage_own_with_view_all_gives_403_with_permission_on_foreign_appointment()
    {
        NeedDb();
        var w = await WorldAsync();
        var token = await TokenAsync(w, Roles.Deputy, w.S.SpecialistA,
            (PermissionKeys.CalendarManage, "own"));
        var (r, b) = await Call(HttpMethod.Patch, $"/api/beauty/appointments/{w.S.AppointmentB}", token, new { status = "completed" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("forbidden_role", Code(b));
        Assert.Equal(PermissionKeys.CalendarManage, b.GetProperty("permission").GetString());
        Assert.Equal("all", b.GetProperty("required").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/beauty/appointments/{w.S.AppointmentB}/cancel", token)).R.StatusCode);
        // свій запис змінювати можна
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/beauty/appointments/{w.S.AppointmentA}", token, new { status = "completed" })).R.StatusCode);
        // перегляд чужого дозволено (view = all)
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/beauty/appointments/{w.S.AppointmentB}", token)).R.StatusCode);
    }

    [SkippableFact]
    public async Task Calendar_endpoints_need_calendar_permission_and_inactive_profile_is_blocked_after_permission()
    {
        NeedDb();
        var w = await WorldAsync();
        var user = await fx.CreateUserAsync(w.T, Roles.Specialist, w.S.SpecialistA,
            Perms(Roles.Specialist, (PermissionKeys.CalendarView, "none"), (PermissionKeys.CalendarManage, "none")));
        var none = user.Tokens.AccessToken;
        foreach (var (m, url) in new[] { (HttpMethod.Get, ListUrl), (HttpMethod.Get, $"/api/beauty/appointments/{w.S.AppointmentA}"), (HttpMethod.Post, "/api/beauty/appointments") })
        {
            var (r, b) = await Call(m, url, none, m == HttpMethod.Post ? new { } : null);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden_role", Code(b));
        }
        // чужий specialistId при calendar.manage = own: 403 forbidden (код без змін)
        await fx.MutateUserAsync(w.T.TenantId, user.UserId, _ => { }, Perms(Roles.Specialist)); // тепер calendar.* = own
        var own = none;
        var create = await Call(HttpMethod.Post, "/api/beauty/appointments", own, new
        {
            locationId = w.S.LocationId, specialistId = w.S.SpecialistB, serviceId = w.S.ServiceId, startsAt = DateTimeOffset.UtcNow.AddDays(5),
            client = new { name = "X", phone = "+380501110000" }, paymentMethod = "cash",
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.R.StatusCode);
        Assert.Equal("forbidden", Code(create.B));

        // M7 + порядок: неактивний профіль без права -> forbidden_role першим; з правом -> specialist_inactive
        await using (var db = fx.Db.CreateContext(w.T.TenantId))
            await db.Specialists.Where(s => s.Id == w.S.SpecialistA).ExecuteUpdateAsync(x => x.SetProperty(s => s.IsActive, false));
        Assert.Equal("forbidden_role", Code((await Call(HttpMethod.Get, "/api/beauty/overview", own)).B));
        var inactive = await Call(HttpMethod.Get, ListUrl, own);
        Assert.Equal(HttpStatusCode.Forbidden, inactive.R.StatusCode);
        Assert.Equal("specialist_inactive", Code(inactive.B));
    }

    // ---------- ціни (M4) ----------

    [SkippableFact]
    public async Task Prices_hidden_in_lists_without_revenue_all_except_own_and_always_in_detail()
    {
        NeedDb();
        var w = await WorldAsync();
        var noRevenue = await TokenAsync(w, Roles.Deputy);                     // revenue.view = none
        var ownRevenue = await TokenAsync(w, Roles.Deputy, w.S.SpecialistA);   // власні записи - з цінами
        var admin = await TokenAsync(w, Roles.Admin);                          // revenue.view = all

        foreach (var item in (await Call(HttpMethod.Get, ListUrl, noRevenue)).B.EnumerateArray())
        {
            Assert.False(item.TryGetProperty("priceFinal", out _));
            Assert.False(item.TryGetProperty("priceOriginal", out _));
        }
        var mine = (await Call(HttpMethod.Get, ListUrl, ownRevenue)).B.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
        Assert.Equal(500m, mine[w.S.AppointmentA.ToString()].GetProperty("priceFinal").GetDecimal());
        Assert.False(mine[w.S.AppointmentB.ToString()].TryGetProperty("priceFinal", out _));
        foreach (var item in (await Call(HttpMethod.Get, ListUrl, admin)).B.EnumerateArray())
            Assert.Equal(500m, item.GetProperty("priceFinal").GetDecimal());

        // деталь: ціна завжди
        var detail = await Call(HttpMethod.Get, $"/api/beauty/appointments/{w.S.AppointmentB}", noRevenue);
        Assert.Equal(500m, detail.B.GetProperty("priceFinal").GetDecimal());
    }

    [SkippableFact]
    public async Task Overview_requires_calendar_all_and_revenue_is_absent_without_revenue_all()
    {
        NeedDb();
        var w = await WorldAsync();
        var url = $"/api/beauty/overview?date={Day(2)}";

        var spec = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA);
        var (rs, bs) = await Call(HttpMethod.Get, url, spec);
        Assert.Equal(HttpStatusCode.Forbidden, rs.StatusCode);
        Assert.Equal(PermissionKeys.CalendarView, bs.GetProperty("permission").GetString());
        Assert.Equal("all", bs.GetProperty("required").GetString());

        var deputy = await TokenAsync(w, Roles.Deputy);
        var (rd, bd) = await Call(HttpMethod.Get, url, deputy);
        Assert.Equal(HttpStatusCode.OK, rd.StatusCode);
        Assert.False(bd.GetProperty("kpi").TryGetProperty("revenue", out _)); // відсутнє, а не 0 чи null
        Assert.True(bd.GetProperty("kpi").TryGetProperty("appointmentsCount", out _));
        foreach (var a in bd.GetProperty("appointments").EnumerateArray()) Assert.False(a.TryGetProperty("priceFinal", out _));

        var admin = await TokenAsync(w, Roles.Admin);
        var (ra, ba) = await Call(HttpMethod.Get, url, admin);
        Assert.Equal(HttpStatusCode.OK, ra.StatusCode);
        Assert.True(ba.GetProperty("kpi").TryGetProperty("revenue", out _));
        Assert.All(ba.GetProperty("appointments").EnumerateArray(), a => Assert.True(a.TryGetProperty("priceFinal", out _)));
    }

    // ---------- аналітика й собівартість ----------

    [SkippableFact]
    public async Task Analytics_needs_revenue_all_and_cost_fields_need_revenue_costs()
    {
        NeedDb();
        var w = await WorldAsync();
        var from = $"from={Day(-3)}&to={Day(3)}";

        var deputy = await TokenAsync(w, Roles.Deputy);
        var (rd, bd) = await Call(HttpMethod.Get, $"/api/beauty/analytics/network?{from}", deputy);
        Assert.Equal(HttpStatusCode.Forbidden, rd.StatusCode);
        Assert.Equal(PermissionKeys.RevenueView, bd.GetProperty("permission").GetString());
        Assert.Equal("all", bd.GetProperty("required").GetString());
        var ownOnly = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA, (PermissionKeys.RevenueView, "own"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"/api/beauty/analytics/network?{from}", ownOnly)).R.StatusCode);

        string[] costFields = ["cost", "masterPayout", "netIncome", "marginPercent", "withoutCost", "revenueWithoutCost"];
        var noCosts = await TokenAsync(w, Roles.Admin, null, (PermissionKeys.RevenueCosts, "none"));
        var limited = (await Call(HttpMethod.Get, $"/api/beauty/analytics/network?{from}", noCosts)).B;
        Assert.True(limited.TryGetProperty("revenue", out _));
        foreach (var f in costFields) Assert.False(limited.TryGetProperty(f, out _), f);
        var series = (await Call(HttpMethod.Get, $"/api/beauty/analytics/network/series?{from}", noCosts)).B;
        foreach (var p in series.GetProperty("points").EnumerateArray())
        {
            Assert.True(p.TryGetProperty("revenue", out _));
            foreach (var f in new[] { "cost", "masterPayout", "netIncome", "withoutCost" }) Assert.False(p.TryGetProperty(f, out _), f);
        }
        var locs = (await Call(HttpMethod.Get, $"/api/beauty/analytics/locations?{from}", noCosts)).B;
        foreach (var l in locs.EnumerateArray()) Assert.False(l.TryGetProperty("cost", out _));
        var details = (await Call(HttpMethod.Get, $"/api/beauty/analytics/network/details?{from}", noCosts)).B;
        foreach (var f in new[] { "cost", "masterPayout", "netIncome", "marginPercent", "withoutCost", "revenueWithoutCost" })
            Assert.False(details.GetProperty("summary").TryGetProperty(f, out _), f);

        var full = await TokenAsync(w, Roles.Admin);
        var all = (await Call(HttpMethod.Get, $"/api/beauty/analytics/network?{from}", full)).B;
        foreach (var f in costFields.Where(f => f != "marginPercent")) Assert.True(all.TryGetProperty(f, out _), f);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/beauty/analytics/promotions?{from}", noCosts)).R.StatusCode);
    }

    [SkippableFact]
    public async Task Service_costs_need_revenue_costs_levels_and_analytics_module()
    {
        NeedDb();
        var w = await WorldAsync();
        var deputy = await TokenAsync(w, Roles.Deputy);
        var (r0, b0) = await Call(HttpMethod.Get, "/api/beauty/service-costs", deputy);
        Assert.Equal(HttpStatusCode.Forbidden, r0.StatusCode);
        Assert.Equal(PermissionKeys.RevenueCosts, b0.GetProperty("permission").GetString());
        Assert.Equal("view", b0.GetProperty("required").GetString());

        var viewer = await TokenAsync(w, Roles.Admin, null, (PermissionKeys.RevenueCosts, "view"));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/beauty/service-costs", viewer)).R.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/beauty/services/{w.S.ServiceId}/costs", viewer)).R.StatusCode);
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            var (r, b) = await Call(m, $"/api/beauty/services/{w.S.ServiceId}/costs", viewer, m == HttpMethod.Put ? new { costAmount = 10m } : null);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("edit", b.GetProperty("required").GetString());
        }
        var editor = await TokenAsync(w, Roles.Admin);
        Assert.Equal(HttpStatusCode.OK,
            (await Call(HttpMethod.Put, $"/api/beauty/services/{w.S.ServiceId}/costs", editor, new { costAmount = 10m })).R.StatusCode);

        // D2: без beauty_analytics (з beauty_catalog) - module_disabled
        await fx.SetModulesAsync(w.T.TenantId, "beauty_booking", "beauty_catalog", "beauty_clients");
        var off = await Call(HttpMethod.Get, "/api/beauty/service-costs", editor);
        Assert.Equal(HttpStatusCode.Forbidden, off.R.StatusCode);
        Assert.Equal("module_disabled", Code(off.B));
    }

    // ---------- /me/revenue ----------

    private async Task SeedRevenueAsync(World w)
    {
        await using var db = fx.Db.CreateContext(w.T.TenantId);
        var day = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(10), TimeSpan.Zero);
        Appointment Make(Guid specialist, AppointmentStatus status, decimal price, int hour) => new()
        {
            LocationId = w.S.LocationId, SpecialistId = specialist, ServiceId = w.S.ServiceId, ClientId = w.S.ClientId,
            StartsAt = day.AddHours(hour), DurationMinutes = 60, Status = status, Source = AppointmentSource.Admin,
            PriceOriginal = price, PriceFinal = price,
        };
        db.AddRange(
            Make(w.S.SpecialistA, AppointmentStatus.Completed, 700m, 0),
            Make(w.S.SpecialistA, AppointmentStatus.Completed, 300m, 2),
            Make(w.S.SpecialistA, AppointmentStatus.Cancelled, 5000m, 4),
            Make(w.S.SpecialistA, AppointmentStatus.NoShow, 6000m, 6),
            Make(w.S.SpecialistB, AppointmentStatus.Completed, 999m, 0));
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Me_revenue_counts_only_own_completed_and_validates_range()
    {
        NeedDb();
        var w = await WorldAsync();
        await SeedRevenueAsync(w);
        var token = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA, (PermissionKeys.RevenueView, "own"));
        var url = $"/api/beauty/me/revenue?from={Day(-3)}&to={Day(3)}";

        var (r, b) = await Call(HttpMethod.Get, url + $"&specialistId={w.S.SpecialistB}", token); // specialistId у запиті ігнорується
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal(w.S.SpecialistA.ToString(), b.GetProperty("specialistId").GetString());
        Assert.Equal(2, b.GetProperty("completed").GetInt32());
        Assert.Equal(1000m, b.GetProperty("revenue").GetDecimal());
        var loc = Assert.Single(b.GetProperty("byLocation").EnumerateArray());
        Assert.Equal(1000m, loc.GetProperty("revenue").GetDecimal());
        foreach (var f in new[] { "cost", "masterPayout", "netIncome" }) Assert.False(b.TryGetProperty(f, out _));

        // revenue.view = all теж дає лише власні (адмін без профілю: нулі)
        var admin = await TokenAsync(w, Roles.Admin);
        var (_, ba) = await Call(HttpMethod.Get, url, admin);
        Assert.Equal(JsonValueKind.Null, ba.GetProperty("specialistId").ValueKind);
        Assert.Equal(0, ba.GetProperty("completed").GetInt32());
        Assert.Equal(0m, ba.GetProperty("revenue").GetDecimal());
        Assert.Empty(ba.GetProperty("byLocation").EnumerateArray());

        // валідація
        foreach (var bad in new[]
                 {
                     "/api/beauty/me/revenue", $"/api/beauty/me/revenue?from={Day(0)}", $"/api/beauty/me/revenue?from={Day(3)}&to={Day(0)}",
                     $"/api/beauty/me/revenue?from={Day(-100)}&to={Day(0)}", "/api/beauty/me/revenue?from=2026-13-01&to=2026-13-02",
                     "/api/beauty/me/revenue?from=1999-01-01&to=1999-01-02",
                 })
        {
            var (rb, bb) = await Call(HttpMethod.Get, bad, token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rb.StatusCode);
            Assert.Equal("invalid_range", Code(bb));
        }
        // 92 дні - межа дозволена
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/beauty/me/revenue?from={Day(-91)}&to={Day(0)}", token)).R.StatusCode);
    }

    [SkippableFact]
    public async Task Me_revenue_permission_module_and_inactive_profile()
    {
        NeedDb();
        var w = await WorldAsync();
        var url = $"/api/beauty/me/revenue?from={Day(-3)}&to={Day(3)}";
        var user = await fx.CreateUserAsync(w.T, Roles.Specialist, w.S.SpecialistA, Perms(Roles.Specialist));
        var none = user.Tokens.AccessToken;
        var (r, b) = await Call(HttpMethod.Get, url, none);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(PermissionKeys.RevenueView, b.GetProperty("permission").GetString());
        Assert.Equal("own", b.GetProperty("required").GetString());

        // порядок фільтрів: вимкнений модуль раніше за відсутнє право
        await fx.SetModulesAsync(w.T.TenantId, "beauty_booking", "beauty_catalog", "beauty_clients");
        Assert.Equal("module_disabled", Code((await Call(HttpMethod.Get, url, none)).B));
        await fx.MutateUserAsync(w.T.TenantId, user.UserId, _ => { }, Perms(Roles.Specialist, (PermissionKeys.RevenueView, "own")));
        var granted = none;
        Assert.Equal("module_disabled", Code((await Call(HttpMethod.Get, url, granted)).B));
        await fx.SetModulesAsync(w.T.TenantId, [.. BeautyModules.All]);

        await using (var db = fx.Db.CreateContext(w.T.TenantId))
            await db.Specialists.Where(s => s.Id == w.S.SpecialistA).ExecuteUpdateAsync(x => x.SetProperty(s => s.IsActive, false));
        Assert.Equal("specialist_inactive", Code((await Call(HttpMethod.Get, url, granted)).B));
    }

    // ---------- created_by_user_id і scope клієнта (M3) ----------

    [SkippableFact]
    public async Task Staff_created_appointment_records_creator()
    {
        NeedDb();
        var salon = await new RegressionHarness(fx).CreateSalonAsync();
        var admin = await fx.CreateUserAsync(salon.Tenant, Roles.Admin, null, Perms(Roles.Admin));
        var (r, b) = await Call(HttpMethod.Post, "/api/beauty/appointments", admin.Tokens.AccessToken, new
        {
            locationId = salon.LocationId, specialistId = salon.SpecialistId, serviceId = salon.ServiceId,
            startsAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(10), TimeSpan.Zero),
            client = new { name = "Anna", phone = "+380501112233" }, reminder = "none", paymentMethod = "cash",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        await using var db = fx.Db.CreateContext(salon.Tenant.TenantId);
        var row = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == b.GetProperty("id").GetGuid());
        Assert.Equal(admin.UserId, row.CreatedByUserId);
    }

    [SkippableFact]
    public async Task Create_with_client_id_is_limited_to_clients_view_scope()
    {
        NeedDb();
        var w = await WorldAsync();
        object Body() => new
        {
            locationId = w.S.LocationId, specialistId = w.S.SpecialistA, serviceId = w.S.ServiceId,
            startsAt = DateTimeOffset.UtcNow.AddDays(6), client = new { id = w.S.ClientId }, paymentMethod = "cash",
        };

        // clients.view = none: клієнт поза scope = як неіснуючий
        var none = await fx.CreateUserAsync(w.T, Roles.Specialist, w.S.SpecialistA, Perms(Roles.Specialist));
        var (r0, b0) = await Call(HttpMethod.Post, "/api/beauty/appointments", none.Tokens.AccessToken, Body());
        Assert.Equal(HttpStatusCode.NotFound, r0.StatusCode);
        Assert.Equal("client_not_found", Code(b0));

        // all: scope не блокує (далі можливі лише бізнес-помилки слота)
        var deputy = await TokenAsync(w, Roles.Deputy);
        Assert.NotEqual("client_not_found", Code((await Call(HttpMethod.Post, "/api/beauty/appointments", deputy, Body())).B));

        // own: клієнт має запис у власного майстра
        await fx.MutateUserAsync(w.T.TenantId, none.UserId, _ => { }, Perms(Roles.Specialist, (PermissionKeys.ClientsView, "own")));
        var own = none;
        Assert.NotEqual("client_not_found", Code((await Call(HttpMethod.Post, "/api/beauty/appointments", own.Tokens.AccessToken, Body())).B));

        // M3: запис, створений самим актором і скасований, scope не відкриває
        await using (var db = fx.Db.CreateContext(w.T.TenantId))
            await db.Appointments.Where(a => a.Id == w.S.AppointmentA).ExecuteUpdateAsync(x => x
                .SetProperty(a => a.Status, AppointmentStatus.Cancelled).SetProperty(a => a.CreatedByUserId, own.UserId));
        var (r1, b1) = await Call(HttpMethod.Post, "/api/beauty/appointments", own.Tokens.AccessToken, Body());
        Assert.Equal(HttpStatusCode.NotFound, r1.StatusCode);
        Assert.Equal("client_not_found", Code(b1));
    }

    // ---------- staff, locations, absences ----------

    [SkippableFact]
    public async Task Staff_and_locations_writes_need_edit_level()
    {
        NeedDb();
        var w = await WorldAsync();
        var deputy = await TokenAsync(w, Roles.Deputy); // staff.access = all, locations.access = all (не edit)
        var (r, b) = await Call(HttpMethod.Post, "/api/beauty/specialists", deputy, new { name = "N" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(PermissionKeys.StaffAccess, b.GetProperty("permission").GetString());
        Assert.Equal("edit", b.GetProperty("required").GetString());
        var (rl, bl) = await Call(HttpMethod.Put, $"/api/beauty/locations/{w.S.LocationId}", deputy, new { name = "N", timezone = "UTC" });
        Assert.Equal(HttpStatusCode.Forbidden, rl.StatusCode);
        Assert.Equal(PermissionKeys.LocationsAccess, bl.GetProperty("permission").GetString());
        // довідники лишаються відкритими
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/beauty/specialists", deputy)).R.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/beauty/locations", deputy)).R.StatusCode);
        // з edit - дозволено
        var admin = await TokenAsync(w, Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/beauty/specialists/{w.S.SpecialistB}", admin, new { name = "Renamed" })).R.StatusCode);
        // phone/hasAccount лише за edit
        var seen = (await Call(HttpMethod.Get, "/api/beauty/specialists", deputy)).B.EnumerateArray().First();
        Assert.False(seen.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.String);
    }

    [SkippableFact]
    public async Task Absence_type_of_foreign_absence_is_hidden_without_staff_edit_D7()
    {
        NeedDb();
        var w = await WorldAsync();
        var (rc, bc) = await Call(HttpMethod.Post, $"/api/beauty/specialists/{w.S.SpecialistA}/absences", w.Owner,
            new { type = "sick", dateFrom = Day(20), dateTo = Day(21) });
        Assert.Equal(HttpStatusCode.Created, rc.StatusCode);
        Assert.Equal("approved", bc.GetProperty("status").GetString());
        var url = $"/api/beauty/absences?from={Day(18)}&to={Day(25)}";

        var other = await TokenAsync(w, Roles.Specialist, w.S.SpecialistB);
        var foreign = (await Call(HttpMethod.Get, url, other)).B.EnumerateArray().Single();
        Assert.False(foreign.TryGetProperty("type", out _));
        Assert.Equal("approved", foreign.GetProperty("status").GetString());

        var deputy = await TokenAsync(w, Roles.Deputy); // staff.access = all (не edit)
        Assert.False((await Call(HttpMethod.Get, url, deputy)).B.EnumerateArray().Single().TryGetProperty("type", out _));

        var mine = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA);
        Assert.Equal("sick", (await Call(HttpMethod.Get, url, mine)).B.EnumerateArray().Single().GetProperty("type").GetString());
        Assert.Equal("sick", (await Call(HttpMethod.Get, url, w.Owner)).B.EnumerateArray().Single().GetProperty("type").GetString());
    }

    // ---------- coupons/check ----------

    [SkippableFact]
    public async Task Coupon_check_needs_calendar_manage()
    {
        NeedDb();
        var w = await WorldAsync();
        var none = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA, (PermissionKeys.CalendarManage, "none"));
        var (r, b) = await Call(HttpMethod.Post, "/api/beauty/coupons/check", none, new { code = "ABC", locationId = w.S.LocationId, serviceId = w.S.ServiceId });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(PermissionKeys.CalendarManage, b.GetProperty("permission").GetString());
    }
}
