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
public sealed class AuthController(AuthService auth, RefreshThrottle refreshThrottle, IAccessContext access) : ControllerBase
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
    [EnableRateLimiting(AuthPolicies.RefreshRateLimit)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        // Ширший ліміт на IP (політика) + вузький на токен і tenant: токен одноразовий, повтори = replay/перебір.
        if (TokenCodec.Parse(request.RefreshToken) is { } parsed && !refreshThrottle.TryAcquire(parsed.TenantId, parsed.Hash))
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
        return this.ToResult(await auth.RefreshAsync(request.RefreshToken, ct));
    }

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

    /// <summary>
    /// Поточний користувач з ефективними правами (§30.9.1): permissions (усі ключі каталогу), accessVersion, accessStamp, modules.
    /// <c>Cache-Control: no-store</c>. Доступний і з неактивним профілем майстра (M7).
    /// </summary>
    [HttpGet("me")]
    [Authorize(Policy = AuthPolicies.Staff)]
    [AllowInactiveSpecialist]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        return this.ToResult(await auth.MeAsync(actor.UserId, ct), u => Ok(u with
        {
            AccessVersion = access.AccessVersion,
            AccessStamp = access.AccessStamp,
            CatalogVersion = PermissionCatalog.Version,
            Modules = access.Modules,
            Permissions = access.Access.Effective,
        }));
    }
}

/// <summary>Каталог прав (ADR-023, §30.9.2): однаковий для всіх ролей, кешується приватно на 5 хв. Не залежить від активності профілю майстра.</summary>
[ApiController]
[Route("api/access")]
[Authorize(Policy = AuthPolicies.Staff)]
public sealed class AccessController : ControllerBase
{
    private static readonly AccessCatalogDto Catalog = PermissionCatalog.ToDto();

    [HttpGet("catalog")]
    [AllowInactiveSpecialist]
    [ProducesResponseType<AccessCatalogDto>(StatusCodes.Status200OK)]
    public IActionResult GetCatalog()
    {
        Response.Headers.CacheControl = "private, max-age=300";
        return Ok(Catalog);
    }
}

/// <summary>Користувачі й запрошення tenant. Лише owner/admin; admin керує тільки спеціалістами, власник недоторканний.</summary>
[ApiController]
[Route("api")]
[Authorize(Policy = AuthPolicies.Staff)] // усі ролі проходять політику; доступ - за правом team.manage (§30.10 п.1: 403 forbidden_role {permission, required})
[RequirePermission(PermissionKeys.TeamManage, PermissionLevels.Manage)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class UsersController(UserAdminService admin, AccessAdminService access) : ControllerBase
{
    private const int AccessBody = 8 * 1024; // §30.9: тіла доступів і запрошень

    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken ct) =>
        User.ToActor() is { } actor ? Ok(await admin.ListUsersAsync(actor, ct)) : Unauthorized();

    /// <summary>
    /// Вимкнути/ввімкнути користувача (вимкнення відкликає його refresh-токени й pending-запрошення, access_version + 1, аудит).
    /// <c>expectedVersion</c> необов'язкове; 409 access_version_conflict. 403 для owner і рівних/вищих рангів.
    /// </summary>
    [HttpPatch("users/{id:guid}/status")]
    [RequestSizeLimit(AccessBody)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetUserActiveRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await access.SetStatusAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>Доступ користувача (§30.9.3): granted/effective/assignable/locked. no-store.</summary>
    [HttpGet("users/{id:guid}/access")]
    [ProducesResponseType<UserAccessDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAccess(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return User.ToActor() is { } actor ? this.ToResult(await access.GetAccessAsync(actor, id, ct)) : Unauthorized();
    }

    /// <summary>Зміна ролі (admin &lt;-&gt; deputy) і/або прав (§30.9.4, §30.10). expectedVersion обов'язкове; dryRun без запису.</summary>
    [HttpPut("users/{id:guid}/access")]
    [RequestSizeLimit(AccessBody)]
    [ProducesResponseType<UserAccessDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PutAccess(Guid id, [FromBody] UpdateAccessRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return User.ToActor() is { } actor ? this.ToResult(await access.UpdateAsync(actor, id, request, ct)) : Unauthorized();
    }

    /// <summary>Скидання до пресету поточної ролі (з обрізанням стелею актора).</summary>
    [HttpPost("users/{id:guid}/access/reset")]
    [RequestSizeLimit(AccessBody)]
    [ProducesResponseType<UserAccessDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetAccess(Guid id, [FromBody] ResetAccessRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return User.ToActor() is { } actor ? this.ToResult(await access.ResetAsync(actor, id, request, ct)) : Unauthorized();
    }

    /// <summary>Журнал змін доступу (лише owner, Q7): keyset (created_at desc, id desc), take 1..100 (50 за замовчуванням).</summary>
    [HttpGet("users/access-audit")]
    [Authorize(Policy = AuthPolicies.Owner)]
    [ProducesResponseType<AccessAuditPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AccessAudit([FromQuery] Guid? userId, [FromQuery] Guid? inviteId, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return User.ToActor() is { } actor ? this.ToResult(await access.ListAuditAsync(actor, userId, inviteId, cursor, take, ct)) : Unauthorized();
    }

    /// <summary>201 + одноразовий токен запрошення (передати адресату поза API); owner запрошує admin/specialist, admin — specialist.</summary>
    [HttpPost("invites")]
    [ProducesResponseType<InviteCreatedDto>(StatusCodes.Status201Created)]
    [RequestSizeLimit(AccessBody)]
    public async Task<IActionResult> CreateInvite([FromBody] CreateInviteRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await admin.CreateInviteAsync(actor, request, ct), r => this.InviteCreated(r))
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
