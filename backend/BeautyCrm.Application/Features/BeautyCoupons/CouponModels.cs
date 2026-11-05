using System.Text.Json.Serialization;
using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyCoupons;

// Купони (TASK-778, §26.6/§26.11.4, ADR-015). Персональні коди API не віддає ніколи: лише code_hint (останні 3 символи).

public enum CouponScope { Staff, Public, Chat }

/// <summary>
/// Купон у запиті на запис. Рівно одне з <paramref name="Code"/> (staff, публічний, код текстом у чаті) / <paramref name="CouponId"/>
/// (чат: купон із пропозиції розмови, не з тексту моделі). <paramref name="RateKey"/> - ключ лічильника спроб: id користувача (staff),
/// IP (публічний), id розмови (чат). Код не потрапляє в ToString/логи.
/// </summary>
public sealed record CouponUse(CouponScope Scope, string? Code = null, Guid? CouponId = null, Guid? CampaignId = null, string? RateKey = null)
{
    private bool PrintMembers(System.Text.StringBuilder sb)
    {
        sb.Append("Scope = ").Append(Scope);
        return true;
    }
}

public sealed class CouponOptions
{
    private int _chat = 5, _public = 10, _staff = 30, _hold = 72, _bare = 20, _bareTenant = 200, _write = 20;
    /// <summary>`Coupons:ChatBareLookupPerConversationPerHour` (M5): пошуків за хешем для тексту БЕЗ префікса «код…» і без формату персонального коду, що не знайшли купон; понад - хеш не шукається.</summary>
    public int ChatBareLookupPerConversationPerHour { get => _bare; set => _bare = Math.Clamp(value, 1, 1000); }
    /// <summary>`Coupons:ChatBareLookupPerTenantPerHour` (M5): те саме на tenant (розмови можна плодити).</summary>
    public int ChatBareLookupPerTenantPerHour { get => _bareTenant; set => _bareTenant = Math.Clamp(value, 1, 100_000); }
    /// <summary>`Coupons:WritePerUserPerMinute` (L7): створення / відкликання загальних кодів на користувача.</summary>
    public int WritePerUserPerMinute { get => _write; set => _write = Math.Clamp(value, 1, 1000); }
    /// <summary>`Coupons:BlockedCodes` (M5): очевидні слова, які не можна використовувати як загальний код (порівняння за літерами без цифр; додається до типового списку).</summary>
    public HashSet<string> BlockedCodes { get; set; } = new(DefaultBlockedCodes, StringComparer.Ordinal);
    public static readonly string[] DefaultBlockedCodes =
    [
        "SALE", "PROMO", "PROMOCODE", "VIP", "DISCOUNT", "COUPON", "CODE", "GIFT", "FREE", "NEW", "WELCOME", "BONUS", "TEST", "HELLO", "ADMIN",
        "SKIDKA", "AKCIYA", "AKCIA", "PODAROK", "ZNIZHKA", "BEAUTY", "SALON", "BLACKFRIDAY", "SUPER", "MEGA",
        "ABCD", "ABCDEF", "QWERTY", "PASSWORD", "ALL", "ONE", "TWO",
    ];
    /// <summary>`Coupons:ChatInvalidPerConversationPerHour`: невдалих кодів у чаті за годину; понад - coupon_locked.</summary>
    public int ChatInvalidPerConversationPerHour { get => _chat; set => _chat = Math.Clamp(value, 1, 1000); }
    /// <summary>`Coupons:PublicCheckPerIpPer10Min`: публічна перевірка / створення з кодом (невдалі й успішні).</summary>
    public int PublicCheckPerIpPer10Min { get => _public; set => _public = Math.Clamp(value, 1, 10_000); }
    /// <summary>`Coupons:StaffCheckPerUserPerMinute`.</summary>
    public int StaffCheckPerUserPerMinute { get => _staff; set => _staff = Math.Clamp(value, 1, 10_000); }
    /// <summary>`Coupons:ConversationHoldHours` (1..720): скільки діє пропозиція розмови (використовує чат).</summary>
    public int ConversationHoldHours { get => _hold; set => _hold = Math.Clamp(value, 1, 720); }
}

// ---- порт даних ----

/// <param name="Kind">personal | shared.</param>
/// <param name="Status">active | revoked.</param>
public sealed record CouponInfo(
    Guid Id, string Kind, Guid PromotionId, Guid? CampaignId, Guid? ClientId, string CodeHint, string? Code,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo, int? MaxUses, int UsesCount, string Status);

