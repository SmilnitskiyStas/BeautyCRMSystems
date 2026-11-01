using System.Net;
using System.Text;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Tests.Channels;

/// <summary>TASK-725 (ADR-011 §2.1, контракт §21.3/§21.10 сценарії 2, 13, 17): ingest/polling Telegram. Реального Telegram немає.</summary>
internal sealed class FakeTelegram : HttpMessageHandler
{
    public readonly List<string> Urls = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();

    public FakeTelegram Updates(params string[] updates) =>
        Script(_ => Json(HttpStatusCode.OK, $"{{\"ok\":true,\"result\":[{string.Join(',', updates)}]}}"));
    public FakeTelegram Status(HttpStatusCode code, string body = "{\"ok\":false}") => Script(_ => Json(code, body));
    public FakeTelegram Throw(Exception ex) => Script(_ => throw ex);
    public FakeTelegram Script(Func<HttpRequestMessage, HttpResponseMessage> f) { lock (_script) _script.Enqueue(f); return this; }

    public IEnumerable<string> GetUpdatesUrls => Urls.Where(u => u.Contains("/getUpdates"));
    public string? LastOffset => GetUpdatesUrls.LastOrDefault()?.Split("offset=")[1].Split('&')[0];

    private static HttpResponseMessage Json(HttpStatusCode c, string body) =>
        new(c) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        lock (Urls) Urls.Add(r.RequestUri!.OriginalString);
        Func<HttpRequestMessage, HttpResponseMessage>? f = null;
        lock (_script) if (_script.Count > 0) f = _script.Dequeue();
        return Task.FromResult(f is null ? Json(HttpStatusCode.OK, "{\"ok\":true,\"result\":[]}") : f(r));
    }
}

internal sealed class LogCapture : ILoggerProvider, ILogger
{
    public readonly List<string> Lines = new();
    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel l, EventId e, TState state, Exception? ex, Func<TState, Exception?, string> f)
    {
        lock (Lines) Lines.Add($"{l} {f(state, ex)} {ex}");
    }
    public string All { get { lock (Lines) return string.Join('\n', Lines); } }
    public void Dispose() { }
}

