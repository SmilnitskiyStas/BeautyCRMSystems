using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Tenancy;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// TASK-790 (L4): модуль <c>beauty_marketing</c> поточного tenant (tenants.modules, активний tenant). Перевіряється поза HTTP-фільтром:
/// у чат-конвеєрі (пропозиція згоди, /marketing, купони) і в <see cref="CampaignService"/>. Один запит на scope (кеш).
/// Без <see cref="IAuthStore"/> (хост без auth) модуль вважається ввімкненим; tenant не встановлено -> вимкнено.
/// </summary>
public sealed class MarketingModuleGate(TenantContext tenant, IAuthStore? store = null)
{
    public const string Module = "beauty_marketing";
    private bool? _cached;
    private Guid? _cachedFor;

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId) return false;
        if (_cached is { } known && _cachedFor == tenantId) return known;
        var on = store is null
            || await store.GetTenantAsync(ct) is { IsActive: true } t && t.Id == tenantId && t.Modules.Contains(Module, StringComparer.OrdinalIgnoreCase);
        _cached = on;
        _cachedFor = tenantId;
        return on;
    }
}
