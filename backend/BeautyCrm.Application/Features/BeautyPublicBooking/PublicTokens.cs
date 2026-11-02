using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Application.Features.BeautyPublicBooking;

/// <summary>
/// Публічний токен запису = base64url(HMAC-SHA256(ключ сервера, tenantId.appointmentId)): 256 біт, непідбірний без ключа
/// і не послідовний. У БД лишається лише SHA-256 токена; токен виводиться детерміновано, тож повтор Idempotency-Key
/// може знову його повернути (клієнт міг загубити відповідь).
/// </summary>
public sealed class PublicTokenService(PublicBookingOptions options)
{
    public const int TokenLength = 43; // base64url від 32 байт без паддінгу

    public string Create(Guid tenantId, Guid appointmentId)
    {
        if (options.TokenKey.Length < 32) throw new InvalidOperationException("PublicBooking token key is not configured.");
        var mac = HMACSHA256.HashData(options.TokenKey, Encoding.ASCII.GetBytes($"{tenantId:N}.{appointmentId:N}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool IsWellFormed(string? token) =>
        token is { Length: TokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Нормалізація телефону до E.164 (+цифри). Неоднозначні національні формати відхиляються.</summary>
public static class PhoneNormalizer
{
    /// <summary>
    /// Приймає: +CC..., 00CC..., 0XXXXXXXXX (10 цифр, національний UA -> +38...), CC... (11-15 цифр без +).
    /// Дозволені роздільники: пробіл, дужки, дефіс, крапка. Null — формат недійсний.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32) return null;
        var s = raw.Trim();
        var plus = s.StartsWith('+');
        var body = plus ? s[1..] : s;
        if (!body.All(c => char.IsAsciiDigit(c) || c is ' ' or '(' or ')' or '-' or '.')) return null;
        var digits = new string(body.Where(char.IsAsciiDigit).ToArray());

        if (!plus && digits.StartsWith("00")) { digits = digits[2..]; plus = true; }
        if (!plus)
        {
            if (digits.Length == 10 && digits[0] == '0') digits = "38" + digits;
            else if (digits.Length < 11) return null;
        }
        // §25.5.3: E.164 = 8..15 цифр, перша не 0; український код +380 = рівно 12 цифр (+380 і 9 цифр абонента)
        if (digits.Length is < 8 or > 15 || digits[0] == '0') return null;
        if (digits.StartsWith("380", StringComparison.Ordinal) && digits.Length != 12) return null;
        return "+" + digits;
    }

    /// <summary>Лише ASCII-цифри рядка (порівняння номерів у різних форматах, TASK-769 / §25.5.3).</summary>
    public static string Digits(string? raw) =>
        raw is null ? "" : new string(raw.Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// Варіанти цифр номера для порівняння зі спадковими (ненормалізованими) значеннями в БД: сирі цифри, нормалізовані цифри
    /// і національний вигляд 0XXXXXXXXX для +380. Порожній список = номер без цифр. Порівняння лише за цифрами (V1, §25.7).
    /// </summary>
    public static IReadOnlyList<string> MatchCandidates(string? raw)
    {
        var set = new List<string>(3);
        void Add(string d)
        {
            if (d.Length >= 5 && !set.Contains(d)) set.Add(d);
        }
        Add(Digits(raw));
        if (Normalize(raw) is { } e164)
        {
            var d = e164[1..];
            Add(d);
            if (d.StartsWith("380", StringComparison.Ordinal) && d.Length == 12) Add("0" + d[3..]);
        }
        return set;
    }

    /// <summary>Маска для inbox (§25.7): «+», потім • замість усіх цифр, крім останніх двох. Порожній номер -> null.</summary>
    public static string? Mask(string? phone)
    {
        var d = Digits(phone);
        if (d.Length == 0) return null;
        return d.Length <= 2 ? "+" + d : "+" + new string('•', d.Length - 2) + d[^2..];
    }
}

/// <summary>
/// Визначає tenant за slug з URL до першого звернення до БД (безпечно: окреме з'єднання + вузька SELECT-політика
/// tenants_login_lookup, як при логіні). Невідомий, призупинений tenant чи вимкнений модуль beauty_booking
/// неможливо розрізнити ззовні — завжди false (404).
/// </summary>
public sealed partial class PublicTenantResolver(IAuthStore store)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$")]
    private static partial Regex SlugPattern();

    public Guid? TenantId { get; private set; }

    public async Task<bool> ResolveAsync(string? slug, CancellationToken ct)
    {
        var normalized = slug?.Trim().ToLowerInvariant();
        if (normalized is null || !SlugPattern().IsMatch(normalized)) return false;
        var tenant = await store.FindTenantBySlugAsync(normalized, ct);
        if (tenant is not { IsActive: true } || !tenant.Modules.Contains("beauty_booking", StringComparer.OrdinalIgnoreCase))
            return false;
        store.UseTenant(tenant.Id);
        TenantId = tenant.Id;
        return true;
    }
}
