using System.Text.Json;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Tests.Conversations;

/// <summary>Чисті перевірки TASK-840: префіл start_booking_flow, нормалізація текстового вибору, набір tools і правила промпту. Без БД.</summary>
public sealed class ChatFlowLlmUnitTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void prefill_drops_invalid_values_silently_and_ignores_unknown_fields()
    {
        var id = Guid.NewGuid();
        var p = ClientChatToolbox.ParsePrefill(J($$"""{"serviceId":"{{id}}","specialistId":"не-guid","locationId":42,"date":"31.12.2026","preferredTime":"15:07","tenantId":"{{Guid.NewGuid()}}"}"""));
        Assert.Equal(id, p.ServiceId);
        Assert.Null(p.SpecialistId);
        Assert.Null(p.LocationId);
        Assert.Null(p.Day);
        Assert.Null(p.PreferredTime);
    }

    [Fact]
    public void prefill_accepts_date_and_time_on_the_15_minute_grid()
    {
        var p = ClientChatToolbox.ParsePrefill(J("""{"date":"2026-11-08","preferredTime":"15:45"}"""));
        Assert.Equal(new DateOnly(2026, 11, 8), p.Day);
        Assert.Equal(new TimeOnly(15, 45), p.PreferredTime);
        Assert.Equal(new FlowPrefill(), ClientChatToolbox.ParsePrefill(J("[]")));
    }

    [Theory]
    [InlineData("  ✅ Так,  У «Салон»  ", "так, у «салон»")]
    [InlineData("➕ Ще", "ще")]
    [InlineData("⬅ Назад", "назад")]
    [InlineData("✖ Скасувати", "скасувати")]
    [InlineData("📆 Інший день", "інший день")]
    [InlineData("ІНШИЙ", "інший")]
    [InlineData("пт 14:30", "пт 14:30")]
    public void choice_normalization_strips_markers_case_and_spaces(string raw, string expected) =>
        Assert.Equal(expected, ConversationTurnProcessor.NormalizeChoice(raw));

    [Fact]
    public void tool_set_swaps_prepare_booking_for_start_booking_flow_only_when_flow_is_available()
    {
        var plain = AiToolCatalog.For(AiScope.ClientChat).Select(t => t.Name).ToList();
        Assert.Contains("prepare_booking", plain);
        Assert.DoesNotContain("start_booking_flow", plain);

        var flow = AiToolCatalog.For(AiScope.ClientChat, flowAvailable: true).Select(t => t.Name).ToList();
        Assert.DoesNotContain("prepare_booking", flow);
        Assert.Contains("start_booking_flow", flow);
        Assert.Contains("find_slots", flow);
        Assert.Contains("request_handoff", flow);

        Assert.Contains("prepare_booking", AiToolCatalog.For(AiScope.ClientChat, flowAvailable: true, llmPrepare: true).Select(t => t.Name));
        var schema = AiToolCatalog.StartBookingFlow.InputSchemaJson;
        Assert.DoesNotContain("tenantId", schema);
        Assert.DoesNotContain("conversationId", schema);
        Assert.DoesNotContain("clientId", schema);
    }

    [Fact]
    public void flow_rules_are_added_to_the_prompt_only_for_flow_chat()
    {
        var withFlow = PromptComposer.Compose(AiMode.Confirm, null, AiScope.ClientChat, null, flow: true);
        var without = PromptComposer.Compose(AiMode.Confirm, null, AiScope.ClientChat, null);
        Assert.Contains("start_booking_flow", withFlow);
        Assert.Contains("СТАН ЗАПИСУ", withFlow);
        Assert.DoesNotContain("start_booking_flow", without);
    }
}

