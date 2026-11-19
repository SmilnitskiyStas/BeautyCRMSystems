using System.Text;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyPublicBooking;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

public enum PhoneParseKind { NotAPhone, Phone, Invalid, Decline, ChangeNumber }

/// <param name="E164">Лише для <see cref="PhoneParseKind.Phone"/>. У <c>ToString</c>/логи не потрапляє.</param>
public sealed record PhoneParseResult(PhoneParseKind Kind, string? E164 = null)
{
    public static readonly PhoneParseResult NotAPhone = new(PhoneParseKind.NotAPhone);
    public static readonly PhoneParseResult Invalid = new(PhoneParseKind.Invalid);
    public static readonly PhoneParseResult Decline = new(PhoneParseKind.Decline);
    public static readonly PhoneParseResult ChangeNumber = new(PhoneParseKind.ChangeNumber);

    /// <summary>Номер не друкується (записи не повинні потрапляти в лог).</summary>
    private bool PrintMembers(System.Text.StringBuilder sb)
    {
        sb.Append("Kind = ").Append(Kind);
        return true;
    }
}

/// <summary>
/// Детермінований розбір відповіді на питання про номер (§25.5.2-§25.5.3, ADR-014 §3.4). Без LLM.
/// Після trim і необов'язкового префікса («мій номер», «номер», «телефон», «тел», «тел.», «ось»; з «:» чи без) решта мусить складатися
/// лише з цифр, «+», пробілів, «()», «.», «-» (цифри інших алфавітів - «схоже на номер», але не приймаються), інакше це не номер.
/// Кілька номерів (≥ 2 груп по ≥ 7 цифр) - <see cref="PhoneParseKind.Invalid"/>. Нормалізація - <see cref="PhoneNormalizer"/>
/// (E.164, <c>+380</c> = рівно 12 цифр).
/// </summary>
public static partial class ChatPhoneParser
{
    private static readonly HashSet<string> DeclineWords = new(StringComparer.Ordinal)
        { "не треба", "ні", "не хочу", "не потрібно", "відміна" };

    private static readonly HashSet<string> ChangeWords = new(StringComparer.Ordinal)
        { "змінити номер", "змінити спосіб", "мій номер" };

    [GeneratedRegex(@"^(?:мій\s+номер|номер|телефон|тел\.?|ось)\s*:?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRx();

    [GeneratedRegex(@"\p{Nd}+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRunRx();

    /// <summary>Короткі слова відмови (точний збіг після нормалізації); включно з підписом кнопки «Не треба».</summary>
    public static bool IsDecline(string? text) => DeclineWords.Contains(ChatConfirmationText.Normalize(text));

    /// <summary>Явне прохання змінити номер/спосіб (точний збіг після нормалізації), §25.5.1.</summary>
    public static bool IsChangeNumber(string? text) => ChangeWords.Contains(ChatConfirmationText.Normalize(text));

    public static PhoneParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PhoneParseResult.NotAPhone;
        if (IsDecline(text)) return PhoneParseResult.Decline;
        if (IsChangeNumber(text)) return PhoneParseResult.ChangeNumber;

        var s = text.Trim();
        var prefix = PrefixRx().Match(s);
        var rest = prefix.Success && prefix.Length < s.Length ? s[prefix.Length..].Trim() : s;
        if (rest.Length == 0 || rest.Length > 120) return PhoneParseResult.NotAPhone;

        // кілька номерів (через слово чи кому) - не вгадуємо, який саме
        var runs = DigitRunRx().Matches(rest);
        if (runs.Count(m => m.Length >= 7) >= 2) return PhoneParseResult.Invalid;

        if (rest.Length > 32) return PhoneParseResult.NotAPhone;
        var digits = 0;
        var ascii = true;
        foreach (var rune in rest.EnumerateRunes())
        {
            if (Rune.IsDigit(rune))
            {
                digits++;
                if (rune.Value is < '0' or > '9') ascii = false;
            }
            else if (rune.Value is not ('+' or ' ' or '(' or ')' or '.' or '-')) return PhoneParseResult.NotAPhone;
        }
        // менше 6 цифр - це не спроба ввести номер («2 3», «1 год» тощо)
        if (digits < 6) return PhoneParseResult.NotAPhone;
        if (!ascii) return PhoneParseResult.Invalid;
        // «+» лише на початку
        if (rest.LastIndexOf('+') > 0) return PhoneParseResult.Invalid;

        return PhoneNormalizer.Normalize(rest) is { } e164 ? new PhoneParseResult(PhoneParseKind.Phone, e164) : PhoneParseResult.Invalid;
    }
}
