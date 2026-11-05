using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Tenancy;

/// <summary>
/// Глобальний фільтр (TASK-826, M3): запит із JWT користувача tenant (claim tenant_id) до не-публічного ендпоінта
/// отримує 403 <c>tenant_suspended</c>, якщо tenant призупинено, - навіть із ще чинним access-токеном (до 15 хв).
/// Пропускає [AllowAnonymous] (login/refresh/accept/публічний запис), запити без tenant-claim (X-Platform-Key,
/// ControlPlaneHmac, webhook) і tenant, якого не видно під RLS (нічого не поверне й так). Без кешу: один запит
/// до tenants за PK на запит, тож resume діє одразу.
/// </summary>
public sealed class TenantActiveFilter : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true) return;
        if (http.User.FindFirst(TenantMiddleware.ClaimType) is null) return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) return;

        var store = http.RequestServices.GetService<IAuthStore>();
        if (store is null || http.RequestServices.GetService<ITenantContext>()?.TenantId is null) return;

        var tenant = await store.GetTenantAsync(http.RequestAborted);
        if (tenant is { IsActive: false })
            context.Result = new ObjectResult(new ApiError("tenant_suspended", "Tenant is suspended.")) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
