using System.Globalization;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.AI.Beauty;

public sealed record AiProviderInfo(string Id, string Label);

/// <summary>Запис allowlist-у моделей (контракт §20.2). Ціни null = «не вказано» (не вигадуються).</summary>
public sealed record AiModelInfo(
    string Id, string Provider, string Tier, bool Enabled, IReadOnlyList<string> DefaultForRoles, int MaxOutputTokens,
    decimal? InputPricePerMTok = null, decimal? OutputPricePerMTok = null, string Currency = "USD", DateOnly? PriceUpdatedAt = null);

public sealed record AiResolvedModel(string Provider, string Model, string Tier, int MaxOutputTokens, bool Fallback);

/// <summary>
/// Каталог провайдерів і моделей із серверного конфігу (<c>Ai:Models</c>, інакше вбудований дефолт V1). Allowlist:
/// невідома/вимкнена модель не приймається. Доступність провайдера визначає <see cref="AiProviderRegistry"/>, а не конфіг.
/// </summary>
public sealed class AiModelCatalog
{
    public const string ErrUnknownProvider = "unknown_provider";
    public const string ErrProviderNotAvailable = "provider_not_available";
    public const string ErrUnknownModel = "unknown_model";
    public const string ErrModelProviderMismatch = "model_provider_mismatch";

    private readonly Dictionary<string, AiProviderInfo> _providers;
    private readonly Dictionary<string, AiModelInfo> _models;

