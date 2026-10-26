using System.Net;
using System.Text;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Application.Features;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Auth;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Channels;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Tests.Conversations;

/// <summary>Fake Telegram Bot API: лише sendMessage. Зберігає (токен, chat_id, текст); збої скриптуються. Реальної мережі немає.</summary>
internal sealed class FakeBotApi : HttpMessageHandler
{
    public sealed record Sent(string Token, string ChatId, string Text, string? Markup = null, long MessageId = 0);

    /// <summary>editMessageReplyMarkup (прибирання кнопок): chat_id, message_id і чи порожня клавіатура.</summary>
    public sealed record Edit(string ChatId, string MessageId, bool Empty);

    private readonly object _gate = new();
    private readonly List<Sent> _sent = [];
    private readonly List<string> _urls = [];
    private readonly List<Edit> _edits = [];
    private long _nextMessageId = 500;
    /// <summary>Скільки наступних editMessageReplyMarkup повертають 5xx (sweeper-повтор, TASK-753).</summary>
    public int FailEdits;
    public IReadOnlyList<Edit> Edits { get { lock (_gate) return _edits.ToList(); } }
    public int CountUrls(string method) { lock (_gate) return _urls.Count(u => u.EndsWith("/" + method, StringComparison.Ordinal)); }
    public int FailNext;
    /// <summary>Статус відповіді для наступних <see cref="FailNext"/> відправок (TASK-733: 400 = постійна помилка, 5xx = тимчасова).</summary>
    public HttpStatusCode FailStatus = HttpStatusCode.InternalServerError;
    /// <summary>Тіло помилкової відповіді Telegram (TASK-739: {"ok":false,"description":"Bad Request: chat not found"}).</summary>
    public string? FailBody;

    public IReadOnlyList<Sent> Messages { get { lock (_gate) return _sent.ToList(); } }
    public string UrlsText { get { lock (_gate) return string.Join('\n', _urls); } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var url = req.RequestUri!.OriginalString;
        var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
        lock (_gate)
        {
            _urls.Add(url);
            if (FailNext > 0)
            {
                FailNext--;
                return new HttpResponseMessage(FailStatus)
                    { Content = FailBody is null ? null : new StringContent(FailBody, Encoding.UTF8, "application/json") };
            }
            using var doc = JsonDocument.Parse(body);
            var token = url.Split("/bot")[1].Split('/')[0];
            if (url.EndsWith("/editMessageReplyMarkup", StringComparison.Ordinal))
            {
                if (FailEdits > 0) { FailEdits--; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }
                var root = doc.RootElement;
                _edits.Add(new Edit(root.GetProperty("chat_id").ToString(), root.GetProperty("message_id").ToString(),
                    root.GetProperty("reply_markup").GetProperty("inline_keyboard").GetArrayLength() == 0));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json") };
            }
            if (!doc.RootElement.TryGetProperty("text", out var textEl))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json") };
            var id = ++_nextMessageId;
            var markup = doc.RootElement.TryGetProperty("reply_markup", out var rm) ? rm.GetRawText() : null;
            _sent.Add(new Sent(token, doc.RootElement.GetProperty("chat_id").ToString(), textEl.GetString()!, markup, id));
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"{{\"ok\":true,\"result\":{{\"message_id\":{id}}}}}", Encoding.UTF8, "application/json") };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") };
    }
}

internal sealed class TestClock : TimeProvider
{
    private long _offsetTicks;
    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
}

internal sealed record ChatSeed(Guid TenantId, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ChannelId, DateTimeOffset Start, string Token);

/// <summary>
/// Повний стек конвеєра розмов (реальний PostgreSQL з RLS і роллю без BYPASSRLS): справжні ingest, сервіси запису, TenantAiClient,
/// BeautyAssistant; замінено лише провайдера ШІ (MockAiProvider) і HTTP до Telegram (FakeBotApi). Реальних викликів немає.
/// </summary>
internal sealed class ChatHarness
{
    private const string Hours =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    public const string PlatformKey = "test-platform-key-NOTREAL-7f3a";
    /// <summary>Типові опції тестів: воркер увімкнено, sweeper бере будь-які `received` без очікування.</summary>
    public static ChatAssistantOptions Opts(bool enabled = true) => new() { Enabled = enabled, SweepMinAge = TimeSpan.Zero };