internal sealed class SwitchQueue : IChannelQueue
{
    public bool Fail;
    public int Signalled;
    public Task EnqueueInboundAsync(IReadOnlyList<InboundMessage> m, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("queue down");
        Signalled += m.Count; return Task.CompletedTask;
    }
    public Task ScheduleOutboxRetryAsync(Guid id, TimeSpan d, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class TestEnv(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "t";
    public string ContentRootPath { get; set; } = ".";
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}

public class TelegramParserTests
{
    private static TelegramAdapter A() => new(new HttpClient(), new ChCreds());
    private static async Task<IReadOnlyList<InboundMessage>> P(string body) => await A().ParseInboundAsync(body);

    private static string Msg(string extra, string chat = "{\"id\":42,\"type\":\"private\"}", string from = "{\"id\":42,\"is_bot\":false,\"first_name\":\"Ann\"}") =>
        $"{{\"update_id\":1,\"message\":{{\"message_id\":7,\"from\":{from},\"chat\":{chat},\"date\":1700000000,{extra}}}}}";

    [Fact]
    public async Task parse_text_command_and_caption_are_text_or_command_with_text_as_is()
    {
        var t = Assert.Single(await P(Msg("\"text\":\"  Привіт  \\n<b>x</b>\"")));
        Assert.Equal((InboundKinds.Text, "  Привіт  \n<b>x</b>", "42:7"), (t.Kind, t.Text, t.ExternalMessageId));
        Assert.Equal(InboundKinds.Command, Assert.Single(await P(Msg("\"text\":\"/start\""))).Kind);
        Assert.Equal(InboundKinds.Command, Assert.Single(await P(Msg("\"text\":\"/help me\""))).Kind);
        var cap = Assert.Single(await P(Msg("\"photo\":[{}],\"caption\":\"мій підпис\"")));
        Assert.Equal((InboundKinds.Text, "мій підпис"), (cap.Kind, cap.Text));
    }

    [Theory]
    [InlineData("\"sticker\":{}", "[sticker]")]
    [InlineData("\"voice\":{}", "[voice]")]
    [InlineData("\"photo\":[{}]", "[photo]")]
    [InlineData("\"document\":{}", "[document]")]
    [InlineData("\"location\":{}", "[location]")]
    [InlineData("\"poll\":{}", "[unsupported]")]
    public async Task parse_media_is_unsupported_with_placeholder_and_never_keeps_contact_phone(string extra, string placeholder)
    {
        var m = Assert.Single(await P(Msg(extra)));
        Assert.Equal((InboundKinds.Unsupported, placeholder), (m.Kind, m.Text));
        Assert.DoesNotContain("380501112233", m.Text);
    }

    [Theory]
    [InlineData("{\"id\":-100,\"type\":\"supergroup\"}")]
    [InlineData("{\"id\":-100,\"type\":\"group\"}")]
    [InlineData("{\"id\":-100,\"type\":\"channel\"}")]
    [InlineData("{\"id\":-100}")]            // type не надіслано: від'ємний id = не приватний чат
    public async Task parse_ignores_groups_and_channels(string chat) =>
        Assert.Empty(await P(Msg("\"text\":\"hi\"", chat: chat)));

    [Fact]
    public async Task parse_ignores_bots_edited_messages_and_other_update_types()
    {
        Assert.Empty(await P(Msg("\"text\":\"hi\"", from: "{\"id\":9,\"is_bot\":true,\"first_name\":\"Bot\"}")));
        Assert.Empty(await P("""{"update_id":2,"edited_message":{"message_id":7,"from":{"id":42},"chat":{"id":42,"type":"private"},"text":"x"}}"""));
        // TASK-750: callback_query парситься (без message/data це ack-only, не збережений рядок)
        Assert.True(Assert.Single(await P("""{"update_id":3,"callback_query":{"id":"1"}}""")).AckOnly);
        Assert.Empty(await P("""{"update_id":4,"channel_post":{"message_id":1,"chat":{"id":-5,"type":"channel"},"text":"x"}}"""));
    }

    [Fact]
    public async Task parse_legacy_payload_without_type_and_is_bot_still_works()
    {
        var m = Assert.Single(await P("""{"update_id":1,"message":{"message_id":7,"from":{"id":42,"first_name":"Ann","last_name":"K"},"chat":{"id":42},"date":1700000000,"text":"Hello"}}"""));
        Assert.Equal(("42:7", "42", "Ann K", "Hello"), (m.ExternalMessageId, m.ExternalConversationId, m.SenderName, m.Text));
    }

    [Fact]
    public void options_start_fails_outside_development_or_with_mocks_and_is_off_by_default()
    {
        TelegramPollingOptions Opt(params (string, string?)[] kv) =>
            TelegramPollingOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build());
        var off = Opt();
        Assert.False(off.Enabled);
        off.ValidateForHost(isDevelopment: false, useMocks: true); // вимкнено: нічого не перевіряється
        var id = Guid.NewGuid();
        var on = Opt(("Channels:Telegram:Polling:Enabled", "true"), ("Channels:Telegram:Polling:ChannelIds", $"{id}, {id}"));
        Assert.Equal([id], on.ChannelIds);
        on.ValidateForHost(true, false);
        Assert.Throws<InvalidOperationException>(() => on.ValidateForHost(false, false));
        Assert.Throws<InvalidOperationException>(() => on.ValidateForHost(true, true));
        var arr = Opt(("Channels:Telegram:Polling:Enabled", "true"), ("Channels:Telegram:Polling:ChannelIds:0", id.ToString()));
        Assert.Equal([id], arr.ChannelIds);
        Assert.Throws<InvalidOperationException>(() => Opt(("Channels:Telegram:Polling:ChannelIds", "not-a-guid")));
        Assert.Throws<InvalidOperationException>(() => Opt(("Channels:Telegram:Polling:TimeoutSeconds", "99")));
    }

    [Fact]
    public async Task service_start_throws_outside_development_and_with_mocks()
    {
        var opts = new TelegramPollingOptions { Enabled = true, ChannelIds = [Guid.NewGuid()] };
        TelegramPollingService Svc(string env, bool mocks) => new(
            new ConfigurationBuilder().AddInMemoryCollection([new("Channels:UseMocks", mocks ? "true" : "false")]).Build(),
            opts, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramPollingService>.Instance, new TestEnv(env));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Svc("Production", false).StartAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Svc("Development", true).StartAsync(default));
    }

