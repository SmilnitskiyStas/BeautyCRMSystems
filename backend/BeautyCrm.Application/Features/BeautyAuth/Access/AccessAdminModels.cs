using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeautyCrm.Application.Features.BeautyAuth;

// ---------- DTO й запити (контракт §30.9.3-30.9.6) ----------

public sealed record PermissionChange(string Permission, string From, string To);

/// <param name="Reason">ceiling | locked.</param>
public sealed record PermissionClamp(string Permission, string Requested, string Applied, string Reason);

public sealed record RoleChange(string From, string To);

/// <summary>
/// <c>GET /api/users/{id}/access</c> і відповідь PUT/reset (<see cref="Changes"/>, <see cref="RoleChange"/>, <see cref="Clamped"/>
/// з'являються лише у відповіді запису; для GET відсутні).
/// </summary>
public sealed record UserAccessDto(
    Guid UserId, string Role, bool IsActive, Guid? SpecialistId, int AccessVersion,
    bool Editable, string? NotEditableReason, IReadOnlyList<string> RoleOptions,
    IReadOnlyDictionary<string, string> Granted, IReadOnlyDictionary<string, string> Effective,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Assignable,
    IReadOnlyList<string> Locked, IReadOnlyList<string> ModuleDisabled, bool OwnWithoutSpecialist)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PermissionChange>? Changes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RoleChange? RoleChange { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PermissionClamp>? Clamped { get; init; }
}

/// <param name="Permissions">PATCH-семантика: не вказані ключі не змінюються.</param>
public sealed record UpdateAccessRequest(int? ExpectedVersion, string? Role, Dictionary<string, string>? Permissions, bool DryRun = false);

public sealed record ResetAccessRequest(int? ExpectedVersion, bool DryRun = false);

public sealed record AuditActorDto(Guid Id, string? FullName);

public sealed record AccessAuditItemDto(
    Guid Id, DateTimeOffset CreatedAt, string Action, AuditActorDto? Actor, AuditActorDto? Target, Guid? InviteId, JsonElement Changes);

public sealed record AccessAuditPageDto(IReadOnlyList<AccessAuditItemDto> Items, string? NextCursor);

// ---------- порти (реалізація - Infrastructure) ----------

/// <summary>Користувач у межах транзакції зміни доступу. <see cref="Permissions"/>: сирі рядки user_permissions.</summary>
public sealed record AccessUserRow(
    Guid Id, string Email, string FullName, string Role, bool IsActive, string State, Guid? SpecialistId, int AccessVersion,
    DateTimeOffset? LastLoginAt, IReadOnlyDictionary<string, string> Permissions);

public sealed record AuditEntry(string Action, Guid? ActorUserId, Guid? TargetUserId, Guid? InviteId, string ChangesJson);

public sealed record AuditRow(
    Guid Id, DateTimeOffset CreatedAt, string Action, Guid? ActorId, string? ActorName, Guid? TargetId, string? TargetName,
    Guid? InviteId, string ChangesJson);

public sealed record AuditQuery(Guid? UserId, Guid? InviteId, DateTimeOffset? CursorAt, Guid? CursorId, int Take);

