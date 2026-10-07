using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Tests.Beauty;

public class PromotionPricingTests
{
    private static readonly Guid L1 = Guid.NewGuid(), L2 = Guid.NewGuid(), S1 = Guid.NewGuid(), S2 = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2030, 1, 7, 10, 0, 0, TimeSpan.Zero);

    private static PromotionRule Rule(string type = "percent", decimal value = 20, Guid[]? locs = null, Guid[]? svcs = null,
        bool active = true, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(Guid.NewGuid(), "promo", type, value, from, to, active, locs ?? [], svcs ?? []);

    [Fact]
    public void quote_applies_percent_discount() =>
        Assert.Equal(800m, PromotionPricing.Quote(1000m, L1, S1, At, [Rule()]).Final);

    [Fact]
    public void quote_applies_fixed_discount() =>
        Assert.Equal(850m, PromotionPricing.Quote(1000m, L1, S1, At, [Rule("fixed", 150)]).Final);

    [Fact]
    public void quote_never_goes_below_zero() =>
        Assert.Equal(0m, PromotionPricing.Quote(100m, L1, S1, At, [Rule("fixed", 500)]).Final);

    [Fact]
    public void quote_ignores_promotion_for_other_location()
    {
        var q = PromotionPricing.Quote(1000m, L2, S1, At, [Rule(locs: [L1])]);
        Assert.Equal(1000m, q.Final);
        Assert.Null(q.PromotionId);
    }

    [Fact]
    public void quote_ignores_promotion_for_other_service() =>
        Assert.Equal(1000m, PromotionPricing.Quote(1000m, L1, S2, At, [Rule(svcs: [S1])]).Final);

    [Fact]
    public void quote_applies_when_location_and_service_match() =>
        Assert.Equal(900m, PromotionPricing.Quote(1000m, L1, S1, At, [Rule(value: 10, locs: [L1, L2], svcs: [S1])]).Final);

    [Fact]
    public void quote_respects_period_and_active_flag()
    {
        Assert.Equal(1000m, PromotionPricing.Quote(1000m, L1, S1, At, [Rule(from: At.AddDays(1))]).Final);
        Assert.Equal(1000m, PromotionPricing.Quote(1000m, L1, S1, At, [Rule(to: At)]).Final); // кінець виключно
        Assert.Equal(1000m, PromotionPricing.Quote(1000m, L1, S1, At, [Rule(active: false)]).Final);
    }

    [Fact]
    public void quote_picks_best_discount_without_stacking()
    {
        var best = Rule(value: 30);
        var q = PromotionPricing.Quote(1000m, L1, S1, At, [Rule(value: 10), best, Rule("fixed", 200)]);
        Assert.Equal(700m, q.Final);
        Assert.Equal(best.Id, q.PromotionId);
    }

    [Fact]
    public void quote_rounds_to_cents() =>
        Assert.Equal(66.67m, PromotionPricing.Quote(100m, L1, S1, At, [Rule(value: 33.333m)]).Final);
}

public class SlotCalculatorTests
{
    // 2030-01-07 — понеділок
    private static readonly DateOnly Monday = new(2030, 1, 7);
    private static readonly DateTimeOffset EarlyNow = new(2030, 1, 6, 0, 0, 0, TimeSpan.Zero);
    private static SpecialistSchedule Sched(string tz = "UTC", string? json = FakeBookingStore.Mon9To18) =>
        new(FakeBookingStore.Specialist, tz, json);

    private static DateTimeOffset T(int h, int m = 0) => new(2030, 1, 7, h, m, 0, TimeSpan.Zero);

    [Fact]
    public void compute_uses_service_duration_for_slot_length()
    {
        var slots = SlotCalculator.Compute(Sched(), Monday, 90, [], EarlyNow);
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(90), s.EndsAt - s.StartsAt));
        Assert.Equal(T(9), slots.First().StartsAt);
        Assert.Equal(T(16, 30), slots.Last().StartsAt); // 16:30 + 90 хв = 18:00
    }

    [Fact]
    public void compute_does_not_return_slot_overlapping_existing_appointment()
    {
        var busy = new[] { new TimeRange(T(10), T(11, 30)) };
        var slots = SlotCalculator.Compute(Sched(), Monday, 60, busy, EarlyNow);
        Assert.DoesNotContain(slots, s => s.StartsAt < T(11, 30) && T(10) < s.EndsAt);
        Assert.Contains(slots, s => s.StartsAt == T(9)); // впритул до початку — вільний
        Assert.Contains(slots, s => s.StartsAt == T(11, 30)); // впритул після кінця — вільний
        Assert.DoesNotContain(slots, s => s.StartsAt == T(9, 15)); // 9:15-10:15 заходить на 10:00
    }

    [Fact]
    public void compute_returns_nothing_on_day_off_or_without_schedule()
    {
        Assert.Empty(SlotCalculator.Compute(Sched(), new DateOnly(2030, 1, 12), 60, [], EarlyNow)); // субота
        Assert.Empty(SlotCalculator.Compute(Sched(json: null), Monday, 60, [], EarlyNow));
    }

    [Fact]
    public void compute_skips_slots_in_the_past()
    {
        var slots = SlotCalculator.Compute(Sched(), Monday, 60, [], T(12));
        Assert.All(slots, s => Assert.True(s.StartsAt >= T(12)));
    }

    [Fact]
    public void compute_returns_nothing_when_service_longer_than_working_day() =>
        Assert.Empty(SlotCalculator.Compute(Sched(), Monday, 600, [], EarlyNow));

    [Fact]
    public void compute_uses_location_timezone()
    {
        // Київ узимку UTC+2: 09:00 локально = 07:00 UTC
        var slots = SlotCalculator.Compute(Sched("Europe/Kyiv"), Monday, 60, [], EarlyNow);
        Assert.Equal(T(7), slots.First().StartsAt);
    }

    [Fact]
    public void check_flags_busy_outside_hours_and_past()
    {
        var busy = new[] { new TimeRange(T(10), T(11)) };
        Assert.Equal(SlotCheck.Ok, SlotCalculator.Check(Sched(), T(11), 60, busy, EarlyNow));
        Assert.Equal(SlotCheck.Busy, SlotCalculator.Check(Sched(), T(10, 30), 60, busy, EarlyNow));
        Assert.Equal(SlotCheck.OutsideWorkingHours, SlotCalculator.Check(Sched(), T(17, 30), 60, [], EarlyNow));
        Assert.Equal(SlotCheck.InPast, SlotCalculator.Check(Sched(), T(11), 60, [], T(12)));
    }
}
