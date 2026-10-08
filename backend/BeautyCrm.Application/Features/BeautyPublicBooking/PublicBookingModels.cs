using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyPublicBooking;

// Публічний онлайн-запис (TASK-688). DTO навмисно без внутрішніх id клієнта й запису: запис адресується токеном.

public sealed class PublicBookingOptions
{
    /// <summary>Якщо true, POST /appointments вимагає токен CAPTCHA, який має підтвердити <see cref="ICaptchaVerifier"/>.</summary>
    public bool CaptchaRequired { get; set; }
    /// <summary>Скільки майбутніх активних онлайн-записів може мати один телефон у tenant.</summary>
    public int MaxActiveBookingsPerPhone { get; set; } = 3;
    /// <summary>Скільки онлайн-записів (будь-який статус) можна створити з одного телефону за годину.</summary>
    public int MaxCreatesPerPhonePerHour { get; set; } = 5;
    /// <summary>Горизонт запису/слотів уперед, днів.</summary>
    public int MaxDaysAhead { get; set; } = 180;
    /// <summary>Ключ HMAC для публічних токенів (щонайменше 32 байти).</summary>
    public byte[] TokenKey { get; set; } = [];
}

public sealed record PublicLocationDto(Guid Id, string Name, string? Address, string? Phone, string Timezone);
public sealed record PublicSpecialistDto(Guid Id, string Name, string? Title, string? PhotoUrl);

/// <summary>Послуга закладу: ціна з override закладу й акцією, що діє зараз.</summary>
public sealed record PublicServiceDto(
    Guid Id, string Name, string? Description, string? Category, int DurationMinutes,
    decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string? PromotionName);

/// <param name="MarketingConsent">Лише явне true дає згоду на маркетинг (лише для нового клієнта; наявного профілю не змінює).</param>
public sealed record PublicClientInput(string? Name, string? Phone, bool? MarketingConsent = null);

public sealed record PublicCreateAppointmentRequest(
    Guid? LocationId, Guid? SpecialistId, Guid? ServiceId, DateTimeOffset? StartsAt,
    PublicClientInput? Client, string? Reminder, string? PaymentMethod, string? CaptchaToken, string? Website = null);

/// <summary>Контекст HTTP-запиту, потрібний захисту від зловживань (без PII).</summary>
public sealed record PublicRequestContext(string? IdempotencyKey, string? CaptchaToken, string? RemoteIp);

/// <summary>Вигляд запису для клієнта: мінімум PII (без імені/телефону/email клієнта й без внутрішніх id).</summary>
public sealed record PublicAppointmentDto(
    Guid LocationId, string LocationName, Guid SpecialistId, string SpecialistName, Guid ServiceId, string ServiceName,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, int DurationMinutes, string Status,
    decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string ReminderOption,
    string? PaymentMethod, string? PaymentStatus, CancellationTerms? Cancellation);

public sealed record PublicBookingCreatedDto(string PublicToken, PublicAppointmentDto Appointment);

/// <param name="Replayed">true — повтор того самого Idempotency-Key (HTTP 200 замість 201).</param>
public sealed record PublicCreateResult(PublicBookingCreatedDto Booking, bool Replayed);

public sealed record PublicCancelResultDto(PublicAppointmentDto Appointment, decimal RefundAmount, int RefundPercent, int FeePercent);

public sealed record IdempotencyHit(Guid AppointmentId, string RequestHash);

/// <summary>Порт даних публічного запису. Усе виконується під RLS tenant-а, визначеного за slug.</summary>
public interface IPublicBookingStore
{
    Task<IReadOnlyList<PublicLocationDto>> ListActiveLocationsAsync(CancellationToken ct);
    /// <summary>Лише придатні до запису майстри: активні, зі збереженим графіком у закладі й призначеними послугами; serviceId — що надають цю послугу.</summary>
    Task<IReadOnlyList<PublicSpecialistDto>> ListSpecialistsAsync(Guid locationId, Guid? serviceId, CancellationToken ct);
    /// <summary>Активні послуги з ціною для закладу (override або мережева); без ціни пропускаються.</summary>
    /// <summary>Лише послуги, призначені хоча б одному придатному майстру закладу (specialistId — саме цьому майстру).</summary>
    Task<IReadOnlyList<PublicServiceBase>> ListServicesAsync(Guid locationId, Guid? specialistId, CancellationToken ct);
    Task<Guid?> FindAppointmentIdByTokenHashAsync(string tokenHash, CancellationToken ct);
    Task<IdempotencyHit?> FindByIdempotencyKeyAsync(string keyHash, CancellationToken ct);
    /// <summary>Майбутні активні (pending/confirmed) онлайн-записи клієнтів із цим телефоном.</summary>
    Task<int> CountActiveOnlineByPhoneAsync(string phone, DateTimeOffset now, CancellationToken ct);
    /// <summary>Онлайн-записи з цим телефоном, створені після since (будь-який статус) — для rate limit на телефон.</summary>
    Task<int> CountRecentOnlineByPhoneAsync(string phone, DateTimeOffset since, CancellationToken ct);
}

public sealed record PublicServiceBase(Guid Id, string Name, string? Description, string? Category, int DurationMinutes, decimal Price);

/// <summary>
/// Хук CAPTCHA: провайдера не обрано. Реалізація підміняє реєстрацію в DI. Типова реалізація відхиляє все
/// (fail closed), тож <c>PublicBooking:CaptchaRequired=true</c> без підключеного провайдера блокує створення записів.
/// </summary>
public interface ICaptchaVerifier
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

public sealed class RejectAllCaptchaVerifier : ICaptchaVerifier
{
    public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct) => Task.FromResult(false);
}
