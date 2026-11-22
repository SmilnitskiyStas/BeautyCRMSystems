using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Api.Auth.ControlPlane;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-864 (ADR-024, §31.8, §31.9, §31.15 п. 7, 13, 14, 22): запрошення персоналу (409 email_in_use, ліміт перевірок, квота нових
/// ідентичностей, гонка прийняття), provisioning власника (409 owner_email_in_use, активація pending, 410 invite_invalid).
/// Реальний PostgreSQL + RLS (WebApplicationFactory), роль застосунку без BYPASSRLS.
/// </summary>
public sealed class GlobalIdentityInviteTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Kid = "cp-tests";
    private const string NewPassword = "Brand-New-Passw0rd!";
    private static readonly byte[] Secret = Enumerable.Range(0, 48).Select(i => (byte)(i * 11 + 3)).ToArray();
    private static readonly object Actor = new { id = "sg-admin-7", displayName = "Admin Sg" };
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    // ---------- інфраструктура ----------

    private HttpClient ClientWith(Action<IWebHostBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Secret", Convert.ToBase64String(Secret));
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Status", "active");
            b.UseSetting("ControlPlane:PublicAppUrl", "https://app.example.test");
            configure?.Invoke(b);
            if (services is not null) b.ConfigureTestServices(services);
        });
        _disposables.Add(f);
        var c = f.CreateClient();
        _disposables.Add(c);
        return c;
    }

    private static HttpRequestMessage Signed(HttpMethod method, string path, object? body = null)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = "N" + Guid.NewGuid().ToString("N");
        var sig = ControlPlaneSignature.Sign(Secret, ControlPlaneSignature.Canonical(Kid, timestamp, nonce, method.Method, path, "", bytes));
        var r = new HttpRequestMessage(method, path);
        if (bytes.Length > 0)
        {
            r.Content = new ByteArrayContent(bytes);
            r.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        }
        r.Headers.Add(ControlPlaneSignature.KeyIdHeader, Kid);
        r.Headers.Add(ControlPlaneSignature.TimestampHeader, timestamp);
        r.Headers.Add(ControlPlaneSignature.NonceHeader, nonce);
        r.Headers.Add(ControlPlaneSignature.SignatureHeader, sig);
        return r;
    }

    private static async Task<(HttpResponseMessage Resp, JsonElement Body)> Read(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return (resp, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? Code(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static string NewEmail(string prefix = "p") => $"{prefix}-{Guid.NewGuid():N}@inv.test";

    private async Task<T> AdminScalar<T>(string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        var o = await cmd.ExecuteScalarAsync();
        return o is null or DBNull ? default! : (T)o;
    }

    private async Task AdminExec(string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(TenantSeed Tenant, string Owner)> OwnerOfNewTenantAsync()
    {
        var t = await fx.CreateTenantAsync();
        return (t, (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken);
    }

    private Task<HttpResponseMessage> Invite(HttpClient c, string bearer, string email, string role = "admin") =>
        c.SendAsync(Req(HttpMethod.Post, "/api/invites", new { email, role }, bearer));

    private Task<HttpResponseMessage> Accept(HttpClient c, string token, string fullName = "Staff") =>
        c.SendAsync(Req(HttpMethod.Post, "/api/auth/invites/accept", new { token, fullName, password = Password }));

    private async Task<string> InviteTokenAsync(HttpClient c, string bearer, string email, string role = "admin")
    {
        var r = await Invite(c, bearer, email, role);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AuthApiFixture.Read<InviteCreatedDto>(r)).Token;
    }

    // ============================== 13. запрошення персоналу: створення ==============================

    [SkippableFact]
    public async Task Invite_with_an_email_used_in_another_business_is_409_email_in_use_without_naming_it()
    {
        NeedDb();
        var (a, ownerA) = await OwnerOfNewTenantAsync();
        var (b, _) = await OwnerOfNewTenantAsync();
        var logs = new CapturingLoggerProvider();
        var c = ClientWith(services: s => s.AddSingleton<ILoggerProvider>(logs));

        var (resp, body) = await Read(await Invite(c, ownerA, b.OwnerEmail)); // активна ідентичність іншого бізнесу
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("email_in_use", Code(body));
        Assert.Equal("This email is already used in Beauty CRM.", body.GetProperty("message").GetString());
        var raw = body.ToString();
        Assert.DoesNotContain(b.Slug, raw);
        Assert.DoesNotContain(b.OwnerEmail, raw);
        Assert.Equal(0L, await AdminScalar<long>("SELECT count(*) FROM invites WHERE tenant_id = @t", ("t", a.TenantId)));

        // лог без пошти, але з tenant
        Assert.Contains(logs.Lines, l => l.Contains("invite_email_in_use") && l.Contains(a.TenantId.ToString()));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(b.OwnerEmail));
    }

    [SkippableFact]
    public async Task Invite_with_an_email_held_by_a_pending_identity_is_409_email_in_use()
    {
        NeedDb();
        var (_, ownerA) = await OwnerOfNewTenantAsync();
        var c = ClientWith();
        var pendingEmail = NewEmail("pend");
        var created = await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", new
        {
            externalRef = "sg-" + Guid.NewGuid().ToString("N")[..16], name = "Pending Co", slug = "pc-" + Guid.NewGuid().ToString("N")[..16],
            owner = new { email = pendingEmail, fullName = "Pending Owner" }, actor = Actor,
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var (resp, body) = await Read(await Invite(c, ownerA, pendingEmail));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("email_in_use", Code(body));
    }

    [SkippableFact]
    public async Task Invite_with_an_email_that_is_already_a_member_of_this_tenant_stays_user_exists()
    {
        NeedDb();
        var (_, owner) = await OwnerOfNewTenantAsync();
        var email = NewEmail("m");
        Assert.Equal(HttpStatusCode.Created, (await Accept(fx.Client, await InviteTokenAsync(fx.Client, owner, email))).StatusCode);

        var (resp, body) = await Read(await Invite(fx.Client, owner, email));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("user_exists", Code(body));
    }

    [SkippableFact]
    public async Task Specialist_invite_endpoint_also_returns_email_in_use()
    {
        NeedDb();
        var (a, ownerA) = await OwnerOfNewTenantAsync();
        var (b, _) = await OwnerOfNewTenantAsync();
        var seed = await fx.SeedBeautyAsync(a.TenantId);
        var (resp, body) = await Read(await fx.Client.SendAsync(Req(HttpMethod.Post, $"/api/beauty/specialists/{seed.SpecialistA}/invite",
            new { email = b.OwnerEmail }, ownerA)));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("email_in_use", Code(body));
    }

    // ============================== 7. ліміт перевірок пошти на tenant ==============================

    [SkippableFact]
    public async Task Invite_email_check_is_limited_per_tenant_and_every_check_spends_a_permit_whatever_the_result()
    {
        NeedDb();
        var (a, ownerA) = await OwnerOfNewTenantAsync();
        var (b, ownerB) = await OwnerOfNewTenantAsync();
        var c = ClientWith(b => b.UseSetting("Auth:InviteEmailCheck:PerTenantPerHour", "3"));

        Assert.Equal(HttpStatusCode.Created, (await Invite(c, ownerA, NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Invite(c, ownerA, b.OwnerEmail)).StatusCode); // email_in_use теж витрачає місце
        Assert.Equal(HttpStatusCode.Created, (await Invite(c, ownerA, NewEmail())).StatusCode);

        var limited = await Invite(c, ownerA, NewEmail());
        var (resp, body) = await Read(limited);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal("rate_limited", Code(body));
        var retry = int.Parse(resp.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retry, 1, 3600);
        // ліміт на tenant: інший бізнес не зачеплений
        Assert.Equal(HttpStatusCode.Created, (await Invite(c, ownerB, NewEmail())).StatusCode);
        Assert.NotEqual(Guid.Empty, a.TenantId);
    }

    // ============================== 13. прийняття: ідентичність, dual-write, аудит ==============================

    [SkippableFact]
    public async Task Accept_creates_an_active_identity_with_dual_write_and_audits_identity_created_for_the_inviting_tenant_without_email()
    {
        NeedDb();
        var (a, owner) = await OwnerOfNewTenantAsync();
        var email = NewEmail("acc");
        var token = await InviteTokenAsync(fx.Client, owner, email);

        var (resp, _) = await Read(await Accept(fx.Client, token));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        Assert.Equal("active", await AdminScalar<string>("SELECT state FROM identities WHERE email = @e", ("e", email)));
        var identityHash = await AdminScalar<string>("SELECT password_hash FROM identities WHERE email = @e", ("e", email));
        var memberHash = await AdminScalar<string>("SELECT password_hash FROM users WHERE tenant_id = @t AND email = @e", ("t", a.TenantId), ("e", email));
        Assert.False(string.IsNullOrEmpty(identityHash));
        Assert.Equal(identityHash, memberHash); // dual-write до фази 3
        Assert.Equal(await AdminScalar<Guid>("SELECT id FROM identities WHERE email = @e", ("e", email)),
            await AdminScalar<Guid>("SELECT identity_id FROM users WHERE tenant_id = @t AND email = @e", ("t", a.TenantId), ("e", email)));

        var details = await AdminScalar<string>(
            "SELECT details::text FROM platform_audit_log WHERE action = 'identity.created' AND tenant_id = @t AND details ->> 'via' = 'staff_invite'", ("t", a.TenantId));
        Assert.DoesNotContain(email, details);
        Assert.Equal("identity", await AdminScalar<string>(
            "SELECT actor_type FROM platform_audit_log WHERE action = 'identity.created' AND tenant_id = @t AND details ->> 'via' = 'staff_invite'", ("t", a.TenantId)));

        // вхід без коду бізнесу працює
        var login = await fx.Client.SendAsync(Req(HttpMethod.Post, "/api/auth/login", new { email, password = Password }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [SkippableFact]
    public async Task Accept_is_409_email_in_use_when_the_email_was_taken_meanwhile_and_the_invite_stays_pending()
    {
        NeedDb();
        var (a, ownerA) = await OwnerOfNewTenantAsync();
        var (b, ownerB) = await OwnerOfNewTenantAsync();
        var email = NewEmail("race");
        var tokenA = await InviteTokenAsync(fx.Client, ownerA, email); // обидва запрошення створено, поки пошта була вільною
        var tokenB = await InviteTokenAsync(fx.Client, ownerB, email);

        // гонка: обидва приймають паралельно -> рівно один 201
        var results = await Task.WhenAll(Accept(fx.Client, tokenA), Accept(fx.Client, tokenB));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        var loser = results.Single(r => r.StatusCode != HttpStatusCode.Created);
        var (resp, body) = await Read(loser);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("email_in_use", Code(body));
        Assert.Equal(1L, await AdminScalar<long>("SELECT count(*) FROM identities WHERE email = @e", ("e", email)));

        // запрошення переможеного лишилось pending (не прийняте й не відкликане)
        var (loserTenant, loserOwner) = results[0].StatusCode == HttpStatusCode.Created ? (b, ownerB) : (a, ownerA);
        Assert.Equal(1L, await AdminScalar<long>(
            "SELECT count(*) FROM invites WHERE tenant_id = @t AND email = @e AND accepted_at IS NULL AND revoked_at IS NULL", ("t", loserTenant.TenantId), ("e", email)));
        Assert.Equal(0L, await AdminScalar<long>("SELECT count(*) FROM users WHERE tenant_id = @t AND email = @e", ("t", loserTenant.TenantId), ("e", email)));
        var listed = (await Read(await fx.Client.SendAsync(Req(HttpMethod.Get, "/api/invites", bearer: loserOwner)))).Body;
        Assert.Contains(listed.EnumerateArray(), i => i.GetProperty("status").GetString() == "pending");
    }

    [SkippableFact]
    public async Task Accept_is_user_exists_when_the_email_became_a_member_of_this_tenant_meanwhile()
    {
        NeedDb();
        var (a, owner) = await OwnerOfNewTenantAsync();
        var email = NewEmail("dupe");
        var first = await InviteTokenAsync(fx.Client, owner, email);
        // створити другий токен без відкликання першого неможливо (повторне запрошення відкликає попереднє) - тож: приймаємо, потім
        // відновлюємо запрошення в БД й приймаємо ще раз
        Assert.Equal(HttpStatusCode.Created, (await Accept(fx.Client, first)).StatusCode);
        await AdminExec("UPDATE invites SET accepted_at = NULL WHERE tenant_id = @t AND email = @e", ("t", a.TenantId), ("e", email));

        var (resp, body) = await Read(await Accept(fx.Client, first));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("user_exists", Code(body));
    }

    // ============================== 22. квота нових ідентичностей ==============================

    [SkippableFact]
    public async Task Identity_quota_per_tenant_gives_429_with_retry_after_on_accept_and_leaves_the_invite_pending()
    {
        NeedDb();
        var (a, ownerA) = await OwnerOfNewTenantAsync();
        var (b, ownerB) = await OwnerOfNewTenantAsync();
        var c = ClientWith(b => b.UseSetting("Auth:IdentityCreate:PerTenantPerDay", "2"));

        var tokens = new List<(string Email, string Token)>();
        for (var i = 0; i < 3; i++)
        {
            var e = NewEmail("q");
            tokens.Add((e, await InviteTokenAsync(c, ownerA, e)));
        }
        Assert.Equal(HttpStatusCode.Created, (await Accept(c, tokens[0].Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Accept(c, tokens[1].Token)).StatusCode);

        var (resp, body) = await Read(await Accept(c, tokens[2].Token)); // третя за добу
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal("rate_limited", Code(body));
        var retry = int.Parse(resp.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retry, 60, 86400);
        Assert.Equal(0, retry % 60); // округлення вгору до хвилини
        Assert.Equal(0L, await AdminScalar<long>("SELECT count(*) FROM identities WHERE email = @e", ("e", tokens[2].Email)));
        Assert.Equal(1L, await AdminScalar<long>(
            "SELECT count(*) FROM invites WHERE tenant_id = @t AND email = @e AND accepted_at IS NULL AND revoked_at IS NULL", ("t", a.TenantId), ("e", tokens[2].Email)));

        // квота іншого tenant не зачеплена
        var other = NewEmail("qb");
        Assert.Equal(HttpStatusCode.Created, (await Accept(c, await InviteTokenAsync(c, ownerB, other))).StatusCode);

        // кожне створення записало identity.created з правильним tenant і via
        Assert.Equal(2L, await AdminScalar<long>(
            "SELECT count(*) FROM platform_audit_log WHERE action = 'identity.created' AND tenant_id = @t AND details ->> 'via' = 'staff_invite'", ("t", a.TenantId)));
        Assert.Equal(1L, await AdminScalar<long>(
            "SELECT count(*) FROM platform_audit_log WHERE action = 'identity.created' AND tenant_id = @t AND details ->> 'via' = 'staff_invite'", ("t", b.TenantId)));
    }

    // ============================== 14. власник: control plane ==============================

    private static object ControlBody(string externalRef, string slug, string email) => new
    {
        externalRef, name = "Owner Co", slug, owner = new { email, fullName = "Owner Name" }, actor = Actor,
    };

    private static string Ext() => "sg-" + Guid.NewGuid().ToString("N")[..16];

    private static string Slug() => "own-" + Guid.NewGuid().ToString("N")[..20];

    [SkippableFact]
    public async Task Control_plane_create_with_an_email_in_use_is_409_owner_email_in_use_and_creates_nothing()
    {
        NeedDb();
        var existing = await fx.CreateTenantAsync(); // активна ідентичність
        var c = ClientWith();
        var ext = Ext();
        var slug = Slug();

        var (resp, body) = await Read(await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(ext, slug, existing.OwnerEmail))));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("owner_email_in_use", Code(body));
        Assert.Equal(0L, await AdminScalar<long>("SELECT count(*) FROM tenants WHERE slug = @s OR external_ref = @x", ("s", slug), ("x", ext)));

        // pending-ідентичність теж займає пошту
        var pendingEmail = NewEmail("cp");
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(Ext(), Slug(), pendingEmail)))).StatusCode);
        var (resp2, body2) = await Read(await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(Ext(), Slug(), pendingEmail.ToUpperInvariant()))));
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
        Assert.Equal("owner_email_in_use", Code(body2));
    }

    [SkippableFact]
    public async Task Control_plane_keeps_the_order_replay_then_ref_conflict_then_slug_taken_then_owner_email_in_use()
    {
        NeedDb();
        var c = ClientWith();
        var email = NewEmail("ord");
        var ext = Ext();
        var slug = Slug();
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(ext, slug, email)))).StatusCode);

        // replay за externalRef: 200, не 409 (пошту тепер займає pending-ідентичність цього ж tenant)
        var replay = await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(ext, slug, email)));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        // externalRef зайнятий іншим tenant -> external_ref_conflict (раніше за пошту)
        var (_, refBody) = await Read(await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(ext, Slug(), NewEmail()))));
        Assert.Equal("external_ref_conflict", Code(refBody));
        // slug зайнятий, і пошта теж -> tenant_slug_taken (раніше за пошту)
        var (slugResp, slugBody) = await Read(await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(Ext(), slug, email))));
        Assert.Equal(HttpStatusCode.Conflict, slugResp.StatusCode);
        Assert.Equal("tenant_slug_taken", Code(slugBody));
        // новий externalRef і slug, пошта зайнята -> owner_email_in_use
        var (_, mailBody) = await Read(await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(Ext(), Slug(), email))));
        Assert.Equal("owner_email_in_use", Code(mailBody));
    }

    [SkippableFact]
    public async Task Parallel_control_plane_creates_with_the_same_owner_email_give_one_success_and_one_owner_email_in_use()
    {
        NeedDb();
        var c = ClientWith();
        var email = NewEmail("par");
        var results = await Task.WhenAll(
            c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(Ext(), Slug(), email))),
            c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(Ext(), Slug(), email))));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        var loser = results.Single(r => r.StatusCode != HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("owner_email_in_use", Code((await Read(loser)).Body));
        Assert.Equal(1L, await AdminScalar<long>("SELECT count(*) FROM identities WHERE email = @e", ("e", email)));
        Assert.Equal(1L, await AdminScalar<long>("SELECT count(*) FROM users WHERE email = @e", ("e", email)));
    }

    // ============================== 14. власник: /api/platform/tenants ==============================

    [SkippableFact]
    public async Task Platform_create_with_an_email_in_use_is_409_owner_email_in_use_and_slug_taken_still_works()
    {
        NeedDb();
        var existing = await fx.CreateTenantAsync();
        var slug = Slug();
        var (resp, body) = await Read(await fx.Send(HttpMethod.Post, "/api/platform/tenants", new
        {
            name = "Dup", slug, owner = new { email = existing.OwnerEmail, fullName = "O", password = Password },
        }, platformKey: PlatformKey));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("owner_email_in_use", Code(body));
        Assert.Equal(0L, await AdminScalar<long>("SELECT count(*) FROM tenants WHERE slug = @s", ("s", slug)));

        var (slugResp, slugBody) = await Read(await fx.Send(HttpMethod.Post, "/api/platform/tenants", new
        {
            name = "Dup", slug = existing.Slug, owner = new { email = NewEmail("fresh"), fullName = "O", password = Password },
        }, platformKey: PlatformKey));
        Assert.Equal(HttpStatusCode.Conflict, slugResp.StatusCode);
        Assert.Equal("tenant_slug_taken", Code(slugBody));
    }

    [SkippableFact]
    public async Task Platform_create_makes_the_owner_identity_active_at_once()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        Assert.Equal("active", await AdminScalar<string>("SELECT state FROM identities WHERE email = @e", ("e", t.OwnerEmail)));
        Assert.Equal(Guid.Empty == t.OwnerId ? 0L : 1L, await AdminScalar<long>(
            "SELECT count(*) FROM users u JOIN identities i ON i.id = u.identity_id WHERE u.id = @u AND i.password_hash = u.password_hash", ("u", t.OwnerId)));
    }

    // ============================== 14. прийняття запрошення власника ==============================

    private async Task<(Guid TenantId, string Slug, string Email, string Token)> CpOwnerAsync(HttpClient c)
    {
        var email = NewEmail("ow");
        var slug = Slug();
        var r = await c.SendAsync(Signed(HttpMethod.Post, "/api/control/v1/tenants", ControlBody(Ext(), slug, email)));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = (await Read(r)).Body;
        var url = j.GetProperty("invite").GetProperty("url").GetString()!;
        return (j.GetProperty("tenantId").GetGuid(), slug, email, url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..]);
    }

    [SkippableFact]
    public async Task Owner_accept_activates_the_pending_identity_and_the_owner_can_log_in_without_a_business_code()
    {
        NeedDb();
        var c = ClientWith();
        var o = await CpOwnerAsync(c);
        Assert.Equal("pending", await AdminScalar<string>("SELECT state FROM identities WHERE email = @e", ("e", o.Email)));
        // pending не входить
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/login", new { email = o.Email, password = NewPassword }))).StatusCode);

        var accept = await c.SendAsync(Req(HttpMethod.Post, "/api/auth/invite/accept", new { token = o.Token, password = NewPassword }));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal("active", await AdminScalar<string>("SELECT state FROM identities WHERE email = @e", ("e", o.Email)));
        Assert.Equal(1L, await AdminScalar<long>(
            "SELECT count(*) FROM identities i JOIN users u ON u.identity_id = i.id WHERE i.email = @e AND i.password_hash = u.password_hash AND i.password_hash IS NOT NULL", ("e", o.Email)));
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/login", new { email = o.Email, password = NewPassword }))).StatusCode);
    }

    [SkippableFact]
    public async Task Owner_accept_is_410_invite_invalid_and_rolls_back_when_the_identity_is_already_active()
    {
        NeedDb();
        var c = ClientWith();
        var o = await CpOwnerAsync(c);
        await AdminExec("UPDATE identities SET state = 'active', password_hash = 'x-existing' WHERE email = @e", ("e", o.Email)); // як після злиття

        var (resp, body) = await Read(await c.SendAsync(Req(HttpMethod.Post, "/api/auth/invite/accept", new { token = o.Token, password = NewPassword })));
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
        Assert.Equal("invite_invalid", Code(body));
        // відкат: запрошення не погашено, власник усе ще invited без пароля, ідентичність не змінена
        Assert.Equal(1L, await AdminScalar<long>("SELECT count(*) FROM platform_owner_invites WHERE tenant_id = @t AND used_at IS NULL", ("t", o.TenantId)));
        Assert.Equal("invited", await AdminScalar<string>("SELECT state FROM users WHERE tenant_id = @t AND email = @e", ("t", o.TenantId), ("e", o.Email)));
        Assert.Equal("x-existing", await AdminScalar<string>("SELECT password_hash FROM identities WHERE email = @e", ("e", o.Email)));
    }

    // ============================== §31.12: метрики ==============================

    [SkippableFact]
    public async Task Identity_created_and_invite_email_in_use_metrics_are_emitted_without_pii()
    {
        NeedDb();
        var measurements = new List<(string Name, string? Via)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == AuthMetrics.MeterName && inst.Name is "identity_created" or "invite_email_in_use") l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, _, tags, _) =>
        {
            string? via = null;
            foreach (var t in tags) if (t.Key == "via") via = t.Value?.ToString();
            lock (measurements) measurements.Add((inst.Name, via));
        });
        listener.Start();

        var (a, ownerA) = await OwnerOfNewTenantAsync(); // via = platform
        var (b, _) = await OwnerOfNewTenantAsync();
        Assert.Equal(HttpStatusCode.Created, (await Accept(fx.Client, await InviteTokenAsync(fx.Client, ownerA, NewEmail()))).StatusCode); // staff_invite
        Assert.Equal(HttpStatusCode.Conflict, (await Invite(fx.Client, ownerA, b.OwnerEmail)).StatusCode);

        lock (measurements)
        {
            Assert.Contains(("identity_created", "platform"), measurements);
            Assert.Contains(("identity_created", "staff_invite"), measurements);
            Assert.Contains(("invite_email_in_use", (string?)null), measurements);
        }
        Assert.NotEqual(Guid.Empty, a.TenantId);
    }
}
