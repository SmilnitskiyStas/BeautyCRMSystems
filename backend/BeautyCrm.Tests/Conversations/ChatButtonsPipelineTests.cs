using System.Text.Json;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Conversations.ChatHarness;

namespace BeautyCrm.Tests.Conversations;

/// <summary>Чисті функції TASK-753: команди, розкладки, шаблони. Без БД.</summary>
public sealed class ChatButtonsUnitTests753
{
    [Theory]
    [InlineData("/start", ChatCommand.Start)]
    [InlineData("/START", ChatCommand.Start)]
    [InlineData("/start payload", ChatCommand.Start)]
    [InlineData("/book", ChatCommand.Book)]
    [InlineData("/zapys", ChatCommand.Book)]
    [InlineData("/zapis", ChatCommand.Book)]
    [InlineData("/my@BeautyBot", ChatCommand.My)]
    [InlineData("/cancel", ChatCommand.Cancel)]
    [InlineData("/stop", ChatCommand.Stop)]
    [InlineData("/help", ChatCommand.Help)]
    [InlineData("/whatever", ChatCommand.Unknown)]
    public void commands_parse_case_insensitive_with_bot_suffix(string text, ChatCommand expected) =>
        Assert.Equal(expected, ChatCommands.Parse(text));

    [Theory]
    [InlineData("привіт")]
    [InlineData("")]
    [InlineData(null)]
    public void plain_text_is_not_a_command(string? text) => Assert.Null(ChatCommands.Parse(text));

    [Fact]
    public void summary_has_no_markdown_old_price_in_words_and_a_button_or_text_instruction()
    {
        var dto = new BeautyCrm.Infrastructure.AI.Beauty.ChatBookingSummaryDto("Стрижка", "Марина", "Салон", "вул. 1", new DateOnly(2030, 1, 4),
            new TimeOnly(14, 30), "Europe/Kyiv", 60, 600m, 500m, "Будні");
        var withButtons = BeautyCrm.Infrastructure.AI.Beauty.ChatSummaryText.Format(dto, 15, "грн", buttons: true);
        var text = BeautyCrm.Infrastructure.AI.Beauty.ChatSummaryText.Format(dto, 15, "грн");
        Assert.DoesNotContain("~", withButtons);
        Assert.Contains("500 грн (замість 600 грн, акція «Будні»)", withButtons);
        Assert.Contains("Натисніть «Так, записатися» або напишіть «Так»", withButtons);
        Assert.Contains("Відповідайте «Так»", text);
        Assert.DoesNotContain("кнопк", text);
    }