    public static readonly string BotToken = "700111:BOTTOKENSECRETVALUE";

    private static long _msg = 1000;

    public required PostgresRlsFixture Db;
    public required ServiceProvider Sp;
    public required MockAiProvider Ai;
    public required FakeBotApi Bot;
    public required TestClock Clock;
    public required LogCapture Logs;
    public required ChatAssistantOptions Options;
    public MockChannelAdapter? MockChannel;

    public ConversationWorker Worker => Sp.GetRequiredService<ConversationWorker>();
    public ConversationSignals Signals => Sp.GetRequiredService<ConversationSignals>();

    public static ChatHarness Create(PostgresRlsFixture db, ChatAssistantOptions? options = null, int maxActivePerClient = 3,
        bool mockChannel = false, IDictionary<string, string?>? extraConfig = null, Action<IServiceCollection>? configure = null)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var config = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = db.AppConnectionString,
            ["Channels:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Ai:Platform:EnabledByDefault"] = "true",
            ["Ai:Platform:DefaultMonthlyTokenQuota"] = "1000000000",
            ["Ai:Platform:DefaultMonthlyRequestQuota"] = "1000000",
            ["Ai:Platform:Keys:anthropic"] = PlatformKey,
            ["Ai:RateLimit:PerTenantPerMinute"] = "100000",
            // keyring для власних ключів tenant у тестах (TASK-733); платформні сценарії його не використовують
            ["Ai:Secrets:ActiveKeyId"] = "k1",
            ["Ai:Secrets:Keys:k1"] = Convert.ToBase64String(Enumerable.Range(50, 32).Select(i => (byte)i).ToArray()),
        };
        if (extraConfig is not null) foreach (var kv in extraConfig) config[kv.Key] = kv.Value;
        var opts = options ?? Opts();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        var logs = new LogCapture();
        var bot = new FakeBotApi();
        var clock = new TestClock();
        var ai = new MockAiProvider("anthropic");
        var s = new ServiceCollection();
        s.AddSingleton<IConfiguration>(cfg).AddLogging(b => b.AddProvider(logs));
        s.AddSingleton<TimeProvider>(clock);
        s.AddSingleton(opts);
        s.AddBeautyData().AddBeautyChannels(false).AddBeautyAi().AddBeautyApplication().AddBeautyAuthData();
        s.AddBeautyPublicBookingApplication(new PublicBookingOptions { TokenKey = new byte[32], MaxActiveBookingsPerPhone = maxActivePerClient })
            .AddBeautyPublicBookingData();
        s.AddBeautyChatBookingApplication(new ChatBookingOptions()).AddBeautyChatBookingData();
        s.RemoveAll<IAiProvider>();
        s.AddSingleton<IAiProvider>(ai);
        s.RemoveAll<IChannelAdapter>();
        MockChannelAdapter? mock = null;
        if (mockChannel)
        {
            mock = new MockTelegramAdapter();
            s.AddSingleton<IChannelAdapter>(mock);
        }
        else
            s.AddSingleton<IChannelAdapter>(sp => new TelegramAdapter(new HttpClient(bot), sp.GetRequiredService<IChannelCredentialsProvider>()));
        configure?.Invoke(s);
        return new ChatHarness
        {
            Db = db, Sp = s.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), Ai = ai, Bot = bot, Clock = clock,
            Logs = logs, Options = opts, MockChannel = mock,
        };
    }

    // ------------------------------------------------------------ дані

    public async Task<ChatSeed> SeedAsync(string? channelSettings = "{\"assistant\":{\"enabled\":true}}", string? aiMode = null,
        Action<AiSettingsRow>? aiTweak = null, bool aiSettingsRow = false)
    {
        var tenantId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(Db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'cv-{tenantId:N}')", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        var protector = Sp.GetRequiredService<ISecretProtector>();
        await using var ctx = Db.CreateContext(tenantId);
        var loc = new Location { Name = "Салон Центр", Address = "вул. Хрещатик 1", Timezone = "Europe/Kyiv" };
        var sp = new Specialist { FullName = "Марина" };
        var svc = new Service { Name = "Стрижка", DurationMinutes = 60 };
        ctx.AddRange(loc, sp, svc);
        await ctx.SaveChangesAsync();
        var ch = new Channel
        {
            Type = ChannelType.Telegram, Name = "bot", LocationId = loc.Id, IsActive = true, Settings = channelSettings,
            CredentialsEncrypted = ChannelSecrets.Write(protector, new ChannelSecretSet(BotToken, "whsecret")),
        };
        ctx.Add(ch);
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = loc.Id, WorkingHours = Hours });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svc.Id });
        ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = 500m });
        if (aiMode is not null || aiSettingsRow || aiTweak is not null)
        {
            var row = new AiSettingsRow
            {
                KeySource = "platform", ChatModel = "claude-haiku-5-5", AnalyticsModel = "claude-sonnet-5-5", Mode = aiMode ?? "confirm",
                PlatformEnabled = true, PlatformMonthlyTokenQuota = 1_000_000_000, PlatformMonthlyRequestQuota = 1_000_000,
            };
            aiTweak?.Invoke(row);
            ctx.AiSettings.Add(row);
        }
        await ctx.SaveChangesAsync();

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var local = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(3).AddHours(10), DateTimeKind.Unspecified);
        return new ChatSeed(tenantId, loc.Id, sp.Id, svc.Id, ch.Id, new DateTimeOffset(local, tz.GetUtcOffset(local)), BotToken);
    }

    // ------------------------------------------------------------ ingest і хід

    /// <summary>Вхідне повідомлення тим самим шляхом, що webhook/polling: InboundIngestCore -> збереження -> сигнал черги.</summary>
    public async Task<(Guid? Conversation, int Fresh)> IngestAsync(ChatSeed s, string text, long? messageId = null, string chat = "100",
        string kind = InboundKinds.Text, string? name = "Anna", DateTimeOffset? at = null)
    {
        var id = messageId ?? Interlocked.Increment(ref _msg);
        using var scope = Sp.CreateScope();
        var p = scope.ServiceProvider;
        p.GetRequiredService<TenantContext>().SetTenant(s.TenantId);
        using var _ = p.GetRequiredService<ChannelRequestContext>().Begin(
            new ResolvedChannel(s.TenantId, s.ChannelId, "telegram", new ChannelCredentials(s.Token, "whsecret")));
        var fresh = await p.GetRequiredService<InboundIngestCore>().IngestAsync(
            [new InboundMessage("telegram", $"{chat}:{id}", chat, chat, name, text, at ?? DateTimeOffset.UtcNow, kind)], default);
        return (fresh.Count > 0 ? fresh[0].ConversationId : await ConversationIdAsync(s, chat), fresh.Count);
    }

    /// <summary>Натискання кнопки тим самим шляхом, що webhook/polling (callback_query -> InboundMessage kind=callback). Повертає id розмови.</summary>
    public async Task<(Guid Conversation, int Fresh)> IngestCallbackAsync(ChatSeed s, string? data, string? messageRef, string chat = "100", string? queryId = null)
    {
        var id = queryId ?? Interlocked.Increment(ref _msg).ToString();
        using var scope = Sp.CreateScope();
        var p = scope.ServiceProvider;
        p.GetRequiredService<TenantContext>().SetTenant(s.TenantId);
        using var _ = p.GetRequiredService<ChannelRequestContext>().Begin(
            new ResolvedChannel(s.TenantId, s.ChannelId, "telegram", new ChannelCredentials(s.Token, "whsecret")));
        var fresh = await p.GetRequiredService<InboundIngestCore>().IngestAsync(
            [new InboundMessage("telegram", $"{chat}:cb:{id}", chat, chat, "Anna", "[кнопка]", DateTimeOffset.UtcNow, InboundKinds.Callback)
                { CallbackQueryId = id, CallbackData = data, CallbackMessageRef = messageRef }], default);
        return (await ConversationIdAsync(s, chat), fresh.Count);
    }

    /// <summary>Клієнт натискає кнопку -> хід розмови.</summary>
    public async Task<Guid> PressAsync(ChatSeed s, string? data, string? messageRef, string chat = "100")
    {
        var (conv, _) = await IngestCallbackAsync(s, data, messageRef, chat);
        await TurnAsync(s.TenantId, conv);
        return conv;
    }

    public async Task<Guid> ConversationIdAsync(ChatSeed s, string chat = "100")
    {
        await using var ctx = Db.CreateContext(s.TenantId);
        return await ctx.Conversations.AsNoTracking().Where(c => c.ChannelId == s.ChannelId && c.ExternalChatId == chat).Select(c => c.Id).SingleAsync();
    }

    public async Task<TurnRunResult> TurnAsync(Guid tenantId, Guid conversation, CancellationToken ct = default)
    {
        using var scope = Sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ConversationTurnProcessor>().RunAsync(tenantId, conversation, ct);
    }

    /// <summary>Клієнт пише -> хід розмови. Повертає id розмови.</summary>
    public async Task<Guid> SayAsync(ChatSeed s, string text, string chat = "100", string kind = InboundKinds.Text)
    {
        var (conv, _) = await IngestAsync(s, text, chat: chat, kind: kind);
        await TurnAsync(s.TenantId, conv!.Value);
        return conv.Value;
    }

    // ------------------------------------------------------------ ШІ-скрипти

    public static AiResponse Text(string t) => new([new AiTextBlock(t)]);

    public static AiResponse Tool(string name, object input, string id = "tu1") =>
        new([new AiToolUseBlock(id, name, JsonSerializer.SerializeToElement(input))]);

    public static AiResponse PrepareCall(ChatSeed s, DateTimeOffset? start = null) =>
        Tool("prepare_booking", new { serviceId = s.ServiceId, specialistId = s.SpecialistId, startsAt = (start ?? s.Start).ToString("o") });

    /// <summary>Модель готує запис (prepare_booking) і пише вступ; конвеєр додає підсумок від коду.</summary>
    public async Task<Guid> ShowSummaryAsync(ChatSeed s, string chat = "100", DateTimeOffset? start = null, string intro = "Пропоную цей час.")
    {
        Ai.Enqueue(PrepareCall(s, start), Text(intro));
        return await SayAsync(s, "Хочу на стрижку о 10", chat);
    }

    // ------------------------------------------------------------ читання стану

    public async Task<List<Message>> MessagesAsync(ChatSeed s)
    {
        await using var ctx = Db.CreateContext(s.TenantId);
        return await ctx.Messages.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
    }

    public async Task<List<Message>> OutboundAsync(ChatSeed s) =>
        (await MessagesAsync(s)).Where(m => m.Direction == MessageDirection.Outbound).ToList();

    public async Task<Conversation> ConversationAsync(ChatSeed s, string chat = "100")
    {
        await using var ctx = Db.CreateContext(s.TenantId);
        return await ctx.Conversations.AsNoTracking().SingleAsync(c => c.ExternalChatId == chat && c.ChannelId == s.ChannelId);
    }

    public async Task<List<ChatBookingProposal>> ProposalsAsync(ChatSeed s)
    {
        await using var ctx = Db.CreateContext(s.TenantId);
        return await ctx.ChatBookingProposals.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
    }

    public async Task<List<Appointment>> AppointmentsAsync(ChatSeed s)
    {
        await using var ctx = Db.CreateContext(s.TenantId);
        return await ctx.Appointments.AsNoTracking().ToListAsync();
    }

    public async Task<List<AiAction>> ActionsAsync(ChatSeed s)
    {
        await using var ctx = Db.CreateContext(s.TenantId);
        return await ctx.AiActions.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync();
    }

    public async Task<List<AiUsageRow>> UsageAsync(ChatSeed s)
    {
        await using var ctx = Db.CreateContext(s.TenantId);
        return await ctx.AiUsage.AsNoTracking().ToListAsync();
    }

    public static string ToDbStatus(Message m) => m.Status;
}
