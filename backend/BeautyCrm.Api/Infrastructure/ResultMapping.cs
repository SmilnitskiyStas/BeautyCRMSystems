using System.Security.Claims;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Infrastructure;

public sealed record ApiError(string Code, string Message);

public static class ResultMapping
{
    /// <summary>Типізовані помилки: 422 валідація, 404, 409 конфлікт (перетин слоту), 402 платіж.</summary>
    public static IActionResult ToError(this ControllerBase c, Error e)
    {
        var body = new ApiError(e.Code, e.Message);
        return e.Kind switch
        {
            ErrorKind.NotFound => c.NotFound(body),
            ErrorKind.Conflict => c.Conflict(body),
            ErrorKind.PaymentFailed => c.StatusCode(StatusCodes.Status402PaymentRequired, body),
            _ => c.UnprocessableEntity(body),
        };
    }

    public static IActionResult ToResult<T>(this ControllerBase c, Result<T> r, Func<T, IActionResult>? ok = null) =>
        r.IsOk ? (ok is null ? c.Ok(r.Value) : ok(r.Value!)) : c.ToError(r.Error!);

    /// <summary>Tenant гарантовано встановлений фільтром [RequireModule].</summary>
    public static Guid TenantId(this ControllerBase c) =>
        c.HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantId!.Value;

    public static Guid? UserId(this ControllerBase c) =>
        Guid.TryParse(c.User.FindFirst("sub")?.Value ?? c.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
