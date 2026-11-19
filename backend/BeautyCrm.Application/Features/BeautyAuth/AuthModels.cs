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

    /// <summary>ADR-025 (§32.1): склад (товари, партії, рух, інвентаризація). Не входить у <see cref="All"/>, без backfill.</summary>
    public const string Inventory = "beauty_inventory";

    /// <summary>Усі допустимі імена модулів (для валідації списку модулів tenant): <see cref="All"/> + опційні.</summary>
    public static readonly IReadOnlyList<string> Known = [.. All, Marketing, Inventory];
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

    // ---- ADR-024 / §31.9 ----
    /// <summary>Вікно підрахунку невдалих входів (с); якщо воно спливло, лічильник починається з 1.</summary>
    public int LoginAttemptWindowSeconds { get; set; } = 900;
    /// <summary>Секрет HMAC для <c>login_key</c> (§31.1), ≥ 32 байти (UTF-8). Поза Development обов'язковий (перевіряє AuthSetup).</summary>
    public string? LoginAttemptKeyPepper { get; set; }
    /// <summary>TTL квитка вибору бізнесу, 60..600 с.</summary>
    public int LoginTicketSeconds { get; set; } = 180;
    public LegacyTenantLoginOptions LegacyTenantLogin { get; set; } = new();
    public IdentityCreateOptions IdentityCreate { get; set; } = new();
    public InviteEmailCheckOptions InviteEmailCheck { get; set; } = new();
    public PasswordForgotOptions PasswordForgot { get; set; } = new();
    /// <summary>TTL токена скидання пароля (хв, 10..120; §31.9).</summary>
    public int PasswordResetMinutes { get; set; } = 30;
}

/// <summary>§31.9: <c>password/forgot</c>. Ліміти in-memory на інстанс; пошта в лімітері лише як sha256.</summary>
public sealed class PasswordForgotOptions
{
    public int PerIpPermit { get; set; } = 5;
    public int PerIpWindowSeconds { get; set; } = 900;
    public int PerEmailPermit { get; set; } = 3;
    public int PerEmailWindowSeconds { get; set; } = 3600;
    public int QueueCapacity { get; set; } = 1000;
}

public sealed class InviteEmailCheckOptions
{
    /// <summary>Перевірок зайнятості пошти при створенні запрошень на tenant за годину (§31.9, in-memory, на інстанс).</summary>
    public int PerTenantPerHour { get; set; } = 30;
}

/// <summary>§31.2.3: заголовки Deprecation/Sunset для запитів входу з необов'язковим полем <c>tenant</c>.</summary>
public sealed class LegacyTenantLoginOptions
{
    public DateTimeOffset? DeprecatedAt { get; set; }
    public DateTimeOffset? SunsetAt { get; set; }
}

public sealed class IdentityCreateOptions
{
    /// <summary>Квота нових ідентичностей через запрошення персоналу на tenant за 24 год (§31.9, 1..200).</summary>
    public int PerTenantPerDay { get; set; } = 20;
}

// ---------- помилки ----------

public enum AuthErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict, Locked, Gone, TooManyRequests }

/// <param name="Extras">Додаткові поля тіла помилки (§30.11): permission, required, currentVersion, permissions[], violations[] тощо.</param>
/// <param name="RetryAfterSeconds">Для <see cref="AuthErrorKind.TooManyRequests"/>: значення заголовка Retry-After.</param>
public sealed record AuthError(AuthErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, object?>? Extras = null, int? RetryAfterSeconds = null)
{
    /// <summary>429 rate_limited + Retry-After (§31.9).</summary>
    public static AuthError RateLimited(int retryAfterSeconds) =>
        new(AuthErrorKind.TooManyRequests, "rate_limited", "Too many requests. Try again later.", null, Math.Max(1, retryAfterSeconds));

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

/// <summary>§31.2: <c>Tenant</c> необов'язкове й deprecated (null/порожнє після Trim = нова форма входу без коду бізнесу).</summary>
public sealed record LoginRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(128)] string Password,
    [StringLength(64)] string? Tenant = null);

public sealed record LoginSelectRequest(
    [Required, StringLength(200)] string LoginTicket,
    [Required] Guid? TenantId);

/// <summary>
/// <c>RefreshToken</c> необов'язковий на рівні ModelState: його відсутність - це <c>401 invalid_token</c> (§31.5 крок 4, §31.15 п.10),
/// а не 422; без чинного refresh-токена поточної сесії переключитися неможливо.
/// </summary>
public sealed record SwitchRequest(
    [Required] Guid? TenantId,
    [StringLength(200)] string? RefreshToken = null);

/// <summary>Бізнес у відповіді варіанта B: лише id, назва й роль (без slug, userId, модулів, прав).</summary>
public sealed record BusinessOption(Guid TenantId, string Name, string Role);

/// <summary>Відповідь <c>login</c>, варіант B (≥ 2 активних членств). Дискримінатор — <c>selectionRequired === true</c>.</summary>
public sealed record LoginSelectionResponse(
    bool SelectionRequired, string LoginTicket, int TicketExpiresInSeconds, IReadOnlyList<BusinessOption> Businesses);

/// <summary><c>GET /api/auth/memberships</c> (§31.4).</summary>
public sealed record MembershipDto(Guid TenantId, string Name, string Role, bool Current);

/// <summary>Результат входу: рівно одне з полів не null (A — токени, B — вибір бізнесу).</summary>
public sealed record LoginOutcome(TokenResponse? Tokens, LoginSelectionResponse? Selection);

/// <summary>§31.7.1: <c>Email</c> без атрибутів валідації: синтаксична помилка = <c>422 invalid_email</c> (а не загальний validation_failed).</summary>
public sealed record ForgotPasswordRequest(string? Email);

