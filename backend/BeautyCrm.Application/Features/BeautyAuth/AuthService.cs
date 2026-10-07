namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>Логін, refresh з ротацією, вихід, прийняття запрошення. Нічого не логує (паролі/токени).</summary>
public sealed class AuthService(IAuthStore store, IPasswordHasher hasher, ITokenIssuer tokens, AuthOptions options, TimeProvider clock)
{
    // Хеш неіснуючого користувача: вирівнює час відповіді, щоб не розкривати наявність облікового запису.
    private string? _dummyHash;

    public async Task<AuthResult<TokenResponse>> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tenant = await store.FindTenantBySlugAsync(req.Tenant.Trim().ToLowerInvariant(), ct);
        if (tenant is null || !tenant.IsActive) return RejectUnknown(req.Password);

        store.UseTenant(tenant.Id);
        var user = await store.FindUserByEmailAsync(NormalizeEmail(req.Email), ct);
        if (user is null) return RejectUnknown(req.Password);

        if (user.LockoutUntil is { } until && until > now)
            return AuthError.Locked("account_locked", "Too many failed attempts. Try again later.");
        if (user.LockoutUntil is not null)
            await store.ResetLockoutAsync(user.Id, ct); // блокування спливло: починаємо лічильник заново

        if (!hasher.Verify(req.Password, user.PasswordHash))
        {
            await store.RegisterFailedLoginAsync(user.Id, options.MaxFailedAttempts, now.AddMinutes(options.LockoutMinutes), ct);
            return AuthError.InvalidCredentials;
        }
        if (!user.IsActive) return AuthError.InvalidCredentials;

        await store.RegisterSuccessfulLoginAsync(user.Id, now, ct);
        return await IssueAsync(user, now, ct);
    }

    public async Task<AuthResult<TokenResponse>> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var parsed = TokenCodec.Parse(refreshToken);
        if (parsed is null) return AuthError.InvalidToken;
        var now = clock.GetUtcNow();

        store.UseTenant(parsed.TenantId);
        var record = await store.FindRefreshTokenAsync(parsed.Hash, ct);
        if (record is null) return AuthError.InvalidToken;
        if (record.RevokedAt is not null)
        {
            // Повторне використання вже ротованого токена = ймовірна крадіжка: закриваємо всі сесії користувача.
            await store.RevokeAllRefreshTokensAsync(record.UserId, now, ct);
            return AuthError.InvalidToken;
        }
        if (record.ExpiresAt <= now) return AuthError.InvalidToken;
        if (!await store.TryRevokeRefreshTokenAsync(record.Id, now, ct)) return AuthError.InvalidToken;

        var tenant = await store.GetTenantAsync(ct);
        var user = await store.GetUserAsync(record.UserId, ct);
        if (tenant is not { IsActive: true } || user is not { IsActive: true }) return AuthError.InvalidToken;
        return await IssueAsync(user, now, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var parsed = TokenCodec.Parse(refreshToken);
        if (parsed is null) return;
        store.UseTenant(parsed.TenantId);
        if (await store.FindRefreshTokenAsync(parsed.Hash, ct) is { RevokedAt: null } record)
            await store.TryRevokeRefreshTokenAsync(record.Id, clock.GetUtcNow(), ct);
    }

    public async Task<AuthResult<UserDto>> AcceptInviteAsync(AcceptInviteRequest req, CancellationToken ct)
    {
        var parsed = TokenCodec.Parse(req.Token);
        if (parsed is null) return InviteInvalid;
        if (PasswordRules.Check(req.Password, options.MinPasswordLength) is { } weak) return weak;

        var now = clock.GetUtcNow();
        store.UseTenant(parsed.TenantId);
        var tenant = await store.GetTenantAsync(ct);
        if (tenant is not { IsActive: true }) return InviteInvalid;
        var invite = await store.FindInviteByHashAsync(parsed.Hash, ct);
        if (invite is null || invite.AcceptedAt is not null || invite.RevokedAt is not null || invite.ExpiresAt <= now)
            return InviteInvalid;

        var (outcome, user) = await store.AcceptInviteAsync(
            invite.Id, new NewUser(invite.Email, req.FullName.Trim(), hasher.Hash(req.Password), invite.Role, invite.SpecialistId), now, ct);
        return outcome switch
        {
            WriteOutcome.Ok => ToDto(user!),
            WriteOutcome.Conflict => AuthError.Conflict("user_exists", "A user with this email or specialist profile already exists."),
            _ => InviteInvalid,
        };
    }

    public async Task<AuthResult<UserDto>> MeAsync(Guid userId, CancellationToken ct) =>
        await store.GetUserAsync(userId, ct) is { IsActive: true } u ? ToDto(u) : AuthError.InvalidToken;

    private static readonly AuthError InviteInvalid = AuthError.NotFound("invite_invalid", "Invite is invalid, used or expired.");

    private AuthError RejectUnknown(string password)
    {
        hasher.Verify(password, _dummyHash ??= hasher.Hash(Guid.NewGuid().ToString("N")));
        return AuthError.InvalidCredentials;
    }

    private async Task<TokenResponse> IssueAsync(UserRecord user, DateTimeOffset now, CancellationToken ct)
    {
        var access = tokens.Issue(user, now);
        var (refresh, hash) = TokenCodec.Generate(user.TenantId);
        await store.AddRefreshTokenAsync(user.Id, hash, now.AddDays(options.RefreshTokenDays), ct);
        return new TokenResponse(access.Token, (int)(access.ExpiresAt - now).TotalSeconds, refresh, ToDto(user));
    }

    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    internal static UserDto ToDto(UserRecord u) =>
        new(u.Id, u.Email, u.FullName, u.Role, u.SpecialistId, u.IsActive, u.LastLoginAt);
}

public static class PasswordRules
{
    public const int MaxLength = 128;

    public static AuthError? Check(string? password, int minLength) =>
        password is null || password.Length < minLength || password.Length > MaxLength
            ? AuthError.Validation("weak_password", $"Password must be {minLength}-{MaxLength} characters.")
            : null;
}
