using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Inbox;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BeautyCrm.Tests.Conversations.ChatHarness;

namespace BeautyCrm.Tests.Conversations;

/// <summary>Розбір відповіді на меню нагадувань і команди (§22.7, TASK-740): чисті функції, без БД.</summary>
public sealed class ReminderChoiceParserTests
{
    [Theory]
    [InlineData("1", new short[] { 60 })]
    [InlineData("1 год", new short[] { 60 })]
    [InlineData("годину", new short[] { 60 })]
    [InlineData("за годину", new short[] { 60 })]
    [InlineData("2", new short[] { 120 })]
    [InlineData("2 год", new short[] { 120 })]
    [InlineData("2 години", new short[] { 120 })]
    [InlineData("3", new short[] { 1440 })]
    [InlineData("24", new short[] { 1440 })]
    [InlineData("24 год", new short[] { 1440 })]
    [InlineData("за 24 години", new short[] { 1440 })]
    [InlineData("добу", new short[] { 1440 })]
    [InlineData("за добу", new short[] { 1440 })]
    [InlineData("за день", new short[] { 1440 })]
    [InlineData("2 3", new short[] { 120, 1440 })]
    [InlineData("3, 2", new short[] { 120, 1440 })]
    [InlineData("1 і 3", new short[] { 60, 1440 })]
    [InlineData("2 та 3", new short[] { 120, 1440 })]
    [InlineData("2+3", new short[] { 120, 1440 })]
    [InlineData("1,2,3", new short[] { 60, 120, 1440 })]
    [InlineData("2 2", new short[] { 120 })]
    [InlineData("  2 3! 👍 ", new short[] { 120, 1440 })]
    public void parse_telegram_offsets_for_menu_numbers_and_words(string text, short[] expected)
    {
        var r = ReminderChoiceParser.Parse(text);
        Assert.Equal(ReminderParseKind.Choice, r.Kind);
        Assert.Equal("telegram", r.Choice!.Method);
        Assert.Equal(expected, r.Choice.Offsets);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("не треба")]
    [InlineData("Не нагадувати")]
    [InlineData("ні")]
    [InlineData("не потрібно")]
    [InlineData("нічого")]
    public void parse_none_phrases(string text)
    {
        var r = ReminderChoiceParser.Parse(text);
        Assert.Equal(ReminderParseKind.Choice, r.Kind);
        Assert.Equal(("none", 0), (r.Choice!.Method, r.Choice.Offsets.Count));
    }

    [Theory]
    [InlineData("зателефонувати")]
    [InlineData("Дзвінок")]
    [InlineData("подзвоніть")]
    [InlineData("зателефонуйте!")]
    public void parse_call_phrases(string text)
    {
        var r = ReminderChoiceParser.Parse(text);
        Assert.Equal(ReminderParseKind.Call, r.Kind);
        Assert.Equal("call", r.Choice!.Method);
        Assert.Equal(new short[] { 1440 }, r.Choice.Offsets);
    }

