using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Tests.Beauty;

public class BookingServiceTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero); // неділя
    private static readonly DateOnly Monday = new(2030, 1, 7);

    private readonly FakeBookingStore _store = new();
    private readonly SpyPayments _pay = new();
    private BookingService Sut() => new(_store, _pay, FakeSettings.Service(), new FakeClock(Now));

    private static DateTimeOffset T(int h, int m = 0) => new(2030, 1, 7, h, m, 0, TimeSpan.Zero);

    private static CreateAppointmentRequest Req(DateTimeOffset start, string reminder = "none", string pay = "cash", string? source = null) =>
        new(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90, start,
            new ClientInput(null, "Олена", "+380501112233", null), reminder, pay, source);

    // ---- слоти ----

    [Fact]
    public async Task getSlots_takes_duration_from_service()
    {
        _store.ServiceDuration = 45;
        var r = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Monday, default);
        Assert.All(r.Value!, s => Assert.Equal(TimeSpan.FromMinutes(45), s.EndsAt - s.StartsAt));
    }

    [Fact]
    public async Task getSlots_excludes_overlap_with_booked_appointment()
    {
        await Sut().CreateAsync(Req(T(10)), default);
        var r = await Sut().GetSlotsAsync(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90, Monday, default);
        Assert.DoesNotContain(r.Value!, s => s.StartsAt < T(11, 30) && T(10) < s.EndsAt);
    }

    [Fact]
    public async Task getSlots_returns_not_found_for_unknown_service()
    {
        var r = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, Guid.NewGuid(), Monday, default);
        Assert.Equal(ErrorKind.NotFound, r.Error!.Kind);
    }

    [Fact]
    public async Task getSlots_returns_empty_when_specialist_not_at_location()
    {
        _store.SpecialistAtLocation = false;
        var r = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Monday, default);
        Assert.Empty(r.Value!);
    }

    // ---- створення ----

    [Fact]
    public async Task create_persists_appointment_visible_in_calendar_with_service_duration()
    {
        var r = await Sut().CreateAsync(Req(T(10), source: "admin"), default);
        Assert.True(r.IsOk);
        Assert.Equal(90, r.Value!.DurationMinutes);
        Assert.Equal(T(11, 30), r.Value.EndsAt);
        Assert.Equal("confirmed", r.Value.Status);
        Assert.Equal("admin", r.Value.Source);

        var calendar = await Sut().ListAsync(T(0), T(0).AddDays(1), null, null, default);
        Assert.Contains(calendar, a => a.Id == r.Value.Id);
    }

    [Fact]
    public async Task create_returns_conflict_when_slot_overlaps()
    {
        Assert.True((await Sut().CreateAsync(Req(T(10)), default)).IsOk);
        var r = await Sut().CreateAsync(Req(T(11)), default); // 11:00 < 11:30
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.Equal("slot_unavailable", r.Error.Code);
    }

    [Fact]
    public async Task create_allows_back_to_back_appointments()
    {
        await Sut().CreateAsync(Req(T(10)), default);
        Assert.True((await Sut().CreateAsync(Req(T(11, 30)), default)).IsOk);
    }

    [Fact]
    public async Task create_returns_conflict_when_store_reports_overlap_race()
    {
        // гонка: перевірка слота пройшла, але exclusion constraint БД спрацював
        var racing = new RacingStore(_store);
        var sut = new BookingService(racing, _pay, FakeSettings.Service(), new FakeClock(Now));
        var r = await sut.CreateAsync(Req(T(10)), default);
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
    }

    [Fact]
    public async Task create_rejects_time_outside_working_hours_and_past()
    {
        var outside = await Sut().CreateAsync(Req(T(17)), default); // 17:00 + 90 хв > 18:00
        Assert.Equal(ErrorKind.Validation, outside.Error!.Kind);
        var past = await new BookingService(_store, _pay, FakeSettings.Service(), new FakeClock(T(12))).CreateAsync(Req(T(10)), default);
        Assert.Equal("slot_in_past", past.Error!.Code);
    }

    [Fact]
    public async Task create_validates_enums_and_client_at_the_boundary()
    {
        Assert.Equal("invalid_reminder", (await Sut().CreateAsync(Req(T(10), reminder: "3h"), default)).Error!.Code);
        Assert.Equal("invalid_payment_method", (await Sut().CreateAsync(Req(T(10), pay: "crypto"), default)).Error!.Code);
        Assert.Equal("invalid_source", (await Sut().CreateAsync(Req(T(10), source: "fax"), default)).Error!.Code);
        var noClient = Req(T(10)) with { Client = new ClientInput(null, "Олена", null, null) };
        Assert.Equal("client_required", (await Sut().CreateAsync(noClient, default)).Error!.Code);
    }

    [Fact]
    public async Task create_returns_not_found_for_unknown_client_id()
    {
        _store.ClientExists = false;
        var r = await Sut().CreateAsync(Req(T(10)) with { Client = new ClientInput(Guid.NewGuid(), null, null, null) }, default);
        Assert.Equal(ErrorKind.NotFound, r.Error!.Kind);
    }

    [Fact]
    public async Task create_applies_promotion_to_final_price()
    {
        var promo = new PromotionRule(Guid.NewGuid(), "-20%", "percent", 20, null, null, true,
            [FakeBookingStore.Location], [FakeBookingStore.Service90]);
        _store.Promotions.Add(promo);
        var r = await Sut().CreateAsync(Req(T(10)), default);
        Assert.Equal(1000m, r.Value!.PriceOriginal);
        Assert.Equal(800m, r.Value.PriceFinal);
        Assert.Equal(promo.Id, r.Value.PromotionId);
    }

    [Fact]
    public async Task create_requires_a_price()
    {
        _store.Price = null;
        Assert.Equal("price_not_set", (await Sut().CreateAsync(Req(T(10)), default)).Error!.Code);
    }

    [Theory]
    [InlineData("1h", 9, 0)]
    [InlineData("2h", 8, 0)]
    public async Task create_schedules_reminder_before_visit(string option, int hour, int minute)
    {
        var r = await Sut().CreateAsync(Req(T(10), option), default);
        Assert.Equal(T(hour, minute), Assert.Single(_store.Reminders, x => x.AppointmentId == r.Value!.Id).At);
    }

    [Fact]
    public async Task create_does_not_schedule_reminder_for_none_or_too_late()
    {
        await Sut().CreateAsync(Req(T(10), "none"), default);
        Assert.Empty(_store.Reminders);
        var late = new BookingService(_store, _pay, FakeSettings.Service(), new FakeClock(T(9, 30)));
        await late.CreateAsync(Req(T(10), "1h"), default); // 09:00 уже минуло
        Assert.Empty(_store.Reminders);
    }

    // ---- оплата ----

    [Fact]
    public async Task create_charges_card_through_payment_service_and_marks_paid()
    {
        var r = await Sut().CreateAsync(Req(T(10), pay: "card"), default);
        Assert.Equal([1000m], _pay.Charges);
        Assert.Equal("paid", _store.Payments[r.Value!.Id].Status);
        Assert.Equal("confirmed", r.Value.Status);
    }

    [Fact]
    public async Task create_does_not_charge_for_cash()
    {
        var r = await Sut().CreateAsync(Req(T(10), pay: "cash"), default);
        Assert.Empty(_pay.Charges);
        Assert.Equal("pending", _store.Payments[r.Value!.Id].Status);
    }

    [Fact]
    public async Task create_cancels_appointment_when_card_charge_fails()
    {
        _pay.ChargeOk = false;
        var r = await Sut().CreateAsync(Req(T(10), pay: "card"), default);
        Assert.Equal(ErrorKind.PaymentFailed, r.Error!.Kind);
        Assert.Equal("cancelled", _store.Appointments.Single().Status);
        // слот знову вільний
        Assert.True((await Sut().CreateAsync(Req(T(10)), default)).IsOk);
    }

    // ---- перенос / статус ----

    [Fact]
    public async Task patch_reschedules_and_replans_reminder()
    {
        var a = (await Sut().CreateAsync(Req(T(10), "1h"), default)).Value!;
        var r = await Sut().PatchAsync(a.Id, new PatchAppointmentRequest(T(14), null), default);
        Assert.Equal(T(14), r.Value!.StartsAt);
        Assert.Equal(T(15, 30), r.Value.EndsAt);
        Assert.Equal(T(13), Assert.Single(_store.Reminders).At);
    }

    [Fact]
    public async Task patch_returns_conflict_when_new_time_overlaps_other_appointment()
    {
        await Sut().CreateAsync(Req(T(14)), default);
        var a = (await Sut().CreateAsync(Req(T(10)), default)).Value!;
        var r = await Sut().PatchAsync(a.Id, new PatchAppointmentRequest(T(15), null), default);
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
    }

    [Fact]
    public async Task patch_allows_shifting_into_own_previous_time()
    {
        var a = (await Sut().CreateAsync(Req(T(10)), default)).Value!;
        Assert.True((await Sut().PatchAsync(a.Id, new PatchAppointmentRequest(T(11), null), default)).IsOk); // перетин лише із собою
    }

    [Fact]
    public async Task patch_changes_status_but_not_to_cancelled()
    {
        var a = (await Sut().CreateAsync(Req(T(10)), default)).Value!;
        Assert.Equal("completed", (await Sut().PatchAsync(a.Id, new PatchAppointmentRequest(null, "completed"), default)).Value!.Status);
        Assert.Equal("invalid_status", (await Sut().PatchAsync(a.Id, new PatchAppointmentRequest(null, "cancelled"), default)).Error!.Code);
    }

    [Fact]
    public async Task patch_returns_not_found_and_conflict_for_closed()
    {
        Assert.Equal(ErrorKind.NotFound, (await Sut().PatchAsync(Guid.NewGuid(), new PatchAppointmentRequest(T(10), null), default)).Error!.Kind);
        var done = _store.Seed(T(10), status: "cancelled");
        Assert.Equal(ErrorKind.Conflict, (await Sut().PatchAsync(done.Id, new PatchAppointmentRequest(T(12), null), default)).Error!.Kind);
    }

    private sealed class RacingStore(FakeBookingStore inner) : IBookingStore
    {
        public Task<ServiceInfo?> GetServiceAsync(Guid id, CancellationToken ct) => inner.GetServiceAsync(id, ct);
        public Task<LocationInfo?> GetLocationAsync(Guid id, CancellationToken ct) => inner.GetLocationAsync(id, ct);
        public Task<IReadOnlyList<SpecialistSchedule>> GetSchedulesAsync(Guid l, Guid? s, CancellationToken ct) => inner.GetSchedulesAsync(l, s, ct);
        public Task<IReadOnlyList<(Guid SpecialistId, TimeRange Range)>> GetBusyAsync(IReadOnlyCollection<Guid> i, DateTimeOffset f, DateTimeOffset t, Guid? e, CancellationToken ct) => inner.GetBusyAsync(i, f, t, e, ct);
        public Task<decimal?> GetPriceAsync(Guid s, Guid l, CancellationToken ct) => inner.GetPriceAsync(s, l, ct);
        public Task<IReadOnlyList<PromotionRule>> GetPromotionRulesAsync(CancellationToken ct) => inner.GetPromotionRulesAsync(ct);
        public Task<Guid?> ResolveClientAsync(ClientInput c, CancellationToken ct) => inner.ResolveClientAsync(c, ct);
        public Task<StoreResult<AppointmentDto>> AddAppointmentAsync(NewAppointment a, CancellationToken ct) => Task.FromResult(StoreResult<AppointmentDto>.Overlap());
        public Task<AppointmentDto?> GetAppointmentAsync(Guid id, CancellationToken ct) => inner.GetAppointmentAsync(id, ct);
        public Task<IReadOnlyList<AppointmentDto>> ListAppointmentsAsync(DateTimeOffset f, DateTimeOffset t, Guid? l, Guid? s, CancellationToken ct) => inner.ListAppointmentsAsync(f, t, l, s, ct);
        public Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset n, DateTimeOffset? r, CancellationToken ct) => inner.RescheduleAsync(id, n, r, ct);
        public Task<AppointmentDto?> SetStatusAsync(Guid id, string s, CancellationToken ct) => inner.SetStatusAsync(id, s, ct);
        public Task<bool> TryClaimCancelAsync(Guid id, DateTimeOffset at, CancellationToken ct) => inner.TryClaimCancelAsync(id, at, ct);
        public Task ReleaseCancelAsync(Guid id, string previousStatus, CancellationToken ct) => inner.ReleaseCancelAsync(id, previousStatus, ct);
        public Task<AppointmentDto?> MarkCancelledAsync(Guid id, DateTimeOffset at, CancellationToken ct) => inner.MarkCancelledAsync(id, at, ct);
        public Task<PaymentRecord?> GetPaymentAsync(Guid id, CancellationToken ct) => inner.GetPaymentAsync(id, ct);
        public Task UpdatePaymentAsync(Guid id, string s, string? p, DateTimeOffset? a, CancellationToken ct) => inner.UpdatePaymentAsync(id, s, p, a, ct);
    }
}
