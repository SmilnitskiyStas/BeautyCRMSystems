using System.Security.Claims;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Controllers;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Tests.Auth;

/// <summary>TASK-850 (ADR-023, §30.2–30.4): каталог, пресети, AccessSet (effective, fail-closed, модулі, інваріанти) - юніт, без БД.</summary>
public sealed class PermissionCatalogTests
{
    private static readonly string[] AllModules = [.. BeautyModules.Known];

    private static AccessSet Build(string role, IReadOnlyDictionary<string, string>? rows = null, IEnumerable<string>? modules = null,
        bool inactive = false) => AccessSet.Build(role, rows, modules ?? AllModules, inactive);

    // ---------- каталог ----------

    [Fact]
    public void Catalog_has_18_unique_keys_with_ordered_levels_and_version_1()
    {
        Assert.Equal(1, PermissionCatalog.Version);
        Assert.Equal(18, PermissionCatalog.All.Count);
        Assert.Equal(18, PermissionCatalog.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["none", "own", "all"], PermissionCatalog.Require("calendar.view").Levels);
        Assert.Equal(["masked", "full"], PermissionCatalog.Require("clients.contacts").Levels);
        Assert.Equal(["view", "edit"], PermissionCatalog.Require("services.catalog").Levels);
        Assert.Equal("view", PermissionCatalog.Require("services.catalog").Lowest);
        Assert.Equal(["none", "manage"], PermissionCatalog.Require("team.manage").Levels);
        Assert.Equal(["admin"], PermissionCatalog.Require("team.manage").RoleCeiling);
        Assert.All(new[] { "staff.access", "locations.access" },
            k => Assert.Equal(["own", "all"], PermissionCatalog.Require(k).MenuLevels));
        Assert.Equal(["beauty_marketing"], PermissionCatalog.Require("marketing.campaigns").Modules);
        Assert.Empty(PermissionCatalog.Require("settings.general").Modules);
    }

    [Fact]
    public void Catalog_keys_and_levels_are_case_sensitive()
    {
        Assert.Null(PermissionCatalog.Find("Calendar.View"));
        Assert.Equal(-1, PermissionCatalog.Require("calendar.view").IndexOf("ALL"));
        Assert.Throws<ArgumentException>(() => PermissionCatalog.Validate("calendar.view", "All"));
        Assert.Throws<ArgumentException>(() => PermissionCatalog.Validate("calendar.nope", "all"));
    }

