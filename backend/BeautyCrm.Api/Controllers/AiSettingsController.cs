using System.Text.Json;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Налаштування ШІ tenant (контракт §20.5, ADR-010). tenant лише з токена (id у шляху/тілі немає). specialist -> 403,
/// admin на шляхах ключів і джерела ключа -> 403 forbidden_role. Cache-Control: no-store задає <c>UseBeautyAiNoStore</c>.
/// Ключ приходить один раз у тілі PUT/POST і ніде не повертається.
/// </summary>
[ApiController]
[Route("api/beauty/ai")]
[RequireModule("beauty_ai")]
[Authorize(Policy = AuthPolicies.Management)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class AiSettingsController(AiSettingsService settings, AiKeyThrottle throttle) : ControllerBase
{
    private const int SettingsBody = 32 * 1024;
    private const int KeyBody = 8 * 1024;
    private const int SmallBody = 1024;

    /// <summary>Коди 502/503/429 мапляться за кодом помилки; 409 key_affects_other_roles несе affectedRoles.</summary>
    private IActionResult Map<T>(Result<T> r, Func<T, IActionResult>? ok = null)
    {
        if (r.IsOk) return ok is null ? Ok(r.Value) : ok(r.Value!);
        var e = r.Error!;
        return e.Code switch
        {
            AiSettingsService.ErrSecretsUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(e.Code, e.Message)),
            AiSettingsService.ErrProviderUnavailable => StatusCode(StatusCodes.Status502BadGateway, new ApiError(e.Code, e.Message)),
            AiSettingsService.ErrKeyAffectsOtherRoles => Conflict(new { code = e.Code, message = e.Message, affectedRoles = e.Conflicts }),
            _ => this.ToError(e),
        };
    }

    private IActionResult? KeyRateLimited(Actor actor)
    {
        if (throttle.TryAcquire(actor.TenantId, actor.UserId, out var retryAfter)) return null;
        Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
    }

    /// <summary>Без властивості keys для admin; ключі лише як last4.</summary>
    [HttpGet("settings")]
    [ProducesResponseType<AiSettingsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await settings.GetAsync(actor, ct)) : Unauthorized();

    /// <summary>Повна заміна (owner, admin). 200 AiSettingsDto + warnings[]; 422 коди §20.5.</summary>
    [HttpPut("settings")]
    [RequestSizeLimit(SettingsBody)]
    [ProducesResponseType<AiSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Put([FromBody] UpdateAiSettingsRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await settings.UpdateAsync(actor, request, ct)) : Unauthorized();

    [HttpPut("settings/key-source")]
    [AiOwnerOnly]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<AiSettingsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PutKeySource([FromBody] AiKeySourceRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await settings.SetKeySourceAsync(actor, request, ct)) : Unauthorized();

    /// <summary>
    /// Встановити/замінити ключ провайдера (owner). 200 {provider,hasKey,last4,updatedAt,keySource,usedByRoles};
    /// 409 key_affects_other_roles; 422 invalid_key_format/key_rejected/provider_not_available; 429; 502; 503.
    /// </summary>
    [HttpPut("settings/keys/{provider}")]
    [AiOwnerOnly]
    [RequestSizeLimit(KeyBody)]
    [ProducesResponseType<AiKeySavedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> PutKey(string provider, [FromBody] AiKeySaveRequest request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        return KeyRateLimited(actor) ?? Map(await settings.SaveKeyAsync(actor, provider, request, ct));
    }

    /// <summary>204 (ідемпотентно); 409 key_affects_other_roles без acknowledgeAffectedRoles.</summary>
    [HttpDelete("settings/keys/{provider}")]
    [AiOwnerOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> DeleteKey(
        string provider, [FromQuery] string? forRole, [FromQuery] bool acknowledgeAffectedRoles = false, CancellationToken ct = default)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        return KeyRateLimited(actor) ?? Map(await settings.DeleteKeyAsync(actor, provider, forRole, acknowledgeAffectedRoles, ct), _ => NoContent());
    }

    /// <summary>Без apiKey перевіряється збережений ключ, інакше кандидат (не зберігається).</summary>
    [HttpPost("settings/keys/{provider}/test")]
    [AiOwnerOnly]
    [RequestSizeLimit(KeyBody)]
    [ProducesResponseType<AiKeyTestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TestKey(string provider, [FromBody] AiKeyTestRequest? request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        return KeyRateLimited(actor) ?? Map(await settings.TestKeyAsync(actor, provider, request, ct));
    }

    [HttpGet("models")]
    [ProducesResponseType<AiCatalogDto>(StatusCodes.Status200OK)]
    public IActionResult Models() => Map(settings.GetModels());

    [HttpGet("usage")]
    [ProducesResponseType<AiUsageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Usage([FromQuery] int months = 1, CancellationToken ct = default) =>
        User.ToActor() is { } actor ? Map(await settings.GetUsageAsync(actor, months, ct)) : Unauthorized();
}

/// <summary>
/// Квоти платформи на tenant (оператор платформи, заголовок X-Platform-Key, як <c>PlatformTenantsController</c>; rate limit як на логіні).
/// Відсутнє поле = без змін, null = дефолт конфігу. Квоти не змінюються через tenant-API.
/// </summary>
[ApiController]
[Route("api/platform/tenants/{id:guid}/ai")]
[Authorize(Policy = AuthPolicies.PlatformOperator)]
[EnableRateLimiting(AuthPolicies.RateLimit)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class PlatformTenantAiController(AiSettingsService settings) : ControllerBase
{
    [HttpPatch]
    [RequestSizeLimit(2 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Patch(Guid id, [FromBody] JsonElement body, CancellationToken ct) =>
        this.ToResult(await settings.UpdatePlatformAsync(id, body, ct), _ => NoContent());
}
