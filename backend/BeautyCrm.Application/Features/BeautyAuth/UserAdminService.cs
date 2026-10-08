using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>Запрошення й керування користувачами tenant. Усі виклики йдуть під RLS tenant поточного користувача.</summary>
public sealed class UserAdminService(IAuthStore store, AuthOptions options, TimeProvider clock)
{
    public async Task<AuthResult<InviteCreatedDto>> CreateInviteAsync(Actor actor, CreateInviteRequest req, CancellationToken ct)
    {
        var role = req.Role.Trim().ToLowerInvariant();
        if (role is not (Roles.Admin or Roles.Specialist))
            return AuthError.Validation("invalid_role", "Role must be admin or specialist.");
        if (!RolePolicy.CanInvite(actor.Role, role))
            return AuthError.Forbidden("forbidden_role", "You cannot invite this role.");

        if (role == Roles.Specialist)
        {
            if (req.SpecialistId is not { } specialistId)
                return AuthError.Validation("specialist_required", "specialistId is required for role specialist.");
            if (!await store.SpecialistExistsAsync(specialistId, ct))
                return AuthError.Validation("specialist_not_found", "Specialist profile not found.");
            if (!await store.SpecialistIsActiveAsync(specialistId, ct))
                return AuthError.Conflict("specialist_inactive", "The specialist profile is deactivated; reactivate it first.");
            if (await store.SpecialistHasUserAsync(specialistId, ct))
                return AuthError.Conflict("specialist_linked", "This specialist profile already has a user.");
        }
        else if (req.SpecialistId is not null)
            return AuthError.Validation("specialist_not_allowed", "specialistId is only for role specialist.");

        var email = AuthService.NormalizeEmail(req.Email);
        if ((await store.ListUsersAsync(ct)).Any(u => u.Email == email))
            return AuthError.Conflict("user_exists", "A user with this email already exists.");

        var now = clock.GetUtcNow();
        var (token, hash) = TokenCodec.Generate(actor.TenantId);
        var invite = await store.AddInviteAsync(
            new NewInvite(email, role, role == Roles.Specialist ? req.SpecialistId : null, hash, now.AddDays(options.InviteDays), actor.UserId), ct);
        if (invite is null) return AuthError.Conflict("specialist_inactive", "The specialist profile is deactivated; reactivate it first.");
        return new InviteCreatedDto(ToDto(invite, now), token);
    }

    public async Task<IReadOnlyList<InviteDto>> ListInvitesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return (await store.ListInvitesAsync(ct)).Select(i => ToDto(i, now)).ToList();
    }

    public async Task<AuthResult<bool>> RevokeInviteAsync(Actor actor, Guid inviteId, CancellationToken ct)
    {
        var invite = (await store.ListInvitesAsync(ct)).FirstOrDefault(i => i.Id == inviteId);
        if (invite is null) return AuthError.NotFound("invite_not_found", "Invite not found.");
        if (!RolePolicy.CanManage(actor.Role, invite.Role)) return AuthError.Forbidden("forbidden_role", "You cannot manage this invite.");
        return await store.RevokeInviteAsync(inviteId, clock.GetUtcNow(), ct)
            ? true
            : AuthError.Conflict("invite_closed", "Invite is already accepted or revoked.");
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct) =>
        (await store.ListUsersAsync(ct)).Select(AuthService.ToDto).ToList();

    public async Task<AuthResult<UserDto>> SetUserActiveAsync(Actor actor, Guid userId, bool active, CancellationToken ct)
    {
        var target = await store.GetUserAsync(userId, ct);
        if (target is null) return AuthError.NotFound("user_not_found", "User not found.");
        if (target.Id == actor.UserId) return AuthError.Validation("cannot_modify_self", "You cannot change your own status.");
        if (!RolePolicy.CanManage(actor.Role, target.Role)) return AuthError.Forbidden("forbidden_role", "You cannot manage this user.");

        await store.SetUserActiveAsync(userId, active, clock.GetUtcNow(), ct);
        return AuthService.ToDto(target with { IsActive = active });
    }

    private static InviteDto ToDto(InviteRecord i, DateTimeOffset now) => new(
        i.Id, i.Email, i.Role, i.SpecialistId, i.ExpiresAt,
        i.AcceptedAt is not null ? "accepted" : i.RevokedAt is not null ? "revoked" : i.ExpiresAt <= now ? "expired" : "pending");
}

