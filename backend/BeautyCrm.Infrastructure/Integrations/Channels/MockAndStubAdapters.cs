using System.Collections.Concurrent;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>Dev/test adapter (Development only). Header "X-Mock-Signature" must equal the channel's configured webhook secret; no secret = reject (no built-in default).</summary>
public class MockChannelAdapter(string channel, IChannelCredentialsProvider? creds = null) : IChannelAdapter
{
    public const string SignatureHeader = "X-Mock-Signature";
    private readonly ConcurrentQueue<OutboundMessage> _sent = new();
    public IReadOnlyCollection<OutboundMessage> Sent => _sent.ToArray();
    /// <summary>Test hook: number of upcoming SendAsync calls that throw TransientChannelException.</summary>
    public int FailNextSends { get; set; }

    public string Channel { get; } = channel;

    public bool VerifySignature(WebhookRequest req, string body)
    {
        var secret = creds?.Get(Channel)?.WebhookSecret;
        var got = req.Header(SignatureHeader);
        return !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(got) && Crypto.FixedTimeEquals(secret, got);
    }

    /// <summary>Body: {"id","chatId","from","name","text"}.</summary>
    public Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var r = doc.RootElement;
        string S(string p) => r.TryGetProperty(p, out var v) ? v.GetString() ?? "" : "";
        IReadOnlyList<InboundMessage> list =
        [
            new InboundMessage(Channel, S("id"), S("chatId"), S("from"),
                string.IsNullOrEmpty(S("name")) ? null : S("name"), S("text"), DateTimeOffset.UtcNow)
        ];
        return Task.FromResult(list);
    }

    public Task SendAsync(OutboundMessage msg, CancellationToken ct)
    {
        if (FailNextSends > 0) { FailNextSends--; throw new TransientChannelException("mock transient failure"); }
        _sent.Enqueue(msg);
        return Task.CompletedTask;
    }
}

public sealed class MockTelegramAdapter(IChannelCredentialsProvider? c = null) : MockChannelAdapter(ChannelIds.Telegram, c);
public sealed class MockInstagramAdapter(IChannelCredentialsProvider? c = null) : MockChannelAdapter(ChannelIds.Instagram, c);

/// <summary>Placeholder for channels not implemented in release 1 (Viber, WhatsApp, Messenger, widget).</summary>
public class NotImplementedChannelAdapter(string channel) : IChannelAdapter
{
    public string Channel { get; } = channel;
    public bool VerifySignature(WebhookRequest req, string body) => false; // fail closed
    public Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body) =>
        throw new NotSupportedException($"Channel '{Channel}' is not implemented yet");
    public Task SendAsync(OutboundMessage msg, CancellationToken ct) =>
        throw new PermanentChannelException($"Channel '{Channel}' is not implemented yet");
}

public sealed class ViberAdapter() : NotImplementedChannelAdapter(ChannelIds.Viber);
public sealed class WhatsAppAdapter() : NotImplementedChannelAdapter(ChannelIds.WhatsApp);
public sealed class MessengerAdapter() : NotImplementedChannelAdapter(ChannelIds.Messenger);
public sealed class WidgetAdapter() : NotImplementedChannelAdapter(ChannelIds.Widget);
