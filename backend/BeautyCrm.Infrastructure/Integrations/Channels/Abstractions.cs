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

    /// <summary>TASK-768 (§25.4): результат перевірки контакту (<see cref="ContactVerdicts"/>); лише для kind=contact.</summary>
    public string? ContactVerdict { get; init; }

    /// <summary>
    /// TASK-768 (§25.4): нормалізований E.164 власного контакту (verdict=own). Тимчасово: пишеться в beauty_messages.contact_phone,
    /// обнуляється конвеєром, sweeper - за 24 год. НІКОЛИ не потрапляє в Text, логи, винятки чи ToString.
    /// </summary>
    public string? ContactPhone { get; init; }

    /// <summary>Idempotency key for queue/DB (dedup of webhook redeliveries).</summary>
    public string IdempotencyKey => $"{Channel}:{ExternalMessageId}";

    // Автогенерований ToString друкував би Text/SenderName/ContactPhone; лише службові поля без ПД.
    private bool PrintMembers(StringBuilder sb)
    {
        sb.Append("Channel = ").Append(Channel).Append(", Kind = ").Append(Kind);
        return true;
    }
}

/// <summary>beauty_messages.kind (CHECK: text|command|unsupported|callback|contact). Тип медіа видно з тіла-плейсхолдера: [sticker], [voice], [photo]...</summary>
public static class InboundKinds
{
    public const string Text = "text";
    public const string Command = "command";
    public const string Unsupported = "unsupported";
    /// <summary>TASK-750 (§23.4): натискання інлайн-кнопки.</summary>
    public const string Callback = "callback";
    /// <summary>TASK-768 (§25.4): картка контакту (request_contact). Тіло завжди <see cref="ContactBody"/>.</summary>
    public const string Contact = "contact";
    public const string ContactBody = "[контакт]";
    /// <summary>Тіло повідомлення, яке клієнт написав номером (підставляє конвеєр, TASK-770).</summary>
    public const string PhoneTextBody = "[номер телефону]";
}

/// <summary>beauty_messages.contact_verdict (CHECK own|foreign|invalid), §25.4.</summary>
public static class ContactVerdicts
{
    public const string Own = "own";
    public const string Foreign = "foreign";
    public const string Invalid = "invalid";
}

/// <summary>Нейтральна розкладка клавіатури вихідного повідомлення (§23.4): рядки кнопок. Зберігається в beauty_messages.actions як {"rows":[[{"label","data"}]]}.</summary>
public sealed record ChatActionLayout(IReadOnlyList<IReadOnlyList<ChatActionButton>> Rows)
{
    public const int MaxCallbackDataBytes = 64;

    /// <summary>TASK-768 (§25.4): reply-клавіатура (взаємовиключно з Rows/RemoveReply). Telegram: keyboard + request_contact.</summary>
    public ChatReplyKeyboard? Reply { get; init; }

    /// <summary>TASK-768 (§25.4): прибрати reply-клавіатуру (Telegram remove_keyboard). Взаємовиключно з Rows/Reply.</summary>
    public bool RemoveReply { get; init; }

