using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Api.Payments.Monobank;

/// <summary>
/// Перевірка X-Sign вебхука: ECDSA (P-256) + SHA-256 над сирим тілом, підпис - base64 DER (api.monobank.ua/docs/acquiring.html).
/// Ключ кешується; якщо перевірка не пройшла - ключ перезапитується не частіше разу на хвилину (monobank радить саме так при ротації).
/// </summary>
public sealed class MonobankSignature(MonobankClient api, TimeProvider clock, ILogger<MonobankSignature> log)
{
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private ECDsa? key;
    private DateTimeOffset lastFetch = DateTimeOffset.MinValue;

    public async Task<bool> VerifyAsync(byte[] body, string? xSign, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(xSign)) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(xSign.Trim()); }
        catch (FormatException) { return false; }

        var current = key ?? await RefreshAsync(force: false, ct);
        if (current is not null && Verify(current, body, sig)) return true;
        var fresh = await RefreshAsync(force: true, ct);
        return fresh is not null && !ReferenceEquals(fresh, current) && Verify(fresh, body, sig);
    }

    public static bool Verify(ECDsa k, byte[] body, byte[] sig)
    {
        try { return k.VerifyData(body, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Base64(PEM) -> ключ. Невалідний формат = виняток (ключ від провайдера зіпсований).</summary>
    public static ECDsa ImportKey(string base64Pem)
    {
        var pem = Encoding.UTF8.GetString(Convert.FromBase64String(base64Pem.Trim()));
        var k = ECDsa.Create();
        k.ImportFromPem(pem);
        return k;
    }

    private async Task<ECDsa?> RefreshAsync(bool force, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (key is not null && (!force || now - lastFetch < RefreshCooldown)) return key;
            if (key is null && now - lastFetch < TimeSpan.FromSeconds(5)) return null; // невдалий запит щойно - не довбати API
            lastFetch = now;
            try
            {
                key = ImportKey(await api.GetPublicKeyAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("monobank_pubkey_failed error={Error}", ex.GetType().Name);
            }
            return key;
        }
        finally
        {
            gate.Release();
        }
    }
}
