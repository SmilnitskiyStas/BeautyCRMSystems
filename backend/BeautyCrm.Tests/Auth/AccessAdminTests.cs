using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyStaff;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>TASK-851: чисті правила (AccessPolicy, курсор, CanModify) без БД.</summary>
public sealed class AccessAdminUnitTests
{
    private static readonly string[] Known = [.. BeautyModules.Known];

    private static AccessSet Owner() => AccessSet.Build(Roles.Owner, null, Known, false);

    private static AccessSet Admin(params (string Key, string Level)[] over)
    {
        var g = new Dictionary<string, string>(PermissionPresets.For(Roles.Admin));
        foreach (var (k, l) in over) g[k] = l;
        return AccessSet.Build(Roles.Admin, g, Known, false);
    }

    [Fact]
    public void Cursor_roundtrips_and_rejects_garbage()
    {
        var at = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero).AddTicks(1234);
        var id = Guid.NewGuid();
        Assert.True(AccessAdminService.TryDecodeCursor(AccessAdminService.EncodeCursor(at, id), out var a, out var i));
        Assert.Equal((at, id), (a, i));
        foreach (var bad in new[] { "", "!!!", "AAAA", Convert.ToBase64String("1|x"u8.ToArray()), Convert.ToBase64String("99999999999999999999|"u8.ToArray()) })
            Assert.False(AccessAdminService.TryDecodeCursor(bad, out _, out _));
    }

    [Fact]
    public void Violations_detect_lte_and_requires()
    {
        var set = new Dictionary<string, string>(PermissionPresets.For(Roles.Admin))
        {
            [PermissionKeys.CalendarView] = "own", [PermissionKeys.CalendarManage] = "all",
            [PermissionKeys.ClientsView] = "own", [PermissionKeys.ClientsCalls] = "all",
        };
        var v = AccessPolicy.Violations(set);
        Assert.Contains(v, x => JsonSerializer.Serialize(x).Contains("\"rule\":\"lte\"") && JsonSerializer.Serialize(x).Contains(PermissionKeys.CalendarManage));
        Assert.Contains(v, x => JsonSerializer.Serialize(x).Contains("\"rule\":\"requires\"") && JsonSerializer.Serialize(x).Contains(PermissionKeys.ClientsCalls));
        Assert.Empty(AccessPolicy.Violations(new Dictionary<string, string>(PermissionPresets.For(Roles.Admin))));
    }

    [Fact]
    public void CheckExplicit_orders_unknown_ceiling_role_module()
    {
        var admin = Admin((PermissionKeys.RevenueView, "own"), (PermissionKeys.RevenueCosts, "none"));
        var deputy = new Dictionary<string, string>(PermissionPresets.For(Roles.Deputy));
        Err(admin, Roles.Deputy, deputy, deputy, [], new() { ["nope"] = "all" }, "invalid_permission");
        Err(admin, Roles.Deputy, deputy, deputy, [], new() { [PermissionKeys.CalendarView] = "zzz" }, "invalid_level");
        Err(admin, Roles.Deputy, deputy, deputy, [], new() { [PermissionKeys.RevenueView] = "all" }, "permission_ceiling");
        Err(Owner(), Roles.Deputy, deputy, deputy, [], new() { [PermissionKeys.TeamManage] = "manage" }, "permission_not_allowed_for_role");
        var noAnalytics = AccessSet.Build(Roles.Owner, null, Known.Where(m => m != "beauty_analytics"), false);
        Err(noAnalytics, Roles.Deputy, deputy, deputy, [], new() { [PermissionKeys.RevenueView] = "own" }, "module_disabled");
        Assert.Null(AccessPolicy.CheckExplicit(Owner(), Roles.Deputy, deputy, deputy, [], new Dictionary<string, string> { [PermissionKeys.RevenueView] = "own" }));
    }

    private static void Err(AccessSet a, string role, IReadOnlyDictionary<string, string> b, IReadOnlyDictionary<string, string> c,
        IReadOnlyCollection<string> locked, Dictionary<string, string> ex, string code) =>
        Assert.Equal(code, AccessPolicy.CheckExplicit(a, role, b, c, locked, ex)?.Code);

    [Fact]
    public void BuildInviteSnapshot_is_full_and_clamped_by_ceiling()
    {
        var limited = Admin((PermissionKeys.CalendarView, "own"), (PermissionKeys.CalendarManage, "own"));
        var snap = AccessPolicy.BuildInviteSnapshot(limited, Roles.Deputy, null).Value!;
        Assert.Equal(PermissionCatalog.All.Count, snap.Permissions.Count);
        Assert.Equal("own", snap.Permissions[PermissionKeys.CalendarView]);
        Assert.Contains(snap.Clamped, c => c.Permission == PermissionKeys.CalendarView && c.Reason == "ceiling" && c.Applied == "own");
        Assert.Equal("permission_ceiling",
            AccessPolicy.BuildInviteSnapshot(Admin((PermissionKeys.RevenueView, "own")), Roles.Deputy, new Dictionary<string, string> { [PermissionKeys.RevenueView] = "all" }).Error!.Code);
    }

    [Fact]
    public void PlanAccept_revokes_when_inviter_lost_rights_and_clamps_by_current_ceiling()
    {
        var snapshot = JsonSerializer.Serialize(PermissionPresets.For(Roles.Deputy));
        InviterState Inviter(string role, bool active, params (string, string)[] over)
        {
            var g = new Dictionary<string, string>(PermissionPresets.For(role));
            foreach (var (k, l) in over) g[k] = l;
            return new InviterState(role, active, UserStates.Active, g, Known);
        }
        Assert.True(AccessPolicy.PlanAccept(Roles.Deputy, snapshot, null).Revoke);
        Assert.True(AccessPolicy.PlanAccept(Roles.Deputy, snapshot, Inviter(Roles.Admin, false)).Revoke);
        Assert.True(AccessPolicy.PlanAccept(Roles.Deputy, snapshot, Inviter(Roles.Admin, true, (PermissionKeys.TeamManage, "none"))).Revoke);
        Assert.True(AccessPolicy.PlanAccept(Roles.Admin, snapshot, Inviter(Roles.Admin, true)).Revoke); // ранг не вищий
        var plan = AccessPolicy.PlanAccept(Roles.Deputy, snapshot,
            Inviter(Roles.Admin, true, (PermissionKeys.CalendarView, "own"), (PermissionKeys.CalendarManage, "own")));
        Assert.False(plan.Revoke);
        Assert.Equal("own", plan.Permissions[PermissionKeys.CalendarView]);
        Assert.NotEmpty(plan.Clamped);
        Assert.Equal(PermissionCatalog.All.Count, plan.Permissions.Count);
        // зіпсований знімок = найнижчі рівні
        var broken = AccessPolicy.PlanAccept(Roles.Deputy, "{not json", Inviter(Roles.Admin, true));
        Assert.False(broken.Revoke);
        Assert.Equal("none", broken.Permissions[PermissionKeys.ClientsEdit]);
    }

    [Fact]
    public void CanModify_is_rank_based_with_self_rule()
    {
        var admin = Actor.ForTest(Roles.Admin);
        var owner = Actor.ForTest(Roles.Owner);
        Assert.True(StaffRoles.CanModify(admin, null));
        Assert.True(StaffRoles.CanModify(admin, Roles.Specialist, Guid.NewGuid()));
        Assert.True(StaffRoles.CanModify(admin, Roles.Deputy, Guid.NewGuid()));
        Assert.False(StaffRoles.CanModify(admin, Roles.Admin, Guid.NewGuid()));
        Assert.False(StaffRoles.CanModify(admin, Roles.Owner, Guid.NewGuid()));
        Assert.True(StaffRoles.CanModify(owner, Roles.Admin, Guid.NewGuid()));
        Assert.False(StaffRoles.CanModify(admin, Roles.Specialist, admin.UserId)); // власний профіль
    }

    [Fact]
    public void RolePolicy_CanManage_is_rank_based_and_never_owner()
    {
        Assert.True(RolePolicy.CanManage(Roles.Owner, Roles.Admin));
        Assert.True(RolePolicy.CanManage(Roles.Admin, Roles.Deputy));
        Assert.True(RolePolicy.CanManage(Roles.Deputy, Roles.Specialist));
        Assert.False(RolePolicy.CanManage(Roles.Admin, Roles.Admin));
        Assert.False(RolePolicy.CanManage(Roles.Owner, Roles.Owner));
        Assert.False(RolePolicy.CanManage(Roles.Admin, Roles.Owner));
    }
}

