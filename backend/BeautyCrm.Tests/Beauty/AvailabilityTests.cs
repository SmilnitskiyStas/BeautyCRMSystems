using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// Календар доступності (мобільний потік): статуси closed/past/full/available, сьогодні з минулим часом,
/// узгодженість із /slots, specialistId і «будь-який майстер», валідація. Детерміновано (FakeClock, 2030-01-07 = понеділок).
/// Графік фейкового майстра: пн і вт 09:00-18:00, послуга 90 хв, заклад у UTC.
/// </summary>
public class AvailabilityTests
{
    private static readonly DateOnly Mon = new(2030, 1, 7);
    private readonly FakeBookingStore _store = new();

    private static DateTimeOffset T(int day, int h, int m = 0) => new(2030, 1, day, h, m, 0, TimeSpan.Zero);

    private BookingService Sut(DateTimeOffset now) => new(_store, new SpyPayments(), FakeSettings.Service(), new FakeClock(now));

    private async Task<IReadOnlyList<DayAvailability>> Days(DateTimeOffset now, DateOnly? from = null, int days = 7, Guid? specialist = null)
    {
        var r = await Sut(now).GetAvailabilityAsync(FakeBookingStore.Location, specialist, FakeBookingStore.Service90, from, days, 180, default);
        Assert.True(r.IsOk, r.Error?.Code);
        return r.Value!;
    }

    private async Task<int> SlotsCount(DateTimeOffset now, DateOnly date, Guid? specialist = null) =>
        (await Sut(now).GetSlotsAsync(FakeBookingStore.Location, specialist, FakeBookingStore.Service90, date, default)).Value!.Count;

    [Fact]
    public async Task working_day_is_available_with_the_same_slot_count_as_slots_and_non_working_day_is_closed()
    {
        var now = T(7, 0);
        var days = await Days(now, Mon, 4); // пн, вт, ср, чт
        Assert.Equal([Mon, Mon.AddDays(1), Mon.AddDays(2), Mon.AddDays(3)], days.Select(d => d.Date));
        Assert.Equal([DayStatus.Available, DayStatus.Available, DayStatus.Closed, DayStatus.Closed], days.Select(d => d.Status));
        Assert.Equal(await SlotsCount(now, Mon), days[0].FreeSlots);
        Assert.Equal(await SlotsCount(now, Mon.AddDays(1)), days[1].FreeSlots);
        Assert.True(days[0].FreeSlots > 0);
        Assert.All(days.Skip(2), d => Assert.Equal(0, d.FreeSlots));
    }

    [Fact]
    public async Task day_before_today_is_past()
    {
        var days = await Days(T(7, 0), Mon.AddDays(-1), 2); // нд (минулий), пн (сьогодні)
        Assert.Equal(DayStatus.Past, days[0].Status);
        Assert.Equal(0, days[0].FreeSlots);
        Assert.Equal(DayStatus.Available, days[1].Status);
    }

    [Fact]
    public async Task today_with_part_of_the_day_gone_has_fewer_slots_and_matches_slots_endpoint()
    {
        var morning = T(7, 0);
        var noon = T(7, 12);
        var all = (await Days(morning, Mon, 1))[0].FreeSlots;
        var later = (await Days(noon, Mon, 1))[0];
        Assert.Equal(DayStatus.Available, later.Status);
        Assert.True(later.FreeSlots < all);
        Assert.Equal(await SlotsCount(noon, Mon), later.FreeSlots);
    }

    [Fact]
    public async Task today_when_every_remaining_slot_is_gone_is_full_not_closed()
    {
        // 17:00: 90-хвилинна послуга вже не вміщується до 18:00, але день робочий -> full
        var today = (await Days(T(7, 17), Mon, 1))[0];
        Assert.Equal(DayStatus.Full, today.Status);
        Assert.Equal(0, today.FreeSlots);
    }

    [Fact]
    public async Task fully_booked_working_day_is_full()
    {
        foreach (var start in new[] { (9, 0), (10, 30), (12, 0), (13, 30), (15, 0), (16, 30) })
            _store.Seed(T(8, start.Item1, start.Item2), "confirmed"); // вівторок 09:00-18:00 без проміжків
        var days = await Days(T(7, 0), Mon, 2);
        Assert.Equal(DayStatus.Available, days[0].Status);
        Assert.Equal(DayStatus.Full, days[1].Status);
        Assert.Equal(0, days[1].FreeSlots);
    }

    [Fact]
    public async Task cancelled_appointment_does_not_make_the_day_full()
    {
        foreach (var start in new[] { (9, 0), (10, 30), (12, 0), (13, 30), (15, 0), (16, 30) })
            _store.Seed(T(8, start.Item1, start.Item2), "cancelled");
        Assert.Equal(DayStatus.Available, (await Days(T(7, 0), Mon.AddDays(1), 1))[0].Status);
    }

