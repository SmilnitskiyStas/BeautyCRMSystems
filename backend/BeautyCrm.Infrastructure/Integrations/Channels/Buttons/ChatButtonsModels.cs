using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

// Інлайн-кнопки Telegram: клавіатури, перевірка callback, дії клієнта (TASK-752, §23.3-§23.7, §23.11, ADR-013).
// Дані дії беруться лише з БД (options, пропозиція, запис); з callback_data - verb, токен, index.

/// <summary>Налаштування кнопок і скасування в чаті (§23.2): <c>ChatButtons:*</c>, <c>ChatCancel:*</c>. Значення поза межами -> типові.</summary>
public sealed class ChatButtonsOptions
{
    /// <summary>Глобальний вимикач (за замовч. false): із вимкненим натискання старих кнопок дають Stale (Disabled).</summary>
    public bool Enabled { get; init; }
    public int SlotButtons { get; init; } = 6;
    public int SlotPickTtlMinutes { get; init; } = 30;
    public int ListTtlMinutes { get; init; } = 60;
    public int CancelConfirmTtlMinutes { get; init; } = 10;
    public int MaxCallbacksPerConversationPerMinute { get; init; } = 20;
    public int MyAppointmentsMax { get; init; } = 4;
    public bool CancelEnabled { get; init; } = true;
    public int CancelMinLeadMinutes { get; init; }
    public int CancelMaxPerClientPerDay { get; init; } = 3;
    /// <summary>TASK-781 (§26.2): <c>Marketing:Enabled</c> (за замовч. false). Вимкнено: <c>mc</c>, <c>co</c>, <c>i:mkt</c> -> Disabled (T-action-stale); <c>i:mkoff</c> працює завжди.</summary>
    public bool MarketingEnabled { get; init; }
    /// <summary>TASK-781 (§26.2): TTL клавіатури marketing_consent, <c>Marketing:ConsentKeyboardTtlHours</c> (168; 1..720).</summary>
    public int MarketingConsentTtlHours { get; init; } = 168;
    /// <summary>TASK-781 (§26.8): типовий строк campaign_offer, якщо викликач не задав <c>ExpiresAt</c> (queued_at + 30 днів).</summary>
    public const int CampaignOfferDefaultTtlDays = 30;

    public static ChatButtonsOptions Read(IConfiguration c)
    {
        static int I(IConfiguration c, string key, int def, int min, int max) =>
            int.TryParse(c[key], out var v) && v >= min && v <= max ? v : def;
        static bool B(IConfiguration c, string key, bool def) => bool.TryParse(c[key], out var v) ? v : def;
        return new ChatButtonsOptions
        {
            Enabled = B(c, "ChatButtons:Enabled", false),
            SlotButtons = I(c, "ChatButtons:SlotButtons", 6, 1, 8),
            SlotPickTtlMinutes = I(c, "ChatButtons:SlotPickTtlMinutes", 30, 5, 240),
            ListTtlMinutes = I(c, "ChatButtons:ListTtlMinutes", 60, 5, 1440),
            CancelConfirmTtlMinutes = I(c, "ChatButtons:CancelConfirmTtlMinutes", 10, 2, 60),
            MaxCallbacksPerConversationPerMinute = I(c, "ChatButtons:MaxCallbacksPerConversationPerMinute", 20, 5, 120),
            MyAppointmentsMax = I(c, "ChatButtons:MyAppointmentsMax", 4, 1, 5),
            CancelEnabled = B(c, "ChatCancel:Enabled", true),
            CancelMinLeadMinutes = I(c, "ChatCancel:MinLeadMinutes", 0, 0, 1440),
            CancelMaxPerClientPerDay = I(c, "ChatCancel:MaxPerClientPerDay", 3, 1, 20),
            MarketingEnabled = B(c, "Marketing:Enabled", false),
            MarketingConsentTtlHours = I(c, "Marketing:ConsentKeyboardTtlHours", 168, 1, 720),
        };
    }
}

