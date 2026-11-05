using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Application.Features.BeautyReminders;

namespace BeautyCrm.Application.Features.BeautyBooking;

/// <param name="reminderContext">Порт вхідних планувальника нагадувань (налаштування tenant, вподобання клієнта). null = значення за замовчуванням.</param>
public sealed class BookingService(
    IBookingStore store, IPaymentService payments, CancellationSettingsService cancellation, TimeProvider clock,
    IReminderContextStore? reminderContext = null, CouponService? coupons = null)
{
    private readonly ReminderPlanning reminders = new(reminderContext, clock);

    private const string ClosedMessage = "The location is closed on this day.";
    private const string OutsideLocationHoursMessage = "Outside the working hours of the location.";

    // ---------- слоти ----------

    /// <summary>Вільні слоти на дату (локальну для закладу): тривалість береться з послуги, графік — по закладу.</summary>
    public async Task<Result<IReadOnlyList<FreeSlot>>> GetSlotsAsync(
        Guid locationId, Guid? specialistId, Guid serviceId, DateOnly date, CancellationToken ct)
    {
        var service = await store.GetServiceAsync(serviceId, ct);
        if (service is not { IsActive: true }) return Error.NotFound("service_not_found", "Service not found.");
        var location = await store.GetLocationAsync(locationId, ct);
        if (location is not { IsActive: true }) return Error.NotFound("location_not_found", "Location not found.");

        var schedules = await store.GetSchedulesAsync(locationId, specialistId, ct);
        if (schedules.Count == 0) return Result<IReadOnlyList<FreeSlot>>.Ok([]);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(location.Timezone);
        var dayStart = SlotCalculator.ToOffset(date, TimeOnly.MinValue, tz);
        var dayEnd = SlotCalculator.ToOffset(date.AddDays(1), TimeOnly.MinValue, tz);
        var busy = await store.GetBusyAsync(schedules.Select(s => s.SpecialistId).ToList(), dayStart, dayEnd, null, ct);
        var now = clock.GetUtcNow();

        var terms = await cancellation.GetTermsAsync(ct);
        var slots = schedules
            .SelectMany(s => SlotCalculator.Compute(
                s with { Timezone = location.Timezone }, date, service.DurationMinutes,
                busy.Where(b => b.SpecialistId == s.SpecialistId).Select(b => b.Range).ToList(), now, service.Id))
            .OrderBy(s => s.StartsAt).ThenBy(s => s.SpecialistId)
            .Select(s => s with { Cancellation = terms, Timezone = location.Timezone })
            .ToList();
        return Result<IReadOnlyList<FreeSlot>>.Ok(slots);
    }

    // ---------- створення ----------

    public async Task<Result<AppointmentDto>> CreateAsync(
        CreateAppointmentRequest req, CancellationToken ct, PublicCreateOptions? publicOptions = null, CouponUse? couponUse = null)
    {
        var source = req.Source ?? "online";
        if (!BookingValues.Sources.Contains(source)) return Error.Validation("invalid_source", "Unknown source.");
        if (req.Reminder is not null && !BookingValues.Reminders.Contains(req.Reminder))
            return Error.Validation("invalid_reminder", "Reminder must be none, 1h or 2h.");
        if (!BookingValues.PaymentMethods.Contains(req.PaymentMethod)) return Error.Validation("invalid_payment_method", "Payment method must be card or cash.");
        var c = req.Client;
        if (c.Id is null && (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Phone)))
            return Error.Validation("client_required", "Client id or name and phone are required.");

        // §22.6.1: після client_required, до перевірок слоту. reminders разом із reminder -> 422; 409 telegram_unavailable / phone_required / ...
        if (req.Reminders is not null && req.Reminder is not null)
            return Error.Validation("reminder_conflict", "Provide either reminder or reminders, not both.");
        var intent = await reminders.PrepareForCreateAsync(req, ct);
        if (!intent.IsOk) return intent.Error!;

        var service = await store.GetServiceAsync(req.ServiceId, ct);
        if (service is not { IsActive: true }) return Error.NotFound("service_not_found", "Service not found.");
        var location = await store.GetLocationAsync(req.LocationId, ct);
        if (location is not { IsActive: true }) return Error.NotFound("location_not_found", "Location not found.");

        var schedule = (await store.GetSchedulesAsync(req.LocationId, req.SpecialistId, ct)).FirstOrDefault();
        if (schedule is null) return Error.Validation("specialist_not_at_location", "Specialist does not work at this location.");

        var now = clock.GetUtcNow();
        var duration = service.DurationMinutes; // duration_minutes завжди з послуги
        var busy = await store.GetBusyAsync([req.SpecialistId], req.StartsAt, req.StartsAt.AddMinutes(duration), null, ct);
        var check = SlotCalculator.Check(schedule with { Timezone = location.Timezone }, req.StartsAt, duration,
            busy.Select(b => b.Range).ToList(), now, service.Id);
        switch (check)
        {
            case SlotCheck.InPast: return Error.Validation("slot_in_past", "Start time is in the past.");
            case SlotCheck.LocationClosed: return Error.Conflict("location_closed", ClosedMessage);
            case SlotCheck.Unavailable: return Error.Conflict("specialist_unavailable", "The specialist is unavailable on this day or does not offer this service.");
            case SlotCheck.OutsideLocationHours: return Error.Conflict("outside_location_hours", OutsideLocationHoursMessage);
            case SlotCheck.OutsideWorkingHours: return Error.Validation("outside_working_hours", "Outside specialist working hours.");
            case SlotCheck.Busy: return Error.Conflict("slot_unavailable", "The slot overlaps another appointment.");
        }

        var original = await store.GetPriceAsync(req.ServiceId, req.LocationId, ct);
        if (original is null) return Error.Validation("price_not_set", "Service has no price for this location.");

        // §26.6: staff передає код у тілі; публічний шлях і чат - окремим CouponUse (код тіла ігнорується поза staff)
        if (couponUse is null && publicOptions is null && !string.IsNullOrWhiteSpace(req.CouponCode))
            couponUse = new CouponUse(CouponScope.Staff, req.CouponCode);

        Guid? clientId = null;
        CouponCandidate? candidate = null;
        if (couponUse is not null)
        {
            clientId = await store.ResolveClientAsync(c with { PhoneSource = publicOptions is null ? "staff" : "web_form" }, ct);
            if (clientId is null) return Error.NotFound("client_not_found", "Client not found.");
            if (coupons is null) return Error.Conflict("coupon_unavailable", "Coupons are not available.");
            var resolved = await coupons.ResolveAsync(couponUse, clientId, now, ct);
            if (!resolved.IsOk) return resolved.Error!;
            candidate = resolved.Value;
        }
        var priced = PromotionPricing.QuoteWithCoupon(original.Value, req.LocationId, req.ServiceId, now, await store.GetPromotionRulesAsync(ct), candidate);
        var quote = priced.Quote;

        if (req.ExpectedPriceFinal is { } expected && expected != quote.Final)
            return Error.Conflict("price_changed", "The price has changed since it was shown.");

        if (clientId is null)
        {
            clientId = await store.ResolveClientAsync(c with { PhoneSource = publicOptions is null ? "staff" : "web_form" }, ct);
            if (clientId is null) return Error.NotFound("client_not_found", "Client not found.");
        }
        var redemption = priced.Outcome == CouponOutcomes.Applied
            ? new NewCouponRedemption(priced.CouponId!.Value, candidate!.CampaignId, quote.Original - quote.Final, now) : null;
        var couponDto = candidate is null ? null
            : new AppointmentCouponDto(candidate.CouponId, candidate.Kind, candidate.CodeHint, candidate.PromotionName, priced.Outcome);

        // §22.4: точки нагадувань (тиша за зоною закладу, злиття, дзвінок) пише store в тій самій транзакції, що й запис.
        var plan = reminders.Build(intent.Value!, req.StartsAt, location.Timezone, now);

        var added = await store.AddAppointmentAsync(new NewAppointment(
            req.LocationId, req.SpecialistId, req.ServiceId, clientId.Value, req.StartsAt, duration, source,
            quote.Original, quote.Final, quote.PromotionId, plan.Mirror, req.PaymentMethod, plan.LegacyReminderAt,
            quote.Final > 0 ? new NewPayment(req.PaymentMethod, quote.Final) : null, publicOptions, plan, redemption), ct);
        if (added.CouponUnavailable)
            return Error.Conflict(couponUse!.Scope == CouponScope.Public ? CouponService.Invalid : "coupon_unavailable", "The coupon is no longer available.");
        if (added.Duplicate) return Error.Conflict("idempotency_conflict", "A request with this idempotency key is already being processed.");
        if (added.LocationClosed) return Error.Conflict("location_closed", ClosedMessage);
        if (added.OutsideLocationHours) return Error.Conflict("outside_location_hours", OutsideLocationHoursMessage);
        if (added.SpecialistBlocked) return Error.Conflict("specialist_unavailable", "The specialist is unavailable on this day or does not offer this service.");
        if (added.Conflict || added.Value is null) return Error.Conflict("slot_unavailable", "The slot overlaps another appointment.");
        var appt = added.Value;

        if (req.PaymentMethod == "card" && quote.Final > 0)
        {
            var payment = await store.GetPaymentAsync(appt.Id, ct);
            var charge = await payments.ChargeAsync(appt.Id, quote.Final, ct);
            if (!charge.Success)
            {
                if (payment is not null) await store.UpdatePaymentAsync(payment.Id, "failed", null, null, ct);
                await store.MarkCancelledAsync(appt.Id, now, ct);
                return Error.PaymentFailed("payment_failed", charge.Error ?? "Card payment failed.");
            }
            if (payment is not null) await store.UpdatePaymentAsync(payment.Id, "paid", charge.ProviderPaymentId, now, ct);
        }

        // картка оплачена / готівка в закладі -> підтверджено
        var confirmed = await WithTermsAsync((await store.SetStatusAsync(appt.Id, "confirmed", ct))?.Value ?? appt, ct);
        return couponDto is not null && confirmed.Coupon is null ? confirmed with { Coupon = couponDto } : confirmed;
    }

    // ---------- перенос / статус ----------

    public async Task<Result<AppointmentDto>> PatchAsync(Guid id, PatchAppointmentRequest req, CancellationToken ct)
    {
        if (req.StartsAt is null && req.Status is null) return Error.Validation("nothing_to_change", "Provide startsAt and/or status.");
        if (req.Status is not null && !BookingValues.PatchableStatuses.Contains(req.Status))
            return Error.Validation("invalid_status", "Status must be confirmed, completed or no_show; use /cancel to cancel.");

        var appt = await store.GetAppointmentAsync(id, ct);
        if (appt is null) return Error.NotFound("appointment_not_found", "Appointment not found.");
        if (appt.Status is "cancelled" or "completed" or "no_show")
            return Error.Conflict("appointment_closed", $"Appointment with status '{appt.Status}' cannot be changed.");

        var current = appt;
        if (req.StartsAt is { } newStart && newStart != appt.StartsAt)
        {
            var location = await store.GetLocationAsync(appt.LocationId, ct);
            var schedule = (await store.GetSchedulesAsync(appt.LocationId, appt.SpecialistId, ct)).FirstOrDefault();
            if (location is null || schedule is null) return Error.Validation("specialist_not_at_location", "Specialist does not work at this location.");

            var now = clock.GetUtcNow();
            var busy = await store.GetBusyAsync([appt.SpecialistId], newStart, newStart.AddMinutes(appt.DurationMinutes), id, ct);
            switch (SlotCalculator.Check(schedule with { Timezone = location.Timezone }, newStart, appt.DurationMinutes,
                        busy.Select(b => b.Range).ToList(), now, appt.ServiceId))
            {
                case SlotCheck.InPast: return Error.Validation("slot_in_past", "Start time is in the past.");
                case SlotCheck.LocationClosed: return Error.Conflict("location_closed", ClosedMessage);
                case SlotCheck.Unavailable: return Error.Conflict("specialist_unavailable", "The specialist is unavailable on this day or does not offer this service.");
                case SlotCheck.OutsideLocationHours: return Error.Conflict("outside_location_hours", OutsideLocationHoursMessage);
                case SlotCheck.OutsideWorkingHours: return Error.Validation("outside_working_hours", "Outside specialist working hours.");
                case SlotCheck.Busy: return Error.Conflict("slot_unavailable", "The slot overlaps another appointment.");
            }

            // §22.4: той самий план запису (method + offsets), точки перераховуються від нового часу.
            var plan = await reminders.ReplanForMoveAsync(appt, newStart, location.Timezone, ct);
            var moved = await store.RescheduleAsync(id, newStart, plan, ct);
            if (moved.LocationClosed) return Error.Conflict("location_closed", ClosedMessage);
            if (moved.OutsideLocationHours) return Error.Conflict("outside_location_hours", OutsideLocationHoursMessage);
            if (moved.SpecialistBlocked) return Error.Conflict("specialist_unavailable", "The specialist is unavailable on this day or does not offer this service.");
            if (moved.Conflict || moved.Value is null) return Error.Conflict("slot_unavailable", "The slot overlaps another appointment.");
            current = moved.Value;
        }

        if (req.Status is not null)
        {
            var set = await store.SetStatusAsync(id, req.Status, ct);
            if (set is { AppointmentClosed: true })
                return Error.Conflict("appointment_closed", "Appointment was closed concurrently and cannot be changed.");
            current = set?.Value ?? current;
        }
        return await WithTermsAsync(current, ct);
    }

    public async Task<IReadOnlyList<AppointmentDto>> ListAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? locationId, Guid? specialistId, CancellationToken ct, bool includeCancelled = false)
    {
        var terms = await cancellation.GetTermsAsync(ct);
        return (await store.ListAppointmentsAsync(from, to, locationId, specialistId, includeCancelled, ct))
            .Select(a => a with { Cancellation = terms }).ToList();
    }

    public async Task<Result<AppointmentDto>> GetAsync(Guid id, CancellationToken ct) =>
        await store.GetAppointmentAsync(id, ct) is { } a
            ? await WithTermsAsync(a, ct)
            : Error.NotFound("appointment_not_found", "Appointment not found.");

    private async Task<AppointmentDto> WithTermsAsync(AppointmentDto a, CancellationToken ct) =>
        a with { Cancellation = await cancellation.GetTermsAsync(ct) };
}
