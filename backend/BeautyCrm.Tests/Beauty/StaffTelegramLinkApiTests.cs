using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-890 (§32.24.8, §32.24.10 п.2) через повний HTTP-конвеєр на реальній БД (роль app, RLS): рольовий фільтр до розбору тіла,
/// модульний гейт, прапори й запобіжник L3, коди помилок, ліміт 5/год, no-store, відсутність токена/chat id в API, ізоляція tenant.
/// Telegram - <see cref="FakeStaffTransport"/>.
/// </summary>
public sealed class StaffTelegramLinkApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Url = "/api/beauty/inventory/telegram/link";
    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }
    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private WebApplicationFactory<Program> Host(bool enabled, FakeStaffTransport? transport = null, bool l3 = true)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Channels:EncryptionKey", Convert.ToBase64String(Enumerable.Range(20, 32).Select(i => (byte)i).ToArray()));
            b.UseSetting("Inventory:WritePerUserPerMinute", "600");
            if (enabled) b.UseSetting(StaffTelegramOptions.EnabledKey, "true");
            if (l3) b.UseSetting(StaffTelegramOptions.L3Key, "true");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IStaffTelegramTransport>();
                s.AddSingleton<IStaffTelegramTransport>(transport ?? new FakeStaffTransport());
            });
        });
        _disposables.Add(f);
        return f;
    }

    private async Task<(TenantSeed T, string Owner, string Admin, string Spec, string Deputy)> NewTenantAsync(bool withChannel = true, bool inventory = true)
    {
        var t = await fx.CreateTenantAsync(inventory ? [.. BeautyModules.Known] : [.. BeautyModules.Known.Where(m => m != BeautyModules.Inventory)]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        if (withChannel)
        {
            await using var ctx = fx.Db.CreateContext(t.TenantId);
            ctx.Add(new Channel { Type = ChannelType.Telegram, Name = "SalonBot", IsActive = true });
            await ctx.SaveChangesAsync();
        }
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        var deputy = await fx.CreateDeputyAsync(t);
        return (t, owner.AccessToken, admin.AccessToken, spec.AccessToken, deputy.Tokens.AccessToken);
    }

    private static Task<HttpResponseMessage> Call(HttpClient c, HttpMethod m, string? token, object? body = null, string? raw = null)
    {
        var r = new HttpRequestMessage(m, Url);
        if (raw is not null) r.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        else if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c.SendAsync(r);
    }

    private static async Task<JsonElement> Json200(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> Fails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        var el = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, el.GetProperty("code").GetString());
        return el;
    }

    private static readonly object Consent = new { consent = true, consentVersion = "inv-tg-v1" };

    [SkippableFact]
    public async Task Owner_flow_get_issue_replace_unlink_with_no_store_and_no_secrets_in_status()
    {
        NeedDb();
        var c = Host(enabled: true).CreateClient();
        var t = await NewTenantAsync();

        var none = await Json200(await Call(c, HttpMethod.Get, t.Owner));
        Assert.True(none.GetProperty("featureEnabled").GetBoolean());
        Assert.Equal("none", none.GetProperty("state").GetString());
        Assert.Equal("inv-tg-v1", none.GetProperty("consentVersion").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("lastRevoke").ValueKind);

        var resp = await Call(c, HttpMethod.Post, t.Owner, Consent);
        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString());
        var issued = await Json200(resp, HttpStatusCode.Created);
        var deepLink = issued.GetProperty("deepLink").GetString()!;
        Assert.Matches(@"^https://t\.me/test_bot\?start=inv_[A-Za-z0-9_-]{43}$", deepLink);
        Assert.True(issued.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        var token = deepLink[(deepLink.IndexOf("inv_", StringComparison.Ordinal) + 4)..];

        var pending = await Call(c, HttpMethod.Get, t.Owner);
        Assert.Contains("no-store", pending.Headers.CacheControl?.ToString());
        var pendingBody = await pending.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, pendingBody); // токен лише в одній відповіді POST
        Assert.DoesNotContain("chat", pendingBody, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(pendingBody);
        Assert.Equal("pending", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("SalonBot", doc.RootElement.GetProperty("channel").GetProperty("name").GetString());

        await Json200(await Call(c, HttpMethod.Post, t.Owner, Consent), HttpStatusCode.Created); // новий токен замінює попередній
        Assert.Equal(1, await CountAsync(t.T.TenantId, "status = 'revoked' AND revoke_reason = 'replaced'"));
        Assert.Equal(1, await CountAsync(t.T.TenantId, "status = 'issued'"));

        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, t.Owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, t.Owner)).StatusCode); // ідемпотентно
        var after = await Json200(await Call(c, HttpMethod.Get, t.Owner));
        Assert.Equal("none", after.GetProperty("state").GetString());
        Assert.Equal("user_unlink", after.GetProperty("lastRevoke").GetProperty("reason").GetString());
    }

    private async Task<long> CountAsync(Guid tenantId, string where)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.Db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM beauty_staff_telegram_links WHERE tenant_id = '{tenantId}' AND {where}", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [SkippableFact]
    public async Task Non_owner_roles_get_forbidden_role_before_body_binding_and_without_token_401()
    {
        NeedDb();
        var c = Host(enabled: true).CreateClient();
        var t = await NewTenantAsync();
        foreach (var token in new[] { t.Admin, t.Spec, t.Deputy })
        {
            await Fails(await Call(c, HttpMethod.Get, token), HttpStatusCode.Forbidden, "forbidden_role");
            await Fails(await Call(c, HttpMethod.Post, token, Consent), HttpStatusCode.Forbidden, "forbidden_role");
            await Fails(await Call(c, HttpMethod.Post, token, raw: "{not json"), HttpStatusCode.Forbidden, "forbidden_role"); // роль до розбору тіла
            await Fails(await Call(c, HttpMethod.Delete, token), HttpStatusCode.Forbidden, "forbidden_role");
        }
        foreach (var m in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(c, m, null, Consent)).StatusCode);
        Assert.Equal(0, await CountAsync(t.T.TenantId, "true"));
    }

    [SkippableFact]
    public async Task Module_disabled_returns_403_module_disabled()
    {
        NeedDb();
        var c = Host(enabled: true).CreateClient();
        var t = await NewTenantAsync(inventory: false);
        await Fails(await Call(c, HttpMethod.Get, t.Owner), HttpStatusCode.Forbidden, "module_disabled");
        await Fails(await Call(c, HttpMethod.Post, t.Owner, Consent), HttpStatusCode.Forbidden, "module_disabled");
        await Fails(await Call(c, HttpMethod.Delete, t.Owner), HttpStatusCode.Forbidden, "module_disabled");
    }

    [SkippableFact]
    public async Task Flag_off_post_forbidden_get_reports_disabled_delete_still_works()
    {
        NeedDb();
        var c = Host(enabled: false).CreateClient();
        var t = await NewTenantAsync();
        await Fails(await Call(c, HttpMethod.Post, t.Owner, Consent), HttpStatusCode.Forbidden, "telegram_alerts_disabled");
        var st = await Json200(await Call(c, HttpMethod.Get, t.Owner));
        Assert.False(st.GetProperty("featureEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, t.Owner)).StatusCode);
        Assert.Equal(0, await CountAsync(t.T.TenantId, "true"));
    }

    [SkippableFact]
    public async Task Flag_on_without_l3_outside_development_fails_host_start()
    {
        NeedDb();
        var f = Host(enabled: true, l3: false);
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
        await Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Validation_codes_channel_selection_and_bot_unavailable()
    {
        NeedDb();
        var transport = new FakeStaffTransport();
        var c = Host(enabled: true, transport).CreateClient();
        var t = await NewTenantAsync(withChannel: false);

        await Fails(await Call(c, HttpMethod.Post, t.Owner, new { consent = false, consentVersion = "inv-tg-v1" }), (HttpStatusCode)422, "consent_required");
        await Fails(await Call(c, HttpMethod.Post, t.Owner, new { consentVersion = "inv-tg-v1" }), (HttpStatusCode)422, "consent_required");
        var outdated = await Fails(await Call(c, HttpMethod.Post, t.Owner, new { consent = true, consentVersion = "inv-tg-v0" }), (HttpStatusCode)422, "consent_version_outdated");
        Assert.Equal("inv-tg-v1", outdated.GetProperty("currentVersion").GetString());
        await Fails(await Call(c, HttpMethod.Post, t.Owner, Consent), (HttpStatusCode)422, "telegram_channel_unavailable");

        await using (var ctx = fx.Db.CreateContext(t.T.TenantId))
        {
            ctx.Add(new Channel { Type = ChannelType.Telegram, Name = "A", IsActive = true });
            ctx.Add(new Channel { Type = ChannelType.Telegram, Name = "B", IsActive = true });
            await ctx.SaveChangesAsync();
        }
        var req = await Fails(await Call(c, HttpMethod.Post, t.Owner, Consent), (HttpStatusCode)422, "telegram_channel_required");
        var channels = req.GetProperty("channels").EnumerateArray().ToList();
        Assert.Equal(2, channels.Count);
        await Fails(await Call(c, HttpMethod.Post, t.Owner, new { consent = true, consentVersion = "inv-tg-v1", channelId = Guid.NewGuid() }), HttpStatusCode.NotFound, "channel_not_found");

        var chosen = channels[0].GetProperty("id").GetGuid();
        transport.BotDown = true;
        await Fails(await Call(c, HttpMethod.Post, t.Owner, new { consent = true, consentVersion = "inv-tg-v1", channelId = chosen }), HttpStatusCode.ServiceUnavailable, "telegram_bot_unavailable");
        transport.BotDown = false;
        Assert.Equal(0, await CountAsync(t.T.TenantId, "true"));
        await Json200(await Call(c, HttpMethod.Post, t.Owner, new { consent = true, consentVersion = "inv-tg-v1", channelId = chosen }), HttpStatusCode.Created);
    }

    [SkippableFact]
    public async Task Sixth_issue_in_an_hour_is_rate_limited_with_retry_after()
    {
        NeedDb();
        var c = Host(enabled: true).CreateClient();
        var t = await NewTenantAsync();
        for (var i = 0; i < 5; i++) await Json200(await Call(c, HttpMethod.Post, t.Owner, Consent), HttpStatusCode.Created);
        var r = await Call(c, HttpMethod.Post, t.Owner, Consent);
        await Fails(r, (HttpStatusCode)429, "rate_limited");
        Assert.True(r.Headers.RetryAfter is not null);
    }

    [SkippableFact]
    public async Task Tenants_are_isolated_and_active_link_blocks_new_issue()
    {
        NeedDb();
        var c = Host(enabled: true).CreateClient();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        await Json200(await Call(c, HttpMethod.Post, a.Owner, Consent), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, b.Owner)).StatusCode); // чужий owner нічого не закриває
        Assert.Equal(1, await CountAsync(a.T.TenantId, "status = 'issued'"));
        Assert.Equal("none", (await Json200(await Call(c, HttpMethod.Get, b.Owner))).GetProperty("state").GetString());

        // active -> 409
        await using (var conn = new Npgsql.NpgsqlConnection(fx.Db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"UPDATE beauty_staff_telegram_links SET status = 'active', token_hash = NULL, token_expires_at = NULL,
                chat_id_encrypted = 'x', chat_id_hash = decode(repeat('ab', 32), 'hex'), claimed_at = now(), bot_confirmed_at = now(), activated_at = now()
                WHERE tenant_id = '{a.T.TenantId}'", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await Fails(await Call(c, HttpMethod.Post, a.Owner, Consent), HttpStatusCode.Conflict, "telegram_already_linked");
        var st = await Json200(await Call(c, HttpMethod.Get, a.Owner));
        Assert.Equal("active", st.GetProperty("state").GetString());
        Assert.DoesNotContain("chat_id", st.ToString());
    }
}