public static class KeyboardKinds
{
    public const string BookingConfirm = "booking_confirm";
    public const string SlotPick = "slot_pick";
    public const string ReminderChoice = "reminder_choice";
    public const string AppointmentList = "appointment_list";
    public const string AppointmentActions = "appointment_actions";
    public const string CancelConfirm = "cancel_confirm";
    /// <summary>TASK-781 (§26.9.1): вибір рівня згоди (3 кнопки <c>mc</c>).</summary>
    public const string MarketingConsent = "marketing_consent";
    /// <summary>TASK-781 (§26.9.1): кнопка розсилки <c>co</c> (+ <c>i:mkoff</c>); прив'язана до campaign_recipient_id.</summary>
    public const string CampaignOffer = "campaign_offer";

    public static readonly IReadOnlyList<string> All =
        [BookingConfirm, SlotPick, ReminderChoice, AppointmentList, AppointmentActions, CancelConfirm, MarketingConsent, CampaignOffer];

    /// <summary>Нова клавіатура цього виду відкликає попередню активну в розмові (§23.6 «Відкликання»); одна активна appointment_list на розмову (TASK-759, M3); actions не відкликаються (прив'язані до запису).</summary>
    public static bool ReplacesPrevious(string kind) => kind is BookingConfirm or SlotPick or ReminderChoice or CancelConfirm or AppointmentList or MarketingConsent;
}

public static class KeyboardStatuses
{
    public const string Active = "active";
    public const string Used = "used";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

/// <summary>Закритий перелік verb (§23.5). Безстанові - <c>i:*</c> (як у options воркера).</summary>
public static class ButtonVerbs
{
    public const string ConfirmBooking = "by";
    public const string DeclineBooking = "bn";
    public const string OtherTime = "bo";
    public const string SlotPick = "sp";
    public const string ReminderChoice = "rc";
    public const string CancelAsk = "ac";
    public const string Attend = "aa";
    public const string Reschedule = "ar";
    public const string CancelYes = "cy";
    public const string CancelNo = "cn";
    public const string Book = "i:book";
    public const string My = "i:my";
    public const string Help = "i:help";
    public const string Human = "i:human";
    public const string Stop = "i:stop";
    /// <summary>TASK-768 (§25.4/§25.5.1): «Змінити номер / спосіб» - безстанова дія; обробка (T-phone-request) - конвеєр TASK-770.</summary>
    public const string Phone = "i:phone";
    /// <summary>TASK-781 (§26.9.1): вибір рівня згоди; станова, споживає клавіатуру marketing_consent, index 0..2.</summary>
    public const string MarketingConsent = "mc";
    /// <summary>TASK-781: «Записатися зі знижкою» під розсилкою; станова, не споживає, kind=campaign_offer.</summary>
    public const string CampaignOffer = "co";
    /// <summary>TASK-781: меню згоди (<c>/marketing</c>, T-marketing-menu); вимкнено <c>Marketing:Enabled</c> -> T-action-stale.</summary>
    public const string MarketingMenu = "i:mkt";
    /// <summary>TASK-781: «Не надсилати акції»; діє завжди (маркетинг/кнопки вимкнені, handoff).</summary>
    public const string MarketingOff = "i:mkoff";

    public static readonly IReadOnlySet<string> Stateless = new HashSet<string>(StringComparer.Ordinal) { Book, My, Help, Human, Stop, Phone, MarketingMenu, MarketingOff };

    private static readonly IReadOnlyDictionary<string, string[]> ByKind = new Dictionary<string, string[]>
    {
        [KeyboardKinds.BookingConfirm] = [ConfirmBooking, DeclineBooking, OtherTime],
        [KeyboardKinds.SlotPick] = [SlotPick],
        [KeyboardKinds.ReminderChoice] = [ReminderChoice],
        [KeyboardKinds.AppointmentList] = [CancelAsk, Reschedule],
        [KeyboardKinds.AppointmentActions] = [Attend, CancelAsk, Reschedule],
        [KeyboardKinds.CancelConfirm] = [CancelYes, CancelNo],
        [KeyboardKinds.MarketingConsent] = [MarketingConsent],
        [KeyboardKinds.CampaignOffer] = [CampaignOffer],
    };

