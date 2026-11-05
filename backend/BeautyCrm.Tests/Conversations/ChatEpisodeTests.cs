using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BeautyCrm.Tests.Conversations.ChatHarness;

namespace BeautyCrm.Tests.Conversations;

/// <summary>TASK-799: межа епізоду діалогу для LLM. Старі повідомлення лишаються в БД, але не йдуть у запит до моделі.</summary>
public sealed class ChatEpisodeTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatEpisodeTests(PostgresRlsFixture db) => _db = db;

    private static readonly DateTimeOffset T0 = new(2030, 1, 7, 10, 0, 0, TimeSpan.Zero);
    private static EpisodeCandidate In(int min, string kind, string body, string? cb = null) => new(T0.AddMinutes(min), true, false, kind, body, cb);
    private static EpisodeCandidate Sys(int min, string body) => new(T0.AddMinutes(min), false, true, "text", body, null);

    private static DateTimeOffset? Cut(IEnumerable<EpisodeCandidate> c, DateTimeOffset? manager = null, int[]? visible = null, int batch = 100, TimeSpan? idle = null) =>
        ChatEpisode.FindCutoff(c, manager, (visible ?? []).Select(m => T0.AddMinutes(m)).ToList(), T0.AddMinutes(batch), T0.AddMinutes(batch), idle ?? TimeSpan.FromHours(6));

    // ---------------------------------------------------------------- чиста логіка

    [Fact]
    public void book_command_button_and_templates_are_boundaries_ordinary_traffic_is_not()
    {
        Assert.True(ChatEpisode.IsBoundary(In(1, InboundKinds.Command, "/book")));
        Assert.True(ChatEpisode.IsBoundary(In(1, InboundKinds.Command, "/BOOK@MyBot")));
        Assert.True(ChatEpisode.IsBoundary(In(1, InboundKinds.Callback, "[кнопка]", ButtonVerbs.Book)));
        Assert.True(ChatEpisode.IsBoundary(Sys(1, ChatTemplates.BookStart)));
        Assert.True(ChatEpisode.IsBoundary(Sys(1, ChatTemplates.ConfirmedHeader + "\nМанікюр")));
        Assert.True(ChatEpisode.IsBoundary(Sys(1, ChatTemplates.Cancelled(T0, "Europe/Kyiv"))));

        Assert.False(ChatEpisode.IsBoundary(In(1, InboundKinds.Command, "/help")));
        Assert.False(ChatEpisode.IsBoundary(In(1, InboundKinds.Command, "/start")));
        Assert.False(ChatEpisode.IsBoundary(In(1, InboundKinds.Callback, "[кнопка]", ButtonVerbs.My)));
        Assert.False(ChatEpisode.IsBoundary(In(1, InboundKinds.Text, "/book")));      // текст, а не команда
        Assert.False(ChatEpisode.IsBoundary(Sys(1, ChatTemplates.Greeting)));
        Assert.False(ChatEpisode.IsBoundary(new EpisodeCandidate(T0, false, false, "text", ChatTemplates.BookStart, null))); // відповідь ШІ, не шаблон
    }

    [Fact]
    public void cutoff_is_the_latest_boundary_and_continuation_has_none()
    {
        Assert.Null(Cut([Sys(1, ChatTemplates.Greeting)], visible: [1, 2, 3]));
        Assert.Equal(T0.AddMinutes(5), Cut([In(2, InboundKinds.Command, "/book"), Sys(5, ChatTemplates.ConfirmedHeader)]));
        Assert.Equal(T0.AddMinutes(9), Cut([Sys(5, ChatTemplates.ConfirmedHeader)], manager: T0.AddMinutes(9)));
    }

    [Fact]
    public void boundaries_after_the_current_batch_are_ignored()
        => Assert.Null(Cut([In(200, InboundKinds.Command, "/book")], batch: 100));

    [Fact]
    public void idle_gap_over_six_hours_cuts_but_exactly_six_hours_or_less_does_not()
    {
        Assert.Equal(T0.AddMinutes(10), Cut([], visible: [0, 10], batch: 10 + 6 * 60 + 1));
        Assert.Null(Cut([], visible: [0, 10], batch: 10 + 6 * 60));
        Assert.Null(Cut([], visible: [0, 10], batch: 40));
    }

    // ---------------------------------------------------------------- конвеєр (PostgreSQL)

    private static string UserTexts(MockAiCall call) =>
        string.Join("\n", call.Messages.SelectMany(m => m.Blocks).OfType<AiTextBlock>().Select(b => b.Text));

    private async Task ShiftAllAsync(ChatSeed s, TimeSpan back)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        await ctx.Messages.ExecuteUpdateAsync(x => x.SetProperty(m => m.CreatedAt, m => m.CreatedAt - back));
    }

    [SkippableFact]
    public async Task after_book_the_model_does_not_see_the_old_request_but_the_db_keeps_everything()
    {
        var h = ChatHarness.Create(_db); var s = await h.SeedAsync();
        h.Ai.Enqueue(Text("Є вільний час 26 жовтня о 16:15."));
        await h.SayAsync(s, "Хочу стрижку 26 жовтня");
        var before = (await h.MessagesAsync(s)).Count;

        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await Task.Delay(30);
        h.Ai.Enqueue(Text("Добре, на коли?"));
        await h.SayAsync(s, "Манікюр у п'ятницю");

        var text = UserTexts(h.Ai.Calls.Last());
        Assert.Contains("Манікюр у п'ятницю", text);
        Assert.DoesNotContain("26 жовтня", text);
        Assert.True((await h.MessagesAsync(s)).Count > before);
        Assert.Contains(await h.MessagesAsync(s), m => m.Body.Contains("Хочу стрижку 26 жовтня")); // у БД/Inbox лишається
    }

    [SkippableFact]
    public async Task ordinary_continuation_keeps_the_context()
    {
        var h = ChatHarness.Create(_db); var s = await h.SeedAsync();
        h.Ai.Enqueue(Text("Є у вівторок і середу."));
        await h.SayAsync(s, "Які дні вільні на манікюр?");
        await Task.Delay(30);
        h.Ai.Enqueue(Text("У п'ятницю є 12:00."));
        await h.SayAsync(s, "А на п'ятницю?");

        var text = UserTexts(h.Ai.Calls.Last());
        Assert.Contains("Які дні вільні на манікюр?", text);
        Assert.Contains("Є у вівторок і середу.", text);
    }

    [SkippableFact]
    public async Task silence_over_six_hours_starts_a_new_episode_while_shorter_silence_does_not()
    {
        var h = ChatHarness.Create(_db); var s = await h.SeedAsync();
        h.Ai.Enqueue(Text("Відповідь 1"));
        await h.SayAsync(s, "Старе питання про стрижку");
        await ShiftAllAsync(s, TimeSpan.FromHours(1));
        h.Ai.Enqueue(Text("Відповідь 2"));
        await h.SayAsync(s, "Питання через годину");
        Assert.Contains("Старе питання про стрижку", UserTexts(h.Ai.Calls.Last()));

        await ShiftAllAsync(s, TimeSpan.FromHours(7));
        h.Ai.Enqueue(Text("Відповідь 3"));
        await h.SayAsync(s, "Нове питання наступного дня");
        var text = UserTexts(h.Ai.Calls.Last());
        Assert.Contains("Нове питання наступного дня", text);
        Assert.DoesNotContain("Старе питання", text);
        Assert.DoesNotContain("Питання через годину", text);
    }

    [SkippableFact]
    public async Task after_a_confirmed_booking_the_next_message_starts_a_new_episode()
    {
        var h = ChatHarness.Create(_db); var s = await h.SeedAsync();
        await h.ShowSummaryAsync(s);
        await Task.Delay(30);
        await h.SayAsync(s, "Так");
        Assert.Contains(await h.OutboundAsync(s), m => m.Body.StartsWith(ChatTemplates.ConfirmedHeader));
        await Task.Delay(30);
        h.Ai.Enqueue(Text("Чим допомогти?"));
        await h.SayAsync(s, "А ще педикюр є?");

        var text = UserTexts(h.Ai.Calls.Last());
        Assert.Contains("педикюр", text);
        Assert.DoesNotContain("Хочу на стрижку о 10", text);
    }

    [SkippableTheory]
    [InlineData("resolve_conversation")]
    [InlineData("resume_ai")]
    public async Task manager_resolve_or_return_to_ai_starts_a_new_episode(string tool)
    {
        var h = ChatHarness.Create(_db); var s = await h.SeedAsync();
        h.Ai.Enqueue(Text("Відповідь про стару тему"));
        var conv = await h.SayAsync(s, "Стара тема про гель-лак");
        await Task.Delay(30);
        using (var scope = h.Sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(s.TenantId);
            await scope.ServiceProvider.GetRequiredService<IAiActionJournal>().AppendAsync(new AiActionRecord(Guid.NewGuid(), s.TenantId, tool,
                $"conversation:{conv}", DateTimeOffset.UtcNow, AiActionStatus.Done, false, AiMode.Confirm, "{}", conv), default);
        }
        await Task.Delay(30);
        h.Ai.Enqueue(Text("Слухаю"));
        await h.SayAsync(s, "Нове запитання");

        var text = UserTexts(h.Ai.Calls.Last());
        Assert.Contains("Нове запитання", text);
        Assert.DoesNotContain("Стара тема", text);
    }
}
