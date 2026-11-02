using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>
/// Єдиний модуль нормалізації тексту, що потрапляє в промпт (TASK-723, M1/M3 рев'ю безпеки). Використовують і валідатор
/// додатку закладу (<see cref="AiPromptAddendum"/>), і обгортка тексту клієнта (<see cref="PromptInjectionGuard"/>), тож
/// перевірений і відправлений текст проходять однакові кроки.
/// <para>
/// Два подання: <see cref="Clean"/> (те, що зберігається й відправляється: без невидимих символів, NFC) і
/// <see cref="Skeleton"/> (лише для порівняння: NFKD без діакритики, confusables, нижній регістр, розкриті HTML-сутності).
/// Збіг шукається в скелеті, правка робиться в Clean-тексті.
/// </para>
/// </summary>
public static partial class AiTextNormalizer
{
    /// <summary>Імена тегів, якими промпт розділяє довірені й недовірені частини.</summary>
    private const string TagNames = @"client[\s_\-]*message|tenant[\s_\-]*instructions|system|tool[\s_\-]*result(?:[\s_\-]*data)?";

    [GeneratedRegex(@"<[\s/\\]*(?:" + TagNames + @")(?![a-z0-9])")]
    private static partial Regex TagRx();

    [GeneratedRegex(@"&(?:#([0-9]{1,8})|#[xX]([0-9a-fA-F]{1,6})|([a-zA-Z]{2,8}));?|\\u([0-9a-fA-F]{4})")]
    private static partial Regex EntityRx();

    [GeneratedRegex(@"^ *(?:={3,}|-{3,})", RegexOptions.Multiline)]
    private static partial Regex SeparatorRx();

    /// <summary>Скелет тексту містить будь-яку подобу тегів-розділювачів (`&lt;`, fullwidth, кирилиця, невидимі символи, сутності).</summary>
    public static bool ContainsDelimiterTag(string text) => TagRx().IsMatch(Skeleton(text));

    /// <summary>Рядок, що починається з `===`/`---` (будь-які юнікод-пробіли, fullwidth).</summary>
    public static bool ContainsSeparatorLine(string text) => SeparatorRx().IsMatch(Skeleton(text));

    // ---- невидиме ----

    /// <summary>Cf (bidi, ZWJ/ZWNJ, U+00AD, U+180E, ...), блок тегів, variation selectors, U+034F, Hangul fillers, керівні (крім \n, \t).</summary>
    private static bool IsRemovable(Rune r)
    {
        var v = r.Value;
        if (v is '\n' or '\t') return false;
        if (v is >= 0xE0000 and <= 0xE007F) return true;
        if (v is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF) return true;
        if (v is 0x034F or 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x180B or 0x180C or 0x180D or 0x180E or 0x180F) return true;
        var cat = Rune.GetUnicodeCategory(r);
        return cat is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.Surrogate;
    }