    [Theory]
    [InlineData("1 4")]
    [InlineData("4 2")]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("3 год")]
    [InlineData("12 год")]
    [InlineData("99999999999")]
    [InlineData("2 зателефонувати")]
    public void parse_invalid_digits_keep_the_question(string text) =>
        Assert.Equal(ReminderParseKind.Invalid, ReminderChoiceParser.Parse(text).Kind);

    [Theory]
    [InlineData("а скільки коштує?")]
    [InlineData("хочу ще на манікюр")]
    [InlineData("ок")]
    [InlineData("2 будь ласка")]
    [InlineData("")]
    [InlineData("👍")]
    [InlineData("за")]
    [InlineData("1 2 3 4 5 6 7 8 9 1 2 3 4 5")]
    public void parse_other_text_is_not_a_choice(string text) =>
        Assert.Equal(ReminderParseKind.NotAChoice, ReminderChoiceParser.Parse(text).Kind);

    [Theory]
    [InlineData("Стоп", false, true)]
    [InlineData("stop", false, true)]
    [InlineData(" СТОП! ", true, true)]
    [InlineData("не нагадувати", false, true)]
    [InlineData("не нагадувати", true, false)] // під питанням це пункт меню, а не відписка
    [InlineData("стоп, але потім", false, false)]
    [InlineData("не зупиняйся", false, false)]
    [InlineData("/stop", false, false)] // slash-команди - TASK-753
    public void stop_command_is_an_exact_match(string text, bool promptActive, bool expected) =>
        Assert.Equal(expected, ReminderCommands.IsStop(text, promptActive));

    [Theory]
    [InlineData("нагадування", true)]
    [InlineData("Змінити нагадування", true)]
    [InlineData("нагадати", true)]
    [InlineData("а нагадування будуть?", false)]
    public void show_menu_command_is_an_exact_match(string text, bool expected) =>
        Assert.Equal(expected, ReminderCommands.IsShowMenu(text));

    [Fact]
    public void templates_are_plain_text_without_markdown_and_menu_follows_the_numbering()
    {
        var offer = ChatTemplates.ReminderOffer(ReminderChoiceMenu.Options(false));
        Assert.Contains("1 — за 1 годину", offer);
        Assert.Contains("2 — за 2 години", offer);
        Assert.Contains("3 — за 24 години", offer);
        Assert.Contains("4 — не нагадувати", offer);
        Assert.DoesNotContain("зателефонувати", offer);
        Assert.Contains("«зателефонувати»", ChatTemplates.ReminderOffer(ReminderChoiceMenu.Options(true)));
        Assert.Equal("Готово: нагадаємо за 24 год і за 2 год у Telegram. Змінити — напишіть «нагадування».",
            ChatTemplates.ReminderSet(new ReminderChoice("telegram", [120, 1440])));
        Assert.StartsWith("Добре, нагадувань не буде.", ChatTemplates.ReminderSet(ReminderChoice.None));
        Assert.StartsWith("Адміністратор зателефонує", ChatTemplates.ReminderSet(ReminderChoice.Call));
        foreach (var t in new[] { offer, ChatTemplates.Stop, ChatTemplates.ReminderInvalid, ChatTemplates.ReminderCallUnavailable,
                     ChatTemplates.ReminderNoAppointments, ChatTemplates.ReminderSet(new ReminderChoice("telegram", [60, 120, 1440])) })
            Assert.DoesNotMatch("[*_`#~<>]", t);
    }
}

