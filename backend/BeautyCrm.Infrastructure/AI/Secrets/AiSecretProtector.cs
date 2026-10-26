using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BeautyCrm.Infrastructure.AI.Secrets;

public enum AiSecretError
{
    SecretsUnavailable,
    InvalidArgument,
    MalformedCiphertext,
    UnknownKeyId,
    DecryptionFailed,
}

/// <summary>
/// Помилка протектора ключів ШІ. Повідомлення фіксоване за кодом: у ньому (і в InnerException) немає ні ключа, ні
/// шифротексту, ні tenant/provider.
/// </summary>
public sealed class AiSecretException(AiSecretError error)
    : Exception($"AI secret operation failed: {error}.")
{
    public AiSecretError Error { get; } = error;
}

/// <summary>Шифрування ключів провайдерів ШІ (ADR-010 §2.5, контракт §20.8). Окремо від <c>ISecretProtector</c> каналів.</summary>
public interface IAiSecretProtector
{
    AiSecretsState State { get; }
    string? ActiveKeyId { get; }

    /// <summary>Шифрує активним kid. AAD = beauty_ai_key|{tenantId}|{provider}.</summary>
    string Protect(string plaintext, Guid tenantId, string provider);

    /// <summary>Розшифровує за kid із шифротексту. Інший tenant/provider, підміна kid чи пошкодження дають <see cref="AiSecretException"/>.</summary>
    string Unprotect(string protectedValue, Guid tenantId, string provider);

    /// <summary>kid із формату v2 або null, якщо значення не v2.</summary>
    string? TryGetKeyId(string protectedValue);
}

/// <summary>
/// AES-256-GCM; формат <c>v2:{kid}:{base64(nonce[12] | tag[16] | ciphertext)}</c> (порядок як в ADR-010 §2.5 і
/// чинному <c>AesGcmSecretProtector</c>). Розшифрований текст не кешується.
/// </summary>
public sealed partial class AesGcmAiSecretProtector(AiSecretKeyring keyring) : IAiSecretProtector
{
    private const string Version = "v2";
    private const int NonceSize = 12, TagSize = 16, MaxPlaintextBytes = 4096;

    [GeneratedRegex("^[a-z][a-z0-9_]{1,31}$")]
    private static partial Regex ProviderRegex();

    public AiSecretsState State => keyring.State;
    public string? ActiveKeyId => keyring.ActiveKeyId;

    public string Protect(string plaintext, Guid tenantId, string provider)
    {
        var aad = BuildAad(tenantId, provider);
        var data = string.IsNullOrEmpty(plaintext) ? [] : Encoding.UTF8.GetBytes(plaintext);
        if (data.Length is 0 or > MaxPlaintextBytes) throw new AiSecretException(AiSecretError.InvalidArgument);
        var kid = keyring.ActiveKeyId;
        if (kid is null || !keyring.TryGetKey(kid, out var key)) throw new AiSecretException(AiSecretError.SecretsUnavailable);

        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[data.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, data, cipher, tag, aad);
            return $"{Version}:{kid}:{Convert.ToBase64String([.. nonce, .. tag, .. cipher])}";
        }
        catch (CryptographicException)
        {
            throw new AiSecretException(AiSecretError.DecryptionFailed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string Unprotect(string protectedValue, Guid tenantId, string provider)
    {
        var aad = BuildAad(tenantId, provider);
        if (!TryParse(protectedValue, out var kid, out var payload)) throw new AiSecretException(AiSecretError.MalformedCiphertext);
        if (!keyring.IsReady) throw new AiSecretException(AiSecretError.SecretsUnavailable);
        if (!keyring.TryGetKey(kid, out var key)) throw new AiSecretException(AiSecretError.UnknownKeyId);

        var all = new byte[payload.Length];
        if (!Convert.TryFromBase64String(payload, all, out var n) || n <= NonceSize + TagSize)
            throw new AiSecretException(AiSecretError.MalformedCiphertext);

        var plain = new byte[n - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(all.AsSpan(0, NonceSize), all.AsSpan(NonceSize + TagSize, plain.Length),
                all.AsSpan(NonceSize, TagSize), plain, aad);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            throw new AiSecretException(AiSecretError.DecryptionFailed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string? TryGetKeyId(string protectedValue) => TryParse(protectedValue, out var kid, out _) ? kid : null;

    private static bool TryParse(string? value, out string kid, out string payload)
    {
        kid = payload = "";
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split(':');
        if (parts.Length != 3 || parts[0] != Version || !AiSecretKeyring.IsValidKid(parts[1]) || parts[2].Length == 0) return false;
        kid = parts[1];
        payload = parts[2];
        return true;
    }

    private static byte[] BuildAad(Guid tenantId, string provider)
    {
        if (tenantId == Guid.Empty || provider is null || !ProviderRegex().IsMatch(provider))
            throw new AiSecretException(AiSecretError.InvalidArgument);
        return Encoding.UTF8.GetBytes($"beauty_ai_key|{tenantId:D}|{provider}");
    }
}
