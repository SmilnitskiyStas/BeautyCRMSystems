using System.Security.Cryptography;
using System.Text;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.AI.Secrets;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Tests.AI;

// Фейки та допоміжні типи для тестів ШІ-шару (TASK-715). Реальних викликів провайдера немає.

internal sealed class MemSettingsStore : IAiTenantSettingsStore
{
    public Dictionary<Guid, AiSettingsRow> Rows { get; } = new();
    public Task<AiSettingsRow?> GetAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(tenantId));
}

internal sealed class MemKeyStore : IAiProviderKeyStore
{
    public Dictionary<(Guid, string), AiProviderKey> Rows { get; } = new();
    public Task<AiProviderKey?> GetAsync(Guid tenantId, string provider, CancellationToken ct) =>
        Task.FromResult(Rows.GetValueOrDefault((tenantId, provider)));
}

/// <summary>Семантика як у EfAiUsageStore: атомарне резервування під lock.</summary>
internal sealed class MemUsageStore : IAiUsageStore
{
    private readonly object _gate = new();
    public Dictionary<(Guid Tenant, DateOnly Period, AiUsageKey Key), AiUsageRow> Rows { get; } = new();

    public IReadOnlyList<AiUsageRow> For(Guid tenantId) { lock (_gate) return Rows.Where(r => r.Key.Tenant == tenantId).Select(r => r.Value).ToList(); }

    private AiUsageRow Row(Guid t, DateOnly p, AiUsageKey k)
    {
        if (!Rows.TryGetValue((t, p, k), out var r))
            Rows[(t, p, k)] = r = new AiUsageRow { TenantId = t, PeriodStart = p, Role = k.Role, Provider = k.Provider, Model = k.Model, KeySource = k.KeySource };
        return r;
    }

    private AiUsageTotals Totals(Guid t, DateOnly p) =>
        new(Rows.Where(r => r.Key.Tenant == t && r.Key.Period == p).Sum(r => (long)r.Value.RequestCount),
            Rows.Where(r => r.Key.Tenant == t && r.Key.Period == p).Sum(r => r.Value.InputTokens + r.Value.OutputTokens));

    public Task<AiUsageTotals> GetTotalsAsync(Guid tenantId, DateOnly period, CancellationToken ct) { lock (_gate) return Task.FromResult(Totals(tenantId, period)); }

    public Task<bool> TryReserveRequestAsync(Guid tenantId, DateOnly period, AiUsageKey key, long? requestLimit, long? tokenLimit, CancellationToken ct)
    {
        lock (_gate)
        {
            var t = Totals(tenantId, period);
            if ((requestLimit is { } rl && t.Requests >= rl) || (tokenLimit is { } tl && t.Tokens >= tl)) return Task.FromResult(false);
            Row(tenantId, period, key).RequestCount++;
            return Task.FromResult(true);
        }
    }

    public Task AddTokensAsync(Guid tenantId, DateOnly period, AiUsageKey key, long i, long o, CancellationToken ct)
    {
        lock (_gate) { var r = Row(tenantId, period, key); r.InputTokens += i; r.OutputTokens += o; }
        return Task.CompletedTask;
    }

    public Task AddErrorAsync(Guid tenantId, DateOnly period, AiUsageKey key, CancellationToken ct)
    {
        lock (_gate) Row(tenantId, period, key).ErrorCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
    public string All { get { lock (Lines) return string.Join("\n", Lines); } }
}

/// <summary>Один «деплой» в пам'яті: спільні сховища, каталог/реєстр, keyring; кожен tenant має свій TenantContext (як scope запиту).</summary>
internal sealed class AiWorld
{
    public const string AnthropicProvider = "anthropic";
    public MemSettingsStore Settings { get; } = new();
    public MemKeyStore Keys { get; } = new();
    public MemUsageStore Usage { get; } = new();
    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    public Dictionary<string, string?> Config { get; } = new();
    public MockAiProvider Anthropic { get; } = new(AnthropicProvider);
    public List<IAiProvider> ExtraProviders { get; } = new();
    public AiModelCatalog Catalog { get; set; } = AiModelCatalog.Default();
    public AiSecretKeyring Keyring { get; private set; }
    public IAiSecretProtector Protector { get; private set; }
    public AiRateLimiter Limiter { get; private set; } = null!;
    public AiPlatformOptions Platform { get; private set; } = null!;

