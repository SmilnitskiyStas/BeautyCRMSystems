using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Security;

/// <summary>
/// TASK-689: M5 (Instagram appSecret != verifyToken), L3 (ключ дедуплікації включає channelId), L4 (PUT /channels/{id}: колізія id -> 409).
/// Повний HTTP-конвеєр із реальними адаптерами (mock-адаптерів у Production немає) на PostgreSQL з роллю без BYPASSRLS.
/// </summary>
public sealed class ChannelSecurityTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;
    private readonly WebApplicationFactory<Program>? _factory;
    private readonly HttpClient _client = null!;

    public ChannelSecurityTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
        if (fx.SkipReason is not null) return;
        _factory = fx.Factory!.WithWebHostBuilder(b =>
            b.UseSetting("Channels:EncryptionKey", Convert.ToBase64String(Enumerable.Range(20, 32).Select(i => (byte)i).ToArray())));
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory?.Dispose();

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private Task<HttpResponseMessage> Put(string token, Guid id, object body) =>
        _fx.Send(HttpMethod.Put, $"/api/beauty/channels/{id}", body, token, client: _client);

    private static string Hmac(string secret, string body) =>
        "sha256=" + Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static string IgBody(string mid) =>
        $$$"""{"entry":[{"messaging":[{"sender":{"id":"u1"},"recipient":{"id":"p"},"timestamp":1700000000000,"message":{"mid":"{{{mid}}}","text":"Hi"}}]}]}""";

    private Task<HttpResponseMessage> PostWebhook(string channel, Guid id, string body, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/beauty/webhooks/{channel}/{id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (n, v) in headers) req.Headers.Add(n, v);
        return _client.SendAsync(req);
    }

    private Task<HttpResponseMessage> Handshake(Guid id, string verifyToken, string challenge = "chal-123") =>
        _client.GetAsync($"/api/beauty/webhooks/instagram/{id}?hub.mode=subscribe&hub.verify_token={Uri.EscapeDataString(verifyToken)}&hub.challenge={challenge}");

    private async Task<int> MessageCountAsync(Guid tenantId)
    {
        await using var ctx = _fx.Db.CreateContext(tenantId);
        return await ctx.Messages.CountAsync();
    }

    // ---------- M5 ----------

    [SkippableFact]
    public async Task instagram_uses_separate_appSecret_for_signature_and_verifyToken_for_handshake()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var id = Guid.NewGuid();
        var put = await Put(s.OwnerToken, id, new
        {
            type = "instagram", name = "ig", isActive = true, token = "IGTOKEN-ABCD", webhookSecret = "legacy-webhook-secret",
            appSecret = "app-secret-xyz-1", verifyToken = "verify-token-xyz-2",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var raw = await put.Content.ReadAsStringAsync();
        var dto = JsonDocument.Parse(raw).RootElement;
        Assert.True(dto.GetProperty("hasAppSecret").GetBoolean());
        Assert.True(dto.GetProperty("hasVerifyToken").GetBoolean());
        foreach (var secret in new[] { "app-secret-xyz-1", "verify-token-xyz-2", "legacy-webhook-secret", "IGTOKEN" })
            Assert.DoesNotContain(secret, raw);
        var list = await (await _fx.Send(HttpMethod.Get, "/api/beauty/channels", bearer: s.OwnerToken, client: _client)).Content.ReadAsStringAsync();
        foreach (var secret in new[] { "app-secret-xyz-1", "verify-token-xyz-2", "legacy-webhook-secret" })
            Assert.DoesNotContain(secret, list);

        // POST: лише HMAC(appSecret)
        var ok = await PostWebhook("instagram", id, IgBody("m-ok"), ("X-Hub-Signature-256", Hmac("app-secret-xyz-1", IgBody("m-ok"))));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        foreach (var wrong in new[] { "legacy-webhook-secret", "verify-token-xyz-2", "IGTOKEN-ABCD" })
        {
            var body = IgBody("m-" + wrong.Length);
            var denied = await PostWebhook("instagram", id, body, ("X-Hub-Signature-256", Hmac(wrong, body)));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        Assert.Equal(1, await MessageCountAsync(s.Tenant.TenantId));

        // GET handshake: лише verifyToken
        var good = await Handshake(id, "verify-token-xyz-2");
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("chal-123", await good.Content.ReadAsStringAsync());
        foreach (var wrong in new[] { "app-secret-xyz-1", "legacy-webhook-secret", "", "nope" })
            Assert.Equal(HttpStatusCode.Forbidden, (await Handshake(id, wrong)).StatusCode);
    }

    [SkippableFact]
    public async Task instagram_without_appSecret_or_verifyToken_rejects_everything_even_with_legacy_webhookSecret()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Put(s.OwnerToken, id, new
        {
            type = "instagram", name = "ig", isActive = true, token = "IGTOKEN-ABCD", webhookSecret = "legacy-webhook-secret",
        })).StatusCode);

        var body = IgBody("m-legacy");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostWebhook("instagram", id, body, ("X-Hub-Signature-256", Hmac("legacy-webhook-secret", body)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Handshake(id, "legacy-webhook-secret")).StatusCode);
        Assert.Equal(0, await MessageCountAsync(s.Tenant.TenantId));

        // додавання лише appSecret не вмикає handshake, а часткове оновлення зберігає решту секретів
        Assert.Equal(HttpStatusCode.OK, (await Put(s.OwnerToken, id, new
        {
            type = "instagram", name = "ig2", isActive = true, appSecret = "app-secret-xyz-1",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await PostWebhook("instagram", id, body, ("X-Hub-Signature-256", Hmac("app-secret-xyz-1", body)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Handshake(id, "app-secret-xyz-1")).StatusCode);
    }

    // ---------- L3 ----------

    private const string TgBody = """{"update_id":1,"message":{"message_id":7,"from":{"id":42,"first_name":"Ann"},"chat":{"id":42},"date":1700000000,"text":"Hello"}}""";

    [SkippableFact]
    public async Task telegram_dedup_key_includes_channel_id_so_two_bots_of_one_tenant_do_not_collide()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var (id, secret) in new[] { (a, "secret-bot-a"), (b, "secret-bot-b") })
            Assert.Equal(HttpStatusCode.OK, (await Put(s.OwnerToken, id, new
            {
                type = "telegram", name = "bot", isActive = true, token = "123456:ABCDEF", webhookSecret = secret,
            })).StatusCode);

        static (string, string) H(string secret) => ("X-Telegram-Bot-Api-Secret-Token", secret);
        var ra = await PostWebhook("telegram", a, TgBody, H("secret-bot-a"));
        var rb = await PostWebhook("telegram", b, TgBody, H("secret-bot-b")); // той самий chat_id:message_id в іншому боті
        Assert.Equal(1, (await Read<JsonElement>(ra)).GetProperty("accepted").GetInt32());
        Assert.Equal(1, (await Read<JsonElement>(rb)).GetProperty("accepted").GetInt32());
        Assert.Equal(2, await MessageCountAsync(s.Tenant.TenantId));

        // повторна доставка в той самий канал дедуплікується
        Assert.Equal(0, (await Read<JsonElement>(await PostWebhook("telegram", a, TgBody, H("secret-bot-a")))).GetProperty("accepted").GetInt32());
        Assert.Equal(0, (await Read<JsonElement>(await PostWebhook("telegram", b, TgBody, H("secret-bot-b")))).GetProperty("accepted").GetInt32());
        Assert.Equal(2, await MessageCountAsync(s.Tenant.TenantId));

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var keys = await ctx.Messages.Select(m => m.IdempotencyKey).ToListAsync();
        Assert.Contains(keys, k => k == $"telegram:{a:N}:42:7");
        Assert.Contains(keys, k => k == $"telegram:{b:N}:42:7");
    }

    // ---------- L4 ----------

    [SkippableFact]
    public async Task put_channel_with_id_owned_by_another_tenant_is_409_and_does_not_touch_it()
    {
        NeedDb();
        var one = await _h.CreateSalonAsync();
        var two = await _h.CreateSalonAsync();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Put(one.OwnerToken, id, new
        {
            type = "telegram", name = "tenant-one-bot", isActive = true, token = "123456:ABCDEF", webhookSecret = "secret-one",
        })).StatusCode);

        var clash = await Put(two.OwnerToken, id, new { type = "telegram", name = "hijack", isActive = true, token = "999999:ZZZZZZ", webhookSecret = "evil" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("channel_id_conflict", await RegressionHarness.CodeAsync(clash));
        Assert.DoesNotContain("tenant-one-bot", await clash.Content.ReadAsStringAsync());

        // канал власника не змінився і далі працює зі своїм секретом; другий tenant канал не бачить
        var list = await Read<JsonElement>(await _fx.Send(HttpMethod.Get, "/api/beauty/channels", bearer: one.OwnerToken, client: _client));
        Assert.Equal("tenant-one-bot", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty((await Read<JsonElement>(await _fx.Send(HttpMethod.Get, "/api/beauty/channels", bearer: two.OwnerToken, client: _client))).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook("telegram", id, TgBody, ("X-Telegram-Bot-Api-Secret-Token", "secret-one"))).StatusCode);

        // власний повторний PUT (оновлення) працює
        Assert.Equal(HttpStatusCode.OK, (await Put(one.OwnerToken, id, new { name = "renamed", isActive = true })).StatusCode);
        // створення з новим id у другому tenant теж працює
        Assert.Equal(HttpStatusCode.OK, (await Put(two.OwnerToken, Guid.NewGuid(), new { type = "viber", name = "ok", isActive = true })).StatusCode);
    }
}