    /// <summary>Видаляє невидиме (по кодових точках, сурогатні пари враховано); U+2028/2029/0085 -> \n; CR/CRLF -> \n.</summary>
    public static string StripInvisible(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes()) // лишній сурогат -> U+FFFD (не кидає)
        {
            if (r.Value is 0x2028 or 0x2029 or 0x85) { sb.Append('\n'); continue; }
            if (IsRemovable(r)) continue;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Подання для збереження й відправки: без невидимого, потім NFC, \n замість CR. Ідемпотентне.</summary>
    public static string Clean(string? s) => StripInvisible(StripInvisible(s).Normalize(NormalizationForm.FormC));

    // ---- скелет ----

    private static string DecodeEntities(string s) => EntityRx().Replace(s, m =>
    {
        if (m.Groups[4].Success) return CharOf(Convert.ToInt32(m.Groups[4].Value, 16)) ?? m.Value;
        if (m.Groups[1].Success) return int.TryParse(m.Groups[1].Value, out var d) ? CharOf(d) ?? m.Value : m.Value;
        if (m.Groups[2].Success) return CharOf(Convert.ToInt32(m.Groups[2].Value, 16)) ?? m.Value;
        return NamedEntities.TryGetValue(m.Groups[3].Value.ToLowerInvariant(), out var v) ? v : m.Value;
    });

    private static string? CharOf(int cp) => Rune.IsValid(cp) ? new Rune(cp).ToString() : null;

    private static readonly Dictionary<string, string> NamedEntities = new()
    {
        ["lt"] = "<", ["gt"] = ">", ["sol"] = "/", ["bsol"] = "\\", ["equals"] = "=", ["lowbar"] = "_", ["amp"] = "&",
        ["nbsp"] = " ", ["ensp"] = " ", ["emsp"] = " ", ["thinsp"] = " ", ["tab"] = " ", ["newline"] = "\n",
        ["hyphen"] = "-", ["dash"] = "-", ["minus"] = "-", ["lsaquo"] = "<", ["rsaquo"] = ">", ["lang"] = "<", ["rang"] = ">",
        ["laquo"] = "<", ["raquo"] = ">",
    };

    private static readonly Dictionary<char, char> Confusables = BuildConfusables();

    private static Dictionary<char, char> BuildConfusables()
    {
        var d = new Dictionary<char, char>();
        void Add(string from, char to) { foreach (var c in from) d[c] = to; }
        // кирилиця (нижній регістр) -> латиниця
        Add("а", 'a'); Add("е", 'e'); Add("о", 'o'); Add("р", 'p'); Add("с", 'c'); Add("х", 'x'); Add("у", 'y'); Add("к", 'k');
        Add("і", 'i'); Add("ї", 'i'); Add("ј", 'j'); Add("ѕ", 's'); Add("м", 'm'); Add("т", 't'); Add("н", 'h'); Add("в", 'b');
        Add("ԁ", 'd'); Add("ԛ", 'q'); Add("ԝ", 'w'); Add("ѵ", 'v'); Add("ӏ", 'l'); Add("ѡ", 'w'); Add("ɡ", 'g'); Add("ԍ", 'g');
        Add("г", 'r'); Add("п", 'n'); Add("и", 'u'); Add("ѐ", 'e'); Add("є", 'e');
        // грецька
        Add("α", 'a'); Add("ε", 'e'); Add("ο", 'o'); Add("ι", 'i'); Add("ν", 'v'); Add("τ", 't'); Add("κ", 'k'); Add("ρ", 'p');
        Add("υ", 'u'); Add("χ", 'x'); Add("γ", 'y'); Add("η", 'n'); Add("ς", 's'); Add("σ", 'o'); Add("μ", 'u'); Add("ω", 'w');
        // інші латинські схожі
        Add("ı", 'i'); Add("ɩ", 'i'); Add("Ӏӏӏǀ", 'l'); Add("ꞓ", 'c'); Add("ᴄ", 'c'); Add("ᴇ", 'e'); Add("ᴍ", 'm'); Add("ꜱ", 's');
        Add("ᴛ", 't'); Add("ɴ", 'n'); Add("ʏ", 'y'); Add("ɑ", 'a'); Add("ᴜ", 'u'); Add("ɡ", 'g'); Add("ʀ", 'r'); Add("ɪ", 'i');
        // кутові дужки, слеш, дорівнює, дефіс, підкреслення
        Add("‹⟨〈〈⟪《﹤˂ᐸ⧀❮‹«〈", '<'); Add("›⟩〉〉⟫》﹥˃ᐳ⧁❯»〉", '>');
        Add("⁄∕⧸╱⟋⫽／", '/'); Add("∖⧵＼", '\\');
        Add("═⩵≡⚌⁼‗꓿᐀", '='); Add("‐‑‒–—―−﹣⁃─━⎯⸺", '-'); Add("⎽ˍ‿⁔", '_');
        return d;
    }

    /// <summary>
    /// Скелет для порівняння: (невидиме -> HTML/JSON-сутності -> невидиме)* -> NFKD без діакритики -> нижній регістр -> confusables;
    /// будь-які юнікод-пробіли -> ' ' (\n лишається).
    /// </summary>
    public static string Skeleton(string? raw)
    {
        var s = StripInvisible(raw);
        for (var i = 0; i < 4; i++) // &amp;lt; і подібне: декілька проходів
        {
            var d = StripInvisible(DecodeEntities(s));
            if (d == s) break;
            s = d;
        }
        var sb = new StringBuilder(s.Length);
        foreach (var r in StripInvisible(s.Normalize(NormalizationForm.FormKD)).EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark) continue;
            if (r.Value == '\n') { sb.Append('\n'); continue; }
            if (Rune.IsWhiteSpace(r) || cat is UnicodeCategory.SpaceSeparator) { sb.Append(' '); continue; }
            var lower = Rune.ToLowerInvariant(r);
            if (lower.Value == 0x130) { sb.Append('i'); continue; }
            if (lower.IsBmp && Confusables.TryGetValue((char)lower.Value, out var mapped)) { sb.Append(mapped); continue; }
            sb.Append(lower.ToString());
        }
        return sb.ToString();
    }

