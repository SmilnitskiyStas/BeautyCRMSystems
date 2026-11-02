using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCatalog;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.AI;
using BeautyCrm.Tests.Beauty;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Coupons;

/// <summary>Чисті розрахунки й коди (без БД): §26.6 QuoteWithCoupon, нормалізація / хеш / генерація.</summary>
public class CouponPricingUnitTests
{
    private static readonly DateTimeOffset At = new(2030, 3, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Loc = Guid.NewGuid(), Svc = Guid.NewGuid();

    private static PromotionRule Rule(string type, decimal value, DateTimeOffset? to = null, IReadOnlyCollection<Guid>? services = null) =>
        new(Guid.NewGuid(), type + value, type, value, null, to, true, [], services ?? []);

    private static CouponCandidate Cand(PromotionRule r) => new(Guid.NewGuid(), "shared", "ABC", r.Name, null, r);

    [Fact]
    public void percent_coupon_beats_smaller_public_promotion()
    {
        var q = PromotionPricing.QuoteWithCoupon(1000m, Loc, Svc, At, [Rule("percent", 20)], Cand(Rule("percent", 25)));
        Assert.Equal(CouponOutcomes.Applied, q.Outcome);
        Assert.Equal(750m, q.Quote.Final);
        Assert.NotNull(q.CouponId);
    }

    [Fact]
    public void coupon_not_better_when_public_is_larger_or_equal_and_is_not_consumed()
    {
        var larger = PromotionPricing.QuoteWithCoupon(1000m, Loc, Svc, At, [Rule("percent", 20)], Cand(Rule("percent", 15)));
        Assert.Equal(CouponOutcomes.NotBetter, larger.Outcome);
        Assert.Equal(800m, larger.Quote.Final);
        var equal = PromotionPricing.QuoteWithCoupon(1000m, Loc, Svc, At, [Rule("percent", 20)], Cand(Rule("fixed", 200)));
        Assert.Equal(CouponOutcomes.NotBetter, equal.Outcome); // рівні знижки -> публічна
        Assert.Equal(800m, equal.Quote.Final);
    }

    [Fact]
    public void fixed_coupon_is_clamped_and_rounding_is_away_from_zero()
    {
        Assert.Equal(0m, PromotionPricing.QuoteWithCoupon(100m, Loc, Svc, At, [], Cand(Rule("fixed", 500))).Quote.Final);
        var q = PromotionPricing.QuoteWithCoupon(10.05m, Loc, Svc, At, [], Cand(Rule("percent", 50)));
        Assert.Equal(5.03m, q.Quote.Final); // 10.05 - 5.025 = 5.025 -> 5.03
    }

    [Fact]
    public void coupon_outside_scope_or_period_is_not_applicable()
    {
        var other = PromotionPricing.QuoteWithCoupon(1000m, Loc, Svc, At, [], Cand(Rule("percent", 30, services: [Guid.NewGuid()])));
        Assert.Equal(CouponOutcomes.NotApplicable, other.Outcome);
        Assert.Equal(1000m, other.Quote.Final);
        var expired = PromotionPricing.QuoteWithCoupon(1000m, Loc, Svc, At, [], Cand(Rule("percent", 30, to: At)));
        Assert.Equal(CouponOutcomes.NotApplicable, expired.Outcome);
    }

    [Fact]
    public void no_candidate_equals_plain_quote()
    {
        var q = PromotionPricing.QuoteWithCoupon(1000m, Loc, Svc, At, [Rule("percent", 10)], null);
        Assert.Equal(CouponOutcomes.None, q.Outcome);
        Assert.Equal(900m, q.Quote.Final);
        Assert.Null(q.CouponId);
    }

    [Fact]
    public void normalize_hash_and_hint_follow_the_contract_vector()
    {
        Assert.Equal("ABCDE12345", CouponCodes.Normalize(" abcde-12345 "));
        Assert.Equal("0011", CouponCodes.Normalize("oOiL"));
        // SHA-256("coupon:ABCDE12345") - однаковий вектор у .NET і TS (worker/src/jobs/coupon-code.ts)
        Assert.Equal("60ca79e65ce75a6d75b70701e1316a2687f621fe41be01cd4fda45234af1987b", CouponCodes.Hash("abcde-12345"));
        Assert.Equal("345", CouponCodes.Hint("ABCDE-12345"));
    }

    [Fact]
    public void generated_personal_codes_use_unambiguous_alphabet_and_are_10_chars()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => CouponCodes.GeneratePersonal()).ToList();
        Assert.All(codes, c => { Assert.Equal(10, c.Length); Assert.All(c, ch => Assert.Contains(ch, CouponCodes.Alphabet)); });
        Assert.Equal(500, codes.Distinct().Count());
        Assert.DoesNotContain(codes, c => c.IndexOfAny(['I', 'L', 'O', 'U']) >= 0);
        Assert.Matches("^[0-9A-Z]{5}-[0-9A-Z]{5}$", CouponCodes.Display(codes[0]));
    }

    [Fact]
    public void issued_coupon_does_not_leak_code_through_tostring()
    {
        var issued = new IssuedCoupon(Guid.NewGuid(), "ABCDE12345");
        Assert.DoesNotContain("ABCDE", issued.ToString());
        Assert.DoesNotContain("ABCDE", System.Text.Json.JsonSerializer.Serialize(issued));
    }
}

