using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Infrastructure;

public static class AuthResultMapping
{
    public static IActionResult ToAuthError(this ControllerBase c, AuthError e)
    {
        var body = new ApiError(e.Code, e.Message);
        return e.Kind switch
        {
            AuthErrorKind.Unauthorized => c.Unauthorized(body),
            AuthErrorKind.Forbidden => c.StatusCode(StatusCodes.Status403Forbidden, body),
            AuthErrorKind.NotFound => c.NotFound(body),
            AuthErrorKind.Conflict => c.Conflict(body),
            AuthErrorKind.Locked => c.StatusCode(StatusCodes.Status423Locked, body),
            _ => c.UnprocessableEntity(body),
        };
    }

    public static IActionResult ToResult<T>(this ControllerBase c, AuthResult<T> r, Func<T, IActionResult>? ok = null) =>
        r.IsOk ? (ok is null ? c.Ok(r.Value) : ok(r.Value!)) : c.ToAuthError(r.Error!);
}