    [Fact]
    public async Task weekly_day_off_closure_and_absence_make_the_day_closed()
    {
        _store.ClosedWeekdays = ["mon"];
        _store.Closures = [new AbsenceSpan(Mon.AddDays(1), Mon.AddDays(1))];
        var days = await Days(T(7, 0), Mon, 2);
        Assert.All(days, d => Assert.Equal(DayStatus.Closed, d.Status));

        _store.ClosedWeekdays = null;
        _store.Closures = null;
        _store.Absences = [new AbsenceSpan(Mon, Mon)]; // лікарняний/вихідний майстра
        var withAbsence = await Days(T(7, 0), Mon, 2);
        Assert.Equal([DayStatus.Closed, DayStatus.Available], withAbsence.Select(d => d.Status));
    }

    [Fact]
    public async Task service_not_assigned_to_the_specialist_makes_days_closed()
    {
        _store.AssignedServices = [Guid.NewGuid()];
        Assert.All(await Days(T(7, 0), Mon, 2), d => Assert.Equal(DayStatus.Closed, d.Status));
    }

    [Fact]
    public async Task location_hours_restrict_the_day_and_can_close_it()
    {
        _store.LocationHours = """{"mon":[{"from":"12:00","to":"18:00"}]}""";
        var days = await Days(T(7, 0), Mon, 2);
        Assert.Equal(DayStatus.Available, days[0].Status);
        Assert.Equal(await SlotsCount(T(7, 0), Mon), days[0].FreeSlots);
        Assert.Equal(DayStatus.Closed, days[1].Status); // вівторок не в годинах закладу
    }

    [Fact]
    public async Task with_and_without_specialist_id_use_the_same_rules_and_unknown_specialist_gives_closed_days()
    {
        var now = T(7, 0);
        var any = await Days(now, Mon, 2);
        var one = await Days(now, Mon, 2, FakeBookingStore.Specialist);
        Assert.Equal(any, one);
        Assert.All(await Days(now, Mon, 2, Guid.NewGuid()), d => Assert.Equal(DayStatus.Closed, d.Status));
    }

    [Fact]
    public async Task default_range_and_horizon_are_respected()
    {
        var now = T(7, 0);
        var r = await Sut(now).GetAvailabilityAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, null, 31, 180, default);
        Assert.Equal(31, r.Value!.Count);
        Assert.Equal(Mon, r.Value[0].Date); // from за замовчуванням = сьогодні в зоні закладу

        var edge = await Sut(now).GetAvailabilityAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Mon.AddDays(178), 10, 180, default);
        Assert.Equal(3, edge.Value!.Count); // дні далі за горизонт запису відкидаються
    }

    [Fact]
    public async Task validation_and_not_found()
    {
        var sut = Sut(T(7, 0));
        Task<Result<IReadOnlyList<DayAvailability>>> Call(Guid loc, Guid svc, DateOnly? from, int days) =>
            sut.GetAvailabilityAsync(loc, null, svc, from, days, 180, default);

        Assert.Equal("invalid_days", (await Call(FakeBookingStore.Location, FakeBookingStore.Service90, Mon, 0)).Error!.Code);
        Assert.Equal("invalid_days", (await Call(FakeBookingStore.Location, FakeBookingStore.Service90, Mon, 63)).Error!.Code);
        Assert.Equal("invalid_date", (await Call(FakeBookingStore.Location, FakeBookingStore.Service90, Mon.AddDays(-2), 5)).Error!.Code);
        Assert.Equal("invalid_date", (await Call(FakeBookingStore.Location, FakeBookingStore.Service90, Mon.AddDays(181), 5)).Error!.Code);
        Assert.Equal(ErrorKind.NotFound, (await Call(Guid.NewGuid(), FakeBookingStore.Service90, Mon, 5)).Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, (await Call(FakeBookingStore.Location, Guid.NewGuid(), Mon, 5)).Error!.Kind);
    }

    [Fact]
    public async Task today_is_computed_in_the_location_time_zone()
    {
        _store.Timezone = "Pacific/Auckland"; // UTC+13 у січні: 2030-01-06 22:00 UTC вже 07 січня 11:00 локально
        var now = new DateTimeOffset(2030, 1, 6, 22, 0, 0, TimeSpan.Zero);
        var days = await Days(now, new DateOnly(2030, 1, 6), 2);
        Assert.Equal(DayStatus.Past, days[0].Status);
        Assert.Equal(new DateOnly(2030, 1, 7), days[1].Date);
        Assert.Equal(DayStatus.Available, days[1].Status);
    }
}