/// <summary>
/// Транзакційна сесія зміни доступу (§30.10): спершу <see cref="LockActorAsync"/> (FOR SHARE), потім <see cref="LockTargetAsync"/>
/// (FOR UPDATE). Усі записи й аудит - в одній транзакції; без <see cref="CommitAsync"/> усе відкочується при Dispose.
/// </summary>
public interface IAccessAdminSession : IAsyncDisposable
{
    Task<IReadOnlyList<string>> GetModulesAsync(CancellationToken ct);
    Task<AccessUserRow?> LockActorAsync(Guid id, CancellationToken ct);
    Task<AccessUserRow?> LockTargetAsync(Guid id, CancellationToken ct);
    Task<AccessUserRow?> GetUserAsync(Guid id, CancellationToken ct);
    /// <summary>Оновлює роль (якщо не null), upsert рівнів, access_version + 1; повертає нову версію.</summary>
    Task<int> WriteAccessAsync(Guid targetId, string? newRole, IReadOnlyCollection<KeyValuePair<string, string>> upserts, Guid actorId, DateTimeOffset now, CancellationToken ct);
    /// <summary>is_active, access_version + 1; при вимкненні відкликає refresh-токени. Повертає нову версію.</summary>
    Task<int> SetActiveAsync(Guid targetId, bool active, DateTimeOffset now, CancellationToken ct);
    /// <summary>Відкликає pending-запрошення, де користувач є запрошувачем (M5). Повертає id відкликаних.</summary>
    Task<IReadOnlyList<Guid>> RevokePendingInvitesByInviterAsync(Guid inviterId, DateTimeOffset now, CancellationToken ct);
    Task AddAuditAsync(AuditEntry entry, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}

public interface IAccessAdminStore
{
    Task<IAccessAdminSession> BeginAsync(CancellationToken ct);
    Task<IReadOnlyList<AuditRow>> ListAuditAsync(AuditQuery query, CancellationToken ct);
}

// ---------- чиста політика ----------

public sealed record InviterState(string Role, bool IsActive, string State, IReadOnlyDictionary<string, string> Permissions, IReadOnlyList<string> Modules);

/// <param name="Revoke">true: запрошувач утратив права (M5), запрошення слід відкликати (404 invite_invalid, аудит invite_revoked).</param>
public sealed record AcceptPlan(bool Revoke, IReadOnlyDictionary<string, string> Permissions, IReadOnlyList<PermissionClamp> Clamped);

public sealed record InviteSnapshot(IReadOnlyDictionary<string, string> Permissions, IReadOnlyList<PermissionClamp> Clamped);

/// <summary>Чисті правила видачі прав (§30.2, §30.9.5, §30.10). Без доступу до БД.</summary>
public static class AccessPolicy
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Повний набір з БД: невалідні/відсутні рядки = найнижчий рівень (fail-closed).</summary>
    public static Dictionary<string, string> Materialize(IReadOnlyDictionary<string, string>? rows)
    {
        var full = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in PermissionCatalog.All)
            full[d.Key] = rows is not null && rows.TryGetValue(d.Key, out var v) && d.IndexOf(v) >= 0 ? v : d.Lowest;
        return full;
    }

    /// <summary>Порушення lte/requires на повному наборі (п.14). roleCeiling перевіряється окремо (п.12).</summary>
    public static List<object> Violations(IReadOnlyDictionary<string, string> set)
    {
        var list = new List<object>();
        foreach (var d in PermissionCatalog.All)
        {
            if (d.LteKey is not null)
            {
                var other = PermissionCatalog.Require(d.LteKey);
                if (d.IndexOf(set[d.Key]) > other.IndexOf(set[other.Key]))
                    list.Add(new { permission = d.Key, rule = "lte", other = other.Key, level = set[other.Key] });
            }
            if (d.Requires is not null)
            {
                var other = PermissionCatalog.Require(d.Requires.Key);
                if (d.IndexOf(set[d.Key]) > 0 && other.IndexOf(set[other.Key]) < other.IndexOf(d.Requires.Level))
                    list.Add(new { permission = d.Key, rule = "requires", other = other.Key, level = d.Requires.Level });
            }
        }
        return list;
    }

    /// <summary>
    /// Перевірки п.10-13 для явно вказаних рівнів (<paramref name="explicitValues"/>). <paramref name="baseSet"/> - набір, поверх якого
    /// накладаються значення (для п.13: підвищенням вважається значення вище за базове). null = все гаразд.
    /// </summary>
    public static AuthError? CheckExplicit(AccessSet actor, string targetRole, IReadOnlyDictionary<string, string> baseSet,
        IReadOnlyDictionary<string, string> current, IReadOnlyCollection<string> locked, IReadOnlyDictionary<string, string> explicitValues)
    {
        // 10
        foreach (var (key, level) in explicitValues)
        {
            var def = PermissionCatalog.Find(key);
            if (def is null) return Err(AuthErrorKind.Validation, "invalid_permission", "Unknown permission.", ("permission", key));
            if (def.IndexOf(level) < 0)
                return Err(AuthErrorKind.Validation, "invalid_level", "Unknown level for this permission.", ("permission", key), ("level", level));
        }
        // 11
        var ceiling = explicitValues.Where(kv =>
        {
            var d = PermissionCatalog.Require(kv.Key);
            return d.IndexOf(kv.Value) > d.IndexOf(actor.Ceiling(kv.Key)) || (locked.Contains(kv.Key) && kv.Value != current[kv.Key]);
        }).Select(kv => kv.Key).ToList();
        if (ceiling.Count > 0)
            return Err(AuthErrorKind.Forbidden, "permission_ceiling", "You cannot grant more than you have.", ("permissions", ceiling));
        // 12
        foreach (var (key, level) in explicitValues)
        {
            var d = PermissionCatalog.Require(key);
            if (d.RoleCeiling is not null && !d.RoleCeiling.Contains(targetRole, StringComparer.Ordinal) && d.IndexOf(level) > 0)
                return Err(AuthErrorKind.Validation, "permission_not_allowed_for_role", "This permission is not allowed for the role.",
                    ("permission", key), ("role", targetRole));
        }
        // 13
        var raised = explicitValues.Where(kv =>
        {
            var d = PermissionCatalog.Require(kv.Key);
            return d.IndexOf(kv.Value) > d.IndexOf(baseSet[kv.Key]) && actor.ModuleOff(kv.Key);
        }).Select(kv => kv.Key).ToList();
        if (raised.Count > 0)
            return Err(AuthErrorKind.Validation, "module_disabled", "The module for this permission is disabled.", ("permissions", raised));
        return null;
    }

    /// <summary>Повний знімок прав запрошення (B1): пресет ролі + перевизначення, кожен ключ обрізається стелею запрошувача.</summary>
    public static AuthResult<InviteSnapshot> BuildInviteSnapshot(AccessSet actor, string role, IReadOnlyDictionary<string, string>? explicitValues)
    {
        var preset = new Dictionary<string, string>(PermissionPresets.For(role), StringComparer.Ordinal);
        var overrides = explicitValues ?? new Dictionary<string, string>();
        if (CheckExplicit(actor, role, preset, preset, [], overrides) is { } error) return error;

        var set = new Dictionary<string, string>(preset, StringComparer.Ordinal);
        foreach (var (k, v) in overrides) set[k] = v;
        var clamped = new List<PermissionClamp>();
        foreach (var d in PermissionCatalog.All)
        {
            var ceiling = actor.Ceiling(d.Key);
            if (d.IndexOf(set[d.Key]) > d.IndexOf(ceiling))
            {
                clamped.Add(new PermissionClamp(d.Key, set[d.Key], ceiling, "ceiling"));
                set[d.Key] = ceiling;
            }
        }
        if (Violations(set) is { Count: > 0 } violations)
            return Err(AuthErrorKind.Validation, "permission_inconsistent", "The permission set is inconsistent.", ("violations", violations));
        return new InviteSnapshot(set, clamped);
    }

    /// <summary>Прийняття запрошення (§30.9.5, M5): перевірка запрошувача, обрізання його поточною стелею, інваріанти.</summary>
    public static AcceptPlan PlanAccept(string inviteRole, string permissionsJson, InviterState? inviter)
    {
        if (inviter is null || !inviter.IsActive || inviter.State != UserStates.Active || !Roles.IsTenantRole(inviter.Role)
            || Roles.Rank(inviter.Role) <= Roles.Rank(inviteRole))
            return new AcceptPlan(true, new Dictionary<string, string>(), []);
        var access = AccessSet.Build(inviter.Role, inviter.Permissions, inviter.Modules, false);
        if (!access.AtLeast(PermissionKeys.TeamManage, PermissionLevels.Manage))
            return new AcceptPlan(true, new Dictionary<string, string>(), []);

        Dictionary<string, string>? snapshot = null;
        try
        {
            snapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(permissionsJson, Web);
        }
        catch (JsonException)
        {
            // зіпсований знімок = найнижчі рівні (fail-closed)
        }
        var full = Materialize(snapshot);
        var clamped = new List<PermissionClamp>();
        foreach (var d in PermissionCatalog.All)
        {
            var ceiling = access.Ceiling(d.Key);
            if (d.IndexOf(full[d.Key]) > d.IndexOf(ceiling))
            {
                clamped.Add(new PermissionClamp(d.Key, full[d.Key], ceiling, "ceiling"));
                full[d.Key] = ceiling;
            }
        }
        return new AcceptPlan(false, AccessSet.Normalize(inviteRole, full), clamped);
    }

    public static AuthError Err(AuthErrorKind kind, string code, string message, params (string Key, object? Value)[] extras) =>
        new(kind, code, message, extras.ToDictionary(e => e.Key, e => e.Value));
}
