using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.AI.Secrets;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;

namespace BeautyCrm.Infrastructure.AI.Beauty;

// ---- Порти зберігання (ADR-010 §2.4). Читання налаштувань і ключів — EF-адаптери в Adapters/EfAiStores.cs;
// запис налаштувань/ключів (PUT/DELETE, аудит) додає TASK-716. ----

public interface IAiTenantSettingsStore
{
    /// <summary>Рядок налаштувань tenant або null (тоді діють дефолти). Читає під RLS tenant.</summary>
    Task<AiSettingsRow?> GetAsync(Guid tenantId, CancellationToken ct);
}

public interface IAiProviderKeyStore
{
    Task<AiProviderKey?> GetAsync(Guid tenantId, string provider, CancellationToken ct);
}

public sealed record AiUsageKey(string Role, string Provider, string Model, string KeySource);
/// <summary>Сума за місяць по всіх ролях/моделях/джерелах.</summary>
public sealed record AiUsageTotals(long Requests, long Tokens);

public interface IAiUsageStore
{
    Task<AiUsageTotals> GetTotalsAsync(Guid tenantId, DateOnly period, CancellationToken ct);

    /// <summary>
    /// Атомарно: якщо місячні суми нижчі за ліміти — request_count + 1 (upsert) і true; інакше false без змін.
    /// Серіалізація per (tenant, місяць): паралельні виклики при ліміті L дають рівно L успіхів.
    /// </summary>
    Task<bool> TryReserveRequestAsync(Guid tenantId, DateOnly period, AiUsageKey key, long? requestLimit, long? tokenLimit, CancellationToken ct);

    Task AddTokensAsync(Guid tenantId, DateOnly period, AiUsageKey key, long inputTokens, long outputTokens, CancellationToken ct);
    Task AddErrorAsync(Guid tenantId, DateOnly period, AiUsageKey key, CancellationToken ct);
}

// ---- Ефективні налаштування ----

public sealed record AiRoleSelection(string Provider, string Model);

public sealed record AiEffectiveSettings(
    string KeySource, AiRoleSelection Chat, AiRoleSelection Analytics, AiMode Mode, string? PromptAddendum,
    int MaxOutputTokens, long? MonthlyTokenLimit, int? MonthlyRequestLimit,
    bool? PlatformEnabled, long? PlatformTokenQuota, int? PlatformRequestQuota, bool IsDefault);

public sealed record AiRoleStatus(string State, string? Reason)
{
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";
    public bool IsReady => State == Ready;
    public static AiRoleStatus Ok() => new(Ready, null);
    public static AiRoleStatus No(string reason) => new(Unavailable, reason);
}

public sealed record AiStatus(AiRoleStatus Chat, AiRoleStatus Analytics);

/// <summary>
/// Готовність ШІ саме для асистента в чаті клієнта (M2). <see cref="State"/>: `ready` | `unavailable` (<see cref="Reason"/> - причина ролі) |
/// `limits_required` (власний ключ без місячних лімітів токенів і запитів: витрати не обмежені, тож асистент каналу не вмикається).
/// <see cref="RequestLimit"/> - ефективний місячний ліміт запитів (null = без ліміту).
/// </summary>
public sealed record ChatAssistantGate(string State, string? Reason, long? RequestLimit)
{
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";
    public const string LimitsRequired = "limits_required";
}

/// <summary>План одного виклику ШІ: усе, що потрібно для виклику провайдера й обліку. Ключ — у <see cref="AiSecret"/>.</summary>
public sealed record AiCallPlan(
    string Role, IAiProvider Provider, string ProviderId, string Model, int MaxOutputTokens, AiSecret Key, string KeySource,
    long? RequestLimit, long? TokenLimit);

public static class AiUnavailableReasons
{
    public const string ProviderNotAvailable = "provider_not_available";
    public const string SecretsUnavailable = "secrets_unavailable";
    public const string OwnKeyMissing = "own_key_missing";
    public const string OwnKeyUnreadable = "own_key_unreadable";
    public const string PlatformDisabled = "platform_disabled";
    public const string PlatformKeyMissing = "platform_key_missing";
    public const string QuotaExceeded = "quota_exceeded";
}