    /// <summary>Verb дозволений для виду клавіатури (стан), безстанові кнопки (рядок «Записатися» тощо) дозволені в options будь-якого виду.</summary>
    public static bool AllowedFor(string kind, string verb) =>
        Stateless.Contains(verb) || (ByKind.TryGetValue(kind, out var v) && v.Contains(verb, StringComparer.Ordinal));

    /// <summary>Чи потрібен index: sp, rc, а також ac/ar у переліку записів (§23.5).</summary>
    public static bool NeedsIndex(string kind, string verb) =>
        verb is SlotPick or ReminderChoice or MarketingConsent || (kind == KeyboardKinds.AppointmentList && verb is CancelAsk or Reschedule);

    /// <summary>Дії, що споживають клавіатуру (CAS active -> used).</summary>
    public static bool Consuming(string verb) => verb is ConfirmBooking or DeclineBooking or OtherTime or SlotPick or ReminderChoice or CancelYes or CancelNo or MarketingConsent;

    /// <summary>Дозволені під час handoff (§23.6, §25.5.6, §26.9.1): i:stop, i:my, i:phone, rc, aa, ac, cy, cn, mc, i:mkt, i:mkoff. <c>co</c> заблоковано.</summary>
    public static bool AllowedInHandoff(string verb) =>
        verb is Stop or My or Phone or ReminderChoice or Attend or CancelAsk or CancelYes or CancelNo or MarketingConsent or MarketingMenu or MarketingOff;
}

/// <summary>Розібраний <c>callback_data</c>. Станові: <c>verb:token[:index]</c>; безстанові: <c>i:book|my|help|human|stop|phone|mkt|mkoff</c>.</summary>
public readonly record struct CallbackAddress(string Verb, string? Token, int? Index)
{
    private static readonly Regex Stateful = new(@"^(by|bn|bo|sp|rc|ac|aa|ar|cy|cn|mc|co):([A-Za-z0-9_-]{22})(?::([0-9]))?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StatelessRx = new(@"^i:(book|my|help|human|stop|phone|mkt|mkoff)\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool IsStateless => Token is null;

    public static bool TryParse(string? data, out CallbackAddress address)
    {
        address = default;
        if (string.IsNullOrEmpty(data) || Encoding.UTF8.GetByteCount(data) > 28) return false;
        if (StatelessRx.Match(data) is { Success: true } s) { address = new CallbackAddress("i:" + s.Groups[1].Value, null, null); return true; }
        if (Stateful.Match(data) is not { Success: true } m) return false;
        address = new CallbackAddress(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null);
        return true;
    }

    /// <summary><c>verb:token</c> або <c>verb:token:index</c>; безстанові - <c>i:*</c> без токена.</summary>
    public static string Build(string verb, string? token, int? index = null) =>
        ButtonVerbs.Stateless.Contains(verb) ? verb : index is { } i ? $"{verb}:{token}:{i}" : $"{verb}:{token}";
}

/// <summary>
/// Дозволена кнопка з серверними посиланнями (§23.3 «options»): код пише, код читає; модель і клієнт їх не бачать.
/// sp: заклад/послуга/майстер/початок; rc: method/offsets; ac|ar|aa: appointmentId; mc: level (full|selective|none) + textVersion (TASK-781).
/// </summary>
public sealed record KeyboardOption(
    string Verb, string Label, Guid? LocationId = null, Guid? ServiceId = null, Guid? SpecialistId = null,
    DateTimeOffset? StartsAt = null, string? Method = null, IReadOnlyList<short>? Offsets = null, Guid? AppointmentId = null,
    string? Level = null, string? TextVersion = null);

public static class KeyboardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IReadOnlyList<KeyboardOption> options) => JsonSerializer.Serialize(options, Options);

