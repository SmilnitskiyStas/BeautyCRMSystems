using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

internal static class HttpSend
{
    /// <param name="telegramCodes">Класифікація відповідей Telegram Bot API (§22.5): 403 -> bot_blocked, 400 "chat not found" -> chat_not_found.</param>
    public static Task PostAsync(HttpClient http, HttpRequestMessage req, string what, CancellationToken ct, bool telegramCodes = false) =>
        PostForBodyAsync(http, req, what, ct, telegramCodes, readBody: false);

    /// <summary>Як <see cref="PostAsync"/>, але повертає тіло успішної відповіді (TASK-750: Telegram result.message_id); null, якщо тіло не прочитано.</summary>
    public static async Task<string?> PostForBodyAsync(
        HttpClient http, HttpRequestMessage req, string what, CancellationToken ct, bool telegramCodes = false, bool readBody = true)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new TransientChannelException($"{what} network error"); } // без inner: у повідомленні може бути URL з токеном
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new TransientChannelException($"{what} timeout"); }
        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                if (!readBody) return null;
                try { return await res.Content.ReadAsStringAsync(ct); }
                catch (Exception ex) when (ex is HttpRequestException or IOException) { return null; }
            }
            var code = (int)res.StatusCode;
            var msg = $"{what} failed with HTTP {code}"; // never include URL/token
            if (res.StatusCode == HttpStatusCode.TooManyRequests || code >= 500)
                throw new TransientChannelException(msg);
            throw new PermanentChannelException(msg, telegramCodes ? await ClassifyTelegramAsync(res, ct) : null);
        }
    }

    /// <summary>Лише код; тіло відповіді (може містити фрагменти запиту) далі не передається й не логується.</summary>
    private static async Task<string?> ClassifyTelegramAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.StatusCode == HttpStatusCode.Forbidden) return ChannelSendCodes.BotBlocked;
        if (res.StatusCode != HttpStatusCode.BadRequest) return null;
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Contains("chat not found", StringComparison.OrdinalIgnoreCase)) return ChannelSendCodes.ChatNotFound;
            if (body.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)) return ChannelSendCodes.MessageNotModified;
            if (body.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)) return ChannelSendCodes.MessageNotFound;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Telegram Bot API. Webhook authenticity: X-Telegram-Bot-Api-Secret-Token set via setWebhook.</summary>
