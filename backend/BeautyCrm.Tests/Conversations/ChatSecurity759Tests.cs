using System.Net;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Tests.Conversations;

/// <summary>
/// TASK-759 (рев'ю безпеки M3, L8, L10): ліміт шаблонних відповідей без LLM, «Стоп» поза лімітами, ліміт нових розмов для команд,
/// одна активна appointment_list, кнопки каналу вимкнено, довжина вихідних у UTF-16. Реальний PostgreSQL з RLS, fake Telegram.
/// </summary>
public sealed class ChatSecurity759Tests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatSecurity759Tests(PostgresRlsFixture db) => _db = db;

    private static IDictionary<string, string?> Cfg(params (string Key, string Value)[] extra)
    {
        var d = new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true" };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private ChatHarness H(int? templates = null, int? newConversations = null, params (string Key, string Value)[] extra) =>
        ChatHarness.Create(_db, new ChatAssistantOptions
        {
            Enabled = true, SweepMinAge = TimeSpan.Zero,
            MaxTemplateRepliesPerConversationPerMinute = templates ?? 6,
            MaxNewConversationsPerChannelPerHour = newConversations ?? 30,
        }, extraConfig: Cfg(extra));

    private static async Task<int> InboundCountAsync(ChatHarness h, ChatSeed s, string status) =>
        (await h.MessagesAsync(s)).Count(m => m.Direction == MessageDirection.Inbound && m.Status == status);

    // ================================================================ M3

    [SkippableFact]
    public async Task template_replies_over_the_minute_limit_give_one_rate_limited_then_silence_and_recover_after_a_minute()
    {
        var h = H(templates: 3);
        var s = await h.SeedAsync();
        for (var i = 0; i < 3; i++) await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        Assert.Equal(3, h.Bot.Messages.Count(m => m.Text == ChatTemplates.MyEmpty));

        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        await h.SayAsync(s, "/cancel", kind: InboundKinds.Command);
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.RateLimited));
        Assert.Equal(3, h.Bot.Messages.Count(m => m.Text == ChatTemplates.MyEmpty));
        Assert.Equal(4, h.Bot.Messages.Count);
        Assert.Equal(2, await InboundCountAsync(h, s, "skipped"));

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        Assert.Equal(4, h.Bot.Messages.Count(m => m.Text == ChatTemplates.MyEmpty));
        Assert.Equal(0, h.Ai.Calls.Count);
    }

    [SkippableFact]
    public async Task identical_help_start_and_book_are_throttled_and_the_repeat_is_processed_silently()
    {
        var h = H();
        var s = await h.SeedAsync();
        await h.SayAsync(s, "/help", kind: InboundKinds.Command);
        await h.SayAsync(s, "/help", kind: InboundKinds.Command);
        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        await h.SayAsync(s, "/start", kind: InboundKinds.Command);
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.Help));
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.Greeting));
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.BookStart));
        Assert.Equal(6, await InboundCountAsync(h, s, "processed")); // 3 з відповіддю + 3 тихо
        Assert.Equal(0, await InboundCountAsync(h, s, "received"));

        h.Clock.Advance(TimeSpan.FromSeconds(31));
        await h.SayAsync(s, "/help", kind: InboundKinds.Command);
        Assert.Equal(2, h.Bot.Messages.Count(m => m.Text == ChatTemplates.Help));
    }

    [SkippableFact]
    public async Task stop_by_command_and_by_button_still_unsubscribes_when_the_template_limit_is_exhausted_but_stays_silent()
    {
        var h = H(templates: 1);
        var s = await h.SeedAsync();
        await h.SayAsync(s, "/my", kind: InboundKinds.Command); // бюджет вичерпано
        await h.SayAsync(s, "/my", kind: InboundKinds.Command); // T-rate-limited
        var before = h.Bot.Messages.Count;
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.RateLimited));

        var conv = await h.SayAsync(s, "/stop", kind: InboundKinds.Command);
        Assert.Equal(before, h.Bot.Messages.Count); // без відповіді
        Assert.NotNull((await h.ConversationAsync(s)).RemindersStopAt);
        Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == ChatJournal.ReminderStop));

        await using (var ctx = _db.CreateContext(s.TenantId))
            await ctx.Conversations.Where(c => c.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.RemindersStopAt, (DateTimeOffset?)null));
        await h.PressAsync(s, "i:stop", "500");
        Assert.Equal(before, h.Bot.Messages.Count);
        Assert.NotNull((await h.ConversationAsync(s)).RemindersStopAt);
        Assert.Equal(2, (await h.ActionsAsync(s)).Count(a => a.ToolName == ChatJournal.ReminderStop));
    }

    [SkippableFact]
    public async Task stop_button_is_not_blocked_by_the_press_rate_limit()
    {
        var h = H(extra: ("ChatButtons:MaxCallbacksPerConversationPerMinute", "5"));
        var s = await h.SeedAsync();
        for (var i = 0; i < 6; i++) await h.PressAsync(s, "i:my", "500"); // 5 відповідей + 1 пропущено
        await h.PressAsync(s, "i:stop", "500");
        Assert.NotNull((await h.ConversationAsync(s)).RemindersStopAt);
    }

    [SkippableFact]
    public async Task informational_buttons_count_in_the_template_limit_and_repeated_help_is_silent()
    {
        var h = H(templates: 2);
        var s = await h.SeedAsync();
        await h.PressAsync(s, "i:help", "500");
        await h.PressAsync(s, "i:help", "500"); // та сама довідка: тихо, бюджет не витрачено
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.Help));
        await h.PressAsync(s, "i:my", "500");
        await h.PressAsync(s, "i:my", "500"); // ліміт 2 вичерпано
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.MyEmpty));
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.RateLimited));
        await h.PressAsync(s, "i:my", "500");
        Assert.Equal(1, h.Bot.Messages.Count(m => m.Text == ChatTemplates.RateLimited)); // далі тиша
        Assert.Equal(0, await InboundCountAsync(h, s, "received"));
    }

    [SkippableFact]
    public async Task commands_of_new_conversations_over_the_hourly_channel_limit_get_one_busy_notice_then_silence()
    {
        var h = H(newConversations: 1);
        var s = await h.SeedAsync();
        await h.SayAsync(s, "/start", chat: "100", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.Greeting, h.Bot.Messages.Last().Text);

        await h.SayAsync(s, "/start", chat: "101", kind: InboundKinds.Command);
        Assert.Equal(ChatTemplates.Busy, h.Bot.Messages.Last().Text);
        var count = h.Bot.Messages.Count;
        await h.SayAsync(s, "/help", chat: "101", kind: InboundKinds.Command);
        await h.SayAsync(s, "/my", chat: "101", kind: InboundKinds.Command);
        Assert.Equal(count, h.Bot.Messages.Count);
        Assert.Equal(0, h.Ai.Calls.Count);
    }

    [SkippableFact]
    public async Task a_new_my_list_revokes_the_previous_active_appointment_list_keyboard()
    {
        var h = H();
        var s = await h.SeedAsync();
        var (conv, summary) = (await h.ShowSummaryAsync(s), h.Bot.Messages.Last(m => m.Markup is not null));
        await h.PressAsync(s, "by:" + await TokenOfAsync(s, conv, KeyboardKinds.BookingConfirm), summary.MessageId.ToString());
        h.Clock.Advance(TimeSpan.FromSeconds(1));

        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        await h.SayAsync(s, "/my", kind: InboundKinds.Command);
        await using var ctx = _db.CreateContext(s.TenantId);
        var lists = await ctx.ChatKeyboards.AsNoTracking().Where(k => k.Kind == KeyboardKinds.AppointmentList).OrderBy(k => k.CreatedAt).ToListAsync();
        Assert.Equal(2, lists.Count);
        Assert.Equal(["revoked", "active"], lists.Select(k => k.Status));
        // розмітку старого списку прибрано
        Assert.Contains(h.Bot.Edits, e => e.Empty);
    }

    private async Task<string> TokenOfAsync(ChatSeed s, Guid conv, string kind)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.ChatKeyboards.AsNoTracking().Where(k => k.ConversationId == conv && k.Kind == kind).OrderByDescending(k => k.CreatedAt)
            .Select(k => k.Token).FirstAsync();
    }

    // ================================================================ L10

    private async Task SetChannelSettingsAsync(ChatSeed s, string settings)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        await ctx.Channels.Where(c => c.Id == s.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Settings, settings));
    }

    [SkippableFact]
    public async Task old_keyboards_do_nothing_when_buttons_are_switched_off_on_the_channel_but_stop_still_works()
    {
        var h = H();
        var s = await h.SeedAsync();
        var conv = await h.ShowSummaryAsync(s);
        var summary = h.Bot.Messages.Last(m => m.Markup is not null);
        var token = await TokenOfAsync(s, conv, KeyboardKinds.BookingConfirm);

        await SetChannelSettingsAsync(s, "{\"assistant\":{\"enabled\":true,\"buttons\":false}}");
        await h.PressAsync(s, "by:" + token, summary.MessageId.ToString());

        Assert.Empty(await h.AppointmentsAsync(s)); // «Так, записатися» не створила запис
        Assert.Equal(ChatTemplates.ActionStale, h.Bot.Messages.Last().Text); // дружня відповідь
        Assert.Contains(h.Bot.Edits, e => e.MessageId == summary.MessageId.ToString() && e.Empty); // клавіатуру прибрано

        // «Стоп» лишається чинним
        await h.PressAsync(s, "i:stop", "500");
        Assert.Equal(ChatTemplates.Stop, h.Bot.Messages.Last().Text);
        Assert.NotNull((await h.ConversationAsync(s)).RemindersStopAt);
    }

    // ================================================================ L8

    [Fact]
    public async Task telegram_adapter_never_sends_more_than_4096_utf16_units_and_does_not_split_an_emoji()
    {
        var bot = new FakeBotApi();
        var adapter = new TelegramAdapter(new HttpClient(bot), new ChCreds());
        var text = "a" + string.Concat(Enumerable.Repeat("😀", 3000)); // 6001 одиниця; 4096-та одиниця розрізала б пару
        await adapter.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", text), default);

        var sent = Assert.Single(bot.Messages).Text;
        Assert.True(sent.Length <= 4096);
        Assert.Equal(4095, sent.Length);
        Assert.False(char.IsHighSurrogate(sent[^1]));
        Assert.StartsWith("a😀", sent);

        await adapter.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", new string('b', 4096)), default);
        Assert.Equal(4096, bot.Messages[1].Text.Length);
    }

    [Fact]
    public void manager_reply_text_up_to_4096_utf16_is_accepted_by_the_normalizer_and_longer_is_only_caught_by_length_check()
    {
        var ok = string.Concat(Enumerable.Repeat("😀", 2048)); // 4096 UTF-16, 2048 кодових точок
        Assert.Equal(4096, BeautyCrm.Infrastructure.Integrations.Channels.Inbox.InboxService.NormalizeText(ok)!.Length);
        var many = string.Concat(Enumerable.Repeat("😀", 2049));
        Assert.Equal(4098, BeautyCrm.Infrastructure.Integrations.Channels.Inbox.InboxService.NormalizeText(many)!.Length); // далі відхиляє ReplyAsync (invalid_length)
    }

    private sealed class ChCreds : IChannelCredentialsProvider
    {
        public ChannelCredentials? Get(string channel) => new("123456:ABCDEFtoken", "whsecret", "app-secret-1", "verify-token-1");
    }
}
