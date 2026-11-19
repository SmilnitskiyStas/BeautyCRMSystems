using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Infrastructure;

public static class AuthResultMapping
{
    public static IActionResult ToAuthError(this ControllerBase c, AuthError e)
    {
        object body = new ApiError(e.Code, e.Message);
        if (e.Extras is { Count: > 0 })
        {
            var map = new Dictionary<string, object?> { ["code"] = e.Code, ["message"] = e.Message };
            foreach (var (k, v) in e.Extras) map[k] = v;
            body = map;
        }
        return e.Kind switch
        {
            AuthErrorKind.Unauthorized => c.Unauthorized(body),
            AuthErrorKind.Forbidden => c.StatusCode(StatusCodes.Status403Forbidden, body),
            AuthErrorKind.NotFound => c.NotFound(body),
            AuthErrorKind.Conflict => c.Conflict(body),
            AuthErrorKind.Locked => c.StatusCode(StatusCodes.Status423Locked, body),
            AuthErrorKind.Gone => c.StatusCode(StatusCodes.Status410Gone, body),
            _ => c.UnprocessableEntity(body),
        };
    }

    public static IActionResult ToResult<T>(this ControllerBase c, AuthResult<T> r, Func<T, IActionResult>? ok = null) =>
        r.IsOk ? (ok is null ? c.Ok(r.Value) : ok(r.Value!)) : c.ToAuthError(r.Error!);
}
