using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>Параметри шару ШІ для чату клієнта (`ChatAssistant:*`, §21.2). Пайплайн розмов (TASK-728) має власні ключі.</summary>
public sealed class ChatPromptOptions
{
    private int _messages = 20;
    private int _chars = 6000;
    /// <summary>Скільки останніх повідомлень історії (1..100) може побачити модель.</summary>
    public int HistoryMessages { get => _messages; set => _messages = Math.Clamp(value, 1, 100); }
    /// <summary>Сумарний ліміт символів історії (500..50000).</summary>
    public int HistoryMaxChars { get => _chars; set => _chars = Math.Clamp(value, 500, 50_000); }
    private int _iterations = 6;
    /// <summary>Скільки ітерацій tool-use дозволено в ході чату (1..8, M2; TASK-735: за замовч. 6, було 4): не більше глобальних `Ai:MaxToolIterations`. `ChatAssistant:MaxToolIterations`.</summary>
    public int MaxToolIterations { get => _iterations; set => _iterations = Math.Clamp(value, 1, 8); }
    private int _ctxServices = 30;
    private int _ctxSpecialists = 15;
    /// <summary>Скільки послуг максимум наводиться в каталозі контексту ходу (0..100); більше - модель користується list_services. `ChatAssistant:ContextMaxServices`.</summary>
    public int ContextMaxServices { get => _ctxServices; set => _ctxServices = Math.Clamp(value, 0, 100); }
    /// <summary>Скільки майстрів максимум наводиться в каталозі контексту ходу (0..50). `ChatAssistant:ContextMaxSpecialists`.</summary>
    public int ContextMaxSpecialists { get => _ctxSpecialists; set => _ctxSpecialists = Math.Clamp(value, 0, 50); }
    /// <summary>Підпис валюти в підсумку (валюти в БД немає).</summary>
    public string CurrencyLabel { get; set; } = "грн";
}

public enum ChatHistoryRole { Client, Assistant }

public enum ChatHistoryKind
{
    /// <summary>Звичайне повідомлення (текст клієнта або відповідь ШІ).</summary>
    Normal,
    /// <summary>Службове повідомлення/шаблон системи (підсумок запису, привітання, handoff): позначається, не вважається вільним текстом ШІ.</summary>
    Template,
    /// <summary>Відповідь людини-адміністратора.</summary>
    Staff,
    /// <summary>Нетекстове вкладення клієнта (фото, голос...). Вміст ніколи не передається: лише позначка.</summary>
    Unsupported,
}

/// <param name="Text">Тіло повідомлення з БД (для <see cref="ChatHistoryKind.Unsupported"/> ігнорується).</param>
public sealed record ChatHistoryItem(ChatHistoryRole Role, string Text, ChatHistoryKind Kind = ChatHistoryKind.Normal);

/// <summary>
/// Історія розмови для моделі (§21.3, ADR-011 §2.8): обмежене вікно за кількістю й сумою символів (беруться найновіші),
/// кожне повідомлення клієнта проходить <see cref="PromptInjectionGuard.Prepare"/> і йде в &lt;client_message&gt; (роль user);
/// відповіді асистента - роль assistant; службові тексти й відповіді персоналу позначені. Результати tools минулих ходів не відтворюються.
/// </summary>
public static class ChatHistory
{
    public const string TemplateLabel = "[службове повідомлення системи] ";
    public const string StaffLabel = "[відповідь адміністратора] ";
    public const string UnsupportedMarker = "[клієнт надіслав нетекстове вкладення]";
    private const int MaxAssistantChars = 2000;

