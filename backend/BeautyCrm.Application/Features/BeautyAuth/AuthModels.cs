using System.ComponentModel.DataAnnotations;

namespace BeautyCrm.Application.Features.BeautyAuth;

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Specialist = "specialist";
    /// <summary>Оператор платформи: не користувач tenant, автентифікується ключем з .env.</summary>
    public const string PlatformOperator = "platform_operator";

    public static bool IsTenantRole(string? role) => role is Owner or Admin or Specialist;
}

public static class BeautyModules
{
    public static readonly IReadOnlyList<string> All =
        ["beauty_booking", "beauty_catalog", "beauty_clients", "beauty_analytics", "beauty_channels", "beauty_ai"];
}

/// <summary>Автентифікований користувач tenant (з claims JWT).</summary>
public sealed record Actor(Guid UserId, Guid TenantId, string Role, Guid? SpecialistId);

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

public enum AuthErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict, Locked }

public sealed record AuthError(AuthErrorKind Kind, string Code, string Message)
{
    public static AuthError Validation(string code, string message) => new(AuthErrorKind.Validation, code, message);
    public static AuthError Unauthorized(string code, string message) => new(AuthErrorKind.Unauthorized, code, message);
    public static AuthError Forbidden(string code, string message) => new(AuthErrorKind.Forbidden, code, message);
    public static AuthError NotFound(string code, string message) => new(AuthErrorKind.NotFound, code, message);
    public static AuthError Conflict(string code, string message) => new(AuthErrorKind.Conflict, code, message);
    public static AuthError Locked(string code, string message) => new(AuthErrorKind.Locked, code, message);

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
    Guid? SpecialistId);

public sealed record SetUserActiveRequest(bool IsActive);

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
    Guid Id, string Email, string FullName, string Role, Guid? SpecialistId, bool IsActive, DateTimeOffset? LastLoginAt);

public sealed record TokenResponse(string AccessToken, int ExpiresInSeconds, string RefreshToken, UserDto User);

public sealed record InviteDto(
    Guid Id, string Email, string Role, Guid? SpecialistId, DateTimeOffset ExpiresAt, string Status);

/// <summary>Токен запрошення повертається лише при створенні (у БД — тільки хеш).</summary>
public sealed record InviteCreatedDto(InviteDto Invite, string Token);

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
    Guid Id, Guid TenantId, string Email, string FullName, string PasswordHash, string Role, Guid? SpecialistId,
    bool IsActive, int FailedLoginCount, DateTimeOffset? LockoutUntil, DateTimeOffset? LastLoginAt);

public sealed record InviteRecord(
    Guid Id, string Email, string Role, Guid? SpecialistId, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt);

public sealed record RefreshRecord(Guid Id, Guid UserId, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

public sealed record NewUser(string Email, string FullName, string PasswordHash, string Role, Guid? SpecialistId);

public sealed record NewInvite(
    string Email, string Role, Guid? SpecialistId, string TokenHash, DateTimeOffset ExpiresAt, Guid? InvitedByUserId);

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
    /// <summary>Вимкнення також відкликає refresh-токени користувача.</summary>
    Task<bool> SetUserActiveAsync(Guid userId, bool active, DateTimeOffset now, CancellationToken ct);

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
    Task<bool> RevokeInviteAsync(Guid inviteId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Атомарно: позначити запрошення прийнятим і створити користувача.</summary>
    Task<(WriteOutcome Outcome, UserRecord? User)> AcceptInviteAsync(Guid inviteId, NewUser user, DateTimeOffset now, CancellationToken ct);

    /// <summary>Створення tenant + власника однією транзакцією. Conflict = slug уже зайнятий.</summary>
    Task<(WriteOutcome Outcome, Guid OwnerUserId)> CreateTenantWithOwnerAsync(NewTenant tenant, NewUser owner, CancellationToken ct);
    Task<WriteOutcome> UpdateTenantAsync(Guid tenantId, IReadOnlyList<string>? modules, string? status, DateTimeOffset now, CancellationToken ct);
}
