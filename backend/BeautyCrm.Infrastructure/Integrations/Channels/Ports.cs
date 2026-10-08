namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>Secrets come from integration_configs / .env only. Never log or return Token raw; use MaskedToken.</summary>
/// <param name="AppSecret">Instagram/Meta: ключ HMAC підпису тіла (X-Hub-Signature-256). Лише для підпису.</param>
/// <param name="VerifyToken">Instagram/Meta: токен GET-handshake (hub.verify_token). Лише для handshake; не секрет підпису.</param>
public sealed record ChannelCredentials(string? Token, string? WebhookSecret, string? AppSecret = null, string? VerifyToken = null)
{
    public string MaskedToken => SecretMasker.Mask(Token);
    public override string ToString() =>
        $"ChannelCredentials(Token={MaskedToken}, WebhookSecret={SecretMasker.Mask(WebhookSecret)}, " +
        $"AppSecret={SecretMasker.Mask(AppSecret)}, VerifyToken={SecretMasker.Mask(VerifyToken)})";
}

/// <summary>Port: implemented by the Data/config layer.</summary>
public interface IChannelCredentialsProvider
{
    ChannelCredentials? Get(string channel);
}

public enum OutboxStatus { Pending, Sent, Failed }

public sealed record OutboxItem(OutboundMessage Message, OutboxStatus Status, int Attempts);

/// <summary>Port over beauty_conversations / beauty_messages (schema owned by the Data agent).</summary>
public interface IChannelMessageRepository
{
    /// <summary>Persists inbound messages, deduplicating by IdempotencyKey. Returns only newly stored ones.</summary>
    Task<IReadOnlyList<InboundMessage>> SaveInboundAsync(IReadOnlyList<InboundMessage> messages, CancellationToken ct);
    Task<OutboxItem?> GetOutboxItemAsync(Guid messageId, CancellationToken ct);
    Task MarkSentAsync(Guid messageId, CancellationToken ct);
    Task MarkAttemptFailedAsync(Guid messageId, int attempts, string error, bool final, CancellationToken ct);
}

/// <summary>Port: pushes to the queue (beauty.* jobs).</summary>
public interface IChannelQueue
{
    Task EnqueueInboundAsync(IReadOnlyList<InboundMessage> messages, CancellationToken ct);
    /// <summary>Schedules beauty.outbox.send {messageId} again after delay.</summary>
    Task ScheduleOutboxRetryAsync(Guid messageId, TimeSpan delay, CancellationToken ct);
}
