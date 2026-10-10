using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-701 (§17): вихідні дні закладу (щотижневі + дати закриття). Повний HTTP-конвеєр на реальному PostgreSQL (роль без BYPASSRLS):
/// слоти staff/public, запис/перенос на закритий день, підтвердження confirm, ролі, IDOR, публічний каталог, часова зона, гонки.
/// </summary>
public sealed class LocationClosuresApiTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _public = null!;

    public LocationClosuresApiTests(AuthApiFixture fx)
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

    private static DateOnly D(int ahead) => DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(ahead));
    private static string Day(int ahead) => D(ahead).ToString("yyyy-MM-dd");
    private static string Key(DateOnly d) => SlotCalculator.DayKey(d.DayOfWeek);
    private static DateTimeOffset At(int ahead, int hour = 10) => new(DateTime.UtcNow.Date.AddDays(ahead).AddHours(hour), TimeSpan.Zero);

    private Task<HttpResponseMessage> Api(HttpMethod m, string url, string token, object? body = null) => _fx.Send(m, url, body, token);
    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    private sealed record Ctx(RegressionHarness.Salon Salon, TokenResponse Admin, TokenResponse Specialist)
    {
        public string Owner => Salon.OwnerToken;
        public string Base => $"/api/beauty/locations/{Salon.LocationId}";
    }

    private async Task<Ctx> NewCtxAsync(string timezone = "UTC")
    {
        var salon = await _h.CreateSalonAsync(timezone: timezone);
        var admin = await _fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Admin);
        var specialist = await _fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Specialist, salon.SpecialistId);
        return new Ctx(salon, admin, specialist);
    }

    private async Task<Guid> BookAsync(Ctx c, DateTimeOffset at, string phone = "+380501112233")
    {
        var r = await _fx.Send(HttpMethod.Post, "/api/beauty/appointments", RegressionHarness.Booking(c.Salon, at, phone: phone), c.Owner);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> TryBookAsync(Ctx c, DateTimeOffset at, string phone = "+380501112233") =>
        _fx.Send(HttpMethod.Post, "/api/beauty/appointments", RegressionHarness.Booking(c.Salon, at, phone: phone), c.Owner);

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

    private async Task<int> SlotCountAsync(Ctx c, int ahead, string? token = null)
    {
        var r = await Api(HttpMethod.Get,
            $"/api/beauty/slots?locationId={c.Salon.LocationId}&serviceId={c.Salon.ServiceId}&date={Day(ahead)}", token ?? c.Owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).GetArrayLength();
    }

    private async Task<int> PublicSlotCountAsync(Ctx c, int ahead)
    {
        var r = await _public.GetAsync(
            $"/api/public/{c.Salon.Tenant.Slug}/slots?locationId={c.Salon.LocationId}&serviceId={c.Salon.ServiceId}&date={Day(ahead)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).GetArrayLength();
    }

    private Task<HttpResponseMessage> PutWeekdays(Ctx c, string token, object days, bool? confirm = null) =>
        Api(HttpMethod.Put, $"{c.Base}/closed-weekdays", token, confirm is null ? new { closedWeekdays = days } : new { closedWeekdays = days, confirm });

    private Task<HttpResponseMessage> PostClosure(Ctx c, string token, int fromAhead, int toAhead, string? reason = null, bool? confirm = null) =>
        Api(HttpMethod.Post, $"{c.Base}/closures", token, new { dateFrom = Day(fromAhead), dateTo = Day(toAhead), reason, confirm });

    private async Task<string[]> StoredWeekdaysAsync(Ctx c)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        return (await ctx.Locations.AsNoTracking().SingleAsync(l => l.Id == c.Salon.LocationId)).ClosedWeekdays;
    }

    private async Task<AppointmentStatus> StatusAsync(Ctx c, Guid id)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        return (await ctx.Appointments.AsNoTracking().SingleAsync(a => a.Id == id)).Status;
    }

    // ================= щотижневі вихідні =================

    [SkippableFact]
    public async Task new_location_works_seven_days_and_weekly_closed_day_removes_slots_for_staff_and_public()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var list = await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Owner));
        Assert.Equal(0, list.EnumerateArray().Single().GetProperty("closedWeekdays").GetArrayLength());
        Assert.True(await SlotCountAsync(c, 10) > 0);

        var key = Key(D(10));
        string? hoursBefore;
        await using (var before = _fx.Db.CreateContext(c.Salon.Tenant.TenantId))
            hoursBefore = (await before.SpecialistLocations.AsNoTracking().SingleAsync()).WorkingHours;
        var put = await PutWeekdays(c, c.Owner, new[] { key });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(new[] { key }, (await J(put)).GetProperty("closedWeekdays").EnumerateArray().Select(x => x.GetString()!).ToArray());

        // той самий день тижня через тиждень теж закритий, сусідні дні працюють
        foreach (var ahead in new[] { 10, 17 })
        {
            Assert.Equal(0, await SlotCountAsync(c, ahead));
            Assert.Equal(0, await PublicSlotCountAsync(c, ahead));
        }
        Assert.True(await SlotCountAsync(c, 11) > 0);
        Assert.True(await PublicSlotCountAsync(c, 11) > 0);

        // створення: staff -> 409 location_closed, public -> узагальнений slot_unavailable
        var staff = await TryBookAsync(c, At(10));
        Assert.Equal(HttpStatusCode.Conflict, staff.StatusCode);
        Assert.Equal("location_closed", await Code(staff));
        var pub = await PublicBookAsync(c, At(10));
        Assert.Equal(HttpStatusCode.Conflict, pub.StatusCode);
        Assert.Equal("slot_unavailable", await Code(pub));
        Assert.Equal(HttpStatusCode.Created, (await TryBookAsync(c, At(11))).StatusCode);

        // графік майстра не змінено
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        Assert.Equal(hoursBefore, (await ctx.SpecialistLocations.AsNoTracking().SingleAsync()).WorkingHours);
    }

    [SkippableFact]
    public async Task weekdays_are_normalized_deduplicated_and_validated()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var r = await PutWeekdays(c, c.Owner, new[] { "SUN", " mon ", "sun" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new[] { "mon", "sun" }, (await J(r)).GetProperty("closedWeekdays").EnumerateArray().Select(x => x.GetString()!).ToArray());

        foreach (var bad in new object?[] { new[] { "funday" }, new[] { "" }, null })
        {
            var rr = await PutWeekdays(c, c.Owner, bad!);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rr.StatusCode);
            Assert.Equal("invalid_closed_weekdays", await Code(rr));
        }
        Assert.Equal(new[] { "mon", "sun" }, await StoredWeekdaysAsync(c));

        // порожній список знімає вихідні
        Assert.Equal(HttpStatusCode.OK, (await PutWeekdays(c, c.Owner, Array.Empty<string>())).StatusCode);
        Assert.Empty(await StoredWeekdaysAsync(c));
    }

    [SkippableFact]
    public async Task weekday_change_with_future_appointments_needs_confirmation_and_does_not_cancel_them()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var onDay = await BookAsync(c, At(10, 10));
        var otherDay = await BookAsync(c, At(11, 10), "+380501112244");
        var key = Key(D(10));

        // без confirm: 409 + conflicts[], зміни немає
        var r = await PutWeekdays(c, c.Owner, new[] { key });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await J(r);
        Assert.Equal("has_appointments_on_closed_days", body.GetProperty("code").GetString());
        var conflict = Assert.Single(body.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(onDay, conflict.GetProperty("appointmentId").GetGuid());
        Assert.Equal(At(10, 10), conflict.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal("Haircut", conflict.GetProperty("serviceName").GetString());
        Assert.Equal("Master", conflict.GetProperty("specialistName").GetString());
        // без клієнтських даних
        Assert.Equal(new[] { "appointmentId", "serviceName", "specialistName", "startsAt" },
            conflict.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("Anna", body.GetRawText());
        Assert.DoesNotContain("380501112233", body.GetRawText());
        Assert.Empty(await StoredWeekdaysAsync(c));
        Assert.True(await SlotCountAsync(c, 10) > 0);

        // з confirm: застосовано, записи НЕ скасовано
        var ok = await PutWeekdays(c, c.Owner, new[] { key }, confirm: true);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(new[] { key }, await StoredWeekdaysAsync(c));
        Assert.Equal(0, await SlotCountAsync(c, 10));
        Assert.NotEqual(AppointmentStatus.Cancelled, await StatusAsync(c, onDay));
        Assert.NotEqual(AppointmentStatus.Cancelled, await StatusAsync(c, otherDay));

        // повторне збереження того самого набору не потребує підтвердження (нових вихідних немає)
        Assert.Equal(HttpStatusCode.OK, (await PutWeekdays(c, c.Owner, new[] { key })).StatusCode);
        // зняття вихідного не потребує підтвердження
        Assert.Equal(HttpStatusCode.OK, (await PutWeekdays(c, c.Owner, Array.Empty<string>())).StatusCode);
    }

    [SkippableFact]
    public async Task existing_appointment_on_a_closed_day_can_still_be_completed_and_cancelled_but_not_moved_to_a_closed_day()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var id = await BookAsync(c, At(10, 10));
        var other = await BookAsync(c, At(12, 10), "+380501112244");
        Assert.Equal(HttpStatusCode.OK, (await PutWeekdays(c, c.Owner, new[] { Key(D(10)), Key(D(11)) }, confirm: true)).StatusCode);

        // статус/скасування працюють
        var done = await Api(HttpMethod.Patch, $"/api/beauty/appointments/{id}", c.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var cancel = await Api(HttpMethod.Post, $"/api/beauty/appointments/{other}/cancel", c.Owner, new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        // перенос на закритий день -> 409 location_closed; на відкритий -> 200
        var third = await BookAsync(c, At(13, 10), "+380501112255");
        var toClosed = await Api(HttpMethod.Patch, $"/api/beauty/appointments/{third}", c.Owner, new { startsAt = At(11, 10) });
        Assert.Equal(HttpStatusCode.Conflict, toClosed.StatusCode);
        Assert.Equal("location_closed", await Code(toClosed));
        var toOpen = await Api(HttpMethod.Patch, $"/api/beauty/appointments/{third}", c.Owner, new { startsAt = At(14, 10) });
        Assert.Equal(HttpStatusCode.OK, toOpen.StatusCode);
    }

    // ================= дати закриття =================

    [SkippableFact]
    public async Task closure_date_removes_slots_for_staff_and_public_and_blocks_booking_until_deleted()
    {
        NeedDb();
        var c = await NewCtxAsync();
        Assert.True(await SlotCountAsync(c, 8) > 0);

        var created = await PostClosure(c, c.Owner, 8, 9, "Ремонт");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await J(created);
        Assert.Equal(Day(8), dto.GetProperty("dateFrom").GetString());
        Assert.Equal(Day(9), dto.GetProperty("dateTo").GetString());
        Assert.Equal("Ремонт", dto.GetProperty("reason").GetString());
        var closureId = dto.GetProperty("id").GetGuid();

        foreach (var ahead in new[] { 8, 9 })
        {
            Assert.Equal(0, await SlotCountAsync(c, ahead));
            Assert.Equal(0, await PublicSlotCountAsync(c, ahead));
            var staff = await TryBookAsync(c, At(ahead));
            Assert.Equal(HttpStatusCode.Conflict, staff.StatusCode);
            Assert.Equal("location_closed", await Code(staff));
            Assert.Equal("slot_unavailable", await Code(await PublicBookAsync(c, At(ahead))));
        }
        Assert.True(await SlotCountAsync(c, 7) > 0);
        Assert.True(await SlotCountAsync(c, 10) > 0);

        var del = await Api(HttpMethod.Delete, $"{c.Base}/closures/{closureId}", c.Owner);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.True(await SlotCountAsync(c, 8) > 0);
        Assert.Equal(HttpStatusCode.Created, (await TryBookAsync(c, At(8))).StatusCode);
        var again = await Api(HttpMethod.Delete, $"{c.Base}/closures/{closureId}", c.Owner);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("closure_not_found", await Code(again));
    }

    [SkippableFact]
    public async Task closure_with_future_appointments_needs_confirmation_and_overlap_is_rejected()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var id = await BookAsync(c, At(20, 10));
        var cancelled = await BookAsync(c, At(20, 14), "+380501112244");
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/appointments/{cancelled}/cancel", c.Owner, new { })).StatusCode);

        var r = await PostClosure(c, c.Owner, 19, 21, "Свято");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await J(r);
        Assert.Equal("has_appointments_on_closed_days", body.GetProperty("code").GetString());
        // скасований запис не блокує
        Assert.Equal(id, Assert.Single(body.GetProperty("conflicts").EnumerateArray()).GetProperty("appointmentId").GetGuid());
        var none = await J(await Api(HttpMethod.Get, $"{c.Base}/closures?from={Day(0)}&to={Day(40)}", c.Owner));
        Assert.Equal(0, none.GetArrayLength());

        var ok = await PostClosure(c, c.Owner, 19, 21, "Свято", confirm: true);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.NotEqual(AppointmentStatus.Cancelled, await StatusAsync(c, id));

        // перетин (навіть з confirm) -> 409 closure_overlap; сусідні діапазони - ок
        foreach (var (from, to) in new[] { (21, 23), (17, 19), (20, 20), (18, 25) })
        {
            var rr = await PostClosure(c, c.Owner, from, to, confirm: true);
            Assert.Equal(HttpStatusCode.Conflict, rr.StatusCode);
            Assert.Equal("closure_overlap", await Code(rr));
        }
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(c, c.Owner, 22, 22)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(c, c.Owner, 18, 18)).StatusCode);
    }

    [SkippableFact]
    public async Task closure_on_an_already_weekly_closed_day_does_not_ask_for_confirmation()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await BookAsync(c, At(20, 10));
        Assert.Equal(HttpStatusCode.OK, (await PutWeekdays(c, c.Owner, new[] { Key(D(20)) }, confirm: true)).StatusCode);
        // день уже вихідний: нових закритих днів немає - підтвердження не потрібне
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(c, c.Owner, 20, 20, "Збігається")).StatusCode);
    }

    [SkippableFact]
    public async Task closure_validation_errors()
    {
        NeedDb();
        var c = await NewCtxAsync();
        async Task Expect(object body, string code)
        {
            var r = await Api(HttpMethod.Post, $"{c.Base}/closures", c.Owner, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal(code, await Code(r));
        }
        await Expect(new { dateFrom = Day(5), dateTo = Day(4) }, "invalid_dates");
        await Expect(new { dateFrom = Day(5) }, "invalid_dates");
        await Expect(new { dateFrom = Day(5), dateTo = Day(5 + 366) }, "invalid_dates"); // 367 днів
        await Expect(new { dateFrom = Day(-400), dateTo = Day(-399) }, "invalid_dates"); // давніше за рік
        await Expect(new { dateFrom = Day(5), dateTo = Day(5), reason = new string('r', 201) }, "invalid_reason");

        // рівно 366 днів і минуле в межах року - дозволено; причина обрізається, порожня -> без причини
        var max = await Api(HttpMethod.Post, $"{c.Base}/closures", c.Owner, new { dateFrom = Day(5), dateTo = Day(5 + 365), reason = "  Рік  " });
        Assert.Equal(HttpStatusCode.Created, max.StatusCode);
        Assert.Equal("Рік", (await J(max)).GetProperty("reason").GetString());
        var past = await Api(HttpMethod.Post, $"{c.Base}/closures", c.Owner, new { dateFrom = Day(-300), dateTo = Day(-290), reason = "   " });
        Assert.Equal(HttpStatusCode.Created, past.StatusCode);
        Assert.False((await J(past)).TryGetProperty("reason", out _));

        // діапазон списку
        var range = await Api(HttpMethod.Get, $"{c.Base}/closures?from={Day(0)}&to={Day(400)}", c.Owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, range.StatusCode);
        Assert.Equal("invalid_range", await Code(range));
        var inverted = await Api(HttpMethod.Get, $"{c.Base}/closures?from={Day(10)}&to={Day(5)}", c.Owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inverted.StatusCode);
    }

    // ================= ролі, IDOR =================

    [SkippableFact]
    public async Task only_managers_change_closures_staff_read_and_specialist_does_not_see_reason()
    {
        NeedDb();
        var c = await NewCtxAsync();
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(c, c.Admin.AccessToken, 30, 31, "Secret reason")).StatusCode);
        var id = (await J(await Api(HttpMethod.Get, $"{c.Base}/closures?from={Day(0)}&to={Day(60)}", c.Owner))).EnumerateArray().Single()
            .GetProperty("id").GetGuid();

        var spec = c.Specialist.AccessToken;
        foreach (var r in new[]
                 {
                     await PutWeekdays(c, spec, new[] { "sun" }),
                     await PostClosure(c, spec, 40, 41),
                     await Api(HttpMethod.Delete, $"{c.Base}/closures/{id}", spec),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        Assert.Empty(await StoredWeekdaysAsync(c));

        var forSpecialist = await J(await Api(HttpMethod.Get, $"{c.Base}/closures?from={Day(0)}&to={Day(60)}", spec));
        var item = forSpecialist.EnumerateArray().Single();
        Assert.Equal(Day(30), item.GetProperty("dateFrom").GetString());
        Assert.False(item.TryGetProperty("reason", out _));
        Assert.DoesNotContain("Secret", forSpecialist.GetRawText());

        var forAdmin = await J(await Api(HttpMethod.Get, $"{c.Base}/closures?from={Day(0)}&to={Day(60)}", c.Admin.AccessToken));
        Assert.Equal("Secret reason", forAdmin.EnumerateArray().Single().GetProperty("reason").GetString());

        // specialist бачить closedWeekdays у списку закладів
        Assert.Equal(HttpStatusCode.OK, (await PutWeekdays(c, c.Admin.AccessToken, new[] { "sun" })).StatusCode);
        var locs = await J(await Api(HttpMethod.Get, "/api/beauty/locations", spec));
        Assert.Equal("sun", locs.EnumerateArray().Single().GetProperty("closedWeekdays")[0].GetString());

        // без токена
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Send(HttpMethod.Get, $"{c.Base}/closures")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Send(HttpMethod.Put, $"{c.Base}/closed-weekdays", new { closedWeekdays = new[] { "sun" } })).StatusCode);
    }

    [SkippableFact]
    public async Task foreign_tenant_location_and_closure_are_not_found()
    {
        NeedDb();
        var mine = await NewCtxAsync();
        var theirs = await NewCtxAsync();
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(theirs, theirs.Owner, 30, 31, "x")).StatusCode);
        var theirClosure = (await J(await Api(HttpMethod.Get, $"{theirs.Base}/closures?from={Day(0)}&to={Day(60)}", theirs.Owner)))
            .EnumerateArray().Single().GetProperty("id").GetGuid();

        foreach (var r in new[]
                 {
                     await Api(HttpMethod.Put, $"{theirs.Base}/closed-weekdays", mine.Owner, new { closedWeekdays = new[] { "sun" } }),
                     await Api(HttpMethod.Get, $"{theirs.Base}/closures", mine.Owner),
                     await Api(HttpMethod.Post, $"{theirs.Base}/closures", mine.Owner, new { dateFrom = Day(5), dateTo = Day(6) }),
                     await Api(HttpMethod.Delete, $"{theirs.Base}/closures/{theirClosure}", mine.Owner),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        // свій заклад + чужий closureId -> 404, чужий запис не видалено
        var del = await Api(HttpMethod.Delete, $"{mine.Base}/closures/{theirClosure}", mine.Owner);
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
        Assert.Equal("closure_not_found", await Code(del));
        Assert.Equal(1, (await J(await Api(HttpMethod.Get, $"{theirs.Base}/closures?from={Day(0)}&to={Day(60)}", theirs.Owner))).GetArrayLength());
        Assert.Empty(await StoredWeekdaysAsync(theirs));
    }

    // ================= публічний каталог =================

    [SkippableFact]
    public async Task public_catalog_exposes_closed_weekdays_and_future_closures_without_reason()
    {
        NeedDb();
        var c = await NewCtxAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutWeekdays(c, c.Owner, new[] { "sun", "mon" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(c, c.Owner, 5, 6, "PRIVATE-REASON-XYZ")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(c, c.Owner, -20, -10, "минуле")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostClosure(c, c.Owner, 380, 382, "далеко")).StatusCode);

        var r = await _public.GetAsync($"/api/public/{c.Salon.Tenant.Slug}/locations");
        var raw = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PRIVATE-REASON", raw);
        Assert.DoesNotContain("reason", raw, StringComparison.OrdinalIgnoreCase);
        var loc = JsonDocument.Parse(raw).RootElement.EnumerateArray().Single();
        Assert.Equal(new[] { "mon", "sun" }, loc.GetProperty("closedWeekdays").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var closure = Assert.Single(loc.GetProperty("closures").EnumerateArray());
        Assert.Equal(Day(5), closure.GetProperty("dateFrom").GetString());
        Assert.Equal(Day(6), closure.GetProperty("dateTo").GetString());
        Assert.Equal(new[] { "dateFrom", "dateTo" }, closure.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ================= часова зона / межа доби / літній час =================

    private async Task<Guid> SeedAppointmentAtAsync(Ctx c, Guid clientId, DateTimeOffset utcStart)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        var a = new Appointment
        {
            LocationId = c.Salon.LocationId, SpecialistId = c.Salon.SpecialistId, ServiceId = c.Salon.ServiceId, ClientId = clientId,
            StartsAt = utcStart, DurationMinutes = 30, Status = AppointmentStatus.Confirmed, Source = AppointmentSource.Admin,
            PriceOriginal = 1, PriceFinal = 1,
        };
        ctx.Add(a);
        await ctx.SaveChangesAsync();
        return a.Id;
    }

    private async Task<Guid> ClientIdAsync(Ctx c)
    {
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        var client = new Client { FullName = "Edge", Phone = "+380990000001" };
        ctx.Add(client);
        await ctx.SaveChangesAsync();
        return client.Id;
    }

    [SkippableFact]
    public async Task conflicts_use_the_local_day_of_the_location_around_midnight()
    {
        NeedDb();
        var c = await NewCtxAsync("Pacific/Auckland"); // UTC+12/+13
        var clientId = await ClientIdAsync(c);
        // 20:00Z дня X = 08:00/09:00 наступного локального дня (X+1); 10:00Z дня X = 22:00/23:00 локального дня X
        var x = 25;
        var lateUtc = await SeedAppointmentAtAsync(c, clientId, At(x, 20));
        var earlyUtc = await SeedAppointmentAtAsync(c, clientId, At(x, 10));

        // закриття локального дня X+1: лише "пізній за UTC" запис
        var r = await PostClosure(c, c.Owner, x + 1, x + 1);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(lateUtc, Assert.Single((await J(r)).GetProperty("conflicts").EnumerateArray()).GetProperty("appointmentId").GetGuid());

        // закриття локального дня X: лише "ранній за UTC" запис (UTC-дата обох однакова)
        var r2 = await PostClosure(c, c.Owner, x, x);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Equal(earlyUtc, Assert.Single((await J(r2)).GetProperty("conflicts").EnumerateArray()).GetProperty("appointmentId").GetGuid());
    }

    [SkippableFact]
    public async Task daylight_saving_change_day_keeps_appointments_on_their_local_date()
    {
        NeedDb();
        var c = await NewCtxAsync("Europe/Kyiv");
        var clientId = await ClientIdAsync(c);
        // найближчий перехід на зимовий час (остання неділя жовтня, 01:00Z): локальна доба триває 25 годин
        var first = DateTime.UtcNow.Date.AddDays(14);
        var lastSunday = LastSundayOfOctober(first.Year);
        if (lastSunday < first) lastSunday = LastSundayOfOctober(first.Year + 1);
        var change = DateOnly.FromDateTime(lastSunday);
        var ahead = change.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow.Date).DayNumber;

        // 21:30Z дня зміни = 23:30 EET (UTC+2) -> все ще локальна неділя; фіксований +3 дав би понеділок
        var sundayLate = await SeedAppointmentAtAsync(c, clientId, new DateTimeOffset(lastSunday.AddHours(21).AddMinutes(30), TimeSpan.Zero));
        // 22:30Z = 00:30 понеділка
        var mondayEarly = await SeedAppointmentAtAsync(c, clientId, new DateTimeOffset(lastSunday.AddHours(22).AddMinutes(30), TimeSpan.Zero));

        var r = await PostClosure(c, c.Owner, ahead, ahead);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(sundayLate, Assert.Single((await J(r)).GetProperty("conflicts").EnumerateArray()).GetProperty("appointmentId").GetGuid());

        var r2 = await PostClosure(c, c.Owner, ahead + 1, ahead + 1);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Equal(mondayEarly, Assert.Single((await J(r2)).GetProperty("conflicts").EnumerateArray()).GetProperty("appointmentId").GetGuid());
    }

    private static DateTime LastSundayOfOctober(int year)
    {
        var d = new DateTime(year, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    // ================= гонки =================

    [SkippableFact]
    public async Task booking_waits_for_the_location_lock_held_by_a_closure_change_and_then_sees_it()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var key = Key(D(6));

        // зміна вихідних тримає FOR UPDATE: запис чекає, після коміту (вихідний встановлено) -> 409 location_closed
        await using var conn = new NpgsqlConnection(_fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM beauty_locations WHERE id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", c.Salon.LocationId);
            await cmd.ExecuteScalarAsync();
        }
        var pending = TryBookAsync(c, At(6));
        await Task.Delay(1500);
        Assert.False(pending.IsCompleted, "booking must wait for the location row lock");
        await using (var upd = new NpgsqlCommand("UPDATE beauty_locations SET closed_weekdays = ARRAY[@k]::text[] WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("k", key);
            upd.Parameters.AddWithValue("id", c.Salon.LocationId);
            await upd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        var r = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("location_closed", await Code(r));
    }

    [SkippableFact]
    public async Task closing_a_weekday_waits_for_an_in_flight_booking_and_reports_it_as_conflict()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var key = Key(D(6));

        // запис тримає FOR SHARE (відкрита транзакція) -> PUT чекає, після коміту нового запису бачить його -> 409
        await using var conn = new NpgsqlConnection(_fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM beauty_locations WHERE id = @id FOR SHARE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", c.Salon.LocationId);
            await cmd.ExecuteScalarAsync();
        }
        var pending = PutWeekdays(c, c.Owner, new[] { key });
        await Task.Delay(1500);
        Assert.False(pending.IsCompleted, "closing must wait for the share lock");
        var clientId = await ClientIdAsync(c);
        await using (var ins = new NpgsqlCommand(
                         @"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final)
                           VALUES (@t, @l, @sp, @sv, @cl, @at, 30, 'confirmed', 'admin', 1, 1)", conn, tx))
        {
            ins.Parameters.AddWithValue("t", c.Salon.Tenant.TenantId);
            ins.Parameters.AddWithValue("l", c.Salon.LocationId);
            ins.Parameters.AddWithValue("sp", c.Salon.SpecialistId);
            ins.Parameters.AddWithValue("sv", c.Salon.ServiceId);
            ins.Parameters.AddWithValue("cl", clientId);
            ins.Parameters.AddWithValue("at", At(6, 11));
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        var r = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("has_appointments_on_closed_days", await Code(r));
        Assert.Empty(await StoredWeekdaysAsync(c));
    }

    [SkippableFact]
    public async Task concurrent_booking_and_closing_never_leave_an_active_appointment_on_a_closed_weekday()
    {
        NeedDb();
        for (var round = 0; round < 4; round++)
        {
            var c = await NewCtxAsync();
            var ahead = 6 + round;
            var key = Key(D(ahead));
            var book = TryBookAsync(c, At(ahead));
            var close = PutWeekdays(c, c.Owner, new[] { key });
            await Task.WhenAll(book, close);

            var weekdays = await StoredWeekdaysAsync(c);
            await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
            var active = await ctx.Appointments.CountAsync(a => a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed);
            Assert.True(!weekdays.Contains(key) || active == 0, $"round {round}: closed {key} with {active} active appointments");
            var bookCode = (await book).StatusCode;
            var closeCode = (await close).StatusCode;
            // рівно один виграє: або запис створено (а закриття отримало 409), або день закрито (а запис отримав 409)
            Assert.True((bookCode == HttpStatusCode.Created && closeCode == HttpStatusCode.Conflict)
                        || (bookCode == HttpStatusCode.Conflict && closeCode == HttpStatusCode.OK),
                $"round {round}: book={bookCode} close={closeCode}");
        }
    }
}

/// <summary>TASK-701: логіка Application без БД - SlotCalculator і BookingService із вихідними закладу.</summary>
public class LocationClosureUnitTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero); // неділя
    private static readonly DateOnly Monday = new(2030, 1, 7);
    private static readonly DateOnly Tuesday = new(2030, 1, 8);
    private readonly FakeBookingStore _store = new();
    private BookingService Sut() => new(_store, new SpyPayments(), FakeSettings.Service(), new FakeClock(Now));

    private static SpecialistSchedule Schedule(IReadOnlyList<string>? weekdays = null, IReadOnlyList<AbsenceSpan>? closures = null,
        IReadOnlyList<AbsenceSpan>? absences = null, string tz = "UTC") =>
        new(FakeBookingStore.Specialist, tz, FakeBookingStore.Mon9To18, absences, null, weekdays, closures);

    private static DateTimeOffset T(DateOnly d, int h) => new(d.ToDateTime(new TimeOnly(h, 0)), TimeSpan.Zero);

    [Fact]
    public void compute_returns_no_slots_on_closed_weekday_and_on_closure_dates_only()
    {
        Assert.Empty(SlotCalculator.Compute(Schedule(["mon"]), Monday, 60, [], Now));
        Assert.NotEmpty(SlotCalculator.Compute(Schedule(["mon"]), Tuesday, 60, [], Now));
        var closure = new[] { new AbsenceSpan(Monday, Monday) };
        Assert.Empty(SlotCalculator.Compute(Schedule(closures: closure), Monday, 60, [], Now));
        Assert.NotEmpty(SlotCalculator.Compute(Schedule(closures: closure), Tuesday, 60, [], Now));
        Assert.NotEmpty(SlotCalculator.Compute(Schedule([]), Monday, 60, [], Now));
    }

    [Fact]
    public void check_reports_location_closed_before_specialist_unavailable_and_working_hours()
    {
        var both = Schedule(["mon"], absences: [new AbsenceSpan(Monday, Monday)]);
        Assert.Equal(SlotCheck.LocationClosed, SlotCalculator.Check(both, T(Monday, 10), 60, [], Now));
        Assert.Equal(SlotCheck.LocationClosed, SlotCalculator.Check(both, T(Monday, 3), 60, [], Now)); // і поза робочими годинами
        Assert.Equal(SlotCheck.Unavailable, SlotCalculator.Check(Schedule(absences: [new AbsenceSpan(Monday, Monday)]), T(Monday, 10), 60, [], Now));
        Assert.Equal(SlotCheck.InPast, SlotCalculator.Check(both, Now.AddHours(-1), 60, [], Now));
    }

    [Fact]
    public void closed_day_is_determined_in_the_location_time_zone()
    {
        // 22:30Z понеділка = 00:30 вівторка в Києві (UTC+2 узимку)
        var kyiv = Schedule(["tue"], tz: "Europe/Kyiv");
        var start = new DateTimeOffset(2030, 1, 7, 22, 30, 0, TimeSpan.Zero);
        Assert.Equal(SlotCheck.LocationClosed, SlotCalculator.Check(kyiv, start, 30, [], Now));
        // у UTC той самий момент - ще понеділок: не закритий (поза годинами)
        Assert.Equal(SlotCheck.OutsideWorkingHours, SlotCalculator.Check(Schedule(["tue"]), start, 30, [], Now));
    }

    [Fact]
    public async Task create_and_patch_return_location_closed_on_closed_days_and_other_days_work()
    {
        _store.ClosedWeekdays = ["mon"];
        var req = new CreateAppointmentRequest(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90,
            T(Monday, 10), new ClientInput(null, "Олена", "+380501112233", null), "none", "cash", "admin");
        var r = await Sut().CreateAsync(req, default);
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.Equal("location_closed", r.Error.Code);
        Assert.Empty(_store.Appointments);

        var ok = await Sut().CreateAsync(req with { StartsAt = T(Tuesday, 10) }, default);
        Assert.True(ok.IsOk);
        var moved = await Sut().PatchAsync(ok.Value!.Id, new PatchAppointmentRequest(T(Monday, 12), null), default);
        Assert.Equal("location_closed", moved.Error!.Code);

        _store.ClosedWeekdays = null;
        _store.Closures = [new AbsenceSpan(Monday, Monday)];
        var onClosure = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Monday, default);
        Assert.Empty(onClosure.Value!);
        var nextDay = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Tuesday, default);
        Assert.NotEmpty(nextDay.Value!);
    }
}
