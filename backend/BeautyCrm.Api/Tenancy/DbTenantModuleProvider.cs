using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Api.Tenancy;

/// <summary>
/// Модулі з tenants.modules (читається під RLS власного tenant). Призупинений tenant — усі модулі вимкнені,
/// тож його ще чинні access-токени втрачають доступ до beauty-ендпоінтів одразу, а не після закінчення терміну.
/// </summary>
public sealed class DbTenantModuleProvider(IAuthStore store) : ITenantModuleProvider
{
    public async Task<bool> IsEnabledAsync(Guid tenantId, string module, CancellationToken ct)
    {
        var tenant = await store.GetTenantAsync(ct);
        return tenant is { IsActive: true } && tenant.Id == tenantId
            && tenant.Modules.Contains(module, StringComparer.OrdinalIgnoreCase);
    }
}
