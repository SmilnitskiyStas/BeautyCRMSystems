using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyStaff;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-703 (§18.1, §18.2): години роботи закладу. Повний HTTP-конвеєр на реальному PostgreSQL (роль без BYPASSRLS):
/// перетин із графіком майстра, слоти staff/public, запис/перенос, confirm-флоу, ролі, IDOR, часові зони, гонки.
/// Специфікація майстра в тестах: 09:00-18:00 щодня, послуга 60 хв, заклад у UTC, якщо не сказано інше.
/// </summary>
public sealed class LocationWorkingHoursApiTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _public = null!;

    public LocationWorkingHoursApiTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
        if (fx.SkipReason is null)
        {
            var f = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
            {
                b.UseSetting("PublicBooking:RateLimit:ReadPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:TokenPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:WritePermit", "10000");
                b.UseSetting("PublicBooking:MaxActiveBookingsPerPhone", "100");
                b.UseSetting("PublicBooking:MaxCreatesPerPhonePerHour", "100");
            });
            _factories.Add(f);
            _public = f.CreateClient();
        }
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    // ---------- утиліти ----------

    private static DateOnly D(int ahead) => DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(ahead));
    private static string Day(int ahead) => D(ahead).ToString("yyyy-MM-dd");
    private static string Key(DateOnly d) => SlotCalculator.DayKey(d.DayOfWeek);
    private static DateTimeOffset At(int ahead, int hour = 10, int minute = 0) =>
        new(DateTime.UtcNow.Date.AddDays(ahead).AddHours(hour).AddMinutes(minute), TimeSpan.Zero);
    private static DateTimeOffset Local(DateOnly d, int hour, int minute, string tz) =>
        SlotCalculator.ToOffset(d, new TimeOnly(hour, minute), TimeZoneInfo.FindSystemTimeZoneById(tz));

    private static object[] Iv(params (string From, string To)[] xs) => xs.Select(x => (object)new { from = x.From, to = x.To }).ToArray();
    private static Dictionary<string, object[]> AllDays(string from, string to) => Days.ToDictionary(d => d, _ => Iv((from, to)));

    private Task<HttpResponseMessage> Api(HttpMethod m, string url, string token, object? body = null) => _fx.Send(m, url, body, token);
    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;
    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed record Ctx(RegressionHarness.Salon Salon, TokenResponse Admin, TokenResponse Specialist, string Timezone)
    {
        public string Owner => Salon.OwnerToken;
        public string Base => $"/api/beauty/locations/{Salon.LocationId}";
    }

    private async Task<Ctx> NewCtxAsync(string timezone = "UTC")
    {
        var salon = await _h.CreateSalonAsync(timezone: timezone);
        var admin = await _fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Admin);
        var specialist = await _fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Specialist, salon.SpecialistId);
        return new Ctx(salon, admin, specialist, timezone);
    }

    private Task<HttpResponseMessage> PutHours(Ctx c, string token, object? hours, bool? confirm = null) =>
        Api(HttpMethod.Put, $"{c.Base}/working-hours", token, new { workingHours = hours, confirm });

    private async Task SetHoursAsync(Ctx c, object? hours, bool confirm = false)
    {
        var r = await PutHours(c, c.Owner, hours, confirm ? true : null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private Task<HttpResponseMessage> TryBookAsync(Ctx c, DateTimeOffset at, string phone = "+380501112233") =>
        _fx.Send(HttpMethod.Post, "/api/beauty/appointments", RegressionHarness.Booking(c.Salon, at, phone: phone), c.Owner);

    private async Task<Guid> BookAsync(Ctx c, DateTimeOffset at, string phone = "+380501112233")
    {
        var r = await TryBookAsync(c, at, phone);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PublicBookAsync(Ctx c, DateTimeOffset at, string phone = "+380677778811")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{c.Salon.Tenant.Slug}/appointments")
        {
            Content = JsonContent.Create(new
            {
                locationId = c.Salon.LocationId, specialistId = c.Salon.SpecialistId, serviceId = c.Salon.ServiceId, startsAt = at,
                client = new { name = "Olena Test", phone }, reminder = "none", paymentMethod = "cash",
            }, options: Json),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return _public.SendAsync(req);
    }

    private async Task<List<string>> LabelsAsync(Ctx c, int ahead, string? token = null)
    {
        var r = await Api(HttpMethod.Get,
            $"/api/beauty/slots?locationId={c.Salon.LocationId}&serviceId={c.Salon.ServiceId}&date={Day(ahead)}", token ?? c.Owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).EnumerateArray().Select(x => x.GetProperty("label").GetString()!).ToList();
    }

    private async Task<List<string>> PublicLabelsAsync(Ctx c, int ahead)
    {
        var r = await _public.GetAsync(
            $"/api/public/{c.Salon.Tenant.Slug}/slots?locationId={c.Salon.LocationId}&serviceId={c.Salon.ServiceId}&date={Day(ahead)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).EnumerateArray().Select(x => x.GetProperty("label").GetString()!).ToList();
    }

    private async Task<string?> StoredHoursAsync(Ctx c)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        return (await ctx.Locations.AsNoTracking().SingleAsync(l => l.Id == c.Salon.LocationId)).WorkingHours;
    }

    private async Task<Guid> ClientIdAsync(Ctx c)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        var client = new Client { FullName = "Edge", Phone = "+380990000001" };
        ctx.Add(client);
        await ctx.SaveChangesAsync();
        return client.Id;
    }

    private async Task<Guid> SeedAsync(Ctx c, Guid clientId, DateTimeOffset start, AppointmentStatus status = AppointmentStatus.Confirmed,
        int minutes = 60, Guid? locationId = null)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        var a = new Appointment
        {
            LocationId = locationId ?? c.Salon.LocationId, SpecialistId = c.Salon.SpecialistId, ServiceId = c.Salon.ServiceId,
            ClientId = clientId, StartsAt = start.ToUniversalTime(), DurationMinutes = minutes, Status = status, Source = AppointmentSource.Admin,
            PriceOriginal = 1, PriceFinal = 1,
        };
        ctx.Add(a);
        await ctx.SaveChangesAsync();
        return a.Id;
    }

    private async Task<AppointmentStatus> StatusAsync(Ctx c, Guid id)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        return (await ctx.Appointments.AsNoTracking().SingleAsync(a => a.Id == id)).Status;
    }

    // ================= слоти =================

    [SkippableFact]
    public async Task unset_hours_change_nothing_and_set_hours_intersect_with_the_specialist_schedule()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var list = await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Owner));
        var loc = list.EnumerateArray().Single();
        Assert.True(loc.TryGetProperty("workingHours", out var wh));
        Assert.Equal(JsonValueKind.Null, wh.ValueKind);
        Assert.Equal(33, (await LabelsAsync(c, 7)).Count); // 09:00..17:00 крок 15 хв
        Assert.Equal(33, (await PublicLabelsAsync(c, 7)).Count);

        string? scheduleBefore;
        await using (var before = _fx.Db.CreateContext(c.Salon.Tenant.TenantId))
            scheduleBefore = (await before.SpecialistLocations.AsNoTracking().SingleAsync()).WorkingHours;

        var put = await PutHours(c, c.Owner, AllDays("10:00", "14:00"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await J(put);
        Assert.Equal("10:00", body.GetProperty("workingHours").GetProperty("mon")[0].GetProperty("from").GetString());

        // 10:00..13:00 (60 хв закінчується о 14:00): 13 слотів; і staff, і public
        foreach (var labels in new[] { await LabelsAsync(c, 7), await PublicLabelsAsync(c, 7) })
        {
            Assert.Equal(13, labels.Count);
            Assert.Equal("10:00", labels.First());
            Assert.Equal("13:00", labels.Last());
        }
        // specialist-роль бачить ті самі слоти
        Assert.Equal(13, (await LabelsAsync(c, 7, c.Specialist.AccessToken)).Count);

        // графік майстра не змінено
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        Assert.Equal(scheduleBefore, (await ctx.SpecialistLocations.AsNoTracking().SingleAsync()).WorkingHours);

        // скидання в null повертає поведінку «без обмеження»
        await SetHoursAsync(c, null);
        Assert.Equal(33, (await LabelsAsync(c, 7)).Count);
        Assert.Null(await StoredHoursAsync(c));
    }

    [SkippableFact]
    public async Task day_without_intervals_has_no_slots_and_rejects_booking()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await SetHoursAsync(c, new Dictionary<string, object[]> { ["mon"] = Iv(("10:00", "14:00")) });
        var mon = Enumerable.Range(7, 7).First(a => Key(D(a)) == "mon");
        var other = Enumerable.Range(7, 7).First(a => Key(D(a)) != "mon");

        Assert.Equal(13, (await LabelsAsync(c, mon)).Count);
        Assert.Empty(await LabelsAsync(c, other));
        Assert.Empty(await PublicLabelsAsync(c, other));
        var r = await TryBookAsync(c, At(other, 11));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("outside_location_hours", await Code(r));
        Assert.Equal(HttpStatusCode.Created, (await TryBookAsync(c, At(mon, 11))).StatusCode);

        // явний порожній об'єкт = жодного робочого дня
        await SetHoursAsync(c, new { }, confirm: true);
        Assert.Empty(await LabelsAsync(c, mon));
        Assert.Equal("{}", await StoredHoursAsync(c));
    }

    [SkippableFact]
    public async Task overview_free_slots_respect_location_hours()
    {
        NeedDb();
        var c = await NewCtxAsync();
        async Task<int> FreeAsync() =>
            (await J(await Api(HttpMethod.Get, $"/api/beauty/overview?date={Day(7)}", c.Owner))).GetProperty("kpi").GetProperty("freeSlots").GetInt32();
        Assert.Equal(9, await FreeAsync());
        await SetHoursAsync(c, AllDays("10:00", "14:00"));
        Assert.Equal(4, await FreeAsync());
    }

    // ================= створення / перенос =================

    [SkippableFact]
    public async Task booking_outside_hours_is_409_for_staff_and_generic_slot_unavailable_for_public()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await SetHoursAsync(c, AllDays("10:00", "14:00"));

        var early = await TryBookAsync(c, At(7, 9, 30));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("outside_location_hours", await Code(early));
        var late = await TryBookAsync(c, At(7, 13, 15)); // закінчується о 14:15
        Assert.Equal("outside_location_hours", await Code(late));
        Assert.Equal(HttpStatusCode.Created, (await TryBookAsync(c, At(7, 13, 0))).StatusCode); // рівно до межі
        Assert.Equal(HttpStatusCode.Created, (await TryBookAsync(c, At(7, 10, 0))).StatusCode); // рівно від межі

        // поза годинами закладу, але й поза графіком майстра -> пріоритет за годинами закладу (409, не 422)
        var both = await TryBookAsync(c, At(7, 8, 0));
        Assert.Equal(HttpStatusCode.Conflict, both.StatusCode);
        Assert.Equal("outside_location_hours", await Code(both));

        // public: поза годинами = те саме тіло, що й зайнятий слот
        Assert.Equal(HttpStatusCode.Created, (await PublicBookAsync(c, At(7, 11, 0), "+380677778801")).StatusCode);
        var busy = await PublicBookAsync(c, At(7, 11, 30), "+380677778802");
        var outside = await PublicBookAsync(c, At(7, 9, 30), "+380677778803");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, outside.StatusCode);
        Assert.Equal(await busy.Content.ReadAsStringAsync(), await outside.Content.ReadAsStringAsync());
        Assert.Equal("slot_unavailable", await Code(outside));
    }

    [SkippableFact]
    public async Task only_specialist_hours_outside_still_gives_outside_working_hours_and_checks_keep_their_order()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await SetHoursAsync(c, AllDays("07:00", "20:00"));
        // у годинах закладу, але раніше за графік майстра -> 422 outside_working_hours (як і раніше)
        var r = await TryBookAsync(c, At(7, 8, 0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("outside_working_hours", await Code(r));

        await SetHoursAsync(c, AllDays("10:00", "14:00"));
        // відсутність майстра раніше за години закладу
        var abs = await Api(HttpMethod.Post, $"/api/beauty/specialists/{c.Salon.SpecialistId}/absences", c.Owner,
            new { type = "day_off", dateFrom = Day(8), dateTo = Day(8) });
        Assert.Equal(HttpStatusCode.Created, abs.StatusCode);
        var unavailable = await TryBookAsync(c, At(8, 16, 0));
        Assert.Equal("specialist_unavailable", await Code(unavailable));

        // вихідний день закладу раніше за відсутність і години
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"{c.Base}/closed-weekdays", c.Owner,
            new { closedWeekdays = new[] { Key(D(9)) } })).StatusCode);
        var closed = await TryBookAsync(c, At(9, 16, 0));
        Assert.Equal("location_closed", await Code(closed));

        // у минулому -> slot_in_past раніше за все
        var past = await TryBookAsync(c, At(-1, 16, 0));
        Assert.Equal("slot_in_past", await Code(past));
    }

    [SkippableFact]
    public async Task reschedule_into_hours_works_and_outside_hours_is_409_while_status_changes_stay_possible()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await SetHoursAsync(c, AllDays("10:00", "14:00"));
        var id = await BookAsync(c, At(7, 11, 0));

        var outside = await Api(HttpMethod.Patch, $"/api/beauty/appointments/{id}", c.Owner, new { startsAt = At(7, 15, 0) });
        Assert.Equal(HttpStatusCode.Conflict, outside.StatusCode);
        Assert.Equal("outside_location_hours", await Code(outside));
        var inside = await Api(HttpMethod.Patch, $"/api/beauty/appointments/{id}", c.Owner, new { startsAt = At(7, 12, 0) });
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        // години звужено з підтвердженням: наявний запис поза годинами може бути завершений/скасований
        Assert.Equal(HttpStatusCode.Conflict, (await PutHours(c, c.Owner, AllDays("13:00", "14:00"))).StatusCode);
        await SetHoursAsync(c, AllDays("13:00", "14:00"), confirm: true);
        var done = await Api(HttpMethod.Patch, $"/api/beauty/appointments/{id}", c.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var other = await BookAsync(c, At(8, 13, 0), "+380501112244");
        var cancel = await Api(HttpMethod.Post, $"/api/beauty/appointments/{other}/cancel", c.Owner, new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    // ================= confirm-флоу =================

    [SkippableFact]
    public async Task narrowing_hours_needs_confirmation_lists_only_newly_unavailable_appointments_and_cancels_nothing()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await SetHoursAsync(c, AllDays("09:00", "18:00"));
        var client = await ClientIdAsync(c);
        var other = await _h.AddLocationAsync(c.Salon.Tenant.TenantId, c.Salon.SpecialistId);

        var a = await BookAsync(c, At(7, 9, 0));                      // вміщувався, у нових 10-14 не вміщується -> конфлікт
        var b = await BookAsync(c, At(7, 11, 0), "+380501112244");    // вміщується -> ні
        var cc = await BookAsync(c, At(8, 15, 0), "+380501112255");   // конфлікт
        var d = await SeedAsync(c, client, At(7, 8, 0), minutes: 45); // уже поза СТАРИМИ годинами -> не «саме цією зміною»
        var e = await SeedAsync(c, client, At(9, 9, 0), AppointmentStatus.Cancelled);
        var f = await SeedAsync(c, client, At(9, 15, 0), AppointmentStatus.Completed);
        var g = await SeedAsync(c, client, At(-1, 9, 0), AppointmentStatus.Pending); // минуле
        var h = await SeedAsync(c, client, At(7, 15, 0), locationId: other.LocationId); // інший заклад
        var closedDay = await SeedAsync(c, client, At(10, 9, 0));
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"{c.Base}/closed-weekdays", c.Owner,
            new { closedWeekdays = new[] { Key(D(10)) }, confirm = true })).StatusCode);
        var storedBefore = await StoredHoursAsync(c);
        var labelsBefore = await LabelsAsync(c, 7);

        var r = await PutHours(c, c.Owner, AllDays("10:00", "14:00"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var raw = await r.Content.ReadAsStringAsync();
        var body = Raw(raw);
        Assert.Equal("has_appointments_outside_hours", body.GetProperty("code").GetString());
        var conflicts = body.GetProperty("conflicts").EnumerateArray().ToList();
        Assert.Equal(new[] { a, cc }, conflicts.Select(x => x.GetProperty("appointmentId").GetGuid()).ToArray());
        Assert.Equal(At(7, 9, 0), conflicts[0].GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal("Haircut", conflicts[0].GetProperty("serviceName").GetString());
        Assert.Equal("Master", conflicts[0].GetProperty("specialistName").GetString());
        foreach (var x in conflicts)
            Assert.Equal(new[] { "appointmentId", "serviceName", "specialistName", "startsAt" },
                x.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("Anna", raw);
        Assert.DoesNotContain("380501112233", raw);
        // зміни немає
        Assert.Equal(storedBefore, await StoredHoursAsync(c));
        Assert.Equal(labelsBefore, await LabelsAsync(c, 7));

        // confirm=true застосовує, записи не скасовано й не перенесено
        var ok = await PutHours(c, c.Owner, AllDays("10:00", "14:00"), confirm: true);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.All(await LabelsAsync(c, 7), l => Assert.InRange(string.CompareOrdinal(l, "10:00"), 0, 1000)); // не раніше 10:00
        Assert.All(await LabelsAsync(c, 7), l => Assert.True(string.CompareOrdinal(l, "13:00") <= 0));
        foreach (var id in new[] { a, b, cc, d, closedDay, h })
            Assert.Equal(AppointmentStatus.Confirmed, await StatusAsync(c, id));
        Assert.Equal(AppointmentStatus.Cancelled, await StatusAsync(c, e));
        Assert.Equal(AppointmentStatus.Completed, await StatusAsync(c, f));
        Assert.Equal(AppointmentStatus.Pending, await StatusAsync(c, g));
        await using (var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId))
            Assert.Equal(At(7, 9, 0), (await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == a)).StartsAt);

        // те саме збереження, розширення та скидання підтвердження не потребують (нічого нового не стає недоступним)
        Assert.Equal(HttpStatusCode.OK, (await PutHours(c, c.Owner, AllDays("10:00", "14:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutHours(c, c.Owner, AllDays("08:00", "20:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutHours(c, c.Owner, null)).StatusCode);
        Assert.Null(await StoredHoursAsync(c));
    }

    [SkippableFact]
    public async Task confirmation_is_decided_per_local_day_of_the_location_time_zone()
    {
        NeedDb();
        var c = await NewCtxAsync("Pacific/Auckland"); // UTC+12/+13
        var client = await ClientIdAsync(c);
        var wed = D(Enumerable.Range(5, 7).First(a => D(a).DayOfWeek == DayOfWeek.Wednesday));
        var tue = wed.AddDays(-1);
        // локальна середа 09:00 = вівторок 20:00/21:00 за UTC: за UTC-днем вона «вміщалась» б у години вівторка
        var wednesdayAppt = await SeedAsync(c, client, Local(wed, 9, 0, c.Timezone));
        var tuesdayAppt = await SeedAsync(c, client, Local(tue, 10, 0, c.Timezone));
        Assert.Equal(DayOfWeek.Monday, Local(tue, 10, 0, c.Timezone).UtcDateTime.DayOfWeek); // а цей - понеділок за UTC

        var r = await PutHours(c, c.Owner, new Dictionary<string, object[]> { ["tue"] = Iv(("00:00", "23:59")) });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var only = Assert.Single((await J(r)).GetProperty("conflicts").EnumerateArray());
        Assert.Equal(wednesdayAppt, only.GetProperty("appointmentId").GetGuid());
        Assert.NotEqual(tuesdayAppt, only.GetProperty("appointmentId").GetGuid());

        // локальний час, а не UTC: 09:00 Окленд вміщується в 09:00-12:00 щодня
        var fit = await PutHours(c, c.Owner, AllDays("09:00", "12:00"));
        Assert.Equal(HttpStatusCode.OK, fit.StatusCode);
    }

    [SkippableFact]
    public async Task slots_and_booking_use_local_time_of_the_location_and_night_boundaries()
    {
        NeedDb();
        var c = await NewCtxAsync("Pacific/Auckland");
        await SetHoursAsync(c, AllDays("08:00", "12:00"));
        var d = D(7);
        // 09:00-12:00 (перетин з 09:00-18:00 майстра), 60 хв -> 09:00..11:00 щокроку 15 хв
        var labels = await LabelsAsync(c, 7);
        Assert.Equal(9, labels.Count);
        Assert.Equal("09:00", labels.First());
        Assert.Equal("11:00", labels.Last());
        Assert.Equal(HttpStatusCode.Created, (await TryBookAsync(c, Local(d, 11, 0, c.Timezone))).StatusCode);
        var late = await TryBookAsync(c, Local(d, 11, 15, c.Timezone), "+380501112244");
        Assert.Equal("outside_location_hours", await Code(late));
        var atTwelve = await TryBookAsync(c, Local(d, 12, 0, c.Timezone), "+380501112255");
        Assert.Equal("outside_location_hours", await Code(atTwelve));
        // 08:30: у годинах закладу, але раніше за графік майстра
        var early = await TryBookAsync(c, Local(d, 8, 30, c.Timezone), "+380501112266");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal("outside_working_hours", await Code(early));
    }

    [SkippableFact]
    public async Task late_evening_hours_stop_before_midnight()
    {
        NeedDb();
        var c = await NewCtxAsync("Europe/Kyiv");
        var sched = await Api(HttpMethod.Put, $"/api/beauty/specialists/{c.Salon.SpecialistId}/schedule", c.Owner,
            new { locationId = c.Salon.LocationId, workingHours = AllDays("18:00", "23:59") });
        Assert.Equal(HttpStatusCode.OK, sched.StatusCode);
        await SetHoursAsync(c, AllDays("22:00", "23:59"));

        Assert.Equal(new[] { "22:00", "22:15", "22:30", "22:45" }, await LabelsAsync(c, 7)); // 23:00 + 60 хв = північ - не вміщується
        var d = D(7);
        Assert.Equal(HttpStatusCode.Created, (await TryBookAsync(c, Local(d, 22, 45, c.Timezone))).StatusCode);
        var midnight = await TryBookAsync(c, Local(d, 23, 0, c.Timezone), "+380501112244");
        Assert.Equal("outside_location_hours", await Code(midnight));
        var before = await TryBookAsync(c, Local(d, 21, 45, c.Timezone), "+380501112255");
        Assert.Equal("outside_location_hours", await Code(before));
    }

    // ================= валідація =================

    [SkippableFact]
    public async Task invalid_working_hours_are_rejected_and_valid_ones_are_normalized()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var nine = Enumerable.Range(0, 9).Select(i => "{\"from\":\"" + i.ToString("D2") + ":00\",\"to\":\"" + i.ToString("D2") + ":30\"}");
        var bad = new[]
        {
            "{}",                                                                      // поле workingHours відсутнє
            """{"workingHours":"09:00-18:00"}""",
            """{"workingHours":[]}""",
            """{"workingHours":{"funday":[{"from":"09:00","to":"10:00"}]}}""",
            """{"workingHours":{"mon":[{"from":"10:00","to":"09:00"}]}}""",
            """{"workingHours":{"mon":[{"from":"09:00","to":"09:00"}]}}""",
            """{"workingHours":{"mon":[{"from":"09:00","to":"12:00"},{"from":"11:00","to":"13:00"}]}}""",
            """{"workingHours":{"mon":[{"from":"9:00","to":"10:00"}]}}""",
            """{"workingHours":{"mon":[{"from":"09:00","to":"24:00"}]}}""",
            """{"workingHours":{"mon":"x"}}""",
            "{\"workingHours\":{\"mon\":[" + string.Join(",", nine) + "]}}",
        };
        foreach (var json in bad)
        {
            var r = await Api(HttpMethod.Put, $"{c.Base}/working-hours", c.Owner, Raw(json));
            Assert.True(r.StatusCode == HttpStatusCode.UnprocessableEntity, $"{json} -> {r.StatusCode}");
            Assert.Equal("invalid_working_hours", await Code(r));
        }
        Assert.Null(await StoredHoursAsync(c));

        var ok = await Api(HttpMethod.Put, $"{c.Base}/working-hours", c.Owner, Raw(
            """{"workingHours":{"wed":[{"from":"14:00","to":"18:00"},{"from":"09:00","to":"12:00"}],"tue":[]}}"""));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var wh = (await J(ok)).GetProperty("workingHours");
        Assert.False(wh.TryGetProperty("tue", out _)); // порожні дні відкидаються
        Assert.Equal("09:00", wh.GetProperty("wed")[0].GetProperty("from").GetString()); // відсортовано
        Assert.Equal("14:00", wh.GetProperty("wed")[1].GetProperty("from").GetString());
    }

    // ================= ролі / IDOR =================

    [SkippableFact]
    public async Task only_managers_change_hours_everyone_reads_them_and_foreign_tenant_is_not_found()
    {
        NeedDb();
        var mine = await NewCtxAsync();
        var theirs = await NewCtxAsync();
        await SetHoursAsync(theirs, AllDays("10:00", "14:00"));

        Assert.Equal(HttpStatusCode.Forbidden, (await PutHours(mine, mine.Specialist.AccessToken, AllDays("10:00", "14:00"))).StatusCode);
        Assert.Null(await StoredHoursAsync(mine));
        Assert.Equal(HttpStatusCode.OK, (await PutHours(mine, mine.Admin.AccessToken, AllDays("11:00", "15:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _fx.Send(HttpMethod.Put, $"{mine.Base}/working-hours", new { workingHours = new { } })).StatusCode);

        // specialist читає години у списку закладів
        var locs = await J(await Api(HttpMethod.Get, "/api/beauty/locations", mine.Specialist.AccessToken));
        Assert.Equal("11:00", locs.EnumerateArray().Single().GetProperty("workingHours").GetProperty("mon")[0].GetProperty("from").GetString());

        // чужий заклад: 404 і без змін (і для confirm, і для null)
        var foreign = await Api(HttpMethod.Put, $"{theirs.Base}/working-hours", mine.Owner, new { workingHours = (object?)null, confirm = true });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("location_not_found", await Code(foreign));
        Assert.NotNull(await StoredHoursAsync(theirs));
        // невідомий id
        Assert.Equal(HttpStatusCode.NotFound,
            (await Api(HttpMethod.Put, $"/api/beauty/locations/{Guid.NewGuid()}/working-hours", mine.Owner, new { workingHours = new { } })).StatusCode);
    }

    // ================= публічний каталог =================

    [SkippableFact]
    public async Task public_catalog_exposes_working_hours_or_null_and_nothing_extra()
    {
        NeedDb();
        var c = await NewCtxAsync();
        async Task<JsonElement> CatalogAsync()
        {
            var r = await _public.GetAsync($"/api/public/{c.Salon.Tenant.Slug}/locations");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (await J(r)).EnumerateArray().Single();
        }

        var unset = await CatalogAsync();
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("workingHours").ValueKind);

        await SetHoursAsync(c, new Dictionary<string, object[]> { ["mon"] = Iv(("09:00", "13:00"), ("14:00", "18:00")) });
        var loc = await CatalogAsync();
        Assert.Equal(new[] { "address", "closedWeekdays", "closures", "id", "name", "phone", "timezone", "workingHours" },
            loc.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        var mon = loc.GetProperty("workingHours").GetProperty("mon");
        Assert.Equal(2, mon.GetArrayLength());
        Assert.Equal("14:00", mon[1].GetProperty("from").GetString());
        Assert.Equal(new[] { "mon" }, loc.GetProperty("workingHours").EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ================= §18.2: спеціалісти закладу через наявний GET /specialists =================

    [SkippableFact]
    public async Task existing_specialists_endpoint_is_enough_for_the_location_staff_list()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var other = await NewCtxAsync();
        // другий майстер у цьому ж закладі, потім деактивований; профіль без телефону/email не має віддаватись specialist-ролі
        var create = await Api(HttpMethod.Post, "/api/beauty/specialists", c.Owner, new
        {
            name = "Second", phone = "+380631112233", position = "Colorist",
            locationIds = new[] { c.Salon.LocationId }, serviceIds = new[] { c.Salon.ServiceId },
            workingHours = AllDays("10:00", "16:00"),
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var secondId = (await J(create)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"/api/beauty/specialists/{secondId}", c.Owner,
            new { name = "Second", phone = "+380631112233", position = "Colorist", isActive = false })).StatusCode);

        var asSpecialist = await Api(HttpMethod.Get, "/api/beauty/specialists", c.Specialist.AccessToken);
        var text = await asSpecialist.Content.ReadAsStringAsync();
        Assert.DoesNotContain("380631112233", text);
        var list = Raw(text).EnumerateArray().ToList();
        Assert.Equal(2, list.Count); // активні й неактивні
        var second = list.Single(x => x.GetProperty("id").GetGuid() == secondId);
        Assert.False(second.GetProperty("isActive").GetBoolean());
        Assert.False(second.TryGetProperty("phone", out _));
        Assert.False(second.TryGetProperty("email", out _));
        Assert.Equal(1, second.GetProperty("services").GetArrayLength());
        var link = second.GetProperty("locations").EnumerateArray().Single(l => l.GetProperty("locationId").GetGuid() == c.Salon.LocationId);
        Assert.Equal("10:00", link.GetProperty("workingHours").GetProperty("mon")[0].GetProperty("from").GetString());
        Assert.True(link.GetProperty("isActive").GetBoolean());

        // чужий tenant не бачить цих спеціалістів
        var foreignIds = (await J(await Api(HttpMethod.Get, "/api/beauty/specialists", other.Owner)))
            .EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(secondId, foreignIds);
        Assert.DoesNotContain(c.Salon.SpecialistId, foreignIds);
    }

    // ================= гонки =================

    [SkippableFact]
    public async Task booking_waits_for_the_location_lock_held_by_an_hours_change_and_then_sees_new_hours()
    {
        NeedDb();
        var c = await NewCtxAsync();

        await using var conn = new NpgsqlConnection(_fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM beauty_locations WHERE id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", c.Salon.LocationId);
            await cmd.ExecuteScalarAsync();
        }
        var pending = TryBookAsync(c, At(6, 16));
        await Task.Delay(1500);
        Assert.False(pending.IsCompleted, "booking must wait for the location row lock");
        await using (var upd = new NpgsqlCommand(
                         "UPDATE beauty_locations SET working_hours = @h::jsonb WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("h", JsonSerializer.Serialize(AllDays("10:00", "14:00")));
            upd.Parameters.AddWithValue("id", c.Salon.LocationId);
            await upd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        var r = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("outside_location_hours", await Code(r));
    }

    [SkippableFact]
    public async Task hours_change_waits_for_an_in_flight_booking_and_reports_it_as_conflict()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var clientId = await ClientIdAsync(c);

        await using var conn = new NpgsqlConnection(_fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM beauty_locations WHERE id = @id FOR SHARE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", c.Salon.LocationId);
            await cmd.ExecuteScalarAsync();
        }
        var pending = PutHours(c, c.Owner, AllDays("10:00", "14:00"));
        await Task.Delay(1500);
        Assert.False(pending.IsCompleted, "hours change must wait for the share lock");
        await using (var ins = new NpgsqlCommand(
                         @"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final)
                           VALUES (@t, @l, @sp, @sv, @cl, @at, 60, 'confirmed', 'admin', 1, 1)", conn, tx))
        {
            ins.Parameters.AddWithValue("t", c.Salon.Tenant.TenantId);
            ins.Parameters.AddWithValue("l", c.Salon.LocationId);
            ins.Parameters.AddWithValue("sp", c.Salon.SpecialistId);
            ins.Parameters.AddWithValue("sv", c.Salon.ServiceId);
            ins.Parameters.AddWithValue("cl", clientId);
            ins.Parameters.AddWithValue("at", At(6, 16));
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        var r = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("has_appointments_outside_hours", await Code(r));
        Assert.Null(await StoredHoursAsync(c));
    }

    [SkippableFact]
    public async Task concurrent_booking_and_narrowing_hours_never_leave_an_unconfirmed_appointment_outside_the_hours()
    {
        NeedDb();
        for (var round = 0; round < 4; round++)
        {
            var c = await NewCtxAsync();
            var ahead = 6 + round;
            var book = TryBookAsync(c, At(ahead, 16));
            var narrow = PutHours(c, c.Owner, AllDays("10:00", "14:00"));
            await Task.WhenAll(book, narrow);

            var bookCode = (await book).StatusCode;
            var narrowCode = (await narrow).StatusCode;
            // рівно один виграє: або запис створено (а зміна годин отримала 409), або години звужено (а запис - 409)
            Assert.True((bookCode == HttpStatusCode.Created && narrowCode == HttpStatusCode.Conflict)
                        || (bookCode == HttpStatusCode.Conflict && narrowCode == HttpStatusCode.OK),
                $"round {round}: book={bookCode} narrow={narrowCode}");
            if (bookCode == HttpStatusCode.Conflict) Assert.Equal("outside_location_hours", await Code(await book));
            else Assert.Equal("has_appointments_outside_hours", await Code(await narrow));

            await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
            var active = await ctx.Appointments.CountAsync(a => a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed);
            Assert.True(await StoredHoursAsync(c) is null || active == 0, $"round {round}: hours narrowed with {active} active appointments");
        }
    }
}

/// <summary>TASK-703: логіка Application без БД - SlotCalculator і BookingService із годинами закладу.</summary>
public class LocationWorkingHoursUnitTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero); // неділя
    private static readonly DateOnly Monday = new(2030, 1, 7);
    private const string Hours10To14 = """{"mon":[{"from":"10:00","to":"14:00"}],"tue":[{"from":"10:00","to":"14:00"}]}""";
    private readonly FakeBookingStore _store = new();
    private BookingService Sut() => new(_store, new SpyPayments(), FakeSettings.Service(), new FakeClock(Now));

    private static SpecialistSchedule Schedule(string? locationHours, string? specialistHours = null, string tz = "UTC") =>
        new(FakeBookingStore.Specialist, tz, specialistHours ?? FakeBookingStore.Mon9To18, null, null, null, null, locationHours);

    private static DateTimeOffset T(DateOnly d, int h, int m = 0) => new(d.ToDateTime(new TimeOnly(h, m)), TimeSpan.Zero);

    [Fact]
    public void compute_without_location_hours_is_unchanged_and_with_hours_is_the_intersection()
    {
        var all = SlotCalculator.Compute(Schedule(null), Monday, 60, [], Now);
        Assert.Equal(33, all.Count);
        Assert.Equal(33, SlotCalculator.Compute(Schedule("  "), Monday, 60, [], Now).Count); // порожній рядок = не задано

        var cut = SlotCalculator.Compute(Schedule(Hours10To14), Monday, 60, [], Now);
        Assert.Equal("10:00", cut.First().Label);
        Assert.Equal("13:00", cut.Last().Label);
        Assert.Equal(13, cut.Count);

        // години закладу ширші за графік майстра - діє графік майстра
        var wide = SlotCalculator.Compute(Schedule("""{"mon":[{"from":"06:00","to":"22:00"}]}"""), Monday, 60, [], Now);
        Assert.Equal(33, wide.Count);

        // два інтервали закладу з перервою: слот не може перетинати перерву
        var lunch = SlotCalculator.Compute(
            Schedule("""{"mon":[{"from":"09:00","to":"12:00"},{"from":"13:00","to":"18:00"}]}"""), Monday, 60, [], Now);
        Assert.DoesNotContain(lunch, s => s.Label is "11:15" or "11:30" or "11:45" or "12:00" or "12:15" or "12:30" or "12:45");
        Assert.Contains(lunch, s => s.Label == "11:00");
        Assert.Contains(lunch, s => s.Label == "13:00");

        // день без інтервалів у годинах закладу
        Assert.Empty(SlotCalculator.Compute(Schedule("""{"tue":[{"from":"09:00","to":"18:00"}]}"""), Monday, 60, [], Now));
        Assert.Empty(SlotCalculator.Compute(Schedule("{}"), Monday, 60, [], Now));
    }

    [Fact]
    public void check_orders_location_hours_after_availability_and_before_specialist_hours()
    {
        Assert.Equal(SlotCheck.Ok, SlotCalculator.Check(Schedule(Hours10To14), T(Monday, 10), 60, [], Now));
        Assert.Equal(SlotCheck.Ok, SlotCalculator.Check(Schedule(Hours10To14), T(Monday, 13), 60, [], Now));
        Assert.Equal(SlotCheck.OutsideLocationHours, SlotCalculator.Check(Schedule(Hours10To14), T(Monday, 13, 15), 60, [], Now));
        Assert.Equal(SlotCheck.OutsideLocationHours, SlotCalculator.Check(Schedule(Hours10To14), T(Monday, 9, 45), 60, [], Now));
        // у годинах закладу, але поза графіком майстра
        Assert.Equal(SlotCheck.OutsideWorkingHours,
            SlotCalculator.Check(Schedule("""{"mon":[{"from":"06:00","to":"22:00"}]}"""), T(Monday, 7), 60, [], Now));
        // поза обома -> спершу години закладу
        Assert.Equal(SlotCheck.OutsideLocationHours, SlotCalculator.Check(Schedule(Hours10To14), T(Monday, 7), 60, [], Now));
        // вихідний (пріоритет) і відсутність (пріоритет) раніше за години закладу
        var closed = Schedule(Hours10To14) with { ClosedWeekdays = ["mon"] };
        Assert.Equal(SlotCheck.LocationClosed, SlotCalculator.Check(closed, T(Monday, 16), 60, [], Now));
        var absent = Schedule(Hours10To14) with { Absences = [new AbsenceSpan(Monday, Monday)] };
        Assert.Equal(SlotCheck.Unavailable, SlotCalculator.Check(absent, T(Monday, 16), 60, [], Now));
        Assert.Equal(SlotCheck.InPast, SlotCalculator.Check(Schedule(Hours10To14), Now.AddHours(-1), 60, [], Now));
        // зайнятий слот у годинах закладу - Busy
        Assert.Equal(SlotCheck.Busy,
            SlotCalculator.Check(Schedule(Hours10To14), T(Monday, 11), 60, [new TimeRange(T(Monday, 11), T(Monday, 12))], Now));
        // запис через північ не вміщується
        Assert.Equal(SlotCheck.OutsideLocationHours,
            SlotCalculator.Check(Schedule("""{"mon":[{"from":"22:00","to":"23:59"}]}""", """{"mon":[{"from":"18:00","to":"23:59"}]}"""),
                T(Monday, 23, 30), 60, [], Now));
    }

    [Fact]
    public void location_hours_are_read_in_the_location_time_zone()
    {
        // 07:30Z понеділка = 09:30 у Києві (UTC+2): у 10-14 не вміщується; 08:30Z = 10:30 вміщується
        var kyiv = Schedule(Hours10To14, tz: "Europe/Kyiv");
        Assert.Equal(SlotCheck.OutsideLocationHours, SlotCalculator.Check(kyiv, T(Monday, 7, 30), 30, [], Now));
        Assert.Equal(SlotCheck.Ok, SlotCalculator.Check(kyiv, T(Monday, 8, 30), 30, [], Now));
        // у UTC той самий момент 08:30 - поза годинами 10-14
        Assert.Equal(SlotCheck.OutsideLocationHours, SlotCalculator.Check(Schedule(Hours10To14), T(Monday, 8, 30), 30, [], Now));
    }

    [Fact]
    public async Task create_and_patch_return_outside_location_hours()
    {
        _store.LocationHours = Hours10To14;
        var req = new CreateAppointmentRequest(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90,
            T(Monday, 15), new ClientInput(null, "Олена", "+380501112233", null), "none", "cash", "admin");
        var r = await Sut().CreateAsync(req, default);
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.Equal("outside_location_hours", r.Error.Code);
        Assert.Empty(_store.Appointments);

        _store.ServiceDuration = 60;
        var ok = await Sut().CreateAsync(req with { StartsAt = T(Monday, 11) }, default);
        Assert.True(ok.IsOk);
        var moved = await Sut().PatchAsync(ok.Value!.Id, new PatchAppointmentRequest(T(Monday, 15), null), default);
        Assert.Equal("outside_location_hours", moved.Error!.Code);
        var inside = await Sut().PatchAsync(ok.Value.Id, new PatchAppointmentRequest(T(Monday, 12), null), default);
        Assert.True(inside.IsOk);

        var slots = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Monday, default);
        Assert.All(slots.Value!, s => Assert.InRange(s.StartsAt.Hour, 10, 13));
    }

    [Theory]
    [InlineData("{\"mon\":[{\"from\":\"10:00\",\"to\":\"14:00\"}]}")]
    public void parse_roundtrips_normalized_hours_and_null_for_unset(string json)
    {
        Assert.Null(WorkingHoursValidator.Parse(null));
        Assert.Null(WorkingHoursValidator.Parse(" "));
        Assert.Equal("10:00", WorkingHoursValidator.Parse(json)!.Value.GetProperty("mon")[0].GetProperty("from").GetString());
    }
}
