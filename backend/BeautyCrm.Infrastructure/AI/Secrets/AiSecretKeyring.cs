using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.AI.Secrets;

/// <summary>Стан keyring майстер-ключів ШІ (ADR-010 §2.5). Значення ключів ніколи не виходять за межі keyring.</summary>
public enum AiSecretsState
{
    /// <summary>Keyring валідний, активний kid існує.</summary>
    Ready,
    /// <summary>Нічого не налаштовано (модуль ШІ без власних ключів): не помилка старту.</summary>
    NotConfigured,
    /// <summary>Конфіг є, але невалідний (kid, base64, довжина не 32 байти, ActiveKeyId відсутній у keyring).</summary>
    Invalid,
}

/// <summary>
/// Keyring з конфігурації: <c>Ai:Secrets:Keys:{kid}</c> (base64, 32 байти) та <c>Ai:Secrets:ActiveKeyId</c>
/// (env <c>Ai__Secrets__Keys__{kid}</c>, <c>Ai__Secrets__ActiveKeyId</c>). Fail closed: якщо хоч один запис невалідний або
/// активного kid немає, весь keyring недоступний (<see cref="State"/> != Ready). Конструктор не кидає винятків:
/// старт API не залежить від ШІ; помилка проявляється при використанні протектора. Значення ключів не логуються.
/// </summary>
public sealed partial class AiSecretKeyring
{
    public const string KeysSection = "Ai:Secrets:Keys";
    public const string ActiveKeyIdKey = "Ai:Secrets:ActiveKeyId";
    public const int KeySizeBytes = 32;

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex KidRegex();

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public AiSecretKeyring(IConfiguration config)
    {
        // Порожні значення (плейсхолдери з .env.example) вважаються відсутніми.
        var entries = config.GetSection(KeysSection).GetChildren().Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
        var active = config[ActiveKeyIdKey]?.Trim().ToLowerInvariant();

        if (entries.Count == 0 && string.IsNullOrWhiteSpace(active))
        {
            State = AiSecretsState.NotConfigured;
            Problem = "no keys configured";
            return;
        }

        var invalid = 0;
        foreach (var e in entries)
        {
            var kid = e.Key.Trim().ToLowerInvariant();
            if (!IsValidKid(kid) || _keys.ContainsKey(kid) || !TryDecodeKey(e.Value, out var key))
            {
                invalid++;
                continue;
            }
            _keys[kid] = key;
        }
        KeyCount = _keys.Count;

        if (invalid > 0)
            Problem = $"{invalid} keyring entr{(invalid == 1 ? "y is" : "ies are")} invalid (kid format, duplicate kid, or not base64 of 32 bytes)";
        else if (string.IsNullOrWhiteSpace(active))
            Problem = "Ai:Secrets:ActiveKeyId is not set";
        else if (!IsValidKid(active) || !_keys.ContainsKey(active))
            Problem = "Ai:Secrets:ActiveKeyId does not refer to a configured key";

        if (Problem is not null)
        {
            State = AiSecretsState.Invalid;
            _keys.Clear(); // fail closed: невалідний keyring не використовується частково
            return;
        }

        State = AiSecretsState.Ready;
        ActiveKeyId = active;
    }

    public AiSecretsState State { get; }
    public bool IsReady => State == AiSecretsState.Ready;

    /// <summary>Людиночитна причина без значень (для логу старту). Null, якщо Ready.</summary>
    public string? Problem { get; }

    public int KeyCount { get; }
    public string? ActiveKeyId { get; }

    public static bool IsValidKid(string? kid) => kid is not null && KidRegex().IsMatch(kid);

    internal bool TryGetKey(string kid, out byte[] key)
    {
        key = [];
        return IsReady && _keys.TryGetValue(kid, out key!);
    }

    private static bool TryDecodeKey(string? b64, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(b64)) return false;
        var trimmed = b64.Trim();
        var buf = new byte[trimmed.Length]; // base64 -> не більше довжини рядка
        if (!Convert.TryFromBase64String(trimmed, buf, out var written) || written != KeySizeBytes) return false;
        key = buf[..written];
        CryptographicOperations.ZeroMemory(buf);
        return true;
    }

    public override string ToString() => $"AiSecretKeyring({State}, keys={KeyCount})";
}
