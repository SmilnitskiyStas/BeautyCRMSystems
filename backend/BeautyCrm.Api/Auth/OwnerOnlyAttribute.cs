using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Auth;

/// <summary>
/// Рольовий фільтр «лише owner» (етап 2b складу, §32.24.8; патерн <see cref="OwnerAdminOnlyAttribute"/>, L5/§32.26 D9). Authorization-фільтр
/// (Order після <see cref="RequirePermissionAttribute"/>), тож спрацьовує ДО прив'язки тіла: admin/deputy/specialist отримують
/// <c>403 forbidden_role</c> і на некоректний JSON.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OwnerOnlyAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    public int Order => RequirePermissionAttribute.FilterOrder + 100;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var actor = context.HttpContext.User.ToActor();
        if (actor is null) { context.Result = new UnauthorizedResult(); return; }
        if (actor.Role == Roles.Owner) return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<BeautyCrm.Api.Controllers.InboxNoStoreAttribute>().Any())
            context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.Result = new ObjectResult(new ApiError("forbidden_role", "This action is available only to the owner."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
