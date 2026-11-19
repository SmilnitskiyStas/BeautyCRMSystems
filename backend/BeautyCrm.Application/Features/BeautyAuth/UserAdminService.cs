using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>Запрошення й керування користувачами tenant. Усі виклики йдуть під RLS tenant поточного користувача.</summary>
public sealed class UserAdminService(IAuthStore store, AuthOptions options, TimeProvider clock)
{
    public async Task<AuthResult<InviteCreatedDto>> CreateInviteAsync(Actor actor, CreateInviteRequest req, CancellationToken ct)
    {
        var role = req.Role.Trim().ToLowerInvariant();
        if (role is not (Roles.Admin or Roles.Deputy or Roles.Specialist))
            return AuthError.Validation("invalid_role", "Role must be admin, deputy or specialist.");
        // team.manage (§30.9.5): ключ перевіряє [RequirePermission]; тут повторно для викликів поза HTTP (StaffInviteService)
        if (!actor.Access.AtLeast(PermissionKeys.TeamManage, PermissionLevels.Manage))
            return AuthError.ForbiddenPermission(PermissionKeys.TeamManage, PermissionLevels.Manage);
        if (!RolePolicy.CanInvite(actor.Role, role))
            return AuthError.Forbidden("forbidden_role", "You cannot invite this role.");

        if (role == Roles.Specialist)
        {
            if (req.SpecialistId is not { } specialistId)
                return AuthError.Validation("invalid_specialist", "specialistId is required for role specialist.");
            if (!await store.SpecialistExistsAsync(specialistId, ct))
                return AuthError.Validation("invalid_specialist", "Specialist profile not found.");
            if (!await store.SpecialistIsActiveAsync(specialistId, ct))
                return AuthError.Conflict("specialist_inactive", "The specialist profile is deactivated; reactivate it first.");
            if (await store.SpecialistHasUserAsync(specialistId, ct))
                return AuthError.Conflict("specialist_linked", "This specialist profile already has a user.");
        }
        else if (req.SpecialistId is not null)
            return AuthError.Validation("invalid_specialist", "specialistId is only for role specialist.");

        // повний матеріалізований знімок прав (B1): пресет + перевизначення, кожен ключ обрізано стелею запрошувача
        var snapshot = AccessPolicy.BuildInviteSnapshot(actor.Access, role, req.Permissions);
        if (!snapshot.IsOk) return snapshot.Error!;

        var email = AuthService.NormalizeEmail(req.Email);
        if ((await store.ListUsersAsync(ct)).Any(u => u.Email == email))
            return AuthError.Conflict("user_exists", "A user with this email already exists.");

        var now = clock.GetUtcNow();
        var (token, hash) = TokenCodec.Generate(actor.TenantId);
        var permissionsJson = System.Text.Json.JsonSerializer.Serialize(snapshot.Value!.Permissions);
        var auditJson = System.Text.Json.JsonSerializer.Serialize(
            new { role, specialistId = role == Roles.Specialist ? req.SpecialistId : null, clamped = snapshot.Value.Clamped },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var invite = await store.AddInviteAsync(
            new NewInvite(email, role, role == Roles.Specialist ? req.SpecialistId : null, hash, now.AddDays(options.InviteDays), actor.UserId,
                permissionsJson, auditJson), ct);
        if (invite is null) return AuthError.Conflict("specialist_inactive", "The specialist profile is deactivated; reactivate it first.");
        return new InviteCreatedDto(ToDto(invite, now), token, snapshot.Value.Clamped);
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
        return await store.RevokeInviteAsync(inviteId, clock.GetUtcNow(), actor.UserId, ct)
            ? true
            : AuthError.Conflict("invite_closed", "Invite is already accepted or revoked.");
    }

    /// <summary>Список з <c>accessVersion</c> і <c>manageable</c> (§30.6.1).</summary>
    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(Actor actor, CancellationToken ct) =>
        (await store.ListUsersAsync(ct)).Select(u => AuthService.ToDto(u) with
        {
            AccessVersion = u.AccessVersion,
            Manageable = u.Id != actor.UserId && RolePolicy.CanManage(actor.Role, u.Role),
        }).ToList();

    private static InviteDto ToDto(InviteRecord i, DateTimeOffset now) => new(
        i.Id, i.Email, i.Role, i.SpecialistId, i.ExpiresAt,
        i.AcceptedAt is not null ? "accepted" : i.RevokedAt is not null ? "revoked" : i.ExpiresAt <= now ? "expired" : "pending",
        AccessPolicy.Materialize(ParseSnapshot(i.PermissionsJson)), i.InvitedByUserId);

    private static Dictionary<string, string>? ParseSnapshot(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Операції оператора платформи: tenant + власник, модулі, статус. Публічної самореєстрації немає.</summary>
public sealed class PlatformTenantService(IAuthStore store, IPasswordHasher hasher, AuthOptions options, TimeProvider clock)
{
    public async Task<AuthResult<TenantCreatedDto>> CreateTenantAsync(CreateTenantRequest req, CancellationToken ct)
    {
        var slug = TenantRules.NormalizeSlug(req.Slug);
        if (slug is null) return AuthError.Validation("invalid_slug", "Slug: 3-64 chars, a-z, 0-9, hyphen.");
        if (BeautyPublicBooking.PublicTenantResolver.ReservedSlugs.Contains(slug))
            return AuthError.Validation("slug_reserved", "This slug is reserved.");
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
    private static IReadOnlyList<string>? NormalizeModules(IReadOnlyList<string>? modules) => TenantRules.NormalizeModules(modules);
}
