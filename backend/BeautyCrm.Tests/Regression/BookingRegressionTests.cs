using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Regression;

/// <summary>
/// TASK-681: наскрізні регресійні сценарії по HTTP на реальному PostgreSQL (RLS, роль без BYPASSRLS).
/// Без БД пропускаються; BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку.
/// </summary>
public sealed class BookingRegressionTests : IClassFixture<AuthApiFixture>
{
    private static readonly HttpMethod Patch = new("PATCH");

    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;

    public BookingRegressionTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private static DateTimeOffset Start(JsonElement slot) => slot.GetProperty("startsAt").GetDateTimeOffset();
    private static DateTimeOffset End(JsonElement slot) => slot.GetProperty("endsAt").GetDateTimeOffset();
    private static string Q(DateTimeOffset d) => Uri.EscapeDataString(d.ToString("O"));

    // ---------- наскрізний сценарій: запис -> календар -> перетини -> підтвердження -> завершення ----------

    [SkippableFact]
    public async Task regression_online_booking_appears_in_calendar_blocks_slot_then_confirm_and_complete()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(networkPrice: 1000m, durationMinutes: 60);
        var date = RegressionHarness.SlotDate();

        var slots = await _h.SlotsAsync(s, date);
        Assert.NotEmpty(slots);
        Assert.All(slots, x => Assert.Equal(TimeSpan.FromMinutes(60), End(x) - Start(x))); // тривалість = послуга
        var first = Start(slots[4]); // не перший слот дня: потрібен запас для перевірки накладання "зліва"

        // 1. створення -> 201, тривалість у календарі = тривалість послуги
        var created = await _h.BookAsync(s, first, reminder: "none", payment: "cash");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var appt = await Read<JsonElement>(created);
        var id = appt.GetProperty("id").GetGuid();
        Assert.Equal(60, appt.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(first.AddMinutes(60), appt.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal("online", appt.GetProperty("source").GetString());

        // 2. з'являється в календарі
        var list = await _fx.Send(HttpMethod.Get,
            $"/api/beauty/appointments?from={Q(first.AddHours(-1))}&to={Q(first.AddHours(12))}", bearer: s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var inCalendar = (await Read<JsonElement>(list)).EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id);
        Assert.Equal(first.AddMinutes(60), inCalendar.GetProperty("endsAt").GetDateTimeOffset());

        // 3. слот зайнятий: жоден слот не перетинає [first, first+60); впритул після запису - вільний
        var after = await _h.SlotsAsync(s, date);
        Assert.DoesNotContain(after, x => Start(x) < first.AddMinutes(60) && End(x) > first);
        Assert.Contains(after, x => Start(x) == first.AddMinutes(60));

        // 4. перетини -> 409 slot_unavailable (точний збіг і часткове накладання); впритул -> 201
        foreach (var offset in new[] { 0, 30, -45 })
        {
            var clash = await _h.BookAsync(s, first.AddMinutes(offset));
            Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
            Assert.Equal("slot_unavailable", await RegressionHarness.CodeAsync(clash));
        }
        Assert.Equal(HttpStatusCode.Created, (await _h.BookAsync(s, first.AddMinutes(60))).StatusCode);

        // 5. підтвердження -> завершення; завершений запис скасувати не можна
        var confirmed = await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { status = "confirmed" }, s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("confirmed", (await Read<JsonElement>(confirmed)).GetProperty("status").GetString());
        var completed = await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { status = "completed" }, s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("completed", (await Read<JsonElement>(completed)).GetProperty("status").GetString());
        var cancelAfter = await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken);
        Assert.True((int)cancelAfter.StatusCode is 409 or 422, $"cancel of completed must be rejected, got {(int)cancelAfter.StatusCode}");

        // 6. завершений запис потрапляє в виручку (price_final)
        var net = await Read<JsonElement>(await _fx.Send(HttpMethod.Get,
            $"/api/beauty/analytics/network?from={Q(DateTimeOffset.UtcNow.AddDays(-1))}&to={Q(DateTimeOffset.UtcNow.AddDays(10))}", bearer: s.OwnerToken));
        Assert.Equal(1000m, net.GetProperty("revenue").GetDecimal());
        Assert.Equal(1, net.GetProperty("completed").GetInt32());
    }

