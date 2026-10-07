using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Автентифікація. Login/refresh/logout/accept — ПУБЛІЧНІ за дизайном (виняток із auth-за-замовчуванням),
/// обмежені rate limit; самореєстрації немає: tenant створює оператор, користувачів — запрошення.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>200 токени; 401 invalid_credentials; 423 account_locked; 422 валідація; 429 rate limit.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthPolicies.RateLimit)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct) =>
        this.ToResult(await auth.LoginAsync(request, ct));

    /// <summary>Ротація: старий refresh-токен перестає діяти; повторне використання закриває всі сесії користувача.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthPolicies.RateLimit)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct) =>
        this.ToResult(await auth.RefreshAsync(request.RefreshToken, ct));

    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthPolicies.RateLimit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    /// <summary>Прийняти запрошення й задати пароль. 201 UserDto; 404 invite_invalid; 409 user_exists; 422 weak_password.</summary>
    [HttpPost("invites/accept")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthPolicies.RateLimit)]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AcceptInvite([FromBody] AcceptInviteRequest request, CancellationToken ct) =>
        this.ToResult(await auth.AcceptInviteAsync(request, ct), u => StatusCode(StatusCodes.Status201Created, u));

    [HttpGet("me")]
    [Authorize(Policy = AuthPolicies.Staff)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await auth.MeAsync(actor.UserId, ct)) : Unauthorized();
}

/// <summary>Користувачі й запрошення tenant. Лише owner/admin; admin керує тільки спеціалістами, власник недоторканний.</summary>
[ApiController]
[Route("api")]
[Authorize(Policy = AuthPolicies.Management)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class UsersController(UserAdminService admin) : ControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken ct) => Ok(await admin.ListUsersAsync(ct));

    /// <summary>Вимкнути/увімкнути користувача (вимкнення відкликає його refresh-токени). 403 для owner і рівних ролей.</summary>
    [HttpPatch("users/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetUserActiveRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await admin.SetUserActiveAsync(actor, id, request.IsActive, ct)) : Unauthorized();

    /// <summary>201 + одноразовий токен запрошення (передати адресату поза API); owner запрошує admin/specialist, admin — specialist.</summary>
    [HttpPost("invites")]
    [ProducesResponseType<InviteCreatedDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateInvite([FromBody] CreateInviteRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await admin.CreateInviteAsync(actor, request, ct), r => StatusCode(StatusCodes.Status201Created, r))
            : Unauthorized();

    [HttpGet("invites")]
    public async Task<IActionResult> Invites(CancellationToken ct) => Ok(await admin.ListInvitesAsync(ct));

    [HttpDelete("invites/{id:guid}")]
    public async Task<IActionResult> RevokeInvite(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await admin.RevokeInviteAsync(actor, id, ct), _ => NoContent()) : Unauthorized();
}

/// <summary>
/// Оператор платформи: створення tenant + власника та керування модулями/статусом. Захищено ключем платформи
/// (заголовок X-Platform-Key, значення Auth:PlatformKey із .env), не JWT користувача; rate limit як на логіні.
/// </summary>
[ApiController]
[Route("api/platform/tenants")]
[Authorize(Policy = AuthPolicies.PlatformOperator)]
[EnableRateLimiting(AuthPolicies.RateLimit)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class PlatformTenantsController(PlatformTenantService platform) : ControllerBase
{
    /// <summary>201; 409 tenant_slug_taken; 422 invalid_slug / invalid_modules / weak_password.</summary>
    [HttpPost]
    [ProducesResponseType<TenantCreatedDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken ct) =>
        this.ToResult(await platform.CreateTenantAsync(request, ct), t => StatusCode(StatusCodes.Status201Created, t));

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTenantRequest request, CancellationToken ct) =>
        this.ToResult(await platform.UpdateTenantAsync(id, request, ct), _ => NoContent());
}
