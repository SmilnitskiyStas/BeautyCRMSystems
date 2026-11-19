using System.ComponentModel.DataAnnotations;

namespace BeautyCrm.Application.Features.BeautyAuth;

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    /// <summary>Заступник (ADR-023, ранг 2, нижче admin). Права виключно з user_permissions (пресет §30.3).</summary>
    public const string Deputy = "deputy";
    public const string Specialist = "specialist";
    /// <summary>Оператор платформи: не користувач tenant, автентифікується ключем з .env.</summary>
    public const string PlatformOperator = "platform_operator";

    public static bool IsTenantRole(string? role) => role is Owner or Admin or Deputy or Specialist;

    /// <summary>Ранг (§30.1): owner 4, admin 3, deputy 2, specialist 1; 0 — не роль tenant.</summary>
    public static int Rank(string? role) => role switch { Owner => 4, Admin => 3, Deputy => 2, Specialist => 1, _ => 0 };
}

public static class BeautyModules
{
    public static readonly IReadOnlyList<string> All =
        ["beauty_booking", "beauty_catalog", "beauty_clients", "beauty_analytics", "beauty_channels", "beauty_ai"];

    /// <summary>TASK-777 (§26.11.1, ADR-015): кампанії й купони. Не входить у <see cref="All"/> (нікому не вмикається за замовчуванням, без backfill).</summary>
    public const string Marketing = "beauty_marketing";

    /// <summary>Усі допустимі імена модулів (для валідації списку модулів tenant): <see cref="All"/> + опційні.</summary>
    public static readonly IReadOnlyList<string> Known = [.. All, Marketing];
}

public static class UserStates
{
    public const string Active = "active";
    /// <summary>Власник створений control plane і ще не задав пароль (вхід неможливий).</summary>
    public const string Invited = "invited";
}

/// <summary>
/// Автентифікований користувач tenant. Role/SpecialistId — з БД (middleware замінює claims), <see cref="Access"/> — ефективні права
/// (§30.4). <see cref="Access"/> обов'язковий (B2): у production-коді немає дефолту «пресет за роллю»; без контексту
/// доступу <c>ToActor()</c> дає <see cref="AccessSet.None"/>.
/// </summary>
public sealed record Actor(Guid UserId, Guid TenantId, string Role, Guid? SpecialistId, AccessSet Access)
{
    /// <summary>
    /// Лише для тестів (InternalsVisibleTo BeautyCrm.Tests): актор з пресетом ролі й усіма модулями. У production недоступна
    /// (internal), пресет як джерело Access заборонений архітектурним тестом.
    /// </summary>
    internal static Actor ForTest(string role, Guid? specialistId = null, Guid? userId = null, Guid? tenantId = null,
        IReadOnlyDictionary<string, string>? permissions = null, IEnumerable<string>? modules = null) =>
        new(userId ?? Guid.NewGuid(), tenantId ?? Guid.NewGuid(), role, specialistId,
            AccessSet.Build(role, permissions ?? PermissionPresets.For(role), modules ?? BeautyModules.Known, false));
}

public sealed class AuthOptions
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
    public int InviteDays { get; set; } = 7;
    public int MinPasswordLength { get; set; } = 12;
}

// ---------- помилки ----------

public enum AuthErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict, Locked, Gone }

/// <param name="Extras">Додаткові поля тіла помилки (§30.11): permission, required, currentVersion, permissions[], violations[] тощо.</param>
public sealed record AuthError(AuthErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, object?>? Extras = null)
{
    public static AuthError Validation(string code, string message) => new(AuthErrorKind.Validation, code, message);
    public static AuthError Unauthorized(string code, string message) => new(AuthErrorKind.Unauthorized, code, message);
    public static AuthError Forbidden(string code, string message) => new(AuthErrorKind.Forbidden, code, message);
    public static AuthError NotFound(string code, string message) => new(AuthErrorKind.NotFound, code, message);
    public static AuthError Conflict(string code, string message) => new(AuthErrorKind.Conflict, code, message);
    public static AuthError Locked(string code, string message) => new(AuthErrorKind.Locked, code, message);
    public static AuthError Gone(string code, string message) => new(AuthErrorKind.Gone, code, message);

    /// <summary>403 forbidden_role з полями permission/required (відмова за правом, §30.11).</summary>
    public static AuthError ForbiddenPermission(string permission, string required) =>
        new(AuthErrorKind.Forbidden, "forbidden_role", "This action is not available for your access level.",
            new Dictionary<string, object?> { ["permission"] = permission, ["required"] = required });

