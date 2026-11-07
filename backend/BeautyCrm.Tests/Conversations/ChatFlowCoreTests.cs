using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Tests.Conversations;

/// <summary>Чисті перевірки TASK-838: формат callback, навігація, розкладки кроків, відсутність залежності від шару ШІ. Без БД.</summary>
public sealed class ChatFlowUnitTests
{
    private const string Tok = "AAAAAAAAAAAAAAAAAAAAAA";

    [Theory]
    [InlineData("fs:" + Tok + ":0", true)]
    [InlineData("fn:" + Tok + ":9", true)]
    [InlineData("ft:" + Tok + ":3", true)]
    [InlineData("fs:" + Tok, false)]            // index обов'язковий
    [InlineData("fx:" + Tok + ":0", false)]
    [InlineData("fs:" + Tok + ":10", false)]
    public void flow_callback_requires_index_and_known_verb(string data, bool valid) =>
        Assert.Equal(valid, CallbackAddress.TryParse(data, out _));

    [Fact]
    public void nav_is_allowed_only_for_proper_steps()
    {
        Assert.True(FlowNav.AllowedFor(FlowSteps.LastLocation, FlowNav.OtherLocation));
        Assert.False(FlowNav.AllowedFor(FlowSteps.Time, FlowNav.OtherLocation));
        Assert.True(FlowNav.AllowedFor(FlowSteps.Time, FlowNav.OtherDay));
        Assert.False(FlowNav.AllowedFor(FlowSteps.Service, FlowNav.Back));
        Assert.False(FlowNav.AllowedFor(FlowSteps.Confirm, FlowNav.Cancel));
    }

    [Fact]
    public void step_keyboards_respect_layout_limits()
    {
        var b = new ChatKeyboardBuilder(new ChatButtonsOptions { Enabled = true });
        var now = DateTimeOffset.UtcNow;
        var names = new FlowNames("Стрижка", "Марина", "Салон", "вул. 1", "Europe/Kyiv");
        var slots = Enumerable.Range(0, 6).Select(i => new FlowItem(FlowVerbs.Time, "", StartsAt: now.AddHours(i + 1))).ToList();
        var time = new FlowView(FlowSteps.Time, slots, [FlowNav.More, FlowNav.OtherDay, FlowNav.OtherSpecialist, FlowNav.Back], [], null, names);
        var plan = b.FlowStep(time, Guid.NewGuid(), 1, now.AddMinutes(30), now);
        Assert.True(plan.IsValid());
        Assert.Equal(10, plan.Options.Count);
        var list = new FlowView(FlowSteps.Service, Enumerable.Range(0, 5).Select(i => new FlowItem(FlowVerbs.Service, "Послуга " + i, ServiceId: Guid.NewGuid())).ToList(),
            [FlowNav.More, FlowNav.Cancel], [], null, names);
        Assert.True(b.FlowStep(list, Guid.NewGuid(), 0, now.AddMinutes(30), now).IsValid());
        var last = new FlowView(FlowSteps.LastLocation, [new FlowItem(FlowVerbs.Location, "Салон", LocationId: Guid.NewGuid(), Address: "а")],
            [FlowNav.OtherLocation, FlowNav.Back], [], null, names);
        Assert.True(b.FlowStep(last, Guid.NewGuid(), 0, now.AddMinutes(30), now).IsValid());
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void flow_handler_does_not_depend_on_the_ai_layer()
    {
        var banned = new[] { "BeautyAssistant", "IAiClient", "AiToolExecutor" };
        foreach (var p in typeof(ChatBookingFlowService).GetConstructors().SelectMany(c => c.GetParameters()))
            Assert.DoesNotContain(p.ParameterType.Name, banned);
        Assert.DoesNotContain(typeof(ChatBookingFlowService).Assembly.GetReferencedAssemblies(), a => a.Name == "BeautyCrm.Infrastructure");
        var dir = new DirectoryInfo(Path.GetDirectoryName(ThisFile())!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeautyCrm.Infrastructure", "BeautyCrm.Infrastructure.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        foreach (var file in new[] { "AI/Beauty/Conversations/ConversationTurnProcessor.Flow.cs", "AI/Beauty/Conversations/ChatFlowTemplates.cs" })
        {
            var text = File.ReadAllText(Path.Combine(dir!.FullName, "BeautyCrm.Infrastructure", file));
            foreach (var word in banned.Append("assistant.")) Assert.DoesNotContain(word, text);
        }
        foreach (var f in Directory.GetFiles(Path.Combine(dir!.FullName, "BeautyCrm.Application", "Features", "BeautyChatFlow"), "*.cs"))
            foreach (var word in banned) Assert.DoesNotContain(word, File.ReadAllText(f));
    }
}

/// <summary>Інтеграційні тести ядра потоку запису (§28.15): реальний PostgreSQL з RLS, fake Telegram, mock-ШІ (викликів бути не повинно).</summary>
public sealed class ChatFlowCoreTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatFlowCoreTests(PostgresRlsFixture db) => _db = db;

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

    private async Task<List<ChatBookingFlow>> FlowsAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.ChatBookingFlows.AsNoTracking().OrderBy(f => f.CreatedAt).ToListAsync();
    }

