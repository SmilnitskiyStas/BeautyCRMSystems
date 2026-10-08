using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Application.Features.BeautyPublicBooking;

/// <summary>
/// Публічний потік онлайн-запису анонімного клієнта. Tenant уже визначено <see cref="PublicTenantResolver"/>.
/// Логування свідомо немає: тут проходять ім'я й телефон клієнта.
/// </summary>
public sealed partial class PublicBookingService(
    IPublicBookingStore pub, IBookingStore store, BookingService booking, CancellationService cancellation,
    CancellationSettingsService settings, PublicTenantResolver tenant, PublicTokenService tokens,
    ICaptchaVerifier captcha, PublicBookingOptions options, TimeProvider clock)
{
    /// <summary>Єдина відповідь на неіснуючий/чужий/зіпсований токен (розрізнити причини ззовні неможливо).</summary>
    public static readonly Error TokenNotFound = Error.NotFound("not_found", "Not found.");

    [GeneratedRegex("^[A-Za-z0-9._:-]{16,128}$")]
    private static partial Regex KeyPattern();

    // ---------- каталог ----------

    public async Task<IReadOnlyList<PublicLocationDto>> ListLocationsAsync(CancellationToken ct) =>
        await pub.ListActiveLocationsAsync(ct);

    public async Task<Result<IReadOnlyList<PublicSpecialistDto>>> ListSpecialistsAsync(Guid locationId, Guid? serviceId, CancellationToken ct) =>
        await store.GetLocationAsync(locationId, ct) is { IsActive: true }
            ? Result<IReadOnlyList<PublicSpecialistDto>>.Ok(await pub.ListSpecialistsAsync(locationId, serviceId, ct))
            : Error.NotFound("location_not_found", "Location not found.");

    /// <summary>Ціна = override закладу або мережева; акція (найбільша знижка) рахується на поточний момент.</summary>
    public async Task<Result<IReadOnlyList<PublicServiceDto>>> ListServicesAsync(Guid locationId, Guid? specialistId, CancellationToken ct)
    {
        if (await store.GetLocationAsync(locationId, ct) is not { IsActive: true })
            return Error.NotFound("location_not_found", "Location not found.");
        var now = clock.GetUtcNow();
        var rules = await store.GetPromotionRulesAsync(ct);
        var list = (await pub.ListServicesAsync(locationId, specialistId, ct)).Select(s =>
        {
            var q = PromotionPricing.Quote(s.Price, locationId, s.Id, now, rules);
            return new PublicServiceDto(s.Id, s.Name, s.Description, s.Category, s.DurationMinutes, q.Original, q.Final, q.PromotionId, q.PromotionName);
        }).ToList();
        return Result<IReadOnlyList<PublicServiceDto>>.Ok(list);
    }

    public async Task<Result<IReadOnlyList<FreeSlot>>> GetSlotsAsync(
        Guid locationId, Guid? specialistId, Guid serviceId, DateOnly date, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (date < today.AddDays(-1) || date > today.AddDays(options.MaxDaysAhead))
            return Error.Validation("invalid_date", $"date must be within {options.MaxDaysAhead} days ahead.");
        return await booking.GetSlotsAsync(locationId, specialistId, serviceId, date, ct);
    }

    // ---------- створення ----------

    public async Task<Result<PublicCreateResult>> CreateAsync(PublicCreateAppointmentRequest req, PublicRequestContext ctx, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId) return TokenNotFound;

        // Honeypot: приховане поле форми, яке заповнюють лише боти. Відповідь не розкриває причину.
        if (!string.IsNullOrEmpty(req.Website)) return Error.Validation("invalid_request", "Invalid request.");

        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey))
            return Error.Validation("idempotency_key_required", "Idempotency-Key header is required.");
        if (!KeyPattern().IsMatch(ctx.IdempotencyKey))
            return Error.Validation("invalid_idempotency_key", "Idempotency-Key must be 16-128 chars of A-Z a-z 0-9 . _ : -.");

        if (req.LocationId is null || req.SpecialistId is null || req.ServiceId is null || req.StartsAt is null)
            return Error.Validation("invalid_request", "locationId, specialistId, serviceId and startsAt are required.");
        var name = req.Client?.Name?.Trim();
        if (name is null || name.Length is < 2 or > 100 || name.Any(char.IsControl))
            return Error.Validation("invalid_name", "Client name must be 2-100 characters.");
        var phone = PhoneNormalizer.Normalize(req.Client?.Phone);
        if (phone is null)
            return Error.Validation("invalid_phone", "Phone must be in international format, e.g. +380501234567.");
        var startsAt = req.StartsAt.Value;
        var now = clock.GetUtcNow();
        // Лише слоти, які віддає /slots: сітка 15 хв (UTC-ticks) і горизонт запису.
        if (startsAt.UtcTicks % TimeSpan.FromMinutes(SlotCalculator.StepMinutes).Ticks != 0)
            return Error.Validation("invalid_start", "startsAt must be one of the offered slots.");
        if (startsAt > now.AddDays(options.MaxDaysAhead))
            return Error.Validation("invalid_start", $"startsAt must be within {options.MaxDaysAhead} days ahead.");
        var reminder = req.Reminder ?? "none";
        var payment = req.PaymentMethod;
        if (!BookingValues.Reminders.Contains(reminder)) return Error.Validation("invalid_reminder", "Reminder must be none, 1h or 2h.");
        if (payment is null || !BookingValues.PaymentMethods.Contains(payment))
            return Error.Validation("invalid_payment_method", "Payment method must be card or cash.");

        var keyHash = PublicTokenService.Hash("idem:" + ctx.IdempotencyKey);
        var requestHash = PublicTokenService.Hash(string.Join('|', req.LocationId, req.SpecialistId, req.ServiceId,
            startsAt.UtcTicks, phone, name, reminder, payment));

        // Ідемпотентність: той самий ключ і те саме тіло -> той самий результат без повторного списання.
        if (await pub.FindByIdempotencyKeyAsync(keyHash, ct) is { } hit)
            return await ReplayAsync(tenantId, hit, requestHash, ct);

        if (options.CaptchaRequired
            && (string.IsNullOrWhiteSpace(ctx.CaptchaToken) || !await captcha.VerifyAsync(ctx.CaptchaToken, ctx.RemoteIp, ct)))
            return Error.Validation("captcha_failed", "CAPTCHA verification failed.");

        if (await pub.CountActiveOnlineByPhoneAsync(phone, now, ct) >= options.MaxActiveBookingsPerPhone)
            return Error.Validation("booking_limit_reached", "Too many active bookings. Try again later.");
        if (await pub.CountRecentOnlineByPhoneAsync(phone, now.AddHours(-1), ct) >= options.MaxCreatesPerPhonePerHour)
            return Error.Validation("booking_limit_reached", "Too many active bookings. Try again later.");

        var id = Guid.NewGuid();
        var token = tokens.Create(tenantId, id);
        var createRequest = new CreateAppointmentRequest(req.LocationId.Value, req.SpecialistId.Value, req.ServiceId.Value, startsAt,
            new ClientInput(null, name, phone, null, req.Client?.MarketingConsent == true), reminder, payment, "online");
        var publicOptions = new PublicCreateOptions(id, PublicTokenService.Hash(token), keyHash, requestHash);

        // Паралельні повтори одного ключа можуть взаємно відкотитися (unique-ключ, exclusion слоту, deadlock):
        // чекаємо коміт переможця й повторюємо його відповідь; якщо переможця немає — пробуємо створити знову.
        Result<AppointmentDto> created = default!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            created = await booking.CreateAsync(createRequest, ct, publicOptions);
            if (created.Error is not { Kind: ErrorKind.Conflict } conflict) break;
            var polls = conflict.Code == "idempotency_conflict" ? 5 : 1;
            for (var i = 0; i < polls; i++)
            {
                if (await pub.FindByIdempotencyKeyAsync(keyHash, ct) is { } raced)
                    return await ReplayAsync(tenantId, raced, requestHash, ct);
                if (i + 1 < polls) await Task.Delay(100, ct);
            }
            if (conflict.Code != "idempotency_conflict") break;
        }
        // Публічний API не розкриває ПРИЧИНУ недоступності майстра (лікарняний/відпустка/послуга не призначена): внутрішній
        // specialist_unavailable віддається як звичайний зайнятий слот (той самий код, статус і тіло). Різниця — лише у staff API.
        if (created.Error is { Code: "specialist_unavailable" })
            return Error.Conflict("slot_unavailable", "The slot overlaps another appointment.");
        if (!created.IsOk) return created.Error!;

        return new PublicCreateResult(new PublicBookingCreatedDto(token, await ToPublicAsync(created.Value!, ct)), false);
    }

    private async Task<Result<PublicCreateResult>> ReplayAsync(Guid tenantId, IdempotencyHit hit, string requestHash, CancellationToken ct)
    {
        if (hit.RequestHash != requestHash)
            return Error.Validation("idempotency_key_reused", "This Idempotency-Key was used with a different request.");
        var appt = await store.GetAppointmentAsync(hit.AppointmentId, ct);
        if (appt is null) return Error.Conflict("idempotency_conflict", "A request with this idempotency key is already being processed.");
        var payment = await store.GetPaymentAsync(appt.Id, ct);
        if (appt.Status == "cancelled" && payment is { Status: "failed" })
            return Error.PaymentFailed("payment_failed", "Card payment failed.");
        return new PublicCreateResult(
            new PublicBookingCreatedDto(tokens.Create(tenantId, appt.Id), await ToPublicAsync(appt, ct)), true);
    }

    // ---------- перегляд / скасування за токеном ----------

    public async Task<Result<PublicAppointmentDto>> GetAsync(string? token, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { } appt) return TokenNotFound;
        return await ToPublicAsync(appt, ct);
    }

    public async Task<Result<PublicCancelResultDto>> CancelAsync(string? token, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { } appt) return TokenNotFound;
        var result = await cancellation.CancelAsync(appt.Id, ct);
        if (!result.IsOk) return result.Error!.Kind == ErrorKind.NotFound ? TokenNotFound : result.Error;
        var r = result.Value!;
        return new PublicCancelResultDto(await ToPublicAsync(r.Appointment, ct), r.RefundAmount, r.RefundPercent, r.FeePercent);
    }

    private async Task<AppointmentDto?> FindAsync(string? token, CancellationToken ct)
    {
        if (!PublicTokenService.IsWellFormed(token)) return null;
        return await pub.FindAppointmentIdByTokenHashAsync(PublicTokenService.Hash(token!), ct) is { } id
            ? await store.GetAppointmentAsync(id, ct)
            : null;
    }

    private async Task<PublicAppointmentDto> ToPublicAsync(AppointmentDto a, CancellationToken ct) => new(
        a.LocationId, a.LocationName, a.SpecialistId, a.SpecialistName, a.ServiceId, a.ServiceName,
        a.StartsAt, a.EndsAt, a.DurationMinutes, a.Status, a.PriceOriginal, a.PriceFinal, a.PromotionId,
        a.ReminderOption, a.PaymentMethod, (await store.GetPaymentAsync(a.Id, ct))?.Status,
        a.Cancellation ?? await settings.GetTermsAsync(ct));
}

public static class PublicBookingServiceExtensions
{
    public static IServiceCollection AddBeautyPublicBookingApplication(this IServiceCollection services, PublicBookingOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<PublicTokenService>();
        services.AddScoped<PublicTenantResolver>();
        services.AddScoped<PublicBookingService>();
        services.AddSingleton<ICaptchaVerifier, RejectAllCaptchaVerifier>(); // провайдера підключає окрема реєстрація
        return services;
    }
}
