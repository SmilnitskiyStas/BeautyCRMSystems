using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Логін (глобальна ідентичність, ADR-024 / §31), вибір бізнесу, перемикання, refresh з ротацією, вихід, прийняття запрошення.
/// Нічого не логує (паролі, токени, пошта, slug); метрики й логи без ПІІ (§31.12).
/// </summary>
public sealed class AuthService(
    IAuthStore store, IIdentityStore identities, IPasswordHasher hasher, ITokenIssuer tokens, AuthOptions options, TimeProvider clock,
    LoginDummyHash dummyHash, IServiceScopeFactory scopes, ILogger<AuthService> logger)
{
    private int TicketSeconds => Math.Clamp(options.LoginTicketSeconds, 60, 600);

    /// <summary>§31.2.1: порядок кроків обов'язковий. Тіло запиту вже пройшло ModelState, а IP- і email-ліміти — на рівні API.</summary>
    public async Task<AuthResult<LoginOutcome>> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var email = NormalizeEmail(req.Email);
        var legacySlug = string.IsNullOrWhiteSpace(req.Tenant) ? null : req.Tenant.Trim().ToLowerInvariant();
        if (legacySlug is not null)
        {
            AuthMetrics.LegacyTenantUsed();
            logger.LogInformation("auth_login_legacy_tenant");
        }

        // 3. блокування за ключем пошти — ДО пошуку ідентичності: шлях 423 однаковий для будь-якої пошти (F-2); хеш не рахується
        var pepper = options.LoginAttemptKeyPepper ?? throw new InvalidOperationException("Auth:LoginAttemptKeyPepper is not configured.");
        var key = LoginKey.Compute(pepper, email);
        if (await identities.IsLockedAsync(key, now, ct))
        {
            AuthMetrics.LoginOutcome("locked");
            return AccountLocked;
        }

        // 4-5. невідома або pending пошта: фіктивний хеш із тим самим cost, рівно одна перевірка bcrypt
        var identity = await identities.FindAsync(email, ct);
        if (identity is not { IsActive: true })
        {
            hasher.Verify(req.Password, dummyHash.Value);
            return await RejectLoginAsync(key, null, now, ct);
        }

        // 6. рівно одна перевірка bcrypt на кожному шляху
        if (!hasher.Verify(req.Password, identity.PasswordHash!))
            return await RejectLoginAsync(key, identity.Id, now, ct);

        // 7. список членств — лише після успішної перевірки пароля
        var memberships = await identities.MembershipsAsync(identity.Id, ct);
        if (legacySlug is not null)
            memberships = [.. memberships.Where(m => string.Equals(m.TenantSlug, legacySlug, StringComparison.OrdinalIgnoreCase))];

        // 8. Q-C1: правильний пароль без придатного членства = невдала спроба (login_succeeded не викликається)
        if (memberships.Count == 0)
            return await RejectLoginAsync(key, identity.Id, now, ct);

        // 9.
        await identities.ResetAttemptsAsync(key, ct);
        await identities.LoginSucceededAsync(identity.Id, now, ct);

        // 10.
        if (memberships.Count == 1)
        {
            var response = await IssueForMembershipAsync(memberships[0], now, ct);
            if (response is null) return AuthError.InvalidCredentials; // членство змінилося між кроками 7 і 10
            AuthMetrics.LoginOutcome("token");
            return new LoginOutcome(response, null);
        }

        var ticketBytes = RandomNumberGenerator.GetBytes(32);
        await identities.IssueTicketAsync(identity.Id, SHA256.HashData(ticketBytes), now.AddSeconds(TicketSeconds), ct);
        AuthMetrics.LoginOutcome("selection");
        return new LoginOutcome(null, new LoginSelectionResponse(
            true, Base64Url(ticketBytes), TicketSeconds,
            [.. Sort(memberships).Select(m => new BusinessOption(m.TenantId, m.TenantName, m.Role))]));
    }

    /// <summary>§31.3: одна відповідь на всі причини; квиток гаситься будь-яким запитом із чинним квитком (Q-C5).</summary>
    public async Task<AuthResult<TokenResponse>> SelectAsync(LoginSelectRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Q-C5: квиток відомого формату гаситься завжди, навіть коли tenantId відсутній або порожній (функція в БД поверне NULL)
        if (DecodeTicket(req.LoginTicket) is not { } ticket) return SelectInvalid();
        var tenantId = req.TenantId ?? Guid.Empty;

        var userId = await identities.ConsumeTicketAsync(SHA256.HashData(ticket), tenantId, now, ct);
        if (userId is null) return SelectInvalid();

        store.UseTenant(tenantId);
        var user = await store.GetUserAsync(userId.Value, ct);
        var tenant = await store.GetTenantAsync(ct);
        if (user is not { IsActive: true, State: UserStates.Active } || tenant is not { IsActive: true }) return SelectInvalid();

        await store.SetLastLoginAsync(user.Id, now, ct);
        AuthMetrics.SelectOutcome("token");
        return await IssueAsync(store, user, now, ct);
    }

    /// <summary>§31.4: членства ідентичності поточного <c>sub</c> (ідентичність береться з users.identity_id, не з токена).</summary>
    public async Task<AuthResult<IReadOnlyList<MembershipDto>>> MembershipsAsync(Guid userId, Guid currentTenantId, CancellationToken ct)
    {
        var me = await store.GetUserAsync(userId, ct);
        if (me is null) return AuthError.InvalidToken;
        var rows = await identities.MembershipsAsync(me.IdentityId, ct);
        IReadOnlyList<MembershipDto> list = [.. Sort(rows).Select(m => new MembershipDto(m.TenantId, m.TenantName, m.Role, m.TenantId == currentTenantId))];
        return new AuthResult<IReadOnlyList<MembershipDto>>(list, null);
    }

    /// <summary>§31.5: перевірки 3-8 (кроки 1-2 — JWT, middleware, ModelState).</summary>
    public async Task<AuthResult<TokenResponse>> SwitchAsync(Guid userId, Guid currentTenantId, SwitchRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var target = req.TenantId ?? Guid.Empty;
        if (target == currentTenantId) return SwitchFail("same_tenant", AuthError.Validation("same_tenant", "The target business is the current one."));

        // 4. refresh-токен поточної сесії: без каскадного відкликання всіх сесій
        var parsed = TokenCodec.Parse(req.RefreshToken);
        if (parsed is null || parsed.TenantId != currentTenantId) return SwitchFail("invalid_token", AuthError.InvalidToken);
        store.UseTenant(currentTenantId);
        var record = await store.FindRefreshTokenAsync(parsed.Hash, ct);
        if (record is null || record.RevokedAt is not null || record.ExpiresAt <= now || record.UserId != userId)
            return SwitchFail("invalid_token", AuthError.InvalidToken);
        var me = await store.GetUserAsync(userId, ct);
        if (me is not { IsActive: true }) return SwitchFail("invalid_token", AuthError.InvalidToken);
        // §31.6 (defense in depth): токен виданий до sessions_valid_after недійсний і для switch (як у refresh)
        if (me.SessionsValidAfter is { } validAfter && record.CreatedAt < validAfter) return SwitchFail("invalid_token", AuthError.InvalidToken);

        // 5. цільове членство — серед активних членств ідентичності джерела
        var membership = (await identities.MembershipsAsync(me.IdentityId, ct)).FirstOrDefault(m => m.TenantId == target);
        if (membership is null) return SwitchFail("not_found", MembershipNotFound);

        // 6. новий DI-scope: tenant контексту запиту змінити не можна (app.tenant_id зафіксовано на з'єднанні); TOCTOU (F-11):
        //    перечитати рядок цілі під RLS цілі й tenants.status
        await using var scope = scopes.CreateAsyncScope();
        var targetStore = scope.ServiceProvider.GetRequiredService<IAuthStore>();
        targetStore.UseTenant(target);
        var targetUser = await targetStore.GetUserAsync(membership.UserId, ct);
        var targetTenant = await targetStore.GetTenantAsync(ct);
        if (targetUser is not { IsActive: true, State: UserStates.Active } || targetUser.IdentityId != me.IdentityId || targetTenant is not { IsActive: true })
            return SwitchFail("not_found", MembershipNotFound);

        // 7. атомарне відкликання поточного refresh-токена
        if (!await store.TryRevokeRefreshTokenAsync(record.Id, now, ct)) return SwitchFail("invalid_token", AuthError.InvalidToken);

        // 8. видача в scope кроку 6
        await targetStore.SetLastLoginAsync(targetUser.Id, now, ct);
        AuthMetrics.SwitchOutcome("token");
        return await IssueAsync(targetStore, targetUser, now, ct);
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
        if (tenant is not { IsActive: true } || user is not { IsActive: true, State: UserStates.Active }) return AuthError.InvalidToken;
        // §31.6 (F-6): токен виданий до sessions_valid_after (created_at токена, не issued_at) або ідентичність неактивна; без каскаду
        if (user.SessionsValidAfter is { } validAfter && record.CreatedAt < validAfter) return AuthError.InvalidToken;
        if (!await identities.IsActiveAsync(user.IdentityId, ct)) return AuthError.InvalidToken;
        return await IssueAsync(store, user, now, ct);
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
            invite, new NewUser(invite.Email, req.FullName.Trim(), hasher.Hash(req.Password), invite.Role, invite.SpecialistId), now, ct);
        return outcome switch
        {
            WriteOutcome.Ok => ToDto(user!),
            WriteOutcome.Conflict => AuthError.Conflict("user_exists", "A user with this email or specialist profile already exists."),
            // §31.8.2: пошту за цей час зайняла інша ідентичність; запрошення лишається pending (транзакція відкочена)
            WriteOutcome.EmailInUse => UserAdminService.EmailInUse,
            // F-4: квота нових ідентичностей tenant; запрошення лишається pending, Retry-After - до звільнення місця у вікні 24 год
            WriteOutcome.QuotaExceeded => AuthError.RateLimited(await store.IdentityQuotaRetryAfterSecondsAsync(ct)),
            _ => InviteInvalid,
        };
    }

    public async Task<AuthResult<UserDto>> MeAsync(Guid userId, CancellationToken ct) =>
        await store.GetUserAsync(userId, ct) is { IsActive: true } u ? ToDto(u) : AuthError.InvalidToken;

    private static readonly AuthError InviteInvalid = AuthError.NotFound("invite_invalid", "Invite is invalid, used or expired.");

    // Єдина відповідь для заблокованого ключа пошти — наявної, невідомої й pending (F-2).
    private static readonly AuthError AccountLocked = AuthError.Locked("account_locked", "Too many failed attempts. Try again later.");

    private static readonly AuthError LoginTicketInvalid = AuthError.Unauthorized("login_ticket_invalid", "Login ticket is invalid or expired.");

    private static readonly AuthError MembershipNotFound = AuthError.NotFound("membership_not_found", "Membership not found.");

    private static AuthError SelectInvalid()
    {
        AuthMetrics.SelectOutcome("invalid");
        return LoginTicketInvalid;
    }

    private static AuthError SwitchFail(string outcome, AuthError error)
    {
        AuthMetrics.SwitchOutcome(outcome);
        return error;
    }

    /// <summary>
    /// Невдала спроба входу (невідома/pending пошта, невірний пароль, Q-C1): рівно один виклик <c>auth_login_attempt_failed</c>, 401
    /// (навіть якщо ця спроба досягла порогу: 423 лише на наступний запит).
    /// </summary>
    private async Task<AuthResult<LoginOutcome>> RejectLoginAsync(byte[] key, Guid? identityId, DateTimeOffset now, CancellationToken ct)
    {
        var locked = await identities.RegisterFailedAsync(
            key, identityId, options.MaxFailedAttempts, options.LoginAttemptWindowSeconds, options.LockoutMinutes * 60, now, ct);
        if (locked)
        {
            AuthMetrics.LockoutReached();
            logger.LogWarning("auth_lockout");
        }
        AuthMetrics.LoginOutcome("invalid");
        return AuthError.InvalidCredentials;
    }

    /// <summary>users.last_login_at, refresh-токен і JWT для членства (поточний tenant запиту ще не зафіксовано: логін анонімний).</summary>
    private async Task<TokenResponse?> IssueForMembershipAsync(MembershipRow membership, DateTimeOffset now, CancellationToken ct)
    {
        store.UseTenant(membership.TenantId);
        var user = await store.GetUserAsync(membership.UserId, ct);
        if (user is not { IsActive: true, State: UserStates.Active }) return null;
        await store.SetLastLoginAsync(user.Id, now, ct);
        return await IssueAsync(store, user, now, ct);
    }

    private async Task<TokenResponse> IssueAsync(IAuthStore target, UserRecord user, DateTimeOffset now, CancellationToken ct)
    {
        var access = tokens.Issue(user, now);
        var (refresh, hash) = TokenCodec.Generate(user.TenantId);
        await target.AddRefreshTokenAsync(user.Id, hash, now.AddDays(options.RefreshTokenDays), ct);
        return new TokenResponse(access.Token, (int)(access.ExpiresAt - now).TotalSeconds, refresh, ToDto(user));
    }

    /// <summary>§31.2: <c>name</c> без урахування регістру (InvariantCultureIgnoreCase), за рівності — за tenantId.</summary>
    private static IEnumerable<MembershipRow> Sort(IEnumerable<MembershipRow> rows) =>
        rows.OrderBy(m => m.TenantName, StringComparer.InvariantCultureIgnoreCase).ThenBy(m => m.TenantId);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>43 символи base64url без padding = 32 байти; інакше null (формат невірний, без звернення до БД).</summary>
    internal static byte[]? DecodeTicket(string? ticket)
    {
        if (ticket is not { Length: 43 }) return null;
        foreach (var c in ticket)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        try
        {
            var bytes = Convert.FromBase64String(ticket.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
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
