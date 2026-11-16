using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Auth;

/// <summary>
/// ADR-023, контракт §30.5 п.8: <c>[RequirePermission(key, minLevel)]</c>. Authorization-фільтр з Order = -3000 (після
/// <c>TenantActiveFilter</c> -3200 і <c>[RequireModule]</c> -3100; тіло запиту не читається, 422 не повертається). AND-семантика
/// за кількох атрибутів. Відмова: <c>403 module_disabled</c>, якщо права немає лише через вимкнений модуль; інакше
/// <c>403 {code: forbidden_role, message, permission, required}</c>. Права береться з <see cref="IAccessContext"/> (немає
/// контексту = <see cref="AccessSet.None"/>, fail-closed). Невідомий ключ чи рівень кидає виняток під час створення атрибута.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    public const int FilterOrder = -3000;

    public RequirePermissionAttribute(string key, string level)
    {
        PermissionCatalog.Validate(key, level);
        Key = key;
        Level = level;
    }

    public string Key { get; }
    public string Level { get; }
    public int Order => FilterOrder;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) return Task.CompletedTask;

        var ctx = http.RequestServices.GetService<IAccessContext>();
        var access = ctx is { IsLoaded: true } ? ctx.Access : AccessSet.None;
        if (access.AtLeast(Key, Level)) return Task.CompletedTask;

        // відмова коротко замикає конвеєр: result-фільтри (InboxNoStore) не виконуються, тож заголовок ставимо тут
        if (context.ActionDescriptor.EndpointMetadata.OfType<BeautyCrm.Api.Controllers.InboxNoStoreAttribute>().Any())
            http.Response.Headers.CacheControl = "no-store";

        if (access.BlockedByModule(Key, Level))
        {
            context.Result = new ObjectResult(new ApiError("module_disabled", "The module for this action is not enabled."))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return Task.CompletedTask;
        }

        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("BeautyCrm.Access");
        logger?.LogWarning("access_denied permission={Permission} required={Required} user={User} route={Route}",
            Key, Level, http.User.FindFirst("sub")?.Value, (context.ActionDescriptor.AttributeRouteInfo?.Template) ?? http.Request.Path.Value);

        context.Result = new ObjectResult(new
        {
            code = "forbidden_role",
            message = "This action is not available for your access level.",
            permission = Key,
            required = Level,
        })
        { StatusCode = StatusCodes.Status403Forbidden };
        return Task.CompletedTask;
    }
}

/// <summary>
/// Виняток із глобального <see cref="ActiveSpecialistFilter"/> (M7): <c>GET /auth/me</c>, <c>GET /access/catalog</c>, <c>POST /feedback</c>
/// доступні користувачу з неактивним профілем майстра.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowInactiveSpecialistAttribute : Attribute;
