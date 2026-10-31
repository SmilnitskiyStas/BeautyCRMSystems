using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyCoupons;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>
/// TASK-782 (§26.9.2-§26.9.3): розбір текстових команд маркетингу без LLM. Меню («акції», «розсилки», «новини й акції») і відписка
/// («не надсилати акції», «без акцій») - точний збіг після <see cref="ChatConfirmationText.Normalize"/>; інші фрази командою не є.
/// Код купона: необов'язковий префікс «код|промокод|купон» (з «:» чи без) + <c>^[A-Za-z0-9 -]{4,24}$</c>. Код ніде не логується.
/// </summary>
public static partial class ChatMarketingParser
{
    private static readonly HashSet<string> MenuWords = new(StringComparer.Ordinal) { "акції", "розсилки", "новини й акції" };
    private static readonly HashSet<string> OffWords = new(StringComparer.Ordinal) { "не надсилати акції", "без акцій" };

    /// <summary>Тіло повідомлення замість коду купона у збереженому вхідному (код не лишається в історії LLM / inbox / логах).</summary>
    public const string CodeBodyRedacted = "[код купона]";

    [GeneratedRegex(@"^\s*(?:код|промокод|купон)\s*:?\s*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PrefixRx();

    [GeneratedRegex(@"^[A-Za-z0-9 -]{4,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRx();

    public static bool IsMenuText(string? body) => MenuWords.Contains(ChatConfirmationText.Normalize(body));

    public static bool IsOffText(string? body) => OffWords.Contains(ChatConfirmationText.Normalize(body));

    /// <summary>Кандидат у коди купона з тексту. <c>Prefixed</c> - клієнт явно написав «код…».</summary>
    public readonly record struct CodeShape(string Code, bool Prefixed, bool PersonalFormat)
    {
        /// <summary>Достатньо вигляду, щоб вважати текст спробою ввести код без звернення до БД.</summary>
        public bool LooksLikeCode => Prefixed || PersonalFormat;
    }

    /// <returns>null - текст не має вигляду коду.</returns>
    public static CodeShape? ParseCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var text = body.Trim();
        var prefixed = false;
        if (PrefixRx().Match(text) is { Success: true } m)
        {
            prefixed = true;
            text = m.Groups["rest"].Value.Trim();
        }
        if (!CodeRx().IsMatch(text)) return null;
        var norm = CouponCodes.Normalize(text);
        if (!CouponCodes.IsPlausible(norm)) return null;
        // Персональний формат: 10 символів алфавіту Crockford. Лише цифри без префікса - це швидше номер телефону, ніж код.
        var personal = norm.Length == CouponCodes.PersonalLength && norm.All(c => CouponCodes.Alphabet.Contains(c))
            && (prefixed || !norm.All(char.IsAsciiDigit));
        return new CodeShape(text, prefixed, personal);
    }
}
