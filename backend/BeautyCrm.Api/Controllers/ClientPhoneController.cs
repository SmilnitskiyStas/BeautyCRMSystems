using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Рішення адміністратора щодо номера, наданого клієнтом у чаті (контракт §25.7, ADR-014 ч. B): owner/admin
/// (specialist -> 403 forbidden_role до читання тіла). Відповіді містять ПД: Cache-Control: no-store. Tenant лише з токена;
/// чужий або видалений клієнт -> 404 client_not_found.
/// </summary>
[ApiController]
[Route("api/beauty/clients/{clientId:guid}/phone-pending")]
[RequireModule("beauty_clients")]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.ClientsEdit, PermissionLevels.Own)]
[RequirePermission(PermissionKeys.ClientsContacts, PermissionLevels.Full)] // рішення розкриває номер -> лише з повним доступом до контактів
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ClientPhoneController(ClientPhoneService service, IClientScope scope) : ControllerBase
{
    /// <summary>accept: phone = phone_pending (джерело й згода переходять), pending очищується; reject: pending очищується.</summary>
    [HttpPost]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<PhonePendingResolvedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Resolve(Guid clientId, [FromBody] PhonePendingRequest? request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        // поза scope clients.view -> 404 client_not_found (існування не розкривається)
        if (!await scope.InScopeAsync(actor, clientId, ct)) return NotFound(new ApiError("client_not_found", "Client not found."));
        return this.ToResult(await service.ResolvePendingAsync(actor, clientId, request?.Action, ct));
    }
}
