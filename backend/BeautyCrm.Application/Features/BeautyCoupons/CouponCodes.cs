using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Application.Features.BeautyCoupons;

/// <summary>
/// Коди купонів (§26.6, ADR-015): нормалізація, хеш, підказка, генерація персональних кодів. Повний персональний код існує
/// лише в повідомленні клієнту; у БД - SHA-256 і останні 3 символи. Той самий алгоритм у воркері (TASK-780): тестовий вектор у тестах.
/// </summary>
public static class CouponCodes
{
    /// <summary>Crockford без неоднозначних I, L, O, U.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int PersonalLength = 10;
    public const int SharedMinLength = 4;
    public const int SharedMaxLength = 20;
    public const string HashPrefix = "coupon:";

    /// <summary>Верхній регістр, без пробілів і '-', O→0, I|L→1.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch == '-') continue;
            var u = char.ToUpperInvariant(ch);
            sb.Append(u switch { 'O' => '0', 'I' or 'L' => '1', _ => u });
        }
        return sb.ToString();
    }

    /// <summary>Нормалізований код має вигляд, який може існувати (4..20 символів A-Z0-9); решта - заздалегідь invalid.</summary>
    public static bool IsPlausible(string normalized) =>
        normalized.Length is >= SharedMinLength and <= SharedMaxLength && normalized.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z'));

    /// <summary>lower(hex(SHA-256(UTF-8("coupon:" + normalize(code))))).</summary>
    public static string Hash(string? rawCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HashPrefix + Normalize(rawCode)))).ToLowerInvariant();

    /// <summary>Останні 3 символи нормалізованого коду.</summary>
    public static string Hint(string? rawCode)
    {
        var n = Normalize(rawCode);
        return n.Length <= 3 ? n : n[^3..];
    }

    /// <summary>10 символів алфавіту (CSPRNG, 50 біт).</summary>
    public static string GeneratePersonal()
    {
        Span<char> chars = stackalloc char[PersonalLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>XXXXX-XXXXX для показу клієнту.</summary>
    public static string Display(string personalCode) =>
        personalCode.Length == PersonalLength ? $"{personalCode[..5]}-{personalCode[5..]}" : personalCode;

    /// <summary>Загальний код менеджера: 4..20 символів [A-Za-z0-9] (без пробілів і дефісів).</summary>
    public static bool IsValidSharedInput(string? code) =>
        code is { Length: >= SharedMinLength and <= SharedMaxLength } && code.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));
}
