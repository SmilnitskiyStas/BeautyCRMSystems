using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Inbox;
using BeautyCrm.Tests.Conversations;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace BeautyCrm.Tests.Marketing;

/// <summary>TASK-790 (рев'ю безпеки маркетингу 2026-10): юніт-перевірки без БД - маскування кодів, історія LLM, IPv6, роль у DTO запису.</summary>
public sealed class MarketingSecurityUnitTests
{
    [Theory]
    [InlineData("Ваш код: ABCDE-FGHJK, дійсний до п'ятниці")]
    [InlineData("abcde-fghjk")]
    [InlineData("ABCDE–FGHJK")]
    [InlineData("мій ABCDEFGH12 тут")]
    [InlineData("промокод sale2030")]
    [InlineData("Купон: VIP2024 діє")]
    [InlineData("код summer25")]
    public void masker_replaces_coupon_codes_in_any_body(string body)
    {
        var masked = CouponCodeMasker.Mask(body);
        Assert.Contains(CouponCodeMasker.Placeholder, masked);
        foreach (var secret in new[] { "ABCDE", "FGHJK", "ABCDEFGH12", "sale2030", "VIP2024", "summer25" })
            Assert.DoesNotContain(secret, masked, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Хочу на стрижку о 10")]
    [InlineData("мій номер 380501112233")]
    [InlineData("0501112233")]
    [InlineData("Запис на 2024-10-31 о 10:30-11:30")]
    [InlineData("Купон «Літня знижка» застосовано")]
    [InlineData("Купон не застосовано: діє вигідніша акція.")]
    [InlineData("Wednesday")]
    public void masker_leaves_ordinary_text_alone(string body) => Assert.Equal(body, CouponCodeMasker.Mask(body));

    [Fact]
    public void llm_history_never_contains_a_coupon_code_from_any_message_kind()
    {
        var items = new List<ChatHistoryItem>
        {
            new(ChatHistoryRole.Client, "Привіт, мій код ABCDE-FGHJK"),
            new(ChatHistoryRole.Assistant, "Ваш персональний код: ABCDE-FGHJK. Дійсний 7 днів.", ChatHistoryKind.Template),
            new(ChatHistoryRole.Assistant, "промокод SUMMER25 діє до кінця місяця", ChatHistoryKind.Staff),
            new(ChatHistoryRole.Client, "код: summer25"),
            new(ChatHistoryRole.Assistant, "Дякую, код ABCDE-FGHJK прийнято"),
        };
        var text = string.Join("\n", ChatHistory.Build(items, new ChatPromptOptions()).SelectMany(m => m.Blocks).OfType<AiTextBlock>().Select(b => b.Text));
        Assert.Contains(CouponCodeMasker.Placeholder, text);
        foreach (var secret in new[] { "ABCDE", "FGHJK", "SUMMER25" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2:1111:2222:3333:4444", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("203.0.113.5", "203.0.113.5", true)]
    [InlineData("203.0.113.5", "203.0.113.6", false)]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5", true)]
    public void ipv6_limit_key_is_the_slash_64_prefix(string a, string b, bool same) =>
        Assert.Equal(same, ClientIpKey.Normalize(a) == ClientIpKey.Normalize(b));

    [Fact]
    public void ip_key_of_garbage_does_not_throw()
    {
        Assert.Equal("unknown", ClientIpKey.Normalize(null));
        Assert.Equal("not-an-ip", ClientIpKey.Normalize(" not-an-ip "));
    }

    [Fact]
    public void appointment_for_specialist_hides_the_coupon_code_hint_but_managers_see_it()
    {
        var dto = new AppointmentDto(Guid.NewGuid(), Guid.NewGuid(), "L", Guid.NewGuid(), "S", Guid.NewGuid(), "Svc", Guid.NewGuid(), "C",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 60, "confirmed", "admin", 500m, 400m, null, "none", null,
            Coupon: new AppointmentCouponDto(Guid.NewGuid(), "shared", "A12", "Акція", "applied"));
        var owner = new Actor(Guid.NewGuid(), Guid.NewGuid(), Roles.Owner, null);
        var spec = new Actor(Guid.NewGuid(), owner.TenantId, Roles.Specialist, Guid.NewGuid());
        Assert.Equal("A12", dto.ForViewer(owner).Coupon!.CodeHint);
        var seen = dto.ForViewer(spec).Coupon!;
        Assert.Equal("", seen.CodeHint);
        Assert.Equal("applied", seen.Outcome);
    }

    [Fact]
    public void default_blocklist_covers_obvious_words()
    {
        var o = new CouponOptions();
        foreach (var w in new[] { "SALE", "PROMO", "VIP", "DISCOUNT" }) Assert.Contains(w, o.BlockedCodes);
    }
}

/// <summary>
/// TASK-790 H1: відписка на вході (webhook/polling) не залежить від ШІ-конвеєра. «Стоп» / «не надсилати акції» / <c>/stop</c> / <c>i:stop</c> /
/// <c>i:mkoff</c> при прийомі повідомлення знижують згоду й відписують клієнта в тій самій транзакції; конвеєр вимкнено - повідомлення processed, тиша.
/// </summary>
public sealed class IngestUnsubscribeTests : IClassFixture<PostgresRlsFixture>
{
    private const string AssistantOn = "{\"assistant\":{\"enabled\":true}}";
    private const string AssistantOff = "{\"assistant\":{\"enabled\":false}}";
    private readonly PostgresRlsFixture _db;
    public IngestUnsubscribeTests(PostgresRlsFixture db) => _db = db;

    private sealed record Env(ChatHarness H, ChatSeed S, Guid Conv, Guid ClientId)
    {
        public Guid Tenant => S.TenantId;
    }

    private async Task<Env> NewAsync(bool pipeline, string channelSettings = AssistantOn, bool withClient = true, string? level = "full")
    {
        var cfg = new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true", ["Marketing:Enabled"] = "true" };
        var h = ChatHarness.Create(_db, options: ChatHarness.Opts(pipeline), extraConfig: cfg);
        var s = await h.SeedAsync(channelSettings);
        var conv = (await h.IngestAsync(s, "Привіт")).Conversation!.Value;
        await using var ctx = _db.CreateContext(s.TenantId);
        await ctx.Messages.Where(m => m.ConversationId == conv).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, "processed"));
        var clientId = Guid.Empty;
        if (withClient)
        {
            var c = new Client { FullName = "Анна Секретна", MarketingConsent = level is "full" or "selective" };
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            clientId = c.Id;
            await ctx.Conversations.Where(x => x.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClientId, (Guid?)c.Id));
            if (level is not null)
                ctx.MarketingConsents.Add(new MarketingConsent
                {
                    TenantId = s.TenantId, ClientId = c.Id, Channel = "telegram", Level = level, Source = "chat", TextVersion = "mc-v1",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
            await ctx.SaveChangesAsync();
        }
        return new Env(h, s, conv, clientId);
    }

    private async Task<(string Level, string Source)> ConsentAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var c = await ctx.MarketingConsents.AsNoTracking().SingleAsync(x => x.ClientId == e.ClientId && x.Channel == "telegram");
        return (c.Level, c.Source);
    }

    private async Task<Client> ClientAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.Clients.AsNoTracking().SingleAsync(x => x.Id == e.ClientId);
    }

    private async Task<string> LastInboundStatusAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.Messages.AsNoTracking().Where(m => m.ConversationId == e.Conv && m.Direction == MessageDirection.Inbound)
            .OrderByDescending(m => m.CreatedAt).Select(m => m.Status).FirstAsync();
    }

    private async Task<AudienceResult> AudienceAsync(Env e)
    {
        using var scope = e.H.Sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(e.Tenant);
        return await scope.ServiceProvider.GetRequiredService<AudienceService>()
            .ComputeAsync(new AudienceSpec("news", null, "all_consented", 0, null, 0, 0, null), DateTimeOffset.UtcNow, default);
    }

    [SkippableTheory]
    [InlineData("Стоп", "text", true)]
    [InlineData("stop", "text", true)]
    [InlineData("СТОП", "text", true)]
    [InlineData("/stop", "command", true)]
    [InlineData("не нагадувати", "text", true)]
    [InlineData("не надсилати акції", "text", false)]
    [InlineData("Без акцій", "text", false)]
    [InlineData("i:stop", "callback", true)]
    [InlineData("i:mkoff", "callback", false)]
    public async Task stop_on_a_channel_with_the_pipeline_disabled_sets_consent_none_and_the_next_campaign_skips_the_client(string input, string kind, bool full)
    {
        var e = await NewAsync(pipeline: false);
        Assert.Single((await AudienceAsync(e)).Eligible); // до відписки клієнт у аудиторії

        if (kind == "callback") await e.H.IngestCallbackAsync(e.S, input, "1");
        else await e.H.IngestAsync(e.S, input, kind: kind);

        var consent = await ConsentAsync(e);
        Assert.Equal(("none", full ? "stop" : "chat"), consent);
        var client = await ClientAsync(e);
        Assert.False(client.MarketingConsent);
        Assert.Equal(full, client.Unsubscribed);
        Assert.Equal("processed", await LastInboundStatusAsync(e));
        Assert.Empty(e.H.Bot.Messages); // конвеєр вимкнено - клієнту мовчки

        var audience = await AudienceAsync(e);
        Assert.Empty(audience.Eligible);
        Assert.Equal(1, audience.Excluded.Unsubscribed);
    }

    [SkippableFact]
    public async Task stop_with_the_channel_assistant_switched_off_still_unsubscribes_and_is_processed()
    {
        var e = await NewAsync(pipeline: true, channelSettings: AssistantOff);
        await e.H.IngestAsync(e.S, "Стоп");
        Assert.Equal(("none", "stop"), await ConsentAsync(e));
        Assert.True((await ClientAsync(e)).Unsubscribed);
        Assert.Equal("processed", await LastInboundStatusAsync(e));
    }

    [SkippableFact]
    public async Task stop_with_the_pipeline_enabled_unsubscribes_on_ingest_and_the_pipeline_still_answers_with_the_template()
    {
        var e = await NewAsync(pipeline: true);
        await e.H.IngestAsync(e.S, "Стоп");
        Assert.Equal(("none", "stop"), await ConsentAsync(e)); // ефект вже при прийомі
        Assert.Equal("received", await LastInboundStatusAsync(e)); // відповідь дає конвеєр
        await e.H.TurnAsync(e.Tenant, e.Conv);
        Assert.Equal("processed", await LastInboundStatusAsync(e));
        Assert.Equal(ChatTemplates.StopMarketing, e.H.Bot.Messages.Last().Text);
        Assert.Equal(("none", "stop"), await ConsentAsync(e));
        await using var ctx = _db.CreateContext(e.Tenant);
        Assert.Equal(1, await ctx.MarketingConsentEvents.CountAsync(x => x.ClientId == e.ClientId && x.LevelTo == "none")); // подія одна: повтор ідемпотентний
    }

    [SkippableFact]
    public async Task ordinary_sentences_that_only_contain_stop_words_do_not_unsubscribe()
    {
        var e = await NewAsync(pipeline: false);
        await e.H.IngestAsync(e.S, "Зупиніться на цій послузі, без акцій на цей раз я не проти");
        await e.H.IngestAsync(e.S, "Stop by at 10");
        Assert.Equal(("full", "chat"), await ConsentAsync(e));
        Assert.False((await ClientAsync(e)).Unsubscribed);
    }

    [SkippableFact]
    public async Task stop_before_the_client_exists_marks_the_conversation_and_the_lazy_client_gets_a_none_stop_consent_row()
    {
        var e = await NewAsync(pipeline: false, withClient: false);
        await e.H.IngestAsync(e.S, "Стоп");
        await using (var ctx = _db.CreateContext(e.Tenant))
            Assert.NotNull((await ctx.Conversations.AsNoTracking().SingleAsync(c => c.Id == e.Conv)).RemindersStopAt);

        Guid clientId;
        using (var scope = e.H.Sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(e.Tenant);
            clientId = await scope.ServiceProvider.GetRequiredService<IChatBookingStore>().EnsureClientAsync(e.Conv, "Анна", default);
        }
        await using var check = _db.CreateContext(e.Tenant);
        var row = await check.MarketingConsents.AsNoTracking().SingleAsync(c => c.ClientId == clientId && c.Channel == "telegram");
        Assert.Equal(("none", "stop"), (row.Level, row.Source));
        Assert.True((await check.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId)).Unsubscribed);
        Assert.Equal(1, await check.MarketingConsentEvents.CountAsync(x => x.ClientId == clientId && x.Source == "stop"));
    }
}