    [Fact]
    public void builder_layouts_respect_limits_and_slots_prefer_the_requested_time()
    {
        var b = new ChatKeyboardBuilder(new ChatButtonsOptions { Enabled = true, SlotButtons = 4 });
        var spec = Guid.NewGuid();
        var day = new DateTimeOffset(2030, 1, 4, 0, 0, 0, TimeSpan.FromHours(2));
        var slots = Enumerable.Range(0, 20).Select(i => new BeautyCrm.Infrastructure.AI.Beauty.ChatOfferedSlot(spec, day.AddHours(8).AddMinutes(30 * i))).ToList();
        var offered = new BeautyCrm.Infrastructure.AI.Beauty.ChatOfferedSlots(new DateOnly(2030, 1, 4), "Europe/Kyiv", [], slots, Guid.NewGuid(), Guid.NewGuid(),
            new TimeOnly(14, 0));
        var plan = b.SlotPick(offered, new Dictionary<Guid, string>(), DateTimeOffset.UtcNow)!;
        Assert.True(plan.IsValid());
        Assert.Equal(4, plan.Options.Count);
        Assert.Contains(plan.Options, o => o.StartsAt!.Value.Hour == 14 && o.StartsAt.Value.Minute == 0);
        Assert.All(plan.Options, o => Assert.Equal("sp", o.Verb));

        Assert.True(b.BookingConfirm(Guid.NewGuid()).IsValid());
        Assert.True(b.CancelConfirm(Guid.NewGuid()).IsValid());
        Assert.True(b.MainMenu().IsValid());
        Assert.True(b.HumanOnly().IsValid());
        var menu = BeautyCrm.Infrastructure.AI.Beauty.Conversations.ReminderChoiceMenu.Options(true);
        var rc = b.ReminderChoice(Guid.NewGuid(), menu, DateTimeOffset.UtcNow.AddHours(5));
        Assert.True(rc.IsValid());
        Assert.Equal(5, rc.Options.Count);
        Assert.Null(b.SlotPick(null, new Dictionary<Guid, string>(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void chat_rules_forbid_inventing_buttons()
    {
        Assert.Contains("Кнопки під повідомленнями додає лише система", BeautyCrm.Infrastructure.AI.Beauty.PromptComposer.ChatRules);
        Assert.Contains("callback_data", BeautyCrm.Infrastructure.AI.Beauty.PromptComposer.ChatRules);
    }
}

/// <summary>
/// Конвеєр кнопок і команд (TASK-753, §23.5-§23.8, тести §23.13 4-6, 9, 11, 13, 15-19, 21): реальний PostgreSQL з RLS, mock-провайдер ШІ,
/// fake Telegram (sendMessage з message_id, editMessageReplyMarkup, answerCallbackQuery). Реальних викликів немає.
/// </summary>
public sealed class ChatButtonsPipelineTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatButtonsPipelineTests(PostgresRlsFixture db) => _db = db;

    private static readonly IDictionary<string, string?> On = new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true" };

    private ChatHarness H(IDictionary<string, string?>? cfg = null, bool mock = false) => ChatHarness.Create(_db, extraConfig: cfg ?? On, mockChannel: mock);

    private static string LocalDate(ChatSeed s) => s.Start.ToString("yyyy-MM-dd");

    private static List<(string Text, string Data)> Kb(FakeBotApi.Sent m)
    {
        if (m.Markup is null) return [];
        using var doc = JsonDocument.Parse(m.Markup);
        return doc.RootElement.GetProperty("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray())
            .Select(b => (b.GetProperty("text").GetString()!, b.GetProperty("callback_data").GetString()!)).ToList();
    }

    private static string Data(FakeBotApi.Sent m, string labelPart) => Kb(m).First(b => b.Text.Contains(labelPart)).Data;

    private static string Ref(FakeBotApi.Sent m) => m.MessageId.ToString();

    /// <summary>Поле JSON-payload журналу (jsonb нормалізує пробіли, тож не порівнюємо сирий рядок).</summary>
    private static string? Field(AiAction a, string name) =>
        JsonDocument.Parse(a.Payload).RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static FakeBotApi.Sent LastWithButtons(ChatHarness h) => h.Bot.Messages.Last(m => m.Markup is not null);

    private static Task<Guid> PressAsync(ChatHarness h, ChatSeed s, FakeBotApi.Sent m, string labelPart, string chat = "100") =>
        h.PressAsync(s, Data(m, labelPart), Ref(m), chat);

    private async Task<(Guid Conv, FakeBotApi.Sent Summary)> SummaryAsync(ChatHarness h, ChatSeed s, string chat = "100")
    {
        var conv = await h.ShowSummaryAsync(s, chat);
        return (conv, LastWithButtons(h));
    }

    private async Task<(Guid Conv, FakeBotApi.Sent Confirmed)> BookByButtonAsync(ChatHarness h, ChatSeed s)
    {
        var (conv, summary) = await SummaryAsync(h, s);
        await PressAsync(h, s, summary, "Так, записатися");
        return (conv, h.Bot.Messages.Last());
    }

    private async Task<List<ChatKeyboard>> KeyboardsAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.ChatKeyboards.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync();
    }

    private async Task SetHandoffAsync(ChatSeed s, Guid conv)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        await ctx.Conversations.Where(c => c.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiState, "handoff")
            .SetProperty(c => c.HandoffReason, "human_requested").SetProperty(c => c.HandoffAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
            .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(12)));
    }

    private static async Task<int> InboundCountAsync(ChatHarness h, ChatSeed s, string status) =>
        (await h.MessagesAsync(s)).Count(m => m.Direction == MessageDirection.Inbound && m.Status == status);

    // ================================================================ повний потік

    [SkippableFact]
    public async Task full_flow_start_slots_summary_yes_reminder_all_by_buttons_without_llm_for_presses()
    {
        var h = H(); var s = await h.SeedAsync();

        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        var menu = LastWithButtons(h);
        Assert.Equal(ChatTemplates.Greeting, menu.Text);
        Assert.Contains(Kb(menu), b => b.Data == "i:book");
        Assert.Contains(Kb(menu), b => b.Data == "i:my");
        Assert.Contains(Kb(menu), b => b.Data == "i:human");

        await h.PressAsync(s, "i:book", Ref(menu));
        Assert.Equal(ChatTemplates.BookStart, h.Bot.Messages.Last().Text);

        // вибір послуги -> модель шукає слоти -> кнопки слотів на повідомленні моделі
        h.Ai.Enqueue(Tool("find_slots", new { serviceId = s.ServiceId, specialistId = s.SpecialistId, date = LocalDate(s), preferredTime = "10:00" }),
            Text("Ось вільний час."));
        await h.SayAsync(s, "Стрижка в цей день");
        var slots = LastWithButtons(h);
        Assert.Contains("Ось вільний час.", slots.Text);
        Assert.Contains(ChatTemplates.SlotsLine, slots.Text);
        Assert.InRange(Kb(slots).Count, 1, 6);
        var llm = h.Ai.Calls.Count;

        await PressAsync(h, s, slots, "10:00");
        Assert.Equal(llm, h.Ai.Calls.Count); // вибір слота - без LLM
        var summary = LastWithButtons(h);
        Assert.StartsWith("Підсумок запису:", summary.Text);
        Assert.DoesNotContain("~", summary.Text);
        Assert.Contains("Натисніть «Так, записатися»", summary.Text);
        Assert.Equal(["✅ Так, записатися", "Ні", "🕐 Інший час"], Kb(summary).Select(b => b.Text).ToArray());
        Assert.Empty(await h.AppointmentsAsync(s));

        await PressAsync(h, s, summary, "Так, записатися");
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(AppointmentSource.Telegram, appt.Source);
        Assert.Equal(llm, h.Ai.Calls.Count);
        var confirmed = h.Bot.Messages.Last();
        Assert.StartsWith(ChatTemplates.ConfirmedHeader, confirmed.Text);
        Assert.Contains("Оберіть кнопкою", confirmed.Text);
        Assert.Contains(Kb(confirmed), b => b.Text.Contains("За 24 год"));

        await PressAsync(h, s, confirmed, "За 24 год");
        Assert.Equal(llm, h.Ai.Calls.Count);
        Assert.StartsWith("Готово: нагадаємо за 24 год", h.Bot.Messages.Last().Text);
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var pref = await ctx.ClientContactPreferences.AsNoTracking().SingleAsync();
            Assert.Equal("telegram", pref.Method);
            Assert.Equal([(short)1440], pref.OffsetsMinutes);
            Assert.NotEmpty(await ctx.Reminders.AsNoTracking().ToListAsync());
            Assert.Null((await ctx.Conversations.AsNoTracking().SingleAsync()).PendingPrompt);
        }
        // кнопки з повідомлень прибрано: слоти, підсумок, меню нагадувань
        Assert.True(h.Bot.Edits.Count >= 3 && h.Bot.Edits.All(e => e.Empty));
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(slots));
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(summary));
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(confirmed));
        Assert.All((await KeyboardsAsync(s)).Where(k => k.Kind != "appointment_actions"), k => Assert.Equal("used", k.Status));
        var confirm = Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "confirm_booking"));
        Assert.Equal("button", Field(confirm, "via"));
        Assert.Contains("[кнопка]", (await h.MessagesAsync(s)).First(m => m.Kind == "callback").Body);
    }

    [SkippableFact]
    public async Task summary_is_a_separate_code_message_and_the_model_intro_has_no_buttons()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.ShowSummaryAsync(s, intro: "Пропоную цей час.");

        var sent = h.Bot.Messages;
        Assert.Equal(2, sent.Count);
        Assert.Equal("Пропоную цей час.", sent[0].Text);
        Assert.Null(sent[0].Markup);
        Assert.StartsWith("Підсумок запису:", sent[1].Text);
        Assert.NotNull(sent[1].Markup);
        var outbound = await h.OutboundAsync(s);
        Assert.Equal((MessageSenderType.Ai, MessageSenderType.System), (outbound[0].SenderType, outbound[1].SenderType));
        Assert.EndsWith(":s", outbound[1].IdempotencyKey);
        Assert.Null(outbound[0].Actions);
        Assert.Equal("shown", Assert.Single(await h.ProposalsAsync(s)).Status);
        Assert.Equal(outbound[1].Id, (await h.ProposalsAsync(s)).Single().SummaryMessageId);
    }

    [SkippableFact]
    public async Task no_button_rejects_the_proposal_clears_the_keyboard_and_creates_nothing()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, summary) = await SummaryAsync(h, s);
        await PressAsync(h, s, summary, "Ні");

        Assert.Equal(ChatTemplates.Declined, h.Bot.Messages.Last().Text);
        Assert.Equal("rejected", Assert.Single(await h.ProposalsAsync(s)).Status);
        Assert.Empty(await h.AppointmentsAsync(s));
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(summary));
        Assert.NotNull(Assert.Single(await KeyboardsAsync(s)).MarkupClearedAt);

        // «Інший час»: теж відхилення + прохання написати зручний час
        var (_, summary2) = await SummaryAsync(h, s);
        await PressAsync(h, s, summary2, "Інший час");
        Assert.Equal(ChatTemplates.OtherTime, h.Bot.Messages.Last().Text);
    }

    // ================================================================ скасування і «Мої записи»

    [SkippableFact]
    public async Task cancel_visit_takes_two_steps_with_policy_line_and_cancels_as_client()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookByButtonAsync(h, s);
        var appt = Assert.Single(await h.AppointmentsAsync(s));

        var llmBefore = h.Ai.Calls.Count;
        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        var list = h.Bot.Messages.Last();
        Assert.StartsWith(ChatTemplates.MyListHeader, list.Text);
        Assert.Contains("Стрижка", list.Text);
        Assert.Contains(Kb(list), b => b.Data == "i:book");

        await PressAsync(h, s, list, "Скасувати");
        var screen = h.Bot.Messages.Last();
        Assert.StartsWith("Скасувати запис?", screen.Text);
        Assert.Contains("Оплата в закладі, тож нічого повертати не потрібно.", screen.Text);
        Assert.NotEqual(AppointmentStatus.Cancelled, (await h.AppointmentsAsync(s)).Single().Status);

        await PressAsync(h, s, screen, "Так, скасувати");
        Assert.StartsWith("Запис на ", h.Bot.Messages.Last().Text);
        Assert.Contains("скасовано", h.Bot.Messages.Last().Text);
        var after = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(AppointmentStatus.Cancelled, after.Status);
        Assert.Equal("client", after.CancelledByType);
        Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "client_cancel"));
        Assert.Equal(llmBefore, h.Ai.Calls.Count);
        Assert.Equal(appt.Id, after.Id);
    }

    [SkippableFact]
    public async Task cancel_command_with_one_visit_goes_straight_to_the_screen_and_no_keeps_the_visit()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookByButtonAsync(h, s);
        var llm = h.Ai.Calls.Count;

        await h.SayAsync(s, "/cancel", kind: InboundKinds.Command);
        var screen = h.Bot.Messages.Last();
        Assert.StartsWith("Скасувати запис?", screen.Text);
        await PressAsync(h, s, screen, "Ні, залишити");

        Assert.StartsWith("Добре, запис залишається", h.Bot.Messages.Last().Text);
        Assert.NotEqual(AppointmentStatus.Cancelled, Assert.Single(await h.AppointmentsAsync(s)).Status);
        Assert.Equal(llm, h.Ai.Calls.Count);
    }

    [SkippableFact]
    public async Task my_and_cancel_without_visits_say_so_and_offer_to_book()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.MyEmpty, h.Bot.Messages.Last().Text);
        Assert.Contains(Kb(h.Bot.Messages.Last()), b => b.Data == "i:book");

        await h.SayAsync(s, "/cancel", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.MyEmpty, h.Bot.Messages.Last().Text);
        Assert.Empty(h.Ai.Calls);
    }

    [SkippableFact]
    public async Task my_button_from_the_menu_lists_visits_and_reschedule_hands_off_to_the_admin()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookByButtonAsync(h, s);
        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        var menu = LastWithButtons(h);
        await PressAsync(h, s, menu, "Мої записи");
        var list = h.Bot.Messages.Last();
        Assert.StartsWith(ChatTemplates.MyListHeader, list.Text);

        await PressAsync(h, s, list, "Змінити час");
        Assert.Equal(ChatTemplates.RescheduleHandoff, h.Bot.Messages.Last().Text);
        var conv = await h.ConversationAsync(s);
        Assert.Equal(("handoff", "reschedule_request"), (conv.AiState, conv.HandoffReason));
        Assert.NotNull(conv.AwaitingStaffSince);
    }

    // ================================================================ старі, повторні, чужі натискання

    [SkippableFact]
    public async Task repeated_press_within_30_seconds_is_silent_later_it_gets_the_stale_template()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, summary) = await SummaryAsync(h, s);
        await PressAsync(h, s, summary, "Так, записатися");
        var count = h.Bot.Messages.Count;

        await PressAsync(h, s, summary, "Так, записатися"); // ≤ 30 с: без відповіді
        Assert.Equal(count, h.Bot.Messages.Count);
        Assert.Single(await h.AppointmentsAsync(s));

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await PressAsync(h, s, summary, "Так, записатися");
        Assert.Equal(ChatTemplates.ActionStale, h.Bot.Messages.Last().Text);
        Assert.Single(await h.AppointmentsAsync(s));
        Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "confirm_booking"));
    }

    [SkippableFact]
    public async Task two_presses_in_one_batch_give_one_booking_and_one_confirmation()
    {
        var h = H(); var s = await h.SeedAsync();
        var (conv, summary) = await SummaryAsync(h, s);
        var data = Data(summary, "Так, записатися");
        await h.IngestCallbackAsync(s, data, Ref(summary));
        await h.IngestCallbackAsync(s, data, Ref(summary));
        await h.TurnAsync(s.TenantId, conv);

        Assert.Single(await h.AppointmentsAsync(s));
        Assert.Single(h.Bot.Messages.Where(m => m.Text.StartsWith(ChatTemplates.ConfirmedHeader)));
        Assert.Equal(0, await InboundCountAsync(h, s, "received"));
    }

    [SkippableFact]
    public async Task expired_summary_press_gets_expired_template_and_creates_nothing()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, summary) = await SummaryAsync(h, s);
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        await PressAsync(h, s, summary, "Так, записатися");

        Assert.Equal(ChatTemplates.Expired, h.Bot.Messages.Last().Text);
        Assert.Equal("expired", Assert.Single(await h.ProposalsAsync(s)).Status);
        Assert.Empty(await h.AppointmentsAsync(s));
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(summary));
    }

    [SkippableFact]
    public async Task text_between_summary_and_press_revokes_the_keyboard_and_the_press_is_stale()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, summary) = await SummaryAsync(h, s);
        h.Ai.Enqueue(Text("Перевірю 15:00."));
        await h.SayAsync(s, "а можна о 15?");
        Assert.Equal("superseded", Assert.Single(await h.ProposalsAsync(s)).Status);
        Assert.Equal("revoked", Assert.Single(await KeyboardsAsync(s)).Status);
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(summary)); // кнопки з підсумку прибрано

        await PressAsync(h, s, summary, "Так, записатися");
        Assert.Equal(ChatTemplates.ActionStale, h.Bot.Messages.Last().Text);
        Assert.Empty(await h.AppointmentsAsync(s));
    }

    [SkippableFact]
    public async Task text_yes_confirms_and_extinguishes_the_buttons_and_new_summary_revokes_the_old_one()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, first) = await SummaryAsync(h, s);
        var (_, second) = await SummaryAsync(h, s); // нова пропозиція: попередня клавіатура revoked
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(first));
        Assert.Equal(["revoked", "active"], (await KeyboardsAsync(s)).Select(k => k.Status).ToArray());

        await h.SayAsync(s, "Так");
        Assert.Single(await h.AppointmentsAsync(s));
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(second));
        Assert.DoesNotContain("active", (await KeyboardsAsync(s)).Where(k => k.Kind == "booking_confirm").Select(k => k.Status));
        var confirm = Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "confirm_booking"));
        Assert.Equal("text", Field(confirm, "via"));
        Assert.Contains(Kb(h.Bot.Messages.Last()), b => b.Text.Contains("За 24 год")); // меню нагадування кнопками і після текстового «Так»
    }

    [SkippableFact]
    public async Task forged_token_and_a_press_from_another_chat_have_no_effect()
    {
        var h = H(); var s = await h.SeedAsync();
        var (conv, summary) = await SummaryAsync(h, s);

        await h.PressAsync(s, "by:AAAAAAAAAAAAAAAAAAAAAA", "999"); // випадковий токен
        Assert.Equal(ChatTemplates.ActionStale, h.Bot.Messages.Last().Text);

        // інший користувач (інший чат того ж бота) натискає кнопку з чужого підсумку
        await h.SayAsync(s, "привіт", chat: "200");
        var foreign = await h.PressAsync(s, Data(summary, "Так, записатися"), Ref(summary), chat: "200");
        Assert.NotEqual(conv, foreign);
        Assert.Empty(await h.AppointmentsAsync(s));
        Assert.Equal("shown", Assert.Single(await h.ProposalsAsync(s)).Status);
        Assert.Equal("active", Assert.Single(await KeyboardsAsync(s)).Status); // чужа клавіатура не змінена
        Assert.Equal("200", h.Bot.Messages.Last().ChatId);

        // повідомлення-посилання іншого повідомлення: ref_mismatch
        await h.PressAsync(s, Data(summary, "Так, записатися"), "12345");
        Assert.Empty(await h.AppointmentsAsync(s));
    }

    // ================================================================ handoff

    [SkippableFact]
    public async Task during_handoff_yes_is_blocked_with_one_notice_held_and_stop_still_works()
    {
        var h = H(); var s = await h.SeedAsync();
        var (conv, summary) = await SummaryAsync(h, s);
        await SetHandoffAsync(s, conv);

        await PressAsync(h, s, summary, "Так, записатися");
        Assert.Equal(ChatTemplates.ActionHandoff, h.Bot.Messages.Last().Text);
        Assert.Empty(await h.AppointmentsAsync(s));
        Assert.Equal(1, await InboundCountAsync(h, s, "held"));
        Assert.NotNull((await h.ConversationAsync(s)).AwaitingStaffSince);
        Assert.Equal("revoked", Assert.Single(await KeyboardsAsync(s)).Status);

        var count = h.Bot.Messages.Count;
        await h.PressAsync(s, "i:book", Ref(summary)); // раз на 10 хв
        Assert.Equal(count, h.Bot.Messages.Count);

        await h.PressAsync(s, "i:stop", Ref(summary)); // дозволено
        Assert.Equal(ChatTemplates.Stop, h.Bot.Messages.Last().Text);
        Assert.Equal("handoff", (await h.ConversationAsync(s)).AiState);
    }

    [SkippableFact]
    public async Task entering_handoff_revokes_ai_keyboards_and_clears_their_buttons()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, summary) = await SummaryAsync(h, s);
        await h.SayAsync(s, "це жахливо, скарга");

        Assert.Equal("handoff", (await h.ConversationAsync(s)).AiState);
        Assert.Equal("revoked", Assert.Single(await KeyboardsAsync(s)).Status);
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(summary));
    }

    [SkippableFact]
    public async Task human_button_hands_off_and_commands_other_than_stop_are_held_in_handoff()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        await PressAsync(h, s, LastWithButtons(h), "Адміністратор");
        var conv = await h.ConversationAsync(s);
        Assert.Equal(("handoff", "human_requested"), (conv.AiState, conv.HandoffReason));
        Assert.Equal(ChatTemplates.Handoff, h.Bot.Messages.Last().Text);
        Assert.Contains(await h.ActionsAsync(s), a => a.ToolName == "handoff" && Field(a, "source") == "button");

        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.ActionHandoff, h.Bot.Messages.Last().Text);
        await h.SayAsync(s, "/stop", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.Stop, h.Bot.Messages.Last().Text);
        Assert.Empty(h.Ai.Calls);
    }

    // ================================================================ режими

    [SkippableFact]
    public async Task suggest_sends_no_ai_keyboards_but_answers_deterministic_commands()
    {
        var h = H(); var s = await h.SeedAsync(aiMode: "suggest");
        h.Ai.Enqueue(Tool("find_slots", new { serviceId = s.ServiceId, specialistId = s.SpecialistId, date = LocalDate(s) }), Text("Чернетка зі слотами."));
        await h.SayAsync(s, "хочу стрижку");
        var draft = Assert.Single(await h.OutboundAsync(s));
        Assert.Equal(("draft", null), (draft.Status, draft.Actions));
        Assert.Empty(h.Bot.Messages);

        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        await h.SayAsync(s, "/help", kind: InboundKinds.Command);
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        Assert.Empty(h.Bot.Messages); // без шаблону у suggest

        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.MyEmpty, h.Bot.Messages.Last().Text);
        await h.SayAsync(s, "/stop", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.Stop, h.Bot.Messages.Last().Text);
        Assert.Empty(await KeyboardsAsync(s));
    }

    // ================================================================ без кнопок

    [SkippableFact]
    public async Task buttons_disabled_keep_the_glued_summary_and_text_instructions()
    {
        var h = H(new Dictionary<string, string?>()); var s = await h.SeedAsync();
        await h.ShowSummaryAsync(s, intro: "Пропоную цей час.");
        var only = Assert.Single(h.Bot.Messages);
        Assert.Null(only.Markup);
        Assert.Contains("Відповідайте «Так», щоб записатися", only.Text);
        Assert.Empty(await KeyboardsAsync(s));

        await h.SayAsync(s, "Так");
        var confirmed = h.Bot.Messages.Last();
        Assert.Null(confirmed.Markup);
        Assert.Contains("Відповідайте цифрою", confirmed.Text);
        Assert.DoesNotContain("кнопк", confirmed.Text);

        await h.SayAsync(s, "/help", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.Help, h.Bot.Messages.Last().Text);
        Assert.Null(h.Bot.Messages.Last().Markup);
        await h.SayAsync(s, "/cancel", kind: InboundKinds.Command); // без кнопок скасування - через адміністратора
        Assert.Equal("cancel_request", (await h.ConversationAsync(s)).HandoffReason);
    }

    [SkippableFact]
    public async Task channel_flag_buttons_false_and_adapters_without_actions_get_text_only()
    {
        var h = H(); var s = await h.SeedAsync("{\"assistant\":{\"enabled\":true,\"buttons\":false}}");
        await h.ShowSummaryAsync(s);
        Assert.Single(h.Bot.Messages);
        Assert.All(h.Bot.Messages, m => Assert.Null(m.Markup));

        var h2 = H(mock: true); var s2 = await h2.SeedAsync();
        await h2.ShowSummaryAsync(s2);
        var sent = Assert.Single(h2.MockChannel!.Sent);
        Assert.Null(sent.Actions);
        Assert.Contains("Відповідайте «Так»", sent.Text);
        Assert.Empty(await KeyboardsAsync(s2));
    }

    // ================================================================ команди

    [SkippableFact]
    public async Task slash_commands_are_answered_by_code_without_llm()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.BookStart, h.Bot.Messages.Last().Text);
        h.Clock.Advance(TimeSpan.FromSeconds(31)); // TASK-759 (M3): однакова довідкова відповідь не частіше за IdenticalTemplateThrottle
        await h.SayAsync(s, "/zapys@MyBot", kind: InboundKinds.Command);
        Assert.Equal(2, h.Bot.Messages.Count(m => m.Text == ChatTemplates.BookStart));
        await h.SayAsync(s, "/HELP", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.Help, h.Bot.Messages.Last().Text);
        Assert.Contains(Kb(h.Bot.Messages.Last()), b => b.Data == "i:human");
        await h.SayAsync(s, "/stop", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.Stop, h.Bot.Messages.Last().Text);
        Assert.NotNull((await h.ConversationAsync(s)).RemindersStopAt);
        await h.SayAsync(s, "/nonsense", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.TextOnly, h.Bot.Messages.Last().Text);

        Assert.Empty(h.Ai.Calls);
        Assert.Empty(await h.UsageAsync(s));
        Assert.Equal(0, await InboundCountAsync(h, s, "received"));
    }

    [SkippableFact]
    public async Task stop_button_equals_the_stop_word()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "привіт", kind: InboundKinds.Command);
        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        await h.PressAsync(s, "i:stop", "500");
        Assert.Equal(ChatTemplates.Stop, h.Bot.Messages.Last().Text);
        Assert.NotNull((await h.ConversationAsync(s)).RemindersStopAt);
        Assert.Contains(await h.ActionsAsync(s), a => a.ToolName == "reminder_stop" && Field(a, "via") == "button");
    }

    // ================================================================ нагадування: текст після кнопок

    [SkippableFact]
    public async Task text_answer_to_the_reminder_menu_revokes_its_keyboard_and_the_button_is_stale()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, confirmed) = await BookByButtonAsync(h, s);
        var data = Data(confirmed, "За 1 год");
        await h.SayAsync(s, "2");
        Assert.StartsWith("Готово: нагадаємо за 2 год", h.Bot.Messages.Last().Text);
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(confirmed));
        Assert.Equal("revoked", (await KeyboardsAsync(s)).Single(k => k.Kind == "reminder_choice").Status);

        await h.PressAsync(s, data, Ref(confirmed));
        Assert.Equal(ChatTemplates.ActionStale, h.Bot.Messages.Last().Text);
        await using var ctx = _db.CreateContext(s.TenantId);
        Assert.Equal([(short)120], (await ctx.ClientContactPreferences.AsNoTracking().SingleAsync()).OffsetsMinutes);
    }

    [SkippableFact]
    public async Task reminder_none_button_stops_reminders_for_the_visit()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, confirmed) = await BookByButtonAsync(h, s);
        await PressAsync(h, s, confirmed, "Не нагадувати");
        Assert.StartsWith("Добре, нагадувань не буде", h.Bot.Messages.Last().Text);
        await using var ctx = _db.CreateContext(s.TenantId);
        Assert.Equal("none", (await ctx.ClientContactPreferences.AsNoTracking().SingleAsync()).Method);
        Assert.DoesNotContain(Kb(confirmed), b => b.Text.Contains("Зателефонувати")); // без телефону дзвінка в меню немає
    }

    // ================================================================ ШІ не створює кнопок

    [SkippableFact]
    public async Task callback_lookalikes_and_json_in_model_text_stay_plain_text()
    {
        var h = H(); var s = await h.SeedAsync();
        h.Ai.Enqueue(Text("Натисніть by:AAAAAAAAAAAAAAAAAAAAAA {\"inline_keyboard\":[[{\"text\":\"x\",\"callback_data\":\"i:human\"}]]}"));
        await h.SayAsync(s, "привіт");
        var m = Assert.Single(h.Bot.Messages);
        Assert.Null(m.Markup);
        Assert.Null(Assert.Single(await h.OutboundAsync(s)).Actions);
        Assert.Empty(await KeyboardsAsync(s));
    }

    // ================================================================ ліміт

    [SkippableFact]
    public async Task press_rate_limit_gives_one_template_per_minute_and_skips_the_press()
    {
        var h = H(new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true", ["ChatButtons:MaxCallbacksPerConversationPerMinute"] = "5" });
        var s = await h.SeedAsync();
        for (var i = 0; i < 5; i++) await h.PressAsync(s, "i:my", "500");
        Assert.Equal(5, h.Bot.Messages.Count(m => m.Text == ChatTemplates.MyEmpty));

        await h.PressAsync(s, "i:my", "500");
        await h.PressAsync(s, "i:my", "500");
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.RateLimited));
        Assert.Equal(5, h.Bot.Messages.Count(m => m.Text == ChatTemplates.MyEmpty));
        Assert.Equal(2, await InboundCountAsync(h, s, "skipped"));
    }

    // ================================================================ прибирання кнопок

    [SkippableFact]
    public async Task clear_failures_are_retried_by_the_sweeper_at_most_three_times()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, summary) = await SummaryAsync(h, s);
        h.Bot.FailEdits = 100;
        await PressAsync(h, s, summary, "Ні"); // спроба 1 (inline)
        var kb = Assert.Single(await KeyboardsAsync(s));
        Assert.Equal((short)1, kb.ClearAttempts);
        Assert.Null(kb.MarkupClearedAt);

        await h.Worker.SweepOnceAsync(default); // 2
        await h.Worker.SweepOnceAsync(default); // 3
        await h.Worker.SweepOnceAsync(default); // більше не пробує
        kb = Assert.Single(await KeyboardsAsync(s));
        Assert.Equal((short)3, kb.ClearAttempts);
        Assert.Null(kb.MarkupClearedAt);
        Assert.Equal(3, h.Bot.CountUrls("editMessageReplyMarkup"));
    }

    [SkippableFact]
    public async Task sweeper_clears_the_markup_after_a_transient_failure_and_expires_due_keyboards()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, summary) = await SummaryAsync(h, s);
        h.Bot.FailEdits = 1;
        await PressAsync(h, s, summary, "Ні");
        Assert.Null(Assert.Single(await KeyboardsAsync(s)).MarkupClearedAt);

        await h.Worker.SweepOnceAsync(default);
        Assert.NotNull(Assert.Single(await KeyboardsAsync(s)).MarkupClearedAt);
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(summary));

        // протермінована активна клавіатура: sweeper -> expired -> розмітку прибрано
        var (_, summary2) = await SummaryAsync(h, s);
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        await h.Worker.SweepOnceAsync(default);
        var last = (await KeyboardsAsync(s)).Last();
        Assert.Equal("expired", last.Status);
        Assert.NotNull(last.MarkupClearedAt);
        Assert.Contains(h.Bot.Edits, e => e.MessageId == Ref(summary2));
    }

    // ================================================================ ПД у логах

    [SkippableFact]
    public async Task logs_contain_no_tokens_callback_data_chat_ids_names_or_bot_token()
    {
        var h = H(); var s = await h.SeedAsync();
        var (_, confirmed) = await BookByButtonAsync(h, s);
        await PressAsync(h, s, confirmed, "За 2 год");
        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        await PressAsync(h, s, h.Bot.Messages.Last(), "Скасувати");
        await h.PressAsync(s, "by:AAAAAAAAAAAAAAAAAAAAAA", "1");

        var logs = h.Logs.All;
        foreach (var k in await KeyboardsAsync(s)) Assert.DoesNotContain(k.Token, logs);
        Assert.DoesNotContain(ChatHarness.BotToken, logs);
        Assert.DoesNotContain("by:AAAA", logs);
        Assert.DoesNotContain("Марина", logs);
        Assert.DoesNotContain("Стрижка", logs);
        Assert.Contains("chat_callback", logs);
    }
}