    public static List<AiMessage> Build(IReadOnlyList<ChatHistoryItem>? items, ChatPromptOptions options)
    {
        var result = new List<AiMessage>();
        if (items is null || items.Count == 0) return result;

        // Вікно: останні N повідомлень, потім - зверху вниз від найновішого, доки не вичерпано бюджет символів.
        var prepared = new List<(ChatHistoryRole Role, string Text)>();
        var budget = options.HistoryMaxChars;
        foreach (var item in items.Skip(Math.Max(0, items.Count - options.HistoryMessages)).Reverse())
        {
            var text = Render(item);
            if (text.Length == 0) continue;
            if (text.Length > budget) break;
            budget -= text.Length;
            prepared.Add((item.Role, text));
        }
        prepared.Reverse();

        // Розмова для моделі починається з повідомлення клієнта.
        var start = prepared.FindIndex(p => p.Role == ChatHistoryRole.Client);
        if (start < 0) return result;
        foreach (var (role, text) in prepared.Skip(start))
        {
            if (role == ChatHistoryRole.Client) AddUser(result, PromptInjectionGuard.Wrap(text));
            else AddAssistant(result, text);
        }
        return result;
    }

    /// <summary>Додає текст до останнього user-повідомлення або створює нове (послідовні повідомлення клієнта = блоки одного ходу).</summary>
    public static void AddUser(List<AiMessage> messages, string wrapped)
    {
        if (messages.Count > 0 && messages[^1].Role == "user" && messages[^1].Blocks.All(b => b is AiTextBlock))
            messages[^1] = messages[^1] with { Blocks = [.. messages[^1].Blocks, new AiTextBlock(wrapped)] };
        else
            messages.Add(AiMessage.User(wrapped));
    }

    private static void AddAssistant(List<AiMessage> messages, string text)
    {
        if (messages.Count > 0 && messages[^1].Role == "assistant")
            messages[^1] = messages[^1] with { Blocks = [.. messages[^1].Blocks, new AiTextBlock(text)] };
        else
            messages.Add(new AiMessage("assistant", [new AiTextBlock(text)]));
    }

    private static string Render(ChatHistoryItem item)
    {
        if (item.Role == ChatHistoryRole.Client)
            return item.Kind == ChatHistoryKind.Unsupported ? UnsupportedMarker : PromptInjectionGuard.Prepare(item.Text).Trim();

        // Тексти асистента/персоналу/шаблонів теж із БД: знешкоджуємо теги-розділювачі й обмежуємо довжину.
        var clean = AiTextNormalizer.NeutralizeLabels(AiTextNormalizer.NeutralizeTags(AiTextNormalizer.Clean(item.Text))).Trim();
        if (clean.Length == 0) return "";
        clean = AiTextNormalizer.TruncateCodePoints(clean, MaxAssistantChars);
        return item.Kind switch
        {
            ChatHistoryKind.Template => TemplateLabel + clean,
            ChatHistoryKind.Staff => StaffLabel + clean,
            _ => clean,
        };
    }
}

/// <summary>Результат вихідного guard: текст для клієнта (null -> T-fallback) і причина позначки <c>flagged</c> для журналу.</summary>
public sealed record ChatOutputCheck(string? Text, string? FlagReason);

/// <summary>
/// Вихідний guard (§21.5 крок 9): текст ШІ перед відправкою клієнту. Plain text, без parse_mode. Довжини рахуються в UTF-16
/// (так рахує Telegram: емодзі = 2 одиниці), а не в кодових точках.
/// </summary>
public static partial class ChatOutputGuard
{
    /// <summary>Максимум тексту моделі (UTF-16).</summary>
    public const int MaxLength = 3500;
    /// <summary>Ліміт Telegram sendMessage (UTF-16 одиниць після розбору). Підсумок + текст моделі не перевищують його.</summary>
    public const int MaxBodyLength = 4096;
    private const int MinIntroLength = 40;
    private const int SystemGramWords = 6;
    private const int AddendumGramWords = 8;

    public const string FlagSecret = "secret";
    public const string FlagSystemPrompt = "system_prompt";
    public const string FlagAddendum = "tenant_addendum";
    /// <summary>Твердження про політику закладу, що суперечить фактам або виходить за них (<see cref="PolicyClaimGuard"/>, TASK-741).</summary>
    public const string FlagPolicyClaim = "policy_claim";