public sealed class TelegramAdapter(HttpClient http, IChannelCredentialsProvider creds)
    : IChannelAdapter, IChannelCallbackAcknowledger, IChannelMessageEditor, IChannelCommandsSync
{
    private const string ApiBase = "https://api.telegram.org";
    /// <summary>Тайм-аут setMyCommands (§23.4).</summary>
    public static readonly TimeSpan CommandsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Меню команд бота (§23.4): command -> опис (українською).</summary>
    public static readonly IReadOnlyList<(string Command, string Description)> BotCommands =
    [
        ("start", "Почати"), ("book", "Записатися"), ("my", "Мої записи"),
        ("cancel", "Скасувати запис"), ("stop", "Вимкнути нагадування"), ("help", "Допомога"),
    ];

    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    public string Channel => ChannelIds.Telegram;
    public bool SupportsActions => true;

    public bool VerifySignature(WebhookRequest req, string body)
    {
        var secret = creds.Get(Channel)?.WebhookSecret;
        var got = req.Header(SecretHeader);
        return !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(got) && Crypto.FixedTimeEquals(secret, got);
    }

    // Типи вкладень без тексту -> плейсхолдер (медіа не завантажується, телефон із contact не зберігається).
    private static readonly string[] MediaKeys =
        ["sticker", "voice", "video", "video_note", "audio", "photo", "document", "contact", "location", "animation"];

    /// <summary>
    /// Один update (webhook-тіло або елемент result у getUpdates). Лише приватні чати, не боти; edited_message та інші типи
    /// update ігноруються. Текст зберігається як є (нормалізація - на вході в ШІ). <c>callback_query</c> (TASK-750, §23.4):
    /// приватний чат -> kind=callback; інакше ack-only елемент (не зберігається, лише answerCallbackQuery).
    /// </summary>
    public Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body)
    {
        var result = new List<InboundMessage>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("callback_query", out var cq) && cq.ValueKind == JsonValueKind.Object)
        {
            if (ParseCallback(cq) is { } cb) result.Add(cb);
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
            && m.TryGetProperty("chat", out var chat) && m.TryGetProperty("from", out var from)
            && IsPrivateChat(chat) && !(from.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.True))
        {
            var (text, kind) = Classify(m);
            var chatId = chat.GetProperty("id").GetRawText();
            var ts = m.TryGetProperty("date", out var d)
                ? DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()) : DateTimeOffset.UtcNow;
            result.Add(new InboundMessage(Channel, $"{chatId}:{m.GetProperty("message_id").GetRawText()}", chatId,
                from.GetProperty("id").GetRawText(), SenderName(from), text, ts, kind));
        }
        return Task.FromResult<IReadOnlyList<InboundMessage>>(result);
    }

    private static string? SenderName(JsonElement from)
    {
        var name = string.Join(' ', new[] { Str(from, "first_name"), Str(from, "last_name") }
            .Where(s => !string.IsNullOrEmpty(s)));
        if (string.IsNullOrEmpty(name)) name = Str(from, "username") ?? "";
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static readonly Regex StateCallbackData = new(
        @"^(by|bn|bo|sp|rc|ac|aa|ar|cy|cn):[A-Za-z0-9_-]{22}(:[0-9])?\z", RegexOptions.CultureInvariant);
    private static readonly Regex StatelessCallbackData = new(@"^i:(book|my|help|human|stop)\z", RegexOptions.CultureInvariant);
    private static readonly Regex QueryIdPattern = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);

    /// <summary>Формат callback_data за §23.5: станова дія <c>{verb}:{token22}[:{index}]</c> або безстанова <c>i:{cmd}</c>; &lt;= 64 байти UTF-8.</summary>
    public static bool IsValidCallbackData(string? data) =>
        data is { Length: > 0 and <= 64 } && Encoding.UTF8.GetByteCount(data) <= ChatActionLayout.MaxCallbackDataBytes
        && (StateCallbackData.IsMatch(data) || StatelessCallbackData.IsMatch(data));

    private static bool TryLong(JsonElement e, string prop, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
    }

    /// <summary>
    /// Перевірка полів, які клієнт міг підробити: зберігаємо лише якщо є message (не inline_message_id / game), чат приватний,
    /// відправник не бот і chat.id == from.id, є data. Інакше ack-only. Без валідного callback_query.id нічого не повертаємо
    /// (acked бути не може).
    /// </summary>
    private InboundMessage? ParseCallback(JsonElement cq)
    {
        if (!cq.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
        var queryId = idEl.GetString();
        if (queryId is null || !QueryIdPattern.IsMatch(queryId)) return null;

        InboundMessage AckOnly() =>
            new(Channel, $"cb:{queryId}", "", "", null, "[кнопка]", DateTimeOffset.UtcNow, InboundKinds.Callback)
            { CallbackQueryId = queryId, AckOnly = true };

        if (cq.TryGetProperty("inline_message_id", out _) || cq.TryGetProperty("game_short_name", out _)) return AckOnly();
        if (!cq.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object
            || !cq.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object)
            return AckOnly();
        if (from.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.True) return AckOnly();
        if (!TryLong(from, "id", out var fromId)
            || !chat.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "private"
            || !TryLong(chat, "id", out var chatNum) || chatNum != fromId || chatNum <= 0
            || !TryLong(msg, "message_id", out var messageId)
            || !cq.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            return AckOnly();

        var chatId = chatNum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var raw = data.GetString();
        return new InboundMessage(Channel, $"{chatId}:cb:{queryId}", chatId, chatId, SenderName(from), "[кнопка]",
            DateTimeOffset.UtcNow, InboundKinds.Callback)
        {
            CallbackQueryId = queryId,
            CallbackData = IsValidCallbackData(raw) ? raw : null,
            CallbackMessageRef = messageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>type=private; якщо Telegram type не надіслав - лише додатний chat.id (групи й канали мають від'ємні id).</summary>
    private static bool IsPrivateChat(JsonElement chat)
    {
        if (chat.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() == "private";
        return chat.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt64() > 0;
    }

    private static (string Text, string Kind) Classify(JsonElement m)
    {
        if (m.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var text = t.GetString() ?? "";
            return (text, text.StartsWith('/') ? InboundKinds.Command : InboundKinds.Text);
        }
        foreach (var key in MediaKeys)
        {
            if (!m.TryGetProperty(key, out _)) continue;
            // Підпис до медіа йде як текст клієнта; без підпису - плейсхолдер.
            if (m.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                return (c.GetString()!, InboundKinds.Text);
            return ($"[{key}]", InboundKinds.Unsupported);
        }
        return ("[unsupported]", InboundKinds.Unsupported);
    }

    private string Token() =>
        creds.Get(Channel)?.Token ?? throw new PermanentChannelException("Telegram token is not configured", ChannelSendCodes.TokenMissing);

    private static HttpRequestMessage Call(string token, string method, object payload) =>
        new(HttpMethod.Post, $"{ApiBase}/bot{token}/{method}") { Content = JsonContent.Create(payload) };

    internal const int MaxTextUnits = 4096;

    internal static string TruncateUtf16(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = max > 0 && char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }

    public async Task<ChannelSendReceipt> SendAsync(OutboundMessage msg, CancellationToken ct)
    {
        var token = Token();
        // L8: Telegram приймає ≤ 4096 одиниць UTF-16; страхувальна межа для будь-якого вихідного (без розриву сурогатної пари).
        var text = TruncateUtf16(msg.Text, MaxTextUnits);
        // Невалідна розкладка (довге callback_data, порожній підпис) не надсилається: текст іде без кнопок (§23.8).
        object payload = msg.Actions is { } a && a.IsValid()
            ? new
            {
                chat_id = msg.ExternalConversationId, text,
                reply_markup = new
                {
                    inline_keyboard = a.Rows.Select(r => r.Select(b => new { text = b.Label, callback_data = b.Data }).ToArray()).ToArray(),
                },
            }
            : new { chat_id = msg.ExternalConversationId, text };
        using var req = Call(token, "sendMessage", payload);
        var body = await HttpSend.PostForBodyAsync(http, req, "Telegram sendMessage", ct, telegramCodes: true);
        return new ChannelSendReceipt(ReadMessageId(body));
    }

    private static string? ReadMessageId(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("message_id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n))
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>answerCallbackQuery без тексту (§23.4). Один виклик без повторів; тайм-аут задає викликач (InboundIngestCore: 3 с).</summary>
    public async Task AcknowledgeCallbackAsync(string callbackQueryId, CancellationToken ct)
    {
        var token = Token();
        using var req = Call(token, "answerCallbackQuery", new { callback_query_id = callbackQueryId });
        await HttpSend.PostAsync(http, req, "Telegram answerCallbackQuery", ct, telegramCodes: true);
    }

    /// <summary>editMessageReplyMarkup з порожньою клавіатурою. 400 "message is not modified" / "message to edit not found" = вже прибрано.</summary>
    public async Task ClearActionsAsync(string externalConversationId, string externalMessageId, CancellationToken ct)
    {
        var token = Token();
        object messageId = long.TryParse(externalMessageId, out var n) ? n : externalMessageId;
        using var req = Call(token, "editMessageReplyMarkup", new
        {
            chat_id = externalConversationId, message_id = messageId, reply_markup = new { inline_keyboard = Array.Empty<object>() },
        });
        try { await HttpSend.PostAsync(http, req, "Telegram editMessageReplyMarkup", ct, telegramCodes: true); }
        catch (PermanentChannelException ex) when (ex.Code is ChannelSendCodes.MessageNotModified or ChannelSendCodes.MessageNotFound) { }
    }

    /// <summary>setMyCommands двічі: без language_code і з language_code=uk (§23.4). Тайм-аут <see cref="CommandsTimeout"/> на обидва виклики.</summary>
    public async Task SyncCommandsAsync(CancellationToken ct)
    {
        var token = Token();
        var commands = BotCommands.Select(c => new { command = c.Command, description = c.Description }).ToArray();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CommandsTimeout);
        try
        {
            using (var req = Call(token, "setMyCommands", new { commands }))
                await HttpSend.PostAsync(http, req, "Telegram setMyCommands", cts.Token, telegramCodes: true);
            using (var req = Call(token, "setMyCommands", new { commands, language_code = "uk" }))
                await HttpSend.PostAsync(http, req, "Telegram setMyCommands", cts.Token, telegramCodes: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TransientChannelException("Telegram setMyCommands timeout"); }
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetString() : null;
}
