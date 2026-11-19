using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Прив'язка Telegram власника для сповіщень складу (етап 2b, ADR-025 §12, §32.24.8): модуль beauty_inventory, ЛИШЕ owner (фільтр до прив'язки тіла),
/// дія завжди над власним user_id з JWT (userId у запиті немає). Лише маршрутизація: логіка в <see cref="StaffTelegramLinkService"/>. no-store.
/// </summary>
[ApiController]
[Route("api/beauty/inventory/telegram/link")]
[RequireModule(BeautyModules.Inventory)]
[Authorize(Policy = AuthPolicies.Staff)]
[OwnerOnly]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class InventoryTelegramLinkController(StaffTelegramLinkService links, InventoryThrottle throttle) : ControllerBase
{
    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
    }

    [HttpGet]
    [ProducesResponseType<TelegramLinkStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (User.ToActor() is not { } a) return Unauthorized();
        if (!throttle.TryAcquire(a.TenantId, a.UserId)) return RateLimited();
        return this.ToResult(await links.GetStatusAsync(a, ct));
    }

    /// <summary>403 telegram_alerts_disabled; 409 telegram_already_linked; 422 consent_required | consent_version_outdated | telegram_channel_unavailable | telegram_channel_required; 404 channel_not_found; 503 telegram_bot_unavailable; 429 rate_limited.</summary>
    [HttpPost]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType<TelegramLinkIssuedDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Issue([FromBody] IssueTelegramLinkRequest? request, CancellationToken ct)
    {
        if (User.ToActor() is not { } a) return Unauthorized();
        var result = await links.IssueAsync(a, request, ct);
        if (!result.IsOk && result.Error!.Code == "rate_limited") Response.Headers.RetryAfter = "3600";
        return this.ToResult(result, dto => StatusCode(StatusCodes.Status201Created, dto));
    }

    /// <summary>Ідемпотентно: закриває issued|claimed|active; працює й за вимкненого прапора.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(CancellationToken ct)
    {
        if (User.ToActor() is not { } a) return Unauthorized();
        if (!throttle.TryAcquire(a.TenantId, a.UserId)) return RateLimited();
        return this.ToResult(await links.RevokeAsync(a, ct), _ => NoContent());
    }
}