    public static IReadOnlyList<KeyboardOption>? TryParse(string json)
    {
        try { return JsonSerializer.Deserialize<List<KeyboardOption>>(json, Options); }
        catch (JsonException) { return null; }
    }
}

public sealed record ActionButton(string Label, string Data);

/// <summary>Розкладка (§23.8): ≤ 10 кнопок, ≤ 3 у рядку (2 для слотів з майстром), ≤ 6 рядків, підпис ≤ 32 кодові точки, callback_data ≤ 64 байти.</summary>
public static class ChatKeyboardLayout
{
    public const int MaxButtons = 10, MaxRows = 6, MaxLabel = 32;

    /// <summary>Назва з БД -> один рядок без керівних/форматних символів, ≤ <paramref name="max"/> кодових точок.</summary>
    public static string SanitizeLabel(string? s, int max = MaxLabel)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse) { sb.Append(' '); continue; }
            sb.Append(Rune.IsWhiteSpace(r) ? " " : r.ToString());
        }
        var flat = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.Concat(flat.EnumerateRunes().Take(max).Select(r => r.ToString())).TrimEnd();
    }

    /// <returns>null - розкладка валідна; інакше код (<c>keyboard_invalid</c> для логу).</returns>
    public static string? Validate(IReadOnlyList<IReadOnlyList<ActionButton>> rows, int maxPerRow = 3)
    {
        if (rows.Count is 0 or > MaxRows) return "keyboard_invalid";
        var total = 0;
        foreach (var row in rows)
        {
            if (row.Count == 0 || row.Count > maxPerRow) return "keyboard_invalid";
            foreach (var b in row)
            {
                total++;
                if (string.IsNullOrWhiteSpace(b.Label) || b.Label.EnumerateRunes().Count() > MaxLabel) return "keyboard_invalid";
                if (b.Label.Any(char.IsControl)) return "keyboard_invalid";
                if (Encoding.UTF8.GetByteCount(b.Data) > 64 || !CallbackAddress.TryParse(b.Data, out _)) return "keyboard_invalid";
            }
        }
        return total > MaxButtons ? "keyboard_invalid" : null;
    }

    /// <summary>Нейтральна розкладка для <c>beauty_messages.actions</c> (як у воркера): <c>{"rows":[[{"label","data"}]]}</c>.</summary>
    public static string ToActionsJson(IReadOnlyList<IReadOnlyList<ActionButton>> rows) =>
        JsonSerializer.Serialize(new { rows = rows.Select(r => r.Select(b => new { label = b.Label, data = b.Data })) }, KeyboardJson.Options);

    /// <summary>Підписи кнопок з <c>actions</c> (inbox показує лише їх; callback_data і токени не віддаються).</summary>
    public static IReadOnlyList<string> ReadLabels(string? actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(actionsJson);
            var container = doc.RootElement;
            // §25.4: підписи reply-клавіатури ({"reply":{"rows":...}}) показуються в inbox так само, як inline
            if (container.ValueKind == JsonValueKind.Object
                && container.EnumerateObject().FirstOrDefault(p => p.Name.Equals("reply", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.Object } rp)
                container = rp.Value;
            var rows = container.ValueKind == JsonValueKind.Object
                ? container.EnumerateObject().FirstOrDefault(p => p.NameEquals("rows") || p.Name.Equals("Rows", StringComparison.OrdinalIgnoreCase)).Value
                : default;
            if (rows.ValueKind != JsonValueKind.Array) return [];
            var labels = new List<string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in row.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.Object) continue;
                    foreach (var p in b.EnumerateObject())
                        if (p.Name.Equals("label", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                            labels.Add(p.Value.GetString()!);
                }
            }
            return labels;
        }
        catch (JsonException) { return []; }
    }
}

/// <summary>Тіло вхідного callback для inbox: <c>"[кнопка] {підпис}"</c> (≤ 64 символи), підпис - з options у БД, не з Telegram (§23.6).</summary>
public static class CallbackBody
{
    public const string Prefix = "[кнопка]";

