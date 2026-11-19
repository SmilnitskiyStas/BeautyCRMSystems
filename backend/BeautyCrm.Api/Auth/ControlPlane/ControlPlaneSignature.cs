using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace BeautyCrm.Api.Auth.ControlPlane;

/// <summary>
/// Підпис запиту ControlPlaneHmac v1 (beauty-contracts.md §27.2, ADR-018). Чиста логіка без I/O:
/// той самий формат використовує вихідна доставка (worker) і клієнт ShelfGuard; тестовий вектор —
/// .ai/artifacts/contracts/ADR-018-hmac-test-vector.json.
/// </summary>
public static class ControlPlaneSignature
{
    public const string Prefix = "BCRM-HMAC-SHA256-v1";
    public const string SignaturePrefix = "v1=";
    public const int MaxBodyBytes = 64 * 1024;
    public const int WindowSeconds = 300;
    public const int MinKeyBytes = 32;
    public const int NonceTtlMinutes = 15;

    public const string KeyIdHeader = "X-BCRM-Key-Id";
    public const string TimestampHeader = "X-BCRM-Timestamp";
    public const string NonceHeader = "X-BCRM-Nonce";
    public const string SignatureHeader = "X-BCRM-Signature";
    public const string RequestIdHeader = "X-Request-Id";
    public const string ServerTimeHeader = "X-BCRM-Server-Time";

    public static readonly Regex KidFormat = new("^[a-z0-9-]{3,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Unix-секунди: десяткове ціле без знака й провідних нулів.
    public static readonly Regex TimestampFormat = new("^(0|[1-9][0-9]{0,11})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static readonly Regex NonceFormat = new("^[A-Za-z0-9_-]{22,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 43 символи base64url без padding = 32 байти MAC.
    public static readonly Regex SignatureFormat = new("^v1=[A-Za-z0-9_-]{43}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsAllowedMethod(string method) => method is "GET" or "POST" or "PUT";

    public static string BodySha256Hex(ReadOnlySpan<byte> body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    /// <summary>Canonical string: рядки через LF, без кінцевого переводу рядка, UTF-8. Query у v1 завжди порожній.</summary>
    public static string Canonical(string kid, string timestamp, string nonce, string method, string path, string query, ReadOnlySpan<byte> body) =>
        string.Join('\n', Prefix, kid, timestamp, nonce, method, path, query, BodySha256Hex(body));

    /// <summary>Заголовок підпису: <c>v1=&lt;base64url без padding від HMAC-SHA256(key, canonical)&gt;</c>. Ключ — сирі байти секрету.</summary>
    public static string Sign(ReadOnlySpan<byte> key, string canonical) =>
        SignaturePrefix + WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));

    /// <summary>
    /// Порівняння підписів за сталий час (<see cref="CryptographicOperations.FixedTimeEquals"/>). Різна довжина = false
    /// (довжина підпису формату v1 фіксована й не секретна).
    /// </summary>
    public static bool SignaturesEqual(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));

    /// <summary>Перші 12 hex від sha256(сирі байти ключа): єдине похідне від ключа значення, дозволене в БД/аудиті/логах.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> key) => Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..12];
}
