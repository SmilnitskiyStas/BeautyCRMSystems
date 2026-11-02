using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Tests.Conversations;

/// <summary>Чисті функції TASK-782: розбір команд і кодів, шаблони, розкладка згоди. Без БД.</summary>
public sealed class ChatMarketingUnitTests
{
    [Theory]
    [InlineData("/marketing", ChatCommand.Marketing)]
    [InlineData("/MARKETING@BeautyBot", ChatCommand.Marketing)]
    [InlineData("/marketing now", ChatCommand.Marketing)]
    public void marketing_command_is_parsed(string text, ChatCommand expected) => Assert.Equal(expected, ChatCommands.Parse(text));

    [Theory]
    [InlineData("акції", true)]
    [InlineData("  Розсилки! ", true)]
    [InlineData("Новини й акції", true)]
    [InlineData("які в вас акції", false)]
    [InlineData("не надсилати акції", false)]
    public void menu_aliases_are_exact_matches(string text, bool menu) => Assert.Equal(menu, ChatMarketingParser.IsMenuText(text));

    [Theory]
    [InlineData("не надсилати акції", true)]
    [InlineData("Без акцій.", true)]
    [InlineData("не надсилайте мені акції", false)]
    [InlineData("акції", false)]
    public void off_aliases_are_exact_matches(string text, bool off) => Assert.Equal(off, ChatMarketingParser.IsOffText(text));

    [Theory]
    [InlineData("ABCDE-FGHJK", true, false, true)]
    [InlineData("abcde fghjk", true, false, true)]
    [InlineData("код: ABCDE-FGHJK", true, true, true)]
    [InlineData("Промокод SALE2030", true, true, false)]
    [InlineData("купон:SALE2030", true, true, false)]
    [InlineData("SALE2030", true, false, false)]   // не персональний формат: потрібен збіг хешу в БД
    [InlineData("0501112233", true, false, false)]  // лише цифри без префікса - це номер телефону, не код
    [InlineData("код 0501112233", true, true, true)]
    [InlineData("hi", false, false, false)]         // < 4 символів
    [InlineData("Привіт", false, false, false)]
    [InlineData("ABCDE_FGHJK", false, false, false)]
    public void code_shape_follows_the_contract(string text, bool parsed, bool prefixed, bool personal)
    {
        var shape = ChatMarketingParser.ParseCode(text);
        Assert.Equal(parsed, shape is not null);
        if (shape is null) return;
        Assert.Equal((prefixed, personal), (shape.Value.Prefixed, shape.Value.PersonalFormat));
    }

    [Fact]
    public void consent_keyboard_is_valid_and_carries_levels_and_text_version()
    {
        var plan = new ChatKeyboardBuilder(new ChatButtonsOptions { Enabled = true }).MarketingConsent("mc-v1");
        Assert.True(plan.IsValid());
        Assert.Equal(["full", "selective", "none"], plan.Options.Select(o => o.Level).ToArray());
        Assert.All(plan.Options, o => Assert.Equal("mc-v1", o.TextVersion));
        Assert.Equal(KeyboardKinds.MarketingConsent, plan.Kind);
    }

    [Fact]
    public void templates_follow_the_contract_texts()
    {
        Assert.Equal("Зараз: повна інформація. Оберіть:", ChatTemplates.MarketingMenu("full", true, true));
        Assert.StartsWith("Зараз: розсилки вимкнено", ChatTemplates.MarketingMenu("none", true, true));
        Assert.Contains("ви ще не обирали", ChatTemplates.MarketingMenu("none", false, true));
        Assert.Contains("з 9:00 до 20:00", ChatTemplates.MarketingSet("full"));
        Assert.Contains("якими ви користуєтесь", ChatTemplates.MarketingSet("selective"));
        Assert.Equal(ChatTemplates.MarketingOff, ChatTemplates.MarketingSet("none"));
        Assert.Equal("Знижку 15% на Манікюр закріплено за вами до 30.11.2026. Напишіть, на коли бажаєте записатися.",
            ChatTemplates.CampaignBookStart("15%", "Манікюр", "30.11.2026"));
        Assert.Equal("усі послуги", ChatTemplates.ServicesText([]));
        Assert.Equal("A, B, C та ще 2", ChatTemplates.ServicesText(["A", "B", "C", "D", "E"]));
        Assert.Equal("100 грн", ChatTemplates.DiscountText("fixed", 100m, "грн"));
        Assert.Equal("15%", ChatTemplates.DiscountText("percent", 15m, "грн"));
        // кінець виключний: 01.12 00:00 за Києвом -> показується 30.11
        Assert.Equal("30.11.2026", ChatTemplates.UntilDate(new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero), "Europe/Kyiv"));
    }

    [Fact]
    public void summary_text_shows_coupon_price_and_not_better_or_not_applicable_lines()
    {
        static ChatBookingSummaryDto Dto(decimal final, string? promo, string? outcome) =>
            new("Стрижка", "Марина", "Салон", "вул. 1", new DateOnly(2030, 1, 4), new TimeOnly(14, 30), "Europe/Kyiv", 60, 500m, final, promo, outcome);
        var applied = ChatSummaryText.Format(Dto(400m, "Осінь", "applied"), 15, "грн", buttons: true);
        Assert.Contains("Ціна: 400 грн (замість 500 грн, купон «Осінь»)", applied);
        Assert.DoesNotContain("акція «Осінь»", applied);
        var notBetter = ChatSummaryText.Format(Dto(350m, "Будні", "not_better"), 15, "грн", buttons: true);
        Assert.Contains("акція «Будні»", notBetter);
        Assert.Contains("Купон не застосовано: діє вигідніша акція.", notBetter);
        var notApplicable = ChatSummaryText.Format(Dto(500m, null, "not_applicable"), 15, "грн", buttons: true);
        Assert.Contains("Купон не діє на цю послугу.", notApplicable);
        Assert.DoesNotContain("Купон", ChatSummaryText.Format(Dto(500m, null, null), 15, "грн", buttons: true));
    }

    [Fact]
    public void chat_rules_forbid_inventing_coupons_and_policy_guard_ignores_marketing_wording()
    {
        Assert.Contains("Не обіцяй знижок, купонів і кодів, яких немає у фактах чи tools; не вигадуй і не повторюй кодів.", PromptComposer.ChatRules);
        foreach (var text in new[]
                 {
                     "Розсилки акцій — лише за згодою клієнта; керувати можна командою /marketing.",
                     "У вас активна знижка 30% на Стрижка до 30.11.2026; ціну з купоном я покажу в підсумку.",
                     "Щоб не отримувати акції, напишіть «не надсилати акції» або натисніть кнопку під повідомленням.",
                 })
            Assert.Null(PolicyClaimGuard.Find(text, ChatPolicyFacts.Default));
    }
}

