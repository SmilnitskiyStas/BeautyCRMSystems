using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Tests.Beauty;

/// <summary>TASK-706 (§19): чиста логіка без БД - формула завантаження, перетини інтервалів, розбір діапазону, гранулярність.</summary>
public sealed class AnalyticsUnitTests
{
    private static readonly string AllDays = Hours("09:00", "18:00");

    private static string Hours(string from, string to) =>
        "{" + string.Join(",", new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" }.Select(d => $"\"{d}\":[{{\"from\":\"{from}\",\"to\":\"{to}\"}}]")) + "}";

    private static TimeOnly T(string s) => TimeOnly.Parse(s);

    // ---------- SlotCalculator: спільні помічники ----------

    [Fact]
    public void OverlapMinutes_intersects_schedule_with_location_hours()
    {
        IReadOnlyList<(TimeOnly, TimeOnly)> schedule = [(T("10:00"), T("18:00"))];
        IReadOnlyList<(TimeOnly, TimeOnly)> hours = [(T("09:00"), T("17:00"))];
        Assert.Equal(420, SlotCalculator.OverlapMinutes(schedule, hours));
        Assert.Equal(480, SlotCalculator.OverlapMinutes(schedule, null)); // години закладу не задані - обмеження немає
        Assert.Equal(0, SlotCalculator.OverlapMinutes(schedule, []));       // задані, але в цей день порожні
        Assert.Equal(0, SlotCalculator.OverlapMinutes([(T("09:00"), T("12:00"))], [(T("13:00"), T("18:00"))]));
        // дві пари інтервалів: сума попарних перетинів
        Assert.Equal(120 + 60, SlotCalculator.OverlapMinutes(
            [(T("09:00"), T("12:00")), (T("14:00"), T("18:00"))], [(T("10:00"), T("12:00")), (T("13:00"), T("15:00"))]));
    }

    [Fact]
    public void WeeklyIntervals_parses_once_and_tolerates_bad_json()
    {
        var week = SlotCalculator.WeeklyIntervals("""{"mon":[{"from":"09:00","to":"12:00"},{"from":"13:00","to":"18:00"}],"sun":[]}""");
        Assert.Equal(2, week[(int)DayOfWeek.Monday].Count);
        Assert.Empty(week[(int)DayOfWeek.Sunday]);
        Assert.Empty(week[(int)DayOfWeek.Tuesday]);
        Assert.All(SlotCalculator.WeeklyIntervals(null), d => Assert.Empty(d));
        Assert.All(SlotCalculator.WeeklyIntervals("not json"), d => Assert.Empty(d));
        Assert.All(SlotCalculator.WeeklyIntervals("null"), d => Assert.Empty(d));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(100, 0, null)]    // ділення на 0
    [InlineData(0, 480, 0.0)]
    [InlineData(240, 480, 0.5)]
    [InlineData(240, 3780, 0.0635)]
    [InlineData(480, 480, 1.0)]
    [InlineData(500, 480, 1.0)]   // перевищення обрізається до 1
    public void LoadOf_is_null_without_availability_rounded_to_4_digits_and_clamped_to_one(int busy, int avail, double? expected)
    {
        var load = AnalyticsService.LoadOf(busy, avail);
        if (expected is null) Assert.Null(load);
        else Assert.Equal((decimal)expected.Value, load);
    }

    // ---------- сервіс з фейковим сховищем ----------

    private static readonly Guid Loc = Guid.NewGuid();
    private static readonly Guid Spec = Guid.NewGuid();

    private static AnalyticsService Sut(FakeAnalyticsStore store, string now = "2026-10-24T12:00:00Z") =>
        new(store, new FakeClock(DateTimeOffset.Parse(now)));

    private static FakeAnalyticsStore Store(string tz = "Europe/Kyiv", string? scheduleJson = null, string? locationHours = null) => new()
    {
        Locations = [new AnalyticsLocation(Loc, "A", tz, true, [], locationHours)],
        Links = [new AnalyticsLink(Spec, "Master", true, Loc, true, scheduleJson ?? AllDays)],
    };

    private static async Task<T> Value<T>(Task<Result<T>> r)
    {
        var result = await r;
        Assert.True(result.IsOk, result.Error?.Code);
        return result.Value!;
    }

    [Theory]
    [InlineData("2026-02-30", "2026-03-05")]
    [InlineData("2026-06-10", "2026-06-09")]
    [InlineData("2024-01-01", "2025-01-01")]            // 367 днів
    [InlineData("2026-06-01", "2026-06-10T00:00:00Z")]  // змішаний формат
    [InlineData("1999-12-31", "2000-01-02")]
    [InlineData("2026-6-1", "2026-06-10")]
    [InlineData("junk", null)]
    public async Task Invalid_date_ranges_are_rejected_with_invalid_range(string from, string? to)
    {
        var sut = Sut(Store());
        foreach (var result in new[]
                 {
                     await sut.NetworkAsync(from, to, false, default), await sut.LocationsAsync(from, to, false, default),
                     await sut.PromotionsAsync(from, to, true, default),
                 })
        {
            Assert.False(result.IsOk);
            Assert.Equal("invalid_range", result.Error!.Code);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        }
        Assert.Equal("invalid_range", (await sut.SeriesAsync(from, to, default)).Error!.Code);
        Assert.Equal("invalid_range", (await sut.BreakdownAsync(Loc, from, to, default)).Error!.Code);
    }

    [Fact]
    public async Task Exactly_366_days_is_accepted_and_legacy_over_366_days_is_rejected()
    {
        var sut = Sut(Store());
        Assert.True((await sut.NetworkAsync("2024-01-01", "2024-12-31", false, default)).IsOk);
        var legacy = await sut.NetworkAsync("2025-01-01T00:00:00Z", "2026-06-01T00:00:00Z", false, default);
        Assert.Equal("invalid_range", legacy.Error!.Code);
        Assert.Equal("from must be before to.", (await sut.NetworkAsync("2026-06-01T00:00:00Z", "2026-06-01T00:00:00Z", false, default)).Error!.Message);
    }

    [Fact]
    public async Task Legacy_values_are_routed_to_the_frozen_store_methods_and_ignore_compare()
    {
        var store = Store();
        var sut = Sut(store);
        var result = await sut.NetworkAsync("2026-06-10T10:00:00Z", "2026-06-10T11:00:00Z", true, default);
        Assert.IsType<NetworkAnalytics>(result.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-06-10T10:00:00Z"), store.LegacyFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-06-10T11:00:00Z"), store.LegacyTo);
        Assert.Equal(0, store.DailyCalls);
    }

    [Fact]
    public async Task Defaults_use_the_last_30_days_in_the_reference_zone_and_previous_is_adjacent()
    {
        // 23:30Z 24.10 = 02:30 25.10 у Києві (+03:00, перехід на зимовий час о 01:00Z 25.10 ще не настав), тож "сьогодні" вже 25.10
        var sut = Sut(Store(), "2026-10-24T23:30:00Z");
        var net = (NetworkReport)(await Value(sut.NetworkAsync(null, null, true, default)));
        Assert.Equal("2026-10-25", net.ToDate);
        Assert.Equal("2026-09-26", net.FromDate);
        Assert.Equal("2026-08-27", net.Previous!.FromDate);
        Assert.Equal("2026-09-25", net.Previous.ToDate);
        Assert.Equal("Europe/Kyiv", net.Timezone);
    }

    [Fact]
    public async Task Tenant_without_active_locations_uses_utc()
    {
        var store = new FakeAnalyticsStore { Locations = [new AnalyticsLocation(Loc, "Off", "Pacific/Auckland", false, [], null)] };
        var net = (NetworkReport)(await Value(Sut(store).NetworkAsync("2026-06-01", "2026-06-02", false, default)));
        Assert.Equal("UTC", net.Timezone);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00Z"), net.From);
        Assert.Equal(DateTimeOffset.Parse("2026-06-03T00:00:00Z"), net.To);
    }

    [Theory]
    [InlineData(1, "day", 1)]
    [InlineData(31, "day", 31)]
    [InlineData(32, "week", 5)]
    [InlineData(120, "week", 18)]
    [InlineData(121, "month", 5)]
    [InlineData(366, "month", 13)]
    public async Task Series_granularity_depends_on_the_number_of_days(int days, string granularity, int points)
    {
        var from = new DateOnly(2026, 1, 1);
        var series = await Value(Sut(Store()).SeriesAsync("2026-01-01", from.AddDays(days - 1).ToString("yyyy-MM-dd"), default));
        Assert.Equal(granularity, series.Granularity);
        Assert.Equal(points, series.Points.Count);
    }

    [Fact]
    public async Task Series_puts_each_days_rows_in_the_right_bucket_and_sums_to_the_network()
    {
        var store = Store();
        store.Daily =
        [
            new DailyRow(Loc, new DateOnly(2026, 10, 31), "completed", 2, 300m),
            new DailyRow(Loc, new DateOnly(2026, 11, 1), "cancelled", 1, 0m),
            new DailyRow(Loc, new DateOnly(2026, 11, 1), "no_show", 1, 0m),
        ];
        var sut = Sut(store);
        var series = await Value(sut.SeriesAsync("2026-09-01", "2026-12-31", default));
        Assert.Equal("month", series.Granularity);
        Assert.Equal(2, series.Points[1].Appointments);   // жовтень: 31.10
        Assert.Equal(2, series.Points[2].Appointments);   // листопад: 01.11
        Assert.Equal(300m, series.Points[1].Revenue);
        Assert.Equal(1, series.Points[2].Cancelled);
        Assert.Equal(1, series.Points[2].NoShow);
        var net = (NetworkReport)(await Value(sut.NetworkAsync("2026-09-01", "2026-12-31", false, default)));
        Assert.Equal(net.Appointments, series.Points.Sum(p => p.Appointments));
        Assert.Equal(net.Revenue, series.Points.Sum(p => p.Revenue));
        Assert.Equal(0.25m, net.CancellationRate);
        Assert.Equal(150m, net.AverageCheck);
    }

    [Fact]
    public async Task Availability_counts_wall_clock_minutes_on_dst_days_and_loads_are_computed_from_it()
    {
        foreach (var day in new[] { "2026-03-29", "2026-10-25" })
        {
            var store = Store();
            store.Busy = [new BusyRow(Loc, Spec, 270)];
            var b = await Value(Sut(store).BreakdownAsync(Loc, day, day, default));
            Assert.Equal(540, b.AvailableMinutes);
            Assert.Equal(0.5m, b.Load);
            Assert.Equal(0.5m, Assert.Single(b.Specialists).Load);
        }
    }

    [Fact]
    public async Task Closures_absences_closed_weekdays_and_location_hours_reduce_availability_and_other_statuses_do_not_exist_here()
    {
        var store = new FakeAnalyticsStore
        {
            Locations = [new AnalyticsLocation(Loc, "A", "UTC", true, ["sun"], Hours("09:00", "17:00"))],
            Links = [new AnalyticsLink(Spec, "Master", true, Loc, true, Hours("10:00", "18:00"))],
            Closures = [new AnalyticsSpan(Loc, new DateOnly(2026, 6, 3), new DateOnly(2026, 6, 3))],
            Absences = [new AnalyticsSpan(Spec, new DateOnly(2026, 6, 9), new DateOnly(2026, 6, 10))],
        };
        var b = await Value(Sut(store).BreakdownAsync(Loc, "2026-06-01", "2026-06-14", default));
        Assert.Equal(9 * 420, b.AvailableMinutes);
        Assert.Null(AnalyticsService.LoadOf(1, 0));
    }

    [Fact]
    public async Task Future_days_of_an_inactive_profile_are_unavailable_but_today_and_the_past_keep_the_schedule()
    {
        var store = Store();
        store.Links = [new AnalyticsLink(Spec, "Master", false, Loc, true, AllDays)];
        // сьогодні 2026-10-24 (UTC-полудень, Київ той самий день): 22..26 -> 22, 23, 24 рахуються
        var b = await Value(Sut(store).BreakdownAsync(Loc, "2026-10-22", "2026-10-26", default));
        Assert.Equal(3 * 540, b.AvailableMinutes);
    }

    [Fact]
    public async Task Location_list_has_all_active_locations_and_inactive_ones_only_with_bookings_sorted_by_revenue()
    {
        var other = Guid.NewGuid();
        var oldInactive = Guid.NewGuid();
        var idleInactive = Guid.NewGuid();
        var store = new FakeAnalyticsStore
        {
            Locations =
            [
                new AnalyticsLocation(Loc, "A", "UTC", true, [], null), new AnalyticsLocation(other, "B", "UTC", true, [], null),
                new AnalyticsLocation(oldInactive, "Old", "UTC", false, [], null), new AnalyticsLocation(idleInactive, "Idle old", "UTC", false, [], null),
            ],
            Daily =
            [
                new DailyRow(other, new DateOnly(2026, 6, 2), "completed", 1, 500m),
                new DailyRow(oldInactive, new DateOnly(2026, 6, 2), "pending", 1, 0m),
            ],
        };
        var rows = (IReadOnlyList<LocationReport>)(await Value(Sut(store).LocationsAsync("2026-06-01", "2026-06-30", false, default)));
        Assert.Equal(["B", "A", "Old"], rows.Select(r => r.Name).ToArray()); // revenue desc, далі за назвою
        Assert.DoesNotContain(rows, r => r.Name == "Idle old");
        Assert.All(rows, r => Assert.Null(r.Previous));
    }

    [Fact]
    public async Task Compare_queries_the_adjacent_previous_period_for_every_section()
    {
        var store = Store();
        var sut = Sut(store);
        await Value(sut.LocationsAsync("2026-10-01", "2026-10-24", true, default));
        Assert.Contains((new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 30)), store.DailyRanges);
        Assert.Contains((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 24)), store.DailyRanges);
        await Value(sut.PromotionsAsync("2026-10-01", "2026-10-24", true, default));
        Assert.Contains((new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 30)), store.PromotionRanges);
    }

    // ---------- фейки ----------

    private sealed class FakeAnalyticsStore : IAnalyticsStore
    {
        public IReadOnlyList<AnalyticsLocation> Locations { get; set; } = [];
        public IReadOnlyList<AnalyticsLink> Links { get; set; } = [];
        public IReadOnlyList<AnalyticsSpan> Closures { get; set; } = [];
        public IReadOnlyList<AnalyticsSpan> Absences { get; set; } = [];
        public IReadOnlyList<DailyRow> Daily { get; set; } = [];
        public IReadOnlyList<BusyRow> Busy { get; set; } = [];
        public DateTimeOffset? LegacyFrom, LegacyTo;
        public int DailyCalls;
        public List<(DateOnly, DateOnly)> DailyRanges { get; } = [];
        public List<(DateOnly, DateOnly)> PromotionRanges { get; } = [];

        public Task<NetworkAnalytics> NetworkAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            LegacyFrom = from;
            LegacyTo = to;
            return Task.FromResult(new NetworkAnalytics(from, to, 0, 0, 0, 0, 0m, 0m, 0m, 0));
        }
        public Task<IReadOnlyList<LocationAnalytics>> LocationsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LocationAnalytics>>([]);
        public Task<IReadOnlyList<PromotionAnalytics>> PromotionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PromotionAnalytics>>([]);
        public Task<IReadOnlyList<AnalyticsLocation>> AnalyticsLocationsAsync(CancellationToken ct) => Task.FromResult(Locations);
        public Task<IReadOnlyList<DailyRow>> DailyAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct)
        {
            DailyCalls++;
            DailyRanges.Add((from, to));
            return Task.FromResult<IReadOnlyList<DailyRow>>(Daily.Where(d => d.Date >= from && d.Date <= to && (locationId is null || d.LocationId == locationId)).ToList());
        }
        public Task<IReadOnlyList<BusyRow>> BusyAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct) => Task.FromResult(Busy);
        public Task<IReadOnlyList<AnalyticsLink>> LinksAsync(Guid? locationId, CancellationToken ct) => Task.FromResult(Links);
        public Task<IReadOnlyList<AnalyticsSpan>> ClosuresAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct) => Task.FromResult(Closures);
        public Task<IReadOnlyList<AnalyticsSpan>> ApprovedAbsencesAsync(DateOnly from, DateOnly to, CancellationToken ct) => Task.FromResult(Absences);
        public Task<int> NewClientsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<PromotionStatRow>> PromotionStatsAsync(DateOnly from, DateOnly to, CancellationToken ct)
        {
            PromotionRanges.Add((from, to));
            return Task.FromResult<IReadOnlyList<PromotionStatRow>>([]);
        }
        public Task<IReadOnlyList<SpecialistStatRow>> SpecialistStatsAsync(DateOnly from, DateOnly to, Guid locationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SpecialistStatRow>>([]);
        public Task<IReadOnlyList<ServiceStatRow>> TopServicesAsync(DateOnly from, DateOnly to, Guid locationId, int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServiceStatRow>>([]);
    }
}