    [SkippableFact]
    public async Task regression_cancelled_appointment_frees_the_slot()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var date = RegressionHarness.SlotDate();
        var first = Start((await _h.SlotsAsync(s, date))[0]);
        var id = (await Read<JsonElement>(await _h.BookAsync(s, first))).GetProperty("id").GetGuid();

        Assert.DoesNotContain(await _h.SlotsAsync(s, date), x => Start(x) == first);
        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken)).StatusCode);
        Assert.Contains(await _h.SlotsAsync(s, date), x => Start(x) == first);
        Assert.Equal(HttpStatusCode.Created, (await _h.BookAsync(s, first)).StatusCode);
    }

    [SkippableFact]
    public async Task regression_slot_duration_follows_service_duration_in_calendar_and_overlap_checks()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(durationMinutes: 150);
        var slots = await _h.SlotsAsync(s, RegressionHarness.SlotDate());
        Assert.All(slots, x => Assert.Equal(TimeSpan.FromMinutes(150), End(x) - Start(x)));
        var first = Start(slots[0]);

        var appt = await Read<JsonElement>(await _h.BookAsync(s, first));
        Assert.Equal(first.AddMinutes(150), appt.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.Conflict, (await _h.BookAsync(s, first.AddMinutes(149))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _h.BookAsync(s, first.AddMinutes(150))).StatusCode);

        // ends_at в БД виставляє тригер: запис не виходить за тривалість послуги
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var row = await ctx.Appointments.AsNoTracking().SingleAsync(a => a.Id == appt.GetProperty("id").GetGuid());
        Assert.Equal(first.AddMinutes(150), row.EndsAt);
    }

    // ---------- ціна: override закладу + найбільша знижка ----------

    [SkippableFact]
    public async Task regression_price_location_override_and_largest_discount_not_stacked()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(networkPrice: 1000m);
        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Put, $"/api/beauty/services/{s.ServiceId}/prices",
            new { locationId = s.LocationId, price = 800m }, s.OwnerToken)).StatusCode);
        // три акції на всі заклади/послуги: 10% (=80), фіксована 150, фіксована 20 -> діє одна, найбільша
        // найбільша знижка свідомо посередині (і за іменем, і за порядком створення): ловить і "перша", і "остання"
        await CreatePromo(s, "a-small", "fixed", 20);
        var big = await CreatePromo(s, "m-fixed150", "fixed", 150);
        await CreatePromo(s, "z-ten", "percent", 10);

        var first = Start((await _h.SlotsAsync(s, RegressionHarness.SlotDate()))[0]);
        var created = await _h.BookAsync(s, first);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var a = await Read<JsonElement>(created);
        Assert.Equal(800m, a.GetProperty("priceOriginal").GetDecimal()); // override, не мережева 1000
        Assert.Equal(650m, a.GetProperty("priceFinal").GetDecimal());    // 800 - 150, без складання
        Assert.Equal(big, a.GetProperty("promotionId").GetGuid());

        // інший заклад без override: мережева ціна; акція 50%, прив'язана лише до першого закладу, не діє
        var (loc2, _) = await _h.AddLocationAsync(s.Tenant.TenantId, s.SpecialistId);
        await CreatePromo(s, "only-loc1", "percent", 50, [s.LocationId]);
        var slots2 = await _h.SlotsAsync(s, RegressionHarness.SlotDate(), locationId: loc2);
        var second = await _h.BookAsync(s, Start(slots2[0]), locationId: loc2);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var b = await Read<JsonElement>(second);
        Assert.Equal(1000m, b.GetProperty("priceOriginal").GetDecimal());
        Assert.Equal(850m, b.GetProperty("priceFinal").GetDecimal()); // лише "глобальна" fixed150
    }

    [SkippableFact]
    public async Task regression_inactive_or_expired_promotion_is_ignored()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(networkPrice: 1000m);
        Assert.Equal(HttpStatusCode.Created, (await _fx.Send(HttpMethod.Post, "/api/beauty/promotions",
            new { name = "off", discountType = "percent", discountValue = 50m, isActive = false, locationIds = Array.Empty<Guid>(), serviceIds = Array.Empty<Guid>() },
            s.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _fx.Send(HttpMethod.Post, "/api/beauty/promotions",
            new
            {
                name = "old", discountType = "percent", discountValue = 50m, isActive = true,
                startsAt = DateTimeOffset.UtcNow.AddDays(-30), endsAt = DateTimeOffset.UtcNow.AddDays(-1),
                locationIds = Array.Empty<Guid>(), serviceIds = Array.Empty<Guid>(),
            }, s.OwnerToken)).StatusCode);

        var first = Start((await _h.SlotsAsync(s, RegressionHarness.SlotDate()))[0]);
        var a = await Read<JsonElement>(await _h.BookAsync(s, first));
        Assert.Equal(1000m, a.GetProperty("priceFinal").GetDecimal());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("promotionId").ValueKind);
    }

    private async Task<Guid> CreatePromo(RegressionHarness.Salon s, string name, string type, decimal value, Guid[]? locations = null)
    {
        var r = await _fx.Send(HttpMethod.Post, "/api/beauty/promotions",
            new { name, discountType = type, discountValue = value, isActive = true, locationIds = locations ?? Array.Empty<Guid>(), serviceIds = Array.Empty<Guid>() },
            s.OwnerToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Read<JsonElement>(r)).GetProperty("id").GetGuid();
    }

    // ---------- нагадування: запис -> beauty_reminders (worker забирає status='scheduled') ----------

    [SkippableTheory]
    [InlineData("1h", 60)]
    [InlineData("2h", 120)]
    public async Task regression_reminder_option_creates_exactly_one_scheduled_row_before_visit(string option, int minutesBefore)
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var first = Start((await _h.SlotsAsync(s, RegressionHarness.SlotDate()))[0]);
        var created = await _h.BookAsync(s, first, reminder: option);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Read<JsonElement>(created)).GetProperty("id").GetGuid();

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var r = Assert.Single(await ctx.Reminders.AsNoTracking().Where(x => x.AppointmentId == id).ToListAsync());
        Assert.Equal(first.AddMinutes(-minutesBefore), r.ScheduledAt);
        // контракт з worker (pg-poll-store.ts): reminders.status = 'scheduled', appointments.reminder_option = '1h'|'2h'
        Assert.Equal(ReminderStatus.Scheduled, r.Status);
        Assert.Equal(option == "1h" ? ReminderOption.OneHour : ReminderOption.TwoHours,
            (await ctx.Appointments.AsNoTracking().SingleAsync(a => a.Id == id)).ReminderOption);
    }

    [SkippableFact]
    public async Task regression_reminder_none_creates_no_row_and_reschedule_moves_the_reminder()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var slots = await _h.SlotsAsync(s, RegressionHarness.SlotDate());
        var noneId = (await Read<JsonElement>(await _h.BookAsync(s, Start(slots[0]), reminder: "none"))).GetProperty("id").GetGuid();
        var twoH = Start(slots[4]);
        var id = (await Read<JsonElement>(await _h.BookAsync(s, twoH, reminder: "2h"))).GetProperty("id").GetGuid();

        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
            Assert.False(await ctx.Reminders.AnyAsync(r => r.AppointmentId == noneId));

        var moved = twoH.AddHours(2);
        var patch = await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { startsAt = moved }, s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        await using var c2 = _fx.Db.CreateContext(s.Tenant.TenantId);
        var active = await c2.Reminders.AsNoTracking().Where(r => r.AppointmentId == id && r.Status == ReminderStatus.Scheduled).ToListAsync();
        // після перенесення є рівно одне активне нагадування, і воно відповідає новому часу (старе не має спрацювати)
        Assert.Equal(moved.AddHours(-2), Assert.Single(active).ScheduledAt);
    }

    // ---------- скасування A2 + налаштовувана політика на реальній БД ----------

    [SkippableFact]
    public async Task regression_cancel_in_window_refunds_50_percent_and_custom_policy_applies()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(networkPrice: 800m);
        var slots = await _h.SlotsAsync(s, RegressionHarness.SlotDate());

        // запис, сплачений карткою; візит переносимо в потрібну відстань від "зараз" напряму в БД
        async Task<Guid> PaidBookingAsync(int slotIndex, TimeSpan fromNow)
        {
            var r = await _h.BookAsync(s, Start(slots[slotIndex]), payment: "card");
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            var id = (await Read<JsonElement>(r)).GetProperty("id").GetGuid();
            await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
            var newStart = DateTimeOffset.UtcNow + fromNow;
            await ctx.Appointments.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.StartsAt, newStart));
            return id;
        }

        async Task<JsonElement> CancelAsync(Guid id)
        {
            var r = await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await Read<JsonElement>(r);
        }

        // за замовчуванням: <=12 год -> 50%
        var inside = await PaidBookingAsync(0, TimeSpan.FromHours(5));
        var j1 = await CancelAsync(inside);
        Assert.Equal(50, j1.GetProperty("refundPercent").GetInt32());
        Assert.Equal(400m, j1.GetProperty("refundAmount").GetDecimal());

        // >12 год -> 100%
        var j2 = await CancelAsync(await PaidBookingAsync(4, TimeSpan.FromHours(30)));
        Assert.Equal(100, j2.GetProperty("refundPercent").GetInt32());
        Assert.Equal(800m, j2.GetProperty("refundAmount").GetDecimal());

        // межа: на 30 с менше за 12 год -> ще у вікні; на 30 с більше -> поза вікном
        Assert.Equal(50, (await CancelAsync(await PaidBookingAsync(8, TimeSpan.FromHours(12) - TimeSpan.FromSeconds(30)))).GetProperty("refundPercent").GetInt32());
        Assert.Equal(100, (await CancelAsync(await PaidBookingAsync(12, TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1)))).GetProperty("refundPercent").GetInt32());

        // політика власника: вікно 48 год, 20% у вікні, комісія 10% -> 800 * 20% * 90% = 144
        var put = await _fx.Send(HttpMethod.Put, "/api/beauty/settings/cancellation",
            new { windowHours = 48, refundPercentInWindow = 20, refundPercentOutside = 100, deductFee = true, feePercent = 10 }, s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var j4 = await CancelAsync(await PaidBookingAsync(16, TimeSpan.FromHours(30))); // поза старим вікном, всередині нового
        Assert.Equal(20, j4.GetProperty("refundPercent").GetInt32());
        Assert.Equal(10, j4.GetProperty("feePercent").GetInt32());
        Assert.Equal(144m, j4.GetProperty("refundAmount").GetDecimal());

        // платіж у БД: повернений запис має статус refunded
        await using var chk = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal(PaymentStatus.Refunded, (await chk.Payments.AsNoTracking().SingleAsync(p => p.AppointmentId == inside)).Status);

        // повторне скасування -> 409 already_cancelled
        var again = await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{inside}/cancel", bearer: s.OwnerToken);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_cancelled", await RegressionHarness.CodeAsync(again));
    }

    [SkippableFact]
    public async Task regression_cancel_cash_booking_refunds_nothing()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(networkPrice: 800m);
        var first = Start((await _h.SlotsAsync(s, RegressionHarness.SlotDate()))[0]);
        var id = (await Read<JsonElement>(await _h.BookAsync(s, first, payment: "cash"))).GetProperty("id").GetGuid();
        var r = await Read<JsonElement>(await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken));
        Assert.Equal(0m, r.GetProperty("refundAmount").GetDecimal());
    }

    // ---------- ролі на наскрізному рівні ----------

    [SkippableFact]
    public async Task regression_roles_specialist_cannot_change_catalog_or_book_for_others_admin_can_manage_catalog()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var admin = await _fx.InviteAndLoginAsync(s.Tenant, s.OwnerToken, Roles.Admin);
        var spec = await _fx.InviteAndLoginAsync(s.Tenant, s.OwnerToken, Roles.Specialist, s.SpecialistId);

        var promo = new { name = "p", discountType = "percent", discountValue = 10m, isActive = true, locationIds = Array.Empty<Guid>(), serviceIds = Array.Empty<Guid>() };
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.Send(HttpMethod.Post, "/api/beauty/promotions", promo, spec.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.Send(HttpMethod.Put, $"/api/beauty/services/{s.ServiceId}/prices",
            new { locationId = (Guid?)null, price = 1m }, spec.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _fx.Send(HttpMethod.Post, "/api/beauty/promotions", promo, admin.AccessToken)).StatusCode);

        var otherSpecialist = (await _h.AddLocationAsync(s.Tenant.TenantId)).SpecialistId;
        var foreign = await _fx.Send(HttpMethod.Post, "/api/beauty/appointments",
            new
            {
                locationId = s.LocationId, specialistId = otherSpecialist, serviceId = s.ServiceId, startsAt = DateTimeOffset.UtcNow.AddDays(3),
                client = new { name = "A", phone = "+380500000001" }, reminder = "none", paymentMethod = "cash",
            }, spec.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }
}