/// <summary>Купон + його акція (правило без звуження періоду) і доступ акції (public | code_only).</summary>
public sealed record CouponLookup(CouponInfo Coupon, PromotionRule Promotion, string PromotionAccess);

public sealed record NewSharedCoupon(Guid PromotionId, string Code, string CodeHash, string CodeHint, DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo, int? MaxUses, Guid? CreatedByUserId);

public sealed record NewPersonalCoupon(Guid CampaignId, Guid ClientId, Guid PromotionId, string CodeHash, string CodeHint,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo, Guid? CreatedByUserId)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public interface ICouponStore
{
    /// <summary>Пошук за хешем під RLS tenant-а.</summary>
    Task<CouponLookup?> FindByHashAsync(string codeHash, CancellationToken ct);
    Task<CouponLookup?> FindByIdAsync(Guid id, CancellationToken ct);
    /// <summary>Чи має клієнт активне (не повернене) погашення цього купона.</summary>
    Task<bool> HasActiveRedemptionAsync(Guid couponId, Guid clientId, CancellationToken ct);
    /// <summary>public | code_only; null - акції немає.</summary>
    Task<string?> GetPromotionAccessAsync(Guid promotionId, CancellationToken ct);
    Task<IReadOnlyList<SharedCouponDto>> ListSharedAsync(Guid? promotionId, CancellationToken ct);
    /// <summary>null - колізія хешу коду (UNIQUE tenant+code_hash).</summary>
    Task<SharedCouponDto?> CreateSharedAsync(NewSharedCoupon coupon, CancellationToken ct);
    /// <summary>Ідемпотентно status = revoked. false - загального купона з таким id немає (персональні сюди не потрапляють).</summary>
    Task<bool> RevokeSharedAsync(Guid id, CancellationToken ct);
    /// <summary>false - колізія хешу коду.</summary>
    Task<bool> InsertPersonalAsync(NewPersonalCoupon coupon, CancellationToken ct);
    /// <summary>Персональні купони клієнта (без кодів). null - клієнта немає.</summary>
    Task<IReadOnlyList<ClientCouponDto>?> ListForClientAsync(Guid clientId, CancellationToken ct);
    /// <summary>
    /// Повернення купона запису: active -> released (+ release_reason, released_at) і uses_count - 1 в одній транзакції. Ідемпотентно;
    /// повертає кількість повернених погашень (0 або 1).
    /// </summary>
    Task<int> ReleaseForAppointmentAsync(Guid appointmentId, string reason, DateTimeOffset at, CancellationToken ct);
}

public static class CouponReleaseReasons
{
    public const string Cancelled = "cancelled";
    public const string PaymentFailed = "payment_failed";
}

// ---- DTO API (без кодів персональних купонів) ----

public sealed record SharedCouponDto(Guid Id, string Kind, Guid PromotionId, string Code, DateTimeOffset ValidFrom, DateTimeOffset ValidTo,
    int? MaxUses, int UsesCount, string Status);

public sealed record CreateSharedCouponRequest(Guid? PromotionId, string? Code, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, int? MaxUses);

/// <summary>PATCH /coupons/{id}: лише status = revoked.</summary>
public sealed record PatchCouponRequest(string? Status);

public sealed record NamedRefDto(Guid Id, string Name);

public sealed record ClientCouponDto(Guid Id, string Kind, string CodeHint, NamedRefDto Promotion, NamedRefDto? Campaign,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo, string Status, DateTimeOffset? UsedAt, Guid? AppointmentId);

public sealed record CouponCheckRequest(string? Code, Guid? LocationId, Guid? ServiceId, Guid? ClientId);

public sealed record CouponCheckResult(string Outcome, decimal PriceOriginal, decimal PriceFinal, Guid CouponId, string PromotionName);

/// <summary>Публічна перевірка: без id купона й назви акції.</summary>
public sealed record PublicCouponCheckDto(string Outcome, decimal PriceOriginal, decimal PriceFinal);

public sealed record IssueCouponRequest(Guid CampaignId, Guid ClientId, Guid PromotionId, DateTimeOffset ValidFrom, DateTimeOffset ValidTo,
    Guid? CreatedByUserId = null);

/// <summary>Щойно випущений персональний код: єдине місце, де існує повний код (для повідомлення клієнту). Не серіалізується й не друкується.</summary>
public sealed record IssuedCoupon(Guid Id, [property: JsonIgnore] string Code)
{
    private bool PrintMembers(System.Text.StringBuilder sb)
    {
        sb.Append("Id = ").Append(Id);
        return true;
    }

    /// <summary>XXXXX-XXXXX.</summary>
    [JsonIgnore]
    public string DisplayCode => CouponCodes.Display(Code);
}