/// <summary>
/// TASK-740 (§22.12 тести 11-13, 16): вибір нагадування, «Стоп», handoff з `held` на реальному PostgreSQL (RLS), mock-провайдері ШІ
/// і fake Telegram. Реальних Telegram/Anthropic викликів немає.
/// </summary>
public sealed class ReminderChatTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ReminderChatTests(PostgresRlsFixture db) => _db = db;

    private ChatHarness H(ChatAssistantOptions? o = null) => ChatHarness.Create(_db, o);

    private static string UserText(BeautyCrm.Infrastructure.AI.Beauty.Providers.MockAiCall call) =>
        string.Join("\n", call.Messages.Where(m => m.Role == "user").SelectMany(m => m.Blocks)
            .OfType<BeautyCrm.Infrastructure.AI.Beauty.AiTextBlock>().Select(b => b.Text));

    /// <summary>Підсумок + «Так»: запис створено, у відповіді меню нагадувань (вподобання ще немає).</summary>
    private static async Task<Guid> BookAsync(ChatHarness h, ChatSeed s, DateTimeOffset? start = null)
    {
        var conv = await h.ShowSummaryAsync(s, start: start);
        await h.SayAsync(s, "Так");
        return conv;
    }

    private async Task<List<ClientContactPreference>> PrefsAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.ClientContactPreferences.AsNoTracking().ToListAsync();
    }

    private async Task<List<Reminder>> RemindersAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.Reminders.AsNoTracking().OrderBy(r => r.ScheduledAt).ToListAsync();
    }

    private async Task<Client> ClientAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.Clients.AsNoTracking().SingleAsync();
    }

    private async Task<string> LastBodyAsync(ChatHarness h, ChatSeed s) => (await h.OutboundAsync(s)).Last().Body;

    // ------------------------------------------------------------------ 12. вибір у чаті

    [SkippableFact]
    public async Task yes_without_a_preference_books_with_none_and_adds_the_menu_to_the_same_message()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.ShowSummaryAsync(s);
        var before = (await h.OutboundAsync(s)).Count;
        await h.SayAsync(s, "Так");

        var outbound = await h.OutboundAsync(s);
        Assert.Equal(before + 1, outbound.Count); // T-confirmed + меню - одне повідомлення
        var body = outbound.Last().Body;
        Assert.StartsWith(ChatTemplates.ConfirmedHeader, body);
        Assert.Contains("Нагадати вам про візит? Відповідайте цифрою:", body);
        Assert.Contains("1 — за 1 годину", body);
        Assert.Contains("4 — не нагадувати", body);
        Assert.DoesNotContain("зателефонувати", body); // телефону немає
        Assert.DoesNotMatch("[*`#~]", body);

        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(("none", 0), (appt.ReminderMethod, appt.ReminderOffsets.Length));
        Assert.Empty(await RemindersAsync(s));
        var c = await h.ConversationAsync(s);
        Assert.Equal(("reminder_choice", appt.Id), (c.PendingPrompt, c.PendingPromptAppointmentId));
        Assert.InRange(c.PendingPromptExpiresAt!.Value, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25)); // min(starts_at, now+24 год)
    }

    [SkippableFact]
    public async Task choice_2_3_saves_the_preference_replans_the_booking_and_never_calls_the_llm()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, "2 3");

        Assert.Equal(calls, h.Ai.Calls.Count); // крок 4a без LLM
        Assert.Equal("Готово: нагадаємо за 24 год і за 2 год у Telegram. Змінити — напишіть «нагадування».", await LastBodyAsync(h, s));
        var pref = Assert.Single(await PrefsAsync(s));
        var conv = await h.ConversationAsync(s);
        Assert.Equal(("telegram", "chat", conv.Id), (pref.Method, pref.Source, pref.TelegramConversationId));
        Assert.Equal(new short[] { 120, 1440 }, pref.OffsetsMinutes);
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(("telegram", "custom"), (appt.ReminderMethod, appt.ReminderOption.ToString().ToLowerInvariant()));
        Assert.Equal(new short[] { 120, 1440 }, appt.ReminderOffsets);
        var rows = await RemindersAsync(s);
        Assert.Equal(2, rows.Count(r => r.Status == ReminderStatus.Scheduled));
        Assert.Equal(appt.StartsAt.AddHours(-24), rows.Single(r => r.OffsetMinutes == 1440).ScheduledAt);
        Assert.Null(conv.PendingPrompt);
        Assert.Null(conv.PendingPromptAppointmentId);
        Assert.Null(conv.PendingPromptExpiresAt);
        var action = Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "reminder_choice"));
        Assert.Equal(conv.Id, action.ConversationId);
        Assert.Contains($"appointment:{appt.Id}", action.Result);
        Assert.All((await h.MessagesAsync(s)).Where(m => m.Direction == MessageDirection.Inbound), m => Assert.Equal("processed", m.Status));
    }

    [SkippableTheory]
    [InlineData("24", "telegram", new short[] { 1440 })]
    [InlineData("1", "telegram", new short[] { 60 })]
    [InlineData("за годину", "telegram", new short[] { 60 })]
    [InlineData("4", "none", new short[0])]
    [InlineData("не треба", "none", new short[0])]
    public async Task single_choices_are_applied(string answer, string method, short[] offsets)
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, answer);

        var pref = Assert.Single(await PrefsAsync(s));
        Assert.Equal((method, "chat"), (pref.Method, pref.Source));
        Assert.Equal(offsets, pref.OffsetsMinutes);
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(method, appt.ReminderMethod);
        Assert.Equal(offsets.Length, (await RemindersAsync(s)).Count(r => r.Status == ReminderStatus.Scheduled));
        Assert.False((await ClientAsync(s)).Unsubscribed); // «не нагадувати» під питанням - вибір запису, не відписка
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
    }

    [SkippableTheory]
    [InlineData("1 4")]
    [InlineData("5")]
    [InlineData("3 год")]
    public async Task wrong_choice_gets_t_reminder_invalid_and_the_question_stays(string answer)
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, answer);

        Assert.Equal(ChatTemplates.ReminderInvalid, await LastBodyAsync(h, s));
        Assert.Equal(calls, h.Ai.Calls.Count);
        Assert.Empty(await PrefsAsync(s));
        Assert.Equal("reminder_choice", (await h.ConversationAsync(s)).PendingPrompt);

        await h.SayAsync(s, "2"); // питання лишилось: правильна відповідь спрацьовує
        Assert.Equal(new short[] { 120 }, Assert.Single(await PrefsAsync(s)).OffsetsMinutes);
    }

    [SkippableFact]
    public async Task other_text_drops_the_question_and_the_turn_goes_to_the_llm()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        var calls = h.Ai.Calls.Count;
        h.Ai.Enqueue(Text("Стрижка коштує 500 грн."));

        await h.SayAsync(s, "а скільки коштує?");

        Assert.Equal(calls + 1, h.Ai.Calls.Count);
        Assert.Equal("Стрижка коштує 500 грн.", await LastBodyAsync(h, s));
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
        Assert.Empty(await PrefsAsync(s));

        await h.SayAsync(s, "2"); // питання знято: «2» - звичайний текст для моделі
        Assert.Empty(await PrefsAsync(s));
        Assert.Equal(calls + 2, h.Ai.Calls.Count);
    }

    [SkippableFact]
    public async Task answer_after_the_ttl_is_not_parsed()
    {
        var h = H(new ChatAssistantOptions { Enabled = true, SweepMinAge = TimeSpan.Zero, ReminderPromptTtlHours = 1 });
        var s = await h.SeedAsync();
        await BookAsync(h, s);
        Assert.InRange((await h.ConversationAsync(s)).PendingPromptExpiresAt!.Value, DateTimeOffset.UtcNow.AddMinutes(50), DateTimeOffset.UtcNow.AddMinutes(70));
        var calls = h.Ai.Calls.Count;
        h.Clock.Advance(TimeSpan.FromMinutes(90));

        await h.SayAsync(s, "2 3");

        Assert.Empty(await PrefsAsync(s));
        Assert.Equal(calls + 1, h.Ai.Calls.Count);
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
    }

    [SkippableFact]
    public async Task call_without_a_phone_is_unavailable_and_the_question_stays()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, "зателефонувати");

        Assert.Equal(ChatTemplates.ReminderCallUnavailable, await LastBodyAsync(h, s));
        Assert.Equal(calls, h.Ai.Calls.Count);
        Assert.Empty(await PrefsAsync(s));
        Assert.Equal("reminder_choice", (await h.ConversationAsync(s)).PendingPrompt);
        Assert.Empty(await RemindersAsync(s));
    }

    [SkippableFact]
    public async Task reminders_command_shows_the_menu_for_the_nearest_booking_and_call_works_with_a_phone()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "4"); // питання закрито
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Clients.ExecuteUpdateAsync(x => x.SetProperty(c => c.Phone, "+380501234567"));
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, "Нагадування");

        var menu = await LastBodyAsync(h, s);
        Assert.StartsWith("Найближчий візит: Стрижка, ", menu);
        Assert.Contains("«зателефонувати»", menu); // телефон є, дзвінки ввімкнено за замовчуванням
        Assert.Equal(calls, h.Ai.Calls.Count);
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        var c = await h.ConversationAsync(s);
        Assert.Equal(("reminder_choice", appt.Id), (c.PendingPrompt, c.PendingPromptAppointmentId));

        await h.SayAsync(s, "зателефонувати");

        Assert.StartsWith("Адміністратор зателефонує вам напередодні.", await LastBodyAsync(h, s));
        var pref = Assert.Single(await PrefsAsync(s));
        Assert.Equal("call", pref.Method);
        Assert.Equal(new short[] { 1440 }, pref.OffsetsMinutes);
        await using var read = _db.CreateContext(s.TenantId);
        var task = await read.CallTasks.AsNoTracking().SingleAsync(t => t.Status == "open");
        Assert.Equal((appt.Id, (short)1440), (task.AppointmentId, task.OffsetMinutes!.Value));
    }

    [SkippableFact]
    public async Task reminders_command_without_bookings_explains_and_does_not_set_a_question()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "нагадування");

        Assert.Equal(ChatTemplates.ReminderNoAppointments, await LastBodyAsync(h, s));
        Assert.Empty(h.Ai.Calls);
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
    }

    // ------------------------------------------------------------------ 13. вподобання застосовується

    [SkippableFact]
    public async Task second_booking_follows_the_saved_preference_without_a_question()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "2 3");

        await BookAsync(h, s, s.Start.AddDays(1));

        var body = await LastBodyAsync(h, s);
        Assert.StartsWith(ChatTemplates.ConfirmedHeader, body);
        Assert.Contains("Нагадаємо за 24 год і за 2 год у Telegram.", body);
        Assert.DoesNotContain("Нагадати вам про візит?", body);
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
        var second = (await h.AppointmentsAsync(s)).OrderBy(a => a.StartsAt).Last();
        Assert.Equal("telegram", second.ReminderMethod);
        Assert.Equal(new short[] { 120, 1440 }, second.ReminderOffsets);
        Assert.Equal(4, (await RemindersAsync(s)).Count(r => r.Status == ReminderStatus.Scheduled));
    }

    [SkippableFact]
    public async Task two_unanswered_questions_keep_only_the_latest_and_the_answer_applies_to_it()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        var first = (await h.AppointmentsAsync(s)).Single();
        await BookAsync(h, s, s.Start.AddDays(1)); // перше питання знімає звичайний текст, друга пропозиція ставить нове
        var second = (await h.AppointmentsAsync(s)).Single(a => a.Id != first.Id);
        var c = await h.ConversationAsync(s);
        Assert.Equal(second.Id, c.PendingPromptAppointmentId);

        await h.SayAsync(s, "1");

        var rows = await RemindersAsync(s);
        var appts = await h.AppointmentsAsync(s);
        Assert.Equal("telegram", appts.Single(a => a.Id == second.Id).ReminderMethod);
        Assert.Equal("none", appts.Single(a => a.Id == first.Id).ReminderMethod); // перше питання втрачено (одне активне питання на розмову)
        Assert.Single(rows);
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
    }

    [SkippableFact]
    public async Task repeated_answer_is_a_normal_message_and_changes_nothing()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "2");
        var calls = h.Ai.Calls.Count;
        var outbound = (await h.OutboundAsync(s)).Count;
        var rows = (await RemindersAsync(s)).Count;

        await h.SayAsync(s, "2");

        Assert.Equal(calls + 1, h.Ai.Calls.Count); // питання вже немає: «2» іде моделі
        Assert.Equal(outbound + 1, (await h.OutboundAsync(s)).Count);
        Assert.Equal(rows, (await RemindersAsync(s)).Count);
        Assert.Equal(new short[] { 120 }, Assert.Single(await PrefsAsync(s)).OffsetsMinutes);
    }

    [SkippableFact]
    public async Task answer_for_a_cancelled_booking_is_answered_with_a_template_and_the_question_is_dropped()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Appointments.ExecuteUpdateAsync(a => a.SetProperty(x => x.Status, AppointmentStatus.Cancelled));

        await h.SayAsync(s, "2");

        Assert.Equal(ChatTemplates.ReminderAppointmentInactive, await LastBodyAsync(h, s));
        Assert.Empty(await PrefsAsync(s));
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
    }

    // ------------------------------------------------------------------ 11. «Стоп»

    [SkippableTheory]
    [InlineData("Стоп")]
    [InlineData("stop")]
    [InlineData("не нагадувати")]
    public async Task stop_sets_none_replans_upcoming_unsubscribes_and_never_calls_the_llm(string command)
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "2 3");
        await h.SayAsync(s, "Нагадування"); // новий pending_prompt: «Стоп» його знімає
        var calls = h.Ai.Calls.Count;

        if (command == "не нагадувати")
        {
            // під активним питанням «не нагадувати» - пункт меню, тож спершу знімаємо питання звичайним текстом
            h.Ai.Enqueue(Text("Добре."));
            await h.SayAsync(s, "ні, дякую");
        }
        await h.SayAsync(s, command);

        Assert.Equal(command == "не нагадувати" ? calls + 1 : calls, h.Ai.Calls.Count); // сама команда LLM не викликає
        Assert.Equal(ChatTemplates.Stop, await LastBodyAsync(h, s));
        var pref = Assert.Single(await PrefsAsync(s));
        Assert.Equal(("none", "stop", 0), (pref.Method, pref.Source, pref.OffsetsMinutes.Length));
        Assert.True((await ClientAsync(s)).Unsubscribed);
        var rows = await RemindersAsync(s);
        Assert.Equal(0, rows.Count(r => r.Status == ReminderStatus.Scheduled));
        Assert.All(rows.Where(r => r.OffsetMinutes is 120 or 1440), r => Assert.Equal((ReminderStatus.Cancelled, "replanned"), (r.Status, r.SkipReason)));
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(("none", 0), (appt.ReminderMethod, appt.ReminderOffsets.Length));
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
        Assert.Contains(await h.ActionsAsync(s), a => a.ToolName == "reminder_stop");
    }

    [SkippableFact]
    public async Task stop_with_a_trailing_clause_is_not_a_command()
    {
        var h = H(); var s = await h.SeedAsync();
        h.Ai.Enqueue(Text("Добре, уточніть."));
        await h.SayAsync(s, "стоп, але нагадайте мені про пакет");

        Assert.Single(h.Ai.Calls);
        Assert.Equal("Добре, уточніть.", await LastBodyAsync(h, s));
        Assert.Empty(await PrefsAsync(s));
    }

    [SkippableFact]
    public async Task stop_while_a_question_is_active_is_still_stop_and_not_the_menu_item()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);

        await h.SayAsync(s, "Стоп");

        Assert.Equal(ChatTemplates.Stop, await LastBodyAsync(h, s));
        Assert.Equal("stop", Assert.Single(await PrefsAsync(s)).Source);
        Assert.True((await ClientAsync(s)).Unsubscribed);
    }

    [SkippableFact]
    public async Task stop_works_during_handoff_and_keeps_the_handoff_state()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "2 3");
        await h.SayAsync(s, "Це жахливо, хочу поскаржитись");
        var before = await h.ConversationAsync(s);
        Assert.Equal(("handoff", "complaint"), (before.AiState, before.HandoffReason));
        Assert.NotNull(before.AwaitingStaffSince);
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, "Стоп");

        Assert.Equal(ChatTemplates.Stop, await LastBodyAsync(h, s));
        Assert.Equal(calls, h.Ai.Calls.Count);
        var after = await h.ConversationAsync(s);
        Assert.Equal(("handoff", before.AwaitingStaffSince), (after.AiState, after.AwaitingStaffSince)); // стан розмови не змінено
        Assert.Equal("none", Assert.Single(await PrefsAsync(s)).Method);
        Assert.Equal(0, (await RemindersAsync(s)).Count(r => r.Status == ReminderStatus.Scheduled));
        Assert.Equal("processed", (await h.MessagesAsync(s)).Last(m => m.Direction == MessageDirection.Inbound).Status);
    }

    [SkippableFact]
    public async Task stop_without_a_client_sets_reminders_stop_at_and_the_lazy_client_is_created_with_none()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "Стоп");

        Assert.Equal(ChatTemplates.Stop, Assert.Single(await h.OutboundAsync(s)).Body);
        var c = await h.ConversationAsync(s);
        Assert.NotNull(c.RemindersStopAt);
        Assert.Null(c.ClientId);
        Assert.Empty(h.Ai.Calls);

        await BookAsync(h, s);

        var pref = Assert.Single(await PrefsAsync(s));
        Assert.Equal(("none", "stop"), (pref.Method, pref.Source));
        Assert.True((await ClientAsync(s)).Unsubscribed);
        var body = await LastBodyAsync(h, s);
        Assert.StartsWith(ChatTemplates.ConfirmedHeader, body);
        Assert.DoesNotContain("Нагадати вам про візит?", body); // вибір уже зроблено
        Assert.Contains("Нагадувань про візит не буде.", body);
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
        Assert.Equal("none", Assert.Single(await h.AppointmentsAsync(s)).ReminderMethod);
    }

    [SkippableFact]
    public async Task stop_in_suggest_mode_takes_effect_without_a_reply()
    {
        var h = H(); var s = await h.SeedAsync(aiMode: "suggest");
        await h.SayAsync(s, "Стоп");

        Assert.Empty(await h.OutboundAsync(s));
        Assert.Empty(h.Ai.Calls);
        Assert.NotNull((await h.ConversationAsync(s)).RemindersStopAt);
        Assert.Equal("processed", Assert.Single(await h.MessagesAsync(s)).Status);
    }

    [SkippableFact]
    public async Task stop_in_the_middle_of_a_batch_splits_it_and_keeps_the_order()
    {
        var h = H(); var s = await h.SeedAsync();
        h.Ai.Enqueue(Text("Відповідь А"), Text("Відповідь Б"));
        await h.IngestAsync(s, "привіт");
        await h.IngestAsync(s, "Стоп");
        var (conv, _) = await h.IngestAsync(s, "що по ціні?");

        await h.TurnAsync(s.TenantId, conv!.Value);

        Assert.Equal(new[] { "Відповідь А", ChatTemplates.Stop, "Відповідь Б" }, (await h.OutboundAsync(s)).Select(m => m.Body).ToArray());
        Assert.Equal(2, h.Ai.Calls.Count);
        Assert.All((await h.MessagesAsync(s)).Where(m => m.Direction == MessageDirection.Inbound), m => Assert.Equal("processed", m.Status));
    }

    // ------------------------------------------------------------------ 16. inbox: held, таймер, відповідь менеджера

    private async Task<Actor> AdminAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@t.test", FullName = "Олена Адмін", PasswordHash = "x", Role = Roles.Admin };
        ctx.Add(user);
        await ctx.SaveChangesAsync();
        return new Actor(user.Id, s.TenantId, Roles.Admin, null);
    }

    private static async Task<T> InboxAsync<T>(ChatHarness h, ChatSeed s, Func<InboxService, Task<T>> f)
    {
        using var scope = h.Sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(s.TenantId);
        return await f(scope.ServiceProvider.GetRequiredService<InboxService>());
    }

    [SkippableFact]
    public async Task handoff_sets_marks_and_incoming_during_the_pause_become_held_without_a_reply()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "Це жахливо, хочу поскаржитись");
        var c0 = await h.ConversationAsync(s);
        Assert.NotNull(c0.HandoffAt);
        Assert.NotNull(c0.AwaitingStaffSince);
        Assert.Equal(ChatTemplates.Handoff, Assert.Single(await h.OutboundAsync(s)).Body);

        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.SayAsync(s, "алло, є хто?");
        await h.SayAsync(s, "[sticker]", kind: InboundKinds.Unsupported); // медіа теж held

        var inbound = (await h.MessagesAsync(s)).Where(m => m.Direction == MessageDirection.Inbound).ToList();
        Assert.Equal(new[] { "processed", "held", "held" }, inbound.Select(m => m.Status).ToArray());
        Assert.Single(await h.OutboundAsync(s));
        Assert.Empty(h.Ai.Calls);
        var c1 = await h.ConversationAsync(s);
        Assert.Equal(c0.AwaitingStaffSince, c1.AwaitingStaffSince); // coalesce: перша мітка лишається
        Assert.Equal("handoff", c1.AiState);
    }

    [SkippableFact]
    public async Task timer_without_a_manager_reply_returns_the_ai_keeps_the_mark_and_history_includes_held()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "Це жахливо, хочу поскаржитись");
        await h.SayAsync(s, "алло, є хто?"); // held
        var c0 = await h.ConversationAsync(s);

        h.Clock.Advance(TimeSpan.FromHours(13));
        h.Ai.Enqueue(Text("Вітаю знову!"));
        await h.SayAsync(s, "добрий день, хочу дізнатись ціни");

        var call = Assert.Single(h.Ai.Calls);
        Assert.Contains("алло, є хто?", UserText(call)); // історія LLM включає held
        Assert.Equal("Вітаю знову!", await LastBodyAsync(h, s));
        var c1 = await h.ConversationAsync(s);
        Assert.Equal("active", c1.AiState);
        Assert.Equal(c0.AwaitingStaffSince, c1.AwaitingStaffSince); // менеджер бачить пропущену розмову
        Assert.Equal("held", (await h.MessagesAsync(s)).Single(m => m.Body == "алло, є хто?").Status); // ретроспективно не обробляється
    }

    [SkippableFact]
    public async Task manager_reply_makes_the_pause_indefinite_and_resume_ai_clears_held_without_answers()
    {
        var h = H(); var s = await h.SeedAsync();
        var admin = await AdminAsync(s);
        var conv = await h.SayAsync(s, "Це жахливо, хочу поскаржитись");
        await h.SayAsync(s, "алло, є хто?");

        var reply = await InboxAsync(h, s, i => i.ReplyAsync(admin, conv, "reply-key-740-0001", "Добрий день, зараз розберемось"));
        Assert.True(reply.IsOk, reply.Error?.Code);
        Assert.Contains(h.Bot.Messages, m => m.Text == "Добрий день, зараз розберемось"); // fake Telegram отримав текст
        var afterReply = await h.ConversationAsync(s);
        Assert.Equal(("handoff", null, null), (afterReply.AiState, afterReply.AiPausedUntil, afterReply.AwaitingStaffSince));
        Assert.Equal("processed", (await h.MessagesAsync(s)).Single(m => m.Body == "алло, є хто?").Status); // held -> processed відповіддю менеджера

        // Клієнт пише далі: ШІ мовчить навіть після 12 год (пауза безстрокова), повідомлення видиме менеджеру.
        h.Clock.Advance(TimeSpan.FromHours(13));
        await h.SayAsync(s, "дякую, чекаю");
        Assert.Empty(h.Ai.Calls);
        Assert.Equal("held", (await h.MessagesAsync(s)).Single(m => m.Body == "дякую, чекаю").Status);
        var awaiting = await h.ConversationAsync(s);
        Assert.Equal("handoff", awaiting.AiState);
        Assert.NotNull(awaiting.AwaitingStaffSince); // «потрібна відповідь» знову

        var outboundBefore = (await h.OutboundAsync(s)).Count;
        var resumed = await InboxAsync(h, s, i => i.ResumeAiAsync(admin, conv, default));
        Assert.True(resumed.IsOk);
        Assert.Equal("processed", (await h.MessagesAsync(s)).Single(m => m.Body == "дякую, чекаю").Status);
        Assert.Equal(outboundBefore, (await h.OutboundAsync(s)).Count); // старі held без відповіді
        Assert.Empty(h.Ai.Calls);
        var active = await h.ConversationAsync(s);
        Assert.Equal(("active", null), (active.AiState, active.AwaitingStaffSince));

        h.Ai.Enqueue(Text("Слухаю вас."));
        await h.SayAsync(s, "а що з ціною?");
        Assert.Single(h.Ai.Calls);
        Assert.Equal("Слухаю вас.", await LastBodyAsync(h, s));
    }
}