/// <summary>TASK-851 (ADR-023 §2.6, §30.9-30.11, §30.13): AccessAdminService через реальний API + PostgreSQL/RLS.</summary>
public sealed class AccessAdminApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed record Ctx(TenantSeed T, string Owner, Guid AdminId, string Admin, Guid DeputyId, string Deputy, Guid SpecId, string Spec);

    private async Task<Ctx> NewAsync()
    {
        var t = await fx.CreateTenantAsync();
        var owner = (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken;
        var admin = await fx.CreateUserAsync(t, Roles.Admin);
        var dep = await fx.CreateDeputyAsync(t);
        var spec = await fx.CreateUserAsync(t, Roles.Specialist);
        return new Ctx(t, owner, admin.UserId, admin.Tokens.AccessToken, dep.UserId, dep.Tokens.AccessToken, spec.UserId, spec.Tokens.AccessToken);
    }

    private async Task<(HttpResponseMessage R, JsonElement B)> Call(HttpMethod m, string url, string? token, object? body = null)
    {
        var r = await fx.Send(m, url, body, token);
        var text = await r.Content.ReadAsStringAsync();
        return (r, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? Code(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    private async Task<int> Version(Ctx c, Guid user)
    {
        var (_, b) = await Call(HttpMethod.Get, $"/api/users/{user}/access", c.Owner);
        return b.GetProperty("accessVersion").GetInt32();
    }

    private Task<(HttpResponseMessage R, JsonElement B)> Put(Ctx c, string token, Guid user, int? version, object? extra = null, string? role = null,
        Dictionary<string, string>? perms = null, bool dryRun = false) =>
        Call(HttpMethod.Put, $"/api/users/{user}/access", token, new { expectedVersion = version, role, permissions = perms, dryRun });

    private async Task<List<(string Action, Guid? Actor, Guid? Target, Guid? Invite)>> AuditAsync(Guid tenantId)
    {
        await using var db = fx.Db.CreateContext(tenantId);
        return (await db.UserAccessAudit.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync())
            .Select(a => (a.Action, a.ActorUserId, a.TargetUserId, a.InviteId)).ToList();
    }

    // ---------- §30.10: порядок перевірок ----------

    [SkippableFact]
    public async Task Row01_team_manage_required_403_with_permission()
    {
        NeedDb();
        var c = await NewAsync();
        foreach (var token in new[] { c.Deputy, c.Spec })
        {
            var (r, b) = await Put(c, token, c.SpecId, 1, perms: new() { [PermissionKeys.CalendarView] = "own" });
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden_role", Code(b));
            Assert.Equal(PermissionKeys.TeamManage, b.GetProperty("permission").GetString());
            Assert.Equal("manage", b.GetProperty("required").GetString());
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"/api/users/{c.SpecId}/access", c.Deputy)).R.StatusCode);
    }

    [SkippableFact]
    public async Task Row02_unknown_target_is_404()
    {
        NeedDb();
        var c = await NewAsync();
        var (r, b) = await Put(c, c.Owner, Guid.NewGuid(), 1);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("user_not_found", Code(b));
    }

    [SkippableFact]
    public async Task Row03_self_is_422_before_anything_else()
    {
        NeedDb();
        var c = await NewAsync();
        var (r, b) = await Put(c, c.Admin, c.AdminId, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("cannot_modify_self", Code(b));
    }

    [SkippableFact]
    public async Task Row04_rank_blocks_equal_higher_and_owner()
    {
        NeedDb();
        var c = await NewAsync();
        var other = await fx.CreateUserAsync(c.T, Roles.Admin);
        foreach (var target in new[] { other.UserId, c.T.OwnerId })
        {
            var (r, b) = await Put(c, c.Admin, target, 1);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden_role", Code(b));
            Assert.False(b.TryGetProperty("permission", out _));
        }
    }

    [SkippableFact]
    public async Task Row05_expectedVersion_required_Row06_conflict_with_currentVersion()
    {
        NeedDb();
        var c = await NewAsync();
        foreach (int? bad in new int?[] { null, 0, -3 })
        {
            var (r, b) = await Put(c, c.Admin, c.DeputyId, bad);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("expected_version_required", Code(b));
        }
        var v = await Version(c, c.DeputyId);
        var (r2, b2) = await Put(c, c.Admin, c.DeputyId, v + 5, perms: new() { [PermissionKeys.CalendarView] = "own" });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Equal("access_version_conflict", Code(b2));
        Assert.Equal(v, b2.GetProperty("currentVersion").GetInt32());
    }

    [SkippableFact]
    public async Task Row07_invalid_role_Row08_new_rank_below_actor_Row09_only_admin_deputy()
    {
        NeedDb();
        var c = await NewAsync();
        var v = await Version(c, c.DeputyId);
        foreach (var role in new[] { "root", "owner" })
        {
            var (r, b) = await Put(c, c.Owner, c.DeputyId, v, role: role);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("invalid_role", Code(b));
        }
        var (r8, b8) = await Put(c, c.Admin, c.DeputyId, v, role: Roles.Admin); // ранг ролі не нижчий за ранг актора
        Assert.Equal(HttpStatusCode.Forbidden, r8.StatusCode);
        Assert.Equal("forbidden_role", Code(b8));
        var sv = await Version(c, c.SpecId);
        var (r9, b9) = await Put(c, c.Owner, c.SpecId, sv, role: Roles.Admin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r9.StatusCode);
        Assert.Equal("role_change_not_supported", Code(b9));
        var (r9b, b9b) = await Put(c, c.Owner, c.DeputyId, v, role: Roles.Specialist);
        Assert.Equal("role_change_not_supported", Code(b9b));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r9b.StatusCode);
    }

    [SkippableFact]
    public async Task Row10_unknown_key_and_level()
    {
        NeedDb();
        var c = await NewAsync();
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Put(c, c.Owner, c.DeputyId, v, perms: new() { ["nope.key"] = "all" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("invalid_permission", Code(b));
        Assert.Equal("nope.key", b.GetProperty("permission").GetString());
        var (r2, b2) = await Put(c, c.Owner, c.DeputyId, v, perms: new() { [PermissionKeys.CalendarView] = "zzz" });
        Assert.Equal("invalid_level", Code(b2));
        Assert.Equal("zzz", b2.GetProperty("level").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r2.StatusCode);
    }

    [SkippableFact]
    public async Task Row11_ceiling_escalation_and_locked()
    {
        NeedDb();
        var c = await NewAsync();
        var limited = await fx.CreateUserAsync(c.T, Roles.Admin, null,
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin)) { [PermissionKeys.RevenueView] = "own", [PermissionKeys.RevenueCosts] = "none" });
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Put(c, limited.Tokens.AccessToken, c.DeputyId, v, perms: new() { [PermissionKeys.RevenueView] = "all" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("permission_ceiling", Code(b));
        Assert.Contains(PermissionKeys.RevenueView, b.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()));

        // locked: ключ цілі вищий за стелю актора - актор не може його ні підняти, ні знизити
        var perms = new Dictionary<string, string>(PermissionPresets.For(Roles.Deputy)) { [PermissionKeys.RevenueView] = "all" };
        await fx.MutateUserAsync(c.T.TenantId, c.DeputyId, _ => { }, perms);
        var v2 = await Version(c, c.DeputyId);
        var (rl, bl) = await Put(c, limited.Tokens.AccessToken, c.DeputyId, v2, perms: new() { [PermissionKeys.RevenueView] = "none" });
        Assert.Equal(HttpStatusCode.Forbidden, rl.StatusCode);
        Assert.Equal("permission_ceiling", Code(bl));
        var (_, g) = await Call(HttpMethod.Get, $"/api/users/{c.DeputyId}/access", limited.Tokens.AccessToken);
        Assert.Contains(PermissionKeys.RevenueView, g.GetProperty("locked").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(g.GetProperty("assignable").GetProperty(PermissionKeys.RevenueView).EnumerateArray());
    }

    [SkippableFact]
    public async Task Row12_team_manage_only_for_admin_role()
    {
        NeedDb();
        var c = await NewAsync();
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Put(c, c.Owner, c.DeputyId, v, perms: new() { [PermissionKeys.TeamManage] = "manage" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("permission_not_allowed_for_role", Code(b));
        Assert.Equal(Roles.Deputy, b.GetProperty("role").GetString());
    }

    [SkippableFact]
    public async Task Row13_module_disabled_blocks_raising_but_not_lowering()
    {
        NeedDb();
        var c = await NewAsync();
        await fx.SetModulesAsync(c.T.TenantId, BeautyModules.Known.Where(m => m != "beauty_analytics").ToArray());
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Put(c, c.Owner, c.DeputyId, v, perms: new() { [PermissionKeys.RevenueView] = "own" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("module_disabled", Code(b));
        Assert.Contains(PermissionKeys.RevenueView, b.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()));
        // зниження дозволене
        var (r2, _) = await Put(c, c.Owner, c.DeputyId, v, perms: new() { [PermissionKeys.CalendarView] = "own", [PermissionKeys.CalendarManage] = "own" });
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [SkippableFact]
    public async Task Row14_inconsistent_result_set()
    {
        NeedDb();
        var c = await NewAsync();
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Put(c, c.Owner, c.DeputyId, v, perms: new() { [PermissionKeys.CalendarView] = "own" }); // manage = all > view = own
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("permission_inconsistent", Code(b));
        var viol = b.GetProperty("violations").EnumerateArray().First();
        Assert.Equal("lte", viol.GetProperty("rule").GetString());
        Assert.Equal(PermissionKeys.CalendarManage, viol.GetProperty("permission").GetString());
        Assert.Equal(v, await Version(c, c.DeputyId)); // нічого не записано
    }

    // ---------- успішна зміна, dryRun, аудит, конкурентність ----------

    [SkippableFact]
    public async Task Update_applies_patch_bumps_version_and_audits_in_same_tx()
    {
        NeedDb();
        var c = await NewAsync();
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Put(c, c.Admin, c.DeputyId, v, perms: new() { [PermissionKeys.RevenueView] = "own" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal(v + 1, b.GetProperty("accessVersion").GetInt32());
        Assert.Equal("own", b.GetProperty("granted").GetProperty(PermissionKeys.RevenueView).GetString());
        Assert.Equal("none", b.GetProperty("changes").EnumerateArray().Single().GetProperty("from").GetString());
        var audit = (await AuditAsync(c.T.TenantId)).Where(a => a.Target == c.DeputyId).ToList();
        Assert.Equal("permissions_changed", Assert.Single(audit).Action);
        Assert.Equal(c.AdminId, audit[0].Actor);
        // той самий expectedVersion повторно - 409
        Assert.Equal(HttpStatusCode.Conflict, (await Put(c, c.Admin, c.DeputyId, v, perms: new() { [PermissionKeys.RevenueView] = "all" })).R.StatusCode);
        // зміна реально діє на наступний запит користувача (access_version у токен-стемпі)
        var me = await Call(HttpMethod.Get, "/api/auth/me", c.Deputy);
        Assert.Equal("own", me.B.GetProperty("permissions").GetProperty(PermissionKeys.RevenueView).GetString());
    }

    [SkippableFact]
    public async Task DryRun_has_no_side_effects()
    {
        NeedDb();
        var c = await NewAsync();
        var v = await Version(c, c.DeputyId);
        var before = (await AuditAsync(c.T.TenantId)).Count;
        var (r, b) = await Put(c, c.Owner, c.DeputyId, v, perms: new() { [PermissionKeys.RevenueView] = "own" }, dryRun: true);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, b.GetProperty("changes").GetArrayLength());
        Assert.Equal(v, b.GetProperty("accessVersion").GetInt32());
        Assert.Equal(v, await Version(c, c.DeputyId));
        Assert.Equal(before, (await AuditAsync(c.T.TenantId)).Count);
        // dryRun теж проходить усі перевірки
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(c, c.Admin, c.T.OwnerId, v, dryRun: true)).R.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(c, c.Owner, c.DeputyId, v + 1, perms: new() { [PermissionKeys.RevenueView] = "own" }, dryRun: true)).R.StatusCode);
    }

    [SkippableFact]
    public async Task Concurrent_managers_with_same_expectedVersion_one_wins_one_409()
    {
        NeedDb();
        var c = await NewAsync();
        var second = await fx.CreateUserAsync(c.T, Roles.Admin);
        var v = await Version(c, c.SpecId);
        var a = Put(c, c.Admin, c.SpecId, v, perms: new() { [PermissionKeys.ClientsView] = "all" });
        var b = Put(c, second.Tokens.AccessToken, c.SpecId, v, perms: new() { [PermissionKeys.ClientsView] = "own" });
        var codes = (await Task.WhenAll(a, b)).Select(x => x.R.StatusCode).OrderBy(x => (int)x).ToArray();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codes);
        Assert.Equal(v + 1, await Version(c, c.SpecId));
        Assert.Single((await AuditAsync(c.T.TenantId)).Where(x => x.Target == c.SpecId));
    }

    [SkippableFact]
    public async Task Concurrent_lowering_of_actor_does_not_let_stale_actor_write()
    {
        NeedDb();
        var c = await NewAsync();
        // актора знижено (team.manage none) до запису - права перечитуються з БД під блокуванням, токен ще "свіжий" з погляду клієнта
        await fx.MutateUserAsync(c.T.TenantId, c.AdminId, _ => { },
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin)) { [PermissionKeys.TeamManage] = "none" });
        var (r, _) = await Put(c, c.Admin, c.SpecId, 1, perms: new() { [PermissionKeys.ClientsView] = "own" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ---------- роль, reset ----------

    [SkippableFact]
    public async Task Role_change_deputy_to_admin_applies_preset_audits_and_revokes_target_invites()
    {
        NeedDb();
        var c = await NewAsync();
        var inv = await Call(HttpMethod.Post, "/api/invites", c.Deputy, new { email = $"x-{Guid.NewGuid():N}"[..14] + "@" + c.T.Slug + ".test", role = "deputy" });
        Assert.Equal(HttpStatusCode.Forbidden, inv.R.StatusCode); // deputy без team.manage не запрошує
        var invited = await Call(HttpMethod.Post, "/api/invites", c.Admin, new { email = $"y-{Guid.NewGuid():N}"[..14] + "@" + c.T.Slug + ".test", role = "deputy" });
        Assert.Equal(HttpStatusCode.Created, invited.R.StatusCode);

        var v = await Version(c, c.AdminId);
        // admin -> deputy (owner): team.manage скидається пресетом
        var (r, b) = await Put(c, c.Owner, c.AdminId, v, role: Roles.Deputy);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(Roles.Deputy, b.GetProperty("role").GetString());
        Assert.Equal("none", b.GetProperty("granted").GetProperty(PermissionKeys.TeamManage).GetString());
        Assert.Equal(Roles.Admin, b.GetProperty("roleChange").GetProperty("from").GetString());
        Assert.Equal(v + 1, b.GetProperty("accessVersion").GetInt32());
        var audit = await AuditAsync(c.T.TenantId);
        Assert.Contains(audit, a => a.Action == "role_changed" && a.Target == c.AdminId);
        Assert.Contains(audit, a => a.Action == "invite_revoked" && a.Invite == Guid.Parse(invited.B.GetProperty("invite").GetProperty("id").GetString()!));
        // і назад: deputy -> admin, пресет admin включно з team.manage
        var (r2, b2) = await Put(c, c.Owner, c.AdminId, v + 1, role: Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("manage", b2.GetProperty("granted").GetProperty(PermissionKeys.TeamManage).GetString());
    }

    [SkippableFact]
    public async Task Reset_applies_preset_with_clamp_by_actor_ceiling_and_locked()
    {
        NeedDb();
        var c = await NewAsync();
        var limited = await fx.CreateUserAsync(c.T, Roles.Admin, null,
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin))
            { [PermissionKeys.CalendarView] = "own", [PermissionKeys.CalendarManage] = "own" });
        // ціль: calendar.view = none (нижче стелі актора), calendar.manage = all (вище стелі -> locked)
        var seed = new Dictionary<string, string>(PermissionPresets.For(Roles.Deputy))
        { [PermissionKeys.CalendarView] = "all", [PermissionKeys.CalendarManage] = "all", [PermissionKeys.RevenueView] = "none" };
        await fx.MutateUserAsync(c.T.TenantId, c.DeputyId, _ => { }, seed);
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Call(HttpMethod.Post, $"/api/users/{c.DeputyId}/access/reset", limited.Tokens.AccessToken, new { expectedVersion = v });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // пресет deputy = all, поточне all (>= стелі) -> locked лишається без змін
        Assert.Equal("all", b.GetProperty("granted").GetProperty(PermissionKeys.CalendarView).GetString());
        Assert.Contains(PermissionKeys.CalendarView, b.GetProperty("locked").EnumerateArray().Select(x => x.GetString()));

        // reset від owner відновлює пресет без обрізання, аудит preset_reset
        await fx.MutateUserAsync(c.T.TenantId, c.SpecId, _ => { },
            new Dictionary<string, string>(PermissionPresets.For(Roles.Specialist)) { [PermissionKeys.ClientsView] = "all", [PermissionKeys.ClientsEdit] = "all" });
        var sv = await Version(c, c.SpecId);
        var (r2, b2) = await Call(HttpMethod.Post, $"/api/users/{c.SpecId}/access/reset", c.Owner, new { expectedVersion = sv });
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("none", b2.GetProperty("granted").GetProperty(PermissionKeys.ClientsView).GetString());
        Assert.Contains(await AuditAsync(c.T.TenantId), a => a.Action == "preset_reset" && a.Target == c.SpecId);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/users/{c.SpecId}/access/reset", c.Owner, new { expectedVersion = sv })).R.StatusCode);
    }

    [SkippableFact]
    public async Task Reset_clamps_by_ceiling_when_target_is_below_actor_ceiling()
    {
        NeedDb();
        var c = await NewAsync();
        var limited = await fx.CreateUserAsync(c.T, Roles.Admin, null,
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin))
            { [PermissionKeys.CalendarView] = "own", [PermissionKeys.CalendarManage] = "own" });
        await fx.MutateUserAsync(c.T.TenantId, c.DeputyId, _ => { },
            new Dictionary<string, string>(PermissionPresets.For(Roles.Deputy)) { [PermissionKeys.CalendarView] = "none", [PermissionKeys.CalendarManage] = "none" });
        var v = await Version(c, c.DeputyId);
        var (r, b) = await Call(HttpMethod.Post, $"/api/users/{c.DeputyId}/access/reset", limited.Tokens.AccessToken, new { expectedVersion = v });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("own", b.GetProperty("granted").GetProperty(PermissionKeys.CalendarView).GetString());
        Assert.Contains(b.GetProperty("clamped").EnumerateArray(),
            x => x.GetProperty("permission").GetString() == PermissionKeys.CalendarView && x.GetProperty("reason").GetString() == "ceiling");
    }

    // ---------- GET access ----------

    [SkippableFact]
    public async Task Get_access_describes_editability()
    {
        NeedDb();
        var c = await NewAsync();
        var (r, b) = await Call(HttpMethod.Get, $"/api/users/{c.DeputyId}/access", c.Admin);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.True(b.GetProperty("editable").GetBoolean());
        Assert.Equal(PermissionCatalog.All.Count, b.GetProperty("granted").EnumerateObject().Count());
        Assert.Equal(PermissionCatalog.All.Count, b.GetProperty("assignable").EnumerateObject().Count());
        Assert.Empty(b.GetProperty("assignable").GetProperty(PermissionKeys.TeamManage).EnumerateArray()); // стеля ролі deputy
        Assert.Equal(["deputy"], b.GetProperty("roleOptions").EnumerateArray().Select(x => x.GetString())); // лише ролі нижчі за ранг актора
        Assert.Equal("owner", (await Call(HttpMethod.Get, $"/api/users/{c.T.OwnerId}/access", c.Admin)).B.GetProperty("notEditableReason").GetString());
        Assert.Equal("self", (await Call(HttpMethod.Get, $"/api/users/{c.AdminId}/access", c.Admin)).B.GetProperty("notEditableReason").GetString());
        var other = await fx.CreateUserAsync(c.T, Roles.Admin);
        Assert.Equal("rank", (await Call(HttpMethod.Get, $"/api/users/{other.UserId}/access", c.Admin)).B.GetProperty("notEditableReason").GetString());
        var ownerView = await Call(HttpMethod.Get, $"/api/users/{c.AdminId}/access", c.Owner);
        Assert.Contains("deputy", ownerView.B.GetProperty("roleOptions").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/users/{Guid.NewGuid()}/access", c.Owner)).R.StatusCode);
    }

    // ---------- запрошення ----------

    private static string Email(Ctx c, string p) => $"{p}-{Guid.NewGuid():N}"[..14] + "@" + c.T.Slug + ".test";

    [SkippableFact]
    public async Task Invite_deputy_stores_full_snapshot_and_accept_writes_user_permissions()
    {
        NeedDb();
        var c = await NewAsync();
        var email = Email(c, "dep");
        var (r, b) = await Call(HttpMethod.Post, "/api/invites", c.Owner, new { email, role = "deputy", permissions = new Dictionary<string, string> { [PermissionKeys.RevenueView] = "own" } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var snapshot = b.GetProperty("invite").GetProperty("permissions");
        Assert.Equal(PermissionCatalog.All.Count, snapshot.EnumerateObject().Count());
        Assert.Equal("own", snapshot.GetProperty(PermissionKeys.RevenueView).GetString());
        Assert.Equal("none", snapshot.GetProperty(PermissionKeys.TeamManage).GetString());

        var accepted = await fx.Send(HttpMethod.Post, "/api/auth/invites/accept", new { token = b.GetProperty("token").GetString(), fullName = "Dep", password = Password });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var tokens = await fx.LoginAsync(c.T.Slug, email);
        await using (var db = fx.Db.CreateContext(c.T.TenantId))
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
            Assert.Equal(Roles.Deputy, user.Role);
            var rows = await db.UserPermissions.AsNoTracking().Where(p => p.UserId == user.Id).ToDictionaryAsync(p => p.Permission, p => p.Level);
            Assert.Equal(PermissionCatalog.All.Count, rows.Count);
            Assert.Equal("own", rows[PermissionKeys.RevenueView]);
        }
        var me = await Call(HttpMethod.Get, "/api/auth/me", tokens.AccessToken);
        Assert.Equal("own", me.B.GetProperty("permissions").GetProperty(PermissionKeys.RevenueView).GetString());
        var audit = await AuditAsync(c.T.TenantId);
        Assert.Contains(audit, a => a.Action == "invited");
        Assert.Contains(audit, a => a.Action == "invite_accepted");
    }

    [SkippableFact]
    public async Task Invite_validation_ceiling_clamped_and_role_rules()
    {
        NeedDb();
        var c = await NewAsync();
        var limited = await fx.CreateUserAsync(c.T, Roles.Admin, null,
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin))
            { [PermissionKeys.CalendarView] = "own", [PermissionKeys.CalendarManage] = "own", [PermissionKeys.RevenueView] = "own", [PermissionKeys.RevenueCosts] = "none" });
        var t = limited.Tokens.AccessToken;
        // явне перевищення -> 403 permission_ceiling
        var (r1, b1) = await Call(HttpMethod.Post, "/api/invites", t, new { email = Email(c, "a"), role = "deputy", permissions = new Dictionary<string, string> { [PermissionKeys.RevenueView] = "all" } });
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);
        Assert.Equal("permission_ceiling", Code(b1));
        // пресет вищий за стелю -> 201 + clamped[]
        var (r2, b2) = await Call(HttpMethod.Post, "/api/invites", t, new { email = Email(c, "b"), role = "deputy" });
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        Assert.Equal("own", b2.GetProperty("invite").GetProperty("permissions").GetProperty(PermissionKeys.CalendarView).GetString());
        Assert.Contains(b2.GetProperty("clamped").EnumerateArray(), x => x.GetProperty("permission").GetString() == PermissionKeys.CalendarView);
        // стеля ролі
        var (r3, b3) = await Call(HttpMethod.Post, "/api/invites", c.Owner, new { email = Email(c, "c"), role = "deputy", permissions = new Dictionary<string, string> { [PermissionKeys.TeamManage] = "manage" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r3.StatusCode);
        Assert.Equal("permission_not_allowed_for_role", Code(b3));
        // admin не запрошує admin (ранг) і ніхто не запрошує owner
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/invites", c.Admin, new { email = Email(c, "d"), role = "admin" })).R.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/invites", c.Owner, new { email = Email(c, "e"), role = "owner" })).R.StatusCode);
        // inconsistent
        var (r5, b5) = await Call(HttpMethod.Post, "/api/invites", c.Owner, new { email = Email(c, "f"), role = "deputy", permissions = new Dictionary<string, string> { [PermissionKeys.CalendarView] = "own" } });
        Assert.Equal("permission_inconsistent", Code(b5));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r5.StatusCode);
        // невідомий ключ
        Assert.Equal("invalid_permission", Code((await Call(HttpMethod.Post, "/api/invites", c.Owner, new { email = Email(c, "g"), role = "deputy", permissions = new Dictionary<string, string> { ["x"] = "y" } })).B));
        // без team.manage
        var (r6, b6) = await Call(HttpMethod.Post, "/api/invites", c.Deputy, new { email = Email(c, "h"), role = "specialist" });
        Assert.Equal(HttpStatusCode.Forbidden, r6.StatusCode);
        Assert.Equal(PermissionKeys.TeamManage, b6.GetProperty("permission").GetString());
    }

    [SkippableFact]
    public async Task Specialist_invite_via_staff_endpoint_accepts_permissions()
    {
        NeedDb();
        var c = await NewAsync();
        var seed = await fx.SeedBeautyAsync(c.T.TenantId);
        var (r, b) = await Call(HttpMethod.Post, $"/api/beauty/specialists/{seed.SpecialistA}/invite", c.Owner,
            new { email = Email(c, "sp"), permissions = new Dictionary<string, string> { [PermissionKeys.ClientsView] = "all" } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("all", b.GetProperty("invite").GetProperty("permissions").GetProperty(PermissionKeys.ClientsView).GetString());
        // без team.manage
        // (політика Management на цьому ендпоінті лишається до TASK-852, тому тіло порожнє; RequirePermission - другий рубіж)
        var (r2, _) = await Call(HttpMethod.Post, $"/api/beauty/specialists/{seed.SpecialistB}/invite", c.Deputy, new { email = Email(c, "sq") });
        Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);
    }

    private async Task<(Guid InviteId, string Token)> AdminInvitesDeputyAsync(Ctx c, string admin, Dictionary<string, string>? perms = null)
    {
        var (r, b) = await Call(HttpMethod.Post, "/api/invites", admin, new { email = Email(c, "p"), role = "deputy", permissions = perms });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (Guid.Parse(b.GetProperty("invite").GetProperty("id").GetString()!), b.GetProperty("token").GetString()!);
    }

    private async Task<HttpResponseMessage> AcceptAsync(string token) =>
        await fx.Send(HttpMethod.Post, "/api/auth/invites/accept", new { token, fullName = "N", password = Password });

    [SkippableFact]
    public async Task Accept_m5_inviter_lost_team_manage_directly_in_db_gives_404_and_revokes()
    {
        NeedDb();
        var c = await NewAsync();
        var (inviteId, token) = await AdminInvitesDeputyAsync(c, c.Admin);
        await fx.MutateUserAsync(c.T.TenantId, c.AdminId, _ => { },
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin)) { [PermissionKeys.TeamManage] = "none" });
        var r = await AcceptAsync(token);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("invite_invalid", Code(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement));
        Assert.Contains(await AuditAsync(c.T.TenantId), a => a.Action == "invite_revoked" && a.Invite == inviteId);
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(token)).StatusCode);
    }

    [SkippableFact]
    public async Task Accept_m5_inviter_deactivated_gives_404()
    {
        NeedDb();
        var c = await NewAsync();
        var (_, token) = await AdminInvitesDeputyAsync(c, c.Admin);
        await fx.MutateUserAsync(c.T.TenantId, c.AdminId, u => u.IsActive = false);
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(token)).StatusCode);
    }

    [SkippableFact]
    public async Task Accept_clamps_snapshot_by_inviter_current_ceiling()
    {
        NeedDb();
        var c = await NewAsync();
        var (_, token) = await AdminInvitesDeputyAsync(c, c.Admin, new() { [PermissionKeys.RevenueView] = "all" });
        await fx.MutateUserAsync(c.T.TenantId, c.AdminId, _ => { },
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin)) { [PermissionKeys.RevenueView] = "own", [PermissionKeys.RevenueCosts] = "none" });
        var r = await AcceptAsync(token);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        await using var db = fx.Db.CreateContext(c.T.TenantId);
        var user = await db.Users.AsNoTracking().OrderByDescending(u => u.CreatedAt).FirstAsync(u => u.Role == Roles.Deputy && u.Id != c.DeputyId);
        Assert.Equal("own", (await db.UserPermissions.AsNoTracking().SingleAsync(p => p.UserId == user.Id && p.Permission == PermissionKeys.RevenueView)).Level);
    }

    [SkippableFact]
    public async Task Pending_invites_are_revoked_on_deactivation_role_change_and_team_manage_drop()
    {
        NeedDb();
        var c = await NewAsync();
        var a1 = await fx.CreateUserAsync(c.T, Roles.Admin);
        var a2 = await fx.CreateUserAsync(c.T, Roles.Admin);
        var a3 = await fx.CreateUserAsync(c.T, Roles.Admin);
        var i1 = await AdminInvitesDeputyAsync(c, a1.Tokens.AccessToken);
        var i2 = await AdminInvitesDeputyAsync(c, a2.Tokens.AccessToken);
        var i3 = await AdminInvitesDeputyAsync(c, a3.Tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/users/{a1.UserId}/status", c.Owner, new { isActive = false })).R.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(c, c.Owner, a2.UserId, await Version(c, a2.UserId), role: Roles.Deputy)).R.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(c, c.Owner, a3.UserId, await Version(c, a3.UserId), perms: new() { [PermissionKeys.TeamManage] = "none" })).R.StatusCode);

        var audit = await AuditAsync(c.T.TenantId);
        foreach (var (id, token) in new[] { i1, i2, i3 })
        {
            Assert.Contains(audit, a => a.Action == "invite_revoked" && a.Invite == id);
            Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(token)).StatusCode);
        }
    }

    // ---------- PATCH status ----------

    [SkippableFact]
    public async Task Status_uses_rank_deputy_version_audit_and_revokes_session()
    {
        NeedDb();
        var c = await NewAsync();
        var v = await Version(c, c.DeputyId);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Patch, $"/api/users/{c.DeputyId}/status", c.Deputy, new { isActive = false })).R.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Patch, $"/api/users/{c.T.OwnerId}/status", c.Admin, new { isActive = false })).R.StatusCode);
        var peer = await fx.CreateUserAsync(c.T, Roles.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Patch, $"/api/users/{peer.UserId}/status", c.Admin, new { isActive = false })).R.StatusCode);
        var self = await Call(HttpMethod.Patch, $"/api/users/{c.AdminId}/status", c.Admin, new { isActive = false });
        Assert.Equal("cannot_modify_self", Code(self.B));
        var stale = await Call(HttpMethod.Patch, $"/api/users/{c.DeputyId}/status", c.Admin, new { isActive = false, expectedVersion = v + 3 });
        Assert.Equal(HttpStatusCode.Conflict, stale.R.StatusCode);
        Assert.Equal("access_version_conflict", Code(stale.B));

        var ok = await Call(HttpMethod.Patch, $"/api/users/{c.DeputyId}/status", c.Admin, new { isActive = false, expectedVersion = v });
        Assert.Equal(HttpStatusCode.OK, ok.R.StatusCode);
        Assert.False(ok.B.GetProperty("isActive").GetBoolean());
        Assert.Equal(v + 1, await Version(c, c.DeputyId));
        Assert.Contains(await AuditAsync(c.T.TenantId), a => a.Action == "deactivated" && a.Target == c.DeputyId && a.Actor == c.AdminId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/auth/me", c.Deputy)).R.StatusCode);

        var back = await Call(HttpMethod.Patch, $"/api/users/{c.DeputyId}/status", c.Owner, new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, back.R.StatusCode);
        Assert.Contains(await AuditAsync(c.T.TenantId), a => a.Action == "activated" && a.Target == c.DeputyId);
        // повторне ввімкнення без змін не створює аудит і не піднімає версію
        var count = (await AuditAsync(c.T.TenantId)).Count;
        var vv = await Version(c, c.DeputyId);
        await Call(HttpMethod.Patch, $"/api/users/{c.DeputyId}/status", c.Owner, new { isActive = true });
        Assert.Equal(count, (await AuditAsync(c.T.TenantId)).Count);
        Assert.Equal(vv, await Version(c, c.DeputyId));
    }

    // ---------- M1, M2 ----------

    [SkippableFact]
    public async Task M1_profile_deactivation_with_linked_user_requires_team_manage_and_rank_and_audits_cascade()
    {
        NeedDb();
        var c = await NewAsync();
        var seed = await fx.SeedBeautyAsync(c.T.TenantId);
        var linked = await fx.CreateUserAsync(c.T, Roles.Specialist, seed.SpecialistA);
        var url = $"/api/beauty/specialists/{seed.SpecialistA}";

        // admin без team.manage
        var noTeam = await fx.CreateUserAsync(c.T, Roles.Admin, null,
            new Dictionary<string, string>(PermissionPresets.For(Roles.Admin)) { [PermissionKeys.TeamManage] = "none" });
        var (r0, b0) = await Call(HttpMethod.Put, url, noTeam.Tokens.AccessToken, new { name = "Master A", isActive = false });
        Assert.Equal(HttpStatusCode.Forbidden, r0.StatusCode);
        Assert.Equal(PermissionKeys.TeamManage, b0.GetProperty("permission").GetString());
        // профіль без користувача деактивується без team.manage-перевірки M1
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/beauty/specialists/{seed.SpecialistB}", noTeam.Tokens.AccessToken, new { name = "Master B", isActive = false })).R.StatusCode);

        var before = await Version(c, linked.UserId);
        var (r1, _) = await Call(HttpMethod.Put, url, c.Admin, new { name = "Master A", isActive = false });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(before + 1, await Version(c, linked.UserId));
        Assert.Contains(await AuditAsync(c.T.TenantId), a => a.Action == "deactivated" && a.Target == linked.UserId && a.Actor == c.AdminId);
        await using var db = fx.Db.CreateContext(c.T.TenantId);
        Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == linked.UserId)).IsActive);
    }

    [SkippableFact]
    public async Task M1_admin_cannot_deactivate_profile_linked_to_peer_admin()
    {
        NeedDb();
        var c = await NewAsync();
        var seed = await fx.SeedBeautyAsync(c.T.TenantId);
        await fx.CreateUserAsync(c.T, Roles.Admin, seed.SpecialistA);
        var (r, _) = await Call(HttpMethod.Put, $"/api/beauty/specialists/{seed.SpecialistA}", c.Admin, new { name = "Master A", isActive = false });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [SkippableFact]
    public async Task M2_own_absence_is_requested_and_self_approval_is_forbidden()
    {
        NeedDb();
        var c = await NewAsync();
        var seed = await fx.SeedBeautyAsync(c.T.TenantId);
        var self = await fx.CreateUserAsync(c.T, Roles.Admin, seed.SpecialistA);
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(40)).ToString("yyyy-MM-dd");
        var (r, b) = await Call(HttpMethod.Post, $"/api/beauty/specialists/{seed.SpecialistA}/absences", self.Tokens.AccessToken, new { type = "sick", dateFrom = day, dateTo = day });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("requested", b.GetProperty("status").GetString());
        var id = b.GetProperty("id").GetString();
        var (ra, ba) = await Call(HttpMethod.Post, $"/api/beauty/absences/{id}/approve", self.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, ra.StatusCode);
        Assert.Equal("forbidden_self_approval", Code(ba));
        Assert.Equal("forbidden_self_approval", Code((await Call(HttpMethod.Post, $"/api/beauty/absences/{id}/reject", self.Tokens.AccessToken)).B));
        // owner (вищий ранг) погоджує
        Assert.True((await Call(HttpMethod.Post, $"/api/beauty/absences/{id}/approve", c.Owner)).R.IsSuccessStatusCode);
    }

    // ---------- журнал аудиту ----------

    [SkippableFact]
    public async Task Audit_is_owner_only_paged_no_store_and_validated()
    {
        NeedDb();
        var c = await NewAsync();
        for (var i = 0; i < 3; i++)
            await Put(c, c.Owner, c.SpecId, await Version(c, c.SpecId), perms: new() { [PermissionKeys.ClientsView] = i % 2 == 0 ? "own" : "none" });

        foreach (var t in new[] { c.Admin, c.Deputy, c.Spec })
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/users/access-audit", t)).R.StatusCode);

        var (r, page1) = await Call(HttpMethod.Get, "/api/users/access-audit?take=2", c.Owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        var cursor = page1.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var (_, page2) = await Call(HttpMethod.Get, $"/api/users/access-audit?take=2&cursor={cursor}", c.Owner);
        var ids1 = page1.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        var ids2 = page2.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Empty(ids1.Intersect(ids2));
        var first = page1.GetProperty("items")[0];
        Assert.Equal("permissions_changed", first.GetProperty("action").GetString());
        Assert.Equal(c.SpecId.ToString(), first.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal("Owner", first.GetProperty("actor").GetProperty("fullName").GetString());

        var bad = await Call(HttpMethod.Get, "/api/users/access-audit?cursor=%25%25", c.Owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.R.StatusCode);
        Assert.Equal("invalid_cursor", Code(bad.B));
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/users/access-audit?take=0", c.Owner)).B.GetProperty("items").GetArrayLength());
        Assert.True((await Call(HttpMethod.Get, "/api/users/access-audit?take=9999", c.Owner)).B.GetProperty("items").GetArrayLength() <= 100);
        var byUser = await Call(HttpMethod.Get, $"/api/users/access-audit?userId={c.DeputyId}", c.Owner);
        Assert.Empty(byUser.B.GetProperty("items").EnumerateArray());
    }

    // ---------- ізоляція tenant ----------

    [SkippableFact]
    public async Task Tenants_are_isolated()
    {
        NeedDb();
        var a = await NewAsync();
        var b = await NewAsync();
        await Put(b, b.Owner, b.SpecId, await Version(b, b.SpecId), perms: new() { [PermissionKeys.ClientsView] = "own" });

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/users/{b.SpecId}/access", a.Owner)).R.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Put(a, a.Owner, b.SpecId, 1, perms: new() { [PermissionKeys.ClientsView] = "all" })).R.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Patch, $"/api/users/{b.SpecId}/status", a.Owner, new { isActive = false })).R.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/users/{b.SpecId}/access/reset", a.Owner, new { expectedVersion = 1 })).R.StatusCode);
        var (_, audit) = await Call(HttpMethod.Get, "/api/users/access-audit", a.Owner);
        Assert.DoesNotContain(audit.GetProperty("items").EnumerateArray(), x => x.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object && t.GetProperty("id").GetString() == b.SpecId.ToString());
        Assert.Equal((await Call(HttpMethod.Get, $"/api/users/{b.SpecId}/access", b.Owner)).B.GetProperty("granted").GetProperty(PermissionKeys.ClientsView).GetString(), "own");
    }
}
