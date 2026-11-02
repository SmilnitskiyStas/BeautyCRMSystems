using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

public sealed class ChannelRegistry(IEnumerable<IChannelAdapter> adapters)
{
    private readonly Dictionary<string, IChannelAdapter> _map =
        adapters.ToDictionary(a => a.Channel, StringComparer.OrdinalIgnoreCase);
    public IChannelAdapter? Find(string channel) => _map.GetValueOrDefault(channel);
}

public enum WebhookOutcome { Accepted, InvalidSignature, UnknownChannel, BadPayload }
public sealed record WebhookResult(WebhookOutcome Outcome, int NewMessages = 0);

/// <summary>
/// Спільне ядро вхідного потоку для двох транспортів (webhook і polling, ADR-011 §2.1): дедуплікація й збереження
/// (ключ {channel}:{channelId:N}:{chatId}:{messageId}) -> сигнал черги -> ack натискань кнопок (TASK-750, §23.4).
/// Потребує ambient-канал (ChannelRequestContext.Begin) і встановлений tenant; ні того, ні іншого з payload не береться.
/// </summary>
public sealed class InboundIngestCore(
    IChannelMessageRepository repo, IChannelQueue queue,
    ChannelRegistry? registry = null, ChannelRequestContext? context = null, ILogger<InboundIngestCore>? log = null)
{
    /// <summary>Тайм-аут одного answerCallbackQuery (§23.4): best-effort, без повторів.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(3);

    /// <returns>Лише щойно збережені повідомлення (дублі й ack-only відкидаються).</returns>
    public async Task<IReadOnlyList<InboundMessage>> IngestAsync(IReadOnlyList<InboundMessage> parsed, CancellationToken ct)
    {
        if (parsed.Count == 0) return parsed;
        IReadOnlyList<InboundMessage> fresh = [];
        var toStore = parsed.Where(m => !m.AckOnly).ToList();
        if (toStore.Count > 0)
        {
            fresh = await repo.SaveInboundAsync(toStore, ct);
            if (fresh.Count > 0) await queue.EnqueueInboundAsync(fresh, ct);
        }
        // Ack одразу після збереження пакета: для нових, дублів і ack-only (дубль теж мусить прибрати «годинник»).
        await AcknowledgeAsync(parsed, ct);
        return fresh;
    }

    private async Task AcknowledgeAsync(IReadOnlyList<InboundMessage> parsed, CancellationToken ct)
    {
        var ids = parsed.Where(m => !string.IsNullOrEmpty(m.CallbackQueryId)).Select(m => (m.Channel, Id: m.CallbackQueryId!)).Distinct().ToList();
        if (ids.Count == 0) return;
        await Task.WhenAll(ids.Select(x => AckOneAsync(x.Channel, x.Id, ct)));
    }

    private async Task AckOneAsync(string channel, string callbackQueryId, CancellationToken ct)
    {
        var channelId = context?.Value?.ChannelId;
        try
        {
            if (registry?.Find(channel) is not IChannelCallbackAcknowledger acker) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AckTimeout);
            try { await acker.AcknowledgeCallbackAsync(callbackQueryId, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Лише тип помилки: у повідомленні/URL може бути токен чи callback_query.id.
            log?.LogWarning("telegram_ack_failed channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
        }
    }
}

/// <summary>Controller-facing: verify signature (always) -> normalize -> InboundIngestCore (persist -> enqueue).</summary>
public sealed class InboundWebhookService(
    ChannelRegistry registry, IChannelMessageRepository repo, IChannelQueue queue,
    ChannelRequestContext? context = null, ILogger<InboundIngestCore>? log = null)
{
    private readonly InboundIngestCore _core = new(repo, queue, registry, context, log);

    public async Task<WebhookResult> HandleAsync(string channel, WebhookRequest req, string body, CancellationToken ct)
    {
        var adapter = registry.Find(channel);
        if (adapter is null) return new(WebhookOutcome.UnknownChannel);
        if (!adapter.VerifySignature(req, body)) return new(WebhookOutcome.InvalidSignature); // nothing parsed or stored

        IReadOnlyList<InboundMessage> parsed;
        try { parsed = await adapter.ParseInboundAsync(body); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException)
        { return new(WebhookOutcome.BadPayload); }

        var fresh = await _core.IngestAsync(parsed, ct);
        return new(WebhookOutcome.Accepted, fresh.Count);
    }
}

public enum OutboxResult { Sent, AlreadyDone, RetryScheduled, Failed, NotFound }

/// <summary>Handler for beauty.outbox.send {messageId}. MaxAttempts = 3 total attempts, then Failed.</summary>
public sealed class OutboxProcessor(ChannelRegistry registry, IChannelMessageRepository repo, IChannelQueue queue)
{
    public const int MaxAttempts = 3;
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(30 * Math.Pow(4, attempt - 1)); // 30s, 2m

    public async Task<OutboxResult> ProcessAsync(Guid messageId, CancellationToken ct)
    {
        var item = await repo.GetOutboxItemAsync(messageId, ct);
        if (item is null) return OutboxResult.NotFound;
        if (item.Status != OutboxStatus.Pending) return OutboxResult.AlreadyDone; // idempotent

        var attempt = item.Attempts + 1;
        try
        {
            var adapter = registry.Find(item.Message.Channel)
                ?? throw new PermanentChannelException($"No adapter for channel '{item.Message.Channel}'");
            var receipt = await adapter.SendAsync(item.Message, ct);
            await repo.MarkSentAsync(messageId, receipt?.ExternalMessageId, ct);
            return OutboxResult.Sent;
        }
        catch (Exception ex) when (ex is TransientChannelException or PermanentChannelException)
        {
            var final = ex is PermanentChannelException || attempt >= MaxAttempts;
            // Класифікований збій (§22.5) зберігається кодом (bot_blocked, chat_not_found, ...), без тексту помилки.
            var stored = (ex as PermanentChannelException)?.Code ?? ex.Message;
            await repo.MarkAttemptFailedAsync(messageId, attempt, stored, final, ct);
            if (final) return OutboxResult.Failed;
            await queue.ScheduleOutboxRetryAsync(messageId, Backoff(attempt), ct);
            return OutboxResult.RetryScheduled;
        }
    }
}