/// <summary>
/// Per-tenant налаштування й стан ШІ (замість singleton AiSettings, контракт §20.3). tenant береться ЛИШЕ з аргументу
/// викликача, який бере його з автентифікованого/tenant-scoped контексту; <see cref="TenantGuard"/> звіряє його зі scope
/// (розбіжність = <see cref="TenantMismatchException"/>). Ніколи не з промпту чи аргументів LLM.
/// </summary>
public sealed class AiTenantRuntime(
    IAiTenantSettingsStore settingsStore, IAiProviderKeyStore keyStore, IAiUsageStore usageStore,
    AiModelCatalog catalog, AiProviderRegistry providers, IAiSecretProtector protector, AiPlatformOptions platform,
    TenantContext tenant, TimeProvider clock)
{
    public const string KeySourceOwn = "own";
    public const string KeySourcePlatform = "platform";

    public static DateOnly PeriodOf(DateTimeOffset now) { var u = now.UtcDateTime; return new DateOnly(u.Year, u.Month, 1); }
    public static DateTimeOffset NextPeriodStart(DateTimeOffset now) { var p = PeriodOf(now); return new DateTimeOffset(p.Year, p.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1); }

    /// <summary>Немає рядка = безпечні дефолти з ADR-010: platform (вимкнена, квота 0), chat=haiku, analytics=sonnet, confirm.</summary>
    public async Task<AiEffectiveSettings> GetEffectiveAsync(Guid tenantId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var row = await settingsStore.GetAsync(tenantId, ct);
        if (row is null)
        {
            return new AiEffectiveSettings(KeySourcePlatform, DefaultSelection(AiRoles.Chat), DefaultSelection(AiRoles.Analytics),
                AiMode.Confirm, null, 1024, null, null, null, null, null, IsDefault: true);
        }
        return new AiEffectiveSettings(
            row.KeySource == KeySourceOwn ? KeySourceOwn : KeySourcePlatform,
            new AiRoleSelection(row.ChatProvider, row.ChatModel), new AiRoleSelection(row.AnalyticsProvider, row.AnalyticsModel),
            AiModes.Parse(row.Mode), row.PromptAddendum, row.MaxOutputTokens, row.MonthlyTokenLimit, row.MonthlyRequestLimit,
            row.PlatformEnabled, row.PlatformMonthlyTokenQuota, row.PlatformMonthlyRequestQuota, IsDefault: false);
    }

    /// <summary>Режим tenant для BeautyAssistant і POST /ai/actions. auto лише якщо збережено (потребує acknowledgeAutoMode при записі).</summary>
    public async Task<AiMode> GetModeAsync(Guid tenantId, CancellationToken ct) => (await GetEffectiveAsync(tenantId, ct)).Mode;

    private AiRoleSelection DefaultSelection(string role)
    {
        var d = catalog.DefaultFor(role, providers);
        // Каталог без доступного дефолту: порожня модель дасть provider_not_available/unknown у стані, а не падіння.
        return d is null ? new AiRoleSelection(AnthropicAiProvider.ProviderId, "") : new AiRoleSelection(d.Provider, d.Id);
    }

    public AiResolvedModel? ResolveRole(AiEffectiveSettings s, string role)
    {
        var sel = role == AiRoles.Analytics ? s.Analytics : s.Chat;
        return catalog.Resolve(role, sel.Provider, sel.Model, providers);
    }

    /// <summary>Статус обох ролей (контракт §20.3), включно з перевіркою місячного ліміту.</summary>
    public async Task<AiStatus> GetStatusAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await GetEffectiveAsync(tenantId, ct);
        var chat = await EvaluateAsync(tenantId, s, AiRoles.Chat, withQuota: true, ct);
        var analytics = await EvaluateAsync(tenantId, s, AiRoles.Analytics, withQuota: true, ct);
        return new AiStatus(chat.Status, analytics.Status);
    }

    /// <summary>
    /// Чи можна відповідати клієнтам у чаті: роль chat готова і, для власного ключа, задано обидва місячні ліміти. Ліміти платформи
    /// діють завжди (квота за замовчуванням), тож для platform ця умова виконана.
    /// </summary>
    public async Task<ChatAssistantGate> GetChatGateAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await GetEffectiveAsync(tenantId, ct);
        var e = await EvaluateAsync(tenantId, s, AiRoles.Chat, withQuota: false, ct);
        if (!e.Status.IsReady || e.Plan is null) return new ChatAssistantGate(ChatAssistantGate.Unavailable, e.Status.Reason, null);
        if (s.KeySource == KeySourceOwn && (s.MonthlyRequestLimit is null || s.MonthlyTokenLimit is null))
            return new ChatAssistantGate(ChatAssistantGate.LimitsRequired, ChatAssistantGate.LimitsRequired, e.Plan.RequestLimit);
        return new ChatAssistantGate(ChatAssistantGate.Ready, null, e.Plan.RequestLimit);
    }

    /// <summary>План виклику або <see cref="AiUnavailableException"/> з reason (кроки 0-2 контракту; квоту перевіряє атомарне резервування).</summary>
    public async Task<AiCallPlan> PlanAsync(Guid tenantId, string role, CancellationToken ct)
    {
        var s = await GetEffectiveAsync(tenantId, ct);
        var e = await EvaluateAsync(tenantId, s, role, withQuota: false, ct);
        if (!e.Status.IsReady || e.Plan is null) throw new AiUnavailableException(e.Status.Reason ?? "unavailable");
        return e.Plan;
    }

    private sealed record Evaluation(AiRoleStatus Status, AiCallPlan? Plan);

    private async Task<Evaluation> EvaluateAsync(Guid tenantId, AiEffectiveSettings s, string role, bool withQuota, CancellationToken ct)
    {
        var resolved = ResolveRole(s, role);
        // 0. провайдер без адаптера (або порожній каталог)
        if (resolved is null || providers.Get(resolved.Provider) is not { } provider)
            return new(AiRoleStatus.No(AiUnavailableReasons.ProviderNotAvailable), null);

        AiSecret key;
        long? tokenLimit, requestLimit;
        if (s.KeySource == KeySourceOwn)
        {
            // 1. власний ключ
            var (own, reason) = await ReadOwnKeyAsync(tenantId, resolved.Provider, ct);
            if (own is null) return new(AiRoleStatus.No(reason!), null);
            key = own;
            tokenLimit = s.MonthlyTokenLimit;
            requestLimit = s.MonthlyRequestLimit;
        }
        else
        {
            // 2. ключ платформи: вимкнено, якщо enabled=false або будь-яка квота 0
            var enabled = s.PlatformEnabled ?? platform.EnabledByDefault;
            var tokenQuota = s.PlatformTokenQuota ?? platform.DefaultMonthlyTokenQuota;
            var requestQuota = s.PlatformRequestQuota ?? platform.DefaultMonthlyRequestQuota;
            if (!enabled || tokenQuota <= 0 || requestQuota <= 0)
                return new(AiRoleStatus.No(AiUnavailableReasons.PlatformDisabled), null);
            var pk = platform.GetPlatformKey(resolved.Provider);
            if (pk is null) return new(AiRoleStatus.No(AiUnavailableReasons.PlatformKeyMissing), null);
            key = new AiSecret(pk);
            tokenLimit = Min(s.MonthlyTokenLimit, tokenQuota); // для platform діє менше з лімітів
            requestLimit = Min(s.MonthlyRequestLimit, requestQuota);
        }

        if (withQuota)
        {
            // 3. вичерпано місячний ліміт (спільний для ролей)
            var totals = await usageStore.GetTotalsAsync(tenantId, PeriodOf(clock.GetUtcNow()), ct);
            if ((requestLimit is { } rl && totals.Requests >= rl) || (tokenLimit is { } tl && totals.Tokens >= tl))
                return new(AiRoleStatus.No(AiUnavailableReasons.QuotaExceeded), null);
        }

        var modelCap = resolved.MaxOutputTokens;
        // max_output_tokens tenant діє для chat; для analytics окремий cap визначить TASK-721
        var tenantCap = role == AiRoles.Chat ? s.MaxOutputTokens : int.MaxValue;
        var maxTokens = Math.Max(1, Math.Min(Math.Min(tenantCap, modelCap), platform.MaxOutputTokensHardCap));
        var plan = new AiCallPlan(role, provider, resolved.Provider, resolved.Model, maxTokens, key, s.KeySource, requestLimit, tokenLimit);
        return new(AiRoleStatus.Ok(), plan);
    }

    /// <summary>Порядок контракту: keyring -> наявність ключа -> розшифрування (AAD tenant/provider).</summary>
    private async Task<(AiSecret? Key, string? Reason)> ReadOwnKeyAsync(Guid tenantId, string provider, CancellationToken ct)
    {
        if (protector.State != AiSecretsState.Ready) return (null, AiUnavailableReasons.SecretsUnavailable);
        var row = await keyStore.GetAsync(tenantId, provider, ct);
        if (row is null) return (null, AiUnavailableReasons.OwnKeyMissing);
        try
        {
            return (new AiSecret(protector.Unprotect(row.KeyEncrypted, tenantId, provider)), null);
        }
        catch (AiSecretException ex) when (ex.Error is AiSecretError.UnknownKeyId or AiSecretError.SecretsUnavailable)
        {
            return (null, AiUnavailableReasons.SecretsUnavailable);
        }
        catch (AiSecretException)
        {
            return (null, AiUnavailableReasons.OwnKeyUnreadable); // AAD/пошкодження: виклику провайдера не буде
        }
    }

    private static long? Min(long? a, long b) => a is { } x ? Math.Min(x, b) : b;
}