    [Fact]
    public void Catalog_dto_matches_contract_shape()
    {
        var dto = PermissionCatalog.ToDto();
        Assert.Equal(1, dto.CatalogVersion);
        Assert.Equal(["owner", "admin", "deputy", "specialist"], dto.Roles.Select(r => r.Code));
        Assert.Equal([4, 3, 2, 1], dto.Roles.Select(r => r.Rank));
        Assert.Equal([false, true, true, true], dto.Roles.Select(r => r.TenantAssignable));
        Assert.Equal(18, dto.Blocks.SelectMany(b => b.Permissions).Count());
        Assert.Equal(["/beauty", "/beauty/calendar"], dto.Blocks.First(b => b.Block == "calendar").Routes);
        var staff = dto.Blocks.SelectMany(b => b.Permissions).Single(p => p.Key == "staff.access");
        Assert.Equal("menu", staff.Scope);
        Assert.Equal(["own", "all"], staff.MenuLevels);
        Assert.Null(dto.Blocks.SelectMany(b => b.Permissions).Single(p => p.Key == "calendar.view").Scope);
        Assert.Equal(["lte:calendar.manage", "lte:clients.edit", "requires:clients.calls", "requires:revenue.costs", "requires:marketing.campaigns", "roleCeiling:team.manage"],
            dto.Constraints.Select(c => $"{c.Type}:{c.Key}"));
        Assert.Equal(["admin", "deputy", "specialist"], dto.Presets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(18, dto.Presets["deputy"].Count);
        Assert.Contains("ai.provider_keys", dto.ReservedToOwner);
    }

    // ---------- пресети (§30.3) ----------

    [Fact]
    public void Presets_match_contract_table()
    {
        //            admin  deputy  specialist
        var table = new Dictionary<string, string[]>
        {
            ["calendar.view"] = ["all", "all", "own"], ["calendar.manage"] = ["all", "all", "own"],
            ["clients.view"] = ["all", "all", "none"], ["clients.edit"] = ["all", "all", "none"],
            ["clients.contacts"] = ["full", "full", "masked"], ["clients.calls"] = ["all", "all", "none"],
            ["revenue.view"] = ["all", "none", "none"], ["revenue.costs"] = ["edit", "none", "none"],
            ["channels.inbox"] = ["reply", "reply", "none"], ["channels.settings"] = ["edit", "none", "none"],
            ["ai.use"] = ["use", "use", "none"], ["ai.settings"] = ["edit", "none", "none"],
            ["services.catalog"] = ["edit", "view", "view"], ["marketing.campaigns"] = ["edit", "none", "none"],
            ["staff.access"] = ["edit", "all", "own"], ["locations.access"] = ["edit", "all", "own"],
            ["settings.general"] = ["view", "none", "none"], ["team.manage"] = ["manage", "none", "none"],
        };
        Assert.Equal(18, table.Count);
        foreach (var (key, v) in table)
        {
            Assert.Equal(v[0], PermissionPresets.For(Roles.Admin)[key]);
            Assert.Equal(v[1], PermissionPresets.For(Roles.Deputy)[key]);
            Assert.Equal(v[2], PermissionPresets.For(Roles.Specialist)[key]);
        }
        Assert.All(PermissionCatalog.All, d => Assert.Equal(d.Highest, PermissionPresets.For(Roles.Owner)[d.Key]));
        Assert.All(PermissionCatalog.All, d => Assert.Equal(d.Lowest, PermissionPresets.For("nobody")[d.Key]));
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Deputy)]
    [InlineData(Roles.Specialist)]
    public void Presets_are_consistent_effective_equals_preset_with_all_modules(string role)
    {
        var set = Build(role, PermissionPresets.For(role));
        foreach (var (key, level) in PermissionPresets.For(role)) Assert.Equal(level, set.Level(key));
        Assert.Empty(set.ModuleDisabledKeys());
    }

    // ---------- AccessSet: fail-closed ----------

    [Fact]
    public void None_has_lowest_level_for_every_key_and_no_role()
    {
        var none = AccessSet.None;
        Assert.Null(none.Role);
        Assert.All(PermissionCatalog.All, d => Assert.Equal(d.Lowest, none.Level(d.Key)));
        Assert.False(none.AtLeast("calendar.view", "own"));
        Assert.True(none.AtLeast("services.catalog", "view")); // найнижчий рівень ключа без none
        Assert.False(none.AtLeast("services.catalog", "edit"));
    }

