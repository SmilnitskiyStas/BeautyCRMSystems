using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Tests.Channels;

internal sealed class FakeStaffTransport : IStaffTelegramTransport
{
    public readonly List<(Guid Channel, string Chat, string Text, ChatActionLayout? Actions)> Sent = [];
    public bool BotDown { get; set; }
    public bool FailSend { get; set; }

    public Task<string?> GetBotUsernameAsync(ResolvedChannel channel, CancellationToken ct) => Task.FromResult<string?>(BotDown ? null : "test_bot");

    public Task SendTextAsync(ResolvedChannel channel, string chatId, string text, ChatActionLayout? actions, CancellationToken ct)
    {
        if (FailSend) throw new TransientChannelException("down");
        lock (Sent) Sent.Add((channel.ChannelId, chatId, text, actions));
        return Task.CompletedTask;
    }

    public string? LastText(string chat) { lock (Sent) return Sent.LastOrDefault(s => s.Chat == chat).Text; }
    public int Count(string chat) { lock (Sent) return Sent.Count(s => s.Chat == chat); }
}

/// <summary>Детермінований годинник: час змінює лише тест.</summary>
internal sealed class StepClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public void Advance(TimeSpan d) => _now += d;
    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class ThrowingGate : IStaffChatGate
{
    public Task<StaffPartition> PartitionAsync(IReadOnlyList<InboundMessage> parsed, CancellationToken ct) => throw new InvalidOperationException("gate down");
    public Task HandleAsync(IReadOnlyList<InboundMessage> staff, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// TASK-890 (ADR-025 §12, §32.24.3/.4/.7/.8/.10 п.2-4) на реальній PostgreSQL (RLS, роль app): прив'язка Telegram власника, guard вхідних,
/// команди бота, ліміти, прапори, відкликання. Годинник - <see cref="StepClock"/>; Telegram - <see cref="FakeStaffTransport"/>.
/// </summary>
public sealed class StaffTelegramLinkDbTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public StaffTelegramLinkDbTests(PostgresRlsFixture db) => _db = db;
    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly IConfiguration Cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Channels:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(3, 32).Select(i => (byte)i).ToArray()),
    }).Build();
    private static readonly Regex TokenRx = new(@"start=inv_([A-Za-z0-9_-]{43})$");

    private async Task Admin(string sql)
    {
        await using var c = new NpgsqlConnection(_db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(_db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default! : (T)Convert.ChangeType(v, typeof(T));
    }

    private sealed class Tn
    {
        public Guid TenantId, OwnerId, ChannelId;
    }

    private async Task<Tn> SeedAsync(bool module = true)
    {
        var t = new Tn { TenantId = Guid.NewGuid(), OwnerId = Guid.NewGuid() };
        await Admin($@"INSERT INTO tenants (id, name, slug, modules) VALUES ('{t.TenantId}', 'T', 'stg-{t.TenantId:N}', '{(module ? "{beauty_inventory}" : "{}")}');
                       INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                       VALUES ('{t.OwnerId}', '{t.TenantId}', 'o@{t.TenantId:N}.test', 'Owner', 'x', 'owner');");
        t.ChannelId = await AddChannelAsync(t.TenantId);
        return t;
    }

    private async Task<Guid> AddChannelAsync(Guid tenantId, bool active = true)
    {
        await using var ctx = _db.CreateContext(tenantId);
        var ch = new Channel { Type = ChannelType.Telegram, Name = "SalonBot", IsActive = active };
        ctx.Add(ch);
        await ctx.SaveChangesAsync();
        return ch.Id;
    }

    private sealed class Rig(PostgresRlsFixture db, Tn t, StepClock clock, FakeStaffTransport transport, StaffTelegramLimits limits,
        StaffTelegramOptions options, LogCapture log, Guid? channelOverride = null)
    {
        public StepClock Clock => clock;
        public FakeStaffTransport Transport => transport;
        public StaffTelegramLimits Limits => limits;
        public LogCapture Log => log;
        public Guid ChannelId => channelOverride ?? t.ChannelId;
        public BeautyDbContext Ctx { get; } = db.CreateContext(t.TenantId);
        private readonly ChannelRequestContext _chan = new();
        public ResolvedChannel Resolved => new(t.TenantId, ChannelId, "telegram", new ChannelCredentials("tok", "sec"));

        public IDisposable Begin() => _chan.Begin(Resolved);
        public Actor OwnerActor => new(t.OwnerId, t.TenantId, Roles.Owner, null, AccessSet.None);

        private static StaffChatCodec Codec => new(Cfg, new AesGcmSecretProtector(Cfg));

        public StaffTelegramLinkService Service() => new(Ctx, new BeautyCrm.Infrastructure.Data.Tenancy.TenantContext(t.TenantId), clock, options, limits,
            new AesGcmSecretProtector(Cfg), Codec, transport, LoggerFactory.Create(b => b.AddProvider(log)).CreateLogger<StaffTelegramLinkService>());

        public StaffChatGate Gate() => new(Ctx, _chan, clock, options, limits, Codec, transport,
            LoggerFactory.Create(b => b.AddProvider(log)).CreateLogger<StaffChatGate>());

        public InboundIngestCore Core(IStaffChatGate? gate = null) => new(new EfChannelMessageRepository(Ctx, _chan), new SwitchQueue(), null, _chan, null, gate ?? Gate());

        public async Task<string> IssueTokenAsync()
        {
            var r = await Service().IssueAsync(OwnerActor, new IssueTelegramLinkRequest(true, "inv-tg-v1", null), default);
            Assert.True(r.IsOk, r.Error?.Code);
            return TokenRx.Match(r.Value!.DeepLink).Groups[1].Value;
        }

        public Task SayAsync(string chat, string text) => Core().IngestAsync([Msg(chat, text)], default);
        public Task PressAsync(string chat, string data) => Core().IngestAsync([Callback(chat, data)], default);
    }

    private static int _seq;
    private static InboundMessage Msg(string chat, string text, string kind = InboundKinds.Text) =>
        new("telegram", $"{chat}:{Interlocked.Increment(ref _seq)}", chat, chat, "Name", text, T0, kind);
    private static InboundMessage Callback(string chat, string data) =>
        new("telegram", $"{chat}:cb:{Interlocked.Increment(ref _seq)}", chat, chat, null, "[кнопка]", T0, InboundKinds.Callback)
        { CallbackData = data, CallbackQueryId = "q" + Interlocked.Increment(ref _seq) };

    private Rig NewRig(Tn t, bool enabled = true, StepClock? clock = null, StaffTelegramLimits? limits = null, Guid? channel = null)
    {
        var c = clock ?? new StepClock(T0);
        return new Rig(_db, t, c, new FakeStaffTransport(), limits ?? new StaffTelegramLimits(c),
            new StaffTelegramOptions { Enabled = enabled, L3Approved = true, IsDevelopment = false }, new LogCapture(), channel);
    }

    private Task<string?> Status(Tn t) =>
        Scalar<string?>($"SELECT status FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}' ORDER BY created_at DESC LIMIT 1");
    private Task<long> Count(string table, Guid tenant) => Scalar<long>($"SELECT count(*) FROM {table} WHERE tenant_id = '{tenant}'");

    private async Task<string> OpenLinkIdAsync(Tn t) =>
        (await Scalar<Guid>($"SELECT id FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}' AND status IN ('issued','claimed','active')")).ToString();

    // ================================================================== повний потік

    [SkippableFact]
    public async Task Full_flow_issue_claim_confirm_active_status_stop()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();

        // у БД лише SHA-256 від 32 байт токена
        Assert.Equal(32, await Scalar<long>($"SELECT octet_length(token_hash) FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.True(StaffLinkToken.TryHash(token, out var expected));
        Assert.Equal(Convert.ToHexString(expected), await Scalar<string>($"SELECT upper(encode(token_hash,'hex')) FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        var st = (await rig.Service().GetStatusAsync(rig.OwnerActor, default)).Value!;
        Assert.Equal("pending", st.State);

        await rig.SayAsync("555", "/start inv_" + token);
        Assert.Equal("claimed", await Status(t));
        var enc = await Scalar<string>($"SELECT chat_id_encrypted FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'");
        Assert.NotEqual("555", enc);
        Assert.Equal("555", new AesGcmSecretProtector(Cfg).Unprotect(enc)); // той самий формат, що секрети каналів
        Assert.Equal(32, await Scalar<long>($"SELECT octet_length(chat_id_hash) FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        var prompt = rig.Transport.Sent.Single(s => s.Chat == "555");
        Assert.Contains("Ви вмикаєте службові сповіщення", prompt.Text);
        Assert.Equal(2, prompt.Actions!.Rows[0].Count);
        var yes = prompt.Actions.Rows[0][0].Data;
        Assert.StartsWith("iy:", yes);
        Assert.True(TelegramAdapter.IsValidCallbackData(yes));
        Assert.Equal("awaitingConfirmation", (await rig.Service().GetStatusAsync(rig.OwnerActor, default)).Value!.State);

        await rig.PressAsync("555", yes);
        Assert.Equal("active", await Status(t));
        Assert.Null(await Scalar<string?>($"SELECT encode(token_hash,'hex') FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Contains("Готово", rig.Transport.LastText("555"));
        var active = (await rig.Service().GetStatusAsync(rig.OwnerActor, default)).Value!;
        Assert.Equal("active", active.State);
        Assert.NotNull(active.ActivatedAt);
        Assert.Equal("SalonBot", active.Channel!.Name);

        await rig.SayAsync("555", "/status");
        Assert.Contains("увімкнено; прив'язано 19.11.2026", rig.Transport.LastText("555"));
        await rig.SayAsync("555", "привіт");
        Assert.Contains("службовий чат", rig.Transport.LastText("555"));
        var before = rig.Transport.Count("555");
        await rig.SayAsync("555", "ще щось"); // підказка не частіше 1 разу на 10 хв
        Assert.Equal(before, rig.Transport.Count("555"));

        await rig.SayAsync("555", "/stop");
        Assert.Equal("revoked", await Status(t));
        Assert.Equal("bot_stop", await Scalar<string>($"SELECT revoke_reason FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Null(await Scalar<string?>($"SELECT chat_id_encrypted FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Null(await Scalar<string?>($"SELECT encode(chat_id_hash,'hex') FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        var after = (await rig.Service().GetStatusAsync(rig.OwnerActor, default)).Value!;
        Assert.Equal(("none", "bot_stop"), (after.State, after.LastRevoke!.Reason));

        // службове не створило розмов/повідомлень/клієнтів
        Assert.Equal(0, await Count("beauty_conversations", t.TenantId));
        Assert.Equal(0, await Count("beauty_messages", t.TenantId));
        Assert.Equal(0, await Count("beauty_clients", t.TenantId));
        var events = await Scalar<string>($"SELECT string_agg(event, ',' ORDER BY event) FROM beauty_staff_telegram_link_events WHERE tenant_id='{t.TenantId}'");
        Assert.Equal("activated,claimed,issued,revoked", events);
        // лог не містить chat id, токена, хешу
        var logs = rig.Log.All;
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain("555", logs);
        Assert.DoesNotContain(Convert.ToHexString(expected), logs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("staff_tg_link_issued", logs);
        Assert.Contains("staff_tg_link_activated", logs);
    }

    [SkippableFact]
    public async Task After_stop_chat_returns_to_client_flow()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        await rig.SayAsync("700", "/start inv_" + token);
        await rig.PressAsync("700", rig.Transport.Sent.Single().Actions!.Rows[0][0].Data);
        await rig.SayAsync("700", "/stop");
        Assert.Equal(0, await Count("beauty_messages", t.TenantId));
        await rig.SayAsync("700", "хочу записатися"); // більше не службовий
        Assert.Equal(1, await Count("beauty_messages", t.TenantId));
    }

    // ================================================================== токени

    [SkippableFact]
    public async Task Foreign_expired_used_and_other_channel_tokens_get_identical_reply()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var other = await SeedAsync(); // інший tenant
        var rig = NewRig(t);
        var otherRig = NewRig(other, limits: rig.Limits);
        string tokenA, tokenOther;
        using (rig.Begin()) tokenA = await rig.IssueTokenAsync();
        using (otherRig.Begin()) tokenOther = await otherRig.IssueTokenAsync();
        var ch2 = await AddChannelAsync(t.TenantId); // другий бот - після видачі (інакше channelId став би обов'язковим)
        var rig2 = NewRig(t, limits: rig.Limits, channel: ch2);
        var unknown = StaffLinkToken.Generate().Token;
        var replies = new List<string?>();

        using (rig.Begin())
        {
            await rig.SayAsync("1", "/start inv_" + unknown);
            replies.Add(rig.Transport.LastText("1"));
            await rig.SayAsync("2", "/start inv_" + tokenOther); // токен іншого tenant
            replies.Add(rig.Transport.LastText("2"));
            await rig.SayAsync("3", "/start inv_short");
            replies.Add(rig.Transport.LastText("3"));
        }
        using (rig2.Begin())
        {
            await rig2.SayAsync("4", "/start inv_" + tokenA); // інший канал того ж tenant
            replies.Add(rig2.Transport.LastText("4"));
        }
        Assert.Equal("issued", await Status(t));
        Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM beauty_staff_telegram_link_events WHERE tenant_id='{t.TenantId}' AND reason='channel_mismatch'"));

        // використаний іншим чатом
        using (rig.Begin())
        {
            await rig.SayAsync("5", "/start inv_" + tokenA);
            Assert.Equal("claimed", await Status(t));
            await rig.SayAsync("6", "/start inv_" + tokenA);
            replies.Add(rig.Transport.LastText("6"));
        }
        Assert.Equal(5, replies.Count);
        Assert.All(replies, r => Assert.Equal(StaffTelegramTexts.InvalidLink, r));
        Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM beauty_staff_telegram_link_events WHERE tenant_id='{t.TenantId}' AND reason='used'"));
        // чужий tenant не зачеплено
        Assert.Equal("issued", await Status(other));
        Assert.Equal(0, await Scalar<long>($"SELECT count(*) FROM beauty_staff_telegram_link_events WHERE tenant_id='{other.TenantId}' AND event='rejected'"));
    }

    [SkippableFact]
    public async Task Token_expires_exactly_at_ttl_boundary()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        await rig.SayAsync("11", "/start inv_" + token);
        Assert.Equal("claimed", await Status(t)); // 14:59 - ще дійсний

        var t2 = await SeedAsync();
        var rig2 = NewRig(t2);
        using var __ = rig2.Begin();
        var token2 = await rig2.IssueTokenAsync();
        rig2.Clock.Advance(TimeSpan.FromMinutes(15));
        await rig2.SayAsync("12", "/start inv_" + token2);
        Assert.Equal("issued", await Status(t2));
        Assert.Equal(StaffTelegramTexts.InvalidLink, rig2.Transport.LastText("12"));
        Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM beauty_staff_telegram_link_events WHERE tenant_id='{t2.TenantId}' AND reason='expired'"));
        // прострочений токен для UI = none
        Assert.Equal("none", (await rig2.Service().GetStatusAsync(rig2.OwnerActor, default)).Value!.State);
    }

    [SkippableFact]
    public async Task Repeat_start_same_chat_is_idempotent_other_chat_denied_and_foreign_confirm_rejected()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        await rig.SayAsync("21", "/start inv_" + token);
        await rig.SayAsync("21", "/start inv_" + token);
        Assert.Equal(2, rig.Transport.Count("21"));
        Assert.Contains("Ви вмикаєте", rig.Transport.LastText("21"));
        await rig.SayAsync("22", "/start inv_" + token);
        Assert.Equal(StaffTelegramTexts.InvalidLink, rig.Transport.LastText("22"));

        var yes = rig.Transport.Sent.First(s => s.Chat == "21").Actions!.Rows[0][0].Data;
        await rig.PressAsync("22", yes); // чужий чат натискає «Підтверджую»
        Assert.Equal("claimed", await Status(t));
        Assert.Equal(StaffTelegramTexts.InvalidLink, rig.Transport.LastText("22"));
        await rig.PressAsync("21", yes);
        Assert.Equal("active", await Status(t));
    }

    [SkippableFact]
    public async Task Decline_wipes_chat_data()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        await rig.SayAsync("31", "/start inv_" + token);
        await rig.PressAsync("31", rig.Transport.Sent.Single().Actions!.Rows[0][1].Data);
        Assert.Equal("revoked", await Status(t));
        Assert.Equal("declined", await Scalar<string>($"SELECT revoke_reason FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Null(await Scalar<string?>($"SELECT chat_id_encrypted FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Equal(StaffTelegramTexts.Declined, rig.Transport.LastText("31"));
    }

    [SkippableFact]
    public async Task Parallel_start_from_two_chats_claims_exactly_one()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        string token;
        using (rig.Begin()) token = await rig.IssueTokenAsync();
        var a = NewRig(t, limits: rig.Limits);
        var b = NewRig(t, limits: rig.Limits);
        await Task.WhenAll(
            Task.Run(async () => { using var _ = a.Begin(); await a.SayAsync("41", "/start inv_" + token); }),
            Task.Run(async () => { using var _ = b.Begin(); await b.SayAsync("42", "/start inv_" + token); }));
        Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}' AND status='claimed'"));
        var prompts = a.Transport.Sent.Count(s => s.Text.StartsWith("Ви вмикаєте")) + b.Transport.Sent.Count(s => s.Text.StartsWith("Ви вмикаєте"));
        Assert.Equal(1, prompts);
    }

    // ================================================================== API-сервіс

    [SkippableFact]
    public async Task Issue_replaces_open_link_and_refuses_when_active()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var first = await rig.IssueTokenAsync();
        var second = await rig.IssueTokenAsync();
        Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}' AND status='revoked' AND revoke_reason='replaced'"));
        await rig.SayAsync("51", "/start inv_" + first); // старий токен відкликано
        Assert.Equal(StaffTelegramTexts.InvalidLink, rig.Transport.LastText("51"));
        await rig.SayAsync("51", "/start inv_" + second);
        await rig.PressAsync("51", rig.Transport.Sent.Last().Actions!.Rows[0][0].Data);
        var r = await rig.Service().IssueAsync(rig.OwnerActor, new IssueTelegramLinkRequest(true, "inv-tg-v1", null), default);
        Assert.Equal("telegram_already_linked", r.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, r.Error.Kind);
    }

    [SkippableFact]
    public async Task Issue_validations_consent_channel_bot_role()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var svc = rig.Service();
        Task<Result<TelegramLinkIssuedDto>> Issue(IssueTelegramLinkRequest? r, Actor? a = null) => svc.IssueAsync(a ?? rig.OwnerActor, r, default);

        Assert.Equal("consent_required", (await Issue(null)).Error!.Code);
        Assert.Equal("consent_required", (await Issue(new(false, "inv-tg-v1", null))).Error!.Code);
        var old = (await Issue(new(true, "inv-tg-v0", null))).Error!;
        Assert.Equal("consent_version_outdated", old.Code);
        Assert.Equal("inv-tg-v1", old.Extras!["currentVersion"]);
        Assert.Equal("channel_not_found", (await Issue(new(true, "inv-tg-v1", Guid.NewGuid()))).Error!.Code);

        rig.Transport.BotDown = true;
        var down = (await Issue(new(true, "inv-tg-v1", null))).Error!;
        Assert.Equal("telegram_bot_unavailable", down.Code);
        Assert.Equal(ErrorKind.Unavailable, down.Kind);
        Assert.Equal(0, await Count("beauty_staff_telegram_links", t.TenantId)); // нічого не записано до getMe
        rig.Transport.BotDown = false;

        var second = await AddChannelAsync(t.TenantId);
        var req = (await Issue(new(true, "inv-tg-v1", null))).Error!;
        Assert.Equal("telegram_channel_required", req.Code);
        Assert.NotNull(req.Extras!["channels"]);
        Assert.True((await Issue(new(true, "inv-tg-v1", second))).IsOk);

        // не owner (роль з JWT) і не активний owner у БД
        Assert.Equal("forbidden_role", (await Issue(new(true, "inv-tg-v1", null), rig.OwnerActor with { Role = Roles.Admin })).Error!.Code);
        await Admin($"UPDATE users SET is_active = false WHERE id = '{t.OwnerId}'");
        Assert.Equal("forbidden_role", (await Issue(new(true, "inv-tg-v1", second))).Error!.Code);
    }

    [SkippableFact]
    public async Task Issue_without_active_telegram_channel_is_unavailable()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        await Admin($"UPDATE beauty_channels SET is_active = false WHERE tenant_id = '{t.TenantId}'");
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var r = await rig.Service().IssueAsync(rig.OwnerActor, new IssueTelegramLinkRequest(true, "inv-tg-v1", null), default);
        Assert.Equal("telegram_channel_unavailable", r.Error!.Code);
    }

    [SkippableFact]
    public async Task Issue_is_limited_to_five_per_hour_per_user()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        for (var i = 0; i < 5; i++) await rig.IssueTokenAsync();
        var r = await rig.Service().IssueAsync(rig.OwnerActor, new IssueTelegramLinkRequest(true, "inv-tg-v1", null), default);
        Assert.Equal(ErrorKind.TooManyRequests, r.Error!.Kind);
        Assert.Equal("rate_limited", r.Error.Code);
        rig.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.True((await rig.Service().IssueAsync(rig.OwnerActor, new IssueTelegramLinkRequest(true, "inv-tg-v1", null), default)).IsOk);
    }

    [SkippableFact]
    public async Task Unlink_is_idempotent_isolated_and_notifies_chat()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var other = await SeedAsync();
        var rig = NewRig(t);
        var otherRig = NewRig(other);
        using (otherRig.Begin()) await otherRig.IssueTokenAsync();
        using var _ = rig.Begin();
        Assert.True((await rig.Service().RevokeAsync(rig.OwnerActor, default)).IsOk); // нічого немає - 204
        var token = await rig.IssueTokenAsync();
        await rig.SayAsync("61", "/start inv_" + token);
        await rig.PressAsync("61", rig.Transport.Sent.Single().Actions!.Rows[0][0].Data);
        Assert.True((await rig.Service().RevokeAsync(rig.OwnerActor, default)).IsOk);
        Assert.True((await rig.Service().RevokeAsync(rig.OwnerActor, default)).IsOk);
        Assert.Equal("user_unlink", await Scalar<string>($"SELECT revoke_reason FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.True(StaffTelegramTexts.Unlinked == rig.Transport.LastText("61"), rig.Log.All);
        Assert.Null(await Scalar<string?>($"SELECT chat_id_encrypted FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Equal("issued", await Status(other)); // чужий tenant не зачеплено
    }

    [SkippableFact]
    public async Task Deactivation_revoke_wipes_chat_data_and_cancels_pending_notifications()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        await rig.SayAsync("71", "/start inv_" + token);
        await rig.PressAsync("71", rig.Transport.Sent.Single().Actions!.Rows[0][0].Data);
        var linkId = await OpenLinkIdAsync(t);
        await Admin($@"INSERT INTO beauty_staff_notifications (id, tenant_id, link_id, kind, body, status, attempts, next_attempt_at, dedupe_key, created_at, updated_at)
                       VALUES (gen_random_uuid(), '{t.TenantId}', '{linkId}', 'alert', 'x', 'pending', 0, now(), 'k1', now(), now())");
        var revoked = await StaffTelegramRevocation.RevokeForUserAsync(rig.Ctx, t.OwnerId, "user_deactivated", "system", null, rig.Clock.GetUtcNow(), default);
        Assert.Single(revoked);
        Assert.Equal("cancelled", await Scalar<string>($"SELECT status FROM beauty_staff_notifications WHERE tenant_id='{t.TenantId}'"));
        Assert.Null(await Scalar<string?>($"SELECT chat_id_encrypted FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Equal("user_deactivated", await Scalar<string>($"SELECT revoke_reason FROM beauty_staff_telegram_links WHERE tenant_id='{t.TenantId}'"));
        Assert.Empty(await StaffTelegramRevocation.RevokeForUserAsync(rig.Ctx, t.OwnerId, "user_deactivated", "system", null, rig.Clock.GetUtcNow(), default));
    }

    // ================================================================== ліміти спроб

    [SkippableFact]
    public async Task Claim_attempts_per_chat_are_limited_silently()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var bad = "/start inv_" + StaffLinkToken.Generate().Token;
        for (var i = 0; i < 10; i++) await rig.SayAsync("81", bad);
        Assert.Equal(10, rig.Transport.Count("81"));
        await rig.SayAsync("81", bad);
        Assert.Equal(10, rig.Transport.Count("81")); // 11-та - мовчки
        Assert.Equal(0, await Count("beauty_messages", t.TenantId)); // і не потрапила в клієнтський потік
        rig.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await rig.SayAsync("81", bad);
        Assert.Equal(11, rig.Transport.Count("81"));
    }

    [SkippableFact]
    public async Task Failed_claims_per_channel_are_limited_to_sixty()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        for (var i = 0; i < 60; i++) await rig.SayAsync("c" + i, "/start inv_" + StaffLinkToken.Generate().Token);
        await rig.SayAsync("good", "/start inv_" + token); // навіть правильний токен після 60 невдач ігнорується
        Assert.Equal(0, rig.Transport.Count("good"));
        Assert.Equal("issued", await Status(t));
        rig.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)); // вікно минуло (токен уже прострочений - видаємо новий)
        var fresh = await rig.IssueTokenAsync();
        await rig.SayAsync("good", "/start inv_" + fresh);
        Assert.Equal("claimed", await Status(t));
    }

    // ================================================================== guard

    [SkippableFact]
    public async Task Guard_keeps_staff_chat_out_of_client_flow_for_every_kind_and_passes_clients()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        await rig.SayAsync("91", "/start inv_" + token); // claimed

        var batch = new List<InboundMessage>
        {
            Msg("91", "купую", InboundKinds.Text), Msg("91", "/start", InboundKinds.Command), Msg("91", "[контакт]", InboundKinds.Contact),
            Msg("91", "[локація]", InboundKinds.Location), Msg("91", "[sticker]", InboundKinds.Unsupported), Callback("91", "i:book"),
            Msg("92", "/start inv_" + StaffLinkToken.Generate().Token), // неприв'язаний чат, службове за формою
            Msg("93", "Добрий день, хочу записатися"), // клієнт
        };
        var part = await rig.Gate().PartitionAsync(batch, default);
        Assert.Equal(7, part.Staff.Count);
        Assert.Single(part.Client);
        var fresh = await rig.Core().IngestAsync(batch, default);
        Assert.Single(fresh);
        Assert.Equal("93", fresh[0].ExternalConversationId);
        Assert.Equal(1, await Count("beauty_conversations", t.TenantId));
        Assert.Equal(1, await Count("beauty_messages", t.TenantId));
        Assert.Equal(0, await Scalar<long>($"SELECT count(*) FROM beauty_conversations WHERE tenant_id='{t.TenantId}' AND external_chat_id IN ('91','92')"));
    }

    [SkippableFact]
    public async Task Guard_ordinary_client_messages_are_stored_as_before_regression()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var fresh = await rig.Core().IngestAsync([Msg("c1", "/book", InboundKinds.Command), Msg("c1", "привіт"), Msg("c2", "/start")], default);
        Assert.Equal(3, fresh.Count);
        Assert.Equal(3, await Count("beauty_messages", t.TenantId));
        Assert.Equal(2, await Count("beauty_conversations", t.TenantId));
    }

    [SkippableFact]
    public async Task Guard_works_with_feature_flag_off_and_same_chat_in_other_bot_is_client()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using (rig.Begin())
        {
            var token = await rig.IssueTokenAsync();
            await rig.SayAsync("101", "/start inv_" + token);
        }
        var off = NewRig(t, enabled: false);
        var ch2 = await AddChannelAsync(t.TenantId);
        var rig2 = NewRig(t, enabled: false, channel: ch2);
        using (off.Begin())
        {
            var fresh = await off.Core().IngestAsync([Msg("101", "це службовий")], default);
            Assert.Empty(fresh);
            Assert.Equal(0, await Count("beauty_messages", t.TenantId));
        }
        using (rig2.Begin()) // той самий chat id в іншому боті - клієнт
            Assert.Single(await rig2.Core().IngestAsync([Msg("101", "я клієнт")], default));
    }

    [SkippableFact]
    public async Task Guard_failure_is_fail_closed_and_stores_nothing()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        // без контексту каналу реальний guard не пропускає (fail-closed)
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Gate().PartitionAsync([Msg("c9", "привіт")], default));
        using var _ = rig.Begin();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Core(new ThrowingGate()).IngestAsync([Msg("c9", "привіт")], default));
        Assert.Equal(0, await Count("beauty_messages", t.TenantId));
    }

    [SkippableFact]
    public async Task Handler_send_failure_does_not_break_batch_and_is_logged_without_content()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        using var _ = rig.Begin();
        var token = await rig.IssueTokenAsync();
        rig.Transport.FailSend = true;
        var fresh = await rig.Core().IngestAsync([Msg("111", "/start inv_" + token), Msg("c10", "клієнт")], default);
        Assert.Single(fresh); // клієнта збережено, службове не перекинуло виняток
        Assert.Equal("claimed", await Status(t));
        Assert.Contains("staff_tg_command_failed", rig.Log.All);
        Assert.DoesNotContain(token, rig.Log.All);
    }

    // ================================================================== прапори

    [SkippableFact]
    public async Task Flag_off_blocks_issue_and_claim_but_unlink_stop_status_and_guard_work()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var on = NewRig(t);
        using (on.Begin())
        {
            var token = await on.IssueTokenAsync();
            await on.SayAsync("121", "/start inv_" + token);
            await on.PressAsync("121", on.Transport.Sent.Single().Actions!.Rows[0][0].Data);
        }
        var off = NewRig(t, enabled: false);
        using (off.Begin())
        {
            var st = (await off.Service().GetStatusAsync(off.OwnerActor, default)).Value!;
            Assert.False(st.FeatureEnabled);
            Assert.Equal("active", st.State);
            var r = await off.Service().IssueAsync(off.OwnerActor, new IssueTelegramLinkRequest(true, "inv-tg-v1", null), default);
            Assert.Equal(("telegram_alerts_disabled", ErrorKind.Forbidden), (r.Error!.Code, r.Error.Kind));
            await off.SayAsync("121", "/status");
            Assert.Contains("вимкнено адміністратором платформи", off.Transport.LastText("121"));
            await off.SayAsync("121", "/stop");
            Assert.Equal("revoked", await Status(t));
        }

        // /start inv_ з вимкненим прапором: відмова з окремим текстом і rejected disabled; відв'язка працює
        var t2 = await SeedAsync();
        var on2 = NewRig(t2);
        string token2;
        using (on2.Begin()) token2 = await on2.IssueTokenAsync();
        var off2 = NewRig(t2, enabled: false);
        using (off2.Begin())
        {
            await off2.SayAsync("122", "/start inv_" + token2);
            Assert.Equal(StaffTelegramTexts.Disabled, off2.Transport.LastText("122"));
            Assert.Equal("issued", await Status(t2));
            Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM beauty_staff_telegram_link_events WHERE tenant_id='{t2.TenantId}' AND reason='disabled'"));
            Assert.True((await off2.Service().RevokeAsync(off2.OwnerActor, default)).IsOk);
            Assert.Equal("revoked", await Status(t2));
        }
    }

    [SkippableFact]
    public async Task Module_off_treats_claim_as_disabled_and_module_match_is_case_insensitive()
    {
        SkipIfNoDb();
        var t = await SeedAsync();
        var rig = NewRig(t);
        string token;
        using (rig.Begin()) token = await rig.IssueTokenAsync();
        await Admin($"UPDATE tenants SET modules = '{{}}' WHERE id = '{t.TenantId}'");
        using (rig.Begin())
        {
            await rig.SayAsync("131", "/start inv_" + token);
            Assert.Equal(StaffTelegramTexts.Disabled, rig.Transport.LastText("131"));
        }
        await Admin($"UPDATE tenants SET modules = '{{BEAUTY_Inventory}}' WHERE id = '{t.TenantId}'");
        using (rig.Begin())
        {
            await rig.SayAsync("131", "/start inv_" + token);
            Assert.Equal("claimed", await Status(t));
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" True ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("on", false)]
    [InlineData("yes", false)]
    [InlineData("false", false)]
    public void Flag_is_on_only_for_literal_true(string? value, bool expected) => Assert.Equal(expected, StaffTelegramOptions.IsOn(value));

    private static IConfiguration FlagCfg(string? enabled, string? l3) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [StaffTelegramOptions.EnabledKey] = enabled, [StaffTelegramOptions.L3Key] = l3 }).Build();

    [Fact]
    public void Startup_guard_requires_l3_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => StaffTelegramOptions.Read(FlagCfg("true", null), new FakeEnv("Production")).ValidateForHost());
        Assert.Throws<InvalidOperationException>(() => StaffTelegramOptions.Read(FlagCfg("true", "0"), new FakeEnv("Production")).ValidateForHost());
        StaffTelegramOptions.Read(FlagCfg("true", "true"), new FakeEnv("Production")).ValidateForHost();
        StaffTelegramOptions.Read(FlagCfg("true", null), new FakeEnv("Development")).ValidateForHost();
        StaffTelegramOptions.Read(FlagCfg(null, null), new FakeEnv("Production")).ValidateForHost(); // вимкнено - ок
        Assert.False(StaffTelegramOptions.Read(FlagCfg(null, null), new FakeEnv("Development")).Active);
        Assert.True(StaffTelegramOptions.Read(FlagCfg("true", null), new FakeEnv("Development")).Active);
        Assert.False(StaffTelegramOptions.Read(FlagCfg("true", null), new FakeEnv("Staging")).Active);
        Assert.Throws<InvalidOperationException>(() =>
            new StaffTelegramStartupGuard(StaffTelegramOptions.Read(FlagCfg("true", null), new FakeEnv("Production"))).StartAsync(default).GetAwaiter().GetResult());
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "x";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // ================================================================== токен і коди

    [Fact]
    public void Token_codec_roundtrip_canonical_and_link_id()
    {
        var (token, hash) = StaffLinkToken.Generate();
        Assert.Equal(43, token.Length);
        Assert.True(StaffLinkToken.TryHash(token, out var h2));
        Assert.Equal(hash, h2);
        Assert.False(StaffLinkToken.TryHash(token[..42], out _));
        Assert.False(StaffLinkToken.TryHash(token + "A", out _));
        Assert.False(StaffLinkToken.TryHash(token[..42] + "+", out _));
        Assert.False(StaffLinkToken.TryHash(null, out _));
        var id = Guid.NewGuid();
        var s = StaffLinkToken.LinkId22(id);
        Assert.Equal(22, s.Length);
        Assert.True(StaffLinkToken.TryParseLinkId22(s, out var back));
        Assert.Equal(id, back);
        Assert.NotEqual(token, StaffLinkToken.Generate().Token);
    }

    [Fact]
    public void Chat_hash_is_keyed_per_channel_and_deterministic()
    {
        var codec = new StaffChatCodec(Cfg, new AesGcmSecretProtector(Cfg));
        var c1 = Guid.NewGuid();
        Assert.Equal(codec.Hash(c1, "42"), codec.Hash(c1, "42"));
        Assert.NotEqual(codec.Hash(c1, "42"), codec.Hash(Guid.NewGuid(), "42"));
        Assert.NotEqual(codec.Hash(c1, "42"), codec.Hash(c1, "43"));
        var otherKey = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Channels:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(9, 32).Select(i => (byte)i).ToArray()) }).Build();
        Assert.NotEqual(codec.Hash(c1, "42"), new StaffChatCodec(otherKey, new AesGcmSecretProtector(otherKey)).Hash(c1, "42"));
        Assert.Equal("42", codec.Decrypt(codec.Encrypt("42")));
    }

    [Fact]
    public void Service_callbacks_are_valid_callback_data()
    {
        var id = StaffLinkToken.LinkId22(Guid.NewGuid());
        Assert.True(TelegramAdapter.IsValidCallbackData("iy:" + id));
        Assert.True(TelegramAdapter.IsValidCallbackData("in:" + id));
        Assert.False(TelegramAdapter.IsValidCallbackData("ix:" + id));
    }

    [Fact]
    public void Limits_slide_with_clock()
    {
        var clock = new StepClock(T0);
        var l = new StaffTelegramLimits(clock);
        Assert.True(l.TryHit("k", 2, TimeSpan.FromMinutes(1)));
        Assert.True(l.TryHit("k", 2, TimeSpan.FromMinutes(1)));
        Assert.False(l.TryHit("k", 2, TimeSpan.FromMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(l.TryHit("k", 2, TimeSpan.FromMinutes(1)));
        Assert.Equal(1, l.Count("k", TimeSpan.FromMinutes(1)));
    }
}
