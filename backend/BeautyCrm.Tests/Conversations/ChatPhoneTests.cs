using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BeautyCrm.Tests.Conversations.ChatHarness;

namespace BeautyCrm.Tests.Conversations;

/// <summary>Розбір ручного номера (§25.5.3, сценарій 5): чисті функції, без БД.</summary>
public sealed class ChatPhoneParserTests
{
    [Theory]
    [InlineData("+380501112233")]
    [InlineData("380501112233")]
    [InlineData("0501112233")]
    [InlineData("050 111 22 33")]
    [InlineData("(050) 111-22-33")]
    [InlineData("00380501112233")]
    [InlineData("мій номер: 050 111 22 33")]
    [InlineData("Номер 050-111-22-33")]
    [InlineData("тел. +380 50 111 22 33")]
    [InlineData("ось 0501112233")]
    public void ukrainian_formats_are_normalized_to_e164(string text)
    {
        var r = ChatPhoneParser.Parse(text);
        Assert.Equal(PhoneParseKind.Phone, r.Kind);
        Assert.Equal("+380501112233", r.E164);
    }

    [Theory]
    [InlineData("+1 202 555 0143", "+12025550143")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public void foreign_e164_numbers_are_accepted(string text, string expected) =>
        Assert.Equal(expected, ChatPhoneParser.Parse(text).E164);

    [Theory]
    [InlineData("+38050111223")]
    [InlineData("+3805011122334")]
    [InlineData("+0501112233")]
    [InlineData("050111")]
    [InlineData("0501112233, 0671112233")]
    [InlineData("0501112233 або 0671112233")]
    [InlineData("０５０１１１２２３３")] // повноширинні цифри
    [InlineData("٠٥٠١١١٢٢٣٣")] // арабсько-індійські цифри
    public void number_like_garbage_is_invalid(string text) => Assert.Equal(PhoneParseKind.Invalid, ChatPhoneParser.Parse(text).Kind);

    [Theory]
    [InlineData("а скільки коштує?")]
    [InlineData("2 3")]
    [InlineData("1 год")]
    [InlineData("привіт")]
    [InlineData("")]
    [InlineData("номер")]
    [InlineData("хочу на стрижку о 10")]
    public void other_text_is_not_a_phone(string text) => Assert.Equal(PhoneParseKind.NotAPhone, ChatPhoneParser.Parse(text).Kind);

    [Theory]
    [InlineData("Не треба", PhoneParseKind.Decline)]
    [InlineData("ні", PhoneParseKind.Decline)]
    [InlineData("не хочу", PhoneParseKind.Decline)]
    [InlineData("не потрібно!", PhoneParseKind.Decline)]
    [InlineData("відміна", PhoneParseKind.Decline)]
    [InlineData("змінити номер", PhoneParseKind.ChangeNumber)]
    [InlineData("Змінити спосіб", PhoneParseKind.ChangeNumber)]
    [InlineData("мій номер", PhoneParseKind.ChangeNumber)]
    public void decline_and_change_words_are_exact(string text, PhoneParseKind expected) => Assert.Equal(expected, ChatPhoneParser.Parse(text).Kind);

    [Fact]
    public void parse_result_does_not_print_the_number() =>
        Assert.DoesNotContain("1112233", ChatPhoneParser.Parse("0501112233").ToString());

    [Fact]
    public void phone_templates_are_plain_text_and_match_the_contract()
    {
        var withButtons = ChatTemplates.PhoneRequest(true, true);
        Assert.Contains("Натисніть «📱 Поділитися контактом» нижче або напишіть номер у форматі +380XXXXXXXXX.", withButtons);
        Assert.Contains("щодо цього запису", withButtons);
        Assert.Contains("щодо ваших записів", ChatTemplates.PhoneRequest(false, true));
        var text = ChatTemplates.PhoneRequest(true, false);
        Assert.DoesNotContain("Поділитися", text);
        Assert.Contains("напишіть «не треба»", text);
        foreach (var t in new[] { withButtons, text, ChatTemplates.PhoneSaved, ChatTemplates.PhonePending, ChatTemplates.PhoneForeign, ChatTemplates.PhoneInvalid,
                     ChatTemplates.PhoneDeclined, ChatTemplates.ReminderCallUnavailablePhone, ChatTemplates.ContactUnexpected, ChatTemplates.CallDisabled,
                     ChatTemplates.PhoneNoClient })
            Assert.DoesNotMatch("[*_`#~<>]", t);
        Assert.Equal("Дякуємо, номер збережено. Адміністратор зателефонує вам напередодні візиту сб, 31.10 о 13:30. Змінити — напишіть «нагадування».",
            ChatTemplates.PhoneSavedCall(new DateTimeOffset(2026, 10, 31, 11, 30, 0, TimeSpan.Zero), "Europe/Kyiv"));
    }

    [Fact]
    public void menu_offers_the_call_without_a_phone_outside_the_cooldown_only()
    {
        Assert.Contains(ReminderChoiceMenu.Options(CallOffer.NeedsPhone), o => o.Choice.Method == "call");
        Assert.Contains(ReminderChoiceMenu.Options(CallOffer.Available), o => o.Choice.Method == "call");
        Assert.DoesNotContain(ReminderChoiceMenu.Options(CallOffer.Cooldown), o => o.Choice.Method == "call");
        Assert.DoesNotContain(ReminderChoiceMenu.Options(CallOffer.Disabled), o => o.Choice.Method == "call");
        var cooldown = ChatTemplates.ReminderOffer(ReminderChoiceMenu.Options(CallOffer.Cooldown), false, CallOffer.Cooldown);
        Assert.Contains("напишіть «змінити номер»", cooldown);
        Assert.DoesNotContain("«змінити номер»", ChatTemplates.ReminderOffer(ReminderChoiceMenu.Options(CallOffer.NeedsPhone), false, CallOffer.NeedsPhone));
    }
}

/// <summary>
/// TASK-770 (§25.5-§25.6, §25.10 сценарії 3-8, 11-17, 19, 22): розмовна логіка збору телефону на реальному PostgreSQL (RLS), mock-ШІ і fake Telegram.
/// Реальних Telegram/Anthropic викликів немає. Номер не повинен потрапляти в тіла, actions, журнал, логи, історію й запит LLM.
/// </summary>
public sealed class ChatPhoneTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatPhoneTests(PostgresRlsFixture db) => _db = db;