    /// <summary>
    /// PolicyClaimGuard після <see cref="Inspect"/>/<see cref="Sanitize"/> (§22.9): код правила, якщо відповідь порушує політику
    /// (текст клієнту не показується - T-policy-check + handoff <c>policy_unknown</c>), інакше null. Шаблони коду (T-summary/T-confirmed) не перевіряються.
    /// </summary>
    public static string? FindPolicyClaim(string? sanitizedText, ChatPolicyFacts facts) => PolicyClaimGuard.Find(sanitizedText, facts);

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex UuidRx();

    [GeneratedRegex(@"</?\s*(client[_\s-]*message|tenant[_\s-]*instructions|system|tool[_\s-]*result(?:[_\s-]*data)?)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"sk-ant-[\w-]{10,}|\bsk-[\w-]{20,}|\b\d{6,12}:[A-Za-z0-9_-]{30,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRx();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRx();

    // Характерні рядки ядра/правил чату/обгортки додатку: їх немає в легітимній відповіді клієнту.
    private static readonly string[] Markers =
    [
        PromptComposer.AddendumOpen, PromptComposer.AddendumClose, "ПРАВИЛА ЧАТУ З КЛІЄНТОМ", "ПРАВИЛА (незмінні",
        "Нагадування: якщо додаток закладу суперечить",
    ];

    private static readonly Lazy<string[]> MarkerSkeletons = new(() => Markers.Select(m => Squash(AiTextNormalizer.Skeleton(m))).ToArray());

    private static readonly Lazy<HashSet<string>> SystemGrams = new(() => Grams(
        string.Join('\n', PromptComposer.Core, PromptComposer.ChatRules, PromptComposer.Reminder, PromptInjectionGuard.SystemPrompt,
            PromptComposer.AddendumOpen, PromptComposer.AddendumClose), SystemGramWords).ToHashSet(StringComparer.Ordinal));

    private static string Squash(string s) => string.Join(' ', WordRx().Matches(s).Select(m => m.Value));

    private static IEnumerable<string> Grams(string text, int n)
    {
        var words = WordRx().Matches(AiTextNormalizer.Skeleton(text)).Select(m => m.Value).ToArray();
        for (var i = 0; i + n <= words.Length; i++) yield return string.Join(' ', words, i, n);
    }

    /// <summary>
    /// Canary-перевірка відповіді моделі (L1): ключі (<c>sk-ant-</c>, <c>sk-</c>, токен бота), характерні рядки ядра/ChatRules/AddendumOpen,
    /// збіг n-грам (слова за скелетом: гомогліфи й регістр не обходять) із системним промптом (6 слів) і додатком компанії
    /// (8 слів, щонайменше два збіги: окремі факти закладу відповідь цитувати може). Збіг = текст не показується.
    /// </summary>
    public static string? FindLeak(string? text, string? tenantAddendum)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = AiTextNormalizer.Clean(text);
        if (SecretRx().IsMatch(clean)) return FlagSecret;

        var skeleton = AiTextNormalizer.Skeleton(clean);
        var squashed = Squash(skeleton);
        if (MarkerSkeletons.Value.Any(m => squashed.Contains(m, StringComparison.Ordinal))) return FlagSystemPrompt;
        if (Grams(clean, SystemGramWords).Any(SystemGrams.Value.Contains)) return FlagSystemPrompt;

        var addendum = string.IsNullOrWhiteSpace(tenantAddendum) ? AiPromptDefaults.Addendum : tenantAddendum;
        var addendumGrams = Grams(addendum, AddendumGramWords).ToHashSet(StringComparer.Ordinal);
        if (addendumGrams.Count > 0 && Grams(clean, AddendumGramWords).Count(addendumGrams.Contains) >= 2) return FlagAddendum;
        return null;
    }

    /// <summary>Canary-перевірка, потім <see cref="Sanitize"/>. Витік = текст null (T-fallback) + причина для журналу <c>flagged</c>.</summary>
    public static ChatOutputCheck Inspect(string? text, string? tenantAddendum) =>
        FindLeak(text, tenantAddendum) is { } reason ? new ChatOutputCheck(null, reason) : new ChatOutputCheck(Sanitize(text), null);

    /// <summary>UUID і теги-розділювачі видаляються, текст обрізається до 3500 одиниць UTF-16. Порожній результат = null (-> T-fallback).</summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = AiTextNormalizer.Clean(text);
        s = TagRx().Replace(s, "");
        s = UuidRx().Replace(s, "");
        // Подоби тегів, що обійшли точний шаблон (fullwidth, кирилиця): знешкодити як у вході.
        s = AiTextNormalizer.NeutralizeTags(s);
        s = StripMarkdown(s);
        s = Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
        if (s.Length > MaxLength) s = TruncateUtf16(s, MaxLength).TrimEnd();
        // Лишилась сама пунктуація/порожнеча (напр. після видалення UUID: «: ,»): це не відповідь -> null. Емодзі (символи, не пунктуація) - відповідь.
        return s.Length == 0 || OnlyPunctuationRx().IsMatch(s) ? null : s;
    }

    [GeneratedRegex(@"^[\s\p{P}]*$")]
    private static partial Regex OnlyPunctuationRx();
    [GeneratedRegex(@"```[^\n`]*\n?")]
    private static partial Regex CodeFenceRx();
    [GeneratedRegex(@"\*{3}(?=\S)(.+?)(?<=\S)\*{3}")]
    private static partial Regex BoldItalicRx();
    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*")]
    private static partial Regex BoldStarRx();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])__(?=\S)(.+?)(?<=\S)__(?![\p{L}\p{N}_])")]
    private static partial Regex BoldUnderscoreRx();
    [GeneratedRegex(@"(?<![\p{L}\p{N}*])\*(?=[^\s*])([^*\n]+?)(?<=[^\s*])\*(?![\p{L}\p{N}*])")]
    private static partial Regex ItalicStarRx();
    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex StrikeRx();
    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]+", RegexOptions.Multiline)]
    private static partial Regex HeadingRx();
    [GeneratedRegex(@"^[ \t]*\*[ \t]+", RegexOptions.Multiline)]
    private static partial Regex StarBulletRx();
    [GeneratedRegex(@"^[ \t]*([-*_])([ \t]*\1){2,}[ \t]*$\n?", RegexOptions.Multiline)]
    private static partial Regex RuleRx();
    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^\s)]+)\)")]
    private static partial Regex LinkRx();
    [GeneratedRegex(@"^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$\n?", RegexOptions.Multiline)]
    private static partial Regex TableSeparatorRx();
    [GeneratedRegex(@"^[ \t]*\|(.*)$", RegexOptions.Multiline)]
    private static partial Regex TableRowRx();

    /// <summary>
    /// Детермінований постпроцесор чистого тексту для месенджера (TASK-735): Telegram без parse_mode показує markdown буквально.
    /// Прибирає `**`, `__`, `*текст*`, `~~`, зворотні лапки й блоки коду, `#` заголовки, лінійки, таблиці (комірки через « - »),
    /// `[текст](url)` -> `текст (url)`; «* » на початку рядка -> «- ». Числа, дати, «грн», емодзі й «-» списки не чіпає.
    /// </summary>
    public static string StripMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = CodeFenceRx().Replace(text, "");
        s = TableSeparatorRx().Replace(s, "");
        s = TableRowRx().Replace(s, m => string.Join(" - ", m.Groups[1].Value.Trim().TrimEnd('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0)));
        s = RuleRx().Replace(s, "");
        s = HeadingRx().Replace(s, "");
        s = StarBulletRx().Replace(s, "- ");
        s = LinkRx().Replace(s, "$1 ($2)");
        s = BoldItalicRx().Replace(s, "$1");
        s = BoldStarRx().Replace(s, "$1");
        s = BoldUnderscoreRx().Replace(s, "$1");
        s = ItalicStarRx().Replace(s, "$1");
        s = StrikeRx().Replace(s, "$1");
        return s.Replace("**", "").Replace("`", "");
    }

    /// <summary>Обрізання до <paramref name="max"/> одиниць UTF-16 без розриву сурогатної пари.</summary>
    public static string TruncateUtf16(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = Math.Max(0, max);
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut];
    }

    /// <summary>
    /// Тіло відповіді з підсумком запису: ≤ 4096 одиниць UTF-16. Обрізається текст моделі (вступ), підсумок - ніколи
    /// (його «Так/Ні» і умови мусять дійти цілими). Коли місця майже немає, вступ відкидається.
    /// </summary>
    public static string ComposeWithSummary(string? modelText, string summary)
    {
        if (string.IsNullOrWhiteSpace(modelText)) return summary;
        var budget = MaxBodyLength - summary.Length - 2;
        if (budget < MinIntroLength) return summary;
        var intro = TruncateUtf16(modelText, budget).TrimEnd();
        return intro.Length == 0 ? summary : $"{intro}\n\n{summary}";
    }
}

