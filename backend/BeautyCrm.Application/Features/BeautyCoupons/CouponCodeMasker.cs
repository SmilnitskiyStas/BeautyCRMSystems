using System.Text.RegularExpressions;

namespace BeautyCrm.Application.Features.BeautyCoupons;

/// <summary>
/// TASK-790 (M1/L10, security-review-marketing-2026-10): маскування кодів купонів у ТІЛАХ повідомлень, які покидають БД
/// (історія для LLM, DTO Inbox, прев'ю). Не залежить від воркера й від <c>Marketing:Enabled</c>: за шаблоном, а не за довідником.
/// Маскується: персональний код у показі <c>XXXXX-XXXXX</c> (алфавіт Crockford), 10 символів без дефіса (цифра + літера),
/// значення після «код|промокод|купон[:]» (4..24 символи [A-Za-z0-9 -]). Загальний код без префікса за шаблоном невідомий - його маскує
/// конвеєр (редагування вхідного) і хеш-пошук.
/// </summary>
public static partial class CouponCodeMasker
{
    public const string Placeholder = "[код купона]";

    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9A-HJKMNP-TV-Za-hjkmnp-tv-z]{5}[-‐‑‒–—][0-9A-HJKMNP-TV-Za-hjkmnp-tv-z]{5}(?![A-Za-z0-9])")]
    private static partial Regex DashedRx();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9]{10}(?![A-Za-z0-9])")]
    private static partial Regex TenRx();

    [GeneratedRegex(@"(?<kw>\b(?:код|промокод|купон)\b\s*:?\s*)(?<v>[A-Za-z0-9][A-Za-z0-9 -]{2,22}[A-Za-z0-9])(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedRx();

    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = PrefixedRx().Replace(text, m => m.Groups["kw"].Value + Placeholder);
        s = DashedRx().Replace(s, Placeholder);
        s = TenRx().Replace(s, m => LooksLikePersonal(m.Value) ? Placeholder : m.Value);
        return s;
    }

    private static bool LooksLikePersonal(string token) =>
        token.Any(char.IsAsciiDigit) && token.Any(char.IsAsciiLetter) && token.All(c => char.IsAsciiDigit(c) || (char.IsAsciiLetter(c) && char.ToUpperInvariant(c) is not ('I' or 'L' or 'O' or 'U')));
}
