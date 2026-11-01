namespace BeautyCrm.Infrastructure.Data.Entities;

// TASK-776 (§26.3, ADR-015): маркетингова згода, кампанії, купони. Лише сутності й схема; логіка - в сервісах.

/// <summary>
/// Поточний стан маркетингової згоди клієнта по каналу. PK (tenant_id, client_id, channel).
/// Відсутній рядок = none («ще не обирав»). Рішення про розсилку приймається за цією таблицею, не за beauty_clients.marketing_consent.
/// </summary>
public class MarketingConsent : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    /// <summary>telegram | push.</summary>
    public string Channel { get; set; } = null!;
    /// <summary>full | selective | none.</summary>
    public string Level { get; set; } = null!;
    /// <summary>chat | admin | stop.</summary>
    public string Source { get; set; } = null!;
    /// <summary>Версія показаного тексту згоди (обов'язкова для source=chat і level&lt;&gt;none).</summary>
    public string? TextVersion { get; set; }
    /// <summary>Лише для source=admin (і тоді обов'язково).</summary>
    public Guid? UpdatedByUserId { get; set; }
    public Guid? ConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Client? Client { get; set; }
}

/// <summary>Append-only історія змін згоди (незмінність - GRANT SELECT/INSERT для API-ролі, як аудит собівартості).</summary>
public class MarketingConsentEvent : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public string Channel { get; set; } = null!;
    /// <summary>NULL = рядка згоди раніше не було.</summary>
    public string? LevelFrom { get; set; }
    public string LevelTo { get; set; } = null!;
    public string Source { get; set; } = null!;
    /// <summary>button | command | text | api.</summary>
    public string? Via { get; set; }
    public string? TextVersion { get; set; }
    public Guid? ActorUserId { get; set; }
    /// <summary>written | in_person | other; лише для source=admin.</summary>
    public string? Basis { get; set; }
    public string? BasisNote { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? ConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Client? Client { get; set; }
}

/// <summary>Кампанія розсилки. draft - редагується (кожен PUT збільшує Revision); scheduled|sending|done - після підтвердження owner/admin.</summary>
public class Campaign : TenantEntity
{
    public string Name { get; set; } = null!;
    /// <summary>promo | news.</summary>
    public string Type { get; set; } = null!;
    public Guid? PromotionId { get; set; }

    /// <summary>lapsed | frequent | new | all_consented.</summary>
    public string SegmentKind { get; set; } = null!;
    public short? SegmentDays { get; set; }
    public Guid? SegmentServiceId { get; set; }
    public short? SegmentMinVisits { get; set; }
    public short? SegmentPeriodDays { get; set; }
    public Guid? LocationId { get; set; }

    public string Template { get; set; } = null!;
    /// <summary>none | personal | shared.</summary>
    public string CouponMode { get; set; } = "none";
    public Guid? SharedCouponId { get; set; }
    public DateTimeOffset? CouponValidFrom { get; set; }
    public DateTimeOffset? CouponValidTo { get; set; }

    /// <summary>draft | scheduled | sending | done | cancelled.</summary>
    public string Status { get; set; } = "draft";
    public int Revision { get; set; } = 1;
    public DateTimeOffset? ScheduledAt { get; set; }
    public int? RecipientsTotal { get; set; }

    public Guid CreatedByUserId { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    /// <summary>SHA-256 (hex) Idempotency-Key підтвердження.</summary>
    public string? ConfirmKeyHash { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public Promotion? Promotion { get; set; }
    public Service? SegmentService { get; set; }
    public Location? Location { get; set; }
    public Coupon? SharedCoupon { get; set; }
}

/// <summary>Одержувач кампанії (знімок при підтвердженні). Ключ ідемпотентності повідомлення - campaign:{campaignId}:{clientId}.</summary>
public class CampaignRecipient : TenantEntity
{
    public Guid CampaignId { get; set; }
    public Guid ClientId { get; set; }
    /// <summary>pending | queued | sent | failed | skipped.</summary>
    public string Status { get; set; } = "pending";
    /// <summary>Код причини (§26.8); обов'язковий для skipped.</summary>
    public string? SkipReason { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? CouponId { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ClickedAt { get; set; }

    public Campaign? Campaign { get; set; }
    public Client? Client { get; set; }
    public Message? Message { get; set; }
    public Coupon? Coupon { get; set; }
}

/// <summary>
/// Купон. personal: CSPRNG-код на одержувача, у БД лише code_hash (SHA-256) і code_hint; shared: код у відкритому вигляді (публічний за природою).
/// Знижка купона = знижка його акції (access=code_only).
/// </summary>
public class Coupon : TenantEntity
{
    /// <summary>personal | shared.</summary>
    public string Kind { get; set; } = null!;
    public Guid PromotionId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? ClientId { get; set; }
    /// <summary>lower hex SHA-256("coupon:" + normalize(code)), 64 символи.</summary>
    public string CodeHash { get; set; } = null!;
    /// <summary>Лише shared (верхній регістр, 4..20 символів).</summary>
    public string? Code { get; set; }
    /// <summary>Останні 3 символи нормалізованого коду.</summary>
    public string CodeHint { get; set; } = null!;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidTo { get; set; }
    public int? MaxUses { get; set; }
    public int UsesCount { get; set; }
    /// <summary>active | revoked.</summary>
    public string Status { get; set; } = "active";
    public Guid? CreatedByUserId { get; set; }

    public Promotion? Promotion { get; set; }
    public Campaign? Campaign { get; set; }
    public Client? Client { get; set; }
}

/// <summary>Погашення купона на запис. Повернення (скасування запису, збій оплати) = status released, uses_count - 1.</summary>
public class CouponRedemption : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CouponId { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid ClientId { get; set; }
    /// <summary>price_original - price_final.</summary>
    public decimal DiscountAmount { get; set; }
    /// <summary>active | released.</summary>
    public string Status { get; set; } = "active";
    /// <summary>cancelled | payment_failed.</summary>
    public string? ReleaseReason { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Coupon? Coupon { get; set; }
    public Appointment? Appointment { get; set; }
    public Client? Client { get; set; }
}
