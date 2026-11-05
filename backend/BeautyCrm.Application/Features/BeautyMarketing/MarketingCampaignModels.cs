using System.Text.Json.Serialization;

namespace BeautyCrm.Application.Features.BeautyMarketing;

// Аудиторії й кампанії (TASK-779, §26.5, §26.7, §26.11.3, §26.11.5, ADR-015). DTO без ПД: імен, телефонів, id клієнтів, кодів купонів немає.

public static class CampaignTypes
{
    public const string Promo = "promo", News = "news";
}

public static class SegmentKinds
{
    public const string Lapsed = "lapsed", Frequent = "frequent", New = "new", AllConsented = "all_consented";
    public static readonly string[] All = [Lapsed, Frequent, New, AllConsented];
}

public static class CouponModes
{
    public const string None = "none", Personal = "personal", Shared = "shared";
}

public static class CampaignStatuses
{
    public const string Draft = "draft", Scheduled = "scheduled", Sending = "sending", Done = "done", Cancelled = "cancelled";
    public static readonly string[] All = [Draft, Scheduled, Sending, Done, Cancelled];
}

/// <summary>Сегмент у запиті: kind + параметри виду (§26.5). Дефолти (lapsed 60 днів; frequent 3 за 90) підставляє сервіс.</summary>
public sealed record SegmentDto(string? Kind, int? Days = null, Guid? ServiceId = null, int? MinVisits = null, int? PeriodDays = null);

public sealed record CampaignRequest(
    string? Name, string? Type, Guid? PromotionId, SegmentDto? Segment, Guid? LocationId, string? Template, string? CouponMode,
    Guid? SharedCouponId = null, DateTimeOffset? CouponValidFrom = null, DateTimeOffset? CouponValidTo = null, DateTimeOffset? ScheduledAt = null);

public sealed record CampaignDto(
    Guid Id, string Name, string Type, string Status, int Revision, Guid? PromotionId, SegmentDto Segment, Guid? LocationId, string Template,
    string CouponMode, Guid? SharedCouponId, DateTimeOffset? CouponValidFrom, DateTimeOffset? CouponValidTo, DateTimeOffset? ScheduledAt,
    int? RecipientsTotal, Guid? ConfirmedBy, DateTimeOffset? ConfirmedAt, Guid? CancelledBy, DateTimeOffset? CancelledAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, Guid CreatedBy, DateTimeOffset CreatedAt);

public sealed record CampaignListItemDto(
    Guid Id, string Name, string Type, string Status, NamedRef? Promotion, DateTimeOffset? ScheduledAt, int? RecipientsTotal, int Sent, DateTimeOffset CreatedAt);

public sealed record NamedRef(Guid Id, string Name);

public sealed record CampaignListDto(IReadOnlyList<CampaignListItemDto> Items, string? NextCursor);

/// <summary>Причини виключення з аудиторії (§26.5): лише кількості.</summary>
public sealed record AudienceExclusions(int NoTelegram, int Unsubscribed, int NoConsent, int LevelMismatch, int Frequency);

public sealed record AudiencePreviewRequest(string? Type, Guid? PromotionId, SegmentDto? Segment, Guid? LocationId);

/// <summary>Перегляд аудиторії: кількості й розбивка. Без ПД.</summary>
public sealed record AudiencePreviewDto(int Matched, int Recipients, AudienceExclusions Excluded);

public sealed record CampaignLimitsDto(int DailyLimit, int ClientIntervalDays, int EstimatedDays);

public sealed record CampaignWindowDto(string From, string To);

public sealed record CampaignPreviewDto(
    int Revision, string SampleText, IReadOnlyList<string> Buttons, int Matched, int Recipients, AudienceExclusions Excluded,
    CampaignLimitsDto Limits, CampaignWindowDto Window, IReadOnlyList<string> Warnings);

/// <summary>Тіло підтвердження: revision з перегляду; scheduledAt - null = «зараз» (або значення з чернетки).</summary>
public sealed record ConfirmCampaignRequest(int? Revision, DateTimeOffset? ScheduledAt = null);

public sealed record CampaignStatusCounts(int Pending, int Queued, int Sent, int Failed, int Skipped);

public sealed record CampaignFailedCounts(int BotBlocked, int ChatNotFound, int Other);

/// <summary><c>Viewed</c> завжди null: Telegram не дає «переглянуто» (UI пояснює).</summary>
public sealed record CampaignStatsDto(
    int Recipients, CampaignStatusCounts ByStatus, CampaignFailedCounts Failed, IReadOnlyDictionary<string, int> Skipped, int Clicks,
    int Bookings, int BookingsWithCoupon, int CouponRedemptionsActive, decimal Revenue, int UnsubscribedAfter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Viewed = null);

public sealed record CampaignCouponSummaryDto(int Issued, int RedeemedActive, int Released, int Revoked);