    public AiModelCatalog(IEnumerable<AiProviderInfo> providers, IEnumerable<AiModelInfo> models)
    {
        _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _models = models.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Каталог V1. Ціни не вказані (null): їх вносить продукт/backend з офіційної сторінки цін провайдера на дату оновлення.
    /// maxOutputTokens=4096 — консервативний серверний ліміт (не заявлена межа моделі). Рівень claude-fable-5-1 визначить продукт.
    /// OpenAI: лише запис провайдера без моделей (адаптера немає).
    /// </summary>
    public static AiModelCatalog Default() => new(
        [new AiProviderInfo("anthropic", "Anthropic"), new AiProviderInfo("openai", "OpenAI")],
        [
            new AiModelInfo("claude-haiku-5-5", "anthropic", "economy", true, [AiRoles.Chat], 4096),
            new AiModelInfo("claude-sonnet-5-5", "anthropic", "balanced", true, [AiRoles.Analytics], 4096),
            new AiModelInfo("claude-opus-5-5", "anthropic", "smartest", true, [], 4096),
            new AiModelInfo("claude-fable-5-1", "anthropic", "balanced", false, [], 4096),
        ]);

    /// <summary>
    /// <c>Ai:Models:Providers:{n}:Id|Label</c> і <c>Ai:Models:Models:{n}:Id|Provider|Tier|Enabled|DefaultForRoles|MaxOutputTokens|
    /// InputPricePerMTok|OutputPricePerMTok|Currency|PriceUpdatedAt</c>. Немає секції — вбудований дефолт.
    /// </summary>
    public static AiModelCatalog FromConfiguration(IConfiguration cfg)
    {
        var section = cfg.GetSection("Ai:Models");
        var providers = section.GetSection("Providers").GetChildren().Select(c => new AiProviderInfo(c["Id"] ?? "", c["Label"] ?? c["Id"] ?? "")).ToList();
        var models = section.GetSection("Models").GetChildren().Select(c => new AiModelInfo(
            c["Id"] ?? "", c["Provider"] ?? "", c["Tier"] ?? "economy", bool.TryParse(c["Enabled"], out var en) && en,
            (c["DefaultForRoles"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(AiRoles.IsValid).ToList(),
            int.TryParse(c["MaxOutputTokens"], out var mt) && mt > 0 ? mt : 4096,
            Dec(c["InputPricePerMTok"]), Dec(c["OutputPricePerMTok"]), c["Currency"] ?? "USD",
            DateOnly.TryParse(c["PriceUpdatedAt"], CultureInfo.InvariantCulture, out var d) ? d : null)).ToList();
        if (providers.Count == 0 && models.Count == 0) return Default();
        providers = providers.Where(p => p.Id.Length > 0).ToList();
        models = models.Where(m => m.Id.Length > 0 && m.Provider.Length > 0).ToList();
        return new AiModelCatalog(providers, models);
    }

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    public IReadOnlyCollection<AiProviderInfo> Providers => _providers.Values;
    public IReadOnlyCollection<AiModelInfo> Models => _models.Values;
    public AiProviderInfo? FindProvider(string? id) => id is not null && _providers.TryGetValue(id, out var p) ? p : null;
    public AiModelInfo? FindModel(string? id) => id is not null && _models.TryGetValue(id, out var m) ? m : null;

    /// <summary>Модель-дефолт ролі: перша enabled з <c>defaultForRoles ∋ role</c> серед доступних провайдерів.</summary>
    public AiModelInfo? DefaultFor(string role, AiProviderRegistry registry) =>
        _models.Values.FirstOrDefault(m => m.Enabled && m.DefaultForRoles.Contains(role) && registry.IsAvailable(m.Provider));

    /// <summary>
    /// Перевірка вибору ролі в порядку контракту §20.5: unknown_provider -> provider_not_available -> unknown_model ->
    /// model_provider_mismatch. Null = дозволено.
    /// </summary>
    public string? Validate(string? provider, string? model, AiProviderRegistry registry)
    {
        if (FindProvider(provider) is null) return ErrUnknownProvider;
        if (!registry.IsAvailable(provider)) return ErrProviderNotAvailable;
        var m = FindModel(model);
        if (m is null || !m.Enabled) return ErrUnknownModel;
        return m.Provider == provider ? null : ErrModelProviderMismatch;
    }

    /// <summary>
    /// Ефективна модель ролі: збережена, якщо ще валідна; інакше дефолт ролі з каталогу (<c>Fallback=true</c>).
    /// Null, якщо в каталозі немає жодного доступного дефолту.
    /// </summary>
    public AiResolvedModel? Resolve(string role, string? storedProvider, string? storedModel, AiProviderRegistry registry)
    {
        if (Validate(storedProvider, storedModel, registry) is null)
        {
            var m = _models[storedModel!];
            return new AiResolvedModel(m.Provider, m.Id, m.Tier, m.MaxOutputTokens, false);
        }
        var d = DefaultFor(role, registry);
        return d is null ? null : new AiResolvedModel(d.Provider, d.Id, d.Tier, d.MaxOutputTokens, true);
    }
}

/// <summary>
/// Серверні налаштування ШІ з конфігу (контракт §20.2). Ключі платформи читаються з конфігу при кожному зверненні
/// (<c>Ai:Platform:Keys:{provider}</c>; для anthropic fallback <c>ANTHROPIC_API_KEY</c>) і ніколи не віддаються назовні.
/// </summary>
public sealed class AiPlatformOptions(IConfiguration cfg)
{
    public bool EnabledByDefault => Bool("Ai:Platform:EnabledByDefault", false);
    public long DefaultMonthlyTokenQuota => Long("Ai:Platform:DefaultMonthlyTokenQuota", 0);
    public int DefaultMonthlyRequestQuota => (int)Math.Min(int.MaxValue, Long("Ai:Platform:DefaultMonthlyRequestQuota", 0));
    public int MaxOutputTokensHardCap => (int)Math.Clamp(Long("Ai:MaxOutputTokensHardCap", 4096), 1, 100_000);
    public int RatePerTenantPerMinute => (int)Math.Max(1, Long("Ai:RateLimit:PerTenantPerMinute", 30));
    public bool UseMockProvider => Bool("Ai:UseMockProvider", false);

    /// <summary>
    /// Частка хвилинного ліміту tenant для чату клієнта (`ChatAssistant:TenantMinuteShare`, 0.05..1, 0.5): решта лишається
    /// менеджерським функціям (M2). Чат також рахується в загальному ліміті.
    /// </summary>
    public double ChatRateShare =>
        double.TryParse(cfg["ChatAssistant:TenantMinuteShare"], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0.05, 1.0) : 0.5;

    public string? GetPlatformKey(string provider)
    {
        var key = cfg[$"Ai:Platform:Keys:{provider}"];
        if (string.IsNullOrWhiteSpace(key) && provider == AnthropicAiProvider.ProviderId) key = cfg["ANTHROPIC_API_KEY"];
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private bool Bool(string key, bool def) => bool.TryParse(cfg[key], out var v) ? v : def;
    private long Long(string key, long def) => long.TryParse(cfg[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
}
