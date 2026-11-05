namespace BeautyCrm.Infrastructure.Data.Entities;

// ---------- AI settings per tenant (TASK-713, contract §20.4, ADR-010) ----------
// Майстер-ключ (keyring) у БД не зберігається. Жодних колонок для вмісту повідомлень, тексту промпту в журналі чи значень ключа.

/// <summary>Налаштування ШІ tenant (один рядок на tenant; немає рядка = дефолти в коді).</summary>
public class AiSettingsRow : TenantEntity
{
    public string KeySource { get; set; } = "platform";
    public string ChatProvider { get; set; } = "anthropic";
    public string ChatModel { get; set; } = null!;
    public string AnalyticsProvider { get; set; } = "anthropic";
    public string AnalyticsModel { get; set; } = null!;
    public string Mode { get; set; } = "confirm";
    /// <summary>NULL = типовий додаток із коду.</summary>
    public string? PromptAddendum { get; set; }
    public long? MonthlyTokenLimit { get; set; }
    public int? MonthlyRequestLimit { get; set; }
    public int MaxOutputTokens { get; set; } = 1024;
    // пише лише платформний PATCH
    public bool? PlatformEnabled { get; set; }
    public long? PlatformMonthlyTokenQuota { get; set; }
    public int? PlatformMonthlyRequestQuota { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>Власний ключ провайдера tenant: лише шифротекст <c>v2:{kid}:…</c>, kid і last4.</summary>
public class AiProviderKey : TenantEntity
{
    public string Provider { get; set; } = null!;
    public string KeyEncrypted { get; set; } = null!;
    public string KeyKid { get; set; } = null!;
    public string KeyLast4 { get; set; } = null!;
    public Guid CreatedByUserId { get; set; }
    public Guid UpdatedByUserId { get; set; }
}

/// <summary>Місячний облік використання (UTC) за роллю, провайдером, моделлю й джерелом ключа.</summary>
public class AiUsageRow : TenantEntity
{
    public DateOnly PeriodStart { get; set; }
    public string Role { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string Model { get; set; } = null!;
    public string KeySource { get; set; } = null!;
    public int RequestCount { get; set; }
    public int ErrorCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>Append-only журнал змін налаштувань: лише назви полів, без значень ключа, last4 і тексту промпту.</summary>
public class AiSettingsAuditRow : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>NULL = оператор платформи.</summary>
    public Guid? UserId { get; set; }
    public string Action { get; set; } = null!;
    public string? Provider { get; set; }
    public string[] ChangedFields { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