    public static string Build(string? label)
    {
        var clean = ChatKeyboardLayout.SanitizeLabel(label, 64 - Prefix.Length - 1);
        return clean.Length == 0 ? Prefix : $"{Prefix} {clean}";
    }

    /// <summary>Підпис з тіла (для inbox «Клієнт натиснув: …»); порожньо, якщо підпису ще немає.</summary>
    public static string? LabelOf(string? body) =>
        body is not null && body.StartsWith(Prefix + " ", StringComparison.Ordinal) ? body[(Prefix.Length + 1)..] : null;
}

// ------------------------------------------------------------------ результат перевірки callback (§23.5)

public enum CallbackOutcome
{
    Valid, Stateless, InvalidFormat, UnknownToken, ForeignConversation, RefMismatch, InvalidOption, Used, Revoked, Expired, HandoffBlocked,
    /// <summary>Адитивно: <c>ChatButtons:Enabled=false</c> - стара кнопка -> T-action-stale + прибирання.</summary>
    Disabled,
}

/// <summary>Узагальнення для відповіді: Valid (дія), Stale (T-action-stale), Used, Expired, Mismatch (підозріле), Handoff.</summary>
public enum CallbackClass { Valid, Stale, Used, Expired, Mismatch, Handoff }

/// <param name="Id">Клавіатура, знайдена за токеном і ПЕРЕВІРЕНА як належна цій розмові (інакше null).</param>
public sealed record KeyboardInfo(
    Guid Id, string Kind, string Status, Guid MessageId, Guid? ProposalId, Guid? AppointmentId, DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt, Guid? UsedByMessageId, Guid? CampaignRecipientId = null);

/// <param name="Verb">Verb із callback_data (порожньо при invalid_format).</param>
/// <param name="Option">Обрана опція з БД (для безстанових - null).</param>
/// <param name="OptionPosition">Позиція опції в options (для used_option).</param>
/// <param name="Replay">true - повторна обробка того самого вхідного callback (клавіатура вже <c>used</c> ним): ефект ідемпотентний.</param>
public sealed record CallbackResolution(
    CallbackOutcome Outcome, string Verb = "", int? Index = null, KeyboardInfo? Keyboard = null, KeyboardOption? Option = null,
    int OptionPosition = 0, bool Replay = false, DateTimeOffset? Now = null)
{
    public bool IsValid => Outcome is CallbackOutcome.Valid or CallbackOutcome.Stateless;

    /// <summary>TASK-781: для валідного <c>mc</c> - обраний рівень згоди з options у БД (не з callback_data); інакше null.</summary>
    public string? ConsentLevel => IsValid && Verb == ButtonVerbs.MarketingConsent ? Option?.Level : null;

    /// <summary>TASK-781: для валідного <c>mc</c> - версія показаного тексту з options у БД.</summary>
    public string? ConsentTextVersion => IsValid && Verb == ButtonVerbs.MarketingConsent ? Option?.TextVersion : null;

    /// <summary>TASK-781: для валідних <c>co</c> / <c>i:mkoff</c> під повідомленням кампанії - одержувач (перевірений як цієї розмови й клієнта); інакше null.</summary>
    public Guid? CampaignRecipientId => IsValid && Verb is ButtonVerbs.CampaignOffer or ButtonVerbs.MarketingOff ? Keyboard?.CampaignRecipientId : null;

    /// <summary>Код результату §23.5 (<c>ok</c>, <c>invalid_format</c>, <c>used</c> ...).</summary>
    public string Code => Outcome switch
    {
        CallbackOutcome.Valid or CallbackOutcome.Stateless => "ok",
        CallbackOutcome.InvalidFormat => "invalid_format",
        CallbackOutcome.UnknownToken => "unknown_token",
        CallbackOutcome.ForeignConversation => "foreign_conversation",
        CallbackOutcome.RefMismatch => "ref_mismatch",
        CallbackOutcome.InvalidOption => "invalid_option",
        CallbackOutcome.Used => "used",
        CallbackOutcome.Revoked => "revoked",
        CallbackOutcome.Expired => "expired",
        CallbackOutcome.HandoffBlocked => "handoff_blocked",
        _ => "buttons_disabled",
    };

    public CallbackClass Class => Outcome switch
    {
        CallbackOutcome.Valid or CallbackOutcome.Stateless => CallbackClass.Valid,
        CallbackOutcome.Used => CallbackClass.Used,
        CallbackOutcome.Expired => CallbackClass.Expired,
        CallbackOutcome.ForeignConversation or CallbackOutcome.RefMismatch => CallbackClass.Mismatch,
        CallbackOutcome.HandoffBlocked => CallbackClass.Handoff,
        _ => CallbackClass.Stale,
    };

    /// <summary>Прибрати розмітку знайденої клавіатури цієї розмови (§23.5 «Результат → відповідь»).</summary>
    public bool ClearMarkup => Keyboard is not null && !IsValid && Outcome != CallbackOutcome.HandoffBlocked;

    /// <summary><c>used</c> ≤ 30 с після використання - відповіді немає (клієнт уже бачить результат).</summary>
    public bool Quiet => Outcome == CallbackOutcome.Used && Keyboard?.UsedAt is { } at && Now is { } n && n - at <= TimeSpan.FromSeconds(30);

    /// <summary>Для handoff_blocked: відкликати booking_confirm|slot_pick розмови.</summary>
    public bool RevokesAiKeyboards => Outcome == CallbackOutcome.HandoffBlocked;
}

