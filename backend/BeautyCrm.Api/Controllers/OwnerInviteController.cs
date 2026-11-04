using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyControlPlane;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Запрошення власника бізнесу (ADR-018, §27.8). ПУБЛІЧНІ за дизайном (виняток із auth-за-замовчуванням): токен - єдиний секрет.
/// Захист: rate limit на IP (10/хв, окрема політика) + на хеш токена (брутфорс), будь-який непридатний токен = 410 invite_invalid,
/// Cache-Control: no-store (також для 429 - <see cref="OwnerInviteSupport.UseOwnerInviteNoStore"/>). Токен і пароль не логуються.
/// </summary>
[ApiController]
[Route("api/auth/invite")]
[AllowAnonymous]
[EnableRateLimiting(AuthPolicies.InviteRateLimit)]
public sealed class OwnerInviteController(OwnerInviteService invites, OwnerInviteThrottle throttle) : ControllerBase
{
    /// <summary>200 {tenantName, email (маска), expiresAt}; 410 invite_invalid; 403 tenant_suspended; 429.</summary>
    [HttpPost("inspect")]
    [ProducesResponseType<OwnerInviteInspectResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Inspect([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] OwnerInviteTokenRequest? request, CancellationToken ct) =>
        Throttled(request?.Token) ?? this.ToResult(await invites.InspectAsync(request?.Token, ct));

    /// <summary>200 (форма login); 410 invite_invalid; 403 tenant_suspended; 422 weak_password; 429.</summary>
    [HttpPost("accept")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accept([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] OwnerInviteAcceptRequest? request, CancellationToken ct) =>
        Throttled(request?.Token)
        ?? this.ToResult(await invites.AcceptAsync(request ?? new OwnerInviteAcceptRequest(null, null), HttpContext.Connection.RemoteIpAddress?.ToString(), ct));

    /// <summary>429, якщо вичерпано ліміт на хеш токена; null - можна продовжувати. Некоректний формат токена ліміт токена не займає.</summary>
    private IActionResult? Throttled(string? token) =>
        OwnerInviteService.TryHash(token) is { } hash && !throttle.TryAcquire(hash)
            ? StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."))
            : null;
}
