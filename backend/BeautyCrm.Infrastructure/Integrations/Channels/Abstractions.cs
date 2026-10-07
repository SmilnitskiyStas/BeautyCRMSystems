using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>Channel ids as used in /api/beauty/webhooks/{channel} and beauty_channels.</summary>
public static class ChannelIds
{
    public const string Telegram = "telegram";
    public const string Instagram = "instagram";
    public const string Messenger = "messenger";
    public const string WhatsApp = "whatsapp";
    public const string Viber = "viber";
    public const string Widget = "widget";
}

/// <summary>
/// Framework-independent view of an HTTP webhook request (Infrastructure has no ASP.NET reference;
/// the controller maps HttpRequest to this).
/// </summary>
public sealed record WebhookRequest(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string>? Query = null)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed record InboundMessage(
    string Channel,
    string ExternalMessageId,
    string ExternalConversationId,
    string SenderId,
    string? SenderName,
    string Text,
    DateTimeOffset ReceivedAt)
{
    /// <summary>Idempotency key for queue/DB (dedup of webhook redeliveries).</summary>
    public string IdempotencyKey => $"{Channel}:{ExternalMessageId}";
}

/// <param name="MessageId">beauty_messages.id; also the outbox idempotency key.</param>
/// <param name="LastInboundAt">Last client message time; used for the Meta 24h window.</param>
public sealed record OutboundMessage(
    Guid MessageId,
    string Channel,
    string ExternalConversationId,
    string Text,
    DateTimeOffset? LastInboundAt = null);

public interface IChannelAdapter
{
    string Channel { get; }
    bool VerifySignature(WebhookRequest req, string body);
    Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body);
    Task SendAsync(OutboundMessage msg, CancellationToken ct);
}

/// <summary>Failure that must not be retried (4xx, closed Meta window, not implemented).</summary>
public class PermanentChannelException(string message) : Exception(message);

/// <summary>Failure that may succeed on retry (5xx, 429, network).</summary>
public class TransientChannelException(string message, Exception? inner = null) : Exception(message, inner);

internal static class Crypto
{
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string HmacSha256Hex(string secret, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}

public static class SecretMasker
{
    /// <summary>Shows only the last 4 characters; short secrets are fully masked.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        return secret.Length <= 4 ? "****" : "********" + secret[^4..];
    }
}
