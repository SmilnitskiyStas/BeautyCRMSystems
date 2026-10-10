using System.Security.Claims;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Infrastructure;

/// <param name="Conflicts">Лише для 409 зі списком блокувальників (§17 `has_appointments_on_closed_days`); інакше поле відсутнє.</param>
public sealed record ApiError(string Code, string Message,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    object? Conflicts = null);

public static class ResultMapping
{
    /// <summary>Типізовані помилки: 422 валідація, 404, 409 конфлікт (перетин слоту), 402 платіж.</summary>
    public static IActionResult ToError(this ControllerBase c, Error e)
    {
        var body = new ApiError(e.Code, e.Message, e.Conflicts);
        return e.Kind switch
        {
            ErrorKind.NotFound => c.NotFound(body),
            ErrorKind.Conflict => c.Conflict(body),
            ErrorKind.Forbidden => c.StatusCode(StatusCodes.Status403Forbidden, body),
            ErrorKind.PaymentFailed => c.StatusCode(StatusCodes.Status402PaymentRequired, body),
            _ => c.UnprocessableEntity(body),
        };
    }

    public static IActionResult ToResult<T>(this ControllerBase c, Result<T> r, Func<T, IActionResult>? ok = null) =>
        r.IsOk ? (ok is null ? c.Ok(r.Value) : ok(r.Value!)) : c.ToError(r.Error!);

    /// <summary>201 для щойно створеного запрошення: одноразовий токен у тілі не має кешуватися (Cache-Control: no-store).</summary>
    public static IActionResult InviteCreated<T>(this ControllerBase c, T body)
    {
        c.Response.Headers.CacheControl = "no-store";
        return c.StatusCode(StatusCodes.Status201Created, body);
    }

    /// <summary>Tenant гарантовано встановлений фільтром [RequireModule].</summary>
    public static Guid TenantId(this ControllerBase c) =>
        c.HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantId!.Value;

    public static Guid? UserId(this ControllerBase c) =>
        Guid.TryParse(c.User.FindFirst("sub")?.Value ?? c.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
