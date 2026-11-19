using System.Text;
using System.Text.Json;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// ADR-023 §2.6, контракт §30.10 (TASK-851): читання й зміна доступу користувача, зміна ролі, скидання до пресету, вимкнення/ввімкнення,
/// журнал аудиту. Запис = одна транзакція: FOR SHARE рядка актора (його права й активність перечитуються під блокуванням), далі
/// FOR UPDATE рядка цілі; перевірки йдуть у фіксованому порядку (§30.10), зупинка на першій помилці. Аудит і access_version + 1 - у
/// тій самій транзакції. Пресет як джерело прав тут використовується лише для запису (B2).
/// </summary>
public sealed class AccessAdminService(IAccessAdminStore store, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] AssignableRoles = [Roles.Admin, Roles.Deputy];

    private static AuthError NeedTeamManage => AuthError.ForbiddenPermission(PermissionKeys.TeamManage, PermissionLevels.Manage);
    private static readonly AuthError RankDenied = AuthError.Forbidden("forbidden_role", "You cannot manage this user.");
    private static readonly AuthError UserNotFound = AuthError.NotFound("user_not_found", "User not found.");
    private static readonly AuthError SessionRevoked = AuthError.Unauthorized("session_revoked", "The session is no longer valid.");
    private static readonly AuthError SelfModify = AuthError.Validation("cannot_modify_self", "You cannot change your own access.");
    private static readonly AuthError VersionRequired = AuthError.Validation("expected_version_required", "expectedVersion must be an integer >= 1.");

    // ---------- читання (GET /api/users/{id}/access) ----------

    public async Task<AuthResult<UserAccessDto>> GetAccessAsync(Actor actor, Guid userId, CancellationToken ct)
    {
        if (!actor.Access.AtLeast(PermissionKeys.TeamManage, PermissionLevels.Manage)) return NeedTeamManage;
        await using var session = await store.BeginAsync(ct);
        var target = await session.GetUserAsync(userId, ct);
        if (target is null) return UserNotFound;
        return BuildDto(actor.Access, actor.UserId, target, await session.GetModulesAsync(ct));
    }

    // ---------- запис (PUT / reset) ----------

    public Task<AuthResult<UserAccessDto>> UpdateAsync(Actor actor, Guid userId, UpdateAccessRequest req, CancellationToken ct) =>
        WriteAsync(actor, userId, req.ExpectedVersion, req.Role, req.Permissions, reset: false, req.DryRun, ct);

    public Task<AuthResult<UserAccessDto>> ResetAsync(Actor actor, Guid userId, ResetAccessRequest req, CancellationToken ct) =>
        WriteAsync(actor, userId, req.ExpectedVersion, null, null, reset: true, req.DryRun, ct);

    private async Task<AuthResult<UserAccessDto>> WriteAsync(
        Actor actor, Guid userId, int? expectedVersion, string? role, IReadOnlyDictionary<string, string>? explicitValues,
        bool reset, bool dryRun, CancellationToken ct)
    {
        await using var session = await store.BeginAsync(ct);

        // 1 (під блокуванням): актор перечитується з БД; неактивний - 401 session_revoked
        var actorRow = await session.LockActorAsync(actor.UserId, ct);
        if (!IsLive(actorRow)) return SessionRevoked;
        var modules = await session.GetModulesAsync(ct);
        var actorAccess = AccessSet.Build(actorRow!.Role, actorRow.Permissions, modules, false);
        if (!actorAccess.AtLeast(PermissionKeys.TeamManage, PermissionLevels.Manage)) return NeedTeamManage;

        // 3 раніше за блокування цілі: власний рядок уже заблоковано, а друге блокування (UPDATE) могло б дедлокнути з паралельним запитом
        if (userId == actor.UserId) return SelfModify;
        // 2
        var target = await session.LockTargetAsync(userId, ct);
        if (target is null) return UserNotFound;
        // 4
        if (!RolePolicy.CanManage(actorRow.Role, target.Role)) return RankDenied;
        // 5, 6
        if (expectedVersion is null or < 1) return VersionRequired;
        if (expectedVersion != target.AccessVersion) return Conflict(target.AccessVersion);

        var current = AccessPolicy.Materialize(target.Permissions);
        var roleChanging = false;
        var targetRole = target.Role;
        if (role is not null)
        {
            // 7
            if (role is not (Roles.Admin or Roles.Deputy or Roles.Specialist))
                return AuthError.Validation("invalid_role", "Role must be admin, deputy or specialist.");
            // 8
            if (Roles.Rank(role) >= Roles.Rank(actorRow.Role)) return RankDenied;
            // 9
            if (role != target.Role)
            {
                if (!(AssignableRoles.Contains(role) && AssignableRoles.Contains(target.Role)))
                    return AuthError.Validation("role_change_not_supported", "Only admin <-> deputy role changes are supported.");
                roleChanging = true;
                targetRole = role;
            }
        }

        var locked = actorAccess.Locked(current);
        var clamped = new List<PermissionClamp>();
        var baseSet = new Dictionary<string, string>(current, StringComparer.Ordinal);
        if (roleChanging || reset)
        {
            var preset = PermissionPresets.For(targetRole);
            foreach (var d in PermissionCatalog.All)
            {
                var wanted = preset[d.Key];
                if (locked.Contains(d.Key))
                {
                    baseSet[d.Key] = current[d.Key];
                    if (wanted != current[d.Key]) clamped.Add(new PermissionClamp(d.Key, wanted, current[d.Key], "locked"));
                    continue;
                }
                var ceiling = actorAccess.Ceiling(d.Key);
                if (d.IndexOf(wanted) > d.IndexOf(ceiling))
                {
                    clamped.Add(new PermissionClamp(d.Key, wanted, ceiling, "ceiling"));
                    wanted = ceiling;
                }
                baseSet[d.Key] = wanted;
            }
        }

        var overrides = explicitValues ?? new Dictionary<string, string>();
        // 10-13
        if (AccessPolicy.CheckExplicit(actorAccess, targetRole, baseSet, current, locked, overrides) is { } denied) return denied;
        var final = new Dictionary<string, string>(baseSet, StringComparer.Ordinal);
        foreach (var (k, v) in overrides) final[k] = v;
        // 14
        if (AccessPolicy.Violations(final) is { Count: > 0 } violations)
            return AccessPolicy.Err(AuthErrorKind.Validation, "permission_inconsistent", "The permission set is inconsistent.", ("violations", violations));

        var changes = PermissionCatalog.All.Where(d => final[d.Key] != current[d.Key])
            .Select(d => new PermissionChange(d.Key, current[d.Key], final[d.Key])).ToList();
        var roleChange = roleChanging ? new RoleChange(target.Role, targetRole) : null;

        // без змін (або dryRun): версія й аудит не чіпаються, нічого не пишемо
        if (dryRun || (changes.Count == 0 && roleChange is null))
        {
            var unchanged = BuildDto(actorAccess, actor.UserId, target, modules);
            return unchanged with { Changes = changes, RoleChange = roleChange, Clamped = clamped };
        }

        var now = clock.GetUtcNow();
        var upserts = (roleChanging || reset
                ? PermissionCatalog.All.Select(d => new KeyValuePair<string, string>(d.Key, final[d.Key]))
                : changes.Select(c => new KeyValuePair<string, string>(c.Permission, c.To)))
            .ToList();
        await session.WriteAccessAsync(userId, roleChanging ? targetRole : null, upserts, actor.UserId, now, ct);

        var action = roleChanging ? "role_changed" : reset ? "preset_reset" : "permissions_changed";
        object payload = roleChanging
            ? new { role = roleChange, permissions = changes, clamped }
            : changes;
        await session.AddAuditAsync(new AuditEntry(action, actor.UserId, userId, null, JsonSerializer.Serialize(payload, Web)), ct);

        // M5: зміна ролі або зниження team.manage цілі відкликає її pending-запрошення
        var teamLowered = current[PermissionKeys.TeamManage] == PermissionLevels.Manage && final[PermissionKeys.TeamManage] != PermissionLevels.Manage;
        if (roleChanging || teamLowered)
            await RevokeInvitesAsync(session, userId, actor.UserId, roleChanging ? "role_changed" : "team_manage_lowered", now, ct);

        var updated = (await session.GetUserAsync(userId, ct))!;
        await session.CommitAsync(ct);
        return BuildDto(actorAccess, actor.UserId, updated, modules) with { Changes = changes, RoleChange = roleChange, Clamped = clamped };
    }

    // ---------- PATCH /api/users/{id}/status ----------

    public async Task<AuthResult<UserDto>> SetStatusAsync(Actor actor, Guid userId, SetUserActiveRequest req, CancellationToken ct)
    {
        await using var session = await store.BeginAsync(ct);
        var actorRow = await session.LockActorAsync(actor.UserId, ct);
        if (!IsLive(actorRow)) return SessionRevoked;
        var modules = await session.GetModulesAsync(ct);
        var actorAccess = AccessSet.Build(actorRow!.Role, actorRow.Permissions, modules, false);
        if (!actorAccess.AtLeast(PermissionKeys.TeamManage, PermissionLevels.Manage)) return NeedTeamManage;
        if (userId == actor.UserId) return AuthError.Validation("cannot_modify_self", "You cannot change your own status.");
        var target = await session.LockTargetAsync(userId, ct);
        if (target is null) return UserNotFound;
        if (!RolePolicy.CanManage(actorRow.Role, target.Role)) return AuthError.Forbidden("forbidden_role", "You cannot manage this user.");
        if (req.ExpectedVersion is { } ev && (ev < 1 || ev != target.AccessVersion))
            return ev < 1 ? VersionRequired : Conflict(target.AccessVersion);

        if (target.IsActive == req.IsActive) return Dto(target);

        var now = clock.GetUtcNow();
        await session.SetActiveAsync(userId, req.IsActive, now, ct);
        await session.AddAuditAsync(new AuditEntry(req.IsActive ? "activated" : "deactivated", actor.UserId, userId, null,
            JsonSerializer.Serialize(new { cause = "status_change" }, Web)), ct);
        if (!req.IsActive) await RevokeInvitesAsync(session, userId, actor.UserId, "deactivated", now, ct);
        var updated = (await session.GetUserAsync(userId, ct))!;
        await session.CommitAsync(ct);
        return Dto(updated);
    }

    // ---------- GET /api/users/access-audit (лише owner) ----------

    public async Task<AuthResult<AccessAuditPageDto>> ListAuditAsync(
        Actor actor, Guid? userId, Guid? inviteId, string? cursor, int? take, CancellationToken ct)
    {
        if (actor.Role != Roles.Owner) return AuthError.Forbidden("forbidden_role", "The access journal is available to the owner only.");
        DateTimeOffset? cursorAt = null;
        Guid? cursorId = null;
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var at, out var id)) return AuthError.Validation("invalid_cursor", "The cursor is invalid.");
            (cursorAt, cursorId) = (at, id);
        }
        var size = Math.Clamp(take ?? 50, 1, 100);
        var rows = await store.ListAuditAsync(new AuditQuery(userId, inviteId, cursorAt, cursorId, size + 1), ct);
        var page = rows.Take(size).ToList();
        string? next = rows.Count > size ? EncodeCursor(page[^1].CreatedAt, page[^1].Id) : null;
        return new AccessAuditPageDto([.. page.Select(ToItem)], next);
    }

    private static AccessAuditItemDto ToItem(AuditRow r) => new(
        r.Id, r.CreatedAt, r.Action,
        r.ActorId is { } a ? new AuditActorDto(a, r.ActorName) : null,
        r.TargetId is { } t ? new AuditActorDto(t, r.TargetName) : null,
        r.InviteId, JsonDocument.Parse(r.ChangesJson).RootElement.Clone());

    internal static string EncodeCursor(DateTimeOffset at, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.UtcTicks}|{id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecodeCursor(string cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParseExact(parts[1], "N", out id)) return false;
            if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    // ---------- допоміжне ----------

    private static bool IsLive(AccessUserRow? row) =>
        row is { IsActive: true, State: UserStates.Active } && Roles.IsTenantRole(row.Role);

    private static AuthError Conflict(int currentVersion) =>
        new(AuthErrorKind.Conflict, "access_version_conflict", "The access was changed by someone else; reload and retry.",
            new Dictionary<string, object?> { ["currentVersion"] = currentVersion });

    private static UserDto Dto(AccessUserRow u) =>
        new(u.Id, u.Email, u.FullName, u.Role, u.SpecialistId, u.IsActive, u.LastLoginAt) { AccessVersion = u.AccessVersion };

    private static async Task RevokeInvitesAsync(IAccessAdminSession session, Guid inviterId, Guid actorId, string cause, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var inviteId in await session.RevokePendingInvitesByInviterAsync(inviterId, now, ct))
            await session.AddAuditAsync(new AuditEntry("invite_revoked", actorId, null, inviteId,
                JsonSerializer.Serialize(new { cause }, Web)), ct);
    }

    /// <summary><c>UserAccessDto</c> (§30.9.3) для цілі з погляду актора.</summary>
    internal static UserAccessDto BuildDto(AccessSet actorAccess, Guid actorId, AccessUserRow target, IReadOnlyList<string> modules)
    {
        var isOwner = target.Role == Roles.Owner;
        var reason = isOwner ? "owner"
            : target.Id == actorId ? "self"
            : !RolePolicy.CanManage(actorAccess.Role ?? string.Empty, target.Role) ? "rank"
            : null;
        var editable = reason is null;

        var full = AccessPolicy.Materialize(target.Permissions);
        var targetAccess = AccessSet.Build(target.Role, target.Permissions, modules, false);
        var locked = editable ? actorAccess.Locked(full) : [];

        var assignable = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var d in PermissionCatalog.All)
        {
            if (!editable || locked.Contains(d.Key)
                || (d.RoleCeiling is not null && !d.RoleCeiling.Contains(target.Role, StringComparer.Ordinal)))
            {
                assignable[d.Key] = [];
                continue;
            }
            var max = d.IndexOf(actorAccess.Ceiling(d.Key));
            assignable[d.Key] = [.. d.Levels.Take(Math.Max(0, max) + 1)];
        }

        var roleOptions = editable && AssignableRoles.Contains(target.Role)
            ? AssignableRoles.Where(r => Roles.Rank(r) < Roles.Rank(actorAccess.Role)).ToList()
            : [];
        var ownWithout = target.SpecialistId is null && !isOwner && full.Values.Any(v => v == PermissionLevels.Own);

        return new UserAccessDto(
            target.Id, target.Role, target.IsActive, target.SpecialistId, target.AccessVersion, editable, reason, roleOptions,
            isOwner ? new Dictionary<string, string>() : full, targetAccess.Effective, assignable,
            locked, targetAccess.ModuleDisabledKeys(), ownWithout);
    }
}
