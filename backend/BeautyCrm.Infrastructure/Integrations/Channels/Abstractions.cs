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
    DateTimeOffset ReceivedAt,
    string Kind = InboundKinds.Text)
{
    /// <summary>TASK-750 (§23.4): Telegram callback_query.id. Лише для ack (answerCallbackQuery); НЕ зберігається й не логується.</summary>
    public string? CallbackQueryId { get; init; }

    /// <summary>callback_data лише якщо UTF-8 &lt;= 64 байти й відповідає формату §23.5; інакше null (невалідний формат).</summary>
    public string? CallbackData { get; init; }

    /// <summary>Telegram message_id повідомлення з кнопками (beauty_messages.callback_message_ref).</summary>
    public string? CallbackMessageRef { get; init; }

    /// <summary>Callback, який не зберігається (група, бот, inline_message_id, chat.id != from.id, без data): лише ack.</summary>
    public bool AckOnly { get; init; }

    /// <summary>Заповнюється сховищем для щойно збережених повідомлень (не береться з payload); сигнал для черги розмов.</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>Idempotency key for queue/DB (dedup of webhook redeliveries).</summary>
    public string IdempotencyKey => $"{Channel}:{ExternalMessageId}";
}

/// <summary>beauty_messages.kind (CHECK: text|command|unsupported|callback). Тип медіа видно з тіла-плейсхолдера: [sticker], [voice], [photo]...</summary>
public static class InboundKinds
{
    public const string Text = "text";
    public const string Command = "command";
    public const string Unsupported = "unsupported";
    /// <summary>TASK-750 (§23.4): натискання інлайн-кнопки.</summary>
    public const string Callback = "callback";
}

/// <summary>Нейтральна розкладка клавіатури вихідного повідомлення (§23.4): рядки кнопок. Зберігається в beauty_messages.actions як {"rows":[[{"label","data"}]]}.</summary>
public sealed record ChatActionLayout(IReadOnlyList<IReadOnlyList<ChatActionButton>> Rows)
{
    public const int MaxCallbackDataBytes = 64;

    /// <summary>Розбір із beauty_messages.actions (camelCase або PascalCase); невалідне/порожнє -> null (повідомлення піде без кнопок).</summary>
    public static ChatActionLayout? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            System.Text.Json.JsonElement rowsEl = default;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "rows", StringComparison.OrdinalIgnoreCase)) rowsEl = p.Value;
            if (rowsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var rows = new List<IReadOnlyList<ChatActionButton>>();
            foreach (var r in rowsEl.EnumerateArray())
            {
                if (r.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                var row = new List<ChatActionButton>();
                foreach (var b in r.EnumerateArray())
                {
                    string? label = null, data = null;
                    if (b.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                    foreach (var p in b.EnumerateObject())
                    {
                        if (p.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        if (string.Equals(p.Name, "label", StringComparison.OrdinalIgnoreCase)) label = p.Value.GetString();
                        else if (string.Equals(p.Name, "data", StringComparison.OrdinalIgnoreCase)) data = p.Value.GetString();
                    }
                    if (label is null || data is null) return null;
                    row.Add(new ChatActionButton(label, data));
                }
                if (row.Count > 0) rows.Add(row);
            }
            return rows.Count == 0 ? null : new ChatActionLayout(rows);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Захисна перевірка перед відправкою: непорожні підписи, callback_data 1..64 байти UTF-8, розумні межі. Невалідна розкладка не надсилається.</summary>
    public bool IsValid()
    {
        var total = 0;
        if (Rows.Count is 0 or > 8) return false;
        foreach (var row in Rows)
        {
            if (row.Count is 0 or > 8) return false;
            foreach (var b in row)
            {
                total++;
                if (string.IsNullOrWhiteSpace(b.Label) || b.Label.Length > 64) return false;
                var bytes = System.Text.Encoding.UTF8.GetByteCount(b.Data ?? "");
                if (bytes is 0 or > MaxCallbackDataBytes) return false;
            }
        }
        return total <= 10;
    }
}

public sealed record ChatActionButton(string Label, string Data);

/// <summary>Результат відправки (§23.4): id повідомлення в каналі (Telegram result.message_id; mock-и й Instagram - null).</summary>
public sealed record ChannelSendReceipt(string? ExternalMessageId);

/// <param name="MessageId">beauty_messages.id; also the outbox idempotency key.</param>
/// <param name="LastInboundAt">Last client message time; used for the Meta 24h window.</param>
public sealed record OutboundMessage(
    Guid MessageId,
    string Channel,
    string ExternalConversationId,
    string Text,
    DateTimeOffset? LastInboundAt = null,
    ChatActionLayout? Actions = null);

public interface IChannelAdapter
{
    string Channel { get; }
    bool VerifySignature(WebhookRequest req, string body);
    Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body);
    /// <summary>TASK-750: канал підтримує інлайн-кнопки (<see cref="OutboundMessage.Actions"/>). Інші адаптери ігнорують Actions.</summary>
    bool SupportsActions => false;
    Task<ChannelSendReceipt> SendAsync(OutboundMessage msg, CancellationToken ct);
}

/// <summary>Можливість каналу (лише Telegram): answerCallbackQuery без тексту. Токен із ambient-контексту каналу; помилки - Transient/PermanentChannelException без ПД.</summary>
public interface IChannelCallbackAcknowledger
{
    Task AcknowledgeCallbackAsync(string callbackQueryId, CancellationToken ct);
}

/// <summary>Можливість каналу (лише Telegram): editMessageReplyMarkup з порожньою клавіатурою. "message is not modified" / "message to edit not found" = успіх.</summary>
public interface IChannelMessageEditor
{
    Task ClearActionsAsync(string externalConversationId, string externalMessageId, CancellationToken ct);
}

/// <summary>Можливість каналу (лише Telegram): setMyCommands (default і uk).</summary>
public interface IChannelCommandsSync
{
    Task SyncCommandsAsync(CancellationToken ct);
}

/// <summary>
/// Failure that must not be retried (4xx, closed Meta window, not implemented). <see cref="Code"/> (TASK-739, §22.5) - короткий код
/// помилки без ПД (<see cref="ChannelSendCodes"/>); якщо задано, саме він зберігається в <c>beauty_messages.last_error</c>.
/// </summary>
public class PermanentChannelException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>Коди постійних помилок надсилання (§22.5). У БД і API потрапляють лише вони, не текст відповіді провайдера.</summary>
public static class ChannelSendCodes
{
    public const string BotBlocked = "bot_blocked";
    public const string ChatNotFound = "chat_not_found";
    public const string ChannelInactive = "channel_inactive";
    public const string TokenMissing = "token_missing";
    /// <summary>Невідомий/некласифікований збій: API віддає цей код замість сирого last_error.</summary>
    public const string SendFailed = "send_failed";
    /// <summary>Лише для editMessageReplyMarkup (TASK-750): не помилка надсилання, у beauty_messages.last_error не потрапляє.</summary>
    public const string MessageNotModified = "message_not_modified";
    public const string MessageNotFound = "message_not_found";

    public static bool IsKnown(string? code) => code is BotBlocked or ChatNotFound or ChannelInactive or TokenMissing;
}

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
