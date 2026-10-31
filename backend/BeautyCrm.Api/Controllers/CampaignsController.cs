using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyMarketing;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Кампанії розсилок (§26.11.3, ADR-015): модуль beauty_marketing + Marketing:Enabled (інакше 403 marketing_disabled; виняток TASK-790: GET кампаній/stats/summary і cancel доступні й без прапорця), лише owner/admin
/// (specialist -> 403 forbidden_role до прив'язки тіла). Відповіді - Cache-Control: no-store (також 4xx). Чужий id -> 404.
/// </summary>
[ApiController]
[Route("api/beauty")]
[RequireModule("beauty_marketing")]
[RequireMarketingEnabled]
[Authorize(Policy = AuthPolicies.Staff)]
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class CampaignsController(CampaignService service) : ControllerBase
{
    private const int CampaignBody = 16 * 1024, SmallBody = 2 * 1024;

    private IActionResult Map<T>(Result<T> r, Func<T, IActionResult>? ok = null)
    {
        if (!r.IsOk && r.Error!.Kind == ErrorKind.TooManyRequests) Response.Headers.RetryAfter = "60";
        return this.ToResult(r, ok);
    }

    /// <summary>Лише кількості й розбивка виключень, без ПД. 422 invalid_type | invalid_segment; 404 promotion_not_found | service_not_found | location_not_found; 429.</summary>
    [HttpPost("marketing/audience/preview")]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<AudiencePreviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AudiencePreview([FromBody] AudiencePreviewRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.PreviewAudienceAsync(actor, request, ct)) : Unauthorized();

    [HttpGet("campaigns")]
    [AllowWhenMarketingDisabled]
    [ProducesResponseType<CampaignListDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.ListAsync(actor, status, cursor, take, ct)) : Unauthorized();

    /// <summary>422: invalid_name | invalid_type | promotion_required | promotion_not_found | promotion_not_code_only | promotion_must_be_public | invalid_segment | service_not_found | location_not_found | invalid_template | invalid_coupon_mode | coupon_not_found | coupon_not_shared | coupon_promotion_mismatch | coupon_period_outside_promotion.</summary>
    [HttpPost("campaigns")]
    [RequestSizeLimit(CampaignBody)]
    [ProducesResponseType<CampaignDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CampaignRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? Map(await service.CreateAsync(actor, request, ct), d => Created($"/api/beauty/campaigns/{d.Id}", d))
            : Unauthorized();

    [HttpGet("campaigns/{id:guid}")]
    [AllowWhenMarketingDisabled]
    [ProducesResponseType<CampaignDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.GetAsync(actor, id, ct)) : Unauthorized();

    /// <summary>Повна заміна чернетки (revision + 1); не чернетка -> 409 campaign_not_draft.</summary>
    [HttpPut("campaigns/{id:guid}")]
    [RequestSizeLimit(CampaignBody)]
    [ProducesResponseType<CampaignDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Put(Guid id, [FromBody] CampaignRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.UpdateAsync(actor, id, request, ct)) : Unauthorized();

    [HttpDelete("campaigns/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.DeleteAsync(actor, id, ct), _ => NoContent()) : Unauthorized();

    /// <summary>Перегляд чернетки: revision для підтвердження, приклад тексту, кількості, ліміти, попередження. Доступний і через GET (§26.11.3), і через POST.</summary>
    [HttpGet("campaigns/{id:guid}/preview")]
    [HttpPost("campaigns/{id:guid}/preview")]
    [ProducesResponseType<CampaignPreviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.PreviewAsync(actor, id, ct)) : Unauthorized();

    /// <summary>
    /// Одна атомарна дія. Заголовок <c>Idempotency-Key</c> (8..128 видимих ASCII) обов'язковий (400 idempotency_key_required).
    /// 409: revision_changed | campaign_not_draft | idempotency_conflict | promotion_inactive | promotion_in_campaign | coupon_unavailable | audience_empty | audience_too_large;
    /// 422 invalid_schedule | revision_required; 429.
    /// </summary>
    [HttpPost("campaigns/{id:guid}/confirm")]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<CampaignDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(
        Guid id, [FromBody] ConfirmCampaignRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (idempotencyKey is not { Length: >= 8 and <= 128 } || !idempotencyKey.All(c => c is >= '!' and <= '~'))
            return BadRequest(new ApiError("idempotency_key_required", "Idempotency-Key header (8..128 visible ASCII characters) is required."));
        return Map(await service.ConfirmAsync(actor, id, idempotencyKey, request, ct));
    }

    /// <summary>Ідемпотентно (повтор на cancelled -> 200); done -> 409 campaign_finished.</summary>
    [HttpPost("campaigns/{id:guid}/cancel")]
    [AllowWhenMarketingDisabled]
    [ProducesResponseType<CampaignDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.CancelAsync(actor, id, ct)) : Unauthorized();

    [HttpGet("campaigns/{id:guid}/stats")]
    [AllowWhenMarketingDisabled]
    [ProducesResponseType<CampaignStatsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Stats(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.StatsAsync(actor, id, ct)) : Unauthorized();

    /// <summary>Лише кількості, без кодів.</summary>
    [HttpGet("campaigns/{id:guid}/coupons/summary")]
    [AllowWhenMarketingDisabled]
    [ProducesResponseType<CampaignCouponSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CouponSummary(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await service.CouponSummaryAsync(actor, id, ct)) : Unauthorized();
}
