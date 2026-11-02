using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Application.Features.BeautyMarketing;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Marketing;

/// <summary>
/// TASK-779 (§26.5, §26.7, §26.11.3, §26.11.5, §26.14 сц. 1-6, 10, 16, 26; ADR-015): аудиторії й кампанії. Реальний PostgreSQL + RLS:
/// сегменти й межі, рівні згоди, частота, перегляд без ПД, підтвердження (ідемпотентність, гонка, revision), скасування, статистика,
/// ролі/IDOR/no-store, прапорці, логи.
/// </summary>
public sealed class CampaignTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Secret = "унікальний-текст-шаблону-кампанії";
    private const string ClientName = "Оксана Приватна";
    private readonly List<IDisposable> _disposables = [];
    private static readonly string[] MarketingModules = [.. BeautyModules.All, BeautyModules.Marketing];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    // ------------------------------------------------------------------ unit

    [Fact]
    public void coupon_hash_golden_vector_matches_worker()
    {
        // Той самий вектор у worker/src/jobs/coupon-code.test.ts (Node <-> .NET)
        Assert.Equal("60ca79e65ce75a6d75b70701e1316a2687f621fe41be01cd4fda45234af1987b", CouponCodes.Hash("abcde-12345"));
        Assert.Equal("ABCDE12345", CouponCodes.Normalize(" abcde-12345 "));
        Assert.Equal("345", CouponCodes.Hint("ABCDE-12345"));
    }

    [Theory]
    [InlineData("Привіт, {name}! Знижка {discount} на {service}", "promo", "personal", null)]
    [InlineData("Нова послуга у нас, {name}", "news", "none", null)]
    [InlineData("", "promo", "none", "empty")]
    [InlineData("Привіт {unknown}", "promo", "none", "unknown placeholder")]
    [InlineData("Ваш код {code}", "promo", "none", "{code} requires a coupon")]
    [InlineData("Знижка {discount}", "news", "none", "not available for news")]
    [InlineData("Привіт <b>{name}</b>", "promo", "none", "markup tags")]
    [InlineData("Привіт {name", "promo", "none", "unbalanced")]
    [InlineData("Привіт\u0007", "promo", "none", "control")]
    public void template_validation(string template, string type, string mode, string? expectedPart)
    {
        var r = CampaignTemplate.Validate(template, type, mode);
        if (expectedPart is null) Assert.Null(r);
        else Assert.Contains(expectedPart, r, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void template_validation_rejects_too_long_and_worst_case_expansion()
    {
        Assert.NotNull(CampaignTemplate.Validate(new string('a', 1001), "promo", "none"));
        Assert.NotNull(CampaignTemplate.Validate(string.Concat(Enumerable.Repeat("{service}", 111)), "promo", "none"));
        Assert.Null(CampaignTemplate.Validate("Привіт\nрядок два {name}", "promo", "none"));
    }

    [Fact]
    public void sample_text_adds_conditions_code_and_unsubscribe_line()
    {
        var text = CampaignTemplate.Render(new CampaignTemplate.SampleInput(
            "Привіт, {name}!", "promo", "15%", "Стрижка", "01.11.2031", "30.11.2031", "ABCDE-FGHJK", Buttons: true));
        Assert.StartsWith("Привіт, Олена!", text);
        Assert.Contains("Умови: знижка 15% на Стрижка, діє з 01.11.2031 по 30.11.2031.", text);
        Assert.Contains("Ваш код: ABCDE-FGHJK", text);
        Assert.EndsWith(CampaignTemplate.UnsubscribeNoteButtons, text);
        var news = CampaignTemplate.Render(new CampaignTemplate.SampleInput("Новини", "news", "", "", "", "", null, Buttons: false));
        Assert.DoesNotContain("Умови", news);
        Assert.EndsWith(CampaignTemplate.UnsubscribeNote, news);
    }

    [Fact]
    public void segment_normalization_defaults_and_rules()
    {
        Assert.True(AudienceService.TryNormalize(new SegmentDto("lapsed"), out var l, out _));
        Assert.Equal(60, l.Days);
        Assert.True(AudienceService.TryNormalize(new SegmentDto("frequent"), out var f, out _));
        Assert.Equal((3, 90), (f.MinVisits, f.PeriodDays));
        Assert.False(AudienceService.TryNormalize(new SegmentDto("lapsed", Days: 0), out _, out _));
        Assert.False(AudienceService.TryNormalize(new SegmentDto("lapsed", Days: 731), out _, out _));
        Assert.False(AudienceService.TryNormalize(new SegmentDto("frequent", Days: 5), out _, out _));
        Assert.False(AudienceService.TryNormalize(new SegmentDto("frequent", ServiceId: Guid.NewGuid()), out _, out _));
        Assert.False(AudienceService.TryNormalize(new SegmentDto("new", Days: 5), out _, out _));
        Assert.False(AudienceService.TryNormalize(new SegmentDto("bogus"), out _, out _));
        Assert.False(AudienceService.TryNormalize(null, out _, out _));
    }

    // ------------------------------------------------------------------ каркас

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    private sealed record World(AuthApiFixture.TenantSeed Seed, string Owner, string Admin, string Spec, AuthApiFixture.BeautySeed B,
        Guid ServiceX, Guid ServiceY, Guid Loc2, Guid ChannelId)
    {
        public Guid Id => Seed.TenantId;
        public Actor OwnerActor => new(Seed.OwnerId, Seed.TenantId, Roles.Owner, null);
    }

    private async Task<World> NewWorldAsync(bool marketingModule = true)
    {
        var t = await fx.CreateTenantAsync(marketingModule ? MarketingModules : null);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        await using var ctx = fx.Db.CreateContext(t.TenantId);
        var x = new Service { Name = "Послуга X", DurationMinutes = 30 };
        var y = new Service { Name = "Послуга Y", DurationMinutes = 30 };
        var l2 = new Location { Name = "Другий", Timezone = "Europe/Kyiv" };
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot", IsActive = true };
        ctx.AddRange(x, y, l2, ch);
        // клієнт із SeedBeautyAsync не потрібен аудиторії: видаляємо з вибірки
        var seeded = await ctx.Clients.SingleAsync(c => c.Id == seed.ClientId);
        seeded.DeletedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
        return new World(t, owner.AccessToken, admin.AccessToken, spec.AccessToken, seed, x.Id, y.Id, l2.Id, ch.Id);
    }

    private sealed class Svc(BeautyDbContext db, CampaignService campaigns, AudienceService audience) : IAsyncDisposable
    {
        public BeautyDbContext Db { get; } = db;
        public CampaignService C { get; } = campaigns;
        public AudienceService A { get; } = audience;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Svc NewSvc(Guid tenantId, TimeProvider clock, ILogger<CampaignService>? log = null)
    {
        var tenant = new TenantContext(tenantId);
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(fx.Db.AppConnectionString).AddInterceptors(new TenantConnectionInterceptor(tenant));
        var db = new BeautyDbContext(options.Options, tenant);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketing:Enabled"] = "true" }).Build();
        var audience = new AudienceService(db, config);
        return new Svc(db, new CampaignService(db, tenant, audience, config, new CouponAttemptLimiter(clock), clock, log ?? NullLogger<CampaignService>.Instance), audience);
    }

    private async Task<Guid> ClientAsync(World w, string? name = null, string? level = "full", bool telegram = true, bool deleted = false, bool channelActive = true)
    {
        await using var ctx = fx.Db.CreateContext(w.Id);
        var c = new Client { FullName = name ?? "Клієнт " + Guid.NewGuid().ToString("N")[..6], DeletedAt = deleted ? DateTimeOffset.UtcNow : null };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        if (telegram)
        {
            var channelId = w.ChannelId;
            if (!channelActive)
            {
                var off = new Channel { Type = ChannelType.Telegram, Name = "off", IsActive = false };
                ctx.Add(off);
                await ctx.SaveChangesAsync();
                channelId = off.Id;
            }
            ctx.Add(new Conversation { ChannelId = channelId, ClientId = c.Id, ExternalChatId = Guid.NewGuid().ToString("N")[..10], LastMessageAt = DateTimeOffset.UtcNow });
        }
        if (level is not null)
            ctx.Add(new MarketingConsent
            {
                ClientId = c.Id, Channel = "telegram", Level = level, Source = level == "none" ? "stop" : "chat",
                TextVersion = level == "none" ? null : "mc-v1",
            });
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Guid> ApptAsync(World w, Guid clientId, Guid serviceId, DateTimeOffset startsAt, AppointmentStatus status = AppointmentStatus.Completed,
        Guid? locationId = null, decimal price = 100m, Guid? campaignId = null, Guid? couponId = null, string? cancelledByType = null)
    {
        await using var ctx = fx.Db.CreateContext(w.Id);
        var sp = new Specialist { FullName = "M " + Guid.NewGuid().ToString("N")[..5] }; // окремий майстер: без перетину слотів
        ctx.Add(sp);
        await ctx.SaveChangesAsync();
        var a = new Appointment
        {
            LocationId = locationId ?? w.B.LocationId, SpecialistId = sp.Id, ServiceId = serviceId, ClientId = clientId, StartsAt = startsAt,
            DurationMinutes = 30, Status = status, Source = AppointmentSource.Admin, PriceOriginal = price, PriceFinal = price,
            CampaignId = campaignId, CouponId = couponId, CancelledByType = cancelledByType,
            CancelledAt = status == AppointmentStatus.Cancelled ? startsAt : null,
        };
        ctx.Add(a);
        await ctx.SaveChangesAsync();
        return a.Id;
    }

    private async Task<Guid> PromotionAsync(World w, string access = "public", Guid[]? services = null, DateTimeOffset? endsAt = null, bool active = true,
        DiscountType type = DiscountType.Percent, decimal value = 15)
    {
        await using var ctx = fx.Db.CreateContext(w.Id);
        var p = new Promotion { Name = "Акція " + Guid.NewGuid().ToString("N")[..4], DiscountType = type, DiscountValue = value, Access = access, EndsAt = endsAt, IsActive = active };
        ctx.Add(p);
        await ctx.SaveChangesAsync();
        foreach (var s in services ?? []) ctx.Add(new PromotionService { PromotionId = p.Id, ServiceId = s });
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private static CampaignRequest Req(Guid? promotion, string kind = "all_consented", string type = "promo", string template = "Привіт, {name}! " + Secret,
        string mode = "none", Guid? loc = null, int? days = null, Guid? service = null, Guid? sharedCoupon = null, DateTimeOffset? from = null,
        DateTimeOffset? to = null, DateTimeOffset? scheduled = null, string name = "Весна") =>
        new(name, type, promotion, new SegmentDto(kind, days, service), loc, template, mode, sharedCoupon, from, to, scheduled);

    private static async Task<AudienceResult> AudienceAsync(Svc s, DateTimeOffset now, string kind, string type = "promo", Guid? promo = null,
        int days = 0, Guid? service = null, int minVisits = 0, int periodDays = 0, Guid? loc = null) =>
        await s.A.ComputeAsync(new AudienceSpec(type, promo, kind, days, service, minVisits, periodDays, loc), now, default);

    // ------------------------------------------------------------------ сегменти й межі (§26.5)

    [SkippableFact]
    public async Task segment_lapsed_general_uses_last_visit_across_network_and_location_filter()
    {
        NeedDb();
        var w = await NewWorldAsync();
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(10), TimeSpan.Zero);
        await using var s = NewSvc(w.Id, new Clock(now));
        var a = await ClientAsync(w); await ApptAsync(w, a, w.ServiceX, now.AddDays(-61));                       // L1, давно
        var b = await ClientAsync(w); await ApptAsync(w, b, w.ServiceX, now.AddDays(-60));                       // межа
        var c = await ClientAsync(w); await ApptAsync(w, c, w.ServiceX, now.AddDays(-59).AddHours(-23));         // 59д23г -> свіжіший за межу
        var d = await ClientAsync(w); await ApptAsync(w, d, w.ServiceY, now.AddDays(-90));
        await ApptAsync(w, d, w.ServiceY, now.AddDays(5), AppointmentStatus.Confirmed);                          // майбутній
        var e = await ClientAsync(w); await ApptAsync(w, e, w.ServiceX, now.AddDays(-80)); await ApptAsync(w, e, w.ServiceY, now.AddDays(-5)); // свіжий візит на іншу послугу
        var f = await ClientAsync(w); await ApptAsync(w, f, w.ServiceY, now.AddDays(-100), locationId: w.Loc2); // лише L2
        var g = await ClientAsync(w, deleted: true); await ApptAsync(w, g, w.ServiceX, now.AddDays(-100));
        var h = await ClientAsync(w); await ApptAsync(w, h, w.ServiceX, now.AddDays(-100), AppointmentStatus.Cancelled); // не completed

        var net = await AudienceAsync(s, now, "lapsed", days: 60);
        Assert.Equal(new[] { a, b, f }.Order().ToArray(), net.Eligible.Order().ToArray());
        Assert.Equal(3, net.Matched);
        var l1 = await AudienceAsync(s, now, "lapsed", days: 60, loc: w.B.LocationId);
        Assert.Equal(new[] { a, b }.Order().ToArray(), l1.Eligible.Order().ToArray());
        var l2 = await AudienceAsync(s, now, "lapsed", days: 60, loc: w.Loc2);
        Assert.Equal([f], l2.Eligible);
    }

    [SkippableFact]
    public async Task segment_lapsed_with_service_ignores_other_services_and_requires_no_future_on_it()
    {
        NeedDb();
        var w = await NewWorldAsync();
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(10), TimeSpan.Zero);
        await using var s = NewSvc(w.Id, new Clock(now));
        var inX1 = await ClientAsync(w); await ApptAsync(w, inX1, w.ServiceX, now.AddDays(-61));
        var inX2 = await ClientAsync(w); await ApptAsync(w, inX2, w.ServiceX, now.AddDays(-80)); await ApptAsync(w, inX2, w.ServiceY, now.AddDays(-5)); // інша послуга не впливає
        var futX = await ClientAsync(w); await ApptAsync(w, futX, w.ServiceX, now.AddDays(-80)); await ApptAsync(w, futX, w.ServiceX, now.AddDays(4), AppointmentStatus.Confirmed);
        var futY = await ClientAsync(w); await ApptAsync(w, futY, w.ServiceX, now.AddDays(-80)); await ApptAsync(w, futY, w.ServiceY, now.AddDays(4), AppointmentStatus.Confirmed); // майбутній на Y -> X лишається
        var freshX = await ClientAsync(w); await ApptAsync(w, freshX, w.ServiceX, now.AddDays(-80)); await ApptAsync(w, freshX, w.ServiceX, now.AddDays(-10));
        var onlyY = await ClientAsync(w); await ApptAsync(w, onlyY, w.ServiceY, now.AddDays(-100));
        var l2 = await ClientAsync(w); await ApptAsync(w, l2, w.ServiceX, now.AddDays(-100), locationId: w.Loc2);

        var r = await AudienceAsync(s, now, "lapsed", days: 60, service: w.ServiceX);
        Assert.Equal(new[] { inX1, inX2, futY, l2 }.Order().ToArray(), r.Eligible.Order().ToArray());
        var atL1 = await AudienceAsync(s, now, "lapsed", days: 60, service: w.ServiceX, loc: w.B.LocationId);
        Assert.Equal(new[] { inX1, inX2, futY }.Order().ToArray(), atL1.Eligible.Order().ToArray());
    }

    [SkippableFact]
    public async Task segment_frequent_counts_completed_in_period_strictly_and_by_location()
    {
        NeedDb();
        var w = await NewWorldAsync();
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(10), TimeSpan.Zero);
        await using var s = NewSvc(w.Id, new Clock(now));
        var ok = await ClientAsync(w);
        foreach (var d in new[] { 5, 30, 89 }) await ApptAsync(w, ok, w.ServiceX, now.AddDays(-d));
        var edge = await ClientAsync(w); // третій рівно на межі 90 днів: starts_at > now - 90д не виконується
        foreach (var d in new[] { 5, 30, 90 }) await ApptAsync(w, edge, w.ServiceX, now.AddDays(-d));
        var old = await ClientAsync(w);
        foreach (var d in new[] { 5, 30, 91 }) await ApptAsync(w, old, w.ServiceX, now.AddDays(-d));
        var mixed = await ClientAsync(w); // 3 візити, але один скасований і один no_show
        await ApptAsync(w, mixed, w.ServiceX, now.AddDays(-5)); await ApptAsync(w, mixed, w.ServiceX, now.AddDays(-6), AppointmentStatus.Cancelled);
        await ApptAsync(w, mixed, w.ServiceX, now.AddDays(-7), AppointmentStatus.NoShow);
        var split = await ClientAsync(w);
        await ApptAsync(w, split, w.ServiceX, now.AddDays(-5)); await ApptAsync(w, split, w.ServiceX, now.AddDays(-6));
        await ApptAsync(w, split, w.ServiceX, now.AddDays(-7), locationId: w.Loc2);

        var r = await AudienceAsync(s, now, "frequent", minVisits: 3, periodDays: 90);
        Assert.Equal(new[] { ok, split }.Order().ToArray(), r.Eligible.Order().ToArray());
        var l1 = await AudienceAsync(s, now, "frequent", minVisits: 3, periodDays: 90, loc: w.B.LocationId);
        Assert.Equal([ok], l1.Eligible);
        var two = await AudienceAsync(s, now, "frequent", minVisits: 2, periodDays: 90);
        Assert.Contains(edge, two.Eligible);
    }

    [SkippableFact]
    public async Task segment_new_is_exactly_one_completed_and_all_consented_respects_location()
    {
        NeedDb();
        var w = await NewWorldAsync();
        var now = DateTimeOffset.UtcNow;
        await using var s = NewSvc(w.Id, new Clock(now));
        var one = await ClientAsync(w); await ApptAsync(w, one, w.ServiceX, now.AddDays(-3));
        var two = await ClientAsync(w); await ApptAsync(w, two, w.ServiceX, now.AddDays(-3)); await ApptAsync(w, two, w.ServiceX, now.AddDays(-20));
        var none = await ClientAsync(w);
        var cancelledOnly = await ClientAsync(w); await ApptAsync(w, cancelledOnly, w.ServiceX, now.AddDays(-3), AppointmentStatus.Cancelled);
        var oneL2 = await ClientAsync(w); await ApptAsync(w, oneL2, w.ServiceX, now.AddDays(-3), locationId: w.Loc2);
        var twoSplit = await ClientAsync(w); await ApptAsync(w, twoSplit, w.ServiceX, now.AddDays(-3)); await ApptAsync(w, twoSplit, w.ServiceX, now.AddDays(-4), locationId: w.Loc2);
        await ClientAsync(w, deleted: true);

        Assert.Equal(new[] { one, oneL2 }.Order().ToArray(), (await AudienceAsync(s, now, "new")).Eligible.Order().ToArray());
        Assert.Equal([one], (await AudienceAsync(s, now, "new", loc: w.B.LocationId)).Eligible);
        var allNet = await AudienceAsync(s, now, "all_consented");
        Assert.Equal(new[] { one, two, none, cancelledOnly, oneL2, twoSplit }.Order().ToArray(), allNet.Eligible.Order().ToArray());
        var allL1 = await AudienceAsync(s, now, "all_consented", loc: w.B.LocationId);
        Assert.Equal(new[] { one, two, twoSplit }.Order().ToArray(), allL1.Eligible.Order().ToArray());
    }

    // ------------------------------------------------------------------ придатність (§26.5)

    [SkippableFact]
    public async Task eligibility_consent_levels_stop_telegram_and_frequency()
    {
        NeedDb();
        var w = await NewWorldAsync();
        var now = DateTimeOffset.UtcNow;
        await using var s = NewSvc(w.Id, new Clock(now));
        var promo = await PromotionAsync(w, services: [w.ServiceX]);
        var full = await ClientAsync(w);
        var noTg = await ClientAsync(w, telegram: false);
        var inactiveCh = await ClientAsync(w, channelActive: false);
        var stop = await ClientAsync(w, level: "none");
        var stopNoTg = await ClientAsync(w, level: "none", telegram: false); // за першою причиною: noTelegram
        var noRow = await ClientAsync(w, level: null);
        var selOverlap = await ClientAsync(w, level: "selective"); await ApptAsync(w, selOverlap, w.ServiceX, now.AddDays(-40));
        var selNoOverlap = await ClientAsync(w, level: "selective"); await ApptAsync(w, selNoOverlap, w.ServiceY, now.AddDays(-40));
        var selOld = await ClientAsync(w, level: "selective"); await ApptAsync(w, selOld, w.ServiceX, now.AddDays(-400)); // «улюблені» лише за 365 днів
        var recent = await ClientAsync(w);
        var recentOld = await ClientAsync(w);
        var winback = await ClientAsync(w);
        foreach (var c in new[] { full, noTg, inactiveCh, stop, stopNoTg, noRow, selOverlap, selNoOverlap, selOld, recent, recentOld, winback })
            await ApptAsync(w, c, w.ServiceY, now.AddDays(-2));
        await using (var ctx = fx.Db.CreateContext(w.Id))
        {
            var camp = new Campaign { Name = "Інша", Type = "news", SegmentKind = "all_consented", Template = "x", CreatedByUserId = w.Seed.OwnerId };
            ctx.Add(camp);
            await ctx.SaveChangesAsync();
            foreach (var (cid, when) in new[] { (recent, now.AddDays(-2)), (recentOld, now.AddDays(-9)) })
            {
                var conv = await ctx.Conversations.FirstAsync(c => c.ClientId == cid);
                var msg = new Message
                {
                    ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "x", SentAt = when,
                    Status = "sent", Kind = "text", IdempotencyKey = $"campaign:{camp.Id}:{cid}",
                };
                ctx.Add(msg);
                await ctx.SaveChangesAsync();
                ctx.Add(new CampaignRecipient { CampaignId = camp.Id, ClientId = cid, Status = "sent", MessageId = msg.Id, QueuedAt = when, SentAt = when });
            }
            var wconv = await ctx.Conversations.FirstAsync(c => c.ClientId == winback);
            ctx.Add(new Message
            {
                ConversationId = wconv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "w", SentAt = now.AddDays(-3),
                Status = "sent", Kind = "text", IdempotencyKey = $"winback:{winback}:2031-01-01",
            });
            await ctx.SaveChangesAsync();
        }

        var promoR = await AudienceAsync(s, now, "all_consented", "promo", promo);
        Assert.Equal(12, promoR.Matched);
        Assert.Equal(new[] { full, selOverlap, recentOld }.Order().ToArray(), promoR.Eligible.Order().ToArray());
        Assert.Equal(new AudienceExclusions(NoTelegram: 3, Unsubscribed: 1, NoConsent: 1, LevelMismatch: 2, Frequency: 2), promoR.Excluded);

        var news = await AudienceAsync(s, now, "all_consented", "news");
        Assert.Equal(new[] { full, recentOld }.Order().ToArray(), news.Eligible.Order().ToArray()); // selective не отримує news
        Assert.Equal(new AudienceExclusions(NoTelegram: 3, Unsubscribed: 1, NoConsent: 1, LevelMismatch: 3, Frequency: 2), news.Excluded);

        // акція без обмеження послуг перетинається з будь-якими «улюбленими» (навіть без візитів)
        var openPromo = await PromotionAsync(w);
        var open = await AudienceAsync(s, now, "all_consented", "promo", openPromo);
        Assert.Equal(new[] { full, selOverlap, selNoOverlap, selOld, recentOld }.Order().ToArray(), open.Eligible.Order().ToArray());
    }

    // ------------------------------------------------------------------ HTTP

    private sealed record Env(HttpClient Client, CapturingProvider Logs);

    private Env NewEnv(bool marketing = true, Dictionary<string, string>? extra = null)
    {
        var logs = new CapturingProvider();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (marketing)
            {
                b.UseSetting("Marketing:Enabled", "true");
                b.UseSetting("Marketing:LegalGateApproved", "true");
            }
            foreach (var (k, v) in extra ?? []) b.UseSetting(k, v);
            b.ConfigureTestServices(s => s.AddLogging(l => l.SetMinimumLevel(LogLevel.Information).AddProvider(logs)));
        });
        _disposables.Add(f);
        return new Env(f.CreateClient(), logs);
    }

    private static HttpRequestMessage Http(HttpMethod m, string url, string? token, object? body = null, string? key = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) r.Headers.Add("Idempotency-Key", key);
        return r;
    }

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> CodeOf(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;
    private static void NoStore(HttpResponseMessage r) => Assert.Contains("no-store", r.Headers.CacheControl?.ToString() ?? "");

    private async Task<Guid> CreateAsync(Env e, string token, CampaignRequest req)
    {
        var r = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/campaigns", token, req));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetGuid();
    }

    private static string Camp(Guid id, string tail = "") => $"/api/beauty/campaigns/{id}{tail}";

    // ---- ролі, IDOR, no-store, прапорці

    [SkippableFact]
    public async Task api_roles_idor_no_store_and_flags()
    {
        NeedDb();
        var e = NewEnv();
        var w = await NewWorldAsync();
        var other = await NewWorldAsync();
        var promo = await PromotionAsync(w);
        var id = await CreateAsync(e, w.Owner, Req(promo));
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Http(HttpMethod.Get, Camp(id), w.Admin))).StatusCode);

        // specialist: 403 forbidden_role на читання й запис (до прив'язки тіла), no-store
        foreach (var (m, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "/api/beauty/campaigns", null), (HttpMethod.Get, Camp(id), null), (HttpMethod.Post, "/api/beauty/campaigns", new { garbage = 1 }),
                     (HttpMethod.Post, Camp(id, "/preview"), null), (HttpMethod.Post, Camp(id, "/confirm"), new { }), (HttpMethod.Post, Camp(id, "/cancel"), null),
                     (HttpMethod.Get, Camp(id, "/stats"), null), (HttpMethod.Get, Camp(id, "/coupons/summary"), null),
                     (HttpMethod.Post, "/api/beauty/marketing/audience/preview", new { }),
                 })
        {
            var r = await e.Client.SendAsync(Http(m, url, w.Spec, body));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden_role", await CodeOf(r));
            NoStore(r);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.Client.SendAsync(Http(HttpMethod.Get, "/api/beauty/campaigns", null))).StatusCode);

        // чужий tenant: 404 без розкриття, no-store
        foreach (var (m, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, Camp(id), null), (HttpMethod.Get, Camp(id, "/preview"), null), (HttpMethod.Post, Camp(id, "/cancel"), null),
                     (HttpMethod.Get, Camp(id, "/stats"), null), (HttpMethod.Get, Camp(id, "/coupons/summary"), null),
                     (HttpMethod.Put, Camp(id), Req(promo)), (HttpMethod.Delete, Camp(id), null),
                 })
        {
            var r = await e.Client.SendAsync(Http(m, url, other.Owner, body));
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal("campaign_not_found", await CodeOf(r));
            NoStore(r);
        }
        var confirm = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), other.Owner, new { revision = 1 }, "key-12345678"));
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        var list = await J(await e.Client.SendAsync(Http(HttpMethod.Get, "/api/beauty/campaigns", other.Owner)));
        Assert.Empty(list.GetProperty("items").EnumerateArray());

        // модуль beauty_marketing не підключено
        var noModule = await NewWorldAsync(marketingModule: false);
        var m403 = await e.Client.SendAsync(Http(HttpMethod.Get, "/api/beauty/campaigns", noModule.Owner));
        Assert.Equal(HttpStatusCode.Forbidden, m403.StatusCode);
        Assert.Equal("module_disabled", await CodeOf(m403));

        // Marketing:Enabled=false -> marketing_disabled (також для адміна)
        var off = NewEnv(marketing: false);
        foreach (var tk in new[] { w.Owner, w.Admin })
        {
            var r = await off.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/campaigns", tk, Req(promo)));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("marketing_disabled", await CodeOf(r));
            NoStore(r);
        }
        // TASK-790 (M4): читання кампаній доступне й при Marketing:Enabled=false (було marketing_disabled)
        Assert.Equal(HttpStatusCode.OK, (await off.Client.SendAsync(Http(HttpMethod.Get, "/api/beauty/campaigns", w.Owner))).StatusCode);
        Assert.Equal("marketing_disabled", await CodeOf(await off.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/marketing/audience/preview", w.Owner, new { }))));
    }

    // ---- TASK-790 (рев'ю безпеки маркетингу)

    [SkippableFact]
    public async Task m4_cancel_and_reads_stay_available_when_marketing_is_disabled_but_writes_and_confirm_do_not()
    {
        NeedDb();
        var w = await NewWorldAsync();
        var on = NewEnv();
        var id = await CreateAsync(on, w.Owner, Req(await PromotionAsync(w)));
        var off = NewEnv(marketing: false);

        foreach (var url in new[] { "/api/beauty/campaigns", Camp(id), Camp(id, "/stats"), Camp(id, "/coupons/summary") })
            Assert.Equal(HttpStatusCode.OK, (await off.Client.SendAsync(Http(HttpMethod.Get, url, w.Owner))).StatusCode);
        foreach (var (m, url, body, key) in new (HttpMethod, string, object?, string?)[]
                 {
                     (HttpMethod.Post, Camp(id, "/confirm"), new { revision = 1 }, "key-12345678"), (HttpMethod.Put, Camp(id), Req(null), null),
                     (HttpMethod.Post, Camp(id, "/preview"), null, null), (HttpMethod.Delete, Camp(id), null, null),
                 })
        {
            var r = await off.Client.SendAsync(Http(m, url, w.Owner, body, key));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("marketing_disabled", await CodeOf(r));
        }
        var cancel = await off.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/cancel"), w.Owner));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("cancelled", (await J(cancel)).GetProperty("status").GetString());
        // ідемпотентно
        Assert.Equal(HttpStatusCode.OK, (await off.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/cancel"), w.Owner))).StatusCode);

        // без модуля beauty_marketing скасування недоступне так само, як решта API
        var noModule = await NewWorldAsync(marketingModule: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await off.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/cancel"), noModule.Owner))).StatusCode);
    }

    [SkippableFact]
    public async Task l7_coupon_check_hides_detailed_reasons_from_specialists_and_coupon_writes_are_rate_limited()
    {
        NeedDb();
        var w = await NewWorldAsync();
        await using (var ctx = fx.Db.CreateContext(w.Id))
        {
            ctx.Add(new ServicePrice { ServiceId = w.B.ServiceId, LocationId = null, Price = 500m });
            await ctx.SaveChangesAsync();
        }
        var e = NewEnv(extra: new() { ["Coupons:WritePerUserPerMinute"] = "2" });
        var probe = new { code = "NOSUCH77", locationId = w.B.LocationId, serviceId = w.B.ServiceId };

        var owner = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/coupons/check", w.Owner, probe));
        Assert.Equal("coupon_not_found", await CodeOf(owner)); // owner/admin бачать причину
        var spec = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/coupons/check", w.Spec, probe));
        Assert.Equal(HttpStatusCode.Conflict, spec.StatusCode);
        Assert.Equal("coupon_invalid", await CodeOf(spec)); // майстер - узагальнена відповідь
        var badFormat = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/coupons/check", w.Spec, new { code = "!", locationId = w.B.LocationId, serviceId = w.B.ServiceId }));
        Assert.Equal("coupon_invalid", await CodeOf(badFormat));

        var promo = await PromotionAsync(w, "code_only");
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        foreach (var code in new[] { "ZEBRA771", "ZEBRA772", "ZEBRA773" })
        {
            last = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/coupons", w.Owner,
                new { promotionId = promo, code, validFrom = DateTimeOffset.UtcNow.AddDays(-1), validTo = DateTimeOffset.UtcNow.AddDays(5) }));
            statuses.Add(last.StatusCode);
        }
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
        Assert.True(last!.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limited", await CodeOf(last));
    }

    [SkippableFact]
    public async Task startup_requires_legal_gate_outside_development()
    {
        NeedDb();
        var bad = fx.CreateFactory(100).WithWebHostBuilder(b => b.UseSetting("Marketing:Enabled", "true"));
        _disposables.Add(bad);
        Assert.ThrowsAny<Exception>(() => bad.CreateClient());
        var ok = fx.CreateFactory(100).WithWebHostBuilder(b =>
        {
            b.UseSetting("Marketing:Enabled", "true");
            b.UseSetting("Marketing:LegalGateApproved", "true");
        });
        _disposables.Add(ok);
        Assert.NotNull(ok.CreateClient());
    }

    [SkippableFact]
    public async Task draft_validation_returns_422_codes()
    {
        NeedDb();
        var e = NewEnv();
        var w = await NewWorldAsync();
        var pub = await PromotionAsync(w, services: [w.ServiceX], endsAt: DateTimeOffset.UtcNow.AddDays(10));
        var codeOnly = await PromotionAsync(w, "code_only", [w.ServiceX], DateTimeOffset.UtcNow.AddDays(10));
        var otherCodeOnly = await PromotionAsync(w, "code_only");
        var shared = await SharedCouponAsync(w, codeOnly, "SPRING1");
        var sharedOther = await SharedCouponAsync(w, otherCodeOnly, "OTHER1");
        var good = Req(pub);

        async Task Expect(CampaignRequest req, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
        {
            var r = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/campaigns", w.Owner, req));
            Assert.Equal(status, r.StatusCode);
            Assert.Equal(code, await CodeOf(r));
            NoStore(r);
        }

        await Expect(good with { Name = "  " }, "invalid_name");
        await Expect(good with { Name = new string('a', 201) }, "invalid_name");
        await Expect(good with { Type = "ads" }, "invalid_type");
        await Expect(good with { PromotionId = null }, "promotion_required");
        await Expect(good with { PromotionId = Guid.NewGuid() }, "promotion_not_found");
        await Expect(good with { PromotionId = codeOnly }, "promotion_must_be_public");
        await Expect(good with { CouponMode = "personal" }, "promotion_not_code_only");
        await Expect(good with { Segment = new SegmentDto("lapsed", Days: 0) }, "invalid_segment");
        await Expect(good with { Segment = new SegmentDto("bogus") }, "invalid_segment");
        await Expect(good with { Segment = null }, "invalid_segment");
        await Expect(good with { Segment = new SegmentDto("lapsed", ServiceId: Guid.NewGuid()) }, "service_not_found");
        await Expect(good with { LocationId = Guid.NewGuid() }, "location_not_found");
        await Expect(good with { Template = "" }, "invalid_template");
        await Expect(good with { Template = "Привіт {foo}" }, "invalid_template");
        await Expect(good with { Template = "Ваш код {code}" }, "invalid_template");
        await Expect(good with { Template = new string('я', 1001) }, "invalid_template");
        await Expect(good with { Template = "<b>x</b>" }, "invalid_template");
        await Expect(good with { Type = "news", PromotionId = null, Template = "Знижка {discount}" }, "invalid_template");
        await Expect(good with { Type = "news", CouponMode = "none" }, "invalid_type");
        await Expect(good with { CouponMode = "voucher" }, "invalid_coupon_mode");
        await Expect(good with { SharedCouponId = shared }, "invalid_coupon_mode");
        var coupon = good with { PromotionId = codeOnly, CouponMode = "shared" };
        await Expect(coupon with { SharedCouponId = null }, "coupon_not_found");
        await Expect(coupon with { SharedCouponId = Guid.NewGuid() }, "coupon_not_found");
        await Expect(coupon with { SharedCouponId = sharedOther }, "coupon_promotion_mismatch");
        var personal = good with { PromotionId = codeOnly, CouponMode = "personal" };
        await Expect(personal with { CouponValidTo = DateTimeOffset.UtcNow.AddDays(30) }, "coupon_period_outside_promotion");

        // успішні: shared, personal (строк за замовчуванням = кінець акції), news
        Assert.NotEqual(Guid.Empty, await CreateAsync(e, w.Owner, coupon with { SharedCouponId = shared }));
        var p = await J(await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/campaigns", w.Owner, personal)));
        Assert.True(p.GetProperty("couponValidTo").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(9));
        Assert.Equal("draft", p.GetProperty("status").GetString());
        Assert.Equal(1, p.GetProperty("revision").GetInt32());
        Assert.Equal(60, (await J(await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/campaigns", w.Owner, good with { Segment = new SegmentDto("lapsed") })))).GetProperty("segment").GetProperty("days").GetInt32());
        Assert.NotEqual(Guid.Empty, await CreateAsync(e, w.Owner, good with { Type = "news", PromotionId = null, Template = "Новини, {name}" }));
    }

    private async Task<Guid> SharedCouponAsync(World w, Guid promotionId, string code, bool revoked = false, DateTimeOffset? validTo = null)
    {
        await using var ctx = fx.Db.CreateContext(w.Id);
        var c = new Coupon
        {
            Kind = "shared", PromotionId = promotionId, Code = code, CodeHash = CouponCodes.Hash(code), CodeHint = CouponCodes.Hint(code),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = validTo ?? DateTimeOffset.UtcNow.AddDays(20), Status = revoked ? "revoked" : "active",
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Guid> PersonalCouponAsync(World w, Guid campaignId, Guid clientId, Guid promotionId, string status = "active", int uses = 0)
    {
        await using var ctx = fx.Db.CreateContext(w.Id);
        var code = CouponCodes.GeneratePersonal();
        var c = new Coupon
        {
            Kind = "personal", PromotionId = promotionId, CampaignId = campaignId, ClientId = clientId, CodeHash = CouponCodes.Hash(code), CodeHint = CouponCodes.Hint(code),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(20), MaxUses = 1, UsesCount = uses, Status = status,
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    // ---- перегляд без ПД

    [SkippableFact]
    public async Task previews_return_only_counts_and_no_personal_data()
    {
        NeedDb();
        var e = NewEnv();
        var w = await NewWorldAsync();
        var promo = await PromotionAsync(w, services: [w.ServiceX]);
        var named = await ClientAsync(w, ClientName);
        await using (var ctx = fx.Db.CreateContext(w.Id))
            await ctx.Clients.Where(c => c.Id == named).ExecuteUpdateAsync(s => s.SetProperty(c => c.Phone, "+380671112233").SetProperty(c => c.Email, "oksana@private.test"));
        await ClientAsync(w);
        await ClientAsync(w, level: "none");
        await ClientAsync(w, telegram: false);
        await ClientAsync(w, level: null);

        var r = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/marketing/audience/preview", w.Admin,
            new { type = "promo", promotionId = promo, segment = new { kind = "all_consented" } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        NoStore(r);
        var raw = await r.Content.ReadAsStringAsync();
        var j = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(["excluded", "matched", "recipients"], j.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["frequency", "levelMismatch", "noConsent", "noTelegram", "unsubscribed"], j.GetProperty("excluded").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal((5, 2), (j.GetProperty("matched").GetInt32(), j.GetProperty("recipients").GetInt32()));
        Assert.Equal(1, j.GetProperty("excluded").GetProperty("unsubscribed").GetInt32());
        Assert.Equal(1, j.GetProperty("excluded").GetProperty("noTelegram").GetInt32());
        Assert.Equal(1, j.GetProperty("excluded").GetProperty("noConsent").GetInt32());
        foreach (var leak in new[] { ClientName, "+380671112233", "oksana@private.test", named.ToString() }) Assert.DoesNotContain(leak, raw);

        var id = await CreateAsync(e, w.Owner, Req(promo));
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            var pr = await e.Client.SendAsync(Http(method, Camp(id, "/preview"), w.Owner));
            Assert.Equal(HttpStatusCode.OK, pr.StatusCode);
            NoStore(pr);
            var praw = await pr.Content.ReadAsStringAsync();
            var p = JsonDocument.Parse(praw).RootElement;
            Assert.Equal(1, p.GetProperty("revision").GetInt32());
            Assert.Equal((5, 2), (p.GetProperty("matched").GetInt32(), p.GetProperty("recipients").GetInt32()));
            Assert.Contains("Олена", p.GetProperty("sampleText").GetString()); // приклад, не реальне ім'я
            Assert.Contains("Умови: знижка 15%", p.GetProperty("sampleText").GetString());
            Assert.Equal("09:00", p.GetProperty("window").GetProperty("from").GetString());
            Assert.Equal(200, p.GetProperty("limits").GetProperty("dailyLimit").GetInt32());
            Assert.Equal(7, p.GetProperty("limits").GetProperty("clientIntervalDays").GetInt32());
            Assert.Equal(1, p.GetProperty("limits").GetProperty("estimatedDays").GetInt32());
            Assert.Contains("no_buttons_channel", p.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()));
            foreach (var leak in new[] { ClientName, "+380671112233", "oksana@private.test", named.ToString() }) Assert.DoesNotContain(leak, praw);
        }
        Assert.Equal("promo", (await J(await e.Client.SendAsync(Http(HttpMethod.Get, Camp(id), w.Owner)))).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/marketing/audience/preview", w.Owner,
            new { type = "promo", segment = new { kind = "lapsed", days = 0 } }))).StatusCode);
    }

    [SkippableFact]
    public async Task write_and_preview_rate_limits_return_429_with_retry_after()
    {
        NeedDb();
        var e = NewEnv(extra: new() { ["Marketing:WritePerUserPerMinute"] = "2", ["Marketing:PreviewPerUserPerMinute"] = "1" });
        var w = await NewWorldAsync();
        var promo = await PromotionAsync(w);
        await CreateAsync(e, w.Owner, Req(promo));
        await CreateAsync(e, w.Owner, Req(promo));
        var third = await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/campaigns", w.Owner, Req(promo)));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("rate_limited", await CodeOf(third));
        Assert.True(third.Headers.Contains("Retry-After"));
        var body = new { type = "news", segment = new { kind = "all_consented" } };
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/marketing/audience/preview", w.Admin, body))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await e.Client.SendAsync(Http(HttpMethod.Post, "/api/beauty/marketing/audience/preview", w.Admin, body))).StatusCode);
    }

    // ---- підтвердження

    private async Task<(Env E, World W, Guid Promo, Guid[] Clients)> ReadyAsync(int eligible = 3, Dictionary<string, string>? extra = null, string access = "public")
    {
        var e = NewEnv(extra: extra);
        var w = await NewWorldAsync();
        var promo = await PromotionAsync(w, access, [w.ServiceX], DateTimeOffset.UtcNow.AddDays(20));
        var clients = new List<Guid>();
        for (var i = 0; i < eligible; i++) clients.Add(await ClientAsync(w, i == 0 ? ClientName : null));
        await ClientAsync(w, level: "none");
        return (e, w, promo, clients.ToArray());
    }

    private async Task<int> RevisionAsync(Env e, World w, Guid id) =>
        (await J(await e.Client.SendAsync(Http(HttpMethod.Get, Camp(id, "/preview"), w.Owner)))).GetProperty("revision").GetInt32();

    private async Task<List<CampaignRecipient>> RecipientsAsync(World w, Guid id)
    {
        await using var ctx = fx.Db.CreateContext(w.Id);
        return await ctx.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id).ToListAsync();
    }

    [SkippableFact]
    public async Task confirm_is_one_atomic_idempotent_action_with_revision()
    {
        NeedDb();
        var (e, w, promo, clients) = await ReadyAsync();
        var id = await CreateAsync(e, w.Owner, Req(promo));
        var rev = await RevisionAsync(e, w, id);

        // заголовок Idempotency-Key обов'язковий
        var noKey = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { revision = rev }));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("idempotency_key_required", await CodeOf(noKey));
        NoStore(noKey);
        Assert.Equal("idempotency_key_required", await CodeOf(await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { revision = rev }, "short"))));
        Assert.Equal("revision_required", await CodeOf(await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { }, "key-aaaa-0001"))));
        Assert.Empty(await RecipientsAsync(w, id));

        // зміна після перегляду -> revision_changed, нічого не зафіксовано
        var put = await e.Client.SendAsync(Http(HttpMethod.Put, Camp(id), w.Owner, Req(promo, name: "Весна 2")));
        Assert.Equal(2, (await J(put)).GetProperty("revision").GetInt32());
        var stale = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { revision = rev }, "key-aaaa-0002"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("revision_changed", await CodeOf(stale));
        Assert.Empty(await RecipientsAsync(w, id));
        Assert.Equal("draft", (await J(await e.Client.SendAsync(Http(HttpMethod.Get, Camp(id), w.Owner)))).GetProperty("status").GetString());

        rev = await RevisionAsync(e, w, id);
        Assert.Equal(2, rev);
        var ok = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Admin, new { revision = rev }, "key-aaaa-0003"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        NoStore(ok);
        var dto = await J(ok);
        Assert.Equal("scheduled", dto.GetProperty("status").GetString());
        Assert.Equal(clients.Length, dto.GetProperty("recipientsTotal").GetInt32());
        Assert.NotNull(dto.GetProperty("confirmedAt").GetString());
        Assert.True(Math.Abs((dto.GetProperty("scheduledAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalMinutes) < 2);

        var rec = await RecipientsAsync(w, id);
        Assert.Equal(clients.Order().ToArray(), rec.Select(r => r.ClientId).Order().ToArray());
        Assert.All(rec, r => { Assert.Equal("pending", r.Status); Assert.Null(r.CouponId); Assert.Null(r.MessageId); Assert.Null(r.SkipReason); });

        // повтор того ж ключа й тіла -> 200 з тим самим станом, одержувачі не дублюються
        var replay = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Admin, new { revision = rev }, "key-aaaa-0003"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(dto.GetProperty("confirmedAt").GetString(), (await J(replay)).GetProperty("confirmedAt").GetString());
        // той самий ключ, інше тіло
        var conflict = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Admin, new { revision = rev + 1 }, "key-aaaa-0003"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", await CodeOf(conflict));
        // інший ключ -> кампанія вже не чернетка
        var other = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { revision = rev }, "key-aaaa-0004"));
        Assert.Equal("campaign_not_draft", await CodeOf(other));
        Assert.Equal(clients.Length, (await RecipientsAsync(w, id)).Count);

        // після підтвердження чернетку не змінити й не видалити
        Assert.Equal("campaign_not_draft", await CodeOf(await e.Client.SendAsync(Http(HttpMethod.Put, Camp(id), w.Owner, Req(promo)))));
        Assert.Equal("campaign_not_draft", await CodeOf(await e.Client.SendAsync(Http(HttpMethod.Delete, Camp(id), w.Owner))));
        Assert.Equal("campaign_not_draft", await CodeOf(await e.Client.SendAsync(Http(HttpMethod.Get, Camp(id, "/preview"), w.Owner))));

        // акція вже в scheduled-кампанії -> друга кампанія з нею не підтверджується
        var second = await CreateAsync(e, w.Owner, Req(promo, name: "Дубль"));
        var dup = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(second, "/confirm"), w.Owner, new { revision = 1 }, "key-aaaa-0005"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("promotion_in_campaign", await CodeOf(dup));
        Assert.Empty(await RecipientsAsync(w, second));

        // список і фільтр за статусом
        var list = await J(await e.Client.SendAsync(Http(HttpMethod.Get, "/api/beauty/campaigns?status=scheduled", w.Owner)));
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(promo, item.GetProperty("promotion").GetProperty("id").GetGuid());
        Assert.Equal(0, item.GetProperty("sent").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.Client.SendAsync(Http(HttpMethod.Get, "/api/beauty/campaigns?status=weird", w.Owner))).StatusCode);

        // у логах немає текстів, імен і ПД
        var logs = string.Join('\n', e.Logs.Lines);
        Assert.Contains("campaign_confirmed", logs);
        Assert.Contains($"recipients={clients.Length}", logs);
        foreach (var leak in new[] { Secret, ClientName, "Весна" }) Assert.DoesNotContain(leak, logs);
    }

    [SkippableFact]
    public async Task two_parallel_confirms_have_exactly_one_winner()
    {
        NeedDb();
        var (e, w, promo, clients) = await ReadyAsync(eligible: 6);
        for (var round = 0; round < 3; round++)
        {
            var p = round == 0 ? promo : await PromotionAsync(w, "public", [w.ServiceX], DateTimeOffset.UtcNow.AddDays(20));
            var id = await CreateAsync(e, w.Owner, Req(p, name: "Гонка " + round));
            // різні ключі: один переможець, другий бачить, що кампанія вже не чернетка
            var rs = await Task.WhenAll(
                e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { revision = 1 }, $"race-a-{round}-aaaa")),
                e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Admin, new { revision = 1 }, $"race-b-{round}-bbbb")));
            Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
            var loser = rs.Single(r => r.StatusCode != HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Contains(await CodeOf(loser), new[] { "campaign_not_draft", "promotion_in_campaign" });
            var rec = await RecipientsAsync(w, id);
            Assert.Equal(clients.Length, rec.Count);
            Assert.Equal(rec.Count, rec.Select(r => r.ClientId).Distinct().Count());
            // той самий ключ паралельно: обидва 200
            var id2 = await CreateAsync(e, w.Owner, Req(await PromotionAsync(w, "public", [w.ServiceX], DateTimeOffset.UtcNow.AddDays(20)), name: "Один ключ " + round));
            var same = await Task.WhenAll(
                e.Client.SendAsync(Http(HttpMethod.Post, Camp(id2, "/confirm"), w.Owner, new { revision = 1 }, $"race-same-{round}-cc")),
                e.Client.SendAsync(Http(HttpMethod.Post, Camp(id2, "/confirm"), w.Owner, new { revision = 1 }, $"race-same-{round}-cc")));
            Assert.All(same, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Equal(clients.Length, (await RecipientsAsync(w, id2)).Count);
        }
    }

    [SkippableFact]
    public async Task confirm_validations_schedule_audience_promotion_coupon_and_limits()
    {
        NeedDb();
        var (e, w, promo, clients) = await ReadyAsync(eligible: 3, extra: new() { ["Marketing:MaxRecipientsPerCampaign"] = "2" });
        string K() => "k-" + Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Confirm(Guid id, object body) => await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, body, K()));

        var tooLarge = await CreateAsync(e, w.Owner, Req(promo));
        var big = await Confirm(tooLarge, new { revision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, big.StatusCode);
        Assert.Equal("audience_too_large", await CodeOf(big));
        Assert.Empty(await RecipientsAsync(w, tooLarge));
        Assert.Equal("draft", (await J(await e.Client.SendAsync(Http(HttpMethod.Get, Camp(tooLarge), w.Owner)))).GetProperty("status").GetString());

        var (e2, w2, promo2, _) = await ReadyAsync(eligible: 2);
        string K2() => "k-" + Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Confirm2(Guid id, object body) => await e2.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w2.Owner, body, K2()));

        var c = await CreateAsync(e2, w2.Owner, Req(promo2));
        var past = await Confirm2(c, new { revision = 1, scheduledAt = DateTimeOffset.UtcNow.AddHours(-1) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        Assert.Equal("invalid_schedule", await CodeOf(past));
        Assert.Equal("invalid_schedule", await CodeOf(await Confirm2(c, new { revision = 1, scheduledAt = DateTimeOffset.UtcNow.AddDays(31) })));
        Assert.Empty(await RecipientsAsync(w2, c));
        var later = DateTimeOffset.UtcNow.AddDays(2);
        var ok = await Confirm2(c, new { revision = 1, scheduledAt = later });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(Math.Abs(((await J(ok)).GetProperty("scheduledAt").GetDateTimeOffset() - later).TotalSeconds) < 2);

        // порожня аудиторія
        var emptyPromo = await PromotionAsync(w2, "public", [w2.ServiceX], DateTimeOffset.UtcNow.AddDays(20));
        var empty = await CreateAsync(e2, w2.Owner, Req(emptyPromo, kind: "lapsed", days: 60));
        var noAudience = await Confirm2(empty, new { revision = 1 });
        Assert.Equal("audience_empty", await CodeOf(noAudience));
        Assert.Equal(HttpStatusCode.Conflict, noAudience.StatusCode);
        Assert.Empty(await RecipientsAsync(w2, empty));

        // акція неактивна або закінчується до старту
        var dead = await PromotionAsync(w2, "public", [w2.ServiceX], DateTimeOffset.UtcNow.AddDays(20));
        var deadCamp = await CreateAsync(e2, w2.Owner, Req(dead));
        await using (var ctx = fx.Db.CreateContext(w2.Id))
            await ctx.Promotions.Where(p => p.Id == dead).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
        Assert.Equal("promotion_inactive", await CodeOf(await Confirm2(deadCamp, new { revision = 1 })));
        var soon = await PromotionAsync(w2, "public", [w2.ServiceX], DateTimeOffset.UtcNow.AddDays(1));
        var soonCamp = await CreateAsync(e2, w2.Owner, Req(soon));
        Assert.Equal("promotion_inactive", await CodeOf(await Confirm2(soonCamp, new { revision = 1, scheduledAt = DateTimeOffset.UtcNow.AddDays(2) })));

        // купон: загальний відкликано -> coupon_unavailable; персональний - без випуску при підтвердженні
        var codePromo = await PromotionAsync(w2, "code_only", [w2.ServiceX], DateTimeOffset.UtcNow.AddDays(20));
        var revoked = await SharedCouponAsync(w2, codePromo, "REVOKED1", revoked: true);
        var sharedCamp = await CreateAsync(e2, w2.Owner, Req(codePromo, mode: "shared", sharedCoupon: revoked));
        Assert.Equal("coupon_unavailable", await CodeOf(await Confirm2(sharedCamp, new { revision = 1 })));
        var persPromo = await PromotionAsync(w2, "code_only", [w2.ServiceX], DateTimeOffset.UtcNow.AddDays(20));
        var persCamp = await CreateAsync(e2, w2.Owner, Req(persPromo, mode: "personal"));
        var persOk = await Confirm2(persCamp, new { revision = 1 });
        Assert.Equal(HttpStatusCode.OK, persOk.StatusCode);
        Assert.All(await RecipientsAsync(w2, persCamp), r => Assert.Null(r.CouponId)); // персональні коди генерує воркер при постановці в чергу
        var summary = await J(await e2.Client.SendAsync(Http(HttpMethod.Get, Camp(persCamp, "/coupons/summary"), w2.Owner)));
        Assert.Equal([0, 0, 0, 0], new[] { "issued", "redeemedActive", "released", "revoked" }.Select(k => summary.GetProperty(k).GetInt32()).ToArray());
        _ = (promo, clients);
    }

    // ---- скасування

    [SkippableFact]
    public async Task cancel_works_before_and_during_sending_and_is_idempotent()
    {
        NeedDb();
        var (e, w, promo, clients) = await ReadyAsync(eligible: 4);
        var draft = await CreateAsync(e, w.Owner, Req(promo, name: "Чернетка"));
        var cd = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(draft, "/cancel"), w.Admin));
        Assert.Equal(HttpStatusCode.OK, cd.StatusCode);
        var cdj = await J(cd);
        Assert.Equal("cancelled", cdj.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, cdj.GetProperty("cancelledBy").ValueKind);

        var id = await CreateAsync(e, w.Owner, Req(await PromotionAsync(w, "public", [w.ServiceX], DateTimeOffset.UtcNow.AddDays(20)), name: "Відправка"));
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { revision = 1 }, "cancel-key-0001"))).StatusCode);

        // воркер уже в роботі: один sent, один queued, один failed, решта pending
        var rec = await RecipientsAsync(w, id);
        await using (var ctx = fx.Db.CreateContext(w.Id))
        {
            Guid MsgFor(Guid client, string status)
            {
                var conv = ctx.Conversations.First(c => c.ClientId == client);
                var m = new Message
                {
                    ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "x", SentAt = DateTimeOffset.UtcNow,
                    Status = status, Kind = "text", IdempotencyKey = $"campaign:{id}:{client}",
                };
                ctx.Add(m);
                ctx.SaveChanges();
                return m.Id;
            }
            var done = rec[0]; var queued = rec[1]; var failed = rec[2];
            var mDone = MsgFor(done.ClientId, "sent"); var mQueued = MsgFor(queued.ClientId, "pending"); var mFailed = MsgFor(failed.ClientId, "failed");
            var sentAt = DateTimeOffset.UtcNow;
            await ctx.CampaignRecipients.Where(r => r.Id == done.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "sent").SetProperty(r => r.MessageId, (Guid?)mDone).SetProperty(r => r.SentAt, (DateTimeOffset?)sentAt));
            await ctx.CampaignRecipients.Where(r => r.Id == queued.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "queued").SetProperty(r => r.MessageId, (Guid?)mQueued).SetProperty(r => r.QueuedAt, (DateTimeOffset?)sentAt));
            await ctx.CampaignRecipients.Where(r => r.Id == failed.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "failed").SetProperty(r => r.SkipReason, "bot_blocked").SetProperty(r => r.MessageId, (Guid?)mFailed));
            await ctx.Campaigns.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "sending").SetProperty(c => c.StartedAt, DateTimeOffset.UtcNow));
        }

        var c1 = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/cancel"), w.Owner));
        Assert.Equal(HttpStatusCode.OK, c1.StatusCode);
        NoStore(c1);
        Assert.Equal("cancelled", (await J(c1)).GetProperty("status").GetString());
        var after = (await RecipientsAsync(w, id)).ToDictionary(r => r.Id);
        Assert.Equal("sent", after[rec[0].Id].Status);
        Assert.Equal("queued", after[rec[1].Id].Status); // queued відсікає воркер (guard перед відправкою)
        Assert.Equal("failed", after[rec[2].Id].Status);
        var pending = after[rec[3].Id];
        Assert.Equal(("skipped", "campaign_cancelled"), (pending.Status, pending.SkipReason));

        // повтор -> 200; підтвердити скасовану не можна; завершену скасувати не можна
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/cancel"), w.Owner))).StatusCode);
        Assert.Equal("campaign_not_draft", await CodeOf(await e.Client.SendAsync(Http(HttpMethod.Post, Camp(id, "/confirm"), w.Owner, new { revision = 1 }, "cancel-key-0002"))));
        var finished = await CreateAsync(e, w.Owner, Req(await PromotionAsync(w, "public", [w.ServiceX], DateTimeOffset.UtcNow.AddDays(20)), name: "Завершена"));
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Http(HttpMethod.Post, Camp(finished, "/confirm"), w.Owner, new { revision = 1 }, "cancel-key-0003"))).StatusCode);
        await using (var ctx = fx.Db.CreateContext(w.Id))
            await ctx.Campaigns.Where(c => c.Id == finished).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "done").SetProperty(c => c.FinishedAt, DateTimeOffset.UtcNow));
        var fin = await e.Client.SendAsync(Http(HttpMethod.Post, Camp(finished, "/cancel"), w.Owner));
        Assert.Equal(HttpStatusCode.Conflict, fin.StatusCode);
        Assert.Equal("campaign_finished", await CodeOf(fin));

        var logs = string.Join('\n', e.Logs.Lines);
        Assert.Contains("campaign_cancelled", logs);
        foreach (var leak in new[] { Secret, ClientName }) Assert.DoesNotContain(leak, logs);
        _ = clients;
    }

    // ---- статистика

    [SkippableFact]
    public async Task stats_formulas_revenue_by_campaign_coupons_and_unsubscribed_after()
    {
        NeedDb();
        var e = NewEnv();
        var w = await NewWorldAsync();
        var promo = await PromotionAsync(w, "code_only", [w.ServiceX], DateTimeOffset.UtcNow.AddDays(20));
        var cid = await CreateAsync(e, w.Owner, Req(promo, mode: "personal"));
        var otherCid = await CreateAsync(e, w.Owner, Req(promo, mode: "personal", name: "Інша"));
        var now = DateTimeOffset.UtcNow;

        var cl = new List<Guid>();
        for (var i = 0; i < 11; i++) cl.Add(await ClientAsync(w));
        await using (var ctx = fx.Db.CreateContext(w.Id))
        {
            var statuses = new (string Status, string? Reason)[]
            {
                ("sent", null), ("sent", null), ("sent", null), ("failed", "bot_blocked"), ("failed", "chat_not_found"), ("failed", "send_failed"),
                ("skipped", "frequency"), ("skipped", "frequency"), ("skipped", "campaign_cancelled"), ("queued", null), ("pending", null),
            };
            for (var i = 0; i < statuses.Length; i++)
            {
                var (st, reason) = statuses[i];
                Guid? msgId = null;
                if (st is "sent" or "failed" or "queued")
                {
                    var conv = await ctx.Conversations.FirstAsync(c => c.ClientId == cl[i]);
                    var m = new Message
                    {
                        ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "x", SentAt = now,
                        Status = st == "failed" ? "failed" : "sent", Kind = "text", IdempotencyKey = $"campaign:{cid}:{cl[i]}",
                    };
                    ctx.Add(m);
                    await ctx.SaveChangesAsync();
                    msgId = m.Id;
                }
                ctx.Add(new CampaignRecipient
                {
                    CampaignId = cid, ClientId = cl[i], Status = st, SkipReason = reason, MessageId = msgId,
                    QueuedAt = st is "sent" or "queued" or "failed" ? now.AddDays(-3) : null,
                    SentAt = st == "sent" ? now.AddDays(i == 2 ? -20 : -3) : null,
                    ClickedAt = i == 0 ? now.AddDays(-2) : null,
                });
            }
            await ctx.SaveChangesAsync();
        }
        var cp1 = await PersonalCouponAsync(w, cid, cl[0], promo, uses: 1);
        var cp3 = await PersonalCouponAsync(w, cid, cl[1], promo, uses: 1);
        var cp5 = await PersonalCouponAsync(w, cid, cl[2], promo, uses: 0);
        await PersonalCouponAsync(w, cid, cl[3], promo, status: "revoked");
        var foreign = await PersonalCouponAsync(w, otherCid, cl[4], promo, uses: 1);

        var a1 = await ApptAsync(w, cl[0], w.ServiceX, now.AddDays(-1), AppointmentStatus.Completed, price: 800m, campaignId: cid, couponId: cp1);
        await ApptAsync(w, cl[0], w.ServiceX, now.AddDays(3), AppointmentStatus.Confirmed, price: 500m, campaignId: cid);
        var a3 = await ApptAsync(w, cl[1], w.ServiceX, now.AddDays(-2), AppointmentStatus.Completed, price: 700m, couponId: cp3); // лише купон кампанії
        await ApptAsync(w, cl[2], w.ServiceX, now.AddDays(4), AppointmentStatus.Cancelled, price: 300m, campaignId: cid); // збій оплати (без автора скасування)
        var a5 = await ApptAsync(w, cl[2], w.ServiceX, now.AddDays(5), AppointmentStatus.Cancelled, price: 400m, campaignId: cid, couponId: cp5, cancelledByType: "client");
        var aForeign = await ApptAsync(w, cl[4], w.ServiceX, now.AddDays(-1), AppointmentStatus.Completed, price: 999m, campaignId: otherCid, couponId: foreign);
        await using (var ctx = fx.Db.CreateContext(w.Id))
        {
            ctx.AddRange(
                new CouponRedemption { CouponId = cp1, AppointmentId = a1, ClientId = cl[0], DiscountAmount = 100m, Status = "active" },
                new CouponRedemption { CouponId = cp3, AppointmentId = a3, ClientId = cl[1], DiscountAmount = 50m, Status = "active" },
                new CouponRedemption { CouponId = cp5, AppointmentId = a5, ClientId = cl[2], DiscountAmount = 10m, Status = "released", ReleaseReason = "cancelled", ReleasedAt = now },
                new CouponRedemption { CouponId = foreign, AppointmentId = aForeign, ClientId = cl[4], DiscountAmount = 1m, Status = "active" });
            // відписка після розсилки: cl[0] (sent -3д, подія -2д -> у вікні 7 днів), cl[2] (sent -20д, подія -1д -> поза вікном),
            // cl[9] (подія з campaign_id), cl[1] двічі не рахується двічі
            ctx.AddRange(
                Event(cl[0], now.AddDays(-2), null), Event(cl[2], now.AddDays(-1), null), Event(cl[9], now.AddDays(-1), cid), Event(cl[1], now.AddDays(-2), null),
                Event(cl[1], now.AddDays(-1), cid));
            await ctx.SaveChangesAsync();
        }

        var r = await e.Client.SendAsync(Http(HttpMethod.Get, Camp(cid, "/stats"), w.Admin));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        NoStore(r);
        var raw = await r.Content.ReadAsStringAsync();
        var s = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(11, s.GetProperty("recipients").GetInt32());
        var by = s.GetProperty("byStatus");
        Assert.Equal((1, 1, 3, 3, 3), (by.GetProperty("pending").GetInt32(), by.GetProperty("queued").GetInt32(), by.GetProperty("sent").GetInt32(), by.GetProperty("failed").GetInt32(), by.GetProperty("skipped").GetInt32()));
        var f = s.GetProperty("failed");
        Assert.Equal((1, 1, 1), (f.GetProperty("botBlocked").GetInt32(), f.GetProperty("chatNotFound").GetInt32(), f.GetProperty("other").GetInt32()));
        Assert.Equal(2, s.GetProperty("skipped").GetProperty("frequency").GetInt32());
        Assert.Equal(1, s.GetProperty("skipped").GetProperty("campaign_cancelled").GetInt32());
        Assert.Equal(1, s.GetProperty("clicks").GetInt32());
        Assert.Equal(4, s.GetProperty("bookings").GetInt32());              // a1, майбутній, a3, a5; збій оплати не рахується
        Assert.Equal(3, s.GetProperty("bookingsWithCoupon").GetInt32());    // a1, a3, a5
        Assert.Equal(2, s.GetProperty("couponRedemptionsActive").GetInt32());
        Assert.Equal(1500m, s.GetProperty("revenue").GetDecimal());         // 800 + 700; чужа кампанія не рахується
        Assert.Equal(3, s.GetProperty("unsubscribedAfter").GetInt32());     // cl[0] (вікно), cl[9] і cl[1] (campaign_id / вікно); cl[2] поза вікном
        Assert.Equal(JsonValueKind.Null, s.GetProperty("viewed").ValueKind); // Telegram не дає «переглянуто»
        Assert.DoesNotContain("recipientId", raw);
        Assert.DoesNotContain(cl[0].ToString(), raw);

        var sum = await J(await e.Client.SendAsync(Http(HttpMethod.Get, Camp(cid, "/coupons/summary"), w.Owner)));
        Assert.Equal((4, 2, 1, 1), (sum.GetProperty("issued").GetInt32(), sum.GetProperty("redeemedActive").GetInt32(), sum.GetProperty("released").GetInt32(), sum.GetProperty("revoked").GetInt32()));
        Assert.Equal(["issued", "redeemedActive", "released", "revoked"], sum.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    private static MarketingConsentEvent Event(Guid client, DateTimeOffset at, Guid? campaign) => new()
    {
        Id = Guid.NewGuid(), ClientId = client, Channel = "telegram", LevelFrom = "full", LevelTo = "none", Source = "chat", Via = "button", CampaignId = campaign, CreatedAt = at,
    };

    // ---- виправлення TASK-777: персонал не створює згоду

    [SkippableFact]
    public async Task staff_appointment_never_creates_client_with_marketing_consent()
    {
        NeedDb();
        var h = new BeautyCrm.Tests.Regression.RegressionHarness(fx);
        var salon = await h.CreateSalonAsync();
        var at = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(10), TimeSpan.Zero);
        var r = await fx.Send(HttpMethod.Post, "/api/beauty/appointments", new
        {
            locationId = salon.LocationId, specialistId = salon.SpecialistId, serviceId = salon.ServiceId, startsAt = at,
            client = new { name = "Консент Тест", phone = "+380501119999", marketingConsent = true }, reminder = "none", paymentMethod = "cash",
        }, salon.OwnerToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        await using var ctx = fx.Db.CreateContext(salon.Tenant.TenantId);
        var client = await ctx.Clients.AsNoTracking().SingleAsync(c => c.FullName == "Консент Тест");
        Assert.False(client.MarketingConsent);
        Assert.Empty(await ctx.MarketingConsents.AsNoTracking().Where(m => m.ClientId == client.Id).ToListAsync());
        Assert.Empty(await ctx.MarketingConsentEvents.AsNoTracking().Where(m => m.ClientId == client.Id).ToListAsync());
    }
}
