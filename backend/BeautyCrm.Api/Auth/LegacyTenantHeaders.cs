using System.Globalization;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Auth;

/// <summary>
/// §31.2.3 (Q-C3, errata ADR-024 §2.9): на кожну відповідь запиту входу з непорожнім <c>tenant</c> (2xx і 4xx) - заголовки
/// <c>Deprecation: @&lt;unix&gt;</c> (RFC 9745, <c>Auth:LegacyTenantLogin:DeprecatedAt</c>) і <c>Sunset: &lt;HTTP-date&gt;</c> (RFC 8594,
/// <c>SunsetAt</c>). Без <c>tenant</c> заголовків немає. Фільтр виконується раніше за автоматичну відповідь 422 на ModelState.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LegacyTenantHeadersAttribute : ActionFilterAttribute
{
    public LegacyTenantHeadersAttribute() => Order = int.MinValue;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var request = context.ActionArguments.Values.OfType<LoginRequest>().FirstOrDefault();
        if (request is null || string.IsNullOrWhiteSpace(request.Tenant)) return;

        var legacy = context.HttpContext.RequestServices.GetRequiredService<AuthOptions>().LegacyTenantLogin;
        var headers = context.HttpContext.Response.Headers;
        if (legacy.DeprecatedAt is { } deprecated)
            headers["Deprecation"] = "@" + deprecated.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        if (legacy.SunsetAt is { } sunset)
            headers["Sunset"] = sunset.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
    }
}
