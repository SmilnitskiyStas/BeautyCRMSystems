using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>Частотний ліміт викликів на tenant (ковзне вікно 1 хв, in-memory; на кілька інстансів — окрема задача).</summary>
public sealed class AiRateLimiter(AiPlatformOptions options, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _hits = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _chatHits = new();

    private static Queue<DateTimeOffset> Fresh(Dictionary<Guid, Queue<DateTimeOffset>> map, Guid tenantId, DateTimeOffset now)
    {
        if (!map.TryGetValue(tenantId, out var q)) map[tenantId] = q = new Queue<DateTimeOffset>();
        while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
        return q;
    }

    /// <param name="clientChat">
    /// Виклик чату клієнта: додатково діє окремий бюджет - частка (`ChatRateShare`) хвилинного ліміту tenant, щоб чат не витісняв
    /// менеджерські функції (M2). Виклик чату рахується і в загальному ліміті.
    /// </param>
    public bool TryAcquire(Guid tenantId, bool clientChat = false)
    {
        var now = clock.GetUtcNow();
        var limit = options.RatePerTenantPerMinute;
        lock (_gate)
        {
            var q = Fresh(_hits, tenantId, now);
            if (q.Count >= limit) return false;
            if (clientChat)
            {
                var chatQueue = Fresh(_chatHits, tenantId, now);
                if (chatQueue.Count >= Math.Max(1, (int)Math.Floor(limit * options.ChatRateShare))) return false;
                chatQueue.Enqueue(now);
            }
            q.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// <see cref="IAiClient"/> для продакшн-шляху (ADR-010 §2.4). Порядок: tenant із scope -> стан ролі (провайдер, ключ own/platform)
/// -> частотний ліміт -> атомарне резервування запиту проти місячних лімітів -> виклик провайдера -> облік токенів/помилок.
/// У логах і обліку немає вмісту повідомлень, промпту, ключів чи їх масок.
/// </summary>
public sealed class TenantAiClient(
    AiTenantRuntime runtime, IAiUsageStore usage, AiRateLimiter limiter, TenantContext tenant, TimeProvider clock,
    ILogger<TenantAiClient> logger) : IAiClient
{
    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        if (!AiRoles.IsValid(request.Role)) throw new ArgumentException("Unknown AI role.", nameof(request));
        // tenant лише зі scope (JWT або канал webhook), не з запиту й не з моделі
        var tenantId = tenant.TenantId ?? throw new AiUnavailableException("tenant_missing");

        var plan = await runtime.PlanAsync(tenantId, request.Role, ct);
        if (!limiter.TryAcquire(tenantId, request.ClientChat)) throw new AiRateLimitedException();

        var now = clock.GetUtcNow();
        var period = AiTenantRuntime.PeriodOf(now);
        var key = new AiUsageKey(request.Role, plan.ProviderId, plan.Model, plan.KeySource);
        if (!await usage.TryReserveRequestAsync(tenantId, period, key, plan.RequestLimit, plan.TokenLimit, ct))
        {
            logger.LogInformation("ai_call {TenantId} {Role} {Provider} {Model} {KeySource} result=quota_exceeded",
                tenantId, request.Role, plan.ProviderId, plan.Model, plan.KeySource);
            throw new AiQuotaExceededException(AiTenantRuntime.NextPeriodStart(now));
        }

        AiProviderResult result;
        try
        {
            result = await plan.Provider.CompleteAsync(new AiProviderCall(request, plan.Model, plan.MaxOutputTokens, plan.Key), ct);
        }
        catch (OperationCanceledException)
        {
            // L5: запит провайдеру міг уже піти й спожити токени. Помилку й оцінку вхідних токенів пишемо без токена скасування,
            // інакше скасування клієнтом дозволяло б оминати квоту. Вихідні токени невідомі (0).
            await RecordAbortedCallAsync(tenantId, period, key, request);
            throw;
        }
        catch (Exception ex) when (ex is not TenantMismatchException)
        {
            await TryAsync(() => usage.AddErrorAsync(tenantId, period, key, CancellationToken.None));
            var status = (ex as AiProviderException)?.HttpStatus;
            logger.LogWarning("ai_call {TenantId} {Role} {Provider} {Model} {KeySource} result=provider_error type={ErrorType} status={Status}",
                tenantId, request.Role, plan.ProviderId, plan.Model, plan.KeySource, (ex as AiProviderException)?.Error.ToString() ?? "unexpected", status);
            // Чужі винятки (їхній текст міг би містити ключ) не пропускаємо: лише санітизований AiProviderException без InnerException.
            if (ex is AiProviderException) throw;
            throw new AiProviderException(AiProviderError.Unavailable);
        }

        await usage.AddTokensAsync(tenantId, period, key, result.Usage.InputTokens, result.Usage.OutputTokens, CancellationToken.None);
        logger.LogInformation("ai_call {TenantId} {Role} {Provider} {Model} {KeySource} result=ok in={In} out={Out}",
            tenantId, request.Role, plan.ProviderId, plan.Model, plan.KeySource, result.Usage.InputTokens, result.Usage.OutputTokens);
        return result.Response with { Usage = result.Usage };
    }

    private async Task RecordAbortedCallAsync(Guid tenantId, DateOnly period, AiUsageKey key, AiRequest request)
    {
        await TryAsync(() => usage.AddErrorAsync(tenantId, period, key, CancellationToken.None));
        var estimate = EstimateInputTokens(request);
        if (estimate > 0) await TryAsync(() => usage.AddTokensAsync(tenantId, period, key, estimate, 0, CancellationToken.None));
        logger.LogWarning("ai_call {TenantId} {Role} {Provider} {Model} {KeySource} result=cancelled estIn={EstIn}",
            tenantId, request.Role, key.Provider, key.Model, key.KeySource, estimate);
    }

    /// <summary>Груба оцінка (символи / 4) system, повідомлень і визначень tools; не використовується для чого-небудь, крім квоти.</summary>
    internal static long EstimateInputTokens(AiRequest r)
    {
        long chars = r.System.Length;
        foreach (var m in r.Messages)
            foreach (var b in m.Blocks)
                chars += b switch
                {
                    AiTextBlock t => t.Text.Length,
                    AiToolResultBlock tr => tr.Content.Length,
                    AiToolUseBlock u => u.Input.GetRawText().Length,
                    _ => 0,
                };
        foreach (var t in r.Tools) chars += t.Name.Length + t.Description.Length + t.InputSchemaJson.Length;
        return (chars + 3) / 4;
    }

    /// <summary>Облік не повинен підміняти початкову помилку/скасування власною.</summary>
    private async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { logger.LogError("ai_usage_write_failed type={Type}", ex.GetType().Name); }
    }
}
