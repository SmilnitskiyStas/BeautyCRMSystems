using BeautyCrm.Application.Features.BeautyAuth;
using System.Text.Json;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Tests.Channels;
using BeautyCrm.Tests.Conversations;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Tests.Buttons;

/// <summary>
/// TASK-781 (§26.9.1, §26.14 сценарії 7-8 транспорт): verbs mc / co / i:mkt / i:mkoff, види marketing_consent / campaign_offer,
/// /marketing у меню, порядок перевірок §23.5, CAS, гонка, чужі токени, без побічних ефектів у транспорті. Доменні ефекти - TASK-782.
/// </summary>
public sealed class MarketingButtonsUnitTests
{
    private static readonly string Tok = new('A', 22);

    [Theory]
    [InlineData("mc:AAAAAAAAAAAAAAAAAAAAAA:0", true)]
    [InlineData("mc:AAAAAAAAAAAAAAAAAAAAAA:2", true)]
    [InlineData("co:AAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("i:mkt", true)]
    [InlineData("i:mkoff", true)]
    [InlineData("MC:AAAAAAAAAAAAAAAAAAAAAA:0", false)]
    [InlineData("mc:AAAAAAAAAAAAAAAAAAAAA:0", false)]    // токен 21
    [InlineData("co:AAAAAAAAAAAAAAAAAAAAAAA", false)]    // токен 23
    [InlineData("mc:AAAAAAAAAAAAAAAAAAAAAA:12", false)]  // index двозначний
    [InlineData("co:AAAAAAAAAAAAAAAAAAAAAA\n", false)]
    [InlineData("i:mkt:AAAAAAAAAAAAAAAAAAAAAA", false)]  // безстанова з токеном
    [InlineData("i:mkoffx", false)]
    [InlineData("i:MKOFF", false)]
    [InlineData("mkoff", false)]
    [InlineData("i:mk", false)]
    public void new_verbs_pass_both_callback_validators_only_in_the_exact_form(string data, bool valid)
    {
        Assert.Equal(valid, TelegramAdapter.IsValidCallbackData(data));
        Assert.Equal(valid, CallbackAddress.TryParse(data, out _));
    }

    [Fact]
    public void stateless_marketing_verbs_have_no_token_and_stateful_ones_round_trip()
    {
        Assert.True(CallbackAddress.TryParse("i:mkoff", out var off));
        Assert.Equal((ButtonVerbs.MarketingOff, true), (off.Verb, off.IsStateless));
        Assert.Equal("i:mkt", CallbackAddress.Build(ButtonVerbs.MarketingMenu, null));
        Assert.True(CallbackAddress.TryParse($"mc:{Tok}:1", out var mc));
        Assert.Equal((ButtonVerbs.MarketingConsent, Tok, 1), (mc.Verb, mc.Token, mc.Index));
        Assert.True(CallbackAddress.TryParse($"co:{Tok}", out var co));
        Assert.Equal((ButtonVerbs.CampaignOffer, (int?)null), (co.Verb, co.Index));
    }

    [Fact]
    public void verb_to_kind_index_cas_and_handoff_rules_follow_the_contract()
    {
        Assert.Contains(ButtonVerbs.MarketingMenu, ButtonVerbs.Stateless);
        Assert.Contains(ButtonVerbs.MarketingOff, ButtonVerbs.Stateless);
        Assert.DoesNotContain(ButtonVerbs.MarketingConsent, ButtonVerbs.Stateless);
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.MarketingConsent, "mc"));
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.CampaignOffer, "co"));
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.CampaignOffer, "i:mkoff"));
        foreach (var kind in KeyboardKinds.All.Where(k => k != KeyboardKinds.MarketingConsent)) Assert.False(ButtonVerbs.AllowedFor(kind, "mc"), kind);
        foreach (var kind in KeyboardKinds.All.Where(k => k != KeyboardKinds.CampaignOffer)) Assert.False(ButtonVerbs.AllowedFor(kind, "co"), kind);
        Assert.False(ButtonVerbs.AllowedFor(KeyboardKinds.MarketingConsent, "co"));
        Assert.True(ButtonVerbs.NeedsIndex(KeyboardKinds.MarketingConsent, "mc"));
        Assert.False(ButtonVerbs.NeedsIndex(KeyboardKinds.CampaignOffer, "co"));
        Assert.True(ButtonVerbs.Consuming("mc"));
        Assert.False(ButtonVerbs.Consuming("co"));
        Assert.All(new[] { "mc", "i:mkt", "i:mkoff" }, v => Assert.True(ButtonVerbs.AllowedInHandoff(v), v));
        Assert.False(ButtonVerbs.AllowedInHandoff("co"));
        Assert.True(KeyboardKinds.ReplacesPrevious(KeyboardKinds.MarketingConsent));
        Assert.False(KeyboardKinds.ReplacesPrevious(KeyboardKinds.CampaignOffer)); // по одній на одержувача
    }

    [Fact]
    public void marketing_options_are_read_from_config_with_safe_defaults()
    {
        static ChatButtonsOptions Read(params (string, string)[] kv) => ChatButtonsOptions.Read(
            new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build());
        var d = Read();
        Assert.Equal((false, 168), (d.MarketingEnabled, d.MarketingConsentTtlHours));
        var on = Read(("Marketing:Enabled", "true"), ("Marketing:ConsentKeyboardTtlHours", "24"));
        Assert.Equal((true, 24), (on.MarketingEnabled, on.MarketingConsentTtlHours));
        Assert.Equal(168, Read(("Marketing:ConsentKeyboardTtlHours", "0")).MarketingConsentTtlHours);
        Assert.Equal(168, Read(("Marketing:ConsentKeyboardTtlHours", "721")).MarketingConsentTtlHours);
    }

    [Fact]
    public void marketing_layouts_are_valid_and_carry_server_built_callback_data()
    {
        var created = new CreatedKeyboard(Guid.NewGuid(), Tok, DateTimeOffset.UtcNow.AddHours(1), []);
        var consent = MarketingKeyboards.ConsentOptions("mc-v1");
        var rows = MarketingKeyboards.ConsentRows(created, consent);
        Assert.Null(ChatKeyboardLayout.Validate(rows));
        Assert.Equal([$"mc:{Tok}:0", $"mc:{Tok}:1", $"mc:{Tok}:2"], rows.Select(r => r[0].Data).ToArray());
        Assert.Equal(["full", "selective", "none"], consent.Select(o => o.Level).ToArray());
        Assert.All(consent, o => Assert.Equal("mc-v1", o.TextVersion));

        foreach (var withCoupon in new[] { true, false })
        {
            var offer = MarketingKeyboards.OfferOptions(withCoupon);
            var offerRows = MarketingKeyboards.OfferRows(created, offer);
            Assert.Null(ChatKeyboardLayout.Validate(offerRows));
            Assert.Equal([$"co:{Tok}", "i:mkoff"], offerRows.Select(r => r[0].Data).ToArray());
            Assert.Equal(withCoupon ? "🎟 Записатися зі знижкою" : "📅 Записатися", offerRows[0][0].Label);
        }
    }

    [Fact]
    public void text_fallback_for_channels_without_buttons_has_unsubscribe_and_code_only_when_given()
    {
        var withCode = MarketingKeyboards.OfferFallback("ABCDE-FGHJK");
        Assert.Contains("ABCDE-FGHJK", withCode);
        Assert.Contains("не надсилати акції", withCode);
        var plain = MarketingKeyboards.OfferFallback(null);
        Assert.Contains("«Записатися»", plain);
        Assert.DoesNotContain("код", plain);
    }

    [Fact]
    public async Task setMyCommands_has_marketing_after_stop_for_default_and_uk_menu()
    {
        Assert.Equal(["start", "book", "my", "cancel", "stop", "marketing", "help"], TelegramAdapter.BotCommands.Select(c => c.Command).ToArray());
        var rec = new TgRecorder();
        await new TelegramAdapter(new HttpClient(rec), new ChCreds()).SyncCommandsAsync(default);
        var calls = rec.Of("setMyCommands");
        Assert.Equal(2, calls.Count);
        foreach (var c in calls)
        {
            using var d = JsonDocument.Parse(c.Body);
            var cmds = d.RootElement.GetProperty("commands").EnumerateArray().ToList();
            Assert.Equal("marketing", cmds[5].GetProperty("command").GetString());
            Assert.Equal("Новини й акції", cmds[5].GetProperty("description").GetString());
            Assert.All(cmds, x => Assert.True(x.GetProperty("description").GetString()!.Length <= 256));
        }
    }

    [Fact]
    public async Task marketing_keyboards_are_sent_as_inline_markup_and_a_callback_with_the_new_verb_is_parsed()
    {
        var created = new CreatedKeyboard(Guid.NewGuid(), Tok, DateTimeOffset.UtcNow.AddHours(1), []);
        var rows = MarketingKeyboards.OfferRows(created, MarketingKeyboards.OfferOptions(true));
        var layout = ChatActionLayout.TryParse(ChatKeyboardLayout.ToActionsJson(rows));
        Assert.NotNull(layout);
        var rec = new TgRecorder();
        var a = new TelegramAdapter(new HttpClient(rec), new ChCreds());
        await a.SendAsync(new OutboundMessage(Guid.NewGuid(), ChannelIds.Telegram, "42", "Акція", Actions: layout), default);
        using var d = JsonDocument.Parse(Assert.Single(rec.Of("sendMessage")).Body);
        var kb = d.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
        Assert.Equal([$"co:{Tok}", "i:mkoff"], kb.EnumerateArray().Select(r => r[0].GetProperty("callback_data").GetString()!).ToArray());

        var update = $$$"""{"update_id":5,"callback_query":{"id":"77","from":{"id":42,"is_bot":false},"message":{"message_id":9,"chat":{"id":42,"type":"private"}},"data":"co:{{{Tok}}}"}}""";
        var m = Assert.Single(await a.ParseInboundAsync(update));
        Assert.Equal($"co:{Tok}", m.CallbackData);
        var bad = update.Replace($"co:{Tok}", "co:short");
        Assert.Null(Assert.Single(await a.ParseInboundAsync(bad)).CallbackData);
    }
}

