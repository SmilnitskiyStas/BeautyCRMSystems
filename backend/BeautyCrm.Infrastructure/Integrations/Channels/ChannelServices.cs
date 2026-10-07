namespace BeautyCrm.Infrastructure.Integrations.Channels;

public sealed class ChannelRegistry(IEnumerable<IChannelAdapter> adapters)
{
    private readonly Dictionary<string, IChannelAdapter> _map =
        adapters.ToDictionary(a => a.Channel, StringComparer.OrdinalIgnoreCase);
    public IChannelAdapter? Find(string channel) => _map.GetValueOrDefault(channel);
}

public enum WebhookOutcome { Accepted, InvalidSignature, UnknownChannel, BadPayload }
public sealed record WebhookResult(WebhookOutcome Outcome, int NewMessages = 0);

/// <summary>Controller-facing: verify signature (always) -> normalize -> persist -> enqueue.</summary>
public sealed class InboundWebhookService(ChannelRegistry registry, IChannelMessageRepository repo, IChannelQueue queue)
{
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

        var fresh = parsed.Count == 0 ? parsed : await repo.SaveInboundAsync(parsed, ct);
        if (fresh.Count > 0) await queue.EnqueueInboundAsync(fresh, ct);
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
            await adapter.SendAsync(item.Message, ct);
            await repo.MarkSentAsync(messageId, ct);
            return OutboxResult.Sent;
        }
        catch (Exception ex) when (ex is TransientChannelException or PermanentChannelException)
        {
            var final = ex is PermanentChannelException || attempt >= MaxAttempts;
            await repo.MarkAttemptFailedAsync(messageId, attempt, ex.Message, final, ct);
            if (final) return OutboxResult.Failed;
            await queue.ScheduleOutboxRetryAsync(messageId, Backoff(attempt), ct);
            return OutboxResult.RetryScheduled;
        }
    }
}
