using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Заявки «співпраця / покращити / не працює» до провайдера (§27.9, ADR-018). Будь-яка роль tenant (owner/admin/specialist,
/// політика Staff); від модулів не залежить; призупинений tenant -> 403 tenant_suspended. Tenant і користувач - лише з JWT.
/// Текст заявки не логується; відповіді Cache-Control: no-store.
/// </summary>
[ApiController]
[Route("api/beauty")]
[Authorize(Policy = AuthPolicies.Staff)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class PlatformFeedbackController(PlatformFeedbackService feedback) : ControllerBase
{
    private const int BodyLimit = 16 * 1024;

    /// <summary>202 { id }; 422 invalid_type | invalid_severity | invalid_message | invalid_page_url; 429 rate_limited + Retry-After; 403 tenant_suspended.</summary>
    [HttpPost("feedback")]
    [RequestSizeLimit(BodyLimit)]
    [ProducesResponseType<FeedbackAcceptedDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] SubmitFeedbackRequest request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        var result = await feedback.SubmitAsync(actor, request, ct);
        if (result.Error is { Kind: ErrorKind.TooManyRequests } limited)
        {
            Response.Headers.RetryAfter = (limited.Conflicts as int? ?? 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError(limited.Code, limited.Message));
        }
        return this.ToResult(result, dto => StatusCode(StatusCodes.Status202Accepted, dto));
    }
}