/// <summary>
/// T-summary (§21.8): канонічний підсумок запису формує КОД з даних пропозиції, а не модель. Показується клієнту після відповіді ШІ.
/// Значення (назви послуг/майстрів) - дані закладу; теги в них знешкоджуються.
/// </summary>
public static class ChatSummaryText
{
    private static readonly string[] Weekdays = ["нд", "пн", "вт", "ср", "чт", "пт", "сб"];

    /// <param name="buttons">
    /// true - підсумок із кнопками (TASK-753, §23.8): останній рядок про кнопку «Так, записатися». false - текстовий варіант
    /// («Відповідайте «Так»…»), для каналів без кнопок і для вимкнених кнопок.
    /// </param>
    public static string Format(ChatBookingSummaryDto s, int ttlMinutes, string currencyLabel, bool buttons = false)
    {
        string C(string? v) => AiTextNormalizer.NeutralizeTags(AiTextNormalizer.Clean(v)).Trim();
        var inv = CultureInfo.InvariantCulture;
        // Без markdown: Telegram без parse_mode показує «~~стара~~» буквально (TASK-753): стара ціна словами.
        // §26.9.3: купон застосовано -> «купон «назва»»; not_better / not_applicable -> окремий рядок після ціни (купон не споживається).
        var applied = s.CouponOutcome == "applied";
        var price = s.PromotionName is not null || s.PriceFinal < s.PriceOriginal
            ? $"{Money(s.PriceFinal)} {currencyLabel} (замість {Money(s.PriceOriginal)} {currencyLabel}" +
              (s.PromotionName is null ? "" : applied ? $", купон «{C(s.PromotionName)}»" : $", акція «{C(s.PromotionName)}»") + ")"
            : $"{Money(s.PriceFinal)} {currencyLabel}";
        var place = string.IsNullOrWhiteSpace(s.Address) ? C(s.LocationName) : $"{C(s.LocationName)}, {C(s.Address)}";
        var sb = new StringBuilder();
        sb.AppendLine("Підсумок запису:");
        sb.AppendLine($"Послуга: {C(s.ServiceName)}");
        sb.AppendLine($"Майстер: {C(s.SpecialistName)}");
        sb.AppendLine($"Заклад: {place}");
        sb.AppendLine($"Дата й час: {Weekdays[(int)s.LocalDate.DayOfWeek]}, {s.LocalDate.ToString("dd.MM", inv)}, {s.LocalTime.ToString("HH:mm", inv)}, {C(s.Timezone)}");
        sb.AppendLine($"Тривалість: {s.DurationMinutes} хв");
        sb.AppendLine($"Ціна: {price}");
        if (s.CouponOutcome == "not_better") sb.AppendLine("Купон не застосовано: діє вигідніша акція.");
        else if (s.CouponOutcome == "not_applicable") sb.AppendLine("Купон не діє на цю послугу.");
        sb.AppendLine("Оплата в закладі.");
        sb.Append(buttons
            ? $"Натисніть «Так, записатися» або напишіть «Так». Пропозиція дійсна {ttlMinutes} хв."
            : $"Відповідайте «Так», щоб записатися, або «Ні». Пропозиція дійсна {ttlMinutes} хв.");
        return sb.ToString();
    }

    private static string Money(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