    public static readonly AuthError InvalidCredentials = Unauthorized("invalid_credentials", "Invalid credentials.");
    public static readonly AuthError InvalidToken = Unauthorized("invalid_token", "Token is invalid or expired.");
}

public sealed record AuthResult<T>(T? Value, AuthError? Error)
{
    public bool IsOk => Error is null;
    public static implicit operator AuthResult<T>(T value) => new(value, null);
    public static implicit operator AuthResult<T>(AuthError error) => new(default, error);
}

// ---------- запити / відповіді (API-контракт) ----------

public sealed record LoginRequest(
    [Required, StringLength(64)] string Tenant,
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(128)] string Password);

public sealed record RefreshRequest([Required, StringLength(200)] string RefreshToken);

public sealed record AcceptInviteRequest(
    [Required, StringLength(200)] string Token,
    [Required, StringLength(200, MinimumLength = 1)] string FullName,
    [Required, StringLength(128)] string Password);

public sealed record CreateInviteRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(16)] string Role,
    Guid? SpecialistId,
    IReadOnlyDictionary<string, string>? Permissions = null);

public sealed record SetUserActiveRequest(bool IsActive, int? ExpectedVersion = null);

public sealed record OwnerRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(200)] string FullName,
    [Required, StringLength(128)] string Password);

public sealed record CreateTenantRequest(
    [Required, StringLength(200)] string Name,
    [Required, StringLength(64)] string Slug,
    IReadOnlyList<string>? Modules,
    [Required] OwnerRequest Owner);

public sealed record UpdateTenantRequest(IReadOnlyList<string>? Modules, string? Status);

public sealed record UserDto(
    Guid Id, string Email, string FullName, string Role, Guid? SpecialistId, bool IsActive, DateTimeOffset? LastLoginAt)
{
    // Розширення /api/auth/me (§30.9.1); у відповідях login/accept відсутні (null не серіалізується).
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? AccessVersion { get; init; }
    /// <summary>Лише у списку <c>GET /api/users</c>: чи може актор керувати цим користувачем (ранг, не owner, не він сам).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Manageable { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessStamp { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? CatalogVersion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Modules { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Permissions { get; init; }
}

public sealed record TokenResponse(string AccessToken, int ExpiresInSeconds, string RefreshToken, UserDto User);

public sealed record InviteDto(
    Guid Id, string Email, string Role, Guid? SpecialistId, DateTimeOffset ExpiresAt, string Status,
    IReadOnlyDictionary<string, string>? Permissions = null, Guid? InvitedByUserId = null);

/// <summary>Токен запрошення повертається лише при створенні (у БД — тільки хеш). <see cref="Clamped"/>: ключі, обрізані стелею запрошувача (B1).</summary>
public sealed record InviteCreatedDto(InviteDto Invite, string Token, IReadOnlyList<PermissionClamp>? Clamped = null);

public sealed record TenantCreatedDto(Guid TenantId, string Name, string Slug, IReadOnlyList<string> Modules, Guid OwnerUserId);

// ---------- порти ----------

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

public interface ITokenIssuer
{
    IssuedAccessToken Issue(UserRecord user, DateTimeOffset now);
}

public sealed record TenantInfo(Guid Id, string Name, string Slug, IReadOnlyList<string> Modules, string Status)
{
    public bool IsActive => Status == "active";
}

public sealed record UserRecord(
    Guid Id, Guid TenantId, string Email, string FullName, string? PasswordHash, string Role, Guid? SpecialistId,
    bool IsActive, int FailedLoginCount, DateTimeOffset? LockoutUntil, DateTimeOffset? LastLoginAt, string State = UserStates.Active,
    int AccessVersion = 1);

public sealed record InviteRecord(
    Guid Id, string Email, string Role, Guid? SpecialistId, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt, string PermissionsJson = "{}", Guid? InvitedByUserId = null);

