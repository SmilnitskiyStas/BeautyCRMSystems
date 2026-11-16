using BeautyCrm.Api.Auth;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// M3 (§30.6.8): на всіх <c>/clients/{id}/**</c> клієнт поза scope <c>clients.view</c> дає <c>404 client_not_found</c> (не 403):
/// існування чужого клієнта не розкривається. Повертає <c>null</c>, якщо доступ дозволено.
/// </summary>
public static class ClientScopeGuard
{
    public static async Task<IActionResult?> OutOfScopeAsync(this ControllerBase c, IClientScope scope, Guid clientId, CancellationToken ct)
    {
        if (c.User.ToActor() is not { } actor) return c.Unauthorized();
        return await scope.InScopeAsync(actor, clientId, ct)
            ? null
            : c.NotFound(new ApiError("client_not_found", "Client not found."));
    }
}
