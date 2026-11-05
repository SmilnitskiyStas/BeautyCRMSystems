using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>Результат <see cref="PolicyClaimGuard"/>: код правила (для журналу, без тексту) і чи було спрацювання.</summary>
public sealed record PolicyClaim(string Rule);

/// <summary>
/// Вихідний евристичний guard політики (TASK-741, §22.9, ADR-012): твердження моделі про дзвінки, канали, нагадування, оплату й
/// повернення коштів, що суперечать <see cref="ChatPolicyFacts"/> або виходять за них. Спрацювання = текст клієнту НЕ показується
/// (конвеєр відповідає T-policy-check і передає розмову адміністратору, <c>policy_unknown</c>). Журнал містить лише код правила.
/// Евристика свідомо вузька: перевіряються лише фрази-обіцянки/заперечення; перелік фактів, відповідь «такого немає» (заперечення
/// поруч із каналом/оплатою) і звичайні фрази про запис не спрацьовують. Текст клієнта сюди не потрапляє.
/// </summary>
public static partial class PolicyClaimGuard
{
    public const string RuleCallNegative = "call_negative";
    public const string RuleCallPromise = "call_promise";
    public const string RuleOtherChannel = "other_channel";
    public const string RulePayment = "payment";
    public const string RuleRefund = "refund";
    public const string RuleReminderOffset = "reminder_offset";
    public const string RuleReminderChannel = "reminder_channel";

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // ---- виклики ----

    // Заперечення дзвінка: «окремого дзвінка не буде», «дзвінка не буде», «не буде дзвінка», «ми не телефонуємо/дзвонимо».
    [GeneratedRegex(
        @"окремого\s+(?:телефонного\s+)?дзвінка|дзвінк\w*\s+(?:\S+\s+){0,2}не\s+(?:буде|передбачен\w+|робим\w*|здійснюєт\w+)|" +
        @"не\s+буде\s+(?:жодного\s+|окремого\s+|телефонного\s+)*дзвінк\w*|" +
        @"\bне\s+(?:будемо\s+(?:телефонувати|дзвонити)|телефонуємо|дзвонимо|зателефонуємо|подзвонимо|телефонуватимемо|дзвонитимемо)\b|" +
        @"отдельного\s+звонка|не\s+будет\s+(?:отдельного\s+)?звонк\w*|no\s+(?:separate\s+|phone\s+)?call\b", Opts)]
    private static partial Regex CallNegativeRx();

    // Обіцянка дзвінка: ми/адміністратор/менеджер ... зателефонує/подзвонить, «дзвінок буде».
    [GeneratedRegex(
        @"\b(?:зателефонуємо|зателефоную|подзвонимо|подзвоню|продзвонимо|наберемо|телефонуватимемо|телефонуємо|дзвонимо|дзвонитимемо|" +
        @"позвоним|позвоню|перезвоним)\b|" +
        @"\b(?:будемо\s+(?:дзвонити|телефонувати))\b|" +
        @"\b(?:зателефонує|зателефонують|подзвонить|подзвонять|набере|телефонуватиме|позвонит)\b|" +
        @"\b(?:адміністратор|менеджер|оператор|майстер|співробітник|наш\s+\S+)\w*\b(?:(?!\bви\b)[^.!?,:\n]){0,40}?\b(?:зателефону(?:є|ють|вати|ватиме)|подзвонить|подзвонять|подзвонити|набере)\b|" +
        @"\bми\s+(?:\S+\s+){0,2}(?:зателефонувати|подзвонити)\b|" +
        @"\bдзвінок\s+(?:буде|відбудеться|надійде)|\b(?:буде|надійде)\s+дзвінок\b|" +
        @"\bwe(?:'ll|\s+will)\s+(?:call|phone)\b", Opts)]
    private static partial Regex CallPromiseRx();

    // Конкретний час дзвінка: «за годину», «за 30 хвилин», «через 10 хв», гарантії. «За добу/напередодні/перед візитом» дозволені.
    [GeneratedRegex(
        @"\bза\s+(?:півгодини|пів\s+години|годину|півтори|\d+\s*хв\w*|\d+\s*год\w*|(?:дві|два|три|чотири)\s+год\w*|кілька\s+(?:год\w*|хв\w*))|" +
        @"\bчерез\s+(?:\d+|годину|півгодини|кілька)|\bгарантуєм\w*|\bобов.язково\b|\bточно\s+(?:о|в|у|за)\b", Opts)]
    private static partial Regex CallTimeCommitmentRx();

