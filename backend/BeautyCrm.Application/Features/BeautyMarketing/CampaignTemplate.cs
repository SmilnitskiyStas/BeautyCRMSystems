using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BeautyCrm.Application.Features.BeautyMarketing;

/// <summary>
/// Шаблон повідомлення кампанії (§26.7, §26.10): валідація (whitelist підстановок, довжина, заборонені символи й теги) і рендер прикладу
/// для перегляду. Рядки «Умови», код і відписку додає система (менеджер їх не прибирає); повідомлення йде plain text без parse_mode.
/// Той самий текст формує воркер (<c>renderCampaignText</c>).
/// </summary>
public static partial class CampaignTemplate
{
    public const int MaxLength = 1000;
    /// <summary>Гранична довжина тіла (кодові точки) з урахуванням рядків, які додає система (~400): ліміт повідомлення 3500.</summary>
    public const int MaxRenderedBody = 3000;
    public const string UnsubscribeNote = "Щоб не отримувати акції, напишіть «не надсилати акції» (або «Стоп» — тоді вимкнуться й нагадування).";
    public const string UnsubscribeNoteButtons = "Щоб не отримувати акції, напишіть «Стоп» або натисніть кнопку «Не надсилати акції».";
    public static readonly string[] Placeholders = ["name", "discount", "service", "valid_from", "valid_to", "code"];
    private static readonly string[] PromoOnly = ["discount", "service", "valid_from", "valid_to"];

    [GeneratedRegex(@"\{([^{}\s]*)\}")]
    private static partial Regex PlaceholderRx();

    [GeneratedRegex(@"</?[A-Za-z!][^>]*>")]
    private static partial Regex TagRx();

    /// <summary>Повертає код помилки (<c>invalid_template</c>) і причину, або null, якщо шаблон дійсний.</summary>
    public static string? Validate(string? template, string type, string couponMode)
    {
        if (string.IsNullOrWhiteSpace(template)) return "template is empty";
        if (template.Length > MaxLength) return $"template is longer than {MaxLength} characters";
        foreach (var ch in template)
            if (char.IsControl(ch) && ch != '\n') return "template contains control characters";
        if (TagRx().IsMatch(template)) return "template must not contain markup tags";
        var rest = PlaceholderRx().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return Array.IndexOf(Placeholders, key) >= 0 ? "" : "\u0001";
        });
        if (rest.Contains('\u0001')) return "template contains an unknown placeholder";
        if (rest.Contains('{') || rest.Contains('}')) return "template contains an unbalanced brace";
        foreach (Match m in PlaceholderRx().Matches(template))
        {
            var key = m.Groups[1].Value;
            if (key == "code" && couponMode == CouponModes.None) return "{code} requires a coupon";
            if (type == CampaignTypes.News && Array.IndexOf(PromoOnly, key) >= 0) return $"{{{key}}} is not available for news";
        }
        // найгірший випадок підстановок (імена ≤ 30, решта ≤ 80): повідомлення не має перевищити ліміт Telegram
        var worst = PlaceholderRx().Replace(template, m => new string('x', m.Groups[1].Value == "name" ? 30 : m.Groups[1].Value == "code" ? 11 : 80));
        if (new StringInfo(worst).LengthInTextElements > MaxRenderedBody) return "the rendered message would be too long";
        return null;
    }

    public sealed record SampleInput(
        string Template, string Type, string Discount, string Service, string ValidFrom, string ValidTo, string? Code, bool Buttons,
        string Name = "Олена");

    /// <summary>Текст-приклад для перегляду (так само, як складає воркер).</summary>
    public static string Render(SampleInput i)
    {
        var values = new Dictionary<string, string>
        {
            ["name"] = i.Name, ["discount"] = i.Discount, ["service"] = i.Service, ["valid_from"] = i.ValidFrom, ["valid_to"] = i.ValidTo,
            ["code"] = i.Code is null ? "" : i.Code,
        };
        var body = PlaceholderRx().Replace(i.Template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Sanitize(v) : m.Value);
        var tail = new List<string>();
        if (i.Type == CampaignTypes.Promo)
            tail.Add($"Умови: знижка {i.Discount} на {i.Service}, діє з {i.ValidFrom} по {i.ValidTo}. Одна знижка на запис, не сумується з іншими акціями.");
        if (i.Code is not null && !i.Template.Contains("{code}", StringComparison.Ordinal)) tail.Add($"Ваш код: {i.Code}");
        if (!i.Buttons)
            tail.Add(i.Code is not null ? $"Щоб записатися зі знижкою, напишіть сюди код {i.Code}" : "Щоб записатися, напишіть «Записатися».");
        tail.Add(i.Buttons ? UnsubscribeNoteButtons : UnsubscribeNote);
        return body + "\n\n" + string.Join('\n', tail);
    }

    /// <summary>Один рядок, без керівних символів, ≤ 80 символів.</summary>
    public static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value) sb.Append(char.IsControl(ch) ? ' ' : ch);
        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return s.Length > 80 ? s[..80] : s;
    }

    public static string FormatDiscount(string discountType, decimal value, string currencyLabel)
    {
        var v = value.ToString("0.##", CultureInfo.InvariantCulture);
        return discountType == "percent" ? $"{v}%" : $"{v} {currencyLabel}";
    }

    public static string FormatServices(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "усі послуги",
        <= 3 => string.Join(", ", names),
        _ => string.Join(", ", names.Take(3)) + $" та ще {names.Count - 3}",
    };
}
