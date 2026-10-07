using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>Instagram Direct via Meta Graph API. Signature: X-Hub-Signature-256 = sha256=HMAC(appSecret, body).</summary>
public sealed class InstagramAdapter(HttpClient http, IChannelCredentialsProvider creds) : IChannelAdapter
{
    public const string SignatureHeader = "X-Hub-Signature-256";
    public const string GraphUrl = "https://graph.facebook.com/v21.0/me/messages";
    /// <summary>Meta standard messaging window.</summary>
    public static readonly TimeSpan MessagingWindow = TimeSpan.FromHours(24);

    public string Channel => ChannelIds.Instagram;

    public bool VerifySignature(WebhookRequest req, string body)
    {
        var secret = creds.Get(Channel)?.WebhookSecret;
        var header = req.Header(SignatureHeader);
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header)
            || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;
        return Crypto.FixedTimeEquals(Crypto.HmacSha256Hex(secret, body), header[7..].ToLowerInvariant());
    }

    /// <summary>GET handshake (hub.mode=subscribe, hub.verify_token, hub.challenge). Returns challenge or null.</summary>
    public string? VerifyChallenge(WebhookRequest req)
    {
        var q = req.Query;
        var secret = creds.Get(Channel)?.WebhookSecret;
        if (q is null || string.IsNullOrEmpty(secret)) return null;
        return q.GetValueOrDefault("hub.mode") == "subscribe"
            && q.TryGetValue("hub.verify_token", out var t) && Crypto.FixedTimeEquals(secret, t)
            ? q.GetValueOrDefault("hub.challenge") : null;
    }

    public Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body)
    {
        var result = new List<InboundMessage>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("entry", out var entries))
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("messaging", out var events)) continue;
                foreach (var ev in events.EnumerateArray())
                {
                    if (!ev.TryGetProperty("message", out var m)) continue;
                    if (m.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue;
                    if (!m.TryGetProperty("text", out var text) || !m.TryGetProperty("mid", out var mid)) continue;
                    var sender = ev.GetProperty("sender").GetProperty("id").GetString() ?? "";
                    var ts = ev.TryGetProperty("timestamp", out var t)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()) : DateTimeOffset.UtcNow;
                    result.Add(new InboundMessage(Channel, mid.GetString() ?? "", sender, sender, null,
                        text.GetString() ?? "", ts));
                }
            }
        return Task.FromResult<IReadOnlyList<InboundMessage>>(result);
    }

    public async Task SendAsync(OutboundMessage msg, CancellationToken ct)
    {
        if (msg.LastInboundAt is null || DateTimeOffset.UtcNow - msg.LastInboundAt > MessagingWindow)
            throw new PermanentChannelException(
                "Meta 24h messaging window is closed; message templates/human-agent tag required");
        var token = creds.Get(Channel)?.Token ?? throw new PermanentChannelException("Instagram token is not configured");
        using var req = new HttpRequestMessage(HttpMethod.Post, GraphUrl)
        {
            Content = JsonContent.Create(new
            {
                recipient = new { id = msg.ExternalConversationId },
                message = new { text = msg.Text },
                messaging_type = "RESPONSE"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await HttpSend.PostAsync(http, req, "Instagram send", ct);
    }
}