    [Fact]
    public void useMocks_applies_to_sending_through_the_registered_adapter()
    {
        var cfg = new ConfigurationBuilder().Build();
        var sp = new ServiceCollection().AddSingleton<IConfiguration>(cfg).AddLogging().AddBeautyChannels(useMocks: true).BuildServiceProvider();
        Assert.IsType<MockTelegramAdapter>(sp.GetRequiredService<ChannelRegistry>().Find("telegram"));
    }

    [Fact]
    public async Task sendMessage_errors_do_not_contain_token_url_or_reply_text()
    {
        const string token = "999111:SECRETTOKENVALUE", text = "Ваш запис підтверджено ZZZ";
        foreach (var code in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadRequest })
        {
            var a = new TelegramAdapter(new HttpClient(new ChStubHandler(code)), new ChCreds(token));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => a.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", text), default));
            Assert.DoesNotContain(token, ex.ToString());
            Assert.DoesNotContain("ZZZ", ex.ToString());
        }
        var net = new TelegramAdapter(new HttpClient(new FakeTelegram().Throw(new HttpRequestException($"fail https://api.telegram.org/bot{token}/sendMessage"))), new ChCreds(token));
        var e2 = await Assert.ThrowsAsync<TransientChannelException>(() => net.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", text), default));
        Assert.DoesNotContain(token, e2.ToString());
    }
}

public sealed class TelegramPollingDbTests : IClassFixture<PostgresRlsFixture>
{
    private const string Token = "999111:SECRETTOKENVALUE";
    private readonly PostgresRlsFixture _db;
    public TelegramPollingDbTests(PostgresRlsFixture db) => _db = db;

    private sealed class Harness
    {
        public required ServiceProvider Sp; public required FakeTelegram Http; public required LogCapture Logs;
        public required SwitchQueue Queue; public required TelegramPoller Poller; public required TelegramPollingOptions Options;
    }

    private Harness Build(TelegramPollingOptions? opts = null)
    {
        Skip.If(_db.SkipReason is not null, _db.SkipReason);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = _db.AppConnectionString,
            ["Channels:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        }).Build();
        var http = new FakeTelegram(); var logs = new LogCapture(); var queue = new SwitchQueue();
        opts ??= new TelegramPollingOptions { ConflictBackoff = TimeSpan.FromMilliseconds(30), BaseBackoff = TimeSpan.FromMilliseconds(5), EmptyPollDelay = TimeSpan.FromMilliseconds(5), LeaseRetry = TimeSpan.FromMilliseconds(30) };
        var s = new ServiceCollection();
        s.AddSingleton<IConfiguration>(cfg).AddLogging(b => b.AddProvider(logs));
        s.AddSingleton(opts).AddSingleton(new TelegramPollingHttp(new HttpClient(http)));
        s.AddBeautyData().AddBeautyChannels(false);
        s.AddScoped<IChannelQueue>(_ => queue);
        var sp = s.BuildServiceProvider();
        return new Harness { Sp = sp, Http = http, Logs = logs, Queue = queue, Poller = sp.GetRequiredService<TelegramPoller>(), Options = opts };
    }

    private async Task<(Guid Tenant, Guid Channel)> SeedAsync(Harness h, string? token = Token, string webhookSecret = "whsecret", bool active = true)
    {
        var tenantId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'p-{tenantId:N}')", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        var protector = new AesGcmSecretProtector(h.Sp.GetRequiredService<IConfiguration>());
        await using var ctx = _db.CreateContext(tenantId);
        var ch = new Channel
        {
            Type = ChannelType.Telegram, Name = "bot", IsActive = active,
            CredentialsEncrypted = ChannelSecrets.Write(protector, new ChannelSecretSet(token, webhookSecret)),
        };
        ctx.Add(ch);
        await ctx.SaveChangesAsync();
        return (tenantId, ch.Id);
    }

    private static string Upd(long updateId, long msgId, string text = "hi", long chat = 100, string? extra = null, string chatType = "private", bool bot = false) =>
        $"{{\"update_id\":{updateId},{extra ?? ""}\"message\":{{\"message_id\":{msgId},\"from\":{{\"id\":{Math.Abs(chat)},\"is_bot\":{bot.ToString().ToLowerInvariant()},\"first_name\":\"Ann\"}}," +
        $"\"chat\":{{\"id\":{chat},\"type\":\"{chatType}\"}},\"date\":{1700000000 + msgId},\"text\":\"{text}\"}}}}";

