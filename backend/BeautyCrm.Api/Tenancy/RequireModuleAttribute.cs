using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Tenancy;

/// <summary>Модулі, увімкнені для tenant (аналог tenants.modules монолітного репозиторію).</summary>
public interface ITenantModuleProvider
{
    Task<bool> IsEnabledAsync(Guid tenantId, string module, CancellationToken ct);
}

/// <summary>
/// Тимчасова реалізація: Beauty:EnabledModules (масив) для всіх tenant; не задано — усі модулі ввімкнені.
/// Підмінити реєстрацією, коли з'явиться сховище tenants.modules.
/// </summary>
public sealed class ConfigTenantModuleProvider(IConfiguration config) : ITenantModuleProvider
{
    public Task<bool> IsEnabledAsync(Guid tenantId, string module, CancellationToken ct)
    {
        var enabled = config.GetSection("Beauty:EnabledModules").Get<string[]>();
        return Task.FromResult(enabled is null or { Length: 0 } || enabled.Contains(module, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 401 без tenant, 403 якщо модуль вимкнений для tenant. Authorization-фільтр з Order = -3100 (ADR-023, §30.5 п.7):
/// після <see cref="TenantActiveFilter"/> (-3200) і ДО <c>[RequirePermission]</c> (-3000), тіло запиту ще не прив'язане.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireModuleAttribute(string module) : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    public const int FilterOrder = -3100;

    public string Module { get; } = module;

    public int Order => FilterOrder;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var tenantId = services.GetRequiredService<ITenantContext>().TenantId;
        if (tenantId is null)
        {
            context.Result = new UnauthorizedObjectResult(new { code = "tenant_required", message = "Tenant is not resolved." });
            return;
        }
        var modules = services.GetRequiredService<ITenantModuleProvider>();
        if (!await modules.IsEnabledAsync(tenantId.Value, Module, context.HttpContext.RequestAborted))
        {
            context.Result = new ObjectResult(new { code = "module_disabled", message = $"Module '{Module}' is not enabled." }) { StatusCode = 403 };
        }
    }
}