/// <summary>
/// TASK-782 (§26.9, §26.14): розмовна логіка згоди на маркетинг і купона. Реальний PostgreSQL з RLS, mock-провайдер ШІ, fake Telegram.
/// Реальних викликів немає.
/// </summary>
public sealed class ChatMarketingPipelineTests : IClassFixture<PostgresRlsFixture>
{
    private const string Code = "ABCDE-FGHJK";
    private readonly PostgresRlsFixture _db;
    public ChatMarketingPipelineTests(PostgresRlsFixture db) => _db = db;

    private sealed class Env
    {
        public required ChatHarness H;
        public required ChatSeed S;
        public Guid Conv, ClientId, UserId;
        public Guid Tenant => S.TenantId;
    }

    private sealed class Offer
    {
        public Guid Promotion, Campaign, Coupon, Recipient, Message;
        public CreatedKeyboard Keyboard = null!;
    }

    private static ChatAssistantOptions RoomyOpts() => new()
    {
        Enabled = true, SweepMinAge = TimeSpan.Zero, MaxTemplateRepliesPerConversationPerMinute = 100, MaxTurnsPerConversationPerMinute = 100,
    };

    private async Task<Env> NewAsync(bool marketing = true, string? aiMode = null, bool withClient = true, bool preference = false,
        ChatAssistantOptions? opts = null, bool buttons = true)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["ChatButtons:Enabled"] = buttons ? "true" : "false", ["Marketing:Enabled"] = marketing ? "true" : "false",
        };
        var h = ChatHarness.Create(_db, options: opts, extraConfig: cfg);
        var s = await h.SeedAsync(aiMode: aiMode);
        var conv = (await h.IngestAsync(s, "Привіт")).Conversation!.Value;
        var e = new Env { H = h, S = s, Conv = conv };
        await using var ctx = _db.CreateContext(e.Tenant);
        await ctx.Messages.Where(m => m.ConversationId == conv).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, "processed"));
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@t.test", FullName = "Олена", PasswordHash = "x", Role = Roles.Admin };
        ctx.Add(user);
        e.UserId = user.Id;
        if (withClient)
        {
            var c = new Client { FullName = "Анна Секретна" };
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            e.ClientId = c.Id;
            await ctx.Conversations.Where(x => x.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClientId, (Guid?)c.Id));
            if (preference)
                ctx.Add(new ClientContactPreference
                {
                    ClientId = c.Id, Method = "none", OffsetsMinutes = [], Source = "chat", TelegramConversationId = conv,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
        }
        await ctx.SaveChangesAsync();
        return e;
    }

    private static List<(string Text, string Data)> Kb(FakeBotApi.Sent m)
    {
        if (m.Markup is null) return [];
        using var doc = JsonDocument.Parse(m.Markup);
        return doc.RootElement.GetProperty("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray())
            .Select(b => (b.GetProperty("text").GetString()!, b.GetProperty("callback_data").GetString()!)).ToList();
    }

    private static string Data(FakeBotApi.Sent m, string labelPart) => Kb(m).First(b => b.Text.Contains(labelPart)).Data;

    private static string Ref(FakeBotApi.Sent m) => m.MessageId.ToString();

    private static Task<Guid> PressAsync(Env e, FakeBotApi.Sent m, string labelPart) => e.H.PressAsync(e.S, Data(m, labelPart), Ref(m));

    private static Task SayCommandAsync(Env e, string text) => e.H.SayAsync(e.S, text, kind: InboundKinds.Command);

    private static string Texts(Env e) => string.Join("\n---\n", e.H.Bot.Messages.Select(m => m.Text));

    private async Task<MarketingConsent?> ConsentAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.MarketingConsents.AsNoTracking().SingleOrDefaultAsync(c => c.ClientId == e.ClientId && c.Channel == "telegram");
    }

    private async Task<List<MarketingConsentEvent>> EventsAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.MarketingConsentEvents.AsNoTracking().Where(c => c.ClientId == e.ClientId).OrderBy(c => c.CreatedAt).ToListAsync();
    }

    private async Task SetConsentRowAsync(Env e, string level, string source = "chat")
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        ctx.MarketingConsents.Add(new MarketingConsent
        {
            TenantId = e.Tenant, ClientId = e.ClientId, Channel = "telegram", Level = level, Source = source, TextVersion = source == "chat" && level != "none" ? "mc-v1" : null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<Conversation> ConvAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.Conversations.AsNoTracking().SingleAsync(c => c.Id == e.Conv);
    }

    private async Task SetHandoffAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        await ctx.Conversations.Where(c => c.Id == e.Conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiState, "handoff")
            .SetProperty(c => c.HandoffReason, "human_requested").SetProperty(c => c.HandoffAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
            .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(12)));
    }

    // ------------------------------------------------------------ дані кампанії

    /// <summary>Акція code_only + кампанія + персональний купон клієнта + одержувач + повідомлення кампанії з кнопками [co] [i:mkoff].</summary>
    private async Task<Offer> OfferAsync(Env e, decimal percent = 30m, string code = Code, Guid? clientId = null, Guid? conv = null, string ext = "500",
        decimal? publicPercent = null, bool keyboard = true)
    {
        var o = new Offer();
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            var promo = new Promotion { Name = "Осінній купон", DiscountType = DiscountType.Percent, DiscountValue = percent, IsActive = true, Access = "code_only" };
            ctx.Add(promo);
            await ctx.SaveChangesAsync();
            ctx.Add(new PromotionService { PromotionId = promo.Id, ServiceId = e.S.ServiceId });
            if (publicPercent is { } pp)
            {
                var pub = new Promotion { Name = "Будні", DiscountType = DiscountType.Percent, DiscountValue = pp, IsActive = true, Access = "public" };
                ctx.Add(pub);
                await ctx.SaveChangesAsync();
                ctx.Add(new PromotionService { PromotionId = pub.Id, ServiceId = e.S.ServiceId });
            }
            var camp = new Campaign
            {
                Name = "Листопад", Type = "promo", PromotionId = promo.Id, SegmentKind = "all_consented", Template = "Привіт", CouponMode = "personal",
                CouponValidTo = DateTimeOffset.UtcNow.AddDays(10), CreatedByUserId = e.UserId,
            };
            ctx.Add(camp);
            await ctx.SaveChangesAsync();
            var coupon = new Coupon
            {
                Kind = "personal", PromotionId = promo.Id, CampaignId = camp.Id, ClientId = clientId ?? e.ClientId, CodeHash = CouponCodes.Hash(code),
                CodeHint = CouponCodes.Hint(code), ValidFrom = DateTimeOffset.UtcNow.AddHours(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(10), MaxUses = 1,
            };
            ctx.Add(coupon);
            var msg = new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv ?? e.Conv, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System,
                Body = "Акція для вас", Status = "sent", ExternalMessageId = ext, SentAt = DateTimeOffset.UtcNow,
            };
            ctx.Add(msg);
            await ctx.SaveChangesAsync();
            var rec = new CampaignRecipient
            {
                CampaignId = camp.Id, ClientId = clientId ?? e.ClientId, ConversationId = conv ?? e.Conv, Status = "sent", MessageId = msg.Id, CouponId = coupon.Id,
            };
            ctx.Add(rec);
            await ctx.SaveChangesAsync();
            (o.Promotion, o.Campaign, o.Coupon, o.Recipient, o.Message) = (promo.Id, camp.Id, coupon.Id, rec.Id, msg.Id);
        }
        if (!keyboard) return o;
        using var scope = e.H.Sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(e.Tenant);
        var created = await scope.ServiceProvider.GetRequiredService<ChatKeyboardService>().CreateAsync(
            new NewKeyboard(conv ?? e.Conv, o.Message, KeyboardKinds.CampaignOffer, MarketingKeyboards.OfferOptions(true), null, CampaignRecipientId: o.Recipient), default);
        Assert.Null(created.Error);
        o.Keyboard = created.Value!;
        return o;
    }

    private Task<Guid> PressCoAsync(Env e, Offer o, string ext = "500") => e.H.PressAsync(e.S, o.Keyboard.Data("co"), ext);

    private async Task<Coupon> CouponAsync(Env e, Offer o)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.Coupons.AsNoTracking().SingleAsync(c => c.Id == o.Coupon);
    }

    private async Task<CampaignRecipient> RecipientAsync(Env e, Offer o)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.CampaignRecipients.AsNoTracking().SingleAsync(c => c.Id == o.Recipient);
    }

    // ======================================================================== /marketing, меню, mc

    [SkippableTheory]
    [InlineData(0, "full", "Готово: надсилатимемо акції, знижки й новини")]
    [InlineData(1, "selective", "Готово: надсилатимемо лише акції та знижки")]
    [InlineData(2, "none", "Добре, акцій і новин не надсилатимемо")]
    public async Task marketing_command_shows_state_and_consent_buttons_and_mc_saves_each_level_with_text_version(int index, string level, string reply)
    {
        var e = await NewAsync();
        await SayCommandAsync(e, "/marketing");
        var menu = e.H.Bot.Messages.Last();
        Assert.Equal(ChatTemplates.MarketingMenu("none", false, true), menu.Text);
        Assert.Equal(3, Kb(menu).Count);
        Assert.Equal(["📰 Повна інформація", "🏷 Лише акції на мої послуги", "🔕 Не надсилати"], Kb(menu).Select(b => b.Text).ToArray());

        await e.H.PressAsync(e.S, Kb(menu)[index].Data, Ref(menu));
        Assert.StartsWith(reply, e.H.Bot.Messages.Last().Text);
        var row = await ConsentAsync(e);
        Assert.Equal((level, "chat"), (row!.Level, row.Source));
        Assert.Equal("mc-v1", row.TextVersion);
        var ev = Assert.Single(await EventsAsync(e));
        Assert.Equal((null, level, "chat", "button", "mc-v1"), (ev.LevelFrom, ev.LevelTo, ev.Source, ev.Via, ev.TextVersion));
        Assert.Equal(e.Conv, ev.ConversationId);
        Assert.True(e.H.Bot.Edits.Count > 0, "кнопки згоди мають зникнути");
        var kb = await KeyboardsAsync(e);
        Assert.Equal(KeyboardStatuses.Used, kb.Single(k => k.Kind == KeyboardKinds.MarketingConsent).Status);
        Assert.Contains(await e.H.ActionsAsync(e.S), a => a.ToolName == ChatJournal.MarketingConsent);

        // повторне натискання тієї ж клавіатури нічого не змінює; /marketing показує новий стан
        await e.H.PressAsync(e.S, Kb(menu)[index].Data, Ref(menu));
        Assert.Single(await EventsAsync(e));
        await SayCommandAsync(e, "/marketing");
        Assert.StartsWith(level switch { "full" => "Зараз: повна інформація", "selective" => "Зараз: лише акції на ваші послуги", _ => "Зараз: розсилки вимкнено" },
            e.H.Bot.Messages.Last().Text);
    }

    private async Task<List<ChatKeyboard>> KeyboardsAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.ChatKeyboards.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync();
    }

    [SkippableFact]
    public async Task marketing_command_and_alias_without_a_client_explain_the_first_booking()
    {
        var e = await NewAsync(withClient: false);
        await SayCommandAsync(e, "/marketing");
        Assert.Equal(ChatTemplates.MarketingNoClient, e.H.Bot.Messages.Last().Text);
        Assert.Null(e.H.Bot.Messages.Last().Markup);
        await e.H.SayAsync(e.S, "Новини й акції");
        Assert.Equal(ChatTemplates.MarketingNoClient, e.H.Bot.Messages.Last().Text);
    }

    [SkippableFact]
    public async Task menu_text_aliases_show_the_menu_and_other_phrases_go_to_the_model()
    {
        var e = await NewAsync();
        await e.H.SayAsync(e.S, "акції");
        Assert.Equal(ChatTemplates.MarketingMenu("none", false, true), e.H.Bot.Messages.Last().Text);
        Assert.Empty(e.H.Ai.Calls);
        e.H.Ai.Enqueue(ChatHarness.Text("Зараз знижок немає."));
        await e.H.SayAsync(e.S, "які в вас акції зараз");
        Assert.Single(e.H.Ai.Calls);
    }

    [SkippableFact]
    public async Task i_mkt_button_opens_the_menu()
    {
        var e = await NewAsync();
        await SetConsentRowAsync(e, "selective");
        await e.H.PressAsync(e.S, "i:mkt", "1");
        var last = e.H.Bot.Messages.Last();
        Assert.StartsWith("Зараз: лише акції на ваші послуги", last.Text);
        Assert.Equal(3, Kb(last).Count);
    }

    // ======================================================================== прапорець вимкнено

    [SkippableFact]
    public async Task with_marketing_disabled_everything_behaves_as_before()
    {
        var e = await NewAsync(marketing: false);
        await SayCommandAsync(e, "/marketing");
        Assert.Equal(ChatTemplates.TextOnly, e.H.Bot.Messages.Last().Text);
        await e.H.PressAsync(e.S, "i:mkt", "1");
        Assert.Equal(ChatTemplates.ActionStale, e.H.Bot.Messages.Last().Text);
        // «акції» і код-подібний текст - звичайні повідомлення для моделі; TASK-790 (L10): код за шаблоном маскується в тілі й за вимкненого маркетингу
        e.H.Ai.Enqueue(ChatHarness.Text("Вітаю."), ChatHarness.Text("Не розумію."));
        await e.H.SayAsync(e.S, "акції");
        await e.H.SayAsync(e.S, Code);
        Assert.Equal(2, e.H.Ai.Calls.Count);
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            var bodies = await ctx.Messages.AsNoTracking().Where(m => m.Direction == MessageDirection.Inbound).Select(m => m.Body).ToListAsync();
            Assert.DoesNotContain(bodies, b => b.Contains("ABCDE"));
            Assert.Contains(bodies, b => b == ChatMarketingParser.CodeBodyRedacted);
        }
        // перша пропозиція згоди не показується; «Стоп» відповідає старим текстом, але відписку в таблиці згод фіксує
        await SetConsentRowAsync(e, "full");
        await e.H.SayAsync(e.S, "Стоп");
        Assert.Equal(ChatTemplates.Stop, e.H.Bot.Messages.Last().Text);
        Assert.Equal("none", (await ConsentAsync(e))!.Level);
        Assert.Equal("stop", (await ConsentAsync(e))!.Source);
    }

    [SkippableFact]
    public async Task unsubscribe_button_and_text_work_with_marketing_disabled_and_keep_reminders()
    {
        var e = await NewAsync(marketing: false);
        await SetConsentRowAsync(e, "full");
        await e.H.PressAsync(e.S, "i:mkoff", "1");
        Assert.Equal(ChatTemplates.MarketingOff, e.H.Bot.Messages.Last().Text);
        var row = await ConsentAsync(e);
        Assert.Equal(("none", "chat"), (row!.Level, row.Source));
        var ev = Assert.Single(await EventsAsync(e));
        Assert.Equal(("full", "none", "button"), (ev.LevelFrom, ev.LevelTo, ev.Via));
        await using var ctx = _db.CreateContext(e.Tenant);
        Assert.False((await ctx.Clients.AsNoTracking().SingleAsync(c => c.Id == e.ClientId)).MarketingConsent);
        Assert.False((await ctx.Clients.AsNoTracking().SingleAsync(c => c.Id == e.ClientId)).Unsubscribed); // нагадування не чіпали
    }

    [SkippableFact]
    public async Task unsubscribe_text_works_and_takes_the_campaign_from_the_message_recipient_for_the_button()
    {
        var e = await NewAsync();
        await SetConsentRowAsync(e, "selective");
        await e.H.SayAsync(e.S, "Без акцій");
        Assert.Equal(ChatTemplates.MarketingOff, e.H.Bot.Messages.Last().Text);
        var ev = Assert.Single(await EventsAsync(e));
        Assert.Equal(("selective", "none", "text", (Guid?)null), (ev.LevelFrom, ev.LevelTo, ev.Via, ev.CampaignId));

        var e2 = await NewAsync();
        await SetConsentRowAsync(e2, "full");
        var offer = await OfferAsync(e2);
        await e2.H.PressAsync(e2.S, "i:mkoff", "500");
        var ev2 = Assert.Single(await EventsAsync(e2));
        Assert.Equal(offer.Campaign, ev2.CampaignId);
    }

    // ======================================================================== пропозиція згоди після запису

    private async Task<FakeBotApi.Sent> BookAsync(Env e, DateTimeOffset? start = null)
    {
        await e.H.ShowSummaryAsync(e.S, start: start);
        var summary = e.H.Bot.Messages.Last(m => m.Markup is not null);
        await e.H.PressAsync(e.S, Data(summary, "Так, записатися"), Ref(summary));
        return e.H.Bot.Messages.Last(m => m.Text.StartsWith(ChatTemplates.ConfirmedHeader));
    }

    [SkippableFact]
    public async Task consent_is_offered_once_after_reminder_choice_and_not_again_within_the_cooldown()
    {
        var e = await NewAsync(withClient: false);
        var confirmed = await BookAsync(e);
        Assert.DoesNotContain(e.H.Bot.Messages, m => m.Text == ChatTemplates.MarketingOffer); // вибір нагадування ще не зроблено
        e.ClientId = (await ConvAsync(e)).ClientId!.Value;

        await PressAsync(e, e.H.Bot.Messages.Last(m => m.Markup is not null && Kb(m).Any(b => b.Text.Contains("За 1 год"))), "За 1 год");
        var offer = e.H.Bot.Messages.Last();
        Assert.Equal(ChatTemplates.MarketingOffer, offer.Text);
        Assert.Equal(["📰 Повна інформація", "🏷 Лише акції на мої послуги", "🔕 Не надсилати"], Kb(offer).Select(b => b.Text).ToArray());
        Assert.Contains(e.H.Bot.Messages, m => m.Text.StartsWith("Готово: нагадаємо"));
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            Assert.NotNull((await ctx.Clients.AsNoTracking().SingleAsync(c => c.Id == e.ClientId)).MarketingOfferedAt);
            var opts = KeyboardJson.TryParse((await ctx.ChatKeyboards.AsNoTracking().Where(k => k.Kind == KeyboardKinds.MarketingConsent).SingleAsync()).Options)!;
            Assert.All(opts, o => Assert.Equal("mc-v1", o.TextVersion));
            Assert.EndsWith(":m", (await ctx.Messages.AsNoTracking().Where(m => m.Body == ChatTemplates.MarketingOffer).SingleAsync()).IdempotencyKey!);
        }
        Assert.Contains(await e.H.ActionsAsync(e.S), a => a.ToolName == ChatJournal.MarketingOffer);

        // другий запис: вподобання вже є, але пропозиція не частіше за OfferCooldownDays
        await BookAsync(e, e.S.Start.AddHours(3));
        Assert.Single(e.H.Bot.Messages, m => m.Text == ChatTemplates.MarketingOffer);
    }

    [SkippableFact]
    public async Task consent_is_offered_right_after_confirmation_when_a_reminder_choice_exists()
    {
        var e = await NewAsync(preference: true);
        await BookAsync(e);
        var all = e.H.Bot.Messages.ToList();
        var confirmedAt = all.FindIndex(m => m.Text.StartsWith(ChatTemplates.ConfirmedHeader));
        Assert.Equal(ChatTemplates.MarketingOffer, all[confirmedAt + 1].Text); // окремим повідомленням слідом за T-confirmed
    }

    [SkippableFact]
    public async Task no_offer_when_marketing_is_disabled_or_a_consent_row_exists_or_during_handoff()
    {
        var off = await NewAsync(marketing: false, preference: true);
        await BookAsync(off);
        Assert.DoesNotContain(off.H.Bot.Messages, m => m.Text == ChatTemplates.MarketingOffer);

        var has = await NewAsync(preference: true);
        await SetConsentRowAsync(has, "none");
        await BookAsync(has);
        Assert.DoesNotContain(has.H.Bot.Messages, m => m.Text == ChatTemplates.MarketingOffer);

        var noButtons = await NewAsync(preference: true, buttons: false);
        await noButtons.H.ShowSummaryAsync(noButtons.S);
        await noButtons.H.SayAsync(noButtons.S, "Так");
        Assert.Contains(noButtons.H.Bot.Messages, m => m.Text.StartsWith(ChatTemplates.ConfirmedHeader));
        Assert.DoesNotContain(noButtons.H.Bot.Messages, m => m.Text == ChatTemplates.MarketingOffer); // згода - лише кнопкою
    }

    // ======================================================================== «Стоп»

    [SkippableFact]
    public async Task stop_switches_off_marketing_too_over_a_granted_consent()
    {
        var e = await NewAsync();
        await SayCommandAsync(e, "/marketing");
        await PressAsync(e, e.H.Bot.Messages.Last(), "Повна інформація");
        Assert.Equal("full", (await ConsentAsync(e))!.Level);

        await e.H.SayAsync(e.S, "Стоп");
        Assert.Equal(ChatTemplates.StopMarketing, e.H.Bot.Messages.Last().Text);
        var row = await ConsentAsync(e);
        Assert.Equal(("none", "stop"), (row!.Level, row.Source));
        var events = await EventsAsync(e);
        Assert.Equal(("full", "none", "stop"), (events[^1].LevelFrom, events[^1].LevelTo, events[^1].Source));
        await using var ctx = _db.CreateContext(e.Tenant);
        var client = await ctx.Clients.AsNoTracking().SingleAsync(c => c.Id == e.ClientId);
        Assert.False(client.MarketingConsent);
        Assert.True(client.Unsubscribed);
        await SayCommandAsync(e, "/marketing");
        Assert.StartsWith("Зараз: розсилки вимкнено", e.H.Bot.Messages.Last().Text);
    }

    // ======================================================================== кнопка кампанії co

    [SkippableFact]
    public async Task co_starts_a_booking_with_the_personal_coupon_and_books_exactly_once_with_the_discount()
    {
        var e = await NewAsync(preference: true);
        var o = await OfferAsync(e);
        await PressCoAsync(e, o);
        var start = e.H.Bot.Messages.Last();
        Assert.StartsWith("Знижку 30% на Стрижка закріплено за вами до ", start.Text);
        var conv = await ConvAsync(e);
        Assert.Equal((o.Campaign, o.Coupon), (conv.OfferCampaignId, conv.OfferCouponId));
        Assert.InRange(conv.OfferUntil!.Value - DateTimeOffset.UtcNow, TimeSpan.FromHours(71), TimeSpan.FromHours(72.1));
        Assert.NotNull((await RecipientAsync(e, o)).ClickedAt);
        Assert.Single((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName == ChatJournal.CampaignClick));

        // повторне натискання ідемпотентне: без нового журналу й без дубля відповіді
        var before = e.H.Bot.Messages.Count;
        await PressCoAsync(e, o);
        Assert.Equal(before, e.H.Bot.Messages.Count);
        Assert.Single((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName == ChatJournal.CampaignClick));

        // модель готує запис, КОД додає купон: підсумок з ціною купона
        await e.H.ShowSummaryAsync(e.S);
        var summary = e.H.Bot.Messages.Last(m => m.Markup is not null);
        Assert.Contains("Ціна: 350 грн (замість 500 грн, купон «Осінній купон»)", summary.Text);
        var proposal = Assert.Single(await e.H.ProposalsAsync(e.S));
        Assert.Equal(o.Coupon, proposal.CouponId);
        await e.H.PressAsync(e.S, Data(summary, "Так, записатися"), Ref(summary));

        var appt = Assert.Single(await e.H.AppointmentsAsync(e.S));
        Assert.Equal((o.Coupon, o.Campaign, 350m), (appt.CouponId, appt.CampaignId, appt.PriceFinal));
        var coupon = await CouponAsync(e, o);
        Assert.Equal(1, coupon.UsesCount);
        await using (var ctx = _db.CreateContext(e.Tenant))
            Assert.Equal("active", (await ctx.CouponRedemptions.AsNoTracking().SingleAsync(r => r.CouponId == o.Coupon)).Status);
        var after = await ConvAsync(e);
        Assert.Null(after.OfferCouponId);
        Assert.Null(after.OfferUntil);
        Assert.Contains(e.H.Bot.Messages, m => m.Text.StartsWith(ChatTemplates.ConfirmedHeader));
    }

    [SkippableFact]
    public async Task coupon_that_is_not_better_than_a_public_promotion_is_not_consumed()
    {
        var e = await NewAsync(preference: true);
        var o = await OfferAsync(e, percent: 30m, publicPercent: 40m);
        await PressCoAsync(e, o);
        await e.H.ShowSummaryAsync(e.S);
        var summary = e.H.Bot.Messages.Last(m => m.Markup is not null);
        Assert.Contains("Ціна: 300 грн (замість 500 грн, акція «Будні»)", summary.Text);
        Assert.Contains("Купон не застосовано: діє вигідніша акція.", summary.Text);
        await e.H.PressAsync(e.S, Data(summary, "Так, записатися"), Ref(summary));
        var appt = Assert.Single(await e.H.AppointmentsAsync(e.S));
        Assert.Null(appt.CouponId);
        Assert.Equal(300m, appt.PriceFinal);
        var coupon = await CouponAsync(e, o);
        Assert.Equal((0, "active"), (coupon.UsesCount, coupon.Status));
        await using var ctx = _db.CreateContext(e.Tenant);
        Assert.Empty(await ctx.CouponRedemptions.AsNoTracking().ToListAsync());
        Assert.NotNull((await ConvAsync(e)).OfferCouponId); // купон лишається чинним для наступного запису
    }

    [SkippableFact]
    public async Task co_with_a_foreign_expired_or_used_token_sets_no_offer()
    {
        var e = await NewAsync();
        var o = await OfferAsync(e);

        // чужа розмова: токен належить розмові "100", натискає розмова "200"
        await e.H.IngestAsync(e.S, "Привіт", chat: "200");
        var other = await e.H.ConversationIdAsync(e.S, "200");
        await e.H.PressAsync(e.S, o.Keyboard.Data("co"), "500", chat: "200");
        Assert.Null((await e.H.ConversationAsync(e.S, "200")).OfferCouponId);
        Assert.Equal(KeyboardStatuses.Active, (await KeyboardsAsync(e)).Single(k => k.Kind == KeyboardKinds.CampaignOffer).Status);

        // протермінований токен
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.ChatKeyboards.Where(k => k.Id == o.Keyboard.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await PressCoAsync(e, o);
        Assert.Equal(ChatTemplates.ActionStale, e.H.Bot.Messages.Last().Text);
        Assert.Null((await ConvAsync(e)).OfferCouponId);

        // купон уже використаний: T-coupon-unavailable + [Записатися]
        var e2 = await NewAsync();
        var o2 = await OfferAsync(e2);
        await using (var ctx = _db.CreateContext(e2.Tenant))
            await ctx.Coupons.Where(c => c.Id == o2.Coupon).ExecuteUpdateAsync(u => u.SetProperty(c => c.UsesCount, 1));
        await PressCoAsync(e2, o2);
        var last = e2.H.Bot.Messages.Last();
        Assert.Equal(ChatTemplates.CouponUnavailable, last.Text);
        Assert.Equal(["📅 Записатися"], Kb(last).Select(b => b.Text).ToArray());
        Assert.Null((await ConvAsync(e2)).OfferCouponId);
        Assert.Null((await RecipientAsync(e2, o2)).ClickedAt);
        _ = other;
    }

    [SkippableFact]
    public async Task coupon_revoked_between_summary_and_confirmation_gives_a_plain_price_next_time()
    {
        var e = await NewAsync(preference: true);
        var o = await OfferAsync(e);
        await PressCoAsync(e, o);
        await e.H.ShowSummaryAsync(e.S);
        var summary = e.H.Bot.Messages.Last(m => m.Markup is not null);
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.Coupons.Where(c => c.Id == o.Coupon).ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "revoked"));
        await e.H.PressAsync(e.S, Data(summary, "Так, записатися"), Ref(summary));
        Assert.Equal(ChatTemplates.CouponUnavailable, e.H.Bot.Messages.Last().Text);
        Assert.Empty(await e.H.AppointmentsAsync(e.S));
        Assert.Null((await ConvAsync(e)).OfferCouponId);
        Assert.Equal("failed", (await e.H.ProposalsAsync(e.S)).Single().Status);

        await e.H.ShowSummaryAsync(e.S);
        Assert.Contains("Ціна: 500 грн", e.H.Bot.Messages.Last(m => m.Markup is not null).Text);
    }

    // ======================================================================== код текстом

    [SkippableFact]
    public async Task valid_code_text_sets_the_offer_masks_the_body_and_the_code_is_nowhere()
    {
        var e = await NewAsync(preference: true);
        var o = await OfferAsync(e);
        await e.H.SayAsync(e.S, Code);
        Assert.StartsWith("Код прийнято: 30% на Стрижка, діє до ", e.H.Bot.Messages.Last().Text);
        var conv = await ConvAsync(e);
        Assert.Equal((o.Campaign, o.Coupon), (conv.OfferCampaignId, conv.OfferCouponId));
        Assert.Empty(e.H.Ai.Calls);

        // і запис за цим кодом - зі знижкою; код не світиться ніде
        await e.H.ShowSummaryAsync(e.S);
        Assert.Contains("(замість 500 грн, купон «Осінній купон»)", e.H.Bot.Messages.Last(m => m.Markup is not null).Text);
        ScanForCode(e);
    }

    private void ScanForCode(Env e)
    {
        var raw = Code.Replace("-", "");
        bool Has(string? s) => s is not null && (s.Contains(Code, StringComparison.OrdinalIgnoreCase) || s.Contains(raw, StringComparison.OrdinalIgnoreCase));
        using var ctx = _db.CreateContext(e.Tenant);
        Assert.DoesNotContain(ctx.Messages.AsNoTracking().Select(m => m.Body).ToList(), Has);
        Assert.DoesNotContain(ctx.Messages.AsNoTracking().Select(m => m.Actions).ToList(), Has);
        Assert.DoesNotContain(ctx.AiActions.AsNoTracking().Select(a => a.Payload).ToList(), Has);
        Assert.DoesNotContain(ctx.ChatKeyboards.AsNoTracking().Select(k => k.Options).ToList(), Has);
        Assert.False(Has(e.H.Logs.All), "код у логах");
        Assert.False(Has(JsonSerializer.Serialize(e.H.Ai.Calls)), "код у запиті до ШІ");
        Assert.DoesNotContain(e.H.Bot.Messages.Select(m => m.Text), Has);
        Assert.DoesNotContain(e.H.Bot.Messages.Select(m => m.Markup), Has);
        Assert.False(Has(e.H.Bot.UrlsText));
        Assert.Contains(ctx.Messages.AsNoTracking().Where(m => m.Direction == MessageDirection.Inbound).Select(m => m.Body).ToList(), b => b == "[код купона]");
    }

    [SkippableFact]
    public async Task code_with_a_prefix_and_in_any_format_is_recognised()
    {
        var e = await NewAsync();
        var o = await OfferAsync(e);
        await e.H.SayAsync(e.S, "промокод: abcde fghjk");
        Assert.StartsWith("Код прийнято", e.H.Bot.Messages.Last().Text);
        Assert.Equal(o.Coupon, (await ConvAsync(e)).OfferCouponId);
    }

    [SkippableFact]
    public async Task invalid_codes_get_one_answer_and_the_attempt_limit_locks_codes_for_an_hour()
    {
        var e = await NewAsync(opts: RoomyOpts());
        var o = await OfferAsync(e);
        for (var i = 0; i < 5; i++)
        {
            await e.H.SayAsync(e.S, $"код ZZZZ{i}-ZZZZZ");
            Assert.Equal(ChatTemplates.CouponInvalid, e.H.Bot.Messages.Last().Text);
        }
        await e.H.SayAsync(e.S, "код ZZZZ9-ZZZZZ");
        Assert.Equal(ChatTemplates.CouponLocked, e.H.Bot.Messages.Last().Text);
        var count = e.H.Bot.Messages.Count;
        await e.H.SayAsync(e.S, Code); // навіть правильний код не розбирається; повторної відповіді «locked» немає
        Assert.Equal(count, e.H.Bot.Messages.Count);
        Assert.Null((await ConvAsync(e)).OfferCouponId);
        Assert.Empty(e.H.Ai.Calls);
        await using (var ctx = _db.CreateContext(e.Tenant))
            Assert.All(await ctx.Messages.AsNoTracking().Where(m => m.Direction == MessageDirection.Inbound && m.Kind == "text" && m.Body != "Привіт").Select(m => m.Body).ToListAsync(),
                b => Assert.Equal("[код купона]", b));

        e.H.Clock.Advance(TimeSpan.FromMinutes(61));
        await e.H.SayAsync(e.S, Code);
        Assert.StartsWith("Код прийнято", e.H.Bot.Messages.Last().Text);
    }

    [SkippableFact]
    public async Task foreign_personal_code_and_a_personal_code_without_a_client_give_the_same_invalid_answer()
    {
        var foreign = await NewAsync();
        await OfferAsync(foreign, clientId: foreign.ClientId); // купон клієнта розмови
        await using (var ctx = _db.CreateContext(foreign.Tenant))
        {
            var other = new Client { FullName = "Інша" };
            ctx.Add(other);
            await ctx.SaveChangesAsync();
            await ctx.Coupons.ExecuteUpdateAsync(u => u.SetProperty(c => c.ClientId, (Guid?)other.Id));
        }
        await foreign.H.SayAsync(foreign.S, Code);
        Assert.Equal(ChatTemplates.CouponInvalid, foreign.H.Bot.Messages.Last().Text);

        var noClient = await NewAsync(withClient: false);
        await using (var ctx = _db.CreateContext(noClient.Tenant))
        {
            noClient.ClientId = Guid.Empty;
            var other = new Client { FullName = "Власник" };
            ctx.Add(other);
            await ctx.SaveChangesAsync();
            await OfferAsync(noClient, clientId: other.Id, keyboard: false);
        }
        await noClient.H.SayAsync(noClient.S, Code);
        Assert.Equal(ChatTemplates.CouponInvalid, noClient.H.Bot.Messages.Last().Text);
    }

    [SkippableFact]
    public async Task ordinary_text_phone_like_numbers_and_short_words_are_not_treated_as_codes()
    {
        var e = await NewAsync();
        e.H.Ai.Enqueue(ChatHarness.Text("Так."), ChatHarness.Text("Добре."), ChatHarness.Text("Гаразд."));
        await e.H.SayAsync(e.S, "0501112233");
        await e.H.SayAsync(e.S, "hello");
        await e.H.SayAsync(e.S, "Хочу манікюр");
        Assert.Equal(3, e.H.Ai.Calls.Count);
        Assert.DoesNotContain(e.H.Bot.Messages, m => m.Text == ChatTemplates.CouponInvalid);
    }

    [SkippableFact]
    public async Task shared_code_is_recognised_by_hash_without_a_prefix_and_a_code_in_a_batch_is_handled_alone()
    {
        var e = await NewAsync();
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            var promo = new Promotion { Name = "Спільна", DiscountType = DiscountType.Fixed, DiscountValue = 100m, IsActive = true, Access = "code_only" };
            ctx.Add(promo);
            await ctx.SaveChangesAsync();
            ctx.Add(new Coupon
            {
                Kind = "shared", PromotionId = promo.Id, Code = "SALE2030", CodeHash = CouponCodes.Hash("SALE2030"), CodeHint = "030",
                ValidFrom = DateTimeOffset.UtcNow.AddHours(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            });
            await ctx.SaveChangesAsync();
        }
        await e.H.SayAsync(e.S, "sale2030");
        Assert.StartsWith("Код прийнято: 100 грн на усі послуги", e.H.Bot.Messages.Last().Text);

        // два повідомлення підряд: код іде окремим ходом, решта - до моделі без коду
        var e2 = await NewAsync(opts: RoomyOpts());
        await OfferAsync(e2);
        await e2.H.IngestAsync(e2.S, "Привіт, хочу записатись");
        var (conv, _) = await e2.H.IngestAsync(e2.S, Code);
        e2.H.Ai.Enqueue(ChatHarness.Text("Звісно, на яку послугу?"));
        await e2.H.TurnAsync(e2.Tenant, conv!.Value);
        ScanForCode(e2);
        Assert.Single(e2.H.Ai.Calls);
        Assert.Equal(e2.ClientId, e2.ClientId);
    }

    // ======================================================================== handoff і suggest

    [SkippableFact]
    public async Task handoff_allows_consent_menu_and_unsubscribe_but_blocks_the_campaign_button()
    {
        var e = await NewAsync();
        var o = await OfferAsync(e);
        await SayCommandAsync(e, "/marketing");
        var menu = e.H.Bot.Messages.Last();
        await SetHandoffAsync(e);

        await e.H.PressAsync(e.S, "i:mkt", "1");
        Assert.StartsWith("Зараз:", e.H.Bot.Messages.Last().Text);
        menu = e.H.Bot.Messages.Last(); // нове меню відкликало попереднє
        await e.H.PressAsync(e.S, Data(menu, "Повна інформація"), Ref(menu));
        Assert.Equal("full", (await ConsentAsync(e))!.Level);
        await e.H.PressAsync(e.S, "i:mkoff", "500");
        Assert.Equal("none", (await ConsentAsync(e))!.Level);
        await SayCommandAsync(e, "/marketing");
        Assert.StartsWith("Зараз: розсилки вимкнено", e.H.Bot.Messages.Last().Text);
        await e.H.SayAsync(e.S, "не надсилати акції");
        Assert.Equal(ChatTemplates.MarketingOff, e.H.Bot.Messages.Last().Text);

        await PressCoAsync(e, o);
        Assert.Equal(ChatTemplates.ActionHandoff, e.H.Bot.Messages.Last().Text);
        Assert.Null((await ConvAsync(e)).OfferCouponId);
        Assert.Null((await RecipientAsync(e, o)).ClickedAt);
        // код текстом у handoff не розбирається (текст тримається для менеджера)
        await e.H.SayAsync(e.S, Code);
        Assert.Null((await ConvAsync(e)).OfferCouponId);
    }

    [SkippableFact]
    public async Task suggest_answers_consent_templates_but_co_and_code_only_set_the_offer_silently()
    {
        var e = await NewAsync(aiMode: "suggest");
        var o = await OfferAsync(e);
        await SayCommandAsync(e, "/marketing");
        var menu = e.H.Bot.Messages.Last();
        Assert.StartsWith("Зараз:", menu.Text);
        await e.H.PressAsync(e.S, Data(menu, "Лише акції"), Ref(menu));
        Assert.StartsWith("Готово: надсилатимемо лише акції", e.H.Bot.Messages.Last().Text);
        Assert.Equal("selective", (await ConsentAsync(e))!.Level);
        await e.H.PressAsync(e.S, "i:mkoff", "500");
        Assert.Equal(ChatTemplates.MarketingOff, e.H.Bot.Messages.Last().Text);

        var before = e.H.Bot.Messages.Count;
        await PressCoAsync(e, o);
        Assert.Equal(before, e.H.Bot.Messages.Count); // без шаблону
        Assert.Equal(o.Coupon, (await ConvAsync(e)).OfferCouponId);
        Assert.NotNull((await RecipientAsync(e, o)).ClickedAt);

        var e2 = await NewAsync(aiMode: "suggest");
        await OfferAsync(e2);
        await e2.H.SayAsync(e2.S, Code);
        Assert.Empty(e2.H.Bot.Messages.Where(m => m.Text.StartsWith("Код")));
        Assert.NotNull((await ConvAsync(e2)).OfferCouponId);
        Assert.Empty(e2.H.Ai.Calls);
        ScanForCode(e2);
    }

    // ======================================================================== ШІ: факти й правила

    [SkippableFact]
    public async Task facts_block_tells_about_the_active_discount_without_the_code_and_the_model_prompt_has_the_rules()
    {
        var e = await NewAsync(preference: true);
        await OfferAsync(e);
        await e.H.SayAsync(e.S, Code);
        e.H.Ai.Enqueue(ChatHarness.Text("Так, у вас є знижка. Яку послугу обираєте?"));
        await e.H.SayAsync(e.S, "Яка в мене знижка?");
        var system = Assert.Single(e.H.Ai.Calls).System;
        Assert.Contains("У клієнта активна знижка за купоном: 30% на Стрижка до ", system);
        Assert.Contains("ціну з купоном рахує система в підсумку", system);
        Assert.Contains("Розсилки акцій — лише за згодою клієнта; керувати — /marketing.", system);
        Assert.Contains("Не обіцяй знижок, купонів і кодів, яких немає у фактах чи tools", system);
        ScanForCode(e);
    }

    [SkippableFact]
    public async Task facts_have_no_marketing_lines_when_marketing_is_disabled_and_no_offer()
    {
        var e = await NewAsync(marketing: false);
        e.H.Ai.Enqueue(ChatHarness.Text("Вітаю."));
        await e.H.SayAsync(e.S, "Привіт ще раз");
        var system = Assert.Single(e.H.Ai.Calls).System;
        Assert.DoesNotContain("/marketing", system);
        Assert.DoesNotContain("активна знижка", system);
    }
}

