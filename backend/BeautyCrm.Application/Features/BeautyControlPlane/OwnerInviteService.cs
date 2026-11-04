using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Application.Features.BeautyControlPlane;

// ---------- DTO (beauty-contracts.md §27.8) ----------

/// <summary>Тіло inspect/accept. Поля nullable: відсутній/некоректний токен = той самий 410 invite_invalid, а не 422.</summary>
public sealed record OwnerInviteTokenRequest(string? Token);

public sealed record OwnerInviteAcceptRequest(string? Token, string? Password);

public sealed record OwnerInviteInspectResponse(string TenantName, string Email, string ExpiresAt);

// ---------- порт ----------

public enum OwnerInviteState { Valid, Invalid, TenantSuspended }

public sealed record OwnerInviteRow(Guid InviteId, Guid TenantId, Guid UserId, string TenantName, string Email, DateTimeOffset ExpiresAt);

/// <summary>Сесія однієї транзакції після атомарного погашення запрошення, уже прив'язана до tenant (app.tenant_id).</summary>
public interface IOwnerAcceptSession
{
    OwnerInviteRow Invite { get; }

    /// <summary>password_hash + state=active лише для власника в стані invited без пароля. null = гарантію порушено (відкат).</summary>
    Task<UserRecord?> ActivateOwnerAsync(string passwordHash, DateTimeOffset now, CancellationToken ct);

    Task AddRefreshTokenAsync(Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct);

    Task AddAuditAsync(CpAuditEntry entry, CancellationToken ct);
}

public interface IOwnerInviteStore
{
    /// <summary>Лише читання. Valid + Row; Invalid / TenantSuspended (лише для інакше чинного запрошення) без Row.</summary>
    Task<(OwnerInviteState State, OwnerInviteRow? Row)> InspectAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Одна транзакція: lookup за хешем -> app.tenant_id (set_config local) -> блокування tenant -> перевірка статусу ->
    /// <c>UPDATE platform_owner_invites ... WHERE token_hash AND used_at IS NULL ... RETURNING</c> -> <paramref name="onRedeemed"/> -> commit.
    /// Якщо callback повернув null або кинув виняток - відкат (запрошення НЕ погашене).
    /// </summary>
    Task<(OwnerInviteState State, TokenResponse? Tokens)> AcceptAsync(
        byte[] tokenHash, DateTimeOffset now, Func<IOwnerAcceptSession, Task<TokenResponse?>> onRedeemed, CancellationToken ct);
}

/// <summary>
/// Публічні inspect/accept запрошення власника (ADR-018, §27.8). Будь-який непридатний токен (формат, невідомий, прострочений,
/// використаний, відкликаний, власник уже активний) - однаковий 410 invite_invalid. Токен і пароль ніде не логуються.
/// </summary>
public sealed partial class OwnerInviteService(
    IOwnerInviteStore store, IPasswordHasher hasher, ITokenIssuer tokens, AuthOptions options, TimeProvider clock)
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenPattern();

    private static readonly AuthError InviteInvalid = AuthError.Gone("invite_invalid", "Invite is invalid, used or expired.");
    private static readonly AuthError TenantSuspended = AuthError.Forbidden("tenant_suspended", "The business is suspended.");

    /// <summary>sha256(ASCII(token)) - той самий код, що й при видачі (<see cref="ControlPlaneService.HashToken"/>); null = формат неможливий.</summary>
    public static byte[]? TryHash(string? token) =>
        token is not null && TokenPattern().IsMatch(token) ? ControlPlaneService.HashToken(token) : null;

    public async Task<AuthResult<OwnerInviteInspectResponse>> InspectAsync(string? token, CancellationToken ct)
    {
        if (TryHash(token) is not { } hash) return InviteInvalid;
        var (state, row) = await store.InspectAsync(hash, clock.GetUtcNow(), ct);
        return state switch
        {
            OwnerInviteState.Valid => new OwnerInviteInspectResponse(row!.TenantName, MaskEmail(row.Email), ControlPlaneService.Iso(row.ExpiresAt)),
            OwnerInviteState.TenantSuspended => TenantSuspended,
            _ => InviteInvalid,
        };
    }

    public async Task<AuthResult<TokenResponse>> AcceptAsync(OwnerInviteAcceptRequest req, string? ip, CancellationToken ct)
    {
        // Політика пароля не залежить від токена, тому її відмова нічого не розкриває про запрошення (і не гасить токен).
        if (PasswordRules.Check(req.Password, options.MinPasswordLength) is { } weak) return weak;
        if (TryHash(req.Token) is not { } hash) return InviteInvalid;
        var password = req.Password!;
        var now = clock.GetUtcNow();

        var (state, result) = await store.AcceptAsync(hash, now, async s =>
        {
            var user = await s.ActivateOwnerAsync(hasher.Hash(password), now, ct);
            if (user is null) return null;
            var access = tokens.Issue(user, now);
            var (refresh, refreshHash) = TokenCodec.Generate(user.TenantId);
            await s.AddRefreshTokenAsync(user.Id, refreshHash, now.AddDays(options.RefreshTokenDays), ct);
            await s.AddAuditAsync(new CpAuditEntry(
                "owner_invite.accepted", "owner", user.Id.ToString(), null, null, null, ip,
                new Dictionary<string, object?> { ["user_id"] = user.Id, ["invite_id"] = s.Invite.InviteId }), ct);
            return new TokenResponse(access.Token, (int)(access.ExpiresAt - now).TotalSeconds, refresh, AuthService.ToDto(user));
        }, ct);

        return state switch
        {
            OwnerInviteState.Valid when result is not null => result,
            OwnerInviteState.TenantSuspended => TenantSuspended,
            _ => InviteInvalid,
        };
    }

    /// <summary>Перший символ локальної частини + "***" + "@домен" (§27.8).</summary>
    internal static string MaskEmail(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0) return "***";
        return email[..1] + "***" + email[at..];
    }
}