    [Fact]
    public void Missing_unknown_or_miscased_rows_mean_lowest_level()
    {
        var rows = new Dictionary<string, string>
        {
            ["Calendar.View"] = "all",       // регістр - невідомий ключ
            ["calendar.manage"] = "ALL",     // регістр рівня - невідомий рівень
            ["clients.view"] = "everything", // невідомий рівень
            ["bogus.key"] = "all",
            ["revenue.view"] = "own",        // валідний
        };
        var set = Build(Roles.Admin, rows);
        Assert.Equal("none", set.Level("calendar.view"));
        Assert.Equal("none", set.Level("calendar.manage"));
        Assert.Equal("none", set.Level("clients.view"));
        Assert.Equal("own", set.Level("revenue.view"));
        Assert.Equal("", set.Level("bogus.key"));
        Assert.False(set.AtLeast("bogus.key", "all"));
        Assert.False(set.AtLeast("revenue.view", "ALL"));
        Assert.Equal(18, set.Effective.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("platform_operator")]
    [InlineData("OWNER")]
    public void Non_tenant_role_gets_everything_lowest_even_with_rows(string? role)
    {
        var set = AccessSet.Build(role, PermissionPresets.For(Roles.Admin), AllModules, false);
        Assert.Null(set.Role);
        Assert.All(PermissionCatalog.All, d => Assert.Equal(d.Lowest, set.Level(d.Key)));
    }

    [Fact]
    public void Owner_has_max_levels_implicitly_rows_ignored_but_modules_still_apply()
    {
        var all = Build(Roles.Owner, new Dictionary<string, string> { ["calendar.view"] = "none" });
        Assert.All(PermissionCatalog.All, d => Assert.Equal(d.Highest, all.Level(d.Key)));

        var noClients = Build(Roles.Owner, null, AllModules.Where(m => m != "beauty_clients"));
        Assert.Equal("none", noClients.Level("clients.view"));
        Assert.Equal("masked", noClients.Level("clients.contacts"));
        Assert.Equal("none", noClients.Level("marketing.campaigns")); // залежить від clients.view = all
        Assert.Equal("manage", noClients.Level("team.manage")); // owner звільнений від roleCeiling
        Assert.Equal("edit", noClients.Level("settings.general")); // без модулів
    }

    // ---------- AccessSet: модулі як верхня межа ----------

    [Fact]
    public void Disabled_module_lowers_its_keys_and_rows_survive_reenable()
    {
        var rows = PermissionPresets.For(Roles.Admin);
        var off = Build(Roles.Admin, rows, AllModules.Where(m => m != "beauty_booking"));
        Assert.Equal("none", off.Level("calendar.view"));
        Assert.Equal("edit", off.Level("services.catalog")); // beauty_catalog лишився
        Assert.Equal("all", Build(Roles.Admin, rows, AllModules).Level("calendar.view")); // повернулося
        Assert.Contains("calendar.view", off.ModuleDisabledKeys());
        Assert.Contains("staff.access", off.ModuleDisabledKeys());
        Assert.DoesNotContain("settings.general", off.ModuleDisabledKeys());
    }

    [Fact]
    public void Module_names_are_matched_ignoring_case_and_empty_modules_lowers_all_module_keys()
    {
        Assert.Equal("all", Build(Roles.Admin, PermissionPresets.For(Roles.Admin), AllModules.Select(m => m.ToUpperInvariant())).Level("calendar.view"));
        var none = Build(Roles.Admin, PermissionPresets.For(Roles.Admin), []);
        Assert.Equal("view", none.Level("settings.general")); // без модуля - лишається як видано
        Assert.Equal("manage", none.Level("team.manage"));
        Assert.Equal("none", none.Level("calendar.view"));
        Assert.Equal("none", none.Level("ai.use"));
    }

    [Fact]
    public void BlockedByModule_distinguishes_module_from_role_denials()
    {
        var spec = Build(Roles.Specialist, PermissionPresets.For(Roles.Specialist), AllModules.Where(m => m != "beauty_clients"));
        Assert.False(spec.BlockedByModule("clients.view", "all")); // видано none: причина в ролі, не в модулі

        var admin = Build(Roles.Admin, PermissionPresets.For(Roles.Admin), AllModules.Where(m => m != "beauty_clients"));
        Assert.False(admin.AtLeast("clients.view", "own"));
        Assert.True(admin.BlockedByModule("clients.view", "own"));
        Assert.True(admin.BlockedByModule("marketing.campaigns", "edit")); // знижено через модуль ключа-передумови
        Assert.False(admin.BlockedByModule("calendar.view", "all")); // ефективне дозволяє
    }

    // ---------- AccessSet: інваріанти (§30.4) ----------

    [Fact]
    public void Invariants_lower_the_dependent_key_when_rows_were_edited_directly()
    {
        var rows = new Dictionary<string, string>(PermissionPresets.For(Roles.Deputy))
        {
            ["team.manage"] = "manage",       // roleCeiling [admin]: deputy -> none
            ["revenue.costs"] = "edit",       // requires revenue.view = all (у deputy none)
            ["marketing.campaigns"] = "edit", // requires clients.view = all
            ["clients.view"] = "own",         // -> marketing.campaigns none, clients.calls none, clients.edit own
            ["calendar.view"] = "own",        // calendar.manage (all) -> own
        };
        var set = Build(Roles.Deputy, rows);
        Assert.Equal("none", set.Level("team.manage"));
        Assert.Equal("none", set.Level("revenue.costs"));
        Assert.Equal("none", set.Level("marketing.campaigns"));
        Assert.Equal("none", set.Level("clients.calls"));
        Assert.Equal("own", set.Level("clients.edit"));
        Assert.Equal("own", set.Level("calendar.manage"));
        Assert.Equal("own", set.Level("clients.view")); // передумова не змінюється
        // ModuleDisabledKeys порожній: усе знижено інваріантами, не модулями
        Assert.Empty(set.ModuleDisabledKeys());
    }

    [Fact]
    public void Admin_keeps_team_manage_and_revenue_costs_when_prerequisites_hold()
    {
        var set = Build(Roles.Admin, PermissionPresets.For(Roles.Admin));
        Assert.Equal("manage", set.Level("team.manage"));
        Assert.Equal("edit", set.Level("revenue.costs"));
        Assert.Equal("edit", set.Level("marketing.campaigns"));
        var lowered = Build(Roles.Admin, new Dictionary<string, string>(PermissionPresets.For(Roles.Admin)) { ["revenue.view"] = "own" });
        Assert.Equal("none", lowered.Level("revenue.costs"));
    }

    // ---------- AccessSet: AtLeast, SpecialistInactive, Locked ----------

    [Fact]
    public void AtLeast_compares_indexes_not_strings()
    {
        var set = Build(Roles.Specialist, PermissionPresets.For(Roles.Specialist));
        Assert.True(set.AtLeast("calendar.view", "none"));
        Assert.True(set.AtLeast("calendar.view", "own"));
        Assert.False(set.AtLeast("calendar.view", "all"));
        Assert.False(set.AtLeast("calendar.view", "view")); // рівень не з цього ключа
        Assert.True(set.AtLeast("clients.contacts", "masked"));
        Assert.False(set.AtLeast("clients.contacts", "full"));
    }

    [Fact]
    public void SpecialistInactive_flag_is_kept_only_for_tenant_roles()
    {
        Assert.True(Build(Roles.Specialist, null, inactive: true).SpecialistInactive);
        Assert.False(Build(Roles.Specialist, null, inactive: false).SpecialistInactive);
        Assert.False(AccessSet.Build("nobody", null, AllModules, true).SpecialistInactive);
        Assert.False(AccessSet.None.SpecialistInactive);
    }

    [Fact]
    public void Locked_lists_keys_where_target_exceeds_actor_ceiling()
    {
        var actor = Build(Roles.Admin, new Dictionary<string, string>(PermissionPresets.For(Roles.Admin)) { ["revenue.view"] = "none", ["settings.general"] = "none" });
        var target = new Dictionary<string, string>(PermissionPresets.For(Roles.Deputy)) { ["revenue.view"] = "all", ["settings.general"] = "edit", ["bogus.x"] = "all" };
        var locked = actor.Locked(target);
        Assert.Contains("revenue.view", locked);
        Assert.Contains("settings.general", locked);
        Assert.DoesNotContain("calendar.view", locked);
        Assert.Empty(Build(Roles.Owner).Locked(PermissionPresets.For(Roles.Admin)));
    }

    // ---------- stamp ----------

    [Fact]
    public void AccessStamp_is_version_dot_hex8_of_sorted_modules()
    {
        var a = AccessStamp.Compute(7, ["beauty_clients", "beauty_booking"]);
        var b = AccessStamp.Compute(7, ["beauty_booking", "BEAUTY_CLIENTS", "beauty_booking"]);
        Assert.Equal(a, b);
        Assert.Matches(@"^7\.[0-9a-f]{8}$", a);
        Assert.NotEqual(a, AccessStamp.Compute(7, ["beauty_booking"]));
        Assert.StartsWith("8.", AccessStamp.Compute(8, ["beauty_booking", "beauty_clients"]));
        Assert.Equal(a[2..], AccessStamp.Compute(8, ["beauty_booking", "beauty_clients"])[2..]);
    }

    // ---------- Actor / ToActor (B2) ----------

    [Fact]
    public void ToActor_without_access_context_gets_AccessSet_None()
    {
        var principal = Principal(Roles.Admin);
        var actor = principal.ToActor()!;
        Assert.Same(AccessSet.None, actor.Access);
        Assert.False(actor.Access.AtLeast("calendar.view", "own"));
    }

    [Fact]
    public void ToActor_takes_AccessSet_from_loaded_context_only()
    {
        var principal = Principal(Roles.Admin);
        var ctx = new AccessContext();
        principal.AddIdentity(new AccessIdentity(ctx));
        Assert.Same(AccessSet.None, principal.ToActor()!.Access); // контекст не завантажено

        var set = Build(Roles.Admin, PermissionPresets.For(Roles.Admin));
        ctx.Load(set, 3, "3.abcdef01", ["beauty_booking"]);
        Assert.Same(set, principal.ToActor()!.Access);
        Assert.Same(set, principal.Clone().ToActor()!.Access); // Clone зберігає носій
    }

    [Fact]
    public void ToActor_accepts_deputy_and_rejects_unknown_roles()
    {
        Assert.NotNull(Principal(Roles.Deputy).ToActor());
        Assert.Null(Principal("platform_operator").ToActor());
        Assert.Null(Principal(null).ToActor());
    }

    private static ClaimsPrincipal Principal(string? role)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new("tenant_id", Guid.NewGuid().ToString()) };
        if (role is not null) claims.Add(new Claim("role", role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "sub", "role"));
    }

    // ---------- [RequirePermission] ----------

    [Fact]
    public void RequirePermission_ctor_rejects_unknown_key_or_level()
    {
        Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute("nope.nope", "all"));
        Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute("calendar.view", "edit"));
        Assert.Equal(-3000, new RequirePermissionAttribute("calendar.view", "own").Order);
    }

    private static async Task<IActionResult?> RunPermission(RequirePermissionAttribute attr, IAccessContext? ctx)
    {
        var services = new ServiceCollection().AddLogging();
        if (ctx is not null) services.AddSingleton(ctx);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var fc = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
        await attr.OnAuthorizationAsync(fc);
        return fc.Result;
    }

    private static AccessContext Loaded(AccessSet set)
    {
        var c = new AccessContext();
        c.Load(set, 1, "1.00000000", []);
        return c;
    }

    [Fact]
    public async Task RequirePermission_allows_when_level_is_enough()
    {
        var ctx = Loaded(Build(Roles.Admin, PermissionPresets.For(Roles.Admin)));
        Assert.Null(await RunPermission(new RequirePermissionAttribute("calendar.view", "all"), ctx));
        Assert.Null(await RunPermission(new RequirePermissionAttribute("calendar.view", "none"), ctx));
    }

    [Fact]
    public async Task RequirePermission_denies_with_forbidden_role_permission_and_required()
    {
        var ctx = Loaded(Build(Roles.Specialist, PermissionPresets.For(Roles.Specialist)));
        var result = Assert.IsType<ObjectResult>(await RunPermission(new RequirePermissionAttribute("clients.view", "own"), ctx));
        Assert.Equal(403, result.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("forbidden_role", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("clients.view", doc.RootElement.GetProperty("permission").GetString());
        Assert.Equal("own", doc.RootElement.GetProperty("required").GetString());
        Assert.True(doc.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task RequirePermission_returns_module_disabled_when_only_the_module_blocks()
    {
        var ctx = Loaded(Build(Roles.Admin, PermissionPresets.For(Roles.Admin), AllModules.Where(m => m != "beauty_clients")));
        var result = Assert.IsType<ObjectResult>(await RunPermission(new RequirePermissionAttribute("clients.view", "own"), ctx));
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("module_disabled", ((ApiError)result.Value!).Code);
    }

    [Fact]
    public async Task RequirePermission_fails_closed_without_access_context_or_unloaded()
    {
        Assert.IsType<ObjectResult>(await RunPermission(new RequirePermissionAttribute("calendar.view", "own"), null));
        Assert.IsType<ObjectResult>(await RunPermission(new RequirePermissionAttribute("calendar.view", "own"), new AccessContext()));
        // services.catalog має найнижчий рівень "view": навіть він без контексту не дає доступу до edit
        Assert.IsType<ObjectResult>(await RunPermission(new RequirePermissionAttribute("services.catalog", "edit"), null));
    }

    // ---------- порядок фільтрів (§30.5) ----------

    [Fact]
    public void Authorization_filter_order_is_tenant_then_module_then_permission_then_marketing_gate()
    {
        Assert.Equal(-3200, TenantActiveFilter.FilterOrder);
        Assert.Equal(-3100, RequireModuleAttribute.FilterOrder);
        Assert.Equal(-3000, RequirePermissionAttribute.FilterOrder);
        Assert.True(TenantActiveFilter.FilterOrder < RequireModuleAttribute.FilterOrder);
        Assert.True(RequireModuleAttribute.FilterOrder < RequirePermissionAttribute.FilterOrder);
        Assert.True(RequirePermissionAttribute.FilterOrder < new CouponsMarketingGateAttribute().Order);
        Assert.True(new CouponsMarketingGateAttribute().Order < -2000); // ModelStateInvalidFilter
        // module і permission - authorization-фільтри: виконуються до прив'язки тіла й усіх action-фільтрів
        Assert.IsAssignableFrom<IAsyncAuthorizationFilter>(new RequireModuleAttribute("beauty_booking"));
        Assert.IsAssignableFrom<IAsyncAuthorizationFilter>(new RequirePermissionAttribute("calendar.view", "own"));
        Assert.IsAssignableFrom<IAsyncAuthorizationFilter>(new TenantActiveFilter());
        Assert.IsAssignableFrom<IAsyncActionFilter>(new ActiveSpecialistFilter(new AccessContext())); // порядок 0, після 422
    }

    [Fact]
    public async Task ActiveSpecialistFilter_uses_access_context_flag_and_honours_the_exemption()
    {
        static async Task<IActionResult?> Run(AccessContext ctx, params object[] endpointMetadata)
        {
            var action = new ActionDescriptor { EndpointMetadata = endpointMetadata };
            var ec = new ActionExecutingContext(new ActionContext(new DefaultHttpContext(), new RouteData(), action), [], new Dictionary<string, object?>(), new object());
            await new ActiveSpecialistFilter(ctx).OnActionExecutionAsync(ec, () => Task.FromResult<ActionExecutedContext>(null!));
            return ec.Result;
        }

        var inactive = new AccessContext();
        inactive.Load(Build(Roles.Specialist, PermissionPresets.For(Roles.Specialist), inactive: true), 1, "1.0", []);
        var r = Assert.IsType<ObjectResult>(await Run(inactive));
        Assert.Equal(403, r.StatusCode);
        Assert.Equal("specialist_inactive", ((ApiError)r.Value!).Code);
        Assert.Null(await Run(inactive, new AllowInactiveSpecialistAttribute()));

        var active = new AccessContext();
        active.Load(Build(Roles.Specialist, PermissionPresets.For(Roles.Specialist)), 1, "1.0", []);
        Assert.Null(await Run(active));
        Assert.Null(await Run(new AccessContext())); // не завантажено (публічний маршрут)
    }
}