    [GeneratedRegex(@"\bза\s+(?:добу|день|24\s*год\w*|двадцять\s+чотири)", Opts)]
    private static partial Regex DayBeforeRx();

    // ---- канали ----

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:sms|смс|viber|вайбер\w*|вібер\w*|whats\s?app|ватсап\w*|вотсап\w*|e-?mail|емейл\w*|імейл\w*|електронн\w+\s+(?:пошт\w*|лист\w*)|" +
        @"на\s+(?:вашу\s+)?(?:електронну\s+)?пошт[уи]|поштою)(?![\p{L}\p{N}])", Opts)]
    private static partial Regex OtherChannelRx();

    // Контекст «як канал»: обіцянка/запит надіслати, нагадати, написати.
    [GeneratedRegex(@"нагад|надішл|надсил|надсл|повідом|напиш|зв.яж|відправ|прийд|надійд|сповіст|отрим|розсил|канал|відповім|send|remind|notify", Opts)]
    private static partial Regex ChannelContextRx();

    // ---- оплата ----

    [GeneratedRegex(
        @"онлайн[\s-]*(?:оплат|платіж|платеж)\w*|оплат\w*\s+онлайн|посиланн\w*\s+на\s+оплат\w*|оплатіть\s+(?:карт\w*|онлайн|через|за\s+посиланням)|" +
        @"сплатіть\s+(?:карт\w*|онлайн|через)|оплат\w*\s+(?:на\s+сайті|через\s+(?:посиланн\w*|сайт))|виставимо\s+рахунок|" +
        @"рахунок\s+на\s+оплат\w*|liqpay|apple\s*pay|google\s*pay|передоплат\w*|предоплат\w*|pay\s+online|online\s+payment|payment\s+link", Opts)]
    private static partial Regex PaymentRx();

    // ---- повернення коштів ----

    [GeneratedRegex(@"поверн\w*|відшкод\w*|компенс\w*|refund", Opts)]
    private static partial Regex RefundContextRx();

    [GeneratedRegex(@"\b(?:повернемо|повернуть|поверне|повернеться|повернуться|поверну|відшкодуємо|компенсуємо|refund)\b", Opts)]
    private static partial Regex RefundPromiseRx();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(\d{1,3}(?:[.,]\d+)?)\s*(?:%|відсот\w*|процент\w*)", Opts)]
    private static partial Regex PercentRx();

    // Пояснення умов: така фраза не є голою обіцянкою (відсотки ловить окреме правило).
    [GeneratedRegex(@"згідно|відповідно|за\s+умовами|залежно|залежить|за\s+політик\w*|якщо|в\s+залежності|за\s+правилами", Opts)]
    private static partial Regex ConditionalRx();

    // «менше ніж за 12 год» (без «не» перед «менше»): ми всередині вікна повернення.
    [GeneratedRegex(@"(?<!не\s)(?:менш\w*|менше|пізніше|протягом|в\s+межах|ближче)\s+(?:ніж\s+)?(?:за\s+)?(\d{1,3})\s*год", Opts)]
    private static partial Regex InWindowHoursRx();

    // ---- нагадування ----

    [GeneratedRegex(@"\bнагада(?:ємо|ю|ти)\b|надішлемо\s+(?:вам\s+)?нагадуванн\w*|нагадуванн\w*\s+(?:надійде|прийде|надсилатиме\w*)|надсилатимемо\s+нагадуванн\w*", Opts)]
    private static partial Regex ReminderPromiseRx();

    [GeneratedRegex(@"\bза\s+(\d{1,3})\s*(год\w*|хв\w*|дн\w*|тиж\w*)", Opts)]
    private static partial Regex ReminderOffsetNumRx();

    [GeneratedRegex(@"\bза\s+(?:півгодини|пів\s+години|тиждень|кілька\s+(?:днів|годин)|(?:три|чотири|п.ять|шість)\s+(?:годин\w*|дні\w*|днів))", Opts)]
    private static partial Regex ReminderOffsetWordRx();

    // ---- допоміжні ----

    [GeneratedRegex(@"\b(?:не|ні|немає|нема|відсутн\w*|недоступн\w*|без|no|not|never)\b", Opts)]
    private static partial Regex NegationRx();

    [GeneratedRegex(@"(?<=[.!?…])\s+|[\n;]+")]
    private static partial Regex SentenceSplitRx();

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex SpacesRx();

    /// <summary>Код правила, якщо текст порушує політику, інакше null. Порожній текст не перевіряється.</summary>
    public static string? Find(string? text, ChatPolicyFacts facts)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var plain = Normalize(text);
        // Гомогліфи (латинська «c» у «смс», «e» в «email» тощо): друга перевірка по тексту зі зведеними латинськими двійниками.
        var folded = FoldLatinLookalikes(plain);
        return Check(plain, facts) ?? (folded == plain ? null : Check(folded, facts));
    }

    private static string? Check(string text, ChatPolicyFacts facts)
    {
        // Заперечення дзвінка суперечить фактам, коли дзвінки в закладі ввімкнено (дзвінок-завдання існує).
        if (facts.CallEnabled && CallNegativeRx().IsMatch(text)) return RuleCallNegative;

        foreach (var sentence in SentenceSplitRx().Split(text))
        {
            var s = sentence.Trim();
            if (s.Length == 0) continue;
            var rule = CheckSentence(s, facts);
            if (rule is not null) return rule;
        }
        return null;
    }

    private static string? CheckSentence(string s, ChatPolicyFacts f)
    {
        // Обіцянка дзвінка: недоступний -> завжди; доступний -> лише з конкретним часом чи гарантією.
        if (!CallNegativeRx().IsMatch(s))
            foreach (Match m in CallPromiseRx().Matches(s))
            {
                // «Адміністратор не зможе зателефонувати»: заперечення всередині збігу або перед ним - не обіцянка.
                if (NegationRx().IsMatch(m.Value) || NegationRx().IsMatch(s[Math.Max(0, m.Index - 14)..m.Index])) continue;
                if (!f.CallAvailable) return RuleCallPromise;
                if (CallTimeCommitmentRx().IsMatch(s) && !DayBeforeRx().IsMatch(s)) return RuleCallPromise;
            }

        // Інший канал як спосіб зв'язку: SMS/Viber/WhatsApp/email/пошта з контекстом «надішлемо/нагадаємо», без заперечення поруч.
        if (ChannelContextRx().IsMatch(s))
            foreach (Match m in OtherChannelRx().Matches(s))
                if (!NegatedNear(s, m)) return RuleOtherChannel;

        // Оплата, якої немає у фактах (онлайн, передоплата, посилання). «Повернення передоплати» - з фактів.
        foreach (Match m in PaymentRx().Matches(s))
        {
            if (NegatedNear(s, m)) continue;
            // «Повернення передоплати» - формулювання з фактів: передоплата в реченні про повернення не є обіцянкою оплати.
            if ((m.Value.StartsWith("перед", StringComparison.OrdinalIgnoreCase) || m.Value.StartsWith("предоплат", StringComparison.OrdinalIgnoreCase))
                && RefundContextRx().IsMatch(s))
                continue;
            return RulePayment;
        }

        // Повернення коштів: відсоток поза фактами / не той для вікна або гола обіцянка без умов і відсотка.
        if (RefundContextRx().IsMatch(s))
        {
            var percents = PercentRx().Matches(s).Select(p => ParsePercent(p.Groups[1].Value)).ToList();
            if (percents.Count > 0)
            {
                var allowed = AllowedPercents(f.Cancellation);
                if (percents.Any(p => !allowed.Contains(p))) return RuleRefund;
                if (InWindowHoursRx().Match(s) is { Success: true } w && int.TryParse(w.Groups[1].Value, out var hours) && hours <= f.Cancellation.WindowHours
                    && f.Cancellation.RefundPercentOutside != f.Cancellation.RefundPercentInWindow
                    && percents.Any(p => p == f.Cancellation.RefundPercentOutside) && !percents.Any(p => p == f.Cancellation.RefundPercentInWindow))
                    return RuleRefund;
            }
            else if (RefundPromiseRx().IsMatch(s) && !ConditionalRx().IsMatch(s) && !NegationRx().IsMatch(s))
            {
                return RuleRefund;
            }
        }

        // Нагадування: точки поза набором 1/2/24 год; Telegram вимкнено, а модель обіцяє нагадування.
        if (ReminderPromiseRx().IsMatch(s))
        {
            if (!f.TelegramEnabled && !NegationRx().IsMatch(s)) return RuleReminderChannel;
            foreach (Match m in ReminderOffsetNumRx().Matches(s))
                if (int.TryParse(m.Groups[1].Value, out var n) && !OffsetAllowed(n, m.Groups[2].Value)) return RuleReminderOffset;
            if (ReminderOffsetWordRx().IsMatch(s)) return RuleReminderOffset;
        }
        return null;
    }

    private static bool OffsetAllowed(int n, string unit)
    {
        var u = unit.ToLowerInvariant();
        if (u.StartsWith("год", StringComparison.Ordinal)) return n is 1 or 2 or 24;
        if (u.StartsWith("хв", StringComparison.Ordinal)) return n is 60 or 120 or 1440;
        if (u.StartsWith("дн", StringComparison.Ordinal)) return n == 1;
        return false; // тижні
    }

    private static HashSet<decimal> AllowedPercents(BeautyCrm.Application.Features.BeautyBooking.CancellationSettings c)
    {
        var set = new HashSet<decimal> { 0, c.RefundPercentInWindow, c.RefundPercentOutside };
        if (c.DeductFee && c.FeePercent > 0)
        {
            set.Add(c.FeePercent);
            set.Add(Math.Round(c.RefundPercentInWindow * (100m - c.FeePercent) / 100m, 2));
            set.Add(Math.Round(c.RefundPercentOutside * (100m - c.FeePercent) / 100m, 2));
        }
        return set;
    }

    private static decimal ParsePercent(string raw) =>
        decimal.Parse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static readonly char[] ClauseDelimiters = [',', ';', ':', '.', '!', '?', '—', '–', '(', ')'];

    /// <summary>
    /// Заперечення в тому ж уривку перед збігом (від попереднього розділового знака, до 40 символів: «не надсилаємо нагадування в Viber»)
    /// або до 40 символів після нього («SMS-нагадувань немає», перелік «(SMS, Viber, email) немає»). «Не хвилюйтесь, надішлемо SMS» - не заперечення.
    /// </summary>
    private static bool NegatedNear(string s, Match m)
    {
        var before = s[Math.Max(0, m.Index - 40)..m.Index];
        var cut = before.LastIndexOfAny(ClauseDelimiters);
        if (cut >= 0) before = before[(cut + 1)..];
        var after = s[(m.Index + m.Length)..Math.Min(s.Length, m.Index + m.Length + 40)];
        return NegationRx().IsMatch(before) || NegationRx().IsMatch(after);
    }

    /// <summary>Clean -> нижній регістр -> єдині апострофи й пробіли.</summary>
    internal static string Normalize(string text)
    {
        var s = AiTextNormalizer.Clean(text).ToLowerInvariant();
        s = s.Replace('’', '\'').Replace('ʼ', '\'').Replace('`', '\'').Replace('´', '\'');
        return SpacesRx().Replace(s, " ").Trim();
    }

    // Латинські двійники кириличних літер -> кирилиця, лише у словах, де вже є кирилиця (чисте «sms»/«email» не чіпаємо).
    private static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['i'] = 'і', ['k'] = 'к', ['m'] = 'м', ['o'] = 'о', ['p'] = 'р', ['t'] = 'т', ['x'] = 'х', ['y'] = 'у',
    };

    internal static string FoldLatinLookalikes(string s)
    {
        return Regex.Replace(s, @"[\p{L}\p{N}]+", m =>
        {
            var word = m.Value;
            var mixed = word.Any(c => c is >= 'а' and <= 'я' or 'і' or 'ї' or 'є' or 'ґ') && word.Any(c => c is >= 'a' and <= 'z');
            if (!mixed) return word;
            var sb = new StringBuilder(word.Length);
            foreach (var c in word) sb.Append(Lookalikes.TryGetValue(c, out var cyr) ? cyr : c);
            return sb.ToString();
        });
    }
}