/// <summary>§31.7.2: без атрибутів; порядок перевірок (weak_password до токена) задає сервіс.</summary>
public sealed record ResetPasswordRequest(string? Token, string? Password);

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
    int AccessVersion = 1, Guid IdentityId = default, DateTimeOffset? SessionsValidAfter = null);

public sealed record InviteRecord(
    Guid Id, string Email, string Role, Guid? SpecialistId, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt, string PermissionsJson = "{}", Guid? InvitedByUserId = null);

public sealed record RefreshRecord(Guid Id, Guid UserId, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset CreatedAt = default);

/// <summary>ADR-018: PasswordHash = null лише для <see cref="UserStates.Invited"/> (власник, створений control plane).</summary>
public sealed record NewUser(string Email, string FullName, string? PasswordHash, string Role, Guid? SpecialistId, string State = UserStates.Active);

/// <param name="PermissionsJson">Повний знімок прав (B1), jsonb-об'єкт.</param>
/// <param name="AuditChangesJson">Змістові поля рядка аудиту <c>invited</c> (без email).</param>
public sealed record NewInvite(
    string Email, string Role, Guid? SpecialistId, string TokenHash, DateTimeOffset ExpiresAt, Guid? InvitedByUserId,
    string PermissionsJson = "{}", string AuditChangesJson = "{}");

public sealed record NewTenant(Guid Id, string Name, string Slug, IReadOnlyList<string> Modules);

/// <summary>
/// Conflict - slug / пошта вже член цього tenant / профіль майстра зайнятий (залежно від операції); EmailInUse - пошта зайнята
/// глобальною ідентичністю (§31.8); QuotaExceeded - квота нових ідентичностей tenant (§31.8.2, F-4).
/// </summary>
public enum WriteOutcome { Ok, Conflict, Unavailable, EmailInUse, QuotaExceeded }

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

    Task<UserRecord?> GetUserAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<UserRecord>> ListUsersAsync(CancellationToken ct);
    /// <summary>Лише <c>users.last_login_at</c> членства. Блокування й лічильник входів живуть в <see cref="IIdentityStore"/> (ADR-024); застарілі users.failed_login_count/lockout_until не читаються й не пишуться.</summary>
    Task SetLastLoginAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

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

    /// <summary>
    /// Секунди до звільнення місця в квоті нових ідентичностей поточного tenant (ковзне вікно 24 год, до хвилини вгору; 60..86400).
    /// Викликається після <see cref="WriteOutcome.QuotaExceeded"/> (транзакція прийняття вже відкочена).
    /// </summary>
    Task<int> IdentityQuotaRetryAfterSecondsAsync(CancellationToken ct);

    /// <summary>Створення tenant + власника однією транзакцією. Conflict = slug уже зайнятий; EmailInUse = пошта власника зайнята (§31.8.3).</summary>
    Task<(WriteOutcome Outcome, Guid OwnerUserId)> CreateTenantWithOwnerAsync(NewTenant tenant, NewUser owner, CancellationToken ct);
    Task<WriteOutcome> UpdateTenantAsync(Guid tenantId, IReadOnlyList<string>? modules, string? status, DateTimeOffset now, CancellationToken ct);
}


// ---------- ADR-024: глобальна ідентичність (порт функцій auth_*) ----------

public sealed record IdentityRecord(Guid Id, string? PasswordHash, string State)
{
    public bool IsActive => State == "active" && PasswordHash is not null;
}

/// <summary>Активне членство ідентичності (<c>auth_identity_memberships</c>).</summary>
public sealed record MembershipRow(Guid TenantId, Guid UserId, string TenantName, string TenantSlug, string Role);

/// <summary>
/// Порт глобальної ідентичності: тонкі виклики SECURITY DEFINER-функцій <c>auth_*</c> (§31.16). Прямого доступу до таблиць
/// identities / auth_* застосунок не має. Працює поза RLS tenant (власне з'єднання), тому не залежить від <see cref="IAuthStore.UseTenant"/>.
/// Жоден метод не приймає <c>identity_id</c> від клієнта: його дають лише результати <see cref="FindAsync"/> / членства.
/// </summary>
public interface IIdentityStore
{
    Task<IdentityRecord?> FindAsync(string normalizedEmail, CancellationToken ct);
    Task<bool> IsLockedAsync(byte[] loginKey, DateTimeOffset now, CancellationToken ct);
    /// <returns>true, якщо ця спроба досягла порогу (блокування).</returns>
    Task<bool> RegisterFailedAsync(byte[] loginKey, Guid? identityId, int max, int windowSeconds, int lockoutSeconds, DateTimeOffset now, CancellationToken ct);
    Task ResetAttemptsAsync(byte[] loginKey, CancellationToken ct);
    Task LoginSucceededAsync(Guid identityId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<MembershipRow>> MembershipsAsync(Guid identityId, CancellationToken ct);
    Task<bool> IsActiveAsync(Guid identityId, CancellationToken ct);
    /// <summary><c>auth_identity_email_in_use</c>: пошта зайнята активною або pending ідентичністю (§31.8.1).</summary>
    Task<bool> EmailInUseAsync(string normalizedEmail, CancellationToken ct);
    Task IssueTicketAsync(Guid identityId, byte[] tokenHash, DateTimeOffset expiresAt, CancellationToken ct);
    /// <summary>users.id членства або null (одна відповідь на всі причини, §31.3). Гасить квиток завжди.</summary>
    Task<Guid?> ConsumeTicketAsync(byte[] tokenHash, Guid tenantId, DateTimeOffset now, CancellationToken ct);
}
