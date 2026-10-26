using System.Net;
using System.Text;
using System.Text.Json;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Tests.Channels;

/// <summary>Запис викликів Bot API (TASK-750): метод, URL, тіло. Реального Telegram немає.</summary>
internal sealed class TgRecorder : HttpMessageHandler
{
    public sealed record Call(string Method, string Url, string Body);

    private readonly object _gate = new();
    private readonly List<Call> _calls = [];
    /// <summary>Відповідь для методу; null -> типова ({"ok":true,"result":{"message_id":777}} для sendMessage).</summary>
    public Func<Call, HttpResponseMessage?>? Responder;
    public TimeSpan Delay;

    public IReadOnlyList<Call> Calls { get { lock (_gate) return _calls.ToList(); } }
    public IReadOnlyList<Call> Of(string method) => Calls.Where(c => c.Method == method).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var url = req.RequestUri!.OriginalString;
        var call = new Call(url[(url.LastIndexOf('/') + 1)..], url, req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct));
        lock (_gate) _calls.Add(call);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Responder?.Invoke(call) is { } r) return r;
        var body = call.Method == "sendMessage" ? "{\"ok\":true,\"result\":{\"message_id\":777}}" : "{\"ok\":true,\"result\":true}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public static HttpResponseMessage Fail(HttpStatusCode code, string description) =>
        new(code) { Content = new StringContent($"{{\"ok\":false,\"description\":\"{description}\"}}", Encoding.UTF8, "application/json") };
}

/// <summary>TASK-750 (§23.4, §23.5, §23.13 сценарії 1, 3, 14, 18): парсер callback_query і транспорт без БД.</summary>
public class TelegramButtonsTransportTests
{
    private const string Token = "999111:SECRETTOKENVALUE";
    private static readonly string Tok22 = new('A', 22);

    private static TelegramAdapter A(TgRecorder rec, string? token = Token) => new(new HttpClient(rec), new ChCreds(token));