/// <summary>TASK-790 M1/L10/M5/L4 у чаті: коди купонів у БД, історії LLM, лічильник «голих» пошуків, модуль beauty_marketing.</summary>
public sealed class ChatMarketingSecurityTests : IClassFixture<PostgresRlsFixture>
{
    private const string Shared = "SALE2030";
    private readonly PostgresRlsFixture _db;
    public ChatMarketingSecurityTests(PostgresRlsFixture db) => _db = db;

    private static ChatAssistantOptions Roomy() => new()
    {
        Enabled = true, SweepMinAge = TimeSpan.Zero, MaxTemplateRepliesPerConversationPerMinute = 100, MaxTurnsPerConversationPerMinute = 100,
    };

    private sealed record Env(ChatHarness H, ChatSeed S, Guid Conv)
    {
        public Guid Tenant => S.TenantId;
    }

    private async Task<Env> NewAsync(bool marketing = true, Action<IServiceCollection>? configure = null, bool module = true)
    {
        var cfg = new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true", ["Marketing:Enabled"] = marketing ? "true" : "false" };
        var h = ChatHarness.Create(_db, options: Roomy(), extraConfig: cfg, configure: configure);
        var s = await h.SeedAsync();
        if (!module)
        {
            await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"UPDATE tenants SET modules = '{{}}' WHERE id = '{s.TenantId}'", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        var conv = (await h.IngestAsync(s, "Привіт")).Conversation!.Value;
        await using var ctx = _db.CreateContext(s.TenantId);
        await ctx.Messages.Where(m => m.ConversationId == conv).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, "processed"));
        var c = new Client { FullName = "Анна Секретна" };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        await ctx.Conversations.Where(x => x.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClientId, (Guid?)c.Id));
        return new Env(h, s, conv);
    }

    private async Task AddSharedCouponAsync(Env e, string code)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var promo = new Promotion { Name = "Спільна", DiscountType = DiscountType.Fixed, DiscountValue = 100m, IsActive = true, Access = "code_only" };
        ctx.Add(promo);
        await ctx.SaveChangesAsync();
        ctx.Add(new Coupon
        {
            Kind = "shared", PromotionId = promo.Id, Code = code, CodeHash = CouponCodes.Hash(code), CodeHint = CouponCodes.Hint(code),
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(5),
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<List<string>> InboundBodiesAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.Messages.AsNoTracking().Where(m => m.ConversationId == e.Conv && m.Direction == MessageDirection.Inbound)
            .OrderBy(m => m.CreatedAt).Select(m => m.Body).ToListAsync();
    }

    [SkippableFact]
    public async Task code_in_a_text_with_marketing_disabled_is_masked_in_the_database_and_never_reaches_the_model()
    {
        var e = await NewAsync(marketing: false);
        e.H.Ai.Enqueue(ChatHarness.Text("Чим можу допомогти?"));
        await e.H.SayAsync(e.S, "мій код ABCDE-FGHJK, застосуйте");
        Assert.DoesNotContain(await InboundBodiesAsync(e), b => b.Contains("ABCDE", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(e.H.Ai.Calls);
        var sent = string.Join("\n", e.H.Ai.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Blocks).OfType<AiTextBlock>().Select(b => b.Text));
        Assert.DoesNotContain("ABCDE", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FGHJK", sent, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task code_sent_during_handoff_is_held_masked()
    {
        var e = await NewAsync();
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.Conversations.Where(c => c.Id == e.Conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiState, "handoff")
                .SetProperty(c => c.HandoffReason, "human_requested").SetProperty(c => c.HandoffAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(12)));
        await e.H.SayAsync(e.S, "код: ABCDE-FGHJK");
        var bodies = await InboundBodiesAsync(e);
        Assert.DoesNotContain(bodies, b => b.Contains("ABCDE", StringComparison.OrdinalIgnoreCase));
        await using var check = _db.CreateContext(e.Tenant);
        Assert.Equal("held", await check.Messages.AsNoTracking().Where(m => m.ConversationId == e.Conv && m.Direction == MessageDirection.Inbound)
            .OrderByDescending(m => m.CreatedAt).Select(m => m.Status).FirstAsync());
    }

    [SkippableFact]
    public async Task inbox_api_never_returns_a_coupon_code_in_message_bodies_or_previews()
    {
        var e = await NewAsync();
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            var owner = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@t.test", FullName = "Власник", PasswordHash = "x", Role = Roles.Owner };
            ctx.Add(owner);
            // повідомлення кампанії з кодом (пише воркер) - як є в БД
            ctx.Messages.Add(new Message
            {
                ConversationId = e.Conv, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "Знижка 20%! Ваш код: ABCDE-FGHJK",
                Kind = "text", SentAt = DateTimeOffset.UtcNow, Status = "sent",
            });
            await ctx.SaveChangesAsync();
            var actor = new Actor(owner.Id, e.Tenant, Roles.Owner, null);
            using var scope = e.H.Sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(e.Tenant);
            var inbox = scope.ServiceProvider.GetRequiredService<InboxService>();
            var detail = (await inbox.GetAsync(actor, e.Conv, null, null, default)).Value!;
            Assert.DoesNotContain(detail.Messages, m => m.Body.Contains("ABCDE", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(detail.Messages, m => m.Body.Contains(CouponCodeMasker.Placeholder));
            var list = (await inbox.ListAsync(actor, null, null, null, null, null, default)).Value!;
            Assert.DoesNotContain(list.Items, i => i.LastMessagePreview.Contains("ABCDE", StringComparison.OrdinalIgnoreCase));
        }
    }

    [SkippableFact]
    public async Task bare_token_lookups_are_counted_and_stop_after_the_limit_so_a_valid_shared_code_is_no_longer_guessable()
    {
        var e = await NewAsync(configure: s =>
        {
            s.RemoveAll<CouponOptions>();
            s.AddSingleton(new CouponOptions { ChatBareLookupPerConversationPerHour = 2 });
        });
        await AddSharedCouponAsync(e, Shared);
        foreach (var guess in new[] { "foobar1", "bazqux2" })
        {
            e.H.Ai.Enqueue(ChatHarness.Text("Чим можу допомогти?"));
            await e.H.SayAsync(e.S, guess);
        }
        e.H.Ai.Enqueue(ChatHarness.Text("Чим можу допомогти?"));
        await e.H.SayAsync(e.S, "sale2030"); // правильний код, але ліміт промахів вичерпано: хеш не шукається
        Assert.DoesNotContain(e.H.Bot.Messages, m => m.Text.StartsWith("Код прийнято", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task bare_shared_code_is_still_recognised_within_the_limit()
    {
        var e = await NewAsync();
        await AddSharedCouponAsync(e, Shared);
        await e.H.SayAsync(e.S, "sale2030");
        Assert.StartsWith("Код прийнято", e.H.Bot.Messages.Last().Text);
    }

    [SkippableFact]
    public async Task without_the_beauty_marketing_module_the_chat_has_no_marketing_menu_codes_or_offers()
    {
        var withModule = await NewAsync();
        await withModule.H.SayAsync(withModule.S, "/marketing", kind: "command");
        Assert.Contains(withModule.H.Bot.Messages, m => m.Markup is not null && m.Markup.Contains("mc:"));

        var without = await NewAsync(module: false);
        await AddSharedCouponAsync(without, Shared);
        await without.H.SayAsync(without.S, "/marketing", kind: "command");
        Assert.DoesNotContain(without.H.Bot.Messages, m => m.Markup is not null && m.Markup.Contains("mc:"));
        without.H.Ai.Enqueue(ChatHarness.Text("Чим можу допомогти?"));
        await without.H.SayAsync(without.S, "код sale2030");
        Assert.DoesNotContain(without.H.Bot.Messages, m => m.Text.StartsWith("Код прийнято", StringComparison.Ordinal));
        // відписка працює й без модуля
        await without.H.SayAsync(without.S, "Стоп");
        await using var ctx = _db.CreateContext(without.Tenant);
        Assert.True(await ctx.MarketingConsents.AnyAsync(c => c.Level == "none"));
    }
}
