using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-850 (ADR-023, §30.5, §30.13 п.3-5, 10-12): AccessContextMiddleware, me/catalog, [RequirePermission], порядок фільтрів,
/// deputy. Реальний PostgreSQL + RLS (WebApplicationFactory). Ендпоінти <see cref="AccessProbeController"/> дають доступ до
/// [RequirePermission] і ToActor() у справжньому конвеєрі (поки tenant-ендпоінти ще на ролях, TASK-852/853).
/// </summary>
public sealed class AccessApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly string[] Known = [.. BeautyModules.Known];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private HttpClient Client(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null, bool probe = true)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (probe) b.ConfigureTestServices(s => s.AddControllers().AddApplicationPart(typeof(AccessProbeController).Assembly));
            configure?.Invoke(b);
        });
        _disposables.Add(f);
        var c = f.CreateClient();
        _disposables.Add(c);
        return c;
    }

    private static async Task<(HttpResponseMessage Resp, JsonElement Body)> Get(HttpClient c, string url, string? token, HttpMethod? method = null, object? body = null)
    {
        var req = Req(method ?? HttpMethod.Get, url, body, token);
        var resp = await c.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (resp, json);
    }

    private static string? Code(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static string Stamp(HttpResponseMessage r) => r.Headers.GetValues("X-Access-Version").Single();

    // ---------- forged JWT (підписаний справжнім ключем тестового хоста) ----------

    private static string Forge(Dictionary<string, object?> claims)
    {
        var d = new SecurityTokenDescriptor
        {
            Issuer = "beautycrm",
            Audience = "beautycrm-api",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = claims.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Enumerable.Range(1, 48).Select(i => (byte)i).ToArray()), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(d);
    }

    // ---------- /auth/me ----------

    [SkippableFact]
    public async Task Me_for_owner_returns_all_max_permissions_stamp_no_store_and_header()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);

        var (resp, me) = await Get(c, "/api/auth/me", owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        Assert.Equal("owner", me.GetProperty("role").GetString());
        Assert.Equal(1, me.GetProperty("accessVersion").GetInt32());
        Assert.Equal(2, me.GetProperty("catalogVersion").GetInt32());
        Assert.Equal(me.GetProperty("accessStamp").GetString(), Stamp(resp));
        Assert.Matches(@"^1\.[0-9a-f]{8}$", Stamp(resp));
        Assert.Contains("beauty_booking", me.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        Assert.Contains("beauty_inventory", me.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        var perms = me.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(21, perms.Count);
        Assert.Equal(["all", "all", "edit"], new[] { perms["inventory.view"], perms["inventory.manage"], perms["inventory.costs"] });
        Assert.All(PermissionCatalog.All, d => Assert.Equal(d.Highest, perms[d.Key]));
    }

    [SkippableFact]
    public async Task Login_response_user_has_no_access_fields()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync([.. Known]);
        var r = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = Password });
        var user = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("user");
        Assert.False(user.TryGetProperty("permissions", out _));
        Assert.False(user.TryGetProperty("accessStamp", out _));
    }

    [SkippableFact]
    public async Task Me_for_deputy_and_admin_returns_effective_preset()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var deputy = await fx.CreateDeputyAsync(t);
        var admin = await fx.CreateUserAsync(t, Roles.Admin);

        foreach (var (token, role) in new[] { (deputy.Tokens.AccessToken, Roles.Deputy), (admin.Tokens.AccessToken, Roles.Admin) })
        {
            var (resp, me) = await Get(c, "/api/auth/me", token);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(role, me.GetProperty("role").GetString());
            var perms = me.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            Assert.Equal(PermissionPresets.For(role).OrderBy(x => x.Key).ToList(), perms.OrderBy(x => x.Key).ToList());
        }
    }

    [SkippableFact]
    public async Task Me_for_user_without_permission_rows_is_fail_closed()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var u = await fx.CreateUserAsync(t, Roles.Admin, permissions: new Dictionary<string, string>());
        var (_, me) = await Get(c, "/api/auth/me", u.Tokens.AccessToken);
        var perms = me.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.All(PermissionCatalog.All, d => Assert.Equal(d.Lowest, perms[d.Key]));
    }

    // ---------- /access/catalog ----------

    [SkippableFact]
    public async Task Catalog_is_available_to_every_staff_role_cached_privately_and_401_anonymous()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var deputy = await fx.CreateDeputyAsync(t);

        var (anon, _) = await Get(c, "/api/access/catalog", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var (r1, owned) = await Get(c, "/api/access/catalog", owner.AccessToken);
        var (r2, dep) = await Get(c, "/api/access/catalog", deputy.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.True(r1.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromSeconds(300), r1.Headers.CacheControl?.MaxAge);
        Assert.Equal(owned.GetRawText(), dep.GetRawText()); // однакова для всіх ролей
        Assert.Equal(2, owned.GetProperty("catalogVersion").GetInt32());
        Assert.Equal(12, owned.GetProperty("blocks").GetArrayLength());
        Assert.Equal(8, owned.GetProperty("constraints").GetArrayLength());
        Assert.Equal(4, owned.GetProperty("roles").GetArrayLength());
        var staff = owned.GetProperty("blocks").EnumerateArray().First(b => b.GetProperty("block").GetString() == "staff")
            .GetProperty("permissions")[0];
        Assert.Equal("menu", staff.GetProperty("scope").GetString());
        var calendar = owned.GetProperty("blocks")[0].GetProperty("permissions")[0];
        Assert.False(calendar.TryGetProperty("scope", out _));
        Assert.Equal("full", owned.GetProperty("presets").GetProperty("deputy").GetProperty("clients.contacts").GetString());
    }

    [SkippableFact]
    public async Task Access_version_header_is_exposed_by_cors()
    {
        NeedDb();
        var c = Client(b => b.UseSetting("Beauty:AllowedOrigins:0", "https://admin.example.test"));
        var t = await fx.CreateTenantAsync([.. Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var req = Req(HttpMethod.Get, "/api/auth/me", null, owner.AccessToken);
        req.Headers.Add("Origin", "https://admin.example.test");
        var resp = await c.SendAsync(req);
        Assert.Contains("X-Access-Version", resp.Headers.GetValues("Access-Control-Expose-Headers").SelectMany(h => h.Split(',')).Select(h => h.Trim()));
    }

    // ---------- middleware: fail-closed ----------

    [SkippableFact]
    public async Task Jwt_without_valid_sub_or_tenant_id_gets_401_invalid_token()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var userId = t.OwnerId.ToString();
        var cases = new[]
        {
            new Dictionary<string, object?> { ["tenant_id"] = t.TenantId.ToString(), ["role"] = "owner" },                                     // немає sub
            new Dictionary<string, object?> { ["sub"] = "not-a-guid", ["tenant_id"] = t.TenantId.ToString(), ["role"] = "owner" },
            new Dictionary<string, object?> { ["sub"] = Guid.Empty.ToString(), ["tenant_id"] = t.TenantId.ToString(), ["role"] = "owner" },
            new Dictionary<string, object?> { ["sub"] = userId, ["role"] = "owner" },                                                         // немає tenant_id
            new Dictionary<string, object?> { ["sub"] = userId, ["tenant_id"] = "zzz", ["role"] = "owner" },
            new Dictionary<string, object?> { ["sub"] = userId, ["tenant_id"] = Guid.Empty.ToString(), ["role"] = "owner" },
        };
        foreach (var claims in cases)
        {
            var (resp, body) = await Get(c, "/api/auth/me", Forge(claims));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("invalid_token", Code(body));
            Assert.False(resp.Headers.Contains("X-Access-Version"));
        }
    }

    [SkippableFact]
    public async Task Unknown_user_and_foreign_tenant_user_get_401_session_revoked()
    {
        NeedDb();
        var c = Client();
        var a = await fx.CreateTenantAsync([.. Known]);
        var b = await fx.CreateTenantAsync([.. Known]);

        // токен tenant A з sub користувача tenant B: під RLS tenant A користувача не видно
        var (r1, b1) = await Get(c, "/api/auth/me", Forge(new() { ["sub"] = b.OwnerId.ToString(), ["tenant_id"] = a.TenantId.ToString(), ["role"] = "owner" }));
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        Assert.Equal("session_revoked", Code(b1));

        var (r2, b2) = await Get(c, "/api/auth/me", Forge(new() { ["sub"] = Guid.NewGuid().ToString(), ["tenant_id"] = a.TenantId.ToString(), ["role"] = "owner" }));
        Assert.Equal("session_revoked", Code(b2));
        Assert.False(r2.Headers.Contains("X-Access-Version"));
    }

    private sealed class ThrowingAccessStore : IAccessStore
    {
        public Task<AccessSnapshot?> LoadAsync(Guid userId, CancellationToken ct) => throw new TimeoutException("db down");
    }

    [SkippableFact]
    public async Task Database_failure_gives_503_access_unavailable_and_never_falls_back_to_token_claims()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync([.. Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var broken = Client(b => b.ConfigureTestServices(s => s.AddScoped<IAccessStore, ThrowingAccessStore>()));

        foreach (var url in new[] { "/api/auth/me", "/api/__probe/actor", "/api/access/catalog", "/api/users", "/api/beauty/appointments" })
        {
            var (resp, body) = await Get(broken, url, owner.AccessToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.Equal("access_unavailable", Code(body));
            Assert.Equal("5", resp.Headers.GetValues("Retry-After").Single());
            Assert.False(resp.Headers.Contains("X-Access-Version"));
            Assert.False(body.TryGetProperty("permissions", out _));
        }

        // анонімні маршрути не залежать від перевірки доступу
        var login = await broken.SendAsync(Req(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = Password }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [SkippableFact]
    public async Task Allow_anonymous_endpoint_skips_the_middleware_even_with_a_stale_token()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var ghost = Forge(new() { ["sub"] = Guid.NewGuid().ToString(), ["tenant_id"] = t.TenantId.ToString(), ["role"] = "owner" });
        var resp = await c.SendAsync(Req(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = Password }, ghost));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Platform_key_principal_is_not_touched_by_the_middleware()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var r = await c.SendAsync(Req(HttpMethod.Patch, $"/api/platform/tenants/{t.TenantId}", new { modules = Known }, platformKey: PlatformKey));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.False(r.Headers.Contains("X-Access-Version"));
    }

    // ---------- заміна claims ----------

    [SkippableFact]
    public async Task Duplicate_and_spoofed_role_and_specialist_claims_are_replaced_with_db_values()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var spec = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistA);
        var plain = await fx.CreateUserAsync(t, Roles.Deputy);

        // дублікати role (specialist + owner) і ClaimTypes.Role = owner; spoof specialist_id
        var token = Forge(new()
        {
            ["sub"] = spec.UserId.ToString(), ["tenant_id"] = t.TenantId.ToString(),
            ["role"] = new[] { "specialist", "owner" },
            ["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] = "owner",
            ["specialist_id"] = seed.SpecialistB.ToString(),
        });
        var (resp, actor) = await Get(c, "/api/__probe/actor", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(["specialist"], actor.GetProperty("roleClaims").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(actor.GetProperty("msRoleClaims").EnumerateArray());
        Assert.Equal([seed.SpecialistA.ToString()], actor.GetProperty("specialistClaims").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("specialist", actor.GetProperty("role").GetString());
        Assert.Equal(seed.SpecialistA, actor.GetProperty("specialistId").GetGuid());

        // права підмінено ролі з БД: Management-політика не пускає "owner" з токена
        var (users, _) = await Get(c, "/api/users", token);
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);

        // користувач без прив'язки: specialist_id зі спуфленого токена прибрано
        var spoofNoLink = Forge(new() { ["sub"] = plain.UserId.ToString(), ["tenant_id"] = t.TenantId.ToString(), ["role"] = "owner", ["specialist_id"] = seed.SpecialistB.ToString() });
        var (_, actor2) = await Get(c, "/api/__probe/actor", spoofNoLink);
        Assert.Equal("deputy", actor2.GetProperty("role").GetString());
        Assert.Empty(actor2.GetProperty("specialistClaims").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, actor2.GetProperty("specialistId").ValueKind);
    }

    [SkippableFact]
    public async Task ToActor_in_the_real_pipeline_carries_effective_access()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var deputy = await fx.CreateDeputyAsync(t);
        var spec = await fx.CreateUserAsync(t, Roles.Specialist, (await fx.SeedBeautyAsync(t.TenantId)).SpecialistA);
        var (_, d) = await Get(c, "/api/__probe/actor", deputy.Tokens.AccessToken);
        Assert.Equal("all", d.GetProperty("clientsView").GetString());
        Assert.Equal("all", d.GetProperty("calendarView").GetString());
        var (_, s) = await Get(c, "/api/__probe/actor", spec.Tokens.AccessToken);
        Assert.Equal("none", s.GetProperty("clientsView").GetString());
        Assert.Equal("own", s.GetProperty("calendarView").GetString());
    }

    // ---------- миттєвість (D4) ----------

    [SkippableFact]
    public async Task Role_change_applies_on_next_request_without_relogin()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var admin = await fx.CreateUserAsync(t, Roles.Admin);
        var token = admin.Tokens.AccessToken;

        var (before, _) = await Get(c, "/api/users", token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode); // admin: Management
        var stampBefore = Stamp(before);

        await fx.MutateUserAsync(t.TenantId, admin.UserId, u => u.Role = Roles.Deputy);

        var (after, _) = await Get(c, "/api/users", token); // той самий access-токен, role=admin у claims
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode); // deputy поза Management (fail-closed), не 401
        Assert.NotEqual(stampBefore, Stamp(after));
        Assert.StartsWith("2.", Stamp(after));

        var (me, body) = await Get(c, "/api/auth/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("deputy", body.GetProperty("role").GetString());
        Assert.Equal(2, body.GetProperty("accessVersion").GetInt32());
    }

    [SkippableFact]
    public async Task Permission_change_gives_403_on_next_request_with_same_token_and_new_version()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var deputy = await fx.CreateDeputyAsync(t);
        var token = deputy.Tokens.AccessToken;

        var (ok, _) = await Get(c, "/api/__probe/clients", token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var v1 = Stamp(ok);

        var lowered = new Dictionary<string, string>(PermissionPresets.For(Roles.Deputy)) { ["clients.view"] = "own", ["clients.edit"] = "own" };
        await fx.MutateUserAsync(t.TenantId, deputy.UserId, _ => { }, lowered);

        var (denied, body) = await Get(c, "/api/__probe/clients", token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("forbidden_role", Code(body));
        Assert.Equal("clients.view", body.GetProperty("permission").GetString());
        Assert.Equal("all", body.GetProperty("required").GetString());
        Assert.NotEqual(v1, Stamp(denied)); // X-Access-Version є і на 403
    }

    [SkippableFact]
    public async Task Deactivated_or_not_active_user_gets_401_session_revoked_without_version_header()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var off = await fx.CreateDeputyAsync(t);
        var invited = await fx.CreateDeputyAsync(t);

        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/auth/me", off.Tokens.AccessToken)).Resp.StatusCode);
        await fx.MutateUserAsync(t.TenantId, off.UserId, u => u.IsActive = false);
        await fx.MutateUserAsync(t.TenantId, invited.UserId, u => u.State = "invited");

        foreach (var token in new[] { off.Tokens.AccessToken, invited.Tokens.AccessToken })
        {
            var (resp, body) = await Get(c, "/api/auth/me", token);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("session_revoked", Code(body));
            Assert.False(resp.Headers.Contains("X-Access-Version"));
        }
    }

    [SkippableFact]
    public async Task Module_change_through_control_plane_changes_stamp_and_effective_without_access_version_change()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var deputy = await fx.CreateDeputyAsync(t);
        var token = deputy.Tokens.AccessToken;

        var (r1, me1) = await Get(c, "/api/auth/me", token);
        Assert.Equal("all", me1.GetProperty("permissions").GetProperty("clients.view").GetString());

        await fx.SetModulesAsync(t.TenantId, "beauty_booking", "beauty_catalog");

        var (r2, me2) = await Get(c, "/api/auth/me", token);
        Assert.NotEqual(Stamp(r1), Stamp(r2));
        Assert.Equal(me1.GetProperty("accessVersion").GetInt32(), me2.GetProperty("accessVersion").GetInt32());
        Assert.Equal(me2.GetProperty("accessStamp").GetString(), Stamp(r2));
        Assert.Equal("none", me2.GetProperty("permissions").GetProperty("clients.view").GetString());
        Assert.Equal("all", me2.GetProperty("permissions").GetProperty("calendar.view").GetString());
        Assert.Equal(["beauty_booking", "beauty_catalog"], me2.GetProperty("modules").EnumerateArray().Select(x => x.GetString()));

        // [RequireModule] ішов першим: module_disabled, а не forbidden_role
        var (denied, body) = await Get(c, "/api/__probe/clients", token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("module_disabled", Code(body));
    }

    // ---------- [RequirePermission] і порядок фільтрів ----------

    [SkippableFact]
    public async Task RequirePermission_grants_deputy_denies_specialist_with_fields_before_body_binding()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var deputy = await fx.CreateDeputyAsync(t);
        var spec = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistA);

        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/__probe/clients", deputy.Tokens.AccessToken)).Resp.StatusCode);

        var (resp, body) = await Get(c, "/api/__probe/clients", spec.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("forbidden_role", Code(body));
        Assert.Equal("clients.view", body.GetProperty("permission").GetString());
        Assert.Equal("all", body.GetProperty("required").GetString());

        // невалідне тіло ({}): авторизація раніше за прив'язку - 403, а не 422
        var (post, pb) = await Get(c, "/api/__probe/body", spec.Tokens.AccessToken, HttpMethod.Post, new { });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal("forbidden_role", Code(pb));
        // дозволеному - уже 422 валідації
        var (post2, _) = await Get(c, "/api/__probe/body", deputy.Tokens.AccessToken, HttpMethod.Post, new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post2.StatusCode);
    }

    [SkippableFact]
    public async Task RequirePermission_attributes_combine_with_and()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var admin = await fx.CreateUserAsync(t, Roles.Admin);
        var deputy = await fx.CreateDeputyAsync(t); // calendar.view all, revenue.view none

        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/__probe/both", admin.Tokens.AccessToken)).Resp.StatusCode);
        var (resp, body) = await Get(c, "/api/__probe/both", deputy.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("revenue.view", body.GetProperty("permission").GetString());
    }

    [SkippableFact]
    public async Task RequirePermission_gives_module_disabled_when_the_keys_module_is_off()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. BeautyModules.All]); // без beauty_marketing
        var admin = await fx.CreateUserAsync(t, Roles.Admin);
        var (resp, body) = await Get(c, "/api/__probe/marketing", admin.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("module_disabled", Code(body));

        var spec = await fx.CreateUserAsync(t, Roles.Specialist, (await fx.SeedBeautyAsync(t.TenantId)).SpecialistA);
        var (resp2, body2) = await Get(c, "/api/__probe/marketing", spec.Tokens.AccessToken);
        Assert.Equal("forbidden_role", Code(body2)); // видано none: причина в ролі
    }

    [SkippableFact]
    public async Task Filter_order_tenant_suspended_then_module_then_permission()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var spec = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistA);
        var token = spec.Tokens.AccessToken;

        // 1) permission: specialist без clients.view
        Assert.Equal("forbidden_role", Code((await Get(c, "/api/__probe/clients", token)).Body));
        // 2) модуль вимкнено - module_disabled раніше за forbidden_role (D1)
        await fx.SetModulesAsync(t.TenantId, "beauty_booking", "beauty_catalog");
        Assert.Equal("module_disabled", Code((await Get(c, "/api/__probe/clients", token)).Body));
        Assert.Equal("module_disabled", Code((await Get(c, "/api/beauty/conversations", token)).Body)); // колишній InboxManagementOnly (D1)
        // 3) tenant призупинено - tenant_suspended раніше за module_disabled
        var pr = await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{t.TenantId}", new { status = "suspended" }, platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.NoContent, pr.StatusCode);
        var (resp, body) = await Get(c, "/api/__probe/clients", token);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("tenant_suspended", Code(body));
    }

    // ---------- deputy ----------

    [SkippableFact]
    public async Task Deputy_passes_staff_policy_and_gets_rights_from_preset()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var token = (await fx.CreateDeputyAsync(t)).Tokens.AccessToken;

        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/auth/me", token)).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/access/catalog", token)).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/beauty/locations", token)).Resp.StatusCode); // Staff (без перевірки ролі)
        // права з пресету deputy (§30.3): clients.view = all -> 200; ai.settings = none -> 403 forbidden_role (D3); Owner-політика -> 403 без тіла
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/beauty/clients", token)).Resp.StatusCode);
        var (resp, body) = await Get(c, "/api/beauty/ai/settings", token);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("forbidden_role", Code(body));
        var (ownerResp, ownerBody) = await Get(c, "/api/users/access-audit", token);
        Assert.Equal(HttpStatusCode.Forbidden, ownerResp.StatusCode);
        Assert.Null(Code(ownerBody));
    }

    // ---------- неактивний профіль майстра (M7) ----------

    [SkippableFact]
    public async Task Inactive_specialist_profile_blocks_tenant_endpoints_except_me_catalog_feedback()
    {
        NeedDb();
        var c = Client();
        var t = await fx.CreateTenantAsync([.. Known]);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var spec = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistA);
        var token = spec.Tokens.AccessToken;

        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/beauty/locations", token)).Resp.StatusCode);

        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var s = await ctx.Specialists.FirstAsync(x => x.Id == seed.SpecialistA);
            s.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        foreach (var url in new[] { "/api/beauty/locations", "/api/beauty/appointments", "/api/beauty/specialists", "/api/beauty/absences", "/api/__probe/actor" })
        {
            var (resp, body) = await Get(c, url, token);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.Equal("specialist_inactive", Code(body));
        }
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/auth/me", token)).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/access/catalog", token)).Resp.StatusCode);
        var (fb, _) = await Get(c, "/api/beauty/feedback", token, HttpMethod.Post, new { });
        Assert.NotEqual(HttpStatusCode.Forbidden, fb.StatusCode); // 422 валідації, не specialist_inactive

        // активний користувач без прив'язки (deputy) цим не зачіпається
        var deputy = await fx.CreateDeputyAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/beauty/locations", deputy.Tokens.AccessToken)).Resp.StatusCode);
    }
}
