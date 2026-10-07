using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

internal static class HttpSend
{
    public static async Task PostAsync(HttpClient http, HttpRequestMessage req, string what, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new TransientChannelException($"{what} network error"); }
        using (res)
        {
            if (res.IsSuccessStatusCode) return;
            var code = (int)res.StatusCode;
            var msg = $"{what} failed with HTTP {code}"; // never include URL/token
            if (res.StatusCode == HttpStatusCode.TooManyRequests || code >= 500)
                throw new TransientChannelException(msg);
            throw new PermanentChannelException(msg);
        }
    }
}

/// <summary>Telegram Bot API. Webhook authenticity: X-Telegram-Bot-Api-Secret-Token set via setWebhook.</summary>
public sealed class TelegramAdapter(HttpClient http, IChannelCredentialsProvider creds) : IChannelAdapter
{
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    public string Channel => ChannelIds.Telegram;

    public bool VerifySignature(WebhookRequest req, string body)
    {
        var secret = creds.Get(Channel)?.WebhookSecret;
        var got = req.Header(SecretHeader);
        return !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(got) && Crypto.FixedTimeEquals(secret, got);
    }

    public Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body)
    {
        var result = new List<InboundMessage>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("message", out var m) && m.TryGetProperty("text", out var text)
            && m.TryGetProperty("chat", out var chat) && m.TryGetProperty("from", out var from))
        {
            var chatId = chat.GetProperty("id").GetRawText();
            var name = string.Join(' ', new[] { Str(from, "first_name"), Str(from, "last_name") }
                .Where(s => !string.IsNullOrEmpty(s)));
            if (string.IsNullOrEmpty(name)) name = Str(from, "username") ?? "";
            var ts = m.TryGetProperty("date", out var d)
                ? DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()) : DateTimeOffset.UtcNow;
            result.Add(new InboundMessage(Channel, $"{chatId}:{m.GetProperty("message_id").GetRawText()}", chatId,
                from.GetProperty("id").GetRawText(), string.IsNullOrEmpty(name) ? null : name,
                text.GetString() ?? "", ts));
        }
        return Task.FromResult<IReadOnlyList<InboundMessage>>(result);
    }

    public async Task SendAsync(OutboundMessage msg, CancellationToken ct)
    {
        var token = creds.Get(Channel)?.Token ?? throw new PermanentChannelException("Telegram token is not configured");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{token}/sendMessage")
        {
            Content = JsonContent.Create(new { chat_id = msg.ExternalConversationId, text = msg.Text })
        };
        await HttpSend.PostAsync(http, req, "Telegram sendMessage", ct);
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetString() : null;
}
