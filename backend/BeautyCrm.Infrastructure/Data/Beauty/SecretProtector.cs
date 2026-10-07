using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>Шифрування секретів каналів у спокої (beauty_channels.credentials_encrypted).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>
/// AES-256-GCM; формат base64(nonce[12] | tag[16] | ciphertext). Ключ: Channels:EncryptionKey
/// (env Channels__EncryptionKey), base64 від 32 байт. Без ключа — fail closed (виняток при першому використанні).
/// </summary>
public sealed class AesGcmSecretProtector(IConfiguration config) : ISecretProtector
{
    private const int NonceSize = 12, TagSize = 16;
    private byte[]? _key;

    private byte[] Key => _key ??= LoadKey();

    private byte[] LoadKey()
    {
        var b64 = config["Channels:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException("Channels:EncryptionKey is not configured (env Channels__EncryptionKey, base64 of 32 bytes).");
        var key = Convert.FromBase64String(b64);
        if (key.Length != 32) throw new InvalidOperationException("Channels:EncryptionKey must be 32 bytes (base64).");
        return key;
    }

    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedValue)
    {
        var all = Convert.FromBase64String(protectedValue);
        var nonce = all[..NonceSize];
        var tag = all[NonceSize..(NonceSize + TagSize)];
        var cipher = all[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