    private async Task<List<Message>> MessagesAsync(Guid tenant)
    {
        await using var ctx = _db.CreateContext(tenant);
        return await ctx.Messages.AsNoTracking().OrderBy(m => m.SentAt).ThenBy(m => m.ExternalMessageId).ToListAsync();
    }

    private async Task<long> OffsetAsync(Guid tenant, Guid channel)
    {
        await using var ctx = _db.CreateContext(tenant);
        return (await ctx.ChannelPollStates.AsNoTracking().FirstAsync(s => s.ChannelId == channel)).LastUpdateId;
    }

    [SkippableFact]
    public async Task poll_stores_messages_in_order_and_saves_offset_after_the_batch()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        h.Http.Updates(Upd(10, 1, "one"), Upd(11, 2, "two"), Upd(12, 3, "three"));
        var r = await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal((PollStatus.Ok, 3, 3), (r.Status, r.Updates, r.Stored));
        Assert.Equal(["one", "two", "three"], (await MessagesAsync(t)).Select(m => m.Body));
        Assert.Equal(12, await OffsetAsync(t, ch));
        Assert.Equal(3, h.Queue.Signalled);
        await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal("13", h.Http.LastOffset);
        Assert.Contains("allowed_updates=%5B%22message%22%2C%22callback_query%22%5D", h.Http.GetUpdatesUrls.Last());
        await using var ctx = _db.CreateContext(t);
        Assert.All(await ctx.Conversations.AsNoTracking().ToListAsync(), c => Assert.Equal("100", c.ExternalChatId));
    }

    [SkippableFact]
    public async Task duplicate_update_and_redelivery_after_crash_create_no_duplicates()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        h.Queue.Fail = true; // збій після збереження, до offset
        h.Http.Updates(Upd(10, 1, "one"), Upd(11, 2, "two"));
        Assert.Equal(PollStatus.Transient, (await h.Poller.RunCycleAsync(ch, default)).Status);
        Assert.Equal(0, await OffsetAsync(t, ch)); // offset не просунувся
        Assert.Equal(2, (await MessagesAsync(t)).Count);

        h.Queue.Fail = false; // повторна видача тих самих update + дубль того ж update в пакеті
        h.Http.Updates(Upd(10, 1, "one"), Upd(11, 2, "two"), Upd(11, 2, "two"));
        var r = await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal((PollStatus.Ok, 0), (r.Status, r.Stored));
        Assert.Equal(2, (await MessagesAsync(t)).Count);
        Assert.Equal(11, await OffsetAsync(t, ch));
    }

    [SkippableFact]
    public async Task restart_continues_from_db_offset_and_second_consumer_is_blocked_by_lease()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        h.Http.Updates(Upd(20, 1));
        await h.Poller.RunCycleAsync(ch, default);

        var other = new TelegramPoller(h.Sp.GetRequiredService<IServiceScopeFactory>(), h.Sp.GetRequiredService<TelegramPollingHttp>(), h.Options,
            h.Sp.GetRequiredService<ILogger<TelegramPoller>>());
        Assert.Equal(PollStatus.LeaseHeld, (await other.RunCycleAsync(ch, default)).Status); // один poller на канал
        await h.Poller.ReleaseAsync(ch); // коректне зупинення знімає lease
        h.Http.Updates(Upd(20, 1)); // Telegram повторно видав старий update
        var r = await other.RunCycleAsync(ch, default);
        Assert.Equal("21", h.Http.LastOffset); // продовжує з БД
        Assert.Equal((PollStatus.Ok, 0), (r.Status, r.Stored));
        Assert.Single(await MessagesAsync(t));
    }

    [SkippableFact]
    public async Task groups_bots_and_non_message_updates_are_ignored_but_offset_advances()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        h.Http.Updates(
            Upd(30, 1, chat: -500, chatType: "supergroup"), Upd(31, 2, bot: true),
            """{"update_id":32,"edited_message":{"message_id":3,"from":{"id":1},"chat":{"id":1,"type":"private"},"text":"x"}}""",
            Upd(33, 4, "ok"));
        var r = await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal(1, r.Stored);
        Assert.Equal(["ok"], (await MessagesAsync(t)).Select(m => m.Body));
        Assert.Equal(33, await OffsetAsync(t, ch));
    }

    [SkippableFact]
    public async Task kinds_are_stored_text_command_unsupported()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        string Media(long u, long m, string key) =>
            $"{{\"update_id\":{u},\"message\":{{\"message_id\":{m},\"from\":{{\"id\":100,\"first_name\":\"A\"}},\"chat\":{{\"id\":100,\"type\":\"private\"}},\"date\":{1700000000 + m},{key}}}}}";
        h.Http.Updates(Upd(40, 1, "/start"), Media(41, 2, "\"sticker\":{}"), Media(42, 3, "\"voice\":{}"), Media(43, 4, "\"photo\":[{}]"),
            Media(44, 5, "\"photo\":[{}],\"caption\":\"look\""), Upd(45, 6, "text"));
        await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal([("command", "/start"), ("unsupported", "[sticker]"), ("unsupported", "[voice]"), ("unsupported", "[photo]"), ("text", "look"), ("text", "text")],
            (await MessagesAsync(t)).Select(m => (m.Kind, m.Body)));
    }

    [SkippableFact]
    public async Task tenant_comes_from_the_channel_not_from_the_payload_and_same_chat_id_in_two_tenants_stays_separate()
    {
        var h = Build(); var (ta, cha) = await SeedAsync(h); var (tb, chb) = await SeedAsync(h);
        var evil = $"\"tenant_id\":\"{tb}\",\"channel_id\":\"{chb}\",";
        h.Http.Updates(Upd(50, 1, "for A", extra: evil));
        await h.Poller.RunCycleAsync(cha, default);
        h.Http.Updates(Upd(50, 1, "for B"));
        await h.Poller.RunCycleAsync(chb, default);
        Assert.Equal(["for A"], (await MessagesAsync(ta)).Select(m => m.Body));
        Assert.Equal(["for B"], (await MessagesAsync(tb)).Select(m => m.Body));
    }

    [SkippableFact]
    public async Task webhook_and_polling_deliver_the_same_message_once()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        var update = Upd(60, 9, "same");
        using (var scope = h.Sp.CreateScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<WebhookIngestService>().HandleAsync("telegram", ch,
                new WebhookRequest(new Dictionary<string, string> { [TelegramAdapter.SecretHeader] = "whsecret" }), update, default);
            Assert.Equal((WebhookOutcome.Accepted, 1), (res.Outcome, res.NewMessages));
            var bad = await scope.ServiceProvider.GetRequiredService<WebhookIngestService>().HandleAsync("telegram", ch,
                new WebhookRequest(new Dictionary<string, string> { [TelegramAdapter.SecretHeader] = "wrong" }), Upd(61, 10), default);
            Assert.Equal(WebhookOutcome.InvalidSignature, bad.Outcome); // підпис вебхука як і раніше обов'язковий
        }
        h.Http.Updates(update);
        Assert.Equal(0, (await h.Poller.RunCycleAsync(ch, default)).Stored);
        Assert.Single(await MessagesAsync(t));
    }

    [SkippableFact]
    public async Task errors_409_401_429_and_network_never_leak_the_token()
    {
        var h = Build(); var (_, ch) = await SeedAsync(h);
        h.Http.Status(HttpStatusCode.Conflict).Status(HttpStatusCode.Unauthorized).Status(HttpStatusCode.NotFound)
            .Status(HttpStatusCode.TooManyRequests, "{\"ok\":false,\"parameters\":{\"retry_after\":7}}").Status(HttpStatusCode.BadGateway)
            .Throw(new HttpRequestException($"boom https://api.telegram.org/bot{Token}/getUpdates"))
            .Throw(new TaskCanceledException($"timeout bot{Token}"));
        var statuses = new List<PollStatus>(); TimeSpan? retry = null;
        for (var i = 0; i < 7; i++)
        {
            var r = await h.Poller.RunCycleAsync(ch, default);
            statuses.Add(r.Status); retry ??= r.RetryAfter;
        }
        Assert.Equal([PollStatus.Conflict, PollStatus.Unauthorized, PollStatus.Unauthorized, PollStatus.RateLimited, PollStatus.Transient, PollStatus.Transient, PollStatus.Transient], statuses);
        Assert.Equal(TimeSpan.FromSeconds(7), retry);
        Assert.DoesNotContain(h.Http.Urls, u => u.Contains("deleteWebhook")); // webhook без явного прапорця не чіпаємо
        Assert.NotEmpty(h.Logs.Lines);
        Assert.DoesNotContain(Token, h.Logs.All);
        Assert.DoesNotContain("SECRETTOKENVALUE", h.Logs.All);
        Assert.DoesNotContain("api.telegram.org/bot", h.Logs.All);
    }

    [SkippableFact]
    public async Task conflict_deletes_webhook_only_with_explicit_flag()
    {
        var h = Build(new TelegramPollingOptions { DeleteWebhookOnConflict = true }); var (_, ch) = await SeedAsync(h);
        h.Http.Status(HttpStatusCode.Conflict);
        var r = await h.Poller.RunCycleAsync(ch, default);
        Assert.Equal(PollStatus.Ok, r.Status);
        Assert.Contains(h.Http.Urls, u => u.Contains("/deleteWebhook"));
        Assert.DoesNotContain(Token, h.Logs.All);
    }

    [SkippableFact]
    public async Task service_survives_409_stops_channel_on_401_and_releases_lease_on_stop()
    {
        var h = Build(); var (t, ch) = await SeedAsync(h);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection([new("Channels:UseMocks", "false")]).Build();
        var opts = new TelegramPollingOptions { Enabled = true, ChannelIds = [ch], ConflictBackoff = TimeSpan.FromMilliseconds(20), BaseBackoff = TimeSpan.FromMilliseconds(5), EmptyPollDelay = TimeSpan.FromMilliseconds(5) };
        var svc = new TelegramPollingService(cfg, opts, h.Poller, h.Sp.GetRequiredService<ILogger<TelegramPollingService>>(), new TestEnv("Development"));

        h.Http.Status(HttpStatusCode.Conflict).Status(HttpStatusCode.Conflict).Updates(Upd(70, 1, "alive"));
        await svc.StartAsync(default);
        for (var i = 0; i < 100 && (await MessagesAsync(t)).Count == 0; i++) await Task.Delay(50);
        Assert.Single(await MessagesAsync(t)); // після двох 409 polling продовжив
        await svc.StopAsync(default);
        await using (var ctx = _db.CreateContext(t))
            Assert.Null((await ctx.ChannelPollStates.AsNoTracking().FirstAsync()).LeaseOwner); // коректне зупинення

        var h2 = Build(); var (_, ch2) = await SeedAsync(h2);
        var svc2 = new TelegramPollingService(cfg, new TelegramPollingOptions { Enabled = true, ChannelIds = [ch2] }, h2.Poller,
            h2.Sp.GetRequiredService<ILogger<TelegramPollingService>>(), new TestEnv("Development"));
        h2.Http.Status(HttpStatusCode.Unauthorized);
        await svc2.StartAsync(default);
        await svc2.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)); // 401: polling каналу зупинено, сервіс завершився
        Assert.Single(h2.Http.GetUpdatesUrls);
        Assert.DoesNotContain(Token, h2.Logs.All);
        await svc2.StopAsync(default);
    }

    [SkippableFact]
    public async Task disabled_service_makes_no_requests()
    {
        var h = Build(); var (_, ch) = await SeedAsync(h);
        var svc = new TelegramPollingService(new ConfigurationBuilder().Build(),
            new TelegramPollingOptions { Enabled = false, ChannelIds = [ch] }, h.Poller, h.Sp.GetRequiredService<ILogger<TelegramPollingService>>(), new TestEnv("Production"));
        await svc.StartAsync(default);
        await svc.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await svc.StopAsync(default);
        Assert.Empty(h.Http.Urls);
    }

    [SkippableFact]
    public async Task inactive_or_tokenless_channel_is_not_polled()
    {
        var h = Build();
        var (_, off) = await SeedAsync(h, active: false);
        var (_, noToken) = await SeedAsync(h, token: null);
        Assert.Equal(PollStatus.ChannelUnavailable, (await h.Poller.RunCycleAsync(off, default)).Status);
        Assert.Equal(PollStatus.ChannelUnavailable, (await h.Poller.RunCycleAsync(noToken, default)).Status);
        Assert.Equal(PollStatus.ChannelUnavailable, (await h.Poller.RunCycleAsync(Guid.NewGuid(), default)).Status);
        Assert.Empty(h.Http.Urls);
    }
}
