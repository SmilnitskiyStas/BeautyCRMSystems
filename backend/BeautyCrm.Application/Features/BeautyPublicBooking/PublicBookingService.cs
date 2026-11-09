using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyPublicBooking;

/// <summary>
/// Публічний потік онлайн-запису анонімного клієнта. Tenant уже визначено <see cref="PublicTenantResolver"/>.
/// Логування мінімальне: лише Warning про спрацювання лімітів (id tenant/закладу, без імені, телефону й токенів).
/// </summary>
public sealed partial class PublicBookingService(
    IPublicBookingStore pub, IBookingStore store, BookingService booking, CancellationService cancellation,
    CancellationSettingsService settings, PublicTenantResolver tenant, PublicTokenService tokens,
    IClientIntegrityVerifier integrity, PublicBookingOptions options, TimeProvider clock, ILogger<PublicBookingService> log)
{
    /// <summary>Єдина відповідь на неіснуючий/чужий/зіпсований токен (розрізнити причини ззовні неможливо).</summary>
    public static readonly Error TokenNotFound = Error.NotFound("not_found", "Not found.");

    [GeneratedRegex("^[A-Za-z0-9._:-]{16,128}$")]
    private static partial Regex KeyPattern();

    // ---------- каталог ----------

    public async Task<IReadOnlyList<PublicLocationDto>> ListLocationsAsync(CancellationToken ct) =>
        await pub.ListActiveLocationsAsync(clock.GetUtcNow(), ct);

    public async Task<Result<IReadOnlyList<PublicSpecialistDto>>> ListSpecialistsAsync(Guid locationId, Guid? serviceId, CancellationToken ct)
    {
        if (await store.GetLocationAsync(locationId, ct) is not { IsActive: true })
            return Error.NotFound("location_not_found", "Location not found.");
        var year = clock.GetUtcNow().Year;
        var rows = await pub.ListSpecialistsAsync(locationId, serviceId, ct);
        return Result<IReadOnlyList<PublicSpecialistDto>>.Ok(rows.Select(r => new PublicSpecialistDto(
            r.Id, r.Name, r.Title, r.PhotoUrl,
            r.PracticeStartYear is { } y ? Math.Max(0, year - y) : null,
            r.Bio, r.Rating is { } rt ? Math.Round(rt, 1, MidpointRounding.AwayFromZero) : null, r.ReviewsCount)).ToList());
    }

    public const int MaxReviewsLimit = 50;

    /// <summary>Опубліковані відгуки майстра: limit 1..50 (за замовч. 20), offset >= 0. Невідомий/чужий майстер -> 404.</summary>
    public async Task<Result<PublicReviewsPage>> ListReviewsAsync(Guid specialistId, int? limit, int? offset, CancellationToken ct)
    {
        var l = limit ?? 20;
        var o = offset ?? 0;
        if (l < 1 || l > MaxReviewsLimit) return Error.Validation("invalid_limit", $"limit must be between 1 and {MaxReviewsLimit}.");
        if (o < 0) return Error.Validation("invalid_offset", "offset must be >= 0.");
        return await pub.ListReviewsAsync(specialistId, l, o, ct) is { } page
            ? page
            : Error.NotFound("specialist_not_found", "Specialist not found.");
    }

    /// <summary>Календар доступності (див. <see cref="BookingService.GetAvailabilityAsync"/>); горизонт — options.MaxDaysAhead.</summary>
    public async Task<Result<PublicAvailabilityDto>> GetAvailabilityAsync(
        Guid locationId, Guid? specialistId, Guid serviceId, DateOnly? from, int? days, CancellationToken ct)
    {
        var r = await booking.GetAvailabilityAsync(locationId, specialistId, serviceId, from, days ?? 31, options.MaxDaysAhead, ct);
        return r.IsOk ? new PublicAvailabilityDto(r.Value!) : r.Error!;
    }

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
        var email = NormalizeEmail(req.Client?.Email, out var emailValid);
        if (!emailValid)
            return Error.Validation("invalid_email", "Email must be a valid address of at most 254 characters.");
        var startsAt = req.StartsAt.Value;
        var now = clock.GetUtcNow();
        // Лише слоти, які віддає /slots: сітка 15 хв (UTC-ticks) і горизонт запису.
        if (startsAt.UtcTicks % TimeSpan.FromMinutes(SlotCalculator.StepMinutes).Ticks != 0)
            return Error.Validation("invalid_start", "startsAt must be one of the offered slots.");
        if (startsAt > now.AddDays(options.MaxDaysAhead))
            return Error.Validation("invalid_start", $"startsAt must be within {options.MaxDaysAhead} days ahead.");
        var reminder = req.Reminder ?? "none";
        var payment = req.PaymentMethod;
        if (!BookingValues.Reminders.Contains(reminder)) return Error.Validation("invalid_reminder", "Reminder must be none, 1h, 2h or 24h.");
        if (payment is null || !BookingValues.PaymentMethods.Contains(payment))
            return Error.Validation("invalid_payment_method", "Payment method must be card or cash.");

        var keyHash = PublicTokenService.Hash("idem:" + ctx.IdempotencyKey);
        var requestHash = PublicTokenService.Hash(string.Join('|', req.LocationId, req.SpecialistId, req.ServiceId,
            startsAt.UtcTicks, phone, name, reminder, payment, email));

        // Ідемпотентність: той самий ключ і те саме тіло -> той самий результат без повторного списання.
        if (await pub.FindByIdempotencyKeyAsync(keyHash, ct) is { } hit)
            return await ReplayAsync(tenantId, hit, requestHash, ct);

        if (await CheckClientProofAsync(ctx, ct) is { } proofError) return proofError;

        if (options.MaxPublicCreatesPerLocationPerHour > 0
            && await pub.CountRecentOnlineByLocationAsync(req.LocationId.Value, now.AddHours(-1), ct) >= options.MaxPublicCreatesPerLocationPerHour)
            return LimitReached("location_hourly", tenantId, req.LocationId.Value, "Too many bookings right now. Try again later.");

        if (await pub.CountActiveOnlineByPhoneAsync(phone, now, ct) >= options.MaxActiveBookingsPerPhone)
            return LimitReached("phone_active", tenantId, req.LocationId.Value, "Too many active bookings. Try again later.");
        if (await pub.CountRecentOnlineByPhoneAsync(phone, now.AddHours(-1), ct) >= options.MaxCreatesPerPhonePerHour)
            return LimitReached("phone_hourly", tenantId, req.LocationId.Value, "Too many active bookings. Try again later.");

        var id = Guid.NewGuid();
        var token = tokens.Create(tenantId, id);
        var createRequest = new CreateAppointmentRequest(req.LocationId.Value, req.SpecialistId.Value, req.ServiceId.Value, startsAt,
            new ClientInput(null, name, phone, email, req.Client?.MarketingConsent == true), reminder, payment, "online");
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
        // Те саме для закритого дня закладу (вихідний/закриття, §17) і часу поза годинами закладу (§18.1): причина не розкривається.
        if (created.Error is { Code: "specialist_unavailable" or "location_closed" or "outside_location_hours" })
            return Error.Conflict("slot_unavailable", "The slot overlaps another appointment.");
        if (!created.IsOk) return created.Error!;

        await QueueConfirmationEmailAsync(created.Value!, email, ct);
        return new PublicCreateResult(new PublicBookingCreatedDto(token, await ToPublicAsync(created.Value!, ct)), false);
    }

    public const int MaxEmailLength = 254;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex EmailPattern();

    /// <summary>Порожній/відсутній -> null (валідно); інакше trim + lower-case, формат, ≤ 254, без керівних символів і кількох '@'.</summary>
    internal static string? NormalizeEmail(string? raw, out bool valid)
    {
        valid = true;
        var e = raw?.Trim();
        if (string.IsNullOrEmpty(e)) return null;
        e = e.ToLowerInvariant();
        if (e.Length > MaxEmailLength || e.Any(char.IsControl) || e.Count(c => c == '@') != 1 || !EmailPattern().IsMatch(e)
            || e.Split('@')[1].StartsWith('.') || e.EndsWith('.') || e.Contains(".."))
        {
            valid = false;
            return null;
        }
        return e;
    }

    /// <summary>
    /// Лист-підтвердження в outbox (kind confirmation: ключ "confirmation:{appointmentId}"). Адреса — з запиту, інакше збережена в клієнта.
    /// Токен скасування в листі НЕ передається (він лише в застосунку). Збій черги не скасовує вже створений запис; email у лог не потрапляє.
    /// </summary>
    private async Task QueueConfirmationEmailAsync(AppointmentDto a, string? requestEmail, CancellationToken ct)
    {
        try
        {
            var email = requestEmail ?? await pub.GetClientEmailAsync(a.ClientId, ct);
            if (string.IsNullOrWhiteSpace(email)) return;
            var tz = (await store.GetLocationAsync(a.LocationId, ct))?.Timezone ?? a.Timezone ?? "UTC";
            var address = await pub.GetLocationAddressAsync(a.LocationId, ct);
            await pub.QueueEmailMessageAsync(a.ClientId, email, $"confirmation:{a.Id}", ConfirmationText(a, tz, address), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "booking_confirmation_email_queue_failed appointment={AppointmentId}", a.Id);
        }
    }

    internal static string ConfirmationText(AppointmentDto a, string timezone, string? address)
    {
        DateTimeOffset local;
        try { local = TimeZoneInfo.ConvertTime(a.StartsAt, TimeZoneInfo.FindSystemTimeZoneById(timezone)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { local = a.StartsAt.ToUniversalTime(); }
        var lines = new List<string>
        {
            "Ваш запис підтверджено.",
            "",
            $"Заклад: {a.LocationName}",
        };
        if (!string.IsNullOrWhiteSpace(address)) lines.Add($"Адреса: {address}");
        lines.Add($"Послуга: {a.ServiceName}");
        lines.Add($"Майстер: {a.SpecialistName}");
        lines.Add($"Дата і час: {local.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)} ({timezone})");
        lines.Add("");
        lines.Add("Скасувати або переглянути запис можна в застосунку.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Політика за міткою клієнта: none — без перевірки; monitor — перевіряємо й лише позначаємо вердикт (Activity), не блокуємо;
    /// enforce — без валідного доказу відмова (fail closed). Web: captcha_failed (як і раніше); ios/android: integrity_failed.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Meter Meter = new("BeautyCrm.PublicBooking");
    private static readonly System.Diagnostics.Metrics.Counter<long> LimitReachedCounter =
        Meter.CreateCounter<long>("beauty.public_booking.limit_reached", description: "booking_limit_reached rejections by scope");

    /// <summary>ADR-011 Consequences: сигнал про можливе блокування закладу (метрика + Warning без PII: лише scope та id tenant/закладу).</summary>
    private Error LimitReached(string scope, Guid tenantId, Guid locationId, string message)
    {
        LimitReachedCounter.Add(1, new KeyValuePair<string, object?>("scope", scope));
        log.LogWarning("booking_limit_reached scope={Scope} tenant={TenantId} location={LocationId}", scope, tenantId, locationId);
        return Error.Validation("booking_limit_reached", message);
    }

    private async Task<Error?> CheckClientProofAsync(PublicRequestContext ctx, CancellationToken ct)
    {
        var mode = options.ProtectionFor(ctx.Client);
        if (mode == ProtectionMode.None) return null;
        var web = ctx.Client == ClientKind.Web;
        var proof = new ClientProof(
            web ? ClientProofKind.Captcha : ctx.Client == ClientKind.Ios ? ClientProofKind.AppAttest : ClientProofKind.PlayIntegrity,
            web ? ctx.CaptchaToken : ctx.IntegrityToken);
        var ok = !string.IsNullOrWhiteSpace(proof.Token) && await integrity.VerifyAsync(proof, ctx.RemoteIp, ct);
        if (mode == ProtectionMode.Monitor)
        {
            System.Diagnostics.Activity.Current?.SetTag("beauty.integrity.verdict", $"{ctx.Client}:{(ok ? "ok" : "fail")}");
            return null;
        }
        return ok ? null : web
            ? Error.Validation("captcha_failed", "CAPTCHA verification failed.")
            : Error.Validation("integrity_failed", "Client verification failed.");
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

    public async Task<Result<PublicCancelResultDto>> CancelAsync(string? token, CancellationToken ct, string? reason = null)
    {
        if (await FindAsync(token, ct) is not { } appt) return TokenNotFound;
        var result = await cancellation.CancelAsync(appt.Id, ct, CancelOrigin.Client, reason);
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
        services.AddSingleton<IClientIntegrityVerifier, DefaultClientIntegrityVerifier>(); // ADR-011: App Attest/Play Integrity — пізніше
        services.AddScoped<BeautyPublicCatalog.PublicCatalogService>();
        return services;
    }
}
