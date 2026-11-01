using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Conversations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Inbox;

/// <summary>
/// TASK-739 (§22.6.5, §22.10, §22.12 сценарій 17): HTTP-шар inbox через повний конвеєр (JWT, ролі, модуль, tenant, rate limit, заголовки)
/// на реальному PostgreSQL. Telegram - fake HTTP-обробник; реальної мережі немає.
/// </summary>
public sealed class InboxApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Base = "/api/beauty/conversations";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client, FakeBotApi Bot);
    private sealed record Tn(TenantSeed Seed, string Owner, string Admin, string Spec, Guid ChannelId, Guid Conv)
    {
        public Guid Id => Seed.TenantId;
    }

    private Env NewEnv()
    {
        var bot = new FakeBotApi();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Channels:EncryptionKey", Convert.ToBase64String(Enumerable.Range(10, 32).Select(i => (byte)i).ToArray()));
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IChannelAdapter>();
                s.AddSingleton<IChannelAdapter>(sp => new TelegramAdapter(new HttpClient(bot), sp.GetRequiredService<IChannelCredentialsProvider>()));
            });
        });
        _disposables.Add(f);
        return new Env(f, f.CreateClient(), bot);
    }

    private async Task<Tn> NewTenantAsync(Env e)
    {
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        var channelId = Guid.NewGuid();
        var put = await fx.Send(HttpMethod.Put, $"/api/beauty/channels/{channelId}",
            new { type = "telegram", name = "bot", locationId = seed.LocationId, isActive = true, token = "700111:BOTTOKENSECRETVALUE", webhookSecret = "whsecret" },
            owner.AccessToken, client: e.Client);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Guid conv;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var c = new Conversation { ChannelId = channelId, ExternalChatId = "5550123456", LastMessageAt = DateTimeOffset.UtcNow };
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            ctx.Add(new Message
            {
                ConversationId = c.Id, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client, Body = "Доброго дня",
                Status = "held", SentAt = DateTimeOffset.UtcNow, ExternalMessageId = "1", IdempotencyKey = $"telegram:{Guid.NewGuid():N}",
            });
            await ctx.SaveChangesAsync();
            conv = c.Id;
        }
        return new Tn(t, owner.AccessToken, admin.AccessToken, spec.AccessToken, channelId, conv);
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? token, object? body = null, string? key = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) r.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return r;
    }

    private static string K() => "http-" + Guid.NewGuid().ToString("N");
    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    // ------------------------------------------------------------------ ролі

    [SkippableFact]
    public async Task anonymous_is_401_specialist_is_403_forbidden_role_on_every_endpoint_admin_and_owner_pass()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync(e);
        var calls = new (HttpMethod M, string Url, object? Body, string? Key)[]
        {
            (HttpMethod.Get, Base, null, null),
            (HttpMethod.Get, $"{Base}/{t.Conv}", null, null),
            (HttpMethod.Post, $"{Base}/{t.Conv}/reply", new { text = "hi" }, K()),
            (HttpMethod.Post, $"{Base}/{t.Conv}/resume-ai", null, null),
            (HttpMethod.Post, $"{Base}/{t.Conv}/resolve", null, null),
            (HttpMethod.Post, $"{Base}/{t.Conv}/read", null, null),
        };
        foreach (var (m, url, body, key) in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await e.Client.SendAsync(Req(m, url, null, body, key))).StatusCode);
            var spec = await e.Client.SendAsync(Req(m, url, t.Spec, body, key));
            Assert.Equal(HttpStatusCode.Forbidden, spec.StatusCode);
            Assert.Equal("forbidden_role", await Code(spec));
        }
        // специфікатор із зламаним тілом теж отримує 403, а не 422 (роль перевіряється до прив'язки тіла)
        var broken = Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Spec, null, K());
        broken.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Client.SendAsync(broken)).StatusCode);
        Assert.Empty(e.Bot.Messages);

        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Get, Base, t.Admin))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}/{t.Conv}", t.Owner))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "hi" }, K()))).StatusCode);
    }

    [SkippableFact]
    public async Task disabled_channels_module_is_403_module_disabled()
    {
        NeedDb();
        var e = NewEnv();
        var t = await fx.CreateTenantAsync(["beauty_booking"]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var r = await e.Client.SendAsync(Req(HttpMethod.Get, Base, owner.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("module_disabled", await Code(r));
    }

    // ------------------------------------------------------------------ відповідь

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("пробіл у ключі 1")]
    [InlineData("has space in it")]
    public async Task reply_without_a_valid_idempotency_key_is_400(string? key)
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync(e);
        var r = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "hi" }, key));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("idempotency_key_required", await Code(r));
        var tooLong = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "hi" }, new string('k', 129)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "hi" }, new string('k', 128)))).StatusCode);
        Assert.Single(e.Bot.Messages);
    }

    [SkippableFact]
    public async Task reply_flow_202_same_key_same_message_other_text_409_invalid_text_422_literal_html()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync(e);
        var key = K();
        var r1 = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "<b>Вітаю</b>" }, key));
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        var j1 = (await J(r1)).GetProperty("message");
        Assert.Equal("sent", j1.GetProperty("status").GetString());
        Assert.Equal("no-store", r1.Headers.CacheControl?.ToString());

        var r2 = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "<b>Вітаю</b>" }, key));
        Assert.Equal(HttpStatusCode.Accepted, r2.StatusCode);
        Assert.Equal(j1.GetProperty("id").GetGuid(), (await J(r2)).GetProperty("message").GetProperty("id").GetGuid());
        var r3 = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "інакше" }, key));
        Assert.Equal(HttpStatusCode.Conflict, r3.StatusCode);
        Assert.Equal("idempotency_conflict", await Code(r3));
        var sent = Assert.Single(e.Bot.Messages);
        Assert.Equal(("<b>Вітаю</b>", "5550123456", "700111:BOTTOKENSECRETVALUE"), (sent.Text, sent.ChatId, sent.Token));

        foreach (var body in new object[] { new { text = new string('a', 3501) }, new { text = "   " }, new { }, new { text = "a\tb" } })
        {
            var bad = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, body, K()));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
            Assert.Equal("invalid_text", await Code(bad));
        }
        Assert.Single(e.Bot.Messages);

        // історія та стан у деталях: staff від admin, held → processed, ШІ на паузі безстроково
        var d = await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}/{t.Conv}", t.Owner)));
        Assert.Equal("handoff", d.GetProperty("conversation").GetProperty("aiState").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("conversation").GetProperty("aiPausedUntil").ValueKind);
        var msgs = d.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["processed", "sent"], msgs.Select(m => m.GetProperty("status").GetString()).ToArray());
        Assert.Equal("staff", msgs[1].GetProperty("senderType").GetString());
        Assert.True(msgs[1].GetProperty("senderUser").GetProperty("name").GetString()!.Length > 0);
    }

    [SkippableFact]
    public async Task eleventh_reply_in_a_minute_is_429_rate_limited_with_retry_after()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync(e);
        for (var n = 0; n < 10; n++)
            Assert.Equal(HttpStatusCode.Accepted, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = $"m{n}" }, K()))).StatusCode);
        var r = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/reply", t.Admin, new { text = "11" }, K()));
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("rate_limited", await Code(r));
        Assert.True(r.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        Assert.Equal(10, e.Bot.Messages.Count);
    }

    // ------------------------------------------------------------------ IDOR

    [SkippableFact]
    public async Task foreign_tenant_conversation_is_404_conversation_not_found_for_every_endpoint()
    {
        NeedDb();
        var e = NewEnv();
        var a = await NewTenantAsync(e); var b = await NewTenantAsync(e);
        var calls = new (HttpMethod M, string Url, object? Body, string? Key)[]
        {
            (HttpMethod.Get, $"{Base}/{a.Conv}", null, null),
            (HttpMethod.Post, $"{Base}/{a.Conv}/reply", new { text = "чужа" }, K()),
            (HttpMethod.Post, $"{Base}/{a.Conv}/resume-ai", null, null),
            (HttpMethod.Post, $"{Base}/{a.Conv}/resolve", null, null),
            (HttpMethod.Post, $"{Base}/{a.Conv}/read", null, null),
            (HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", null, null),
        };
        foreach (var (m, url, body, key) in calls)
        {
            var r = await e.Client.SendAsync(Req(m, url, b.Owner, body, key));
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal("conversation_not_found", await Code(r));
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        }
        Assert.Empty(e.Bot.Messages);
        var list = await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}?filter=all", b.Owner)));
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == a.Conv);
        Assert.Equal(b.Conv, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        // header X-Tenant-Id не дозволяє підміну tenant із токена
        var spoof = Req(HttpMethod.Get, $"{Base}/{a.Conv}", b.Owner);
        spoof.Headers.Add("X-Tenant-Id", a.Id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await e.Client.SendAsync(spoof)).StatusCode);
    }

    // ------------------------------------------------------------------ форма відповідей

    [SkippableFact]
    public async Task list_and_detail_have_the_contract_shape_no_store_and_no_external_chat_id()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync(e);
        await using (var ctx = fx.Db.CreateContext(t.Id))
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_conversations SET awaiting_staff_since = now(), ai_state = 'handoff', handoff_reason = 'complaint' WHERE id = {t.Conv}");

        var listResp = await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}?filter=needs_reply&take=10", t.Admin));
        Assert.Equal("no-store", listResp.Headers.CacheControl?.ToString());
        var text = await listResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("5550123456", text);
        var list = JsonDocument.Parse(text).RootElement;
        var item = list.GetProperty("items")[0];
        Assert.Equal(1, list.GetProperty("meta").GetProperty("needsReplyCount").GetInt32());
        Assert.Equal(("telegram", "Клієнт Telegram", "handoff", "complaint", "Доброго дня"),
            (item.GetProperty("channel").GetProperty("type").GetString(), item.GetProperty("title").GetString(), item.GetProperty("aiState").GetString(),
             item.GetProperty("handoffReason").GetString(), item.GetProperty("lastMessagePreview").GetString()));
        Assert.Equal(1, item.GetProperty("unreadCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("awaitingStaffSince").ValueKind);
        Assert.Equal(t.ChannelId, item.GetProperty("channel").GetProperty("id").GetGuid());

        var detail = await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}/{t.Conv}", t.Admin));
        Assert.Equal("no-store", detail.Headers.CacheControl?.ToString());
        var dj = await J(detail);
        Assert.True(dj.GetProperty("conversation").GetProperty("canReply").GetBoolean());
        var m = dj.GetProperty("messages")[0];
        Assert.Equal(("inbound", "client", "text", "held"),
            (m.GetProperty("direction").GetString(), m.GetProperty("senderType").GetString(), m.GetProperty("kind").GetString(), m.GetProperty("status").GetString()));
        Assert.False(dj.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, dj.GetProperty("upcomingAppointments").GetArrayLength());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}?filter=bogus", t.Admin))).StatusCode);
    }

    [SkippableFact]
    public async Task resume_ai_and_read_return_204_and_read_accepts_an_empty_or_json_body()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync(e);
        await using (var ctx = fx.Db.CreateContext(t.Id))
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_conversations SET awaiting_staff_since = now(), ai_state = 'handoff' WHERE id = {t.Conv}");
        var msgId = (await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}/{t.Conv}", t.Admin)))).GetProperty("messages")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/read", t.Admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/read", t.Admin, new { upToMessageId = msgId }))).StatusCode);
        var missing = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/read", t.Admin, new { upToMessageId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("message_not_found", await Code(missing));

        Assert.Equal(HttpStatusCode.NoContent, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/resume-ai", t.Admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/resume-ai", t.Admin))).StatusCode);
        var d = await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}/{t.Conv}", t.Admin)));
        Assert.Equal("active", d.GetProperty("conversation").GetProperty("aiState").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("conversation").GetProperty("awaitingStaffSince").ValueKind);
        Assert.Equal("processed", d.GetProperty("messages")[0].GetProperty("status").GetString());
        Assert.Equal(0, d.GetProperty("conversation").GetProperty("unreadCount").GetInt32());
    }

    [SkippableFact]
    public async Task resolve_returns_204_no_store_and_is_idempotent_and_removes_the_conversation_from_needs_reply()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync(e);
        await using (var ctx = fx.Db.CreateContext(t.Id))
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_conversations SET awaiting_staff_since = now(), ai_state = 'handoff' WHERE id = {t.Conv}");
        Assert.Equal(1, (await J(await e.Client.SendAsync(Req(HttpMethod.Get, Base, t.Admin)))).GetProperty("meta").GetProperty("needsReplyCount").GetInt32());

        var r1 = await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/resolve", t.Admin));
        Assert.Equal(HttpStatusCode.NoContent, r1.StatusCode);
        Assert.Equal("no-store", r1.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await e.Client.SendAsync(Req(HttpMethod.Post, $"{Base}/{t.Conv}/resolve", t.Owner))).StatusCode);

        var list = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Base, t.Admin)));
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(0, list.GetProperty("meta").GetProperty("needsReplyCount").GetInt32());
        var d = await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Base}/{t.Conv}", t.Admin)));
        Assert.Equal("active", d.GetProperty("conversation").GetProperty("aiState").GetString());
        Assert.Equal(0, d.GetProperty("conversation").GetProperty("unreadCount").GetInt32());
    }
}