public sealed record RefreshRecord(Guid Id, Guid UserId, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

/// <summary>ADR-018: PasswordHash = null лише для <see cref="UserStates.Invited"/> (власник, створений control plane).</summary>
public sealed record NewUser(string Email, string FullName, string? PasswordHash, string Role, Guid? SpecialistId, string State = UserStates.Active);

/// <param name="PermissionsJson">Повний знімок прав (B1), jsonb-об'єкт.</param>
/// <param name="AuditChangesJson">Змістові поля рядка аудиту <c>invited</c> (без email).</param>
public sealed record NewInvite(
    string Email, string Role, Guid? SpecialistId, string TokenHash, DateTimeOffset ExpiresAt, Guid? InvitedByUserId,
    string PermissionsJson = "{}", string AuditChangesJson = "{}");

public sealed record NewTenant(Guid Id, string Name, string Slug, IReadOnlyList<string> Modules);

public enum WriteOutcome { Ok, Conflict, Unavailable }

/// <summary>
/// Порт даних автентифікації. Усе, крім <see cref="FindTenantBySlugAsync"/>, працює під RLS поточного tenant:
/// спершу <see cref="UseTenant"/>, потім запити. Зміна tenant після першого запиту до БД заборонена.
/// </summary>
public interface IAuthStore
{
    /// <summary>Пошук tenant за slug до того, як tenant відомий (вузька SELECT-політика tenants).</summary>
    Task<TenantInfo?> FindTenantBySlugAsync(string slug, CancellationToken ct);
    void UseTenant(Guid tenantId);
    Task<TenantInfo?> GetTenantAsync(CancellationToken ct);

    Task<UserRecord?> FindUserByEmailAsync(string normalizedEmail, CancellationToken ct);
    Task<UserRecord?> GetUserAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<UserRecord>> ListUsersAsync(CancellationToken ct);
    /// <summary>Атомарно: лічильник +1; досягнення maxAttempts -> блокування до lockUntil.</summary>
    Task RegisterFailedLoginAsync(Guid userId, int maxAttempts, DateTimeOffset lockUntil, CancellationToken ct);
    Task RegisterSuccessfulLoginAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
    Task ResetLockoutAsync(Guid userId, CancellationToken ct);

    Task AddRefreshTokenAsync(Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct);
    Task<RefreshRecord?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct);
    /// <summary>true, лише якщо саме цей виклик відкликав активний токен (захист від подвійного використання).</summary>
    Task<bool> TryRevokeRefreshTokenAsync(Guid id, DateTimeOffset now, CancellationToken ct);
    Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    Task<bool> SpecialistExistsAsync(Guid specialistId, CancellationToken ct);
    Task<bool> SpecialistHasUserAsync(Guid specialistId, CancellationToken ct);
    /// <summary>Профіль існує й активний (деактивованому запрошення не видаються).</summary>
    Task<bool> SpecialistIsActiveAsync(Guid specialistId, CancellationToken ct);

    /// <summary>Створює запрошення й відкликає попередні активні для того ж email та (для specialist) для того ж specialist_id.</summary>
    /// <returns>null — профіль майстра деактивовано паралельно (під lock профілю); запрошення не створено.</returns>
    Task<InviteRecord?> AddInviteAsync(NewInvite invite, CancellationToken ct);
    Task<IReadOnlyList<InviteRecord>> ListInvitesAsync(CancellationToken ct);
    Task<InviteRecord?> FindInviteByHashAsync(string tokenHash, CancellationToken ct);
    /// <summary>Відкликає pending-запрошення й пише аудит <c>invite_revoked</c> (actor = той, хто відкликав) в одній транзакції.</summary>
    Task<bool> RevokeInviteAsync(Guid inviteId, DateTimeOffset now, Guid actorUserId, CancellationToken ct);
    /// <summary>
    /// Одна транзакція (§30.9.5, M5): FOR SHARE рядка запрошувача, перевірка його прав (<see cref="AccessPolicy.PlanAccept"/>), обрізання
    /// стелею, створення користувача з user_permissions і аудитом <c>invite_accepted</c>. Запрошувач утратив права: запрошення
    /// відкликається (аудит <c>invite_revoked</c>, actor = null) і результат <see cref="WriteOutcome.Unavailable"/>.
    /// </summary>
    Task<(WriteOutcome Outcome, UserRecord? User)> AcceptInviteAsync(InviteRecord invite, NewUser user, DateTimeOffset now, CancellationToken ct);

    /// <summary>Створення tenant + власника однією транзакцією. Conflict = slug уже зайнятий.</summary>
    Task<(WriteOutcome Outcome, Guid OwnerUserId)> CreateTenantWithOwnerAsync(NewTenant tenant, NewUser owner, CancellationToken ct);
    Task<WriteOutcome> UpdateTenantAsync(Guid tenantId, IReadOnlyList<string>? modules, string? status, DateTimeOffset now, CancellationToken ct);
}