    public static string NewKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string NewApiKey() => "sk-ant-test-" + Guid.NewGuid().ToString("N");

    public AiWorld(bool withKeyring = true)
    {
        if (withKeyring)
        {
            Config["Ai:Secrets:ActiveKeyId"] = "k1";
            Config["Ai:Secrets:Keys:k1"] = NewKeyBase64();
        }
        Keyring = new AiSecretKeyring(Build());
        Protector = new AesGcmAiSecretProtector(Keyring);
        Rebuild();
    }

    private IConfiguration Build() => new ConfigurationBuilder().AddInMemoryCollection(Config).Build();

    /// <summary>Перечитати конфіг (платформа/ліміти) після змін <see cref="Config"/>.</summary>
    public void Rebuild()
    {
        var cfg = Build();
        Platform = new AiPlatformOptions(cfg);
        Limiter = new AiRateLimiter(Platform, Clock);
    }

    public void ReplaceKeyring(IConfiguration cfg)
    {
        Keyring = new AiSecretKeyring(cfg);
        Protector = new AesGcmAiSecretProtector(Keyring);
    }

    public AiProviderRegistry Registry() => new([Anthropic, .. ExtraProviders]);

    public void EnablePlatform(string? platformKey = null, long tokens = 1_000_000, int requests = 1000)
    {
        Config["Ai:Platform:EnabledByDefault"] = "true";
        Config["Ai:Platform:DefaultMonthlyTokenQuota"] = tokens.ToString();
        Config["Ai:Platform:DefaultMonthlyRequestQuota"] = requests.ToString();
        if (platformKey is not null) Config["Ai:Platform:Keys:anthropic"] = platformKey;
        Rebuild();
    }

    public AiSettingsRow SetSettings(Guid tenantId, Action<AiSettingsRow>? tweak = null)
    {
        var row = new AiSettingsRow
        {
            Id = Guid.NewGuid(), TenantId = tenantId, KeySource = "own",
            ChatProvider = "anthropic", ChatModel = "claude-haiku-5-5", AnalyticsProvider = "anthropic", AnalyticsModel = "claude-sonnet-5-5",
            Mode = "confirm", MaxOutputTokens = 1024,
        };
        tweak?.Invoke(row);
        Settings.Rows[tenantId] = row;
        return row;
    }

    public AiProviderKey SetOwnKey(Guid tenantId, string apiKey, string provider = AnthropicProvider)
    {
        var row = new AiProviderKey
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Provider = provider,
            KeyEncrypted = Protector.Protect(apiKey, tenantId, provider), KeyKid = Protector.ActiveKeyId!, KeyLast4 = apiKey[^4..],
        };
        Keys.Rows[(tenantId, provider)] = row;
        return row;
    }

    public TenantScope Scope(Guid tenantId) => new(this, tenantId);

    public static string KeyFingerprint(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..8].ToLowerInvariant();
}

/// <summary>Аналог scope запиту: свій TenantContext + runtime + клієнт.</summary>
internal sealed class TenantScope
{
    public TenantContext Tenant { get; }
    public AiTenantRuntime Runtime { get; }
    public TenantAiClient Client { get; }
    public CapturingLogger<TenantAiClient> Log { get; } = new();

    public TenantScope(AiWorld w, Guid tenantId)
    {
        Tenant = new TenantContext(tenantId);
        Runtime = new AiTenantRuntime(w.Settings, w.Keys, w.Usage, w.Catalog, w.Registry(), w.Protector, w.Platform, Tenant, w.Clock);
        Client = new TenantAiClient(Runtime, w.Usage, w.Limiter, Tenant, w.Clock, Log);
    }
}