    private static readonly IDictionary<string, string?> PhoneOn = new Dictionary<string, string?> { ["ChatPhone:Enabled"] = "true" };
    private static readonly IDictionary<string, string?> PhoneAndButtons = new Dictionary<string, string?>
        { ["ChatPhone:Enabled"] = "true", ["ChatButtons:Enabled"] = "true" };

    private const string Own = "+380931234567"; // у шаблонах такого номера немає (приклад шаблону - +380501112233)
    private static readonly string[] Needles = ["1234567", "9312345", "0931234"];
    private static long _mid = 9_000_000;

    private ChatHarness H(IDictionary<string, string?>? cfg = null, ChatAssistantOptions? o = null) => ChatHarness.Create(_db, o, extraConfig: cfg ?? PhoneOn);

    // ---------------------------------------------------------------- допоміжне

    private static async Task<Guid> BookAsync(ChatHarness h, ChatSeed s, DateTimeOffset? start = null)
    {
        var conv = await h.ShowSummaryAsync(s, start: start);
        await h.SayAsync(s, "Так");
        return conv;
    }

    private async Task<Client> ClientAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.Clients.AsNoTracking().SingleAsync();
    }

    private async Task<ClientContactPreference> PrefAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.ClientContactPreferences.AsNoTracking().SingleAsync();
    }

    private async Task<List<CallTask>> TasksAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.CallTasks.AsNoTracking().ToListAsync();
    }

    private async Task<string> LastBodyAsync(ChatHarness h, ChatSeed s) => (await h.OutboundAsync(s)).Last().Body;

    /// <summary>Контакт Telegram тим самим шляхом, що polling/webhook (kind=contact, verdict, номер лише для own).</summary>
    private static async Task<(Guid Conv, int Fresh)> IngestContactAsync(ChatHarness h, ChatSeed s, string verdict, string? phone, long? messageId = null, string chat = "100")
    {
        var id = messageId ?? Interlocked.Increment(ref _mid);
        using var scope = h.Sp.CreateScope();
        var p = scope.ServiceProvider;
        p.GetRequiredService<TenantContext>().SetTenant(s.TenantId);
        using var _ = p.GetRequiredService<ChannelRequestContext>().Begin(
            new ResolvedChannel(s.TenantId, s.ChannelId, "telegram", new ChannelCredentials(s.Token, "whsecret")));
        var fresh = await p.GetRequiredService<InboundIngestCore>().IngestAsync(
            [new InboundMessage("telegram", $"{chat}:{id}", chat, chat, "Anna", InboundKinds.ContactBody, DateTimeOffset.UtcNow, InboundKinds.Contact)
                { ContactVerdict = verdict, ContactPhone = phone }], default);
        return (await h.ConversationIdAsync(s, chat), fresh.Count);
    }

    private static async Task<Guid> ContactAsync(ChatHarness h, ChatSeed s, string verdict = ContactVerdicts.Own, string? phone = Own, long? messageId = null)
    {
        var (conv, _) = await IngestContactAsync(h, s, verdict, phone, messageId);
        await h.TurnAsync(s.TenantId, conv);
        return conv;
    }

    private static JsonElement Markup(FakeBotApi.Sent m) => JsonDocument.Parse(m.Markup!).RootElement;

    /// <summary>Номер не лишився ніде, крім картки клієнта: тіла, actions, contact_phone, журнал, логи, відправлене в Telegram, запити LLM.</summary>
    private async Task AssertNoPhoneAsync(ChatHarness h, ChatSeed s)
    {
        void No(string? where, string? text)
        {
            foreach (var n in Needles) Assert.DoesNotContain(n, text ?? "");
            _ = where;
        }
        foreach (var m in await h.MessagesAsync(s))
        {
            No("body", m.Body);
            No("actions", m.Actions);
            Assert.Null(m.ContactPhone);
        }
        foreach (var a in await h.ActionsAsync(s))
        {
            No("payload", a.Payload);
            No("result", a.Result);
        }
        No("logs", h.Logs.All);
        foreach (var b in h.Bot.Messages)
        {
            No("tg text", b.Text);
            No("tg markup", b.Markup);
        }
        foreach (var c in h.Ai.Calls)
        {
            No("llm system", c.System);
            foreach (var t in c.Messages.SelectMany(m => m.Blocks).OfType<AiTextBlock>()) No("llm", t.Text);
        }
    }

    // ---------------------------------------------------------------- сценарій 3: потік (текст) і 2: відправка

    [SkippableFact]
    public async Task call_without_a_phone_asks_for_the_number_and_an_own_contact_saves_it_with_a_call_task()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        var menu = await LastBodyAsync(h, s);
        Assert.Contains("«зателефонувати»", menu); // NeedsPhone: пункт є і без телефону
        var calls = h.Ai.Calls.Count;
        var appt = Assert.Single(await h.AppointmentsAsync(s));

        await h.SayAsync(s, "зателефонувати");

        Assert.Equal(calls, h.Ai.Calls.Count); // без LLM
        Assert.Equal(ChatTemplates.PhoneRequest(true, true), await LastBodyAsync(h, s));
        var request = h.Bot.Messages.Last();
        var kb = Markup(request);
        var rows = kb.GetProperty("keyboard").EnumerateArray().Select(r => r.EnumerateArray().ToList()).ToList();
        Assert.Equal("📱 Поділитися контактом", rows[0][0].GetProperty("text").GetString());
        Assert.True(rows[0][0].GetProperty("request_contact").GetBoolean());
        Assert.Equal("Не треба", rows[1][0].GetProperty("text").GetString());
        Assert.True(kb.GetProperty("one_time_keyboard").GetBoolean());
        Assert.True(kb.GetProperty("resize_keyboard").GetBoolean());
        Assert.Equal("+380XXXXXXXXX", kb.GetProperty("input_field_placeholder").GetString());
        var conv = await h.ConversationAsync(s);
        Assert.Equal(("phone_request", appt.Id), (conv.PendingPrompt, conv.PendingPromptAppointmentId));
        Assert.NotNull(conv.ReplyKeyboardOpenAt);
        Assert.InRange(conv.PendingPromptExpiresAt!.Value, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
        Assert.NotNull((await ClientAsync(s)).PhoneRequestedAt);
        await using (var read = _db.CreateContext(s.TenantId)) Assert.Empty(await read.ChatKeyboards.AsNoTracking().ToListAsync()); // reply-keyboard: рядків немає

        await ContactAsync(h, s);

        Assert.Equal(calls, h.Ai.Calls.Count);
        var client = await ClientAsync(s);
        Assert.Equal((Own, "telegram_contact"), (client.Phone, client.PhoneSource));
        Assert.NotNull(client.CallConsentAt);
        var pref = await PrefAsync(s);
        Assert.Equal("call", pref.Method);
        Assert.Equal(new short[] { 1440 }, pref.OffsetsMinutes);
        var task = Assert.Single(await TasksAsync(s), t => t.Status == "open");
        Assert.Equal((appt.Id, (short)1440), (task.AppointmentId, task.OffsetMinutes!.Value));
        var body = await LastBodyAsync(h, s);
        Assert.StartsWith("Дякуємо, номер збережено. Адміністратор зателефонує вам напередодні візиту ", body);
        Assert.Contains("remove_keyboard", h.Bot.Messages.Last().Markup);
        conv = await h.ConversationAsync(s);
        Assert.Null(conv.PendingPrompt);
        Assert.Null(conv.ReplyKeyboardOpenAt);
        var inbound = (await h.MessagesAsync(s)).Last(m => m.Direction == MessageDirection.Inbound);
        Assert.Equal(("contact", InboundKinds.ContactBody, "processed"), (inbound.Kind, inbound.Body, inbound.Status));
        var tools = (await h.ActionsAsync(s)).Select(a => a.ToolName).ToList();
        Assert.Contains("phone_prompt", tools);
        Assert.Contains("phone_saved", tools);
        await AssertNoPhoneAsync(h, s);
        Assert.Contains("chat_phone conversation=", h.Logs.All);
    }

    [SkippableFact]
    public async Task without_buttons_support_the_question_is_text_only()
    {
        var h = H(mock: true); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");

        Assert.Equal(ChatTemplates.PhoneRequest(true, false), await LastBodyAsync(h, s));
        Assert.Null((await h.OutboundAsync(s)).Last().Actions);
        Assert.Equal("phone_request", (await h.ConversationAsync(s)).PendingPrompt);
    }

    private ChatHarness H(bool mock) => ChatHarness.Create(_db, extraConfig: PhoneOn, mockChannel: mock);

    // ---------------------------------------------------------------- сценарій 4: кнопки

    [SkippableFact]
    public async Task rc_call_button_without_a_phone_asks_for_the_number_and_revokes_the_menu()
    {
        var h = H(PhoneAndButtons); var s = await h.SeedAsync();
        var summary = await h.ShowSummaryAsync(s);
        var sm = h.Bot.Messages.Last(m => m.Markup is not null);
        await h.PressAsync(s, Kb(sm).First(b => b.Text.Contains("Так, записатися")).Data, sm.MessageId.ToString());
        var confirmed = h.Bot.Messages.Last();
        var call = Kb(confirmed).First(b => b.Text.Contains("Зателефонувати"));

        await h.PressAsync(s, call.Data, confirmed.MessageId.ToString());

        Assert.Equal(ChatTemplates.PhoneRequest(true, true), await LastBodyAsync(h, s));
        Assert.Equal("phone_request", (await h.ConversationAsync(s)).PendingPrompt);
        await using var read = _db.CreateContext(s.TenantId);
        Assert.All(await read.ChatKeyboards.AsNoTracking().Where(k => k.Kind == "reminder_choice").ToListAsync(), k => Assert.NotEqual("active", k.Status));
        Assert.Contains("keyboard", h.Bot.Messages.Last().Markup);
        Assert.NotEqual(Guid.Empty, summary);
    }

    private static List<(string Text, string Data)> Kb(FakeBotApi.Sent m)
    {
        if (m.Markup is null) return [];
        using var doc = JsonDocument.Parse(m.Markup);
        if (!doc.RootElement.TryGetProperty("inline_keyboard", out var rows)) return [];
        return rows.EnumerateArray().SelectMany(r => r.EnumerateArray()).Select(b => (b.GetProperty("text").GetString()!, b.GetProperty("callback_data").GetString()!)).ToList();
    }

    // ---------------------------------------------------------------- сценарій 6: чужий / невалідний контакт

    [SkippableTheory]
    [InlineData(ContactVerdicts.Foreign)]
    [InlineData(ContactVerdicts.Invalid)]
    public async Task foreign_or_invalid_contact_is_refused_and_the_question_stays(string verdict)
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");

        await ContactAsync(h, s, verdict, phone: null);

        Assert.Equal(verdict == ContactVerdicts.Foreign ? ChatTemplates.PhoneForeign : ChatTemplates.PhoneInvalid, await LastBodyAsync(h, s));
        Assert.Null((await ClientAsync(s)).Phone);
        var conv = await h.ConversationAsync(s);
        Assert.Equal("phone_request", conv.PendingPrompt);
        Assert.NotNull(conv.ReplyKeyboardOpenAt);
        Assert.Empty(await TasksAsync(s));

        await ContactAsync(h, s); // потім власний
        Assert.Equal(Own, (await ClientAsync(s)).Phone);
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
        await AssertNoPhoneAsync(h, s);
    }

    // ---------------------------------------------------------------- сценарій 5: ручний номер

    [SkippableTheory]
    [InlineData("+380931234567")]
    [InlineData("380931234567")]
    [InlineData("0931234567")]
    [InlineData("093 123 45 67")]
    [InlineData("(093) 123-45-67")]
    [InlineData("00380931234567")]
    [InlineData("мій номер: 093 123 45 67")]
    public async Task a_typed_number_is_saved_as_manual_chat_and_the_body_is_redacted(string typed)
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, typed);

        Assert.Equal(calls, h.Ai.Calls.Count);
        var client = await ClientAsync(s);
        Assert.Equal((Own, "manual_chat"), (client.Phone, client.PhoneSource));
        Assert.NotNull(client.CallConsentAt);
        Assert.Equal("call", (await PrefAsync(s)).Method);
        Assert.Single(await TasksAsync(s), t => t.Status == "open");
        var inbound = (await h.MessagesAsync(s)).Last(m => m.Direction == MessageDirection.Inbound);
        Assert.Equal((ChatTemplates.PhoneBodyRedacted, "processed"), (inbound.Body, inbound.Status));
        Assert.StartsWith("Дякуємо, номер збережено.", await LastBodyAsync(h, s));

        // номер не потрапляє в запит LLM далі: у історії лише «[номер телефону]»
        h.Ai.Enqueue(Text("Стрижка коштує 500 грн."));
        await h.SayAsync(s, "а скільки коштує?");
        var llm = h.Ai.Calls.Last();
        Assert.Contains(ChatTemplates.PhoneBodyRedacted, string.Join("\n", llm.Messages.SelectMany(m => m.Blocks).OfType<AiTextBlock>().Select(b => b.Text)));
        await AssertNoPhoneAsync(h, s);
    }

    [SkippableTheory]
    [InlineData("+1 202 555 0143", "+12025550143")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public async Task a_foreign_e164_number_is_accepted(string typed, string expected)
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        await h.SayAsync(s, typed);
        Assert.Equal(expected, (await ClientAsync(s)).Phone);
    }

    [SkippableTheory]
    [InlineData("+38093123456")]
    [InlineData("+3809312345678")]
    [InlineData("+0931234567")]
    [InlineData("093123")]
    [InlineData("0931234567, 0671234567")]
    [InlineData("０９３１２３４５６７")]
    public async Task number_like_garbage_gets_t_phone_invalid_and_the_question_stays(string typed)
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, typed);

        Assert.Equal(ChatTemplates.PhoneInvalid, await LastBodyAsync(h, s));
        Assert.Equal(calls, h.Ai.Calls.Count);
        Assert.Null((await ClientAsync(s)).Phone);
        Assert.Equal("phone_request", (await h.ConversationAsync(s)).PendingPrompt);
        var inbound = (await h.MessagesAsync(s)).Last(m => m.Direction == MessageDirection.Inbound);
        Assert.Equal(ChatTemplates.PhoneBodyRedacted, inbound.Body);
    }

    [SkippableFact]
    public async Task other_text_drops_the_question_goes_to_the_llm_and_removes_the_reply_keyboard()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        var calls = h.Ai.Calls.Count;
        h.Ai.Enqueue(Text("Стрижка коштує 500 грн."));

        await h.SayAsync(s, "а скільки коштує?");

        Assert.Equal(calls + 1, h.Ai.Calls.Count);
        Assert.Equal("Стрижка коштує 500 грн.", await LastBodyAsync(h, s));
        var conv = await h.ConversationAsync(s);
        Assert.Null(conv.PendingPrompt);
        Assert.Null(conv.ReplyKeyboardOpenAt); // §25.5.5: клавіатура прибрана першою відповіддю
        Assert.Contains("remove_keyboard", h.Bot.Messages.Last().Markup);
        Assert.Null((await ClientAsync(s)).Phone);
        // повідомлення з inline-кнопками клавіатуру не прибирає: окремо перевіряється нижче у тесті з кнопками
    }

    // ---------------------------------------------------------------- сценарій 7: відмова, cooldown

    [SkippableFact]
    public async Task decline_keeps_telegram_reminders_and_pauses_the_next_automatic_question()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");

        await h.SayAsync(s, "Не треба");

        var outbound = await h.OutboundAsync(s);
        Assert.Equal(ChatTemplates.PhoneDeclined, outbound[^2].Body);
        Assert.Contains("remove_keyboard", h.Bot.Messages[^2].Markup);
        Assert.Contains("Нагадати вам про візит?", outbound[^1].Body);
        Assert.DoesNotContain("зателефонувати", outbound[^1].Body);
        var conv = await h.ConversationAsync(s);
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(("reminder_choice", appt.Id), (conv.PendingPrompt, conv.PendingPromptAppointmentId));
        Assert.Null(conv.ReplyKeyboardOpenAt);
        Assert.NotNull((await ClientAsync(s)).PhoneRequestedAt);
        Assert.Null((await ClientAsync(s)).Phone);

        await h.SayAsync(s, "2");

        var pref = await PrefAsync(s);
        Assert.Equal("telegram", pref.Method);
        Assert.Equal(new short[] { 120 }, pref.OffsetsMinutes);
        Assert.Empty(await TasksAsync(s));
    }

    [SkippableFact]
    public async Task cooldown_hides_the_call_item_blocks_the_automatic_question_and_explicit_change_bypasses_it()
    {
        var cfg = new Dictionary<string, string?>(PhoneOn) { ["ChatPhone:CooldownDays"] = "1" };
        // вхідні не старіють за добу симульованого часу
        var h = H(cfg, new ChatAssistantOptions { Enabled = true, SweepMinAge = TimeSpan.Zero, MaxInboundAge = TimeSpan.FromDays(10) }); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        await h.SayAsync(s, "не треба");
        await h.SayAsync(s, "4"); // питання про нагадування закрито
        h.Clock.Advance(TimeSpan.FromHours(2));

        await h.SayAsync(s, "нагадування");
        var menu = await LastBodyAsync(h, s);
        Assert.DoesNotContain("«зателефонувати»", menu);
        Assert.Contains("Щоб адміністратор телефонував, напишіть «змінити номер».", menu);

        await h.SayAsync(s, "зателефонувати");
        Assert.Equal(ChatTemplates.ReminderCallUnavailablePhone, await LastBodyAsync(h, s));
        Assert.Equal("reminder_choice", (await h.ConversationAsync(s)).PendingPrompt);

        var requestedAt = (await ClientAsync(s)).PhoneRequestedAt;
        await h.SayAsync(s, "змінити номер"); // явний обхід паузи
        Assert.Equal(ChatTemplates.PhoneRequest(true, true), await LastBodyAsync(h, s));
        var conv = await h.ConversationAsync(s);
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(("phone_request", appt.Id), (conv.PendingPrompt, conv.PendingPromptAppointmentId));
        Assert.NotEqual(requestedAt, (await ClientAsync(s)).PhoneRequestedAt);

        // повтор тригера при активному питанні: питання й phone_requested_at не зсуваються
        var stamp = (await ClientAsync(s)).PhoneRequestedAt;
        var expires = conv.PendingPromptExpiresAt;
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.SayAsync(s, "змінити номер");
        Assert.Equal(stamp, (await ClientAsync(s)).PhoneRequestedAt);
        Assert.Equal(expires, (await h.ConversationAsync(s)).PendingPromptExpiresAt);

        // відмова, 25 год потому пауза минула: меню знову з пунктом, «зателефонувати» веде до питання
        await h.SayAsync(s, "ні");
        h.Clock.Advance(TimeSpan.FromHours(25));
        await h.SayAsync(s, "4");
        await h.SayAsync(s, "нагадування");
        Assert.Contains("«зателефонувати»", await LastBodyAsync(h, s));
        await h.SayAsync(s, "зателефонувати");
        Assert.Equal(ChatTemplates.PhoneRequest(true, true), await LastBodyAsync(h, s));
    }

    // ---------------------------------------------------------------- сценарій 8: TTL

    [SkippableFact]
    public async Task an_unanswered_question_expires_after_24_hours_and_a_late_contact_is_not_saved()
    {
        var h = H(new Dictionary<string, string?>(PhoneOn) { ["ChatPhone:PromptTtlHours"] = "1" }); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        h.Clock.Advance(TimeSpan.FromMinutes(90));
        var calls = h.Ai.Calls.Count;

        await ContactAsync(h, s);

        Assert.Null((await ClientAsync(s)).Phone);
        Assert.Null((await h.ConversationAsync(s)).PendingPrompt);
        Assert.Equal(ChatTemplates.ContactUnexpected, await LastBodyAsync(h, s));
        Assert.Equal(calls, h.Ai.Calls.Count);
        Assert.Empty(await TasksAsync(s));
        await AssertNoPhoneAsync(h, s);
    }

    // ---------------------------------------------------------------- сценарій 9: конфлікт

    [SkippableFact]
    public async Task a_different_number_goes_to_pending_and_never_overwrites_the_existing_one()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Clients.ExecuteUpdateAsync(x => x.SetProperty(c => c.Phone, "+380671112233"));
        await h.SayAsync(s, "змінити номер"); // телефон є: питання лише про номер
        var conv = await h.ConversationAsync(s);
        Assert.Equal(("phone_request", null), (conv.PendingPrompt, conv.PendingPromptAppointmentId));
        Assert.Null(conv.AwaitingStaffSince);

        await ContactAsync(h, s);

        var client = await ClientAsync(s);
        Assert.Equal("+380671112233", client.Phone);
        Assert.Equal((Own, "telegram_contact"), (client.PhonePending, client.PhonePendingSource));
        Assert.NotNull(client.PhonePendingAt);
        Assert.Equal(ChatTemplates.PhonePending, await LastBodyAsync(h, s));
        conv = await h.ConversationAsync(s);
        Assert.NotNull(conv.AwaitingStaffSince);
        Assert.Null(conv.PendingPrompt);
        Assert.Contains("phone_conflict", (await h.ActionsAsync(s)).Select(a => a.ToolName));
        await AssertNoPhoneAsync(h, s);
    }

    [SkippableFact]
    public async Task the_same_number_in_another_format_is_unchanged()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Clients.ExecuteUpdateAsync(x => x.SetProperty(c => c.Phone, "093 123 45 67"));
        await h.SayAsync(s, "змінити номер");

        await h.SayAsync(s, Own);

        var client = await ClientAsync(s);
        Assert.Equal(("093 123 45 67", null), (client.Phone, client.PhonePending));
        Assert.NotNull(client.CallConsentAt);
        Assert.Equal(ChatTemplates.PhoneSaved, await LastBodyAsync(h, s));
        Assert.Contains("unchanged", (await h.ActionsAsync(s)).Single(a => a.ToolName == "phone_saved").Payload);
    }

    // ---------------------------------------------------------------- сценарії 11-12: контакт без питання, handoff, suggest

    [SkippableFact]
    public async Task a_contact_without_a_question_is_not_saved_and_the_notice_is_throttled()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "4");

        await ContactAsync(h, s);
        Assert.Equal(ChatTemplates.ContactUnexpected, await LastBodyAsync(h, s));
        var count = (await h.OutboundAsync(s)).Count;
        await ContactAsync(h, s);

        Assert.Equal(count, (await h.OutboundAsync(s)).Count); // раз на 10 хв
        Assert.Null((await ClientAsync(s)).Phone);
        await AssertNoPhoneAsync(h, s);
    }

    [SkippableFact]
    public async Task during_handoff_a_contact_answers_the_question_and_other_text_is_held()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        var staffSince = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Conversations.ExecuteUpdateAsync(x => x.SetProperty(c => c.AiState, "handoff").SetProperty(c => c.HandoffReason, "complaint")
                .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(12)).SetProperty(c => c.HandoffAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                .SetProperty(c => c.AwaitingStaffSince, (DateTimeOffset?)staffSince));
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, "а коли?");
        var conv = await h.ConversationAsync(s);
        Assert.Equal("phone_request", conv.PendingPrompt); // питання не знімається
        Assert.Equal("held", (await h.MessagesAsync(s)).Last(m => m.Direction == MessageDirection.Inbound).Status);

        await ContactAsync(h, s);

        Assert.Equal(calls, h.Ai.Calls.Count);
        Assert.Equal(Own, (await ClientAsync(s)).Phone);
        Assert.StartsWith("Дякуємо, номер збережено.", await LastBodyAsync(h, s));
        conv = await h.ConversationAsync(s);
        Assert.Equal(("handoff", null), (conv.AiState, conv.PendingPrompt));
        Assert.Equal(staffSince.ToUnixTimeSeconds(), conv.AwaitingStaffSince!.Value.ToUnixTimeSeconds()); // не змінюється
        Assert.Equal("processed", (await h.MessagesAsync(s)).Last(m => m.Direction == MessageDirection.Inbound).Status);

        // контакт без питання під паузою: held, без відповіді, номера немає
        var sent = (await h.OutboundAsync(s)).Count;
        await ContactAsync(h, s);
        Assert.Equal(sent, (await h.OutboundAsync(s)).Count);
        Assert.Equal("held", (await h.MessagesAsync(s)).Last(m => m.Direction == MessageDirection.Inbound).Status);
        await AssertNoPhoneAsync(h, s);
    }

    [SkippableFact]
    public async Task in_suggest_mode_the_answer_is_saved_with_a_template_reply_and_without_the_llm()
    {
        var h = H(); var s = await h.SeedAsync(aiMode: "confirm");
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.AiSettings.ExecuteUpdateAsync(x => x.SetProperty(a => a.Mode, "suggest"));
        var calls = h.Ai.Calls.Count;

        await h.SayAsync(s, "0931234567");

        Assert.Equal(calls, h.Ai.Calls.Count);
        Assert.Equal(Own, (await ClientAsync(s)).Phone);
        Assert.StartsWith("Дякуємо, номер збережено.", await LastBodyAsync(h, s));
        Assert.Equal("call", (await PrefAsync(s)).Method);
    }

    // ---------------------------------------------------------------- сценарій 13: повтори

    [SkippableFact]
    public async Task two_contacts_in_a_row_save_once_and_a_repeated_message_id_is_dropped()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");

        var (conv, fresh1) = await IngestContactAsync(h, s, ContactVerdicts.Own, Own, messageId: 777);
        var (_, fresh2) = await IngestContactAsync(h, s, ContactVerdicts.Own, Own, messageId: 778);
        var (_, dup) = await IngestContactAsync(h, s, ContactVerdicts.Own, Own, messageId: 777);
        Assert.Equal((1, 1, 0), (fresh1, fresh2, dup));
        await h.TurnAsync(s.TenantId, conv);

        var client = await ClientAsync(s);
        Assert.Equal(Own, client.Phone);
        Assert.Single(await TasksAsync(s), t => t.Status == "open");
        Assert.Equal(1, (await h.ActionsAsync(s)).Count(a => a.ToolName == "phone_saved"));
        var inbound = (await h.MessagesAsync(s)).Where(m => m.Kind == "contact").ToList();
        Assert.All(inbound, m => Assert.Equal("processed", m.Status));
        Assert.Equal(1, (await h.OutboundAsync(s)).Count(m => m.Body.StartsWith("Дякуємо, номер збережено.")));
        await AssertNoPhoneAsync(h, s);
    }

    // ---------------------------------------------------------------- сценарій 14: запис скасовано

    [SkippableFact]
    public async Task a_booking_cancelled_between_the_question_and_the_answer_saves_the_number_without_a_call_task()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Appointments.ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, AppointmentStatus.Cancelled));

        await ContactAsync(h, s);

        Assert.Equal(Own, (await ClientAsync(s)).Phone);
        Assert.Equal(ChatTemplates.PhoneSaved, await LastBodyAsync(h, s));
        Assert.Empty(await TasksAsync(s));
    }

    // ---------------------------------------------------------------- сценарій 15: вимкнено

    [SkippableFact]
    public async Task with_the_flag_off_the_old_behaviour_stays()
    {
        var h = ChatHarness.Create(_db); var s = await h.SeedAsync();
        await BookAsync(h, s);
        var menu = await LastBodyAsync(h, s);
        Assert.DoesNotContain("зателефонувати", menu);

        await h.SayAsync(s, "зателефонувати"); // питання є, але дзвінка немає: старий текст
        Assert.DoesNotContain("Поділитися", await LastBodyAsync(h, s));
        Assert.Equal("reminder_choice", (await h.ConversationAsync(s)).PendingPrompt);

        await ContactAsync(h, s);

        Assert.Null((await ClientAsync(s)).Phone);
        Assert.Equal(ChatTemplates.TextOnly, await LastBodyAsync(h, s));
        await using var read = _db.CreateContext(s.TenantId);
        Assert.All(await read.Messages.AsNoTracking().ToListAsync(), m => Assert.Null(m.ContactPhone));
    }

    [SkippableFact]
    public async Task with_the_flag_off_call_without_a_phone_keeps_the_old_unavailable_text()
    {
        var h = ChatHarness.Create(_db); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await h.SayAsync(s, "зателефонувати");
        Assert.Equal(ChatTemplates.ReminderCallUnavailable, await LastBodyAsync(h, s));
    }

    [SkippableFact]
    public async Task disabled_calls_in_the_tenant_settings_give_t_call_disabled()
    {
        var h = H(); var s = await h.SeedAsync();
        await BookAsync(h, s);
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            ctx.ReminderSettings.Add(new ReminderSettingsRow { CallEnabled = false, UpdatedAt = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await h.SayAsync(s, "зателефонувати");

        Assert.Equal(ChatTemplates.CallDisabled, await LastBodyAsync(h, s));
        Assert.Equal("reminder_choice", (await h.ConversationAsync(s)).PendingPrompt);
        Assert.Null((await ClientAsync(s)).PhoneRequestedAt);
    }

    [SkippableFact]
    public async Task change_number_without_a_client_explains_that_the_number_is_needed_after_a_booking()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "змінити номер");
        Assert.Equal(ChatTemplates.PhoneNoClient, await LastBodyAsync(h, s));
        Assert.Empty(h.Ai.Calls);
    }

    // ---------------------------------------------------------------- кнопка «Змінити спосіб» (i:phone)

    [SkippableFact]
    public async Task the_change_method_button_in_the_cooldown_menu_asks_for_the_number()
    {
        var cfg = new Dictionary<string, string?>(PhoneAndButtons) { ["ChatPhone:CooldownDays"] = "1" };
        var h = H(cfg); var s = await h.SeedAsync();
        await h.ShowSummaryAsync(s);
        var sm = h.Bot.Messages.Last(m => m.Markup is not null);
        await h.PressAsync(s, Kb(sm).First(b => b.Text.Contains("Так, записатися")).Data, sm.MessageId.ToString());
        var confirmed = h.Bot.Messages.Last();
        await h.PressAsync(s, Kb(confirmed).First(b => b.Text.Contains("Зателефонувати")).Data, confirmed.MessageId.ToString());
        await h.SayAsync(s, "Не треба");
        await h.SayAsync(s, "4");

        await h.SayAsync(s, "нагадування");
        var menu = h.Bot.Messages.Last(m => m.Markup is not null);
        var buttons = Kb(menu);
        Assert.DoesNotContain(buttons, b => b.Text.Contains("Зателефонувати"));
        var change = Assert.Single(buttons, b => b.Text.Contains("Змінити спосіб"));
        Assert.Equal("i:phone", change.Data);

        await h.PressAsync(s, change.Data, menu.MessageId.ToString());

        Assert.Equal(ChatTemplates.PhoneRequest(true, true), await LastBodyAsync(h, s));
        Assert.Equal("phone_request", (await h.ConversationAsync(s)).PendingPrompt);
        // inline-відповідь без reply-клавіатури: removeReply не потрібен, питання активне
        Assert.NotNull((await h.ConversationAsync(s)).ReplyKeyboardOpenAt);
    }

    [SkippableFact]
    public async Task a_reply_with_inline_buttons_does_not_remove_the_reply_keyboard_but_a_plain_one_does()
    {
        var h = H(PhoneAndButtons); var s = await h.SeedAsync();
        await h.ShowSummaryAsync(s);
        var sm = h.Bot.Messages.Last(m => m.Markup is not null);
        await h.PressAsync(s, Kb(sm).First(b => b.Text.Contains("Так, записатися")).Data, sm.MessageId.ToString());
        var confirmed = h.Bot.Messages.Last();
        await h.PressAsync(s, Kb(confirmed).First(b => b.Text.Contains("Зателефонувати")).Data, confirmed.MessageId.ToString());
        // клавіатура відкрита; питання знімаємо «зовні» (наприклад, відповідь менеджера), клавіатура лишається відкритою
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Conversations.ExecuteUpdateAsync(x => x.SetProperty(c => c.PendingPrompt, (string?)null)
                .SetProperty(c => c.PendingPromptAppointmentId, (Guid?)null).SetProperty(c => c.PendingPromptExpiresAt, (DateTimeOffset?)null));
        Assert.NotNull((await h.ConversationAsync(s)).ReplyKeyboardOpenAt);

        await h.SayAsync(s, "/help", kind: InboundKinds.Command); // T-help з inline «Передати адміністратору»
        Assert.NotNull((await h.ConversationAsync(s)).ReplyKeyboardOpenAt);
        Assert.Contains("inline_keyboard", h.Bot.Messages.Last().Markup);

        await h.SayAsync(s, "/book", kind: InboundKinds.Command); // T-book-start без кнопок
        Assert.Null((await h.ConversationAsync(s)).ReplyKeyboardOpenAt);
        Assert.Contains("remove_keyboard", h.Bot.Messages.Last().Markup);
    }

    // ---------------------------------------------------------------- сценарій 17: sweeper

    [SkippableFact]
    public async Task the_sweeper_clears_old_unprocessed_contact_numbers()
    {
        var h = H(); var s = await h.SeedAsync();
        var (conv, _) = await IngestContactAsync(h, s, ContactVerdicts.Own, Own);
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            Assert.NotNull((await ctx.Messages.AsNoTracking().SingleAsync()).ContactPhone);
            await ctx.Messages.ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, "processed"));
        }
        h.Clock.Advance(TimeSpan.FromHours(25));

        await h.Worker.SweepOnceAsync(default);

        await using var read = _db.CreateContext(s.TenantId);
        Assert.Null((await read.Messages.AsNoTracking().SingleAsync()).ContactPhone);
        Assert.NotEqual(Guid.Empty, conv);
    }

    // ---------------------------------------------------------------- ChatRules, факти

    [Fact]
    public void chat_rules_forbid_the_model_to_ask_for_the_number()
    {
        Assert.Contains("Не проси номер телефону й не обіцяй дзвінка без нього", PromptComposer.ChatRules);
        var facts = ChatPolicyFacts.Default;
        var off = ChatContextBuilder.FactsText(facts, null);
        Assert.Contains("телефон у чаті не запитуємо", off);
        var allowed = ChatContextBuilder.FactsText(facts with { PhoneCapture = PhoneCaptureFact.Allowed }, null);
        Assert.Contains("обрати «зателефонувати», тоді система попросить номер", allowed);
        var cooldown = ChatContextBuilder.FactsText(facts with { PhoneCapture = PhoneCaptureFact.Cooldown }, null);
        Assert.Contains("«змінити номер»", cooldown);
    }
}