/// <summary>Інтеграційні тести TASK-840 (§28.7, §28.15 п. 8, 11, 12, 17): ШІ лише стартує потік і відповідає на вільний текст; реальний PostgreSQL, fake Telegram, mock-ШІ.</summary>
public sealed class ChatFlowLlmTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatFlowLlmTests(PostgresRlsFixture db) => _db = db;

    private static readonly IDictionary<string, string?> On = new Dictionary<string, string?>
    {
        ["ChatButtons:Enabled"] = "true", ["ChatFlow:Enabled"] = "true",
    };

    private const string Hours =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    private ChatHarness H(IDictionary<string, string?>? cfg = null) => ChatHarness.Create(_db, extraConfig: cfg ?? On);

    private static List<(string Text, string Data)> Kb(FakeBotApi.Sent m)
    {
        if (m.Markup is null) return [];
        using var doc = JsonDocument.Parse(m.Markup);
        return doc.RootElement.GetProperty("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray())
            .Select(b => (b.GetProperty("text").GetString()!, b.GetProperty("callback_data").GetString()!)).ToList();
    }

    private static FakeBotApi.Sent Last(ChatHarness h) => h.Bot.Messages.Last(m => m.Markup is not null);
    private static string Ref(FakeBotApi.Sent m) => m.MessageId.ToString();
    private static string Data(FakeBotApi.Sent m, string part) => Kb(m).First(b => b.Text.Contains(part)).Data;
    private static Task<Guid> Press(ChatHarness h, ChatSeed s, FakeBotApi.Sent m, string part) => h.PressAsync(s, Data(m, part), Ref(m));
    private static string Tomorrow() => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

    private async Task<List<ChatBookingFlow>> FlowsAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.ChatBookingFlows.AsNoTracking().OrderBy(f => f.CreatedAt).ToListAsync();
    }

    private async Task<Guid> SecondSpecialistAsync(ChatSeed s, string specialist, string? service, Guid? sharedService = null)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var sp = new Specialist { FullName = specialist };
        ctx.Add(sp);
        Guid svcId;
        if (sharedService is { } shared) svcId = shared;
        else
        {
            var svc = new Service { Name = service!, DurationMinutes = 30 };
            ctx.Add(svc);
            await ctx.SaveChangesAsync();
            ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = 300m });
            svcId = svc.Id;
        }
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = s.LocationId, WorkingHours = Hours });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svcId });
        await ctx.SaveChangesAsync();
        return sp.Id;
    }

    /// <summary>Сума запитів LLM з журналу обліку chat_llm_usage (метрика §28.14).</summary>
    private static async Task<int> LlmCallsAsync(ChatHarness h, ChatSeed s) =>
        (await h.ActionsAsync(s)).Where(a => a.ToolName == ChatJournal.LlmUsage)
            .Sum(a => JsonDocument.Parse(a.Payload).RootElement.GetProperty("calls").GetInt32());

    private static IEnumerable<AiToolResultBlock> ToolResults(MockAiCall c) => c.Messages.SelectMany(m => m.Blocks).OfType<AiToolResultBlock>();

    // ---------------------------------------------------------------- start_booking_flow: префіл

    [SkippableFact]
    public async Task book_tomorrow_at_15_is_led_by_the_flow_and_never_shows_the_summary_at_once()
    {
        var h = H(); var s = await h.SeedAsync();
        h.Ai.Enqueue(ChatHarness.Tool("start_booking_flow", new { serviceId = s.ServiceId, date = Tomorrow(), preferredTime = "15:00" }));
        await h.SayAsync(s, "Хочу записатись на стрижку завтра о 15:00");

        var time = Last(h);
        Assert.Contains("Вільний час, Марина, Салон Центр:", time.Text);
        var slots = Kb(time).Where(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Select(b => b.Text).ToList();
        Assert.NotEmpty(slots);
        Assert.Matches(@" 14:\d\d$", slots[0]);             // сторінка від (бажаний час - 60 хв)
        Assert.DoesNotContain(h.Bot.Messages, m => m.Text.StartsWith("Підсумок запису"));
        Assert.Empty(await h.ProposalsAsync(s));
        Assert.Empty(await h.AppointmentsAsync(s));

        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("llm", flow.Origin);
        Assert.Equal("time", flow.Step);
        Assert.Equal(DateOnly.Parse(Tomorrow()), flow.Day);
        Assert.Equal(new TimeOnly(15, 0), flow.PreferredTime);

        // модель не бачить id у відповіді tool; хід завершується одразу після start_booking_flow (1 запит)
        var call = Assert.Single(h.Ai.Calls);
        Assert.Contains("start_booking_flow", call.ToolNames);
        Assert.DoesNotContain("prepare_booking", call.ToolNames);
        var action = Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "start_booking_flow"));
        using (var env = JsonDocument.Parse(action.Result!))
        {
            Assert.Equal("done", env.RootElement.GetProperty("Status").GetString());        // конверт журналу: статус AI-шару і target
            Assert.Equal($"flow:{flow.Id}", env.RootElement.GetProperty("Target").GetString());
        }
        using (var payload = JsonDocument.Parse(action.Payload))
        {
            Assert.Equal("time", payload.RootElement.GetProperty("step").GetString());
            var prefilled = payload.RootElement.GetProperty("prefilled").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("service", prefilled);
            Assert.Contains("date", prefilled);
            Assert.Contains("time", prefilled);
        }

        // далі лише кнопки: 0 нових викликів LLM, usage лише від першого ходу
        await h.PressAsync(s, Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Data, Ref(time));
        Assert.StartsWith("Підсумок запису:", Last(h).Text);
        await Press(h, s, Last(h), "Так, записатися");
        Assert.Single(await h.AppointmentsAsync(s));
        Assert.Single(h.Ai.Calls);
        Assert.Equal(1, await LlmCallsAsync(h, s));                 // chat_llm_usage: рівно один запит LLM на весь запис
    }

    [SkippableFact]
    public async Task prefill_that_does_not_fit_is_dropped_silently()
    {
        var h = H(); var s = await h.SeedAsync();
        var other = await SecondSpecialistAsync(s, "Олена", "Манікюр");
        // Олена не виконує «Стрижку», заклад вигаданий, дата в минулому: все відкидається, а потік іде звичайним шляхом
        h.Ai.Enqueue(ChatHarness.Tool("start_booking_flow", new
        {
            serviceId = s.ServiceId, specialistId = other, locationId = Guid.NewGuid(), date = "2020-01-01", preferredTime = "15:00",
        }));
        await h.SayAsync(s, "Запишіть мене на стрижку до Олени");

        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal(s.SpecialistId, flow.SpecialistId);      // Марина (єдина придатна), а не Олена
        Assert.Null(flow.Day);
        Assert.Null(flow.PreferredTime);
        Assert.Equal(s.LocationId, flow.LocationId);          // заклад каналу
        using var payload = JsonDocument.Parse((await h.ActionsAsync(s)).Single(a => a.ToolName == "start_booking_flow").Payload);
        var prefilled = payload.RootElement.GetProperty("prefilled").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["service"], prefilled);
        Assert.DoesNotContain(h.Bot.Messages, m => m.Text.Contains("Олена"));
    }

    [SkippableFact]
    public async Task prefill_with_only_the_service_stops_at_the_specialist_step()
    {
        var h = H(); var s = await h.SeedAsync();
        await SecondSpecialistAsync(s, "Олена", null, sharedService: s.ServiceId);
        h.Ai.Enqueue(ChatHarness.Tool("start_booking_flow", new { serviceId = s.ServiceId }));
        await h.SayAsync(s, "Хочу на стрижку");

        Assert.Contains("Оберіть спеціаліста для «Стрижка»", Last(h).Text);
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("specialist", flow.Step);
        Assert.Empty(await h.ProposalsAsync(s));
    }

    [SkippableFact]
    public async Task start_booking_flow_without_arguments_starts_at_the_service_step_and_repeat_call_gives_one_flow()
    {
        var h = H(); var s = await h.SeedAsync();
        h.Ai.Enqueue(new AiResponse([
            new AiToolUseBlock("a", "start_booking_flow", JsonSerializer.SerializeToElement(new { })),
            new AiToolUseBlock("b", "start_booking_flow", JsonSerializer.SerializeToElement(new { serviceId = s.ServiceId })),
        ]));
        await h.SayAsync(s, "Хочу записатись");

        Assert.Equal("Оберіть послугу:", Last(h).Text);
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("service", flow.Step);
        Assert.Equal("llm", flow.Origin);
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Markup is not null));   // одне повідомлення кроку, а не два
    }

    [SkippableFact]
    public async Task prepare_booking_is_not_available_to_the_model_while_the_flow_is_available()
    {
        var h = H(); var s = await h.SeedAsync();
        h.Ai.Enqueue(ChatHarness.PrepareCall(s), ChatHarness.Text("Ось що вийшло."));
        await h.SayAsync(s, "Хочу на стрижку о 10");

        Assert.Equal(2, h.Ai.Calls.Count);
        Assert.DoesNotContain("prepare_booking", h.Ai.Calls[0].ToolNames);
        var result = Assert.Single(ToolResults(h.Ai.Calls[1]));
        Assert.True(result.IsError);
        Assert.Contains("tool_not_allowed_in_client_scope", result.Content);
        Assert.Empty(await h.ProposalsAsync(s));
        Assert.DoesNotContain(h.Bot.Messages, m => m.Text.StartsWith("Підсумок запису"));
    }

    [SkippableFact]
    public async Task slot_pick_is_not_created_for_find_slots_while_the_flow_is_available()
    {
        var h = H(); var s = await h.SeedAsync();
        h.Ai.Enqueue(ChatHarness.Tool("find_slots", new { serviceId = s.ServiceId, date = Tomorrow() }), ChatHarness.Text("Є вільний час, напишіть, що зручно."));
        await h.SayAsync(s, "Які вікна завтра на стрижку?");
        Assert.DoesNotContain(h.Bot.Messages, m => m.Markup is not null);
        await using var ctx = _db.CreateContext(s.TenantId);
        Assert.DoesNotContain(await ctx.ChatKeyboards.AsNoTracking().ToListAsync(), k => k.Kind == "slot_pick");
    }

    [SkippableTheory]
    [InlineData("flow_off")]
    [InlineData("channel_off")]
    [InlineData("suggest")]
    public async Task flow_tools_are_absent_when_flow_is_not_available(string variant)
    {
        var cfg = variant == "flow_off"
            ? new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true", ["ChatFlow:Enabled"] = "false" }
            : On;
        var h = H(cfg);
        var s = await h.SeedAsync(
            variant == "channel_off" ? "{\"assistant\":{\"enabled\":true,\"bookingFlow\":false}}" : "{\"assistant\":{\"enabled\":true}}",
            aiMode: variant == "suggest" ? "suggest" : null);
        h.Ai.Enqueue(ChatHarness.Tool("start_booking_flow", new { }), ChatHarness.Text("Чернетка."));
        await h.SayAsync(s, "Хочу записатись");

        var calls = h.Ai.Calls;
        Assert.NotEmpty(calls);
        Assert.DoesNotContain("start_booking_flow", calls[0].ToolNames);
        Assert.Contains("prepare_booking", calls[0].ToolNames);
        Assert.DoesNotContain("СТАН ЗАПИСУ", calls[0].System);
        if (calls.Count > 1) Assert.Contains("tool_not_allowed_in_client_scope", Assert.Single(ToolResults(calls[1])).Content);
        Assert.Empty(await FlowsAsync(s));
    }

    // ---------------------------------------------------------------- вільний текст у потоці

    [SkippableFact]
    public async Task free_text_in_flow_gets_a_state_block_and_the_current_step_is_resent_after_the_reply()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        var service = Last(h);
        await Press(h, s, service, "Стрижка");
        var time = Last(h);
        var firstFlow = Assert.Single(await FlowsAsync(s));

        h.Ai.Enqueue(ChatHarness.Text("Стрижка триває близько години."));
        await h.SayAsync(s, "А скільки триває стрижка?");

        var call = Assert.Single(h.Ai.Calls);
        Assert.Contains("start_booking_flow", call.ToolNames);
        Assert.DoesNotContain("prepare_booking", call.ToolNames);
        var block = call.System[call.System.IndexOf(ConversationTurnProcessor.FlowStateOpen, StringComparison.Ordinal)..];
        block = block[..(block.IndexOf(ConversationTurnProcessor.FlowStateClose, StringComparison.Ordinal) + ConversationTurnProcessor.FlowStateClose.Length)];
        Assert.Contains("крок: вибір часу", block);
        Assert.Contains("спеціаліст: Марина", block);
        Assert.Contains("послуга: Стрижка", block);
        Assert.Contains("заклад: Салон Центр", block);
        Assert.DoesNotMatch(new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase), block); // жодних id
        Assert.DoesNotContain(s.Token, block);

        var texts = h.Bot.Messages.Select(m => m.Text).ToList();
        var reply = texts.Last(x => x.Contains("близько години"));
        var resend = h.Bot.Messages.Last();
        Assert.StartsWith(ConversationTurnProcessor.FlowContinue + "Вільний час, Марина, Салон Центр:", resend.Text);
        Assert.NotNull(resend.Markup);
        Assert.Null(h.Bot.Messages.Single(m => m.Text == reply).Markup);                       // кнопки під текстом моделі не ставляться
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.True(flow.Version > firstFlow.Version);

        // стара клавіатура гасне, нова працює; обидва без нових викликів LLM
        var before = h.Bot.Messages.Count;
        await h.PressAsync(s, Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Data, Ref(time));
        Assert.DoesNotContain(h.Bot.Messages.Skip(before), m => m.Text.StartsWith("Підсумок запису"));
        await h.PressAsync(s, Kb(resend).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Data, Ref(resend));
        Assert.StartsWith("Підсумок запису:", Last(h).Text);
        Assert.Single(h.Ai.Calls);
    }

    [SkippableFact]
    public async Task resend_after_the_reply_can_be_switched_off()
    {
        var h = H(new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true", ["ChatFlow:Enabled"] = "true", ["ChatFlow:ResendStepAfterLlm"] = "false" });
        var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await Press(h, s, Last(h), "Стрижка");
        h.Ai.Enqueue(ChatHarness.Text("Так, звісно."));
        await h.SayAsync(s, "Ви працюєте у вихідні?");
        Assert.DoesNotContain(h.Bot.Messages, m => m.Text.StartsWith(ConversationTurnProcessor.FlowContinue));
    }

    [SkippableFact]
    public async Task free_text_on_the_summary_supersedes_the_proposal_and_returns_to_time_with_a_short_notice()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await Press(h, s, Last(h), "Стрижка");
        var time = Last(h);
        await h.PressAsync(s, Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Data, Ref(time));
        Assert.StartsWith("Підсумок запису:", Last(h).Text);

        h.Ai.Enqueue(ChatHarness.Text("Звісно, оберіть інший час."));
        await h.SayAsync(s, "А можна пізніше?");

        Assert.Equal("superseded", Assert.Single(await h.ProposalsAsync(s)).Status);
        var resend = h.Bot.Messages.Last();
        Assert.StartsWith(ConversationTurnProcessor.FlowContinue + "Вільний час, Марина, Салон Центр:", resend.Text);
        Assert.Contains(Kb(resend), b => b.Data.StartsWith("ft:", StringComparison.Ordinal));
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("active", flow.Status);
        Assert.Equal("time", flow.Step);
        Assert.Null(flow.ProposalId);
        Assert.Empty(await h.AppointmentsAsync(s));
    }

    [SkippableFact]
    public async Task l2_starting_a_new_flow_supersedes_the_previous_flows_proposal_and_revokes_its_confirm_keyboard()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await Press(h, s, Last(h), "Стрижка");
        var time = Last(h);
        await h.PressAsync(s, Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Data, Ref(time));
        Assert.StartsWith("Підсумок запису:", Last(h).Text);
        Assert.NotEqual("superseded", Assert.Single(await h.ProposalsAsync(s)).Status);

        h.Clock.Advance(TimeSpan.FromSeconds(40)); // повторний /book не раніше ніж через 30 с
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);

        Assert.Equal("superseded", Assert.Single(await h.ProposalsAsync(s)).Status);
        var flows = await FlowsAsync(s);
        Assert.Equal(2, flows.Count);
        Assert.Equal("superseded", flows[0].Status);
        Assert.Equal("active", flows[1].Status);
        await using var ctx = _db.CreateContext(s.TenantId);
        var confirm = await ctx.ChatKeyboards.AsNoTracking().Where(k => k.Kind == "booking_confirm").ToListAsync();
        Assert.NotEmpty(confirm);
        Assert.DoesNotContain(confirm, k => k.Status == "active");
    }

    // ---------------------------------------------------------------- текстовий вибір кроку (4f)

    [SkippableFact]
    public async Task exact_label_text_acts_as_a_button_press_without_llm()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await h.SayAsync(s, "  СТРИЖКА ");                         // регістр і пробіли не мають значення
        Assert.Contains("Вільний час, Марина, Салон Центр:", Last(h).Text);

        await h.SayAsync(s, "інший день");                         // підпис «📆 Інший день» без емодзі
        Assert.Equal("Оберіть день:", Last(h).Text);
        await h.SayAsync(s, "назад");
        Assert.Contains("Вільний час", Last(h).Text);

        Assert.Empty(h.Ai.Calls);
        Assert.Empty(await h.UsageAsync(s));
        Assert.Equal(0, await LlmCallsAsync(h, s));
        // текст клієнта у вхідному не змінюється, відповідь іде з ключем ai-reply
        var inbound = (await h.MessagesAsync(s)).Where(m => m.Direction == MessageDirection.Inbound && m.Kind == InboundKinds.Text).ToList();
        Assert.Contains(inbound, m => m.Body == "  СТРИЖКА " || m.Body.Trim() == "СТРИЖКА");
        Assert.All(inbound, m => Assert.Equal("processed", m.Status));
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("time", flow.Step);
        Assert.Equal("active", flow.Status);
    }

    [SkippableFact]
    public async Task text_cancel_label_cancels_the_flow_without_llm_or_handoff()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await h.SayAsync(s, "Скасувати");                           // збіг із підписом, а не прохання скасувати запис (немає handoff)
        Assert.Equal(FlowTemplates.Cancelled, h.Bot.Messages.Last().Text);
        Assert.Equal("client", Assert.Single(await FlowsAsync(s)).CancelReason);
        Assert.Empty(h.Ai.Calls);
        Assert.NotEqual("handoff", (await h.ConversationAsync(s)).AiState);
    }

    [SkippableFact]
    public async Task last_location_yes_and_other_are_handled_by_text_without_llm()
    {
        var h = H(); var s = await h.SeedAsync();
        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Channels.Where(c => c.Id == s.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(c => c.LocationId, (Guid?)null));
        Guid left;
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var loc = new Location { Name = "Салон Лівий", Address = "вул. Лівобережна 5", Timezone = "Europe/Kyiv" };
            ctx.Add(loc);
            await ctx.SaveChangesAsync();
            ctx.Add(new SpecialistLocation { SpecialistId = s.SpecialistId, LocationId = loc.Id, WorkingHours = Hours });
            await ctx.SaveChangesAsync();
            left = loc.Id;
        }
        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        var conv = await h.ConversationIdAsync(s);
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var client = new Client { FullName = "Anna", Phone = null, Email = null };
            ctx.Add(client);
            await ctx.SaveChangesAsync();
            ctx.Add(new Appointment
            {
                LocationId = left, SpecialistId = s.SpecialistId, ServiceId = s.ServiceId, ClientId = client.Id, StartsAt = DateTimeOffset.UtcNow.AddDays(-10),
                DurationMinutes = 60, Status = AppointmentStatus.Completed, Source = AppointmentSource.Telegram, PriceOriginal = 500m, PriceFinal = 500m,
            });
            await ctx.SaveChangesAsync();
            await ctx.Conversations.Where(c => c.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.ClientId, client.Id));
        }

        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await h.SayAsync(s, "Стрижка");
        Assert.Contains("як минулого разу?", Last(h).Text);
        await h.SayAsync(s, "так");
        Assert.Contains("Вільний час, Марина, Салон Лівий", Last(h).Text);

        h.Clock.Advance(TimeSpan.FromSeconds(40));                 // повтор /book поспіль у 30 с ігнорується (§28.3)
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await h.SayAsync(s, "Стрижка");
        Assert.Contains("як минулого разу?", Last(h).Text);
        await h.SayAsync(s, "Інший");
        var list = Last(h);
        Assert.Contains("Оберіть заклад, де Марина виконує «Стрижка»", list.Text);
        Assert.Empty(h.Ai.Calls);
        Assert.Empty(await h.UsageAsync(s));
        Assert.Equal(0, await LlmCallsAsync(h, s));
    }

    [SkippableFact]
    public async Task unknown_or_ambiguous_text_goes_to_the_model()
    {
        var h = H(); var s = await h.SeedAsync();
        await SecondSpecialistAsync(s, "Олена", "Манікюр");
        await SecondSpecialistAsync(s, "Ірина", "МАНІКЮР");
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        h.Ai.Enqueue(ChatHarness.Text("Манікюр і манікюр - різні послуги, оберіть кнопкою."));
        await h.SayAsync(s, "манікюр");                            // два підписи з однаковою нормалізацією - не вгадуємо
        Assert.Single(h.Ai.Calls);
        h.Ai.Enqueue(ChatHarness.Text("Не знаю, запитайте адміністратора."));
        await h.SayAsync(s, "а який у вас паркінг?");
        Assert.Equal(2, h.Ai.Calls.Count);
        Assert.Single((await FlowsAsync(s)).Where(f => f.Step == "service"));
    }

    // ---------------------------------------------------------------- T-flow-no-slots, no-days, price_changed (кнопки)

    [SkippableFact]
    public async Task no_slots_show_the_notice_with_other_day_and_back_and_no_days_notice_follows()
    {
        var h = H(); var s = await h.SeedAsync();
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            // вікно 10 хвилин коротше за послугу (60 хв): майстер придатний, але вільного часу немає
            const string narrow =
                """{"mon":[{"from":"00:00","to":"00:10"}],"tue":[{"from":"00:00","to":"00:10"}],"wed":[{"from":"00:00","to":"00:10"}],"thu":[{"from":"00:00","to":"00:10"}],"fri":[{"from":"00:00","to":"00:10"}],"sat":[{"from":"00:00","to":"00:10"}],"sun":[{"from":"00:00","to":"00:10"}]}""";
            await ctx.SpecialistLocations.Where(x => x.SpecialistId == s.SpecialistId).ExecuteUpdateAsync(u => u.SetProperty(x => x.WorkingHours, narrow));
        }
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await Press(h, s, Last(h), "Стрижка");
        var none = Last(h);
        Assert.EndsWith("У Марина немає вільного часу найближчими днями.", none.Text);
        var buttons = Kb(none);
        Assert.DoesNotContain(buttons, b => b.Data.StartsWith("ft:", StringComparison.Ordinal));
        Assert.Contains(buttons, b => b.Text.Contains("Інший день"));
        Assert.Contains(buttons, b => b.Text.Contains("Назад"));
        Assert.DoesNotContain(buttons, b => b.Text.Contains("Інший спеціаліст")); // майстер один

        await Press(h, s, none, "Інший день");
        // дні скануються вікнами по MaxScanDays: поки вікно порожнє, а горизонт не вичерпано, є «Ще дні»; наприкінці - T-flow-no-days
        for (var i = 0; i < 12 && Last(h).Text != FlowTemplates.NoDays; i++)
        {
            Assert.DoesNotContain(Kb(Last(h)), b => b.Data.StartsWith("fd:", StringComparison.Ordinal));
            await Press(h, s, Last(h), "Ще");
        }
        Assert.Equal(FlowTemplates.NoDays, Last(h).Text);
        Assert.Contains(Kb(Last(h)), b => b.Text.Contains("Назад"));
        Assert.Contains(Kb(Last(h)), b => b.Text.Contains("Скасувати"));
        Assert.Empty(h.Ai.Calls);
        Assert.Equal("active", Assert.Single(await FlowsAsync(s)).Status);
    }

    [SkippableFact]
    public async Task price_changed_on_the_confirm_button_gives_a_fresh_summary_and_a_new_proposal()
    {
        var h = H(); var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await Press(h, s, Last(h), "Стрижка");
        var time = Last(h);
        await h.PressAsync(s, Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Data, Ref(time));
        var summary = Last(h);
        Assert.Contains("500", summary.Text);

        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.ServicePrices.Where(p => p.ServiceId == s.ServiceId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Price, 650m));
        await Press(h, s, summary, "Так, записатися");

        Assert.Empty(await h.AppointmentsAsync(s));                  // запис за старою ціною не створено
        var texts = h.Bot.Messages.Select(m => m.Text).ToList();
        Assert.Contains(ChatTemplates.PriceChanged, texts);
        var fresh = Last(h);
        Assert.StartsWith("Підсумок запису:", fresh.Text);
        Assert.Contains("650", fresh.Text);
        Assert.NotEqual(summary.MessageId, fresh.MessageId);
        var proposals = await h.ProposalsAsync(s);
        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, p => Assert.NotNull(p.FlowId));
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("confirm", flow.Step);
        Assert.Equal(proposals.Last().Id, flow.ProposalId);

        await Press(h, s, fresh, "Так, записатися");                 // нова пропозиція підтверджується за новою ціною
        var appointment = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(650m, appointment.PriceFinal);
        Assert.Equal("completed", Assert.Single(await FlowsAsync(s)).Status);
        Assert.Empty(h.Ai.Calls);
        Assert.Empty(await h.UsageAsync(s));
        Assert.Equal(0, await LlmCallsAsync(h, s));
    }
}