    /// <summary>Розбір із beauty_messages.actions (camelCase або PascalCase); невалідне/порожнє -> null (повідомлення піде без кнопок).</summary>
    public static ChatActionLayout? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            System.Text.Json.JsonElement rowsEl = default, replyEl = default, removeEl = default;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, "rows", StringComparison.OrdinalIgnoreCase)) rowsEl = p.Value;
                else if (string.Equals(p.Name, "reply", StringComparison.OrdinalIgnoreCase)) replyEl = p.Value;
                else if (string.Equals(p.Name, "removeReply", StringComparison.OrdinalIgnoreCase)) removeEl = p.Value;
            }
            // TASK-768 (§25.4): reply-keyboard / removeReply. Поєднання з rows дає розкладку, яку IsValid відхилить (лише текст).
            var hasReply = replyEl.ValueKind != System.Text.Json.JsonValueKind.Undefined;
            var remove = removeEl.ValueKind == System.Text.Json.JsonValueKind.True;
            if (hasReply || remove || removeEl.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            {
                ChatReplyKeyboard? reply = null;
                if (hasReply && (reply = ChatReplyKeyboard.TryParse(replyEl)) is null) return null;
                if (!hasReply && !remove) return null; // removeReply != true без reply
                IReadOnlyList<IReadOnlyList<ChatActionButton>> inline = [];
                if (rowsEl.ValueKind == System.Text.Json.JsonValueKind.Array && ParseRows(rowsEl) is { Count: > 0 } r0) inline = r0;
                return new ChatActionLayout(inline) { Reply = reply, RemoveReply = remove };
            }
            if (rowsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            return ParseRows(rowsEl) is { Count: > 0 } parsed ? new ChatActionLayout(parsed) : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static List<IReadOnlyList<ChatActionButton>>? ParseRows(System.Text.Json.JsonElement rowsEl)
    {
        {
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
            return rows;
        }
    }

    /// <summary>Розкладка reply-keyboard з кнопкою «Поділитися контактом» (§25.4): для <c>beauty_messages.actions</c> і <see cref="OutboundMessage.Actions"/>.</summary>
    public static ChatActionLayout ForReply(ChatReplyKeyboard reply) => new([]) { Reply = reply };

    /// <summary>Прибрати reply-keyboard (<c>remove_keyboard</c>), §25.4.</summary>
    public static ChatActionLayout ForRemoveReply() => new([]) { RemoveReply = true };

    /// <summary><c>{"removeReply":true}</c> для <c>beauty_messages.actions</c>.</summary>
    public const string RemoveReplyJson = "{\"removeReply\":true}";

    /// <summary>Захисна перевірка перед відправкою: непорожні підписи, callback_data 1..64 байти UTF-8, розумні межі. Невалідна розкладка не надсилається.</summary>
    public bool IsValid()
    {
        // §25.4: рівно одне з Rows / Reply / RemoveReply.
        var modes = (Rows.Count > 0 ? 1 : 0) + (Reply is not null ? 1 : 0) + (RemoveReply ? 1 : 0);
        if (modes != 1) return false;
        if (Reply is not null) return Reply.IsValid();
        if (RemoveReply) return true;
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

/// <summary>Кнопка reply-клавіатури (§25.4). Натискання = звичайне message (текст кнопки або contact), не callback_query.</summary>
public sealed record ChatReplyButton(string Label, bool RequestContact = false);

/// <summary>
/// Reply-клавіатура (§25.4): ≤ 2 рядки, ≤ 4 кнопки, підпис ≤ 32 кодові точки, Placeholder ≤ 64. Кнопка request_contact -
/// єдиний спосіб отримати contact від Telegram. Рядків у beauty_chat_keyboards немає.
/// </summary>
public sealed record ChatReplyKeyboard(
    IReadOnlyList<IReadOnlyList<ChatReplyButton>> Rows, bool OneTime = true, bool Resize = true, string? Placeholder = null)
{
    public const int MaxRows = 2, MaxButtons = 4, MaxLabel = 32, MaxPlaceholder = 64;
    public const string ShareContactLabel = "📱 Поділитися контактом";
    public const string DeclineLabel = "Не треба";
    public const string DefaultPlaceholder = "+380XXXXXXXXX";

    /// <summary>Типова клавіатура питання про телефон: [📱 Поділитися контактом] / [Не треба], one_time + resize (§25.6).</summary>
    public static ChatReplyKeyboard ContactRequest(bool withDecline = true) => new(
        withDecline
            ? [[new ChatReplyButton(ShareContactLabel, true)], [new ChatReplyButton(DeclineLabel)]]
            : [[new ChatReplyButton(ShareContactLabel, true)]],
        OneTime: true, Resize: true, Placeholder: DefaultPlaceholder);

    private static int Cp(string s) => s.EnumerateRunes().Count();

    public bool IsValid()
    {
        if (Rows.Count is 0 or > MaxRows) return false;
        var total = 0;
        foreach (var row in Rows)
        {
            if (row.Count == 0) return false;
            foreach (var b in row)
            {
                total++;
                if (string.IsNullOrWhiteSpace(b.Label) || Cp(b.Label) > MaxLabel || b.Label.Any(char.IsControl)) return false;
            }
        }
        if (total is 0 or > MaxButtons) return false;
        return Placeholder is null || (Cp(Placeholder) <= MaxPlaceholder && !Placeholder.Any(char.IsControl));
    }

    /// <summary><c>{"reply":{"rows":[[{"label","requestContact"}]],"oneTime","resize","placeholder"}}</c> для <c>beauty_messages.actions</c>.</summary>
    public string ToActionsJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        reply = new
        {
            rows = Rows.Select(r => r.Select(b => b.RequestContact ? (object)new { label = b.Label, requestContact = true } : new { label = b.Label })),
            oneTime = OneTime, resize = Resize, placeholder = Placeholder,
        },
    }, new System.Text.Json.JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    internal static ChatReplyKeyboard? TryParse(System.Text.Json.JsonElement el)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        System.Text.Json.JsonElement rowsEl = default;
        bool oneTime = false, resize = false;
        string? placeholder = null;
        foreach (var p in el.EnumerateObject())
        {
            if (string.Equals(p.Name, "rows", StringComparison.OrdinalIgnoreCase)) rowsEl = p.Value;
            else if (string.Equals(p.Name, "oneTime", StringComparison.OrdinalIgnoreCase)) oneTime = p.Value.ValueKind == System.Text.Json.JsonValueKind.True;
            else if (string.Equals(p.Name, "resize", StringComparison.OrdinalIgnoreCase)) resize = p.Value.ValueKind == System.Text.Json.JsonValueKind.True;
            else if (string.Equals(p.Name, "placeholder", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                placeholder = p.Value.GetString();
        }
        if (rowsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var rows = new List<IReadOnlyList<ChatReplyButton>>();
        foreach (var r in rowsEl.EnumerateArray())
        {
            if (r.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var row = new List<ChatReplyButton>();
            foreach (var b in r.EnumerateArray())
            {
                if (b.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                string? label = null;
                var request = false;
                foreach (var p in b.EnumerateObject())
                {
                    if (string.Equals(p.Name, "label", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String) label = p.Value.GetString();
                    else if (string.Equals(p.Name, "requestContact", StringComparison.OrdinalIgnoreCase)) request = p.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                if (label is null) return null;
                row.Add(new ChatReplyButton(label, request));
            }
            if (row.Count > 0) rows.Add(row);
        }
        return rows.Count == 0 ? null : new ChatReplyKeyboard(rows, oneTime, resize, placeholder);
    }
}

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
