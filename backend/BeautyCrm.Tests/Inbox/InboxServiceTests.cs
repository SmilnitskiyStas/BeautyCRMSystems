using BeautyCrm.Application.Features.BeautyChatBooking;
using System.Net;
using System.Text;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Inbox;
using BeautyCrm.Tests.Channels;
using BeautyCrm.Tests.Conversations;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Tests.Inbox;

/// <summary>
/// TASK-739 (контракт §22.6.5, §22.12 сценарій 17 + IDOR/ідемпотентність/ролі/ліміти): сервіс inbox на реальному PostgreSQL (RLS),
/// fake Telegram (HTTP-обробник), реальні OutboundDispatcher/OutboxProcessor і sweeper. Реального Telegram немає.
/// </summary>
public sealed class InboxServiceTests : IClassFixture<PostgresRlsFixture>
{
    private const string Chat = "5550123456";
    private readonly PostgresRlsFixture _db;
    public InboxServiceTests(PostgresRlsFixture db) => _db = db;

    private sealed record Env(ChatHarness H, ChatSeed S, Actor Admin, Actor Owner, Actor Specialist)
    {
        public Guid TenantId => S.TenantId;
    }

    private async Task<Env> NewAsync()
    {
        var h = ChatHarness.Create(_db);
        var s = await h.SeedAsync();
        await using var ctx = _db.CreateContext(s.TenantId);
        User U(string role, string name) => new()
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@t.test", FullName = name, PasswordHash = "x", Role = role,
            SpecialistId = role == Roles.Specialist ? s.SpecialistId : null,
        };
        var admin = U(Roles.Admin, "Олена Адмін"); var owner = U(Roles.Owner, "Іван Власник"); var spec = U(Roles.Specialist, "Марина");
        ctx.AddRange(admin, owner, spec);
        await ctx.SaveChangesAsync();
        return new Env(h, s, new Actor(admin.Id, s.TenantId, Roles.Admin, null), new Actor(owner.Id, s.TenantId, Roles.Owner, null),
            new Actor(spec.Id, s.TenantId, Roles.Specialist, s.SpecialistId));
    }

    private static async Task<T> Svc<T>(Env e, Guid tenantId, Func<InboxService, Task<T>> f)
    {
        using var scope = e.H.Sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        return await f(scope.ServiceProvider.GetRequiredService<InboxService>());
    }

    private static Task<T> Svc<T>(Env e, Func<InboxService, Task<T>> f) => Svc(e, e.TenantId, f);

    private async Task<Guid> ConvAsync(Env e, string chat = Chat, string text = "Доброго дня", DateTimeOffset? at = null) =>
        (await e.H.IngestAsync(e.S, text, chat: chat, at: at)).Conversation!.Value;

    private Task<(Guid? Conversation, int Fresh)> Ingest(Env e, string text) => e.H.IngestAsync(e.S, text, chat: Chat);

    private async Task SqlAsync(Env e, FormattableString sql)
    {
        await using var ctx = _db.CreateContext(e.TenantId);
        await ctx.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private async Task<Conversation> LoadConvAsync(Env e, Guid id)
    {
        await using var ctx = _db.CreateContext(e.TenantId);
        return await ctx.Conversations.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private async Task<List<Message>> MsgsAsync(Env e, Guid conv)
    {
        await using var ctx = _db.CreateContext(e.TenantId);
        return await ctx.Messages.AsNoTracking().Where(m => m.ConversationId == conv).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync();
    }

    private async Task<Message> AddMessageAsync(Env e, Guid conv, MessageDirection dir, MessageSenderType sender, string body, string status,
        string? lastError = null, DateTimeOffset? sentAt = null)
    {
        await using var ctx = _db.CreateContext(e.TenantId);
        var m = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, Direction = dir, SenderType = sender, Body = body, Status = status, LastError = lastError,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
        };
        ctx.Add(m);
        await ctx.SaveChangesAsync();
        return m;
    }

    private static string Key() => "key-" + Guid.NewGuid().ToString("N")[..16];

    // ------------------------------------------------------------------ відповідь: стан і текст

    [SkippableFact]
    public async Task reply_sends_literal_plain_text_and_applies_the_adr_012_state_changes()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var second = (await Ingest(e, "Ще питання")).Conversation!.Value;
        Assert.Equal(conv, second);

        Guid apptId, proposalId;
        await using (var ctx = _db.CreateContext(e.TenantId))
        {
            var client = new Client { FullName = "Анна" };
            ctx.Add(client);
            await ctx.SaveChangesAsync();
            var appt = new Appointment
            {
                LocationId = e.S.LocationId, SpecialistId = e.S.SpecialistId, ServiceId = e.S.ServiceId, ClientId = client.Id,
                StartsAt = e.S.Start.ToUniversalTime(), DurationMinutes = 60, Status = AppointmentStatus.Confirmed, Source = AppointmentSource.Admin,
                PriceOriginal = 500m, PriceFinal = 500m,
            };
            ctx.Add(appt);
            await ctx.SaveChangesAsync();
            var proposal = new ChatBookingProposal
            {
                ConversationId = conv, LocationId = e.S.LocationId, SpecialistId = e.S.SpecialistId, ServiceId = e.S.ServiceId,
                StartsAt = e.S.Start.ToUniversalTime(), DurationMinutes = 60, PriceOriginal = 500m, PriceFinal = 500m, Status = "shown",
                ConfirmationTokenHash = new string('a', 64), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            };
            ctx.Add(proposal);
            await ctx.SaveChangesAsync();
            apptId = appt.Id; proposalId = proposal.Id;
        }
        await SqlAsync(e, $@"UPDATE beauty_conversations SET ai_state = 'handoff', handoff_reason = 'complaint', ai_paused_until = now() + interval '12 hours',
              awaiting_staff_since = now() - interval '1 hour', pending_prompt = 'reminder_choice', pending_prompt_appointment_id = {apptId},
              pending_prompt_expires_at = now() + interval '1 hour' WHERE id = {conv}");
        await SqlAsync(e, $"UPDATE beauty_messages SET status = 'held' WHERE conversation_id = {conv} AND direction = 'inbound'");

        const string text = "  <b>Добрий день</b> *x* _y_ [l](http://u)  ";
        var r = await Svc(e, i => i.ReplyAsync(e.Admin, conv, "reply-key-0001", text));

        Assert.True(r.IsOk, r.Error?.Code);
        Assert.Equal("sent", r.Value!.Message.Status);
        var sent = Assert.Single(e.H.Bot.Messages);
        Assert.Equal(("<b>Добрий день</b> *x* _y_ [l](http://u)", Chat, ChatHarness.BotToken), (sent.Text, sent.ChatId, sent.Token)); // буквально, trim
        Assert.DoesNotContain("parse_mode", e.H.Bot.UrlsText);

        var c = await LoadConvAsync(e, conv);
        Assert.Equal(("handoff", "complaint"), (c.AiState, c.HandoffReason)); // причина існуючого handoff не затирається
        Assert.Null(c.AiPausedUntil);                                          // безстроково
        Assert.Null(c.AwaitingStaffSince);
        Assert.NotNull(c.StaffRepliedAt);
        Assert.NotNull(c.StaffLastReadAt);
        Assert.Equal((null, null, null), (c.PendingPrompt, c.PendingPromptAppointmentId, c.PendingPromptExpiresAt));
        Assert.Equal(c.StaffRepliedAt, c.StaffLastReadAt);

        var msgs = await MsgsAsync(e, conv);
        Assert.All(msgs.Where(m => m.Direction == MessageDirection.Inbound), m => Assert.Equal("processed", m.Status)); // held -> processed
        var outMsg = Assert.Single(msgs, m => m.Direction == MessageDirection.Outbound);
        Assert.Equal((MessageSenderType.Staff, e.Admin.UserId, "sent"), (outMsg.SenderType, outMsg.SenderUserId, outMsg.Status));
        Assert.StartsWith($"staff-reply:{conv}:", outMsg.IdempotencyKey);
        Assert.DoesNotContain("reply-key-0001", outMsg.IdempotencyKey); // у ключі лише sha256
        await using var ctx2 = _db.CreateContext(e.TenantId);
        Assert.Equal("superseded", (await ctx2.ChatBookingProposals.AsNoTracking().SingleAsync(p => p.Id == proposalId)).Status);
    }

    [SkippableFact]
    public async Task reply_to_an_active_conversation_switches_it_to_handoff_staff_reply_without_a_timer()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        Assert.Equal("active", (await LoadConvAsync(e, conv)).AiState);
        Assert.True((await Svc(e, i => i.ReplyAsync(e.Owner, conv, Key(), "Вітаю"))).IsOk);
        var c = await LoadConvAsync(e, conv);
        Assert.Equal(("handoff", "staff_reply", null), (c.AiState, c.HandoffReason, c.AiPausedUntil));
        Assert.NotNull(c.HandoffAt);
        Assert.Contains(await MsgsAsync(e, conv), m => m.SenderType == MessageSenderType.Staff && m.SenderUserId == e.Owner.UserId);
    }

    [SkippableFact]
    public async Task client_message_after_a_manager_reply_is_not_answered_by_ai_until_resume()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        Assert.True((await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), "Підкажіть, будь ласка"))).IsOk);
        // ai_paused_until = NULL при handoff -> конвеєр (ConversationTurnProcessor) не повертає ШІ сам: пауза безстрокова
        await Ingest(e, "дякую, а скільки коштує?");
        await e.H.TurnAsync(e.TenantId, conv);
        Assert.Empty(e.H.Ai.Calls);
        Assert.Single(e.H.Bot.Messages); // лише відповідь менеджера
        Assert.Equal("handoff", (await LoadConvAsync(e, conv)).AiState);
    }

    // ------------------------------------------------------------------ ідемпотентність

    [SkippableFact]
    public async Task same_key_and_text_returns_the_same_message_and_sends_once_other_text_or_user_is_409()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var key = Key();
        var first = await Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "Один"));
        var again = await Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "  Один "));
        Assert.True(first.IsOk && again.IsOk);
        Assert.Equal(first.Value!.Message.Id, again.Value!.Message.Id);
        Assert.Equal("sent", again.Value.Message.Status);
        Assert.Single(e.H.Bot.Messages);
        Assert.Single((await MsgsAsync(e, conv)).Where(m => m.Direction == MessageDirection.Outbound));

        var other = await Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "Інший текст"));
        Assert.Equal("idempotency_conflict", other.Error!.Code);
        Assert.Equal(Application.Features.BeautyCommon.ErrorKind.Conflict, other.Error.Kind);
        var otherUser = await Svc(e, i => i.ReplyAsync(e.Owner, conv, key, "Один"));
        Assert.Equal("idempotency_conflict", otherUser.Error!.Code);
        Assert.Single(e.H.Bot.Messages);

        // той самий ключ в іншій розмові - окреме повідомлення (ключ прив'язано до розмови)
        var conv2 = await ConvAsync(e, "5550999999");
        var inOther = await Svc(e, i => i.ReplyAsync(e.Admin, conv2, key, "Один"));
        Assert.True(inOther.IsOk);
        Assert.NotEqual(first.Value.Message.Id, inOther.Value!.Message.Id);
        Assert.Equal(2, e.H.Bot.Messages.Count);
    }

    [SkippableFact]
    public async Task concurrent_requests_with_one_key_produce_one_message_and_one_send()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var key = Key();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "Паралельно"))));
        Assert.All(results, r => Assert.True(r.IsOk, r.Error?.Code));
        Assert.Single(results.Select(r => r.Value!.Message.Id).Distinct());
        Assert.Single(e.H.Bot.Messages);
        Assert.Single((await MsgsAsync(e, conv)).Where(m => m.Direction == MessageDirection.Outbound));
    }

    // ------------------------------------------------------------------ валідація тексту

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("a\tb")]
    [InlineData("a\u0000b")]
    [InlineData("a\rb")]
    [InlineData("a\u001bb")]
    public async Task invalid_text_is_422_and_nothing_is_stored_or_sent(string? text)
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var r = await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), text));
        Assert.Equal("invalid_text", r.Error!.Code);
        Assert.Equal(Application.Features.BeautyCommon.ErrorKind.Validation, r.Error.Kind);
        Assert.Empty(e.H.Bot.Messages);
        Assert.Empty((await MsgsAsync(e, conv)).Where(m => m.Direction == MessageDirection.Outbound));
    }

    [Fact]
    public void lone_surrogates_and_control_characters_are_rejected_by_the_normalizer()
    {
        Assert.Null(InboxService.NormalizeText("ab" + char.ConvertFromUtf32(0x1F600)[0] + "cd"));
        Assert.Null(InboxService.NormalizeText("x" + char.ConvertFromUtf32(0x1F600)[1]));
        Assert.Equal("a😀b", InboxService.NormalizeText(" a😀b "));
        Assert.Equal("a" + (char)10 + "b", InboxService.NormalizeText("a" + (char)13 + (char)10 + "b"));
        Assert.Null(InboxService.NormalizeText("a" + (char)0x85 + "b"));
    }

    [SkippableFact]
    public async Task text_length_is_counted_in_code_points_3500_ok_3501_invalid_and_newlines_are_kept()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var emoji = string.Concat(Enumerable.Repeat("😀", 3500)); // 3500 кодових точок = 7000 UTF-16
        // TASK-759 (L8): 3500 кодових точок у межах, але понад 4096 одиниць UTF-16 (Telegram) -> invalid_length (раніше було IsOk).
        Assert.Equal("invalid_length", (await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), emoji))).Error!.Code);
        Assert.True((await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), string.Concat(Enumerable.Repeat("😀", 2048))))).IsOk); // рівно 4096 UTF-16
        Assert.Equal("invalid_text", (await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), emoji + "x"))).Error!.Code);
        Assert.Equal("invalid_text", (await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), new string('a', 3501)))).Error!.Code);
        Assert.True((await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), "рядок 1\r\nрядок 2\nрядок 3"))).IsOk);
        Assert.Equal("рядок 1\nрядок 2\nрядок 3", e.H.Bot.Messages[^1].Text);
    }

    // ------------------------------------------------------------------ канал

    [SkippableFact]
    public async Task instagram_and_inactive_channels_are_409_without_any_send()
    {
        var e = await NewAsync();
        Guid igConv, inactiveConv;
        await using (var ctx = _db.CreateContext(e.TenantId))
        {
            var ig = new Channel { Type = ChannelType.Instagram, Name = "ig", LocationId = e.S.LocationId, IsActive = true };
            var off = new Channel { Type = ChannelType.Telegram, Name = "tg-off", LocationId = e.S.LocationId, IsActive = false };
            ctx.AddRange(ig, off);
            await ctx.SaveChangesAsync();
            var c1 = new Conversation { ChannelId = ig.Id, ExternalChatId = "ig-1", LastMessageAt = DateTimeOffset.UtcNow };
            var c2 = new Conversation { ChannelId = off.Id, ExternalChatId = "tg-off-1", LastMessageAt = DateTimeOffset.UtcNow };
            ctx.AddRange(c1, c2);
            await ctx.SaveChangesAsync();
            igConv = c1.Id; inactiveConv = c2.Id;
        }
        var r1 = await Svc(e, i => i.ReplyAsync(e.Admin, igConv, Key(), "привіт"));
        var r2 = await Svc(e, i => i.ReplyAsync(e.Admin, inactiveConv, Key(), "привіт"));
        Assert.Equal(("channel_unsupported", Application.Features.BeautyCommon.ErrorKind.Conflict), (r1.Error!.Code, r1.Error.Kind));
        Assert.Equal(("channel_inactive", Application.Features.BeautyCommon.ErrorKind.Conflict), (r2.Error!.Code, r2.Error.Kind));
        Assert.Empty(e.H.Bot.Messages);
        Assert.Empty(await MsgsAsync(e, igConv));
        Assert.Empty(await MsgsAsync(e, inactiveConv));

        var d1 = (await Svc(e, i => i.GetAsync(e.Admin, igConv, null, null, default))).Value!.Conversation;
        var d2 = (await Svc(e, i => i.GetAsync(e.Admin, inactiveConv, null, null, default))).Value!.Conversation;
        Assert.Equal((false, "channel_unsupported"), (d1.CanReply, d1.ReplyBlockedReason));
        Assert.Equal((false, "channel_inactive"), (d2.CanReply, d2.ReplyBlockedReason));
    }

    [SkippableFact]
    public async Task channel_deactivated_after_the_fact_fails_the_message_with_a_code_and_never_calls_telegram()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var pending = await AddMessageAsync(e, conv, MessageDirection.Outbound, MessageSenderType.Staff, "x", "sending");
        await SqlAsync(e, $"UPDATE beauty_channels SET is_active = false WHERE id = {e.S.ChannelId}");
        var outcome = await Svc(e, async _ =>
        {
            using var scope = e.H.Sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(e.TenantId);
            return await scope.ServiceProvider.GetRequiredService<OutboundDispatcher>().DispatchAsync(e.TenantId, pending.Id, default);
        });
        Assert.Equal(OutboxResult.Failed, outcome);
        var m = (await MsgsAsync(e, conv)).Single(x => x.Id == pending.Id);
        Assert.Equal(("failed", "channel_inactive"), (m.Status, m.LastError));
        Assert.Empty(e.H.Bot.Messages);
    }

    // ------------------------------------------------------------------ класифікація помилок Telegram

    [SkippableTheory]
    [InlineData(HttpStatusCode.Forbidden, "{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot was blocked by the user\"}", "bot_blocked")]
    [InlineData(HttpStatusCode.BadRequest, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}", "chat_not_found")]
    public async Task telegram_permanent_errors_are_stored_as_codes_without_retry(HttpStatusCode status, string body, string code)
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        e.H.Bot.FailNext = 1; e.H.Bot.FailStatus = status; e.H.Bot.FailBody = body;
        var r = await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), "СЕКРЕТНИЙ-ТЕКСТ-ВІДПОВІДІ"));
        Assert.True(r.IsOk);
        Assert.Equal("failed", r.Value!.Message.Status);

        var m = (await MsgsAsync(e, conv)).Single(x => x.Direction == MessageDirection.Outbound);
        Assert.Equal(("failed", code, 1), (m.Status, m.LastError, m.Attempts));

        var detail = (await Svc(e, i => i.GetAsync(e.Admin, conv, null, null, default))).Value!;
        Assert.Equal(code, detail.Messages.Single(x => x.Id == m.Id).FailureCode);

        e.H.Clock.Advance(TimeSpan.FromMinutes(10));
        await e.H.Worker.SweepOnceAsync(default); // failed не повторюється
        Assert.Equal(1, e.H.Bot.UrlsText.Split('\n').Length);
        Assert.Empty(e.H.Bot.Messages);
    }

    [SkippableFact]
    public async Task transient_telegram_error_leaves_the_reply_pending_and_the_sweeper_delivers_staff_replies()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        e.H.Bot.FailNext = 1; // 500
        var r = await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), "Повторимо"));
        Assert.Equal("pending", r.Value!.Message.Status);
        Assert.Empty(e.H.Bot.Messages);

        await e.H.Worker.SweepOnceAsync(default); // ще не минуло PendingRetryAfter
        Assert.Empty(e.H.Bot.Messages);
        e.H.Clock.Advance(TimeSpan.FromMinutes(3));
        await e.H.Worker.SweepOnceAsync(default);
        var sent = Assert.Single(e.H.Bot.Messages);
        Assert.Equal(("Повторимо", Chat), (sent.Text, sent.ChatId));
        Assert.Equal("sent", (await MsgsAsync(e, conv)).Single(m => m.Direction == MessageDirection.Outbound).Status);
    }

    [Fact]
    public async Task telegram_adapter_classifies_403_400_and_keeps_other_failures_unclassified_without_leaking_the_body()
    {
        static TelegramAdapter A(HttpStatusCode c, string body) =>
            new(new HttpClient(new BodyHandler(c, body)), new ChCreds("999:SECRETTOKEN"));
        static OutboundMessage Msg() => new(Guid.NewGuid(), "telegram", "42", "СЕКРЕТНИЙ-ТЕКСТ");

        var blocked = await Assert.ThrowsAsync<PermanentChannelException>(() => A(HttpStatusCode.Forbidden, "{\"description\":\"Forbidden: bot was blocked by the user\"}").SendAsync(Msg(), default));
        Assert.Equal("bot_blocked", blocked.Code);
        var gone = await Assert.ThrowsAsync<PermanentChannelException>(() => A(HttpStatusCode.BadRequest, "{\"description\":\"Bad Request: CHAT NOT FOUND\"}").SendAsync(Msg(), default));
        Assert.Equal("chat_not_found", gone.Code);
        var other400 = await Assert.ThrowsAsync<PermanentChannelException>(() => A(HttpStatusCode.BadRequest, "{\"description\":\"Bad Request: message is too long\"}").SendAsync(Msg(), default));
        Assert.Null(other400.Code);
        var unauthorized = await Assert.ThrowsAsync<PermanentChannelException>(() => A(HttpStatusCode.Unauthorized, "{}").SendAsync(Msg(), default));
        Assert.Null(unauthorized.Code);
        await Assert.ThrowsAsync<TransientChannelException>(() => A(HttpStatusCode.TooManyRequests, "{}").SendAsync(Msg(), default));
        await Assert.ThrowsAsync<TransientChannelException>(() => A(HttpStatusCode.BadGateway, "{}").SendAsync(Msg(), default));
        foreach (var ex in new[] { blocked, gone, other400 })
        {
            Assert.DoesNotContain("SECRETTOKEN", ex.ToString());
            Assert.DoesNotContain("СЕКРЕТНИЙ", ex.ToString());
            Assert.DoesNotContain("blocked by the user", ex.ToString());
        }
        var noToken = new TelegramAdapter(new HttpClient(new BodyHandler(HttpStatusCode.OK, "{}")), new ChCreds(token: null));
        Assert.Equal("token_missing", (await Assert.ThrowsAsync<PermanentChannelException>(() => noToken.SendAsync(Msg(), default))).Code);
    }

    private sealed class BodyHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    // ------------------------------------------------------------------ ліміти

    [Fact]
    public void throttle_limits_per_conversation_user_and_tenant_and_does_not_spend_on_denial()
    {
        var clock = new TestClock();
        var t = new InboxThrottle(new InboxOptions { ReplyPerUserPerMinute = 3, ReplyPerConversationPerMinute = 2, ReplyPerTenantPerHour = 5 }, clock);
        Guid tenant = Guid.NewGuid(), u1 = Guid.NewGuid(), u2 = Guid.NewGuid(), c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid();

        Assert.True(t.TryAcquireReply(tenant, u1, c1, out _));
        Assert.True(t.TryAcquireReply(tenant, u2, c1, out _));
        Assert.False(t.TryAcquireReply(tenant, u1, c1, out var wait)); // розмова: 2/хв
        Assert.InRange(wait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.True(t.TryAcquireReply(tenant, u1, c2, out _));          // u1: 2-га; відмова вище нічого не списала
        Assert.True(t.TryAcquireReply(tenant, u1, c3, out _));          // u1: 3-тя
        Assert.False(t.TryAcquireReply(tenant, u1, Guid.NewGuid(), out _)); // користувач: 3/хв
        Assert.True(t.TryAcquireReply(tenant, u2, Guid.NewGuid(), out _));  // tenant: 5-та за годину
        Assert.False(t.TryAcquireReply(tenant, u2, Guid.NewGuid(), out _)); // tenant: 5/год
        Assert.True(t.TryAcquireReply(Guid.NewGuid(), u2, Guid.NewGuid(), out _)); // інший tenant не зачеплений

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(t.TryAcquireReply(tenant, u2, Guid.NewGuid(), out _)); // годинне вікно tenant ще діє
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(t.TryAcquireReply(tenant, u1, c1, out _));
    }

    [SkippableFact]
    public async Task eleventh_reply_in_a_minute_is_429_with_retry_after_and_is_not_stored_or_sent()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        for (var n = 0; n < 10; n++) Assert.True((await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), $"м{n}"))).IsOk);
        var denied = await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), "одинадцяте"));
        Assert.Equal("rate_limited", denied.Error!.Code);
        Assert.IsType<int>(denied.Error.Conflicts);
        Assert.Equal(10, e.H.Bot.Messages.Count);
        Assert.Equal(10, (await MsgsAsync(e, conv)).Count(m => m.Direction == MessageDirection.Outbound));

        // повтор уже виконаного запиту ліміт не споживає і проходить навіть у вичерпаному вікні
        var key = Key();
        e.H.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "після вікна"))).IsOk);
        Assert.True((await Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "після вікна"))).IsOk);
        Assert.Equal(11, e.H.Bot.Messages.Count);
    }

    // ------------------------------------------------------------------ ролі, IDOR

    [SkippableFact]
    public async Task specialist_is_forbidden_everywhere_and_nothing_is_read_or_changed()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var denied = new[]
        {
            (await Svc(e, i => i.ListAsync(e.Specialist, null, null, null, null, null, default))).Error,
            (await Svc(e, i => i.GetAsync(e.Specialist, conv, null, null, default))).Error,
            (await Svc(e, i => i.ReplyAsync(e.Specialist, conv, Key(), "hi"))).Error,
            (await Svc(e, i => i.ResumeAiAsync(e.Specialist, conv, default))).Error,
            (await Svc(e, i => i.ResolveAsync(e.Specialist, conv, default))).Error,
            (await Svc(e, i => i.MarkReadAsync(e.Specialist, conv, null, default))).Error,
        };
        Assert.All(denied, err =>
        {
            Assert.Equal("forbidden_role", err!.Code);
            Assert.Equal(Application.Features.BeautyCommon.ErrorKind.Forbidden, err.Kind);
        });
        Assert.Empty(e.H.Bot.Messages);
        Assert.Null((await LoadConvAsync(e, conv)).StaffLastReadAt);
    }

    [SkippableFact]
    public async Task foreign_tenant_conversation_is_404_for_every_operation_and_absent_from_lists()
    {
        var a = await NewAsync();
        var b = await NewAsync();
        var convA = await ConvAsync(a);
        await SqlAsync(a, $"UPDATE beauty_conversations SET awaiting_staff_since = now() WHERE id = {convA}");
        var msgA = (await MsgsAsync(a, convA)).First();

        // actor і scope тенанта B звертаються до id розмови тенанта A
        var results = new[]
        {
            (await Svc(a, b.TenantId, i => i.GetAsync(b.Admin, convA, null, null, default))).Error,
            (await Svc(a, b.TenantId, i => i.ReplyAsync(b.Admin, convA, Key(), "чужа"))).Error,
            (await Svc(a, b.TenantId, i => i.ResumeAiAsync(b.Admin, convA, default))).Error,
            (await Svc(a, b.TenantId, i => i.MarkReadAsync(b.Admin, convA, null, default))).Error,
            (await Svc(a, b.TenantId, i => i.MarkReadAsync(b.Admin, convA, msgA.Id, default))).Error,
        };
        Assert.All(results, err => Assert.Equal("conversation_not_found", err!.Code));
        Assert.Empty(a.H.Bot.Messages);
        var listB = (await Svc(a, b.TenantId, i => i.ListAsync(b.Admin, "all", null, null, null, null, default))).Value!;
        Assert.Empty(listB.Items);
        Assert.Equal(0, listB.Meta.NeedsReplyCount);

        // actor B під scope A (підміна tenant у токені) - TenantMismatch, а не дані
        await Assert.ThrowsAsync<TenantMismatchException>(() => Svc(a, a.TenantId, i => i.GetAsync(b.Admin, convA, null, null, default)));
        Assert.Null((await LoadConvAsync(a, convA)).StaffRepliedAt);
    }

    // ------------------------------------------------------------------ resume-ai, read

    [SkippableFact]
    public async Task resume_ai_activates_clears_pause_marks_held_processed_journals_once_and_is_idempotent()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        await Ingest(e, "ще повідомлення");
        await SqlAsync(e, $@"UPDATE beauty_conversations SET ai_state = 'handoff', handoff_reason = 'complaint', ai_paused_until = now() + interval '5 hours',
              awaiting_staff_since = now() WHERE id = {conv}");
        await SqlAsync(e, $"UPDATE beauty_messages SET status = 'held' WHERE conversation_id = {conv}");

        var r = await Svc(e, i => i.ResumeAiAsync(e.Admin, conv, default));
        Assert.True(r.IsOk);
        var c = await LoadConvAsync(e, conv);
        Assert.Equal(("active", null, null, null), (c.AiState, c.HandoffReason, c.AiPausedUntil, c.AwaitingStaffSince));
        Assert.All(await MsgsAsync(e, conv), m => Assert.Equal("processed", m.Status));

        async Task<List<AiAction>> Journal()
        {
            await using var ctx = _db.CreateContext(e.TenantId);
            return await ctx.AiActions.AsNoTracking().Where(a => a.ToolName == "resume_ai").ToListAsync();
        }
        var rows = await Journal();
        var row = Assert.Single(rows);
        Assert.Equal(conv, row.ConversationId);
        Assert.Equal(e.Admin.UserId, JsonDocument.Parse(row.Payload).RootElement.GetProperty("userId").GetGuid());

        Assert.True((await Svc(e, i => i.ResumeAiAsync(e.Admin, conv, default))).IsOk); // ідемпотентно
        Assert.Single(await Journal());

        // ШІ відповідає на нове повідомлення (старі processed не чіпаються)
        e.H.Ai.Enqueue(ChatHarness.Text("Вітаю! Чим допомогти?"));
        await Ingest(e, "Скільки триває стрижка?");
        await e.H.TurnAsync(e.TenantId, conv);
        Assert.Single(e.H.Ai.Calls);
        Assert.Single(e.H.Bot.Messages);
    }

    [SkippableFact]
    public async Task read_moves_staff_last_read_forward_only_and_validates_the_message()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var m1 = (await MsgsAsync(e, conv)).Single();
        Assert.True((await Svc(e, i => i.MarkReadAsync(e.Admin, conv, m1.Id, default))).IsOk);
        var first = (await LoadConvAsync(e, conv)).StaffLastReadAt;
        Assert.Equal(m1.CreatedAt.UtcTicks, first!.Value.UtcTicks);

        Assert.True((await Svc(e, i => i.MarkReadAsync(e.Admin, conv, null, default))).IsOk); // now
        var second = (await LoadConvAsync(e, conv)).StaffLastReadAt;
        Assert.True(second > first);
        Assert.True((await Svc(e, i => i.MarkReadAsync(e.Admin, conv, m1.Id, default))).IsOk); // назад не рухається
        Assert.Equal(second, (await LoadConvAsync(e, conv)).StaffLastReadAt);

        var missing = await Svc(e, i => i.MarkReadAsync(e.Admin, conv, Guid.NewGuid(), default));
        Assert.Equal("message_not_found", missing.Error!.Code);
        var otherConv = await ConvAsync(e, "5550888888");
        var foreignMsg = (await MsgsAsync(e, otherConv)).Single();
        Assert.Equal("message_not_found", (await Svc(e, i => i.MarkReadAsync(e.Admin, conv, foreignMsg.Id, default))).Error!.Code);
    }

    // ------------------------------------------------------------------ список

    [SkippableFact]
    public async Task list_filters_orders_paginates_and_hides_external_chat_id()
    {
        var e = await NewAsync();
        var now = DateTimeOffset.UtcNow;
        var a = await ConvAsync(e, "5551110001", "Найдавніша", now.AddHours(-3));
        var b = await ConvAsync(e, "5551110002", "Середня", now.AddHours(-2));
        var c = await ConvAsync(e, "5551110003", "Найновіша", now.AddHours(-1));
        await SqlAsync(e, $"UPDATE beauty_conversations SET awaiting_staff_since = now() - interval '5 hours' WHERE id = {b}");
        await SqlAsync(e, $"UPDATE beauty_conversations SET awaiting_staff_since = now() - interval '1 hour' WHERE id = {c}");

        var needs = (await Svc(e, i => i.ListAsync(e.Admin, "needs_reply", null, null, null, null, default))).Value!;
        Assert.Equal([b, c], needs.Items.Select(x => x.Id)); // найдовше чекає - першим
        Assert.Equal(2, needs.Meta.NeedsReplyCount);
        var all = (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default))).Value!;
        Assert.Equal([c, b, a], all.Items.Select(x => x.Id));
        Assert.Equal([b, c], (await Svc(e, i => i.ListAsync(e.Admin, null, null, null, null, null, default))).Value!.Items.Select(x => x.Id)); // за замовч. needs_reply

        // пагінація курсором
        var p1 = (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, 1, default))).Value!;
        Assert.Equal([c], p1.Items.Select(x => x.Id));
        Assert.NotNull(p1.NextCursor);
        var p2 = (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, p1.NextCursor, 1, default))).Value!;
        var p3 = (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, p2.NextCursor, 1, default))).Value!;
        Assert.Equal([b], p2.Items.Select(x => x.Id));
        Assert.Equal([a], p3.Items.Select(x => x.Id));
        Assert.Null(p3.NextCursor);
        var n1 = (await Svc(e, i => i.ListAsync(e.Admin, "needs_reply", null, null, null, 1, default))).Value!;
        var n2 = (await Svc(e, i => i.ListAsync(e.Admin, "needs_reply", null, null, n1.NextCursor, 1, default))).Value!;
        Assert.Equal([b, c], n1.Items.Concat(n2.Items).Select(x => x.Id));
        Assert.Null(n2.NextCursor);

        // курсор від іншого фільтра / сміття / фільтри
        Assert.Equal("invalid_cursor", (await Svc(e, i => i.ListAsync(e.Admin, "needs_reply", null, null, p1.NextCursor, 1, default))).Error!.Code);
        Assert.Equal("invalid_cursor", (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, "!!not-a-cursor", 1, default))).Error!.Code);
        Assert.Equal("invalid_filter", (await Svc(e, i => i.ListAsync(e.Admin, "everything", null, null, null, null, default))).Error!.Code);
        Assert.Equal("invalid_filter", (await Svc(e, i => i.ListAsync(e.Admin, "all", null, "pigeon", null, null, default))).Error!.Code);
        Assert.Equal(3, (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, 1000, default))).Value!.Items.Count); // take ≤ 100

        // заклад і тип каналу
        Guid otherLoc;
        await using (var ctx = _db.CreateContext(e.TenantId))
        {
            var loc = new Location { Name = "Салон Лівий берег", Timezone = "Europe/Kyiv" };
            ctx.Add(loc);
            await ctx.SaveChangesAsync();
            var ch = new Channel { Type = ChannelType.Instagram, Name = "ig", LocationId = loc.Id, IsActive = true };
            ctx.Add(ch);
            await ctx.SaveChangesAsync();
            ctx.Add(new Conversation { ChannelId = ch.Id, ExternalChatId = "ig-user-1", LastMessageAt = now });
            await ctx.SaveChangesAsync();
            otherLoc = loc.Id;
        }
        var byLoc = (await Svc(e, i => i.ListAsync(e.Admin, "all", otherLoc, null, null, null, default))).Value!.Items.Single();
        Assert.Equal(("instagram", "Клієнт Instagram"), (byLoc.Channel.Type, byLoc.Title));
        Assert.Equal(otherLoc, byLoc.Location!.Id);
        Assert.Equal(3, (await Svc(e, i => i.ListAsync(e.Admin, "all", e.S.LocationId, null, null, null, default))).Value!.Items.Count);
        Assert.Single((await Svc(e, i => i.ListAsync(e.Admin, "all", null, "instagram", null, null, default))).Value!.Items);
        Assert.Equal(3, (await Svc(e, i => i.ListAsync(e.Admin, "all", null, "telegram", null, null, default))).Value!.Items.Count);

        var json = JsonSerializer.Serialize(all);
        foreach (var leaked in new[] { "5551110001", "5551110002", "5551110003", "externalChatId", "ig-user-1" }) Assert.DoesNotContain(leaked, json);
    }

    [SkippableFact]
    public async Task list_item_shows_title_preview_unread_last_sender_and_skips_drafts()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e, Chat, "перше");
        Assert.Equal("Клієнт Telegram", (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default))).Value!.Items.Single().Title);

        await using (var ctx = _db.CreateContext(e.TenantId))
        {
            var client = new Client { FullName = "Анна Коваль" };
            ctx.Add(client);
            await ctx.SaveChangesAsync();
            await ctx.Conversations.Where(c => c.Id == conv).ExecuteUpdateAsync(s => s.SetProperty(c => c.ClientId, client.Id));
        }
        var long1 = new string('ж', 200) + "\nхвіст";
        await AddMessageAsync(e, conv, MessageDirection.Inbound, MessageSenderType.Client, long1, "held", sentAt: DateTimeOffset.UtcNow.AddSeconds(1));
        await AddMessageAsync(e, conv, MessageDirection.Outbound, MessageSenderType.Ai, "ЧЕРНЕТКА-ШІ", "draft", sentAt: DateTimeOffset.UtcNow.AddSeconds(2));

        var item = (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default))).Value!.Items.Single();
        Assert.Equal("Анна Коваль", item.Title);
        Assert.Equal("client", item.LastMessageSenderType);
        Assert.Equal(new string('ж', 120), item.LastMessagePreview); // 120 кодових точок, чернетка не в прев'ю
        Assert.Equal(2, item.UnreadCount);

        Assert.True((await Svc(e, i => i.MarkReadAsync(e.Admin, conv, null, default))).IsOk);
        Assert.Equal(0, (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default))).Value!.Items.Single().UnreadCount);
        await Ingest(e, "нове після прочитання");
        Assert.Equal(1, (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default))).Value!.Items.Single().UnreadCount);
    }

    // ------------------------------------------------------------------ деталі

    [SkippableFact]
    public async Task detail_returns_messages_with_kind_status_held_sender_failure_codes_proposal_and_upcoming_appointments()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e, Chat, "Клієнт пише");
        var t0 = DateTimeOffset.UtcNow;
        await AddMessageAsync(e, conv, MessageDirection.Outbound, MessageSenderType.Ai, "чернетка", "draft", sentAt: t0.AddSeconds(6));
        var ok = await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), "Відповідь менеджера"));
        Assert.True(ok.IsOk);
        // повідомлення клієнта ПІСЛЯ відповіді менеджера (пауза триває) лишається held
        await AddMessageAsync(e, conv, MessageDirection.Inbound, MessageSenderType.Client, "під час паузи", "held", sentAt: t0.AddSeconds(5));
        await AddMessageAsync(e, conv, MessageDirection.Outbound, MessageSenderType.Staff, "x1", "failed", "bot_blocked", t0.AddSeconds(30));
        await AddMessageAsync(e, conv, MessageDirection.Outbound, MessageSenderType.Staff, "x2", "failed", "Telegram sendMessage failed with HTTP 500 sk-ant-SECRET", t0.AddSeconds(31));

        Guid apptId;
        await using (var ctx = _db.CreateContext(e.TenantId))
        {
            var client = new Client { FullName = "Анна", Phone = "+380501112233" };
            ctx.Add(client);
            await ctx.SaveChangesAsync();
            var future = new Appointment
            {
                LocationId = e.S.LocationId, SpecialistId = e.S.SpecialistId, ServiceId = e.S.ServiceId, ClientId = client.Id,
                StartsAt = DateTimeOffset.UtcNow.AddDays(2), DurationMinutes = 60, Status = AppointmentStatus.Confirmed, Source = AppointmentSource.Admin,
                PriceOriginal = 500m, PriceFinal = 500m, ReminderMethod = "telegram", ReminderOffsets = [120, 1440],
            };
            var past = new Appointment
            {
                LocationId = e.S.LocationId, SpecialistId = e.S.SpecialistId, ServiceId = e.S.ServiceId, ClientId = client.Id,
                StartsAt = DateTimeOffset.UtcNow.AddDays(-2), DurationMinutes = 60, Status = AppointmentStatus.Completed, Source = AppointmentSource.Admin,
                PriceOriginal = 500m, PriceFinal = 500m,
            };
            ctx.AddRange(future, past);
            await ctx.SaveChangesAsync();
            var summary = new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System,
                Body = "Підсумок: Стрижка, Марина", Status = "sent", SentAt = t0.AddSeconds(2),
            };
            ctx.Add(summary);
            await ctx.SaveChangesAsync();
            ctx.Add(new ChatBookingProposal
            {
                ConversationId = conv, ClientId = client.Id, LocationId = e.S.LocationId, SpecialistId = e.S.SpecialistId, ServiceId = e.S.ServiceId,
                StartsAt = e.S.Start.ToUniversalTime(), DurationMinutes = 60, PriceOriginal = 500m, PriceFinal = 500m, Status = "shown", SummaryMessageId = summary.Id,
                ConfirmationTokenHash = new string('b', 64), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            });
            await ctx.Conversations.Where(c => c.Id == conv).ExecuteUpdateAsync(s => s.SetProperty(c => c.ClientId, client.Id));
            await ctx.SaveChangesAsync();
            apptId = future.Id;
        }

        var d = (await Svc(e, i => i.GetAsync(e.Admin, conv, null, null, default))).Value!;
        Assert.True(d.Conversation.CanReply);
        Assert.Null(d.Conversation.ReplyBlockedReason);
        Assert.False(d.HasMore);
        var sentAt = d.Messages.Select(m => m.SentAt).ToList();
        Assert.Equal(sentAt.OrderBy(x => x), sentAt); // від старих до нових

        var held = d.Messages.Single(m => m.Body == "під час паузи");
        Assert.Equal(("inbound", "client", "text", "held"), (held.Direction, held.SenderType, held.Kind, held.Status));
        Assert.Equal("draft", d.Messages.Single(m => m.Body == "чернетка").Status);
        var staff = d.Messages.Single(m => m.Body == "Відповідь менеджера");
        Assert.Equal(("staff", e.Admin.UserId, "Олена Адмін"), (staff.SenderType, staff.SenderUser!.Id, staff.SenderUser.Name));
        Assert.Equal("bot_blocked", d.Messages.Single(m => m.Body == "x1").FailureCode);
        Assert.Equal("send_failed", d.Messages.Single(m => m.Body == "x2").FailureCode);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(d));
        Assert.Null(d.Messages.Single(m => m.Body == "Відповідь менеджера").FailureCode);

        Assert.Equal("shown", d.ActiveProposal!.Status);
        Assert.Equal("Підсумок: Стрижка, Марина", d.ActiveProposal.SummaryText);
        var up = Assert.Single(d.UpcomingAppointments);
        Assert.Equal((apptId, "Стрижка", "Салон Центр", "telegram"), (up.Id, up.ServiceName, up.LocationName, up.Reminders.Method));
        Assert.Equal<short>([120, 1440], up.Reminders.OffsetsMinutes);
        Assert.DoesNotContain("380501112233", JsonSerializer.Serialize(d)); // телефон клієнта в inbox не віддається
    }

    [SkippableFact]
    public async Task detail_paginates_backwards_with_before_and_reports_has_more()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e, Chat, "m0");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var n = 1; n <= 7; n++)
            await AddMessageAsync(e, conv, MessageDirection.Inbound, MessageSenderType.Client, $"m{n}", "processed", sentAt: t0.AddSeconds(n));
        // повідомлення з однаковим sent_at: порядок і курсор стабільні за id
        for (var n = 8; n <= 9; n++)
            await AddMessageAsync(e, conv, MessageDirection.Inbound, MessageSenderType.Client, $"m{n}", "processed", sentAt: t0.AddSeconds(20));

        var last = (await Svc(e, i => i.GetAsync(e.Admin, conv, null, 4, default))).Value!;
        Assert.True(last.HasMore);
        Assert.Equal(4, last.Messages.Count);
        var seen = last.Messages.Select(m => m.Id).ToList();
        var page = last;
        while (page.HasMore)
        {
            page = (await Svc(e, i => i.GetAsync(e.Admin, conv, page.Messages[0].Id, 4, default))).Value!;
            seen.InsertRange(0, page.Messages.Select(m => m.Id));
        }
        Assert.Equal(10, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(seen, (await Svc(e, i => i.GetAsync(e.Admin, conv, null, 100, default))).Value!.Messages.Select(m => m.Id));
        Assert.Equal("message_not_found", (await Svc(e, i => i.GetAsync(e.Admin, conv, Guid.NewGuid(), 4, default))).Error!.Code);
    }

    [SkippableFact]
    public async Task get_endpoints_are_rate_limited_per_user()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        for (var n = 0; n < 120; n++) Assert.True((await Svc(e, i => i.GetAsync(e.Admin, conv, null, 1, default))).IsOk);
        Assert.Equal("rate_limited", (await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default))).Error!.Code);
        Assert.True((await Svc(e, i => i.GetAsync(e.Owner, conv, null, 1, default))).IsOk); // інший користувач не зачеплений
        e.H.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default))).IsOk);
    }

    // ------------------------------------------------------------------ ПД у логах

    [SkippableFact]
    public async Task logs_carry_ids_and_codes_but_never_message_text_names_or_chat_ids()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e, Chat, "СЕКРЕТНЕ-ПОВІДОМЛЕННЯ-КЛІЄНТА");
        var key = "key-" + Guid.NewGuid().ToString("N");
        e.H.Bot.FailNext = 1; e.H.Bot.FailStatus = HttpStatusCode.Forbidden;
        await Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "СЕКРЕТНА-ВІДПОВІДЬ-МЕНЕДЖЕРА"));
        await Svc(e, i => i.ReplyAsync(e.Admin, conv, key, "інший текст СЕКРЕТНА-ВІДПОВІДЬ-МЕНЕДЖЕРА")); // conflict
        await Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), new string('a', 4000)));                    // invalid
        await Svc(e, i => i.ResumeAiAsync(e.Admin, conv, default));
        await Svc(e, i => i.ResolveAsync(e.Admin, conv, default));
        await Svc(e, i => i.GetAsync(e.Admin, conv, null, null, default));

        var logs = e.H.Logs.All;
        Assert.Contains("inbox_reply", logs);
        Assert.Contains("inbox_resume_ai", logs);
        Assert.Contains("inbox_resolve", logs);
        foreach (var secret in new[] { "СЕКРЕТНЕ-ПОВІДОМЛЕННЯ-КЛІЄНТА", "СЕКРЕТНА-ВІДПОВІДЬ-МЕНЕДЖЕРА", Chat, "Олена Адмін", "Anna", ChatHarness.BotToken, "BOTTOKENSECRETVALUE", key })
            Assert.DoesNotContain(secret, logs);
    }

    // ------------------------------------------------------------------ resolve (TASK-793)

    [SkippableFact]
    public async Task resolve_closes_without_reply_leaves_needs_reply_and_counter_returns_ai_and_is_idempotent()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        var other = await ConvAsync(e, "5550999999", "Інша розмова");
        await Ingest(e, "ще повідомлення");
        await SqlAsync(e, $@"UPDATE beauty_conversations SET ai_state = 'handoff', handoff_reason = 'complaint', handoff_at = now(),
              ai_paused_until = now() + interval '5 hours', awaiting_staff_since = now() WHERE id IN ({conv}, {other})");
        await SqlAsync(e, $"UPDATE beauty_messages SET status = 'held' WHERE conversation_id IN ({conv}, {other})");
        var before = (await MsgsAsync(e, conv)).Count;

        var list0 = await Svc(e, i => i.ListAsync(e.Admin, null, null, null, null, null, default));
        Assert.Equal(2, list0.Value!.Meta.NeedsReplyCount);

        Assert.True((await Svc(e, i => i.ResolveAsync(e.Admin, conv, default))).IsOk);
        var c = await LoadConvAsync(e, conv);
        Assert.Equal(("active", null, null, null, null), (c.AiState, c.HandoffReason, c.HandoffAt, c.AiPausedUntil, c.AwaitingStaffSince));
        Assert.NotNull(c.StaffLastReadAt);
        var msgs = await MsgsAsync(e, conv);
        Assert.Equal(before, msgs.Count);
        Assert.All(msgs, m => Assert.Equal("processed", m.Status));
        Assert.Empty(msgs.Where(m => m.Direction == MessageDirection.Outbound));
        Assert.Empty(e.H.Bot.Messages);
        Assert.Equal("held", (await MsgsAsync(e, other)).Single().Status);

        var list = await Svc(e, i => i.ListAsync(e.Admin, null, null, null, null, null, default));
        Assert.Equal(other, Assert.Single(list.Value!.Items).Id);
        Assert.Equal(1, list.Value.Meta.NeedsReplyCount);
        var all = await Svc(e, i => i.ListAsync(e.Admin, "all", null, null, null, null, default));
        Assert.Equal(0, all.Value!.Items.Single(x => x.Id == conv).UnreadCount);

        async Task<List<AiAction>> Journal()
        {
            await using var ctx = _db.CreateContext(e.TenantId);
            return await ctx.AiActions.AsNoTracking().Where(a => a.ToolName == "resolve_conversation").ToListAsync();
        }
        var row = Assert.Single(await Journal());
        Assert.Equal(e.Admin.UserId, JsonDocument.Parse(row.Payload).RootElement.GetProperty("userId").GetGuid());
        var stamp = (await LoadConvAsync(e, conv)).UpdatedAt;
        Assert.True((await Svc(e, i => i.ResolveAsync(e.Admin, conv, default))).IsOk);
        Assert.Single(await Journal());
        Assert.Equal(stamp, (await LoadConvAsync(e, conv)).UpdatedAt);

        e.H.Ai.Enqueue(ChatHarness.Text("Вітаю! Чим допомогти?"));
        await Ingest(e, "Скільки триває стрижка?");
        await e.H.TurnAsync(e.TenantId, conv);
        Assert.Single(e.H.Ai.Calls);
        Assert.Single(e.H.Bot.Messages);
        Assert.Equal("active", (await LoadConvAsync(e, conv)).AiState);
    }

    [SkippableFact]
    public async Task resolve_does_not_touch_active_booking_proposals_like_resume_ai()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        await SqlAsync(e, $"UPDATE beauty_conversations SET ai_state = 'handoff', awaiting_staff_since = now() WHERE id = {conv}");
        await SqlAsync(e, $"UPDATE beauty_messages SET status = 'held' WHERE conversation_id = {conv}");
        await Svc(e, i => i.ResolveAsync(e.Admin, conv, default));
        await using var ctx = _db.CreateContext(e.TenantId);
        Assert.Equal(0, await ctx.ChatBookingProposals.CountAsync(p => p.ConversationId == conv && p.Status == ChatProposalStatus.Superseded));
    }

    [SkippableFact]
    public async Task resolve_and_reply_race_ends_in_a_consistent_state_without_errors()
    {
        var e = await NewAsync();
        var conv = await ConvAsync(e);
        await SqlAsync(e, $"UPDATE beauty_conversations SET ai_state = 'handoff', awaiting_staff_since = now() WHERE id = {conv}");
        await SqlAsync(e, $"UPDATE beauty_messages SET status = 'held' WHERE conversation_id = {conv}");

        var a = Svc(e, i => i.ResolveAsync(e.Admin, conv, default));
        var b = Svc(e, i => i.ReplyAsync(e.Admin, conv, Key(), "Відповідь"));
        await Task.WhenAll(a, b);
        Assert.True(a.Result.IsOk);
        Assert.True(b.Result.IsOk);
        var c = await LoadConvAsync(e, conv);
        Assert.Null(c.AwaitingStaffSince);
        Assert.Contains(c.AiState, new[] { "active", "handoff" });
        var msgs = await MsgsAsync(e, conv);
        Assert.DoesNotContain(msgs, m => m.Direction == MessageDirection.Inbound && m.Status == "held");
        Assert.Single(msgs.Where(m => m.Direction == MessageDirection.Outbound));
    }
}
