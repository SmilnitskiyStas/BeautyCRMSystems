using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Маркетингова згода в картці клієнта (контракт §26.11.2, ADR-015): модуль beauty_clients, owner/admin (specialist -> 403 forbidden_role до
/// читання тіла). Працює й за <c>Marketing:Enabled=false</c> (зниження/відкликання доступне завжди). Відповіді містять ПД: no-store.
/// Tenant лише з токена; чужий або видалений клієнт -> 404.
/// </summary>
[ApiController]
[Route("api/beauty/clients/{clientId:guid}/marketing-consent")]
[RequireModule("beauty_clients")]
[Authorize(Policy = AuthPolicies.Staff)] // автентифікація; роль перевіряє ReminderRolesAttribute, щоб віддати {code: forbidden_role}
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class MarketingConsentController(MarketingConsentService service) : ControllerBase
{
    private const int BodyLimit = 2 * 1024;

    [HttpGet]
    [ProducesResponseType<MarketingConsentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid clientId, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.GetAsync(actor, clientId, ct)) : Unauthorized();

    /// <summary>
    /// 404 client_not_found; 422 invalid_level | channel_unsupported | basis_required | invalid_basis | invalid_basis_note;
    /// 409 admin_grant_disabled (підвищення за <c>Marketing:AdminGrantEnabled=false</c>).
    /// </summary>
    [HttpPut]
    [RequestSizeLimit(BodyLimit)]
    [ProducesResponseType<MarketingConsentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Put(Guid clientId, [FromBody] PutMarketingConsentRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.PutAsync(actor, clientId, request, ct)) : Unauthorized();
}