/// <summary>TASK-778 на реальному PostgreSQL (RLS, UNIQUE, CAS): купони в записі, повернення, гонки, підбір, code_only.</summary>
public sealed class CouponDbTests : IClassFixture<PostgresRlsFixture>
{
    private const string Hours =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    private readonly PostgresRlsFixture _db;
    public CouponDbTests(PostgresRlsFixture db) => _db = db;
    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class World
    {
        public Guid TenantId, UserId, LocationId, SpecialistId, ServiceId, ClientA, ClientB, PublicPromo, CodeOnlyPromo, CampaignId, ChannelId;
        public DateTimeOffset BaseStart;
        public FixedClock Clock = new(DateTimeOffset.UtcNow);
        public CouponAttemptLimiter Limiter = null!;
        public CouponOptions Options = new();
        public CapturingLogger<CouponService> Log = new();
        public DateTimeOffset Slot(int i) => BaseStart.AddHours(i);
    }

    private async Task<World> SeedAsync(decimal codeOnlyValue = 25, string codeOnlyType = "percent")
    {
        var w = new World { TenantId = Guid.NewGuid(), UserId = Guid.NewGuid() };
        w.Limiter = new CouponAttemptLimiter(w.Clock);
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"INSERT INTO tenants (id, name, slug) VALUES ('{w.TenantId}', 'T', 'cp-{w.TenantId:N}');
                INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ('{w.UserId}', '{w.TenantId}', 'u@{w.TenantId:N}.test', 'Owner', 'x', 'owner');", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var ctx = _db.CreateContext(w.TenantId);
        var loc = new Location { Name = "Main", Address = "St 1", Timezone = "Europe/Kyiv" };
        var sp = new Specialist { FullName = "Master" };
        var svc = new Service { Name = "Haircut", DurationMinutes = 60 };
        var a = new Client { FullName = "Анна", Phone = "+380501110001" };
        var b = new Client { FullName = "Борис", Phone = "+380501110002" };
        var pub = new Promotion { Name = "Публічна", DiscountType = DiscountType.Percent, DiscountValue = 10, Access = "public", IsActive = false };
        var co = new Promotion { Name = "Код", DiscountType = codeOnlyType == "fixed" ? DiscountType.Fixed : DiscountType.Percent, DiscountValue = codeOnlyValue, Access = "code_only" };
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.AddRange(loc, sp, svc, a, b, pub, co, ch);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = loc.Id, WorkingHours = Hours });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svc.Id });
        ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = 1000m });
        var camp = new Campaign
        {
            Name = "К", Type = "promo", PromotionId = co.Id, SegmentKind = "all_consented", Template = "x", CouponMode = "personal",
            CouponValidTo = DateTimeOffset.UtcNow.AddDays(30), CreatedByUserId = w.UserId,
        };
        ctx.Add(camp);
        await ctx.SaveChangesAsync();
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var local = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(3).AddHours(8), DateTimeKind.Unspecified);
        w.BaseStart = new DateTimeOffset(local, tz.GetUtcOffset(local));
        (w.LocationId, w.SpecialistId, w.ServiceId, w.ClientA, w.ClientB, w.PublicPromo, w.CodeOnlyPromo, w.CampaignId, w.ChannelId) =
            (loc.Id, sp.Id, svc.Id, a.Id, b.Id, pub.Id, co.Id, camp.Id, ch.Id);
        return w;
    }

    private sealed class Rig(BeautyDbContext ctx) : IAsyncDisposable
    {
        public BeautyDbContext Ctx = ctx;
        public EfBeautyStore Store = null!;
        public CouponService Coupons = null!;
        public BookingService Booking = null!;
        public CancellationService Cancel = null!;
        public SpyPayments Pay = new();
        public ValueTask DisposeAsync() => Ctx.DisposeAsync();
    }

    private Rig NewRig(World w)
    {
        var r = new Rig(_db.CreateContext(w.TenantId));
        r.Store = new EfBeautyStore(r.Ctx, new NoSecrets());
        r.Coupons = new CouponService(r.Store, r.Store, w.Options, w.Limiter, w.Clock, w.Log);
        var settings = new CancellationSettingsService(r.Store);
        r.Booking = new BookingService(r.Store, r.Pay, settings, w.Clock, null, r.Coupons);
        r.Cancel = new CancellationService(r.Store, r.Pay, settings, w.Clock);
        return r;
    }

    private async Task<T> WithRigAsync<T>(World w, Func<Rig, Task<T>> f)
    {
        await using var r = NewRig(w);
        return await f(r);
    }

    private static CreateAppointmentRequest Req(World w, Guid client, int slot, string? code = null, string pay = "cash", decimal? expected = null) =>
        new(w.LocationId, w.SpecialistId, w.ServiceId, w.Slot(slot), new ClientInput(client, null, null, null), null, pay, "admin",
            expected, CouponCode: code);

    private Task<Result<AppointmentDto>> BookAsync(World w, Guid client, int slot, string? code = null, string pay = "cash", CouponScope? scope = null, string? rateKey = null) =>
        WithRigAsync(w, r =>
        {
            if (scope is null) return r.Booking.CreateAsync(Req(w, client, slot, code, pay), default);
            var opts = new PublicCreateOptions(Guid.NewGuid(), scope == CouponScope.Public ? "th" + Guid.NewGuid().ToString("N") : null,
                "ik" + Guid.NewGuid().ToString("N"), "rh");
            return r.Booking.CreateAsync(Req(w, client, slot), default, opts, new CouponUse(scope.Value, code, RateKey: rateKey));
        });

    private async Task<string> IssueAsync(World w, Guid client, DateTimeOffset? to = null, DateTimeOffset? from = null)
    {
        var issued = await WithRigAsync(w, r => r.Coupons.IssueAsync(new IssueCouponRequest(w.CampaignId, client, w.CodeOnlyPromo,
            from ?? w.Clock.Now.AddDays(-1), to ?? w.Clock.Now.AddDays(10), w.UserId), default));
        Assert.True(issued.IsOk, issued.Error?.Code);
        return issued.Value!.DisplayCode;
    }

    private async Task<SharedCouponDto> SharedAsync(World w, string code, int? maxUses = null, DateTimeOffset? to = null)
    {
        var r = await WithRigAsync(w, x => x.Coupons.CreateSharedAsync(new CreateSharedCouponRequest(w.CodeOnlyPromo, code,
            w.Clock.Now.AddDays(-1), to ?? w.Clock.Now.AddDays(10), maxUses), w.UserId, default));
        Assert.True(r.IsOk, r.Error?.Code);
        return r.Value!;
    }

    private async Task<(int Uses, int Active, int Released)> StateAsync(World w, Guid couponId)
    {
        await using var ctx = _db.CreateContext(w.TenantId);
        var c = await ctx.Coupons.AsNoTracking().SingleAsync(x => x.Id == couponId);
        var reds = await ctx.CouponRedemptions.AsNoTracking().Where(x => x.CouponId == couponId).ToListAsync();
        return (c.UsesCount, reds.Count(x => x.Status == "active"), reds.Count(x => x.Status == "released"));
    }

    // ---------- code_only ----------

    [SkippableFact]
    public async Task code_only_promotions_do_not_leak_into_rules_preview_or_prices()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        await using var r = NewRig(w);
        var rules = await r.Store.GetPromotionRulesAsync(default);
        Assert.DoesNotContain(rules, x => x.Id == w.CodeOnlyPromo);

        var promos = new BeautyCrm.Application.Features.BeautyCatalog.PromotionService(r.Store, w.Clock);
        var preview = await promos.PreviewAsync(w.LocationId, null, w.Clock.Now, default);
        Assert.All(preview.Value!, i => Assert.Equal(i.PriceOriginal, i.PriceFinal));
        var all = await promos.ListAsync(default); // CRUD бачить обидві
        Assert.Contains(all, p => p.Id == w.CodeOnlyPromo && p.Access == "code_only");

        var booked = await r.Booking.CreateAsync(Req(w, w.ClientA, 0), default);
        Assert.Equal(1000m, booked.Value!.PriceFinal);
    }

    [SkippableFact]
    public async Task promotion_crud_validates_access_and_blocks_term_changes_in_active_campaign()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        await using var r = NewRig(w);
        var promos = new BeautyCrm.Application.Features.BeautyCatalog.PromotionService(r.Store, w.Clock);
        var created = await promos.CreateAsync(new UpsertPromotionRequest("N", null, "percent", 5, null, null, true, [], [], "code_only"), default);
        Assert.Equal("code_only", created.Value!.Access);
        Assert.Equal("invalid_access", (await promos.CreateAsync(new UpsertPromotionRequest("N", null, "percent", 5, null, null, true, [], [], "vip"), default)).Error!.Code);
        // без access у PUT - без змін
        var kept = await promos.UpdateAsync(created.Value.Id, new UpsertPromotionRequest("N2", null, "percent", 5, null, null, true, [], []), default);
        Assert.Equal("code_only", kept.Value!.Access);

        await r.Ctx.Campaigns.Where(c => c.Id == w.CampaignId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, "scheduled").SetProperty(c => c.ConfirmedByUserId, w.UserId)
            .SetProperty(c => c.ConfirmedAt, DateTimeOffset.UtcNow).SetProperty(c => c.ScheduledAt, DateTimeOffset.UtcNow));
        var cur = (await promos.ListAsync(default)).Single(p => p.Id == w.CodeOnlyPromo);
        Assert.Equal(1, cur.ActiveCampaigns);
        var change = await promos.UpdateAsync(w.CodeOnlyPromo, new UpsertPromotionRequest(cur.Name, null, "percent", 40, null, null, true, [], []), default);
        Assert.Equal("promotion_in_campaign", change.Error!.Code);
        var deactivate = await promos.UpdateAsync(w.CodeOnlyPromo, new UpsertPromotionRequest(cur.Name, null, "percent", 25, null, null, false, [], []), default);
        Assert.True(deactivate.IsOk);
    }

    // ---------- коди ----------

    [SkippableFact]
    public async Task personal_code_is_stored_as_hash_and_last3_only()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var display = await IssueAsync(w, w.ClientA);
        await using var ctx = _db.CreateContext(w.TenantId);
        var c = await ctx.Coupons.AsNoTracking().SingleAsync();
        Assert.Equal("personal", c.Kind);
        Assert.Null(c.Code);
        Assert.Equal(CouponCodes.Hash(display), c.CodeHash);
        Assert.Equal(CouponCodes.Hint(display), c.CodeHint);
        Assert.Equal(3, c.CodeHint.Length);
        Assert.Equal(1, c.MaxUses);
        Assert.DoesNotContain(display.Replace("-", ""), System.Text.Json.JsonSerializer.Serialize(
            await WithRigAsync(w, x => x.Coupons.ListForClientAsync(w.ClientA, default))));
    }

    [SkippableFact]
    public async Task shared_coupon_crud_enforces_format_period_promotion_and_unique_code()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var s = await SharedAsync(w, "summer25");
        Assert.Equal("SUMMER25", s.Code);
        var dup = await WithRigAsync(w, r => r.Coupons.CreateSharedAsync(new CreateSharedCouponRequest(w.CodeOnlyPromo, "SUMMER25", w.Clock.Now, w.Clock.Now.AddDays(1), null), null, default));
        Assert.Equal("coupon_code_taken", dup.Error!.Code);
        string? Code(CreateSharedCouponRequest q) => WithRigAsync(w, r => r.Coupons.CreateSharedAsync(q, null, default)).Result.Error?.Code;
        Assert.Equal("invalid_code", Code(new(w.CodeOnlyPromo, "a-b", w.Clock.Now, w.Clock.Now.AddDays(1), null)));
        Assert.Equal("invalid_period", Code(new(w.CodeOnlyPromo, "GOODCODE", w.Clock.Now, w.Clock.Now, null)));
        Assert.Equal("invalid_max_uses", Code(new(w.CodeOnlyPromo, "GOODCODE", w.Clock.Now, w.Clock.Now.AddDays(1), 0)));
        Assert.Equal("promotion_not_code_only", Code(new(w.PublicPromo, "GOODCODE", w.Clock.Now, w.Clock.Now.AddDays(1), null)));
        Assert.Single(await WithRigAsync(w, r => r.Coupons.ListSharedAsync(null, default)));

        Assert.True((await WithRigAsync(w, r => r.Coupons.RevokeAsync(s.Id, null, default))).IsOk);
        Assert.True((await WithRigAsync(w, r => r.Coupons.RevokeAsync(s.Id, null, default))).IsOk); // ідемпотентно
        var personal = await IssueAsync(w, w.ClientA);
        Assert.False((await WithRigAsync(w, r => r.Coupons.RevokeAsync(
            (r.Ctx.Coupons.AsNoTracking().Single(c => c.Kind == "personal")).Id, null, default))).IsOk); // персональні через API не відкликаються
        Assert.NotEmpty(personal);
    }

    [SkippableFact]
    public async Task m5_new_shared_codes_need_six_characters_and_may_not_be_obvious_words()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        string? Code(string code) => WithRigAsync(w, r => r.Coupons.CreateSharedAsync(
            new CreateSharedCouponRequest(w.CodeOnlyPromo, code, w.Clock.Now, w.Clock.Now.AddDays(1), null), null, default)).Result.Error?.Code;
        Assert.Equal("invalid_code", Code("ABCDE"));   // 5 символів
        Assert.Equal("invalid_code", Code("DONE"));
        foreach (var obvious in new[] { "PROMO", "promocode", "SALE10", "welcome", "VIP", "discount2024" })
            Assert.Equal("invalid_code", Code(obvious));
        Assert.Null(Code("ZEBRA77"));
        w.Options.BlockedCodes.Add("MYBRAND"); // конфіг додає слова
        Assert.Equal("invalid_code", Code("MYBRAND25"));
    }

    [SkippableFact]
    public async Task l7_shared_coupon_writes_are_rate_limited_per_user()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        w.Options.WritePerUserPerMinute = 2;
        var codes = new[] { "ZEBRA701", "ZEBRA702", "ZEBRA703" };
        var results = new List<string?>();
        foreach (var c in codes)
            results.Add((await WithRigAsync(w, r => r.Coupons.CreateSharedAsync(
                new CreateSharedCouponRequest(w.CodeOnlyPromo, c, w.Clock.Now, w.Clock.Now.AddDays(1), null), w.UserId, default))).Error?.Code);
        Assert.Equal(new string?[] { null, null, "rate_limited" }, results);
        var id = (await WithRigAsync(w, r => r.Ctx.Coupons.AsNoTracking().Where(c => c.Code == "ZEBRA701").Select(c => c.Id).SingleAsync()));
        Assert.Equal("rate_limited", (await WithRigAsync(w, r => r.Coupons.RevokeAsync(id, w.UserId, default))).Error!.Code);
    }

    // ---------- запис з купоном ----------

    [SkippableFact]
    public async Task personal_code_applies_for_owner_stores_pricing_fields_and_is_single_use()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var code = await IssueAsync(w, w.ClientA);

        var foreign = await BookAsync(w, w.ClientB, 0, code);
        Assert.Equal("coupon_foreign_client", foreign.Error!.Code); // чужий клієнт

        var ok = await BookAsync(w, w.ClientA, 0, code.ToLowerInvariant().Replace("-", " "));
        Assert.True(ok.IsOk, ok.Error?.Code);
        Assert.Equal(750m, ok.Value!.PriceFinal);
        Assert.Equal(1000m, ok.Value.PriceOriginal);
        Assert.Equal(w.CodeOnlyPromo, ok.Value.PromotionId);
        Assert.Equal(CouponOutcomes.Applied, ok.Value.Coupon!.Outcome);
        Assert.Equal(CouponCodes.Hint(code), ok.Value.Coupon.CodeHint);

        await using (var ctx = _db.CreateContext(w.TenantId))
        {
            var appt = await ctx.Appointments.AsNoTracking().SingleAsync();
            var coupon = await ctx.Coupons.AsNoTracking().SingleAsync();
            Assert.Equal(coupon.Id, appt.CouponId);
            Assert.Equal(1, coupon.UsesCount);
            var red = await ctx.CouponRedemptions.AsNoTracking().SingleAsync();
            Assert.Equal(250m, red.DiscountAmount);
            Assert.Equal("active", red.Status);
        }

        var again = await BookAsync(w, w.ClientA, 1, code);
        Assert.Equal("coupon_exhausted", again.Error!.Code); // одноразовість
    }

    [SkippableFact]
    public async Task two_parallel_bookings_with_one_personal_code_give_exactly_one_success()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var code = await IssueAsync(w, w.ClientA);
        // сервіс-рівень відсікає виснажений купон, тому гонка перевіряється на CAS стору: обидва перевірили до першого коміту
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => BookAsync(w, w.ClientA, i, code)));
        Assert.Equal(1, results.Count(x => x.IsOk));
        Assert.Contains(results, x => !x.IsOk && x.Error!.Code is "coupon_unavailable" or "coupon_exhausted" or "coupon_already_used");
        await using var ctx = _db.CreateContext(w.TenantId);
        Assert.Equal(1, (await ctx.Coupons.AsNoTracking().SingleAsync()).UsesCount);
        Assert.Equal(1, await ctx.Appointments.CountAsync(a => a.CouponId != null));
    }

    [SkippableFact]
    public async Task shared_max_uses_one_with_two_clients_in_parallel_gives_one_success_and_same_client_twice_is_already_used()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var s = await SharedAsync(w, "ONEUSE", maxUses: 1);
        var res = await Task.WhenAll(BookAsync(w, w.ClientA, 0, "ONEUSE"), BookAsync(w, w.ClientB, 1, "oneuse"));
        Assert.Equal(1, res.Count(x => x.IsOk));
        Assert.Equal((1, 1, 0), await StateAsync(w, s.Id));

        var w2 = await SeedAsync();
        var s2 = await SharedAsync(w2, "TWICE25", maxUses: 5);
        Assert.True((await BookAsync(w2, w2.ClientA, 0, "TWICE25")).IsOk);
        Assert.Equal("coupon_already_used", (await BookAsync(w2, w2.ClientA, 1, "TWICE25")).Error!.Code);
        Assert.True((await BookAsync(w2, w2.ClientB, 1, "TWICE25")).IsOk); // інший клієнт - дозволено
        Assert.Equal(2, (await StateAsync(w2, s2.Id)).Uses);
    }

    [SkippableFact]
    public async Task validity_window_revocation_and_promotion_state_are_checked_at_booking_time()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var to = w.Clock.Now.AddHours(2);
        var s = await SharedAsync(w, "WINDOW", to: to);

        w.Clock.Now = to.AddSeconds(-1);
        Assert.True((await BookAsync(w, w.ClientA, 0, "WINDOW")).IsOk); // valid_to - 1 c -> діє
        w.Clock.Now = to;
        Assert.Equal("coupon_expired", (await BookAsync(w, w.ClientB, 1, "WINDOW")).Error!.Code); // = valid_to -> ні

        var later = await SharedAsync(w, "LATER25");
        await using (var ctx = _db.CreateContext(w.TenantId))
            await ctx.Coupons.Where(c => c.Id == later.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ValidFrom, to.AddDays(1)).SetProperty(c => c.ValidTo, to.AddDays(2)));
        Assert.Equal("coupon_not_started", (await BookAsync(w, w.ClientB, 1, "LATER25")).Error!.Code);

        var rev = await SharedAsync(w, "REVOKED");
        await WithRigAsync(w, r => r.Coupons.RevokeAsync(rev.Id, null, default));
        Assert.Equal("coupon_revoked", (await BookAsync(w, w.ClientB, 1, "REVOKED")).Error!.Code);

        var inactive = await SharedAsync(w, "INACTIVE");
        await using (var ctx = _db.CreateContext(w.TenantId))
            await ctx.Promotions.Where(p => p.Id == w.CodeOnlyPromo).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false));
        Assert.Equal("promotion_inactive", (await BookAsync(w, w.ClientB, 1, "INACTIVE")).Error!.Code);
        Assert.NotEqual(Guid.Empty, inactive.Id);
        Assert.Equal("invalid_coupon_format", (await BookAsync(w, w.ClientB, 1, "x")).Error!.Code);
        Assert.Equal("coupon_not_found", (await BookAsync(w, w.ClientB, 1, "NOSUCHCODE")).Error!.Code);
        Assert.Equal(1, (await StateAsync(w, s.Id)).Uses);
    }

    [SkippableFact]
    public async Task better_public_promotion_wins_and_coupon_is_not_consumed()
    {
        SkipIfNoDb();
        var w = await SeedAsync(codeOnlyValue: 15);
        await using (var ctx = _db.CreateContext(w.TenantId))
            await ctx.Promotions.Where(p => p.Id == w.PublicPromo)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, true).SetProperty(p => p.DiscountValue, 20m));
        var s = await SharedAsync(w, "SMALL15");
        var r1 = await BookAsync(w, w.ClientA, 0, "SMALL15");
        Assert.Equal(800m, r1.Value!.PriceFinal);
        Assert.Equal(w.PublicPromo, r1.Value.PromotionId);
        Assert.Equal(CouponOutcomes.NotBetter, r1.Value.Coupon!.Outcome);
        Assert.Equal((0, 0, 0), await StateAsync(w, s.Id)); // не споживається, клієнт може використати пізніше

        await using (var ctx = _db.CreateContext(w.TenantId))
            await ctx.Promotions.Where(p => p.Id == w.CodeOnlyPromo).ExecuteUpdateAsync(x => x.SetProperty(p => p.DiscountValue, 25m));
        var r2 = await BookAsync(w, w.ClientA, 1, "SMALL15");
        Assert.Equal(750m, r2.Value!.PriceFinal);
        Assert.Equal(CouponOutcomes.Applied, r2.Value.Coupon!.Outcome);
        Assert.Equal((1, 1, 0), await StateAsync(w, s.Id));
    }

    [SkippableFact]
    public async Task expected_price_final_accounts_for_the_coupon()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        await SharedAsync(w, "EXPECT");
        var wrong = await WithRigAsync(w, r => r.Booking.CreateAsync(Req(w, w.ClientA, 0, "EXPECT", expected: 1000m), default));
        Assert.Equal("price_changed", wrong.Error!.Code);
        var right = await WithRigAsync(w, r => r.Booking.CreateAsync(Req(w, w.ClientA, 0, "EXPECT", expected: 750m), default));
        Assert.True(right.IsOk, right.Error?.Code);
    }

    // ---------- повернення ----------

    [SkippableFact]
    public async Task cancellation_and_payment_failure_release_the_coupon_but_no_show_and_completed_do_not()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var s = await SharedAsync(w, "RELEASE", maxUses: 1);

        var a = await BookAsync(w, w.ClientA, 0, "RELEASE");
        var cancel = await WithRigAsync(w, r => r.Cancel.CancelAsync(a.Value!.Id, default, CancelOrigin.Staff(w.UserId)));
        Assert.True(cancel.IsOk, cancel.Error?.Code);
        Assert.Equal((0, 0, 1), await StateAsync(w, s.Id)); // купон знову дійсний
        await WithRigAsync(w, r => r.Cancel.CancelAsync(a.Value.Id, default)); // повторне скасування нічого не змінює
        Assert.Equal((0, 0, 1), await StateAsync(w, s.Id));

        // збій оплати при створенні -> payment_failed
        var failed = await WithRigAsync(w, async r =>
        {
            r.Pay.ChargeOk = false;
            return await r.Booking.CreateAsync(Req(w, w.ClientA, 1, "RELEASE", pay: "card"), default);
        });
        Assert.Equal("payment_failed", failed.Error!.Code);
        await using (var ctx = _db.CreateContext(w.TenantId))
            Assert.Contains(await ctx.CouponRedemptions.AsNoTracking().ToListAsync(), x => x.ReleaseReason == "payment_failed");
        Assert.Equal(0, (await StateAsync(w, s.Id)).Uses);

        // no_show і completed не повертають
        var b = await BookAsync(w, w.ClientA, 2, "RELEASE");
        await WithRigAsync(w, r => r.Booking.PatchAsync(b.Value!.Id, new PatchAppointmentRequest(null, "no_show"), default));
        Assert.Equal(1, (await StateAsync(w, s.Id)).Uses);
        var s2 = await SharedAsync(w, "DONE25", maxUses: 3);
        var c = await BookAsync(w, w.ClientB, 3, "DONE25");
        await WithRigAsync(w, r => r.Booking.PatchAsync(c.Value!.Id, new PatchAppointmentRequest(null, "completed"), default));
        Assert.Equal((1, 1, 0), await StateAsync(w, s2.Id));
    }

    // ---------- публічний шлях і підбір ----------

    [SkippableFact]
    public async Task public_path_accepts_only_shared_codes_with_one_uniform_error()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var personal = await IssueAsync(w, w.ClientA);
        await SharedAsync(w, "PUBLIC25");
        var viaPersonal = await BookAsync(w, w.ClientA, 0, personal, scope: CouponScope.Public, rateKey: "1.1.1.1");
        var unknown = await BookAsync(w, w.ClientA, 0, "UNKNOWNCODE", scope: CouponScope.Public, rateKey: "1.1.1.1");
        Assert.Equal(CouponService.Invalid, viaPersonal.Error!.Code);
        Assert.Equal(viaPersonal.Error, unknown.Error); // однакові код, статус і тіло
        Assert.Equal(ErrorKind.Conflict, viaPersonal.Error!.Kind);

        var ok = await BookAsync(w, w.ClientA, 0, "public25", scope: CouponScope.Public, rateKey: "1.1.1.1");
        Assert.True(ok.IsOk, ok.Error?.Code);
        Assert.Equal(750m, ok.Value!.PriceFinal);
    }

    [SkippableFact]
    public async Task public_check_limit_and_chat_failed_attempts_lock_the_guessing()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var s = await SharedAsync(w, "GUESS25");
        var personal = await IssueAsync(w, w.ClientA);
        await using var r = NewRig(w);
        var expired = await SharedAsync(w, "OLDCODE", to: w.Clock.Now.AddMinutes(1));
        w.Clock.Now = w.Clock.Now.AddMinutes(2);

        // публічна перевірка: невдалі й успішні рахуються, 11-й за 10 хв -> 429
        var codes = new[] { "NOPE1111", personal, "OLDCODE", "GUESS25" };
        for (var i = 0; i < 10; i++)
        {
            var x = await r.Coupons.CheckAsync(new CouponUse(CouponScope.Public, codes[i % 4], RateKey: "9.9.9.9"), w.LocationId, w.ServiceId, null, default);
            if (i % 4 == 3) Assert.True(x.IsOk); else Assert.Equal(CouponService.Invalid, x.Error!.Code);
        }
        var eleventh = await r.Coupons.CheckAsync(new CouponUse(CouponScope.Public, "GUESS25", RateKey: "9.9.9.9"), w.LocationId, w.ServiceId, null, default);
        Assert.Equal(ErrorKind.TooManyRequests, eleventh.Error!.Kind);

        // чат: 5 невдалих -> наступний (навіть валідний) не розбирається
        var conv = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
            Assert.Equal(CouponService.Invalid, (await r.Coupons.ResolveAsync(new CouponUse(CouponScope.Chat, "BAD" + i + "BAD", RateKey: conv), w.ClientA, w.Clock.Now, default)).Error!.Code);
        var locked = await r.Coupons.ResolveAsync(new CouponUse(CouponScope.Chat, "GUESS25", RateKey: conv), w.ClientA, w.Clock.Now, default);
        Assert.Equal(CouponService.Locked, locked.Error!.Code);
        Assert.Equal(ErrorKind.TooManyRequests, locked.Error.Kind);
        w.Clock.Now = w.Clock.Now.AddHours(1).AddSeconds(1); // кінець години
        Assert.True((await r.Coupons.ResolveAsync(new CouponUse(CouponScope.Chat, "GUESS25", RateKey: conv), w.ClientA, w.Clock.Now, default)).IsOk);
        Assert.NotEqual(s.Id, expired.Id);
    }

    [SkippableFact]
    public async Task staff_check_returns_outcome_and_detailed_errors_and_logs_contain_no_codes()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        await SharedAsync(w, "STAFFSECRET");
        var personal = await IssueAsync(w, w.ClientA);
        await using var r = NewRig(w);
        var ok = await r.Coupons.CheckAsync(new CouponUse(CouponScope.Staff, "staffsecret", RateKey: "u1"), w.LocationId, w.ServiceId, w.ClientB, default);
        Assert.Equal((CouponOutcomes.Applied, 1000m, 750m), (ok.Value!.Outcome, ok.Value.PriceOriginal, ok.Value.PriceFinal));
        var foreign = await r.Coupons.CheckAsync(new CouponUse(CouponScope.Staff, personal, RateKey: "u1"), w.LocationId, w.ServiceId, w.ClientB, default);
        Assert.Equal("coupon_foreign_client", foreign.Error!.Code);
        await r.Coupons.CheckAsync(new CouponUse(CouponScope.Staff, "ZZZZZZZZZ", RateKey: "u1"), w.LocationId, w.ServiceId, null, default);

        var logs = w.Log.All;
        Assert.DoesNotContain("STAFFSECRET", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(personal.Replace("-", ""), logs.Replace("-", ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CouponCodes.Hash(personal), logs);
        Assert.Contains("coupon_check scope=staff", logs);
    }

    [SkippableFact]
    public async Task rls_hides_coupons_from_other_tenants_and_same_code_is_allowed_in_two_tenants()
    {
        SkipIfNoDb();
        var a = await SeedAsync();
        var b = await SeedAsync();
        await SharedAsync(a, "SAMECODE");
        await SharedAsync(b, "SAMECODE"); // UNIQUE (tenant, code_hash)
        Assert.Single(await WithRigAsync(a, r => r.Coupons.ListSharedAsync(null, default)));
        var lookupFromB = await WithRigAsync(b, r => r.Store.FindByHashAsync(CouponCodes.Hash("SAMECODE"), default));
        Assert.Equal(b.CodeOnlyPromo, lookupFromB!.Coupon.PromotionId);
    }

    // ---------- чат ----------

    [SkippableFact]
    public async Task chat_proposal_uses_offer_coupon_and_confirm_creates_discounted_booking()
    {
        SkipIfNoDb();
        var w = await SeedAsync();
        var personalCode = await IssueAsync(w, w.ClientA);
        Guid couponId;
        Guid convId;
        await using (var ctx = _db.CreateContext(w.TenantId))
        {
            couponId = (await ctx.Coupons.AsNoTracking().SingleAsync()).Id;
            var conv = new Conversation { ChannelId = w.ChannelId, ClientId = w.ClientA, ExternalChatId = Guid.NewGuid().ToString("N")[..10] };
            ctx.Add(conv);
            await ctx.SaveChangesAsync();
            var msg = new Message { ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "s", Status = "sending" };
            ctx.Add(msg);
            await ctx.SaveChangesAsync();
            convId = conv.Id;
            _msg = msg.Id;
        }

        async Task<T> Chat<T>(Func<ChatBookingService, Task<T>> f)
        {
            await using var r = NewRig(w);
            var pub = new EfPublicBookingStore(r.Ctx);
            var core = new OnlineBookingCore(pub, r.Booking, new PublicBookingOptions(), w.Clock);
            return await f(new ChatBookingService(new EfChatBookingStore(r.Ctx), pub, r.Store, core, new ChatBookingOptions(), w.Clock, null, r.Coupons));
        }

        var prepared = await Chat(s => s.PrepareAsync(new ChatPrepareRequest(convId, w.LocationId, w.ServiceId, w.SpecialistId, w.Slot(0), couponId), default));
        Assert.True(prepared.IsOk, prepared.Error?.Code);
        Assert.Equal(750m, prepared.Value!.Summary.PriceFinal);
        Assert.Equal(CouponOutcomes.Applied, prepared.Value.Summary.CouponOutcome);
        Assert.True(await Chat(s => s.MarkShownAsync(convId, prepared.Value.ProposalId, _msg, default)));

        var confirmed = await Chat(s => s.ConfirmAsync(new ChatConfirmRequest(convId, prepared.Value.ProposalId, true), default));
        Assert.True(confirmed.IsOk, confirmed.Error?.Code);
        Assert.Equal(750m, confirmed.Value!.Appointment.PriceFinal);
        Assert.Equal((1, 1, 0), await StateAsync(w, couponId));
        await using (var ctx = _db.CreateContext(w.TenantId))
            Assert.Equal(couponId, (await ctx.ChatBookingProposals.AsNoTracking().SingleAsync()).CouponId);

        // чужа розмова (інший клієнт) з тим самим персональним купоном -> один uniform invalid
        var other = await Chat(s => s.PrepareAsync(new ChatPrepareRequest(convId, w.LocationId, w.ServiceId, w.SpecialistId, w.Slot(2), Guid.NewGuid()), default));
        Assert.Equal(CouponService.Invalid, other.Error!.Code);
        Assert.NotEmpty(personalCode);
    }

    private Guid _msg;
}