// ------------------------------------------------------------------ створення

/// <param name="ExpiresAt">Явний TTL; null - за видом: slot_pick/appointment_list/cancel_confirm з налаштувань, booking_confirm - до кінця пропозиції,
/// appointment_actions - початок візиту, reminder_choice - 24 год.</param>
public sealed record NewKeyboard(
    Guid ConversationId, Guid MessageId, string Kind, IReadOnlyList<KeyboardOption> Options, DateTimeOffset? ExpiresAt = null,
    Guid? ProposalId = null, Guid? AppointmentId = null, Guid? CampaignRecipientId = null);

/// <param name="Token">22 символи base64url (128 біт CSPRNG). Лише для побудови callback_data; не логувати, не віддавати API.</param>
/// <param name="Revoked">Попередні активні клавіатури, відкликані цією (їхню розмітку треба прибрати).</param>
public sealed record CreatedKeyboard(Guid Id, string Token, DateTimeOffset ExpiresAt, IReadOnlyList<Guid> Revoked)
{
    public string Data(string verb, int? index = null) => CallbackAddress.Build(verb, Token, index);

    public override string ToString() => $"CreatedKeyboard {{ Id = {Id} }}"; // токен не друкується
}

/// <summary>Клавіатура, розмітку якої треба прибрати (sweeper, §23.4). Без external_message_id рядок закривається одразу й не повертається.</summary>
public sealed record PendingClear(Guid KeyboardId, Guid ChannelId, string ExternalChatId, string ExternalMessageId)
{
    public override string ToString() => $"PendingClear {{ KeyboardId = {KeyboardId} }}"; // chat id не друкується
}

/// <summary>Частотний ліміт натискань на розмову (§23.2: <c>MaxCallbacksPerConversationPerMinute</c>); in-memory, ковзне вікно.</summary>
public sealed class ChatCallbackRateLimiter(ChatButtonsOptions options, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    /// <returns>false - ліміт вичерпано (натискання не рахується).</returns>
    public bool TryAcquire(Guid conversationId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_hits.Count > 5000)
                foreach (var k in _hits.Where(kv => kv.Value.Count == 0 || now - kv.Value.Peek() >= TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList())
                    _hits.Remove(k);
            if (!_hits.TryGetValue(conversationId, out var q)) _hits[conversationId] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= options.MaxCallbacksPerConversationPerMinute) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
