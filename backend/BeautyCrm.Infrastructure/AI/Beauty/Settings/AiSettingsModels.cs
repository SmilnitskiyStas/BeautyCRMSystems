using System.Text.Json.Serialization;

namespace BeautyCrm.Infrastructure.AI.Beauty.Settings;

// DTO API налаштувань ШІ (контракт §20.5). Request-DTO з ключем перевизначають ToString, щоб ключ не потрапив у лог/виняток.

public sealed record AiRoleInput(string? Provider, string? Model);

/// <summary>Типи ширші за колонки БД, щоб значення поза межами дали 422 invalid_limits, а не помилку прив'язки.</summary>
public sealed record AiLimitsInput(long? MonthlyTokenLimit, long? MonthlyRequestLimit, int? MaxOutputTokensPerRequest);

/// <summary>Повна заміна. <c>KeySource</c> необов'язковий і лише для owner (admin: 403 forbidden_role, якщо значення змінюється).</summary>
public sealed record UpdateAiSettingsRequest(
    AiRoleInput? Chat, AiRoleInput? Analytics, string? Mode, string? PromptAddendum, AiLimitsInput? Limits,
    bool? AcknowledgeAutoMode, string? KeySource);

public sealed record AiKeySourceRequest(string? KeySource);

public sealed record AiKeySaveRequest(string? ApiKey, string? ForRole, bool? AcknowledgeAffectedRoles, bool? Activate, bool? Verify)
{
    public override string ToString() => "AiKeySaveRequest(***)";
}

public sealed record AiKeyTestRequest(string? ApiKey)
{
    public override string ToString() => "AiKeyTestRequest(***)";
}

public sealed record AiRoleDto(string Provider, string Model, string Tier, bool ModelFallback);

public sealed record AiLimitsDto(long? MonthlyTokenLimit, int? MonthlyRequestLimit, int MaxOutputTokensPerRequest);

public sealed record AiPlatformQuotaDto(bool Enabled, long? MonthlyTokens, int? MonthlyRequests);

public sealed record AiRoleStatusDto(string State, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

public sealed record AiStatusDto(AiRoleStatusDto Chat, AiRoleStatusDto Analytics);

public sealed record AiKeyInfoDto(
    string Provider, bool HasKey, string? Last4, DateTimeOffset? UpdatedAt, string? UpdatedByName, IReadOnlyList<string> UsedByRoles);

public sealed record AiWarningDto(string Code, string Message);

/// <summary>
/// <c>Keys</c> є лише для owner (для admin властивості немає взагалі, а не null). Ключів у відповіді немає: лише last4.
/// <c>Warnings</c> - лише у відповіді PUT /ai/settings.
/// </summary>
public sealed record AiSettingsDto(
    string KeySource, AiRoleDto Chat, AiRoleDto Analytics, string Mode, string PromptAddendum, bool IsDefaultPrompt,
    string DefaultPromptAddendum, int PromptAddendumMaxLength, AiLimitsDto Limits, AiPlatformQuotaDto PlatformQuota, AiStatusDto Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AiKeyInfoDto>? Keys,
    DateTimeOffset? UpdatedAt, string? UpdatedByName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AiWarningDto>? Warnings = null);

public sealed record AiKeySavedDto(
    string Provider, bool HasKey, string? Last4, DateTimeOffset? UpdatedAt, string KeySource, IReadOnlyList<string> UsedByRoles);

public sealed record AiKeyTestDto(
    bool Ok, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    string Provider, string Model, DateTimeOffset CheckedAt);

public sealed record AiCatalogRoleDto(
    string Id, string Label, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? InUse);

public sealed record AiCatalogProviderDto(string Id, string Label, bool Available, string? AvailabilityLabel);

public sealed record AiCatalogModelDto(
    string Id, string Provider, bool Available, string Tier, string TierLabel, int CostRank, string CostLabel,
    decimal? InputPricePerMTok, decimal? OutputPricePerMTok, string Currency, DateOnly? PriceUpdatedAt, IReadOnlyList<string> IsDefaultFor);

public sealed record AiCatalogDto(
    IReadOnlyList<AiCatalogRoleDto> Roles, IReadOnlyList<AiCatalogProviderDto> Providers, IReadOnlyList<AiCatalogModelDto> Models);

public sealed record AiUsagePeriodDto(string From, string To);
public sealed record AiUsageLimitsDto(long? MonthlyTokens, long? MonthlyRequests, string Source);
public sealed record AiUsageCurrentDto(long Requests, long Errors, long InputTokens, long OutputTokens, decimal? EstimatedCost, string Currency);
public sealed record AiUsageByModelDto(
    string Role, string Provider, string Model, string KeySource, long Requests, long InputTokens, long OutputTokens, decimal? EstimatedCost);
public sealed record AiUsageHistoryDto(string Period, long Requests, long InputTokens, long OutputTokens);

public sealed record AiUsageDto(
    string KeySource, AiUsagePeriodDto Period, AiUsageLimitsDto Limits, AiUsageCurrentDto Current,
    IReadOnlyList<AiUsageByModelDto> ByModel, IReadOnlyList<AiUsageHistoryDto> History);
