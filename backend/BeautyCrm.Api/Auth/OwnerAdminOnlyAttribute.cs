using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Auth;

/// <summary>
/// Security-review L5 (§32.16): рольовий фільтр owner/admin для зміни норм матеріалів. Authorization-фільтр (Order після
/// <see cref="RequirePermissionAttribute"/>), тож спрацьовує ДО прив'язки тіла: deputy (навіть зі збереженим правом) отримує
/// <c>403 forbidden_role</c> і на некоректний JSON, а не 400. Сервісна перевірка ролі лишається другою лінією захисту.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OwnerAdminOnlyAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    public int Order => RequirePermissionAttribute.FilterOrder + 100;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var actor = context.HttpContext.User.ToActor();
        if (actor is null) { context.Result = new UnauthorizedResult(); return; }
        if (actor.Role is Roles.Owner or Roles.Admin) return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<BeautyCrm.Api.Controllers.InboxNoStoreAttribute>().Any())
            context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.Result = new ObjectResult(new ApiError("forbidden_role", "This action is not available for your role."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