    // ---- нейтралізація ----

    /// <summary>
    /// Якщо скелет містить подобу тега-розділювача, у Clean-тексті всі `&lt;`-подібні символи замінюються на `[`, `&gt;`-подібні на `]`,
    /// `&amp;` на `+` (довжина не змінюється). Звичайний текст з `&lt;`/`&amp;` без таких тегів не чіпається.
    /// </summary>
    public static string NeutralizeTags(string clean)
    {
        if (!ContainsDelimiterTag(clean)) return clean;
        var sb = new StringBuilder(clean.Length);
        foreach (var r in clean.EnumerateRunes())
        {
            var t = r.ToString();
            if (t == "&") { sb.Append('+'); continue; }
            var sk = Skeleton(t);
            sb.Append(sk switch { "<" => "[", ">" => "]", _ => t });
        }
        var result = sb.ToString();
        // JSON-екранування (System.Text.Json пише '<' як <): знешкодити лише <, >, & і /, решта (кирилиця) лишається
        return ContainsDelimiterTag(result)
            ? JsonEscapeRx().Replace(result, m => Skeleton(m.Value) is "<" or ">" or "&" ? "?" : m.Value)
            : result;
    }

    [GeneratedRegex(@"\\u[0-9a-fA-F]{4}")]
    private static partial Regex JsonEscapeRx();

    // ---- мітки системи/адміністратора (L2) ----

    /// <summary>
    /// Фрази міток, якими історія позначає тексти ролі assistant (<see cref="ChatHistory"/>); шукаються у скелеті. Маркер вкладення
    /// не входить: він стоїть усередині самого `<client_message>`, тож підроблений нічого не додає.
    /// </summary>
    private static readonly string[] LabelPhrases = ["службове повідомлення", "відповідь адміністратора"];

    private static readonly Regex LabelRx = new(
        string.Join('|', LabelPhrases.Select(p => string.Join(@"\s+", Skeleton(p).Split(' ').Select(Regex.Escape)))), RegexOptions.Compiled);

    private static bool IsOpenBracket(string t) => Skeleton(t) == "[" || t is "【" or "〔" or "⟦" or "❲" or "﹇" or "〖";
    private static bool IsCloseBracket(string t) => Skeleton(t) == "]" || t is "】" or "〕" or "⟧" or "❳" or "﹈" or "〗";

    /// <summary>
    /// Текст клієнта не повинен імітувати мітки системи/адміністратора з історії. Якщо у скелеті є фраза мітки (гомогліфи, fullwidth,
    /// невидимі символи вже враховано), усі квадратні дужки (і їхні подоби) замінюються на круглі. Звичайний текст не чіпається; ідемпотентно.
    /// </summary>
    public static string NeutralizeLabels(string clean)
    {
        if (string.IsNullOrEmpty(clean) || !LabelRx.IsMatch(Skeleton(clean))) return clean;
        var sb = new StringBuilder(clean.Length);
        foreach (var r in clean.EnumerateRunes())
        {
            var t = r.ToString();
            sb.Append(IsOpenBracket(t) ? "(" : IsCloseBracket(t) ? ")" : t);
        }
        return sb.ToString();
    }

    /// <summary>Рядки, що починаються з `===`/`---`, замінюються на `[separator]` + решта після розділювача.</summary>
    public static string NeutralizeSeparators(string clean)
    {
        var lines = clean.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!SeparatorRx().IsMatch(Skeleton(lines[i]))) continue;
            // посимвольно: зняти початкові пробіли та символи, чий скелет '=' або '-'
            var runes = lines[i].EnumerateRunes().ToList();
            var k = 0;
            while (k < runes.Count && Skeleton(runes[k].ToString()) is " " or "=" or "-" or "") k++;
            lines[i] = "[separator] " + string.Concat(runes.Skip(k).Select(r => r.ToString()));
        }
        return string.Join('\n', lines);
    }

    public static string TruncateCodePoints(string s, int max)
    {
        if (s.Length <= max) return s; // UTF-16 довжина не менша за кількість кодових точок
        var sb = new StringBuilder();
        var n = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (n++ >= max) break;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }
}
