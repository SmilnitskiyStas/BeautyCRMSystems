using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyPublicBooking;

/// <summary>
/// Спільне ядро онлайн-запису (TASK-726, §21.6): правила публічного запису, які мають бути ОДНАКОВИМИ для форми й чату —
/// сітка слотів 15 хв, горизонт, перевірки <see cref="BookingService.CreateAsync"/> (години/вихідні закладу, відсутності, графік),
/// маскування причин у <c>slot_unavailable</c>, цикл повтору ідемпотентного запису. Винесено з <see cref="PublicBookingService"/>
/// без зміни поведінки публічного API. Ліміти (на телефон / на клієнта) і ключі ідемпотентності визначає викликач.
/// </summary>
public sealed class OnlineBookingCore(IPublicBookingStore pub, BookingService booking, PublicBookingOptions options, TimeProvider clock)
{
    public PublicBookingOptions Options => options;

    /// <summary>Вільні слоти на локальну дату закладу в межах горизонту запису.</summary>
    public async Task<Result<IReadOnlyList<FreeSlot>>> GetSlotsAsync(
        Guid locationId, Guid? specialistId, Guid serviceId, DateOnly date, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (date < today.AddDays(-1) || date > today.AddDays(options.MaxDaysAhead))
            return Error.Validation("invalid_date", $"date must be within {options.MaxDaysAhead} days ahead.");
        return await booking.GetSlotsAsync(locationId, specialistId, serviceId, date, ct);
    }

    /// <summary>Лише слоти, які віддає /slots: сітка 15 хв (UTC-ticks) і горизонт запису.</summary>
    public Error? ValidateStart(DateTimeOffset startsAt, DateTimeOffset now)
    {
        if (startsAt.UtcTicks % TimeSpan.FromMinutes(SlotCalculator.StepMinutes).Ticks != 0)
            return Error.Validation("invalid_start", "startsAt must be one of the offered slots.");
        if (startsAt > now.AddDays(options.MaxDaysAhead))
            return Error.Validation("invalid_start", $"startsAt must be within {options.MaxDaysAhead} days ahead.");
        return null;
    }

    /// <summary>Ліміт активних / нових записів за годину (спільні пороги; що саме рахується — на викликачі).</summary>
    public bool LimitReached(int active, int recentLastHour) =>
        active >= options.MaxActiveBookingsPerPhone || recentLastHour >= options.MaxCreatesPerPhonePerHour;

    public Task<IdempotencyHit?> FindReplayAsync(string keyHash, CancellationToken ct) => pub.FindByIdempotencyKeyAsync(keyHash, ct);

    /// <summary>
    /// Створює запис. Паралельні повтори одного ключа можуть взаємно відкотитися (unique-ключ, exclusion слоту, deadlock):
    /// чекаємо коміт переможця й повертаємо його запис (<see cref="CoreCreateOutcome.Replay"/>); якщо переможця немає — пробуємо знову.
    /// Причини недоступності майстра/закладу маскуються в <c>slot_unavailable</c> (публічний контракт, §12/§17/§18).
    /// </summary>
    public async Task<CoreCreateOutcome> CreateAsync(
        CreateAppointmentRequest request, PublicCreateOptions opts, CancellationToken ct, BeautyCrm.Application.Features.BeautyCoupons.CouponUse? coupon = null)
    {
        Result<AppointmentDto> created = default!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            created = await booking.CreateAsync(request, ct, opts, coupon);
            if (created.Error is not { Kind: ErrorKind.Conflict } conflict) break;
            var polls = conflict.Code == "idempotency_conflict" ? 5 : 1;
            for (var i = 0; i < polls; i++)
            {
                if (await pub.FindByIdempotencyKeyAsync(opts.IdempotencyKeyHash, ct) is { } raced)
                    return new CoreCreateOutcome(null, raced, null);
                if (i + 1 < polls) await Task.Delay(100, ct);
            }
            if (conflict.Code != "idempotency_conflict") break;
        }
        // Публічний API не розкриває ПРИЧИНУ недоступності майстра (лікарняний/відпустка/послуга не призначена): внутрішній
        // specialist_unavailable віддається як звичайний зайнятий слот (той самий код, статус і тіло). Різниця — лише у staff API.
        // Те саме для закритого дня закладу (вихідний/закриття, §17) і часу поза годинами закладу (§18.1): причина не розкривається.
        if (created.Error is { Code: "specialist_unavailable" or "location_closed" or "outside_location_hours" })
            return new CoreCreateOutcome(null, null, Error.Conflict("slot_unavailable", "The slot overlaps another appointment."));
        if (!created.IsOk) return new CoreCreateOutcome(null, null, created.Error!);
        return new CoreCreateOutcome(created.Value!, null, null);
    }
}

/// <summary>Рівно одне з: запис створено / повтор (запис переможця за ключем) / помилка.</summary>
public sealed record CoreCreateOutcome(AppointmentDto? Appointment, IdempotencyHit? Replay, Error? Error);