public sealed class MarketingButtonsDbTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public MarketingButtonsDbTests(PostgresRlsFixture db) => _db = db;

    private sealed class Env
    {
        public required ChatHarness H;
        public required ChatSeed S;
        public Guid Conv;
        public Guid ClientId;
        public Guid UserId;
        public Guid Tenant => S.TenantId;
    }

    private async Task<Env> NewAsync(bool marketing = true, bool buttons = true, bool withClient = true)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["ChatButtons:Enabled"] = buttons ? "true" : "false", ["Marketing:Enabled"] = marketing ? "true" : "false",
        };
        var h = ChatHarness.Create(_db, extraConfig: cfg);
        var s = await h.SeedAsync();
        var conv = (await h.IngestAsync(s, "Привіт")).Conversation!.Value;
        var e = new Env { H = h, S = s, Conv = conv };
        e.ClientId = withClient ? await SetClientAsync(e, conv, "Анна Секретна") : Guid.Empty;
        await using var ctx = _db.CreateContext(e.Tenant);
        var u = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@t.test", FullName = "Олена", PasswordHash = "x", Role = Roles.Admin };
        ctx.Add(u);
        await ctx.SaveChangesAsync();
        e.UserId = u.Id;
        return e;
    }

    private async Task<Guid> SetClientAsync(Env e, Guid conv, string name)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var c = new Client { FullName = name };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        await ctx.Conversations.Where(x => x.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClientId, (Guid?)c.Id));
        return c.Id;
    }

    private static async Task<T> Scope<T>(Env e, Func<IServiceProvider, Task<T>> f)
    {
        using var scope = e.H.Sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(e.Tenant);
        return await f(scope.ServiceProvider);
    }

    private async Task<Guid> OutboundAsync(Env e, Guid? conv = null, string ext = "500")
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var m = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv ?? e.Conv, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System,
            Body = "Хочете акції?", Status = "sent", ExternalMessageId = ext, SentAt = DateTimeOffset.UtcNow,
        };
        ctx.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task<Guid> InboundAsync(Env e, Guid? conv = null)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var m = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv ?? e.Conv, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client,
            Body = "[кнопка]", Kind = "callback", Status = "received", SentAt = DateTimeOffset.UtcNow,
        };
        ctx.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task<Guid> RecipientAsync(Env e, Guid? conv = null, Guid? client = null)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var camp = new Campaign
        {
            Name = "Новини", Type = "news", SegmentKind = "all_consented", Template = "Привіт", CouponMode = "none", CreatedByUserId = e.UserId,
        };
        ctx.Add(camp);
        await ctx.SaveChangesAsync();
        var r = new CampaignRecipient { CampaignId = camp.Id, ClientId = client ?? e.ClientId, ConversationId = conv ?? e.Conv, Status = "pending" };
        ctx.Add(r);
        await ctx.SaveChangesAsync();
        return r.Id;
    }

    private async Task<(string? Code, CreatedKeyboard? Kb)> CreateAsync(Env e, string kind, IReadOnlyList<KeyboardOption> options, Guid? recipient = null,
        Guid? conv = null, Guid? msg = null, DateTimeOffset? exp = null, string ext = "500")
    {
        var m = msg ?? await OutboundAsync(e, conv, ext);
        var r = await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>()
            .CreateAsync(new NewKeyboard(conv ?? e.Conv, m, kind, options, exp, CampaignRecipientId: recipient), default));
        return (r.Error?.Code, r.Value);
    }

    private async Task<CreatedKeyboard> ConsentKbAsync(Env e, Guid? conv = null, string ext = "500", string version = "mc-v1")
    {
        var (code, kb) = await CreateAsync(e, KeyboardKinds.MarketingConsent, MarketingKeyboards.ConsentOptions(version), conv: conv, ext: ext);
        Assert.Null(code);
        return kb!;
    }

    private async Task<CreatedKeyboard> OfferKbAsync(Env e, Guid recipient, bool withCoupon = true, string ext = "500")
    {
        var (code, kb) = await CreateAsync(e, KeyboardKinds.CampaignOffer, MarketingKeyboards.OfferOptions(withCoupon), recipient, ext: ext);
        Assert.Null(code);
        return kb!;
    }

    private Task<CallbackResolution> ResolveAsync(Env e, string? data, string? messageRef = "500", Guid? conv = null, Guid? inbound = null) =>
        Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().ResolveCallbackAsync(conv ?? e.Conv, data, messageRef, inbound, default));

    private Task<ButtonActionResult> ExecuteAsync(Env e, CallbackResolution res, Guid inbound) =>
        Scope(e, sp => sp.GetRequiredService<ChatButtonActions>().ExecuteAsync(new ButtonActionContext(e.Conv, inbound, res), default));

    private async Task<ChatKeyboard> LoadAsync(Env e, Guid id)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.ChatKeyboards.AsNoTracking().SingleAsync(k => k.Id == id);
    }

    private async Task SetHandoffAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        await ctx.Conversations.Where(c => c.Id == e.Conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiState, "handoff")
            .SetProperty(c => c.HandoffReason, "human_requested").SetProperty(c => c.HandoffAt, (DateTimeOffset?)DateTimeOffset.UtcNow));
    }

    // =============================================================== створення

    [SkippableFact]
    public async Task consent_keyboard_gets_the_configured_ttl_and_stores_level_and_text_version_in_options()
    {
        var e = await NewAsync();
        var kb = await ConsentKbAsync(e);
        var row = await LoadAsync(e, kb.Id);
        Assert.Equal(KeyboardKinds.MarketingConsent, row.Kind);
        Assert.InRange(row.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromHours(167), TimeSpan.FromHours(168.1));
        var options = KeyboardJson.TryParse(row.Options)!;
        Assert.Equal(["full", "selective", "none"], options.Select(o => o.Level).ToArray());
        Assert.All(options, o => Assert.Equal("mc-v1", o.TextVersion));
        Assert.Null(row.CampaignRecipientId);
    }

    [SkippableFact]
    public async Task a_new_consent_keyboard_revokes_the_previous_active_one()
    {
        var e = await NewAsync();
        var first = await ConsentKbAsync(e);
        var second = await ConsentKbAsync(e, ext: "501");
        Assert.Equal([first.Id], second.Revoked);
        Assert.Equal(KeyboardStatuses.Revoked, (await LoadAsync(e, first.Id)).Status);
        var old = await ResolveAsync(e, first.Data("mc", 0));
        Assert.Equal(CallbackOutcome.Revoked, old.Outcome);
        Assert.True(old.ClearMarkup);
    }

    [SkippableFact]
    public async Task create_rejects_malformed_marketing_keyboards()
    {
        var e = await NewAsync();
        var rec = await RecipientAsync(e);
        var ok = MarketingKeyboards.ConsentOptions("mc-v1");
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.MarketingConsent, ok.Take(2).ToList())).Code);
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.MarketingConsent, [ok[0], ok[0], ok[2]])).Code);          // два full
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.MarketingConsent, [ok[0], ok[1], ok[2] with { Level = "all" }])).Code);
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.MarketingConsent, ok.Select(o => o with { TextVersion = null }).ToList())).Code);
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.MarketingConsent, ok.Select(o => o with { TextVersion = new string('v', 33) }).ToList())).Code);
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.MarketingConsent, [ok[0], ok[1] with { TextVersion = "mc-v2" }, ok[2]])).Code);
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.BookingConfirm, ok)).Code);                              // mc не в чужому виді
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.MarketingConsent, ok, recipient: rec)).Code);             // recipient лише в campaign_offer
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.CampaignOffer, MarketingKeyboards.OfferOptions(true))).Code); // без recipient
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.CampaignOffer, [new("i:mkoff", "Не надсилати")], rec)).Code); // без co
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.CampaignOffer, [new("co", "A"), new("co", "B")], rec)).Code);
        Assert.Equal("keyboard_invalid", (await CreateAsync(e, KeyboardKinds.CampaignOffer, [new("co", "A", Level: "full")], rec)).Code);   // level лише в mc
    }

    [SkippableFact]
    public async Task campaign_offer_must_point_to_a_recipient_of_this_conversation_and_client()
    {
        var e = await NewAsync();
        var other = await NewAsyncSameTenant(e, "200", "Інна");
        var foreignConvRecipient = await RecipientAsync(e, conv: other.Conv, client: other.ClientId);
        Assert.Equal("recipient_not_found", (await CreateAsync(e, KeyboardKinds.CampaignOffer, MarketingKeyboards.OfferOptions(true), foreignConvRecipient)).Code);
        Assert.Equal("recipient_not_found", (await CreateAsync(e, KeyboardKinds.CampaignOffer, MarketingKeyboards.OfferOptions(true), Guid.NewGuid())).Code);
        // розмова збігається, а клієнт - ні
        var wrongClient = await RecipientAsync(e, conv: e.Conv, client: other.ClientId);
        Assert.Equal("recipient_not_found", (await CreateAsync(e, KeyboardKinds.CampaignOffer, MarketingKeyboards.OfferOptions(true), wrongClient)).Code);
        var good = await RecipientAsync(e);
        var kb = await OfferKbAsync(e, good);
        var row = await LoadAsync(e, kb.Id);
        Assert.Equal(good, row.CampaignRecipientId);
        Assert.InRange(row.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
    }

    private async Task<(Guid Conv, Guid ClientId)> NewAsyncSameTenant(Env e, string chat, string name)
    {
        var conv = (await e.H.IngestAsync(e.S, "Hi", chat: chat)).Conversation!.Value;
        return (conv, await SetClientAsync(e, conv, name));
    }

    // =============================================================== mc

    [SkippableFact]
    public async Task mc_resolves_with_level_and_text_version_from_the_db_and_is_consumed_once_without_domain_effects()
    {
        var e = await NewAsync();
        var kb = await ConsentKbAsync(e, version: "mc-v7");
        var inbound = await InboundAsync(e);
        var res = await ResolveAsync(e, kb.Data("mc", 1), inbound: inbound);
        Assert.True(res.IsValid);
        Assert.Equal((CallbackOutcome.Valid, "selective", "mc-v7"), (res.Outcome, res.ConsentLevel, res.ConsentTextVersion));
        Assert.Equal(1, res.OptionPosition);
        Assert.Equal(KeyboardStatuses.Active, (await LoadAsync(e, kb.Id)).Status); // Resolve нічого не пише

        var act = await ExecuteAsync(e, res, inbound);
        Assert.Equal((ButtonResultCodes.MarketingConsentChoice, "selective", "mc-v7"), (act.Code, act.ConsentLevel, act.ConsentTextVersion));
        var row = await LoadAsync(e, kb.Id);
        Assert.Equal((KeyboardStatuses.Used, (short?)1, (Guid?)inbound), (row.Status, row.UsedOption, row.UsedByMessageId));

        // повтор того самого натискання (падіння до відповіді) - Replay, ефект ідемпотентний
        var replay = await ResolveAsync(e, kb.Data("mc", 1), inbound: inbound);
        Assert.True(replay.Replay);
        Assert.Equal(ButtonResultCodes.MarketingConsentChoice, (await ExecuteAsync(e, replay, inbound)).Code);

        // інше натискання тієї ж клавіатури - used
        var again = await ResolveAsync(e, kb.Data("mc", 0), inbound: await InboundAsync(e));
        Assert.Equal((CallbackOutcome.Used, CallbackClass.Used), (again.Outcome, again.Class));
        // таблиць згоди транспорт не торкається
        await using var ctx = _db.CreateContext(e.Tenant);
        Assert.Equal(0, await ctx.MarketingConsents.CountAsync());
        Assert.Equal(0, await ctx.MarketingConsentEvents.CountAsync());
    }

    [SkippableFact]
    public async Task mc_race_two_parallel_presses_give_exactly_one_winner()
    {
        var e = await NewAsync();
        var kb = await ConsentKbAsync(e);
        var presses = Enumerable.Range(0, 2).Select(async i =>
        {
            var inbound = await InboundAsync(e);
            var res = await ResolveAsync(e, kb.Data("mc", i), inbound: inbound);
            return res.IsValid ? (await ExecuteAsync(e, res, inbound)).Code : res.Code;
        }).ToList();
        var codes = await Task.WhenAll(presses);
        Assert.Equal(1, codes.Count(c => c == ButtonResultCodes.MarketingConsentChoice));
        Assert.Equal(1, codes.Count(c => c is ButtonResultCodes.Used or "used"));
        Assert.Equal(KeyboardStatuses.Used, (await LoadAsync(e, kb.Id)).Status);
    }

    [SkippableFact]
    public async Task mc_rejects_wrong_index_missing_index_forged_and_foreign_tokens()
    {
        var e = await NewAsync();
        var kb = await ConsentKbAsync(e);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, kb.Data("mc", 3))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, kb.Data("mc"))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, kb.Data("co"))).Outcome);          // verb не з цього виду
        Assert.Equal(CallbackOutcome.UnknownToken, (await ResolveAsync(e, $"mc:{new string('Z', 22)}:0")).Outcome);
        Assert.Equal(CallbackOutcome.InvalidFormat, (await ResolveAsync(e, "mc:short:0")).Outcome);
        Assert.Equal(CallbackOutcome.InvalidFormat, (await ResolveAsync(e, null)).Outcome);
        // чужа розмова того ж tenant: клавіатуру не розкриваємо й не чіпаємо
        var other = await NewAsyncSameTenant(e, "200", "Інна");
        var foreign = await ResolveAsync(e, kb.Data("mc", 0), conv: other.Conv);
        Assert.Equal((CallbackOutcome.ForeignConversation, CallbackClass.Mismatch), (foreign.Outcome, foreign.Class));
        Assert.Null(foreign.Keyboard);
        Assert.Equal(KeyboardStatuses.Active, (await LoadAsync(e, kb.Id)).Status);
        // чужий tenant: токен під RLS не знайдено
        var e2 = await NewAsync();
        Assert.Equal(CallbackOutcome.UnknownToken, (await ResolveAsync(e2, kb.Data("mc", 0))).Outcome);
        // message_ref іншого повідомлення
        Assert.Equal(CallbackOutcome.RefMismatch, (await ResolveAsync(e, kb.Data("mc", 0), messageRef: "999")).Outcome);
    }

    [SkippableFact]
    public async Task mc_expires_after_ttl_and_old_keyboards_are_marked_expired()
    {
        var e = await NewAsync();
        var (code, kb) = await CreateAsync(e, KeyboardKinds.MarketingConsent, MarketingKeyboards.ConsentOptions("mc-v1"), exp: DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Null(code);
        Assert.True((await ResolveAsync(e, kb!.Data("mc", 0))).IsValid);
        e.H.Clock.Advance(TimeSpan.FromMinutes(6));
        var late = await ResolveAsync(e, kb.Data("mc", 0));
        Assert.Equal((CallbackOutcome.Expired, true), (late.Outcome, late.ClearMarkup));
        Assert.Equal(KeyboardStatuses.Expired, (await LoadAsync(e, kb.Id)).Status);
    }

    [SkippableFact]
    public async Task mc_works_during_handoff_but_not_with_marketing_or_buttons_disabled()
    {
        var e = await NewAsync();
        var kb = await ConsentKbAsync(e);
        await SetHandoffAsync(e);
        Assert.True((await ResolveAsync(e, kb.Data("mc", 2))).IsValid);

        var off = await NewAsync(marketing: false);
        var kbOff = await ConsentKbAsync(off);
        var r = await ResolveAsync(off, kbOff.Data("mc", 0));
        Assert.Equal((CallbackOutcome.Disabled, CallbackClass.Stale, true), (r.Outcome, r.Class, r.ClearMarkup));
        Assert.Equal(KeyboardStatuses.Active, (await LoadAsync(off, kbOff.Id)).Status);

        var noButtons = await NewAsync(buttons: false);
        var kbNb = await ConsentKbAsync(noButtons);
        Assert.Equal(CallbackOutcome.Disabled, (await ResolveAsync(noButtons, kbNb.Data("mc", 0))).Outcome);
    }

    // =============================================================== co

    [SkippableFact]
    public async Task co_returns_the_recipient_and_does_not_consume_the_keyboard()
    {
        var e = await NewAsync();
        var rec = await RecipientAsync(e);
        var kb = await OfferKbAsync(e, rec);
        for (var i = 0; i < 2; i++) // повторне натискання - знову дійсне (ідемпотентність - у домені)
        {
            var inbound = await InboundAsync(e);
            var res = await ResolveAsync(e, kb.Data("co"), inbound: inbound);
            Assert.True(res.IsValid);
            Assert.Equal(rec, res.CampaignRecipientId);
            var act = await ExecuteAsync(e, res, inbound);
            Assert.Equal((ButtonResultCodes.CampaignOffer, (Guid?)rec), (act.Code, act.CampaignRecipientId));
            Assert.Equal(KeyboardStatuses.Active, (await LoadAsync(e, kb.Id)).Status);
        }
        await using var ctx = _db.CreateContext(e.Tenant);
        Assert.Null((await ctx.CampaignRecipients.AsNoTracking().SingleAsync(r => r.Id == rec)).ClickedAt); // clicked_at ставить домен (TASK-782)
        Assert.Null((await ctx.Conversations.AsNoTracking().SingleAsync(c => c.Id == e.Conv)).OfferUntil);
    }

    [SkippableFact]
    public async Task co_is_foreign_when_the_recipient_belongs_to_another_conversation_or_client()
    {
        var e = await NewAsync();
        var other = await NewAsyncSameTenant(e, "200", "Інна");
        var rec = await RecipientAsync(e);
        var kb = await OfferKbAsync(e, rec);
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.CampaignRecipients.Where(r => r.Id == rec).ExecuteUpdateAsync(u => u.SetProperty(r => r.ConversationId, (Guid?)other.Conv));
        var res = await ResolveAsync(e, kb.Data("co"));
        Assert.Equal((CallbackOutcome.ForeignConversation, CallbackClass.Mismatch), (res.Outcome, res.Class));
        Assert.Null(res.CampaignRecipientId);

        // клієнт розмови змінився (злиття/підміна): одержувач уже не її
        var rec2 = await RecipientAsync(e);
        var kb2 = await OfferKbAsync(e, rec2, ext: "501");
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.Conversations.Where(c => c.Id == e.Conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.ClientId, (Guid?)other.ClientId));
        Assert.Equal(CallbackOutcome.ForeignConversation, (await ResolveAsync(e, kb2.Data("co"), messageRef: "501")).Outcome);
    }

    [SkippableFact]
    public async Task co_from_another_conversation_replay_of_old_or_forged_token_is_rejected()
    {
        var e = await NewAsync();
        var kb = await OfferKbAsync(e, await RecipientAsync(e));
        var other = await NewAsyncSameTenant(e, "200", "Інна");
        Assert.Equal(CallbackOutcome.ForeignConversation, (await ResolveAsync(e, kb.Data("co"), conv: other.Conv)).Outcome);
        Assert.Equal(CallbackOutcome.UnknownToken, (await ResolveAsync(e, $"co:{new string('Q', 22)}")).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, $"co:{kb.Token}:0")).Outcome); // index не потрібен
        // протермінована кнопка
        e.H.Clock.Advance(TimeSpan.FromDays(31));
        var expired = await ResolveAsync(e, kb.Data("co"));
        Assert.Equal((CallbackOutcome.Expired, CallbackClass.Expired), (expired.Outcome, expired.Class));
    }

    [SkippableFact]
    public async Task co_is_blocked_in_handoff_and_stale_when_marketing_is_disabled()
    {
        var e = await NewAsync();
        var kb = await OfferKbAsync(e, await RecipientAsync(e));
        await SetHandoffAsync(e);
        var h = await ResolveAsync(e, kb.Data("co"));
        Assert.Equal((CallbackOutcome.HandoffBlocked, CallbackClass.Handoff), (h.Outcome, h.Class));

        var off = await NewAsync(marketing: false);
        var kbOff = await OfferKbAsync(off, await RecipientAsync(off));
        var r = await ResolveAsync(off, kbOff.Data("co"));
        Assert.Equal((CallbackOutcome.Disabled, CallbackClass.Stale, "buttons_disabled"), (r.Outcome, r.Class, r.Code));
    }

    // =============================================================== i:mkt / i:mkoff

    [SkippableFact]
    public async Task mkt_is_a_stateless_menu_action_allowed_in_handoff_and_stale_when_marketing_is_disabled()
    {
        var e = await NewAsync();
        var inbound = await InboundAsync(e);
        var res = await ResolveAsync(e, "i:mkt", inbound: inbound);
        Assert.Equal(CallbackOutcome.Stateless, res.Outcome);
        Assert.Equal(ButtonResultCodes.MarketingMenu, (await ExecuteAsync(e, res, inbound)).Code);
        await SetHandoffAsync(e);
        Assert.Equal(CallbackOutcome.Stateless, (await ResolveAsync(e, "i:mkt")).Outcome);

        var off = await NewAsync(marketing: false);
        Assert.Equal(CallbackOutcome.Disabled, (await ResolveAsync(off, "i:mkt")).Outcome);
        var noButtons = await NewAsync(buttons: false);
        Assert.Equal(CallbackOutcome.Disabled, (await ResolveAsync(noButtons, "i:mkt")).Outcome);
    }

    [SkippableFact]
    public async Task mkoff_works_always_even_with_marketing_and_buttons_disabled_and_in_handoff()
    {
        var e = await NewAsync(marketing: false, buttons: false);
        var inbound = await InboundAsync(e);
        var res = await ResolveAsync(e, "i:mkoff", inbound: inbound);
        Assert.Equal((CallbackOutcome.Stateless, true), (res.Outcome, res.IsValid));
        var act = await ExecuteAsync(e, res, inbound);
        Assert.Equal((ButtonResultCodes.MarketingOff, (Guid?)null), (act.Code, act.CampaignRecipientId));
        await SetHandoffAsync(e);
        Assert.Equal(CallbackOutcome.Stateless, (await ResolveAsync(e, "i:mkoff")).Outcome);
        // рахується в частотному ліміті конвеєра як i:stop (шаблон-відповідь у межах ліміту), але Resolve сам не відмовляє
        await using var ctx = _db.CreateContext(e.Tenant);
        Assert.Equal(0, await ctx.MarketingConsents.CountAsync()); // запис згоди - домен
    }

    [SkippableFact]
    public async Task mkoff_under_a_campaign_message_exposes_the_recipient_only_for_this_conversation()
    {
        var e = await NewAsync(marketing: false);
        var rec = await RecipientAsync(e);
        var kb = await OfferKbAsync(e, rec, ext: "612");
        var inbound = await InboundAsync(e);
        var res = await ResolveAsync(e, "i:mkoff", messageRef: "612", inbound: inbound);
        Assert.Equal(rec, res.CampaignRecipientId);
        Assert.Equal(rec, (await ExecuteAsync(e, res, inbound)).CampaignRecipientId);
        Assert.Equal(KeyboardStatuses.Active, (await LoadAsync(e, kb.Id)).Status); // не споживається і не прибирається

        // повідомлення без кампанії або чужий message_ref - без прив'язки, але відписка діє
        Assert.Null((await ResolveAsync(e, "i:mkoff", messageRef: "999")).CampaignRecipientId);
        var other = await NewAsyncSameTenant(e, "200", "Інна");
        var foreign = await ResolveAsync(e, "i:mkoff", messageRef: "612", conv: other.Conv);
        Assert.Equal((CallbackOutcome.Stateless, (Guid?)null), (foreign.Outcome, foreign.CampaignRecipientId));
        // одержувач змінив належність - recipient не віддається
        var other2 = await NewAsyncSameTenant(e, "201", "Оля");
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.CampaignRecipients.Where(r => r.Id == rec).ExecuteUpdateAsync(u => u.SetProperty(r => r.ConversationId, (Guid?)other2.Conv));
        Assert.Null((await ResolveAsync(e, "i:mkoff", messageRef: "612")).CampaignRecipientId);
    }

    [SkippableFact]
    public async Task mkoff_still_works_without_a_client_on_the_conversation()
    {
        var e = await NewAsync(withClient: false);
        Assert.Equal(CallbackOutcome.Stateless, (await ResolveAsync(e, "i:mkoff")).Outcome);
        var kb = await ConsentKbAsync(e);
        Assert.True((await ResolveAsync(e, kb.Data("mc", 0))).IsValid); // клієнта немає - відмова T-marketing-no-client у домені, не в транспорті
    }

    // =============================================================== логи

    [SkippableFact]
    public async Task logs_carry_only_ids_and_codes_never_tokens_callback_data_levels_names_or_versions()
    {
        var e = await NewAsync();
        var rec = await RecipientAsync(e);
        var consent = await ConsentKbAsync(e, version: "mc-SECRETVER");
        var offer = await OfferKbAsync(e, rec, ext: "501");
        var other = await NewAsyncSameTenant(e, "200", "Інна");
        var inbound = await InboundAsync(e);
        var res = await ResolveAsync(e, consent.Data("mc", 0), inbound: inbound);
        await ExecuteAsync(e, res, inbound);
        await ResolveAsync(e, consent.Data("mc", 1));                            // used
        await ResolveAsync(e, offer.Data("co"), messageRef: "501");
        await ResolveAsync(e, offer.Data("co"), messageRef: "501", conv: other.Conv); // foreign
        await ResolveAsync(e, $"mc:{new string('Z', 22)}:0");                    // forged
        await ResolveAsync(e, "i:mkoff", messageRef: "501");
        await ResolveAsync(e, "i:mkt");
        var all = e.H.Logs.All;
        Assert.Contains("chat_callback", all);
        foreach (var secret in new[] { consent.Token, offer.Token, new string('Z', 22), "mc-SECRETVER", "Анна Секретна", "Інна", "selective" })
            Assert.DoesNotContain(secret, all);
        Assert.Contains(e.H.Logs.Lines, l => l.StartsWith("Warning") && l.Contains("result=foreign_conversation"));
    }
}
