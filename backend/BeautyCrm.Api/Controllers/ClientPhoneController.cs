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
[Authorize(Policy = AuthPolicies.Staff)] // автентифікація; роль перевіряє ReminderRolesAttribute (код forbidden_role)
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ClientPhoneController(ClientPhoneService service) : ControllerBase
{
    /// <summary>accept: phone = phone_pending (джерело й згода переходять), pending очищується; reject: pending очищується.</summary>
    [HttpPost]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<PhonePendingResolvedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Resolve(Guid clientId, [FromBody] PhonePendingRequest? request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.ResolvePendingAsync(actor, clientId, request?.Action, ct)) : Unauthorized();
}