/// <summary>Операції оператора платформи: tenant + власник, модулі, статус. Публічної самореєстрації немає.</summary>
public sealed partial class PlatformTenantService(IAuthStore store, IPasswordHasher hasher, AuthOptions options, TimeProvider clock)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$")]
    private static partial Regex SlugPattern();

    public async Task<AuthResult<TenantCreatedDto>> CreateTenantAsync(CreateTenantRequest req, CancellationToken ct)
    {
        var slug = req.Slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug)) return AuthError.Validation("invalid_slug", "Slug: 3-64 chars, a-z, 0-9, hyphen.");
        var modules = NormalizeModules(req.Modules) ?? BeautyModules.All;
        if (req.Modules is not null && NormalizeModules(req.Modules) is null)
            return AuthError.Validation("invalid_modules", "Unknown module name.");
        if (PasswordRules.Check(req.Owner.Password, options.MinPasswordLength) is { } weak) return weak;

        var tenant = new NewTenant(Guid.NewGuid(), req.Name.Trim(), slug, modules);
        var owner = new NewUser(AuthService.NormalizeEmail(req.Owner.Email), req.Owner.FullName.Trim(),
            hasher.Hash(req.Owner.Password), Roles.Owner, null);
        var (outcome, ownerId) = await store.CreateTenantWithOwnerAsync(tenant, owner, ct);
        return outcome == WriteOutcome.Ok
            ? new TenantCreatedDto(tenant.Id, tenant.Name, slug, modules, ownerId)
            : AuthError.Conflict("tenant_slug_taken", "A tenant with this slug already exists.");
    }

    public async Task<AuthResult<bool>> UpdateTenantAsync(Guid tenantId, UpdateTenantRequest req, CancellationToken ct)
    {
        if (req.Modules is null && req.Status is null) return AuthError.Validation("nothing_to_change", "Provide modules and/or status.");
        if (req.Status is not null and not ("active" or "suspended")) return AuthError.Validation("invalid_status", "Status must be active or suspended.");
        var modules = req.Modules is null ? null : NormalizeModules(req.Modules);
        if (req.Modules is not null && modules is null) return AuthError.Validation("invalid_modules", "Unknown module name.");

        return await store.UpdateTenantAsync(tenantId, modules, req.Status, clock.GetUtcNow(), ct) == WriteOutcome.Ok
            ? true
            : AuthError.NotFound("tenant_not_found", "Tenant not found.");
    }

    /// <summary>null = містить невідомий модуль.</summary>
    private static IReadOnlyList<string>? NormalizeModules(IReadOnlyList<string>? modules)
    {
        if (modules is null) return null;
        var list = modules.Select(m => m.Trim().ToLowerInvariant()).Distinct().ToList();
        return list.All(m => BeautyModules.All.Contains(m)) ? list : null;
    }
}

/// <summary>Обмеження доступу спеціаліста: лише власні записи. Чужі записи недоступні (404, без витоку існування).</summary>
public sealed class AppointmentAccessService(BookingService booking)
{
    /// <summary>Для спеціаліста — завжди власний профіль (навіть без профілю: порожня вибірка), для решти — як запитано.</summary>
    public static Guid? EffectiveSpecialistId(Actor actor, Guid? requested) =>
        actor.Role == Roles.Specialist ? actor.SpecialistId ?? Guid.Empty : requested;

    public static bool CanCreateFor(Actor actor, Guid specialistId) =>
        actor.Role != Roles.Specialist || (actor.SpecialistId is { } own && own == specialistId);

    public async Task<bool> CanAccessAsync(Actor actor, Guid appointmentId, CancellationToken ct)
    {
        if (actor.Role != Roles.Specialist) return true; // існування перевірить сам сервіс (404)
        var r = await booking.GetAsync(appointmentId, ct);
        return r.IsOk && actor.SpecialistId is { } own && r.Value!.SpecialistId == own;
    }
}