    private async Task<Guid> SecondSpecialistAsync(ChatSeed s, string specialist, string service, Guid? location = null)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var sp = new Specialist { FullName = specialist };
        var svc = new Service { Name = service, DurationMinutes = 30 };
        ctx.AddRange(sp, svc);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = location ?? s.LocationId, WorkingHours = Hours });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svc.Id });
        ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = 300m });
        await ctx.SaveChangesAsync();
        return sp.Id;
    }

    private async Task UnpinChannelAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        await ctx.Channels.Where(c => c.Id == s.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(c => c.LocationId, (Guid?)null));
    }

    private async Task<FakeBotApi.Sent> StartAsync(ChatHarness h, ChatSeed s)
    {
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        return Last(h);
    }

    // ---------------------------------------------------------------- повний потік

    [SkippableFact]
    public async Task full_flow_with_autoskips_books_without_any_llm_call()
    {
        var h = H(); var s = await h.SeedAsync();
        var start = await StartAsync(h, s);
        Assert.Equal("Оберіть послугу:", start.Text);

        await Press(h, s, start, "Стрижка");
        var time = Last(h);
        Assert.Contains("Обрано: спеціаліст Марина", time.Text);
        Assert.DoesNotContain("Обрано: заклад", time.Text); // заклад каналу фіксований: крок не показується й не оголошується
        Assert.Contains("Вільний час, Марина, Салон Центр:", time.Text);
        Assert.Empty(await h.AppointmentsAsync(s));

        var slot = Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal));
        await h.PressAsync(s, slot.Data, Ref(time));
        var summary = Last(h);
        Assert.StartsWith("Підсумок запису:", summary.Text);
        Assert.Empty(await h.AppointmentsAsync(s)); // підсумок лише після вибору слота, запис - після «Так»
        Assert.Equal("confirm", Assert.Single(await FlowsAsync(s)).Step);

        await Press(h, s, summary, "Так, записатися");
        Assert.Single(await h.AppointmentsAsync(s));
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("completed", flow.Status);
        Assert.Empty(h.Ai.Calls);
        Assert.Empty(await h.UsageAsync(s));
        var proposal = Assert.Single(await h.ProposalsAsync(s));
        Assert.Equal(flow.Id, proposal.FlowId);
    }

    [SkippableFact]
    public async Task single_location_is_announced_and_skipped_for_a_network_channel()
    {
        var h = H(); var s = await h.SeedAsync();
        await UnpinChannelAsync(s);
        await Press(h, s, await StartAsync(h, s), "Стрижка");
        var time = Last(h);
        Assert.Contains("Обрано: спеціаліст Марина", time.Text);
        Assert.Contains("Обрано: заклад «Салон Центр», вул. Хрещатик 1", time.Text);
    }

    [SkippableFact]
    public async Task flow_disabled_keeps_the_old_book_reply()
    {
        var h = H(new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true", ["ChatFlow:Enabled"] = "false" });
        var s = await h.SeedAsync();
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.BookStart, h.Bot.Messages.Last().Text);
        Assert.Empty(await FlowsAsync(s));
    }

    [SkippableFact]
    public async Task flow_needs_channel_buttons_enabled()
    {
        var h = H(); var s = await h.SeedAsync("{\"assistant\":{\"enabled\":true,\"buttons\":false}}");
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        Assert.Empty(await FlowsAsync(s));
    }

    // ---------------------------------------------------------------- два спеціалісти з різними послугами, фільтри

    [SkippableFact]
    public async Task two_specialists_with_different_services_filter_both_ways()
    {
        var h = H(); var s = await h.SeedAsync();
        await SecondSpecialistAsync(s, "Олена", "Манікюр");
        var start = await StartAsync(h, s);
        Assert.Contains(Kb(start), b => b.Text == "Стрижка");
        Assert.Contains(Kb(start), b => b.Text == "Манікюр");

        await Press(h, s, start, "Манікюр");
        var time = Last(h);
        Assert.Contains("Обрано: спеціаліст Олена", time.Text);
        Assert.DoesNotContain("Марина", time.Text);
        Assert.Empty(h.Ai.Calls);
    }

    [SkippableFact]
    public async Task two_specialists_for_one_service_show_specialist_step_and_other_specialist_nav()
    {
        var h = H(); var s = await h.SeedAsync();
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var sp = new Specialist { FullName = "Олена" };
            ctx.Add(sp);
            await ctx.SaveChangesAsync();
            ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = s.LocationId, WorkingHours = Hours });
            ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = s.ServiceId });
            await ctx.SaveChangesAsync();
        }
        await Press(h, s, await StartAsync(h, s), "Стрижка");
        var spec = Last(h);
        Assert.Contains("Оберіть спеціаліста для «Стрижка»", spec.Text);
        await Press(h, s, spec, "Олена");
        var time = Last(h);
        Assert.Contains("Вільний час, Олена", time.Text);
        Assert.Contains(Kb(time), b => b.Text.Contains("Інший спеціаліст"));
        await Press(h, s, time, "Інший спеціаліст");
        Assert.Contains("Оберіть спеціаліста", Last(h).Text);
    }

    // ---------------------------------------------------------------- «останній заклад»

    [SkippableFact]
    public async Task last_location_is_offered_when_it_belongs_to_the_pair_and_can_be_declined()
    {
        var h = H(); var s = await h.SeedAsync();
        await UnpinChannelAsync(s);
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

        await Press(h, s, await StartAsync(h, s), "Стрижка");
        var last = Last(h);
        Assert.Contains("Записати вас у «Салон Лівий», вул. Лівобережна 5, як минулого разу?", last.Text);
        Assert.Contains(Kb(last), b => b.Text == "Так, у «Салон Лівий»");
        await Press(h, s, last, "Інший заклад");
        var list = Last(h);
        Assert.Contains("Оберіть заклад, де Марина виконує «Стрижка»", list.Text);
        Assert.Contains(Kb(list), b => b.Text == "Салон Центр");
        await Press(h, s, list, "Салон Центр");
        Assert.Contains("Вільний час, Марина, Салон Центр", Last(h).Text);
    }

    // ---------------------------------------------------------------- «Інший час», «Інший день»

    [SkippableFact]
    public async Task other_time_returns_to_later_slots_and_other_day_lists_days()
    {
        var h = H(); var s = await h.SeedAsync();
        await Press(h, s, await StartAsync(h, s), "Стрижка");
        var time = Last(h);
        var first = Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal));
        await h.PressAsync(s, first.Data, Ref(time));
        var summary = Last(h);

        await Press(h, s, summary, "Інший час");
        var again = Last(h);
        Assert.Contains("Добре, цей час не бронюю", again.Text);
        Assert.DoesNotContain(Kb(again), b => b.Text == first.Text); // слоти лише після відхиленого
        Assert.Equal("time", Assert.Single((await FlowsAsync(s)).Where(f => f.Status == "active")).Step);

        await Press(h, s, again, "Інший день");
        var days = Last(h);
        Assert.Equal("Оберіть день:", days.Text);
        var day = Kb(days).First(b => b.Data.StartsWith("fd:", StringComparison.Ordinal));
        await h.PressAsync(s, day.Data, Ref(days));
        Assert.Contains("Вільний час", Last(h).Text);
        Assert.Empty(h.Ai.Calls);
    }

    // ---------------------------------------------------------------- гонка, повтор, таймаут, handoff

    [SkippableFact]
    public async Task two_concurrent_presses_of_one_keyboard_have_a_single_winner()
    {
        var h = H(); var s = await h.SeedAsync();
        var start = await StartAsync(h, s);
        var data = Data(start, "Стрижка");
        var (conv, _) = await h.IngestCallbackAsync(s, data, Ref(start));
        await h.IngestCallbackAsync(s, data, Ref(start));
        await Task.WhenAll(h.TurnAsync(s.TenantId, conv), h.TurnAsync(s.TenantId, conv));
        await h.TurnAsync(s.TenantId, conv);
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text.Contains("Вільний час")));
        Assert.Equal(1, (await FlowsAsync(s)).Single().Version);
    }

    [SkippableFact]
    public async Task repeated_press_of_a_used_keyboard_gives_no_second_step()
    {
        var h = H(); var s = await h.SeedAsync();
        var start = await StartAsync(h, s);
        await Press(h, s, start, "Стрижка");
        var count = h.Bot.Messages.Count;
        await Press(h, s, start, "Стрижка");
        Assert.Equal(count, h.Bot.Messages.Count);
        Assert.Equal(1, (await FlowsAsync(s)).Single().Version);
    }

    [SkippableFact]
    public async Task press_after_idle_timeout_gets_expired_notice_and_flow_is_expired()
    {
        var h = H(); var s = await h.SeedAsync();
        var start = await StartAsync(h, s);
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        await Press(h, s, start, "Стрижка");
        var reply = h.Bot.Messages.Last();
        Assert.Equal(FlowTemplates.Expired, reply.Text);
        Assert.Contains(Kb(reply), b => b.Data == "i:book");
        Assert.Equal("expired", Assert.Single(await FlowsAsync(s)).Status);
        Assert.Empty(await h.AppointmentsAsync(s));
    }

    [SkippableFact]
    public async Task sweeper_expires_idle_flow_without_message_to_client()
    {
        var h = H(); var s = await h.SeedAsync();
        await StartAsync(h, s);
        var count = h.Bot.Messages.Count;
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        using (var scope = h.Sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<BeautyCrm.Infrastructure.Data.Tenancy.TenantContext>().SetTenant(s.TenantId);
            var store = scope.ServiceProvider.GetRequiredService<IChatFlowStore>();
            Assert.Single(await store.ExpireDueAsync(h.Clock.GetUtcNow(), 10, default));
        }
        Assert.Equal("expired", Assert.Single(await FlowsAsync(s)).Status);
        Assert.Equal(count, h.Bot.Messages.Count);
    }

    [SkippableFact]
    public async Task handoff_cancels_the_flow_and_revokes_its_keyboard()
    {
        var h = H(); var s = await h.SeedAsync();
        var start = await StartAsync(h, s);
        await h.PressAsync(s, "i:human", Ref(start));
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("cancelled", flow.Status);
        Assert.Equal("handoff", flow.CancelReason);
        await using var ctx = _db.CreateContext(s.TenantId);
        Assert.All(await ctx.ChatKeyboards.AsNoTracking().Where(k => k.Kind.StartsWith("flow_")).ToListAsync(), k => Assert.NotEqual("active", k.Status));
    }

    [SkippableFact]
    public async Task declining_the_summary_cancels_the_flow()
    {
        var h = H(); var s = await h.SeedAsync();
        await Press(h, s, await StartAsync(h, s), "Стрижка");
        var time = Last(h);
        await h.PressAsync(s, Kb(time).First(b => b.Data.StartsWith("ft:", StringComparison.Ordinal)).Data, Ref(time));
        await Press(h, s, Last(h), "Ні");
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("cancelled", flow.Status);
        Assert.Equal("declined", flow.CancelReason);
    }

    [SkippableFact]
    public async Task service_step_is_cancelled_by_client()
    {
        var h = H(); var s = await h.SeedAsync();
        await Press(h, s, await StartAsync(h, s), "Скасувати");
        Assert.Equal(FlowTemplates.Cancelled, h.Bot.Messages.Last().Text);
        Assert.Equal("client", Assert.Single(await FlowsAsync(s)).CancelReason);
    }
}