    private static string Cb(string id = "4417", string data = "by:AAAAAAAAAAAAAAAAAAAAAA", string chat = "{\"id\":42,\"type\":\"private\"}",
        string from = "{\"id\":42,\"is_bot\":false,\"first_name\":\"Ann\"}", string message = "\"message\":{\"message_id\":88,\"chat\":CHAT,\"date\":1700000000},",
        string extra = "") =>
        $"{{\"update_id\":5,\"callback_query\":{{\"id\":\"{id}\",\"from\":{from},{message.Replace("CHAT", chat)}\"chat_instance\":\"ci\",{(data is null ? "" : $"\"data\":\"{data}\",")}{extra}\"x\":1}}}}";

    // ------------------------------------------------------------------ парсер

    [Fact]
    public async Task callback_in_private_chat_becomes_a_callback_message_with_key_and_message_ref()
    {
        var m = Assert.Single(await A(new TgRecorder()).ParseInboundAsync(Cb()));
        Assert.Equal((InboundKinds.Callback, "42:cb:4417", "42", "42", "[кнопка]"), (m.Kind, m.ExternalMessageId, m.ExternalConversationId, m.SenderId, m.Text));
        Assert.Equal(("by:AAAAAAAAAAAAAAAAAAAAAA", "88", "4417", false), (m.CallbackData, m.CallbackMessageRef, m.CallbackQueryId, m.AckOnly));
        Assert.Equal("telegram:42:cb:4417", m.IdempotencyKey);
        Assert.True(DateTimeOffset.UtcNow - m.ReceivedAt < TimeSpan.FromMinutes(1)); // серверний час, не з payload
    }

    [Theory]
    [InlineData("group", "{\"id\":-100,\"type\":\"supergroup\"}")]
    [InlineData("channel chat", "{\"id\":-100,\"type\":\"channel\"}")]
    [InlineData("chat.id != from.id", "{\"id\":43,\"type\":\"private\"}")]
    [InlineData("no chat type", "{\"id\":42}")]
    public async Task callback_outside_own_private_chat_is_ack_only(string _, string chat)
    {
        var m = Assert.Single(await A(new TgRecorder()).ParseInboundAsync(Cb(chat: chat)));
        Assert.True(m.AckOnly);
        Assert.Equal("4417", m.CallbackQueryId);
        Assert.Null(m.CallbackData);
    }

    [Fact]
    public async Task callback_from_bot_inline_game_or_without_message_or_data_is_ack_only()
    {
        var a = A(new TgRecorder());
        foreach (var body in new[]
                 {
                     Cb(from: "{\"id\":42,\"is_bot\":true}"),
                     Cb(extra: "\"inline_message_id\":\"abc\","),
                     Cb(extra: "\"game_short_name\":\"g\","),
                     Cb(message: ""),
                     Cb(data: null!),
                     Cb(message: "\"message\":{\"message_id\":\"x\",\"chat\":CHAT},"),
                     Cb(from: "{\"is_bot\":false}"),
                 })
            Assert.True(Assert.Single(await a.ParseInboundAsync(body)).AckOnly, body);
    }

    [Fact]
    public async Task callback_without_usable_id_yields_nothing_because_it_cannot_be_acked()
    {
        var a = A(new TgRecorder());
        Assert.Empty(await a.ParseInboundAsync("""{"update_id":1,"callback_query":{"from":{"id":1},"data":"i:my"}}"""));
        Assert.Empty(await a.ParseInboundAsync("""{"update_id":1,"callback_query":{"id":12345,"data":"i:my"}}"""));
        Assert.Empty(await a.ParseInboundAsync(Cb(id: "a b")));
        Assert.Empty(await a.ParseInboundAsync(Cb(id: new string('1', 65))));
    }

    [Theory]
    [InlineData("by:AAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("sp:abcdefghijklmnopqrstuv:3", true)]
    [InlineData("rc:Zz09_-Zz09_-Zz09_-Zz09:0", true)]
    [InlineData("cn:AAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("i:book", true)]
    [InlineData("i:my", true)]
    [InlineData("i:help", true)]
    [InlineData("i:human", true)]
    [InlineData("i:stop", true)]
    [InlineData("xx:AAAAAAAAAAAAAAAAAAAAAA", false)]            // невідомий verb
    [InlineData("BY:AAAAAAAAAAAAAAAAAAAAAA", false)]            // регістр verb
    [InlineData("by:AAAAAAAAAAAAAAAAAAAAA", false)]             // токен 21 символ
    [InlineData("by:AAAAAAAAAAAAAAAAAAAAAAA", false)]           // токен 23 символи
    [InlineData("by:AAAAAAAAAAAAAAAAAAAAAA:12", false)]         // index двозначний
    [InlineData("by:AAAAAAAAAAAAAAAAAAAAAA:a", false)]
    [InlineData("by:AAAAAAAAAAAAAAAAAAAAAA\\n", false)]         // перенос рядка в кінці
    [InlineData("by:AAAAAAAAAAAAAAAAAAAAA!", false)]
    [InlineData("i:unknown", false)]
    [InlineData("i:book\\n", false)]
    [InlineData("by:", false)]
    [InlineData("", false)]
    public async Task callback_data_format_is_validated_and_invalid_data_is_stored_as_null(string data, bool valid)
    {
        var m = Assert.Single(await A(new TgRecorder()).ParseInboundAsync(Cb(data: data)));
        Assert.False(m.AckOnly); // рядок зберігається (→ T-action-stale), лише callback_data = null
        Assert.Equal(valid ? JsonDocument.Parse($"\"{data}\"").RootElement.GetString() : null, m.CallbackData);
    }

    [Fact]
    public async Task callback_data_over_64_bytes_or_multibyte_is_null_even_if_it_looks_like_a_verb()
    {
        var a = A(new TgRecorder());
        Assert.Null(Assert.Single(await a.ParseInboundAsync(Cb(data: "by:" + new string('A', 62)))).CallbackData); // 65 байт
        Assert.Null(Assert.Single(await a.ParseInboundAsync(Cb(data: "by:" + new string('Я', 22)))).CallbackData);
        Assert.False(TelegramAdapter.IsValidCallbackData(null));
        Assert.True(Encoding.UTF8.GetByteCount("sp:" + Tok22 + ":9") <= 64);
    }

    [Fact]
    public async Task forged_payload_fields_do_not_influence_identity_tenant_or_chat()
    {
        // чужі поля в update/callback_query/message не читаються; розмова = chat.id, відправник = from.id (збігаються за правилом)
        var body = Cb(extra: "\"tenant_id\":\"11111111-1111-1111-1111-111111111111\",\"channel_id\":\"22222222-2222-2222-2222-222222222222\",\"chat_id\":777,");
        var m = Assert.Single(await A(new TgRecorder()).ParseInboundAsync(body));
        Assert.Equal(("42", "42"), (m.ExternalConversationId, m.SenderId));
        Assert.Null(m.ConversationId);
    }

    // ------------------------------------------------------------------ відправка

    [Fact]
    public async Task sendMessage_sends_exact_inline_keyboard_without_parse_mode_and_returns_message_id()
    {
        var rec = new TgRecorder();
        var actions = new ChatActionLayout([
            [new("✅ Так, записатися", "by:" + Tok22), new("Ні", "bn:" + Tok22)],
            [new("🕐 Інший час", "bo:" + Tok22)],
        ]);
        var receipt = await A(rec).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "Підсумок", Actions: actions), default);
        Assert.Equal("777", receipt.ExternalMessageId);
        var call = Assert.Single(rec.Of("sendMessage"));
        Assert.Contains($"/bot{Token}/sendMessage", call.Url);
        using var doc = JsonDocument.Parse(call.Body);
        var root = doc.RootElement;
        Assert.Equal(["chat_id", "text", "reply_markup"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse("""{"inline_keyboard":[[{"text":"✅ Так, записатися","callback_data":"by:AAAAAAAAAAAAAAAAAAAAAA"},{"text":"Ні","callback_data":"bn:AAAAAAAAAAAAAAAAAAAAAA"}],[{"text":"🕐 Інший час","callback_data":"bo:AAAAAAAAAAAAAAAAAAAAAA"}]]}"""),
            System.Text.Json.Nodes.JsonNode.Parse(root.GetProperty("reply_markup").GetRawText())));
    }

    [Fact]
    public async Task sendMessage_without_actions_has_no_reply_markup_and_invalid_actions_are_dropped()
    {
        var rec = new TgRecorder();
        var a = A(rec);
        await a.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x"), default);
        var tooLong = new ChatActionLayout([[new("Так", "by:" + new string('A', 70))]]);
        var empty = new ChatActionLayout([[new(" ", "i:my")]]);
        var tooMany = new ChatActionLayout(Enumerable.Range(0, 11).Select(i => (IReadOnlyList<ChatActionButton>)[new("b" + i, "i:my")]).ToList());
        foreach (var bad in new[] { tooLong, empty, tooMany })
            await a.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x", Actions: bad), default);
        Assert.Equal(4, rec.Of("sendMessage").Count);
        Assert.All(rec.Of("sendMessage"), c => Assert.DoesNotContain("reply_markup", c.Body));
        Assert.All(rec.Of("sendMessage"), c => Assert.DoesNotContain("parse_mode", c.Body));
    }

    [Fact]
    public async Task sendMessage_receipt_is_null_when_telegram_body_has_no_message_id()
    {
        var rec = new TgRecorder { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") } };
        Assert.Null((await A(rec).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x"), default)).ExternalMessageId);
    }

    [Fact]
    public async Task actions_layout_round_trips_from_stored_json_and_rejects_garbage()
    {
        var l = ChatActionLayout.TryParse("""{"rows":[[{"label":"A","data":"i:my"},{"Label":"B","Data":"i:book"}],[]]}""");
        Assert.Equal(["A", "B"], l!.Rows.Single().Select(b => b.Label));
        Assert.True(l.IsValid());
        Assert.Null(ChatActionLayout.TryParse(null));
        Assert.Null(ChatActionLayout.TryParse("[]"));
        Assert.Null(ChatActionLayout.TryParse("{\"rows\":[[{\"label\":1}]]}"));
        Assert.Null(ChatActionLayout.TryParse("{oops"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task instagram_and_mock_adapters_ignore_actions_and_return_no_receipt()
    {
        Assert.False(new MockTelegramAdapter().SupportsActions);
        Assert.False(new MockInstagramAdapter().SupportsActions);
        Assert.True(A(new TgRecorder()).SupportsActions);
        var mock = new MockTelegramAdapter();
        var r = await mock.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "1", "t", Actions: new ChatActionLayout([[new("A", "i:my")]])), default);
        Assert.Null(r.ExternalMessageId);

        var rec = new TgRecorder();
        var ig = new InstagramAdapter(new HttpClient(rec), new ChCreds());
        Assert.False(((IChannelAdapter)ig).SupportsActions);
        var rr = await ig.SendAsync(new OutboundMessage(Guid.NewGuid(), "instagram", "u", "text", DateTimeOffset.UtcNow, new ChatActionLayout([[new("A", "i:my")]])), default);
        Assert.Null(rr.ExternalMessageId);
        Assert.DoesNotContain("inline_keyboard", Assert.Single(rec.Calls).Body);
        Assert.DoesNotContain("i:my", rec.Calls[0].Body);
    }

    // ------------------------------------------------------------------ ack / clear / commands

    [Fact]
    public async Task answerCallbackQuery_sends_only_the_query_id()
    {
        var rec = new TgRecorder();
        await A(rec).AcknowledgeCallbackAsync("4417", default);
        var call = Assert.Single(rec.Calls);
        Assert.EndsWith("/answerCallbackQuery", call.Url);
        Assert.Equal("""{"callback_query_id":"4417"}""", call.Body);
    }

    [Fact]
    public async Task editMessageReplyMarkup_sends_empty_keyboard_and_treats_already_cleared_as_success()
    {
        var rec = new TgRecorder();
        await A(rec).ClearActionsAsync("42", "88", default);
        Assert.Equal("""{"chat_id":"42","message_id":88,"reply_markup":{"inline_keyboard":[]}}""", Assert.Single(rec.Calls).Body);

        foreach (var d in new[] { "Bad Request: message is not modified: specified new message content and reply markup are exactly the same", "Bad Request: message to edit not found" })
        {
            var r2 = new TgRecorder { Responder = _ => TgRecorder.Fail(HttpStatusCode.BadRequest, d) };
            await A(r2).ClearActionsAsync("42", "88", default); // без винятку
        }
    }

    [Fact]
    public async Task editMessageReplyMarkup_classifies_other_failures_without_leaking_token_or_text()
    {
        var blocked = await Assert.ThrowsAsync<PermanentChannelException>(() =>
            A(new TgRecorder { Responder = _ => TgRecorder.Fail(HttpStatusCode.Forbidden, "Forbidden: bot was blocked by the user") }).ClearActionsAsync("42", "88", default));
        Assert.Equal(ChannelSendCodes.BotBlocked, blocked.Code);
        var other400 = await Assert.ThrowsAsync<PermanentChannelException>(() =>
            A(new TgRecorder { Responder = _ => TgRecorder.Fail(HttpStatusCode.BadRequest, "Bad Request: SECRETTOKENVALUE weird") }).ClearActionsAsync("42", "88", default));
        Assert.Null(other400.Code);
        await Assert.ThrowsAsync<TransientChannelException>(() =>
            A(new TgRecorder { Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) }).ClearActionsAsync("42", "88", default));
        foreach (var ex in new Exception[] { blocked, other400 })
        {
            Assert.DoesNotContain("SECRETTOKEN", ex.ToString());
            Assert.DoesNotContain("blocked by the user", ex.ToString());
        }
        var noToken = await Assert.ThrowsAsync<PermanentChannelException>(() => A(new TgRecorder(), token: null).ClearActionsAsync("42", "88", default));
        Assert.Equal(ChannelSendCodes.TokenMissing, noToken.Code);
    }

    [Fact]
    public async Task setMyCommands_is_sent_for_default_and_uk_with_the_command_menu()
    {
        var rec = new TgRecorder();
        await A(rec).SyncCommandsAsync(default);
        var calls = rec.Of("setMyCommands");
        Assert.Equal(2, calls.Count);
        using var d0 = JsonDocument.Parse(calls[0].Body);
        using var d1 = JsonDocument.Parse(calls[1].Body);
        Assert.False(d0.RootElement.TryGetProperty("language_code", out _));
        Assert.Equal("uk", d1.RootElement.GetProperty("language_code").GetString());
        foreach (var d in new[] { d0, d1 })
            Assert.Equal(["start", "book", "my", "cancel", "stop", "help"],
                d.RootElement.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("command").GetString()!).ToArray());
        Assert.Equal("Записатися", d0.RootElement.GetProperty("commands")[1].GetProperty("description").GetString());
    }

    [Fact]
    public async Task setMyCommands_failure_is_classified_and_hides_the_token()
    {
        var rec = new TgRecorder { Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var ex = await Assert.ThrowsAsync<TransientChannelException>(() => A(rec).SyncCommandsAsync(default));
        Assert.DoesNotContain("SECRETTOKEN", ex.ToString());
        Assert.Single(rec.Of("setMyCommands")); // перший збій зупиняє
        var net = new TelegramAdapter(new HttpClient(new FakeTelegram().Throw(new HttpRequestException($"x https://api.telegram.org/bot{Token}/setMyCommands"))), new ChCreds());
        Assert.DoesNotContain("SECRETTOKEN", (await Assert.ThrowsAsync<TransientChannelException>(() => net.SyncCommandsAsync(default))).ToString());
    }

    [Fact]
    public async Task outbox_processor_stores_the_receipt_id_and_passes_actions()
    {
        var rec = new TgRecorder();
        var repo = new ReceiptRepo { Item = new OutboxItem(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "hi", Actions: new ChatActionLayout([[new("A", "i:my")]])), OutboxStatus.Pending, 0) };
        var p = new OutboxProcessor(new ChannelRegistry([A(rec)]), repo, new ChFakeQueue());
        Assert.Equal(OutboxResult.Sent, await p.ProcessAsync(Guid.NewGuid(), default));
        Assert.Equal("777", repo.ExternalId);
        Assert.Contains("inline_keyboard", Assert.Single(rec.Of("sendMessage")).Body);
    }

    private sealed class ReceiptRepo : IChannelMessageRepository
    {
        public OutboxItem? Item; public string? ExternalId;
        public Task<IReadOnlyList<InboundMessage>> SaveInboundAsync(IReadOnlyList<InboundMessage> m, CancellationToken ct) => Task.FromResult(m);
        public Task<OutboxItem?> GetOutboxItemAsync(Guid id, CancellationToken ct) => Task.FromResult(Item);
        public Task MarkSentAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task MarkSentAsync(Guid id, string? externalMessageId, CancellationToken ct) { ExternalId = externalMessageId; return Task.CompletedTask; }
        public Task MarkAttemptFailedAsync(Guid id, int attempts, string error, bool final, CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>TASK-750: ingest (webhook і polling), ack, відправка і порт на реальній БД з RLS (fake Telegram).</summary>
public sealed class TelegramButtonsDbTests : IClassFixture<PostgresRlsFixture>
{
    private const string Token = "999111:SECRETTOKENVALUE";
    private const string WebhookSecret = "whsecret";
    private static readonly string Tok22 = new('B', 22);
    private readonly PostgresRlsFixture _db;
    public TelegramButtonsDbTests(PostgresRlsFixture db) => _db = db;

    private sealed class Harness
    {
        public required ServiceProvider Sp; public required TgRecorder Bot; public required FakeTelegram Poll; public required LogCapture Logs;
        public required TelegramPoller Poller;
    }

    private Harness Build()
    {
        Skip.If(_db.SkipReason is not null, _db.SkipReason);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = _db.AppConnectionString,
            ["Channels:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        }).Build();
        var bot = new TgRecorder(); var poll = new FakeTelegram(); var logs = new LogCapture();
        var opts = new TelegramPollingOptions { EmptyPollDelay = TimeSpan.FromMilliseconds(5) };
        var s = new ServiceCollection();
        s.AddSingleton<IConfiguration>(cfg).AddLogging(b => b.AddProvider(logs));
        s.AddSingleton(opts).AddSingleton(new TelegramPollingHttp(new HttpClient(poll)));
        s.AddBeautyData().AddBeautyChannels(false);
        s.RemoveAll<IChannelAdapter>();
        s.AddSingleton<IChannelAdapter>(sp => new TelegramAdapter(new HttpClient(bot), sp.GetRequiredService<IChannelCredentialsProvider>()));
        s.AddSingleton<IChannelAdapter>(sp => new InstagramAdapter(new HttpClient(bot), sp.GetRequiredService<IChannelCredentialsProvider>()));
        var sp = s.BuildServiceProvider();
        return new Harness { Sp = sp, Bot = bot, Poll = poll, Logs = logs, Poller = sp.GetRequiredService<TelegramPoller>() };
    }

    private async Task<(Guid Tenant, Guid Channel)> SeedAsync(Harness h, ChannelType type = ChannelType.Telegram, string? token = Token, bool active = true, Guid? tenant = null)
    {
        var tenantId = tenant ?? Guid.NewGuid();
        if (tenant is null)
        {
            await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'b-{tenantId:N}')", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        var protector = new AesGcmSecretProtector(h.Sp.GetRequiredService<IConfiguration>());
        await using var ctx = _db.CreateContext(tenantId);
        var ch = new Channel
        {
            Type = type, Name = "bot", IsActive = active,
            CredentialsEncrypted = ChannelSecrets.Write(protector, new ChannelSecretSet(token, WebhookSecret)),
        };
        ctx.Add(ch);
        await ctx.SaveChangesAsync();
        return (tenantId, ch.Id);
    }

    private static string Update(long updateId, string queryId, string data, string chat = "{\"id\":100,\"type\":\"private\"}", long fromId = 100, long msgId = 55) =>
        $"{{\"update_id\":{updateId},\"callback_query\":{{\"id\":\"{queryId}\",\"from\":{{\"id\":{fromId},\"is_bot\":false,\"first_name\":\"Ann\"}}," +
        $"\"message\":{{\"message_id\":{msgId},\"chat\":{chat},\"date\":1700000000}},\"chat_instance\":\"c\",\"data\":\"{data}\"}}}}";

    private async Task<List<Message>> MessagesAsync(Guid tenant)
    {
        await using var ctx = _db.CreateContext(tenant);
        return await ctx.Messages.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
    }

    private static Task<WebhookIngestResult> Webhook(Harness h, Guid ch, string body, string secret = WebhookSecret)
    {
        var scope = h.Sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<WebhookIngestService>().HandleAsync("telegram", ch,
            new WebhookRequest(new Dictionary<string, string> { [TelegramAdapter.SecretHeader] = secret }), body, default);
    }

    [SkippableFact]
    public async Task webhook_and_polling_store_the_same_callback_once_and_ack_every_delivery()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        var upd = Update(10, "9001", "by:" + Tok22);

        var w = await Webhook(h, ch, upd);
        Assert.Equal((WebhookOutcome.Accepted, 1), (w.Outcome, w.NewMessages));
        h.Poll.Updates(upd, upd); // той самий callback_query.id: повторна видача getUpdates + дубль у пакеті
        var r = await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal((PollStatus.Ok, 2, 0), (r.Status, r.Updates, r.Stored));

        var rows = await MessagesAsync(t);
        var m = Assert.Single(rows);
        Assert.Equal(("callback", "[кнопка]", "100:cb:9001", "by:" + Tok22, "55", "received"), (m.Kind, m.Body, m.ExternalMessageId, m.CallbackData, m.CallbackMessageRef, m.Status));
        Assert.Equal(MessageDirection.Inbound, m.Direction);
        Assert.Equal(MessageSenderType.Client, m.SenderType);
        Assert.Equal($"telegram:{ch:N}:100:cb:9001", m.IdempotencyKey);
        // ack для кожної доставки (нові й дублі): webhook 1 + пакет polling (одна унікальна id) 1
        Assert.Equal(2, h.Bot.Of("answerCallbackQuery").Count);
        Assert.All(h.Bot.Of("answerCallbackQuery"), c => Assert.Equal("""{"callback_query_id":"9001"}""", c.Body));
    }

    [SkippableFact]
    public async Task polling_requests_callback_query_and_ack_only_updates_are_acked_but_not_stored()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        h.Poll.Updates(
            Update(20, "g1", "i:my", chat: "{\"id\":-500,\"type\":\"supergroup\"}"),
            Update(21, "g2", "i:my", chat: "{\"id\":100,\"type\":\"private\"}", fromId: 101),
            Update(22, "ok", "i:my"));
        var r = await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal((PollStatus.Ok, 3, 1), (r.Status, r.Updates, r.Stored));
        Assert.Contains("allowed_updates=%5B%22message%22%2C%22callback_query%22%5D", h.Poll.GetUpdatesUrls.Last());
        Assert.Equal(["ok"], (await MessagesAsync(t)).Select(m => m.ExternalMessageId!.Split(":cb:")[1]));
        Assert.Equal(["g1", "g2", "ok"], h.Bot.Of("answerCallbackQuery").Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("callback_query_id").GetString()!).OrderBy(x => x));
        await using var ctx = _db.CreateContext(t);
        Assert.Equal(22, (await ctx.ChannelPollStates.AsNoTracking().FirstAsync(s => s.ChannelId == ch)).LastUpdateId); // offset просунувся
    }

    [SkippableFact]
    public async Task invalid_callback_data_is_stored_with_null_callback_data_and_a_foreign_chat_gets_its_own_conversation()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        await Webhook(h, ch, Update(30, "c1", "by:" + new string('A', 62)));      // 65 байт
        await Webhook(h, ch, Update(31, "c2", "zz:" + Tok22));                     // невідомий verb
        await Webhook(h, ch, Update(32, "c3", "by:" + new string('A', 21)));       // токен 21
        await Webhook(h, ch, Update(33, "c4", "sp:" + Tok22 + ":2", "{\"id\":200,\"type\":\"private\"}", fromId: 200));
        var rows = await MessagesAsync(t);
        Assert.Equal([null, null, null, "sp:" + Tok22 + ":2"], rows.Select(m => m.CallbackData));
        Assert.All(rows, m => Assert.Equal("callback", m.Kind));
        await using var ctx = _db.CreateContext(t);
        Assert.Equal(["100", "200"], (await ctx.Conversations.AsNoTracking().Select(c => c.ExternalChatId).ToListAsync()).Order());
    }

    [SkippableFact]
    public async Task webhook_with_wrong_secret_stores_nothing_and_does_not_ack()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        var w = await Webhook(h, ch, Update(40, "bad", "i:my"), secret: "nope");
        Assert.Equal(WebhookOutcome.InvalidSignature, w.Outcome);
        Assert.Empty(await MessagesAsync(t));
        Assert.Empty(h.Bot.Calls);
    }

    [SkippableFact]
    public async Task ack_failure_or_timeout_does_not_block_storing_and_logs_no_token_or_callback_id()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        h.Bot.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await Webhook(h, ch, Update(50, "SECRETQID1", "i:my"));
        Assert.Single(await MessagesAsync(t));
        Assert.Contains($"telegram_ack_failed channel={ch} error=TransientChannelException", h.Logs.All);

        h.Bot.Responder = null; h.Bot.Delay = TimeSpan.FromSeconds(6);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Webhook(h, ch, Update(51, "SECRETQID2", "i:help"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"ack timeout must be ~3s, was {sw.Elapsed}");
        Assert.Equal(2, (await MessagesAsync(t)).Count);
        Assert.Contains("error=TimeoutException", h.Logs.All);

        foreach (var secret in new[] { Token, "SECRETTOKENVALUE", "SECRETQID1", "SECRETQID2", "i:my", "100" })
            Assert.DoesNotContain(secret, h.Logs.All.Replace($"channel={ch}", ""));
    }

    [SkippableFact]
    public async Task outbound_with_actions_is_sent_with_reply_markup_and_external_message_id_is_saved()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        Guid withActions, plain;
        await using (var ctx = _db.CreateContext(t))
        {
            var conv = new Conversation { Id = Guid.NewGuid(), ChannelId = ch, ExternalChatId = "100" };
            ctx.Conversations.Add(conv);
            Message Out(string body, string? actions) => new()
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System,
                Body = body, Status = "pending", SentAt = DateTimeOffset.UtcNow, Actions = actions,
            };
            var a = Out("з кнопками", $$"""{"rows":[[{"label":"Так","data":"by:{{Tok22}}"},{"label":"Ні","data":"bn:{{Tok22}}"}]]}""");
            var b = Out("без кнопок", null);
            ctx.Messages.AddRange(a, b);
            await ctx.SaveChangesAsync();
            (withActions, plain) = (a.Id, b.Id);
        }
        using (var scope = h.Sp.CreateScope())
        {
            var d = scope.ServiceProvider.GetRequiredService<OutboundDispatcher>();
            Assert.Equal(OutboxResult.Sent, await d.DispatchAsync(t, withActions, default));
        }
        h.Bot.Responder = c => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":778}}", Encoding.UTF8, "application/json") };
        using (var scope = h.Sp.CreateScope())
            Assert.Equal(OutboxResult.Sent, await scope.ServiceProvider.GetRequiredService<OutboundDispatcher>().DispatchAsync(t, plain, default));

        var sends = h.Bot.Of("sendMessage");
        Assert.Equal(2, sends.Count);
        using (var doc = JsonDocument.Parse(sends[0].Body))
        {
            var kb = doc.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
            Assert.Equal(["Так", "Ні"], kb[0].EnumerateArray().Select(x => x.GetProperty("text").GetString()!));
            Assert.Equal(["by:" + Tok22, "bn:" + Tok22], kb[0].EnumerateArray().Select(x => x.GetProperty("callback_data").GetString()!));
        }
        Assert.DoesNotContain("reply_markup", sends[1].Body);

        await using var read = _db.CreateContext(t);
        var rows = await read.Messages.AsNoTracking().Where(m => m.Id == withActions || m.Id == plain).ToDictionaryAsync(m => m.Id);
        Assert.Equal(("sent", "777"), (rows[withActions].Status, rows[withActions].ExternalMessageId));
        Assert.Equal(("sent", "778"), (rows[plain].Status, rows[plain].ExternalMessageId));
        Assert.Null(rows[withActions].LastError);
    }

    private async Task<(Guid Tenant, Guid Channel, IChannelActionsPort Port, IDisposable Scope)> PortAsync(Harness h, ChannelType type = ChannelType.Telegram, string? token = Token, bool active = true)
    {
        var (t, ch) = await SeedAsync(h, type, token, active);
        var scope = h.Sp.CreateScope();
        return (t, ch, scope.ServiceProvider.GetRequiredService<IChannelActionsPort>(), scope);
    }

    [SkippableFact]
    public async Task port_clears_keyboard_answers_callback_and_syncs_commands_for_the_tenants_channel()
    {
        var h = Build();
        var (t, ch, port, scope) = await PortAsync(h); using var _ = scope;
        Assert.Equal(ChannelActionResult.Ok, await port.ClearKeyboardAsync(t, ch, "100", "55", default));
        Assert.Equal("""{"chat_id":"100","message_id":55,"reply_markup":{"inline_keyboard":[]}}""", Assert.Single(h.Bot.Of("editMessageReplyMarkup")).Body);
        Assert.Equal(ChannelActionResult.Ok, await port.AnswerCallbackAsync(t, ch, "77", default));
        Assert.Equal(ChannelActionResult.Ok, await port.SyncCommandsAsync(t, ch, default));
        Assert.Equal(2, h.Bot.Of("setMyCommands").Count);
        Assert.Contains($"telegram_commands_synced channel={ch}", h.Logs.All);
    }

    [SkippableFact]
    public async Task port_maps_results_without_leaking_provider_text_or_token()
    {
        var h = Build();
        var (t, ch, port, scope) = await PortAsync(h); using var _ = scope;

        h.Bot.Responder = _ => TgRecorder.Fail(HttpStatusCode.BadRequest, "Bad Request: message is not modified");
        Assert.Equal(ChannelActionResult.Ok, await port.ClearKeyboardAsync(t, ch, "100", "55", default));
        h.Bot.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal(ChannelActionResult.Failed, await port.ClearKeyboardAsync(t, ch, "100", "55", default));
        h.Bot.Responder = _ => TgRecorder.Fail(HttpStatusCode.Forbidden, "Forbidden: bot was blocked by the user");
        Assert.Equal(ChannelActionResult.Failed, await port.SyncCommandsAsync(t, ch, default));
        Assert.Equal(ChannelActionResult.Failed, await port.AnswerCallbackAsync(t, ch, "77", default));

        Assert.Contains($"telegram_clear_failed channel={ch} error=TransientChannelException", h.Logs.All);
        Assert.Contains($"telegram_commands_failed channel={ch} error=bot_blocked", h.Logs.All);
        Assert.Contains($"telegram_ack_failed channel={ch} error=bot_blocked", h.Logs.All);
        Assert.DoesNotContain("SECRETTOKEN", h.Logs.All);
        Assert.DoesNotContain("blocked by the user", h.Logs.All);

        Assert.Equal(ChannelActionResult.ChannelNotFound, await port.SyncCommandsAsync(t, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task port_rejects_foreign_tenant_inactive_tokenless_and_non_telegram_channels()
    {
        var h = Build();
        var (t, ch, port, scope) = await PortAsync(h); using var _ = scope;
        var (t2, ch2) = await SeedAsync(h);
        Assert.Equal(ChannelActionResult.Ok, await port.SyncCommandsAsync(t, ch, default)); // tenant scope встановлено на t
        await Assert.ThrowsAsync<BeautyCrm.Infrastructure.AI.Beauty.TenantMismatchException>(() => port.SyncCommandsAsync(t2, ch2, default));

        // чужий канал того ж tenant-scope: RLS ховає його
        Assert.Equal(ChannelActionResult.ChannelNotFound, await port.SyncCommandsAsync(t, ch2, default));

        foreach (var (type, token, active, expected) in new[]
                 {
                     (ChannelType.Telegram, (string?)null, true, ChannelActionResult.TokenMissing),
                     (ChannelType.Telegram, (string?)Token, false, ChannelActionResult.Inactive),
                     (ChannelType.Instagram, (string?)"igtoken", true, ChannelActionResult.Unsupported),
                 })
        {
            var (tt, cc, p, s) = await PortAsync(h, type, token, active); using var __ = s;
            Assert.Equal(expected, await p.SyncCommandsAsync(tt, cc, default));
        }
        Assert.Equal(2, h.Bot.Of("setMyCommands").Count); // лише успішний sync вище; відхилені канали Telegram не викликали
    }

    [SkippableFact]
    public async Task polling_start_syncs_commands_best_effort_and_never_logs_the_token()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        await h.Poller.SyncCommandsAsync(ch, default);
        Assert.Equal(2, h.Bot.Of("setMyCommands").Count);
        Assert.Contains($"telegram_commands_synced channel={ch}", h.Logs.All);

        h.Bot.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        await h.Poller.SyncCommandsAsync(ch, default); // не кидає
        Assert.Contains($"telegram_commands_failed channel={ch} error=TransientChannelException", h.Logs.All);
        Assert.DoesNotContain("SECRETTOKEN", h.Logs.All);

        var (_, noToken) = await SeedAsync(h, token: null);
        var before = h.Bot.Calls.Count;
        await h.Poller.SyncCommandsAsync(noToken, default); // без токена - тихо пропускає
        Assert.Equal(before, h.Bot.Calls.Count);
    }

    [SkippableFact]
    public async Task commands_service_after_save_is_best_effort_and_manual_sync_reports_codes()
    {
        var h = Build();
        var (t, ch, _, scope) = await PortAsync(h); using var _s = scope;
        using var sc = h.Sp.CreateScope();
        sc.ServiceProvider.GetRequiredService<BeautyCrm.Infrastructure.Data.Tenancy.TenantContext>().SetTenant(t);
        var svc = sc.ServiceProvider.GetRequiredService<TelegramCommandsService>();
        Assert.Equal(ChannelActionResult.Ok, await svc.SyncAsync(ch, default));
        h.Bot.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal(ChannelActionResult.Failed, await svc.SyncAsync(ch, default));
        await svc.TrySyncAfterSaveAsync(ch, default); // не кидає
        Assert.Equal(ChannelActionResult.ChannelNotFound, await svc.SyncAsync(Guid.NewGuid(), default));
    }
}
