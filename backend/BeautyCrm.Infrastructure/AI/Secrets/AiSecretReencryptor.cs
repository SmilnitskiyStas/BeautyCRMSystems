using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.AI.Secrets;

/// <summary>Рядок зі збереженим ключем провайдера (лише те, що потрібно перешифруванню).</summary>
public sealed record AiSecretRow(string Provider, string KeyEncrypted)
{
    public override string ToString() => "AiSecretRow";
}

/// <summary>
/// Порт сховища для перешифрування. Операції по рядках виконуються під <c>app.tenant_id</c> цього tenant (RLS не обходиться);
/// лише перелік tenant-ів потребує привілейованого з'єднання.
/// </summary>
public interface IAiSecretRowStore
{
    Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct);
    Task<IReadOnlyList<AiSecretRow>> ListRowsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>CAS: оновлює лише якщо шифротекст досі дорівнює <paramref name="expectedOld"/>. false = рядок змінився/зник.</summary>
    Task<bool> ReplaceAsync(Guid tenantId, string provider, string expectedOld, string newValue, string newKeyId, CancellationToken ct);

    /// <summary>CAS-видалення нечитаного рядка (--drop-unreadable).</summary>
    Task<bool> DeleteAsync(Guid tenantId, string provider, string expectedOld, CancellationToken ct);
}

public sealed record AiReencryptOptions(Guid? OnlyTenantId = null, bool DropUnreadable = false);

/// <summary>Звіт лише кількостями: без значень, провайдерів чи tenant id.</summary>
public sealed record AiReencryptReport(
    int Tenants, int Scanned, int Reencrypted, int AlreadyCurrent, int Unreadable, int Dropped, int Conflicts, int FailedTenants)
{
    /// <summary>Рядків, що лишились не на активному kid (нечитані, не видалені; конфлікти).</summary>
    public int Remaining => Unreadable - Dropped + Conflicts;
    public bool Complete => Remaining == 0 && FailedTenants == 0;
}

/// <summary>
/// Перешифрування ключів ШІ активним kid (ротація, ADR-010 §2.5). Ідемпотентне: рядки вже на активному kid пропускаються.
/// Нечитані рядки не змінюються (якщо не задано DropUnreadable).
/// </summary>
public sealed class AiSecretReencryptor(IAiSecretProtector protector, IAiSecretRowStore store, ILogger<AiSecretReencryptor> log)
{
    public async Task<AiReencryptReport> RunAsync(AiReencryptOptions options, CancellationToken ct)
    {
        if (protector.State != AiSecretsState.Ready || protector.ActiveKeyId is null)
            throw new AiSecretException(AiSecretError.SecretsUnavailable);

        IReadOnlyList<Guid> tenants = options.OnlyTenantId is { } one ? [one] : await store.ListTenantIdsAsync(ct);
        int scanned = 0, done = 0, current = 0, unreadable = 0, dropped = 0, conflicts = 0, failed = 0;
        var active = protector.ActiveKeyId;

        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var row in await store.ListRowsAsync(tenantId, ct))
                {
                    scanned++;
                    if (protector.TryGetKeyId(row.KeyEncrypted) == active) { current++; continue; }

                    string plain;
                    try
                    {
                        plain = protector.Unprotect(row.KeyEncrypted, tenantId, row.Provider);
                    }
                    catch (AiSecretException)
                    {
                        unreadable++;
                        if (options.DropUnreadable)
                        {
                            if (await store.DeleteAsync(tenantId, row.Provider, row.KeyEncrypted, ct)) dropped++;
                            else conflicts++;
                        }
                        continue;
                    }

                    var fresh = protector.Protect(plain, tenantId, row.Provider);
                    if (await store.ReplaceAsync(tenantId, row.Provider, row.KeyEncrypted, fresh, active, ct)) done++;
                    else conflicts++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                log.LogError("AI secrets re-encryption failed for one tenant ({ErrorType}).", ex.GetType().Name);
            }
        }

        return new AiReencryptReport(tenants.Count, scanned, done, current, unreadable, dropped, conflicts, failed);
    }
}
