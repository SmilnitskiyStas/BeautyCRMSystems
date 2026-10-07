using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Tests.Beauty;

public class CancellationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly CancellationSettings Def = CancellationSettings.Defaults;

    private static RefundCalculation Calc(double hoursBefore, double paid, CancellationSettings s) =>
        CancellationPolicy.Calculate(Now.AddHours(hoursBefore), Now, (decimal)paid, s);

    [Theory]
    [InlineData(48, 1000, 1000, 100)]
    [InlineData(12.01, 1000, 1000, 100)]
    [InlineData(12, 1000, 500, 50)] // межа: рівно windowHours -> в межах вікна
    [InlineData(6, 1000, 500, 50)]
    [InlineData(0.5, 801, 400.5, 50)]
    [InlineData(-1, 1000, 500, 50)] // візит уже почався
    public void calculate_defaults_return_refund_by_window(double hoursBefore, double paid, double expected, int percent)
    {
        var r = Calc(hoursBefore, paid, Def);
        Assert.Equal((decimal)expected, r.Amount);
        Assert.Equal(percent, r.Percent);
        Assert.Equal(0, r.FeePercent);
    }

    [Fact]
    public void calculate_returns_zero_when_nothing_paid() =>
        Assert.Equal(new RefundCalculation(0m, 50, 0), CancellationPolicy.Calculate(Now.AddHours(1), Now, 0m, Def));

    [Theory]
    // (години до візиту, deductFee, feePercent) -> сума з 1000 при in=50 / out=100
    [InlineData(6, false, 10, 500.00, 50, 0)] // feePercent ігнорується при deductFee=false
    [InlineData(6, true, 10, 450.00, 50, 10)] // 500 - 10%
    [InlineData(48, false, 10, 1000.00, 100, 0)]
    [InlineData(48, true, 10, 900.00, 100, 10)]
    [InlineData(12, true, 20, 400.00, 50, 20)] // межа вікна + комісія
    [InlineData(12.01, true, 20, 800.00, 100, 20)] // одразу за межею
    [InlineData(6, true, 0, 500.00, 50, 0)] // комісія 0%
    [InlineData(6, true, 100, 0.00, 50, 100)] // комісія 100% -> нічого не повертається
    public void calculate_applies_fee_only_when_deductFee(double hours, bool deduct, int fee, double expected, int percent, int appliedFee)
    {
        var r = Calc(hours, 1000, new CancellationSettings(12, 50, 100, deduct, fee));
        Assert.Equal((decimal)expected, r.Amount);
        Assert.Equal(percent, r.Percent);
        Assert.Equal(appliedFee, r.FeePercent);
    }

    [Theory]
    [InlineData(0, 100, 0, 1000)] // 0% всередині, 100% поза
    [InlineData(100, 100, 1000, 1000)] // 100% всередині й поза
    [InlineData(100, 0, 1000, 0)] // 0% поза вікном
    [InlineData(0, 0, 0, 0)]
    public void calculate_handles_0_and_100_percent(int inWindow, int outside, double expectedIn, double expectedOut)
    {
        var s = new CancellationSettings(12, inWindow, outside, false, 0);
        Assert.Equal((decimal)expectedIn, Calc(1, 1000, s).Amount);
        Assert.Equal((decimal)expectedOut, Calc(100, 1000, s).Amount);
    }

    [Fact]
    public void calculate_uses_custom_window_boundary_inclusively()
    {
        var s = new CancellationSettings(24, 30, 90, false, 0);
        Assert.Equal(300m, Calc(24, 1000, s).Amount);
        Assert.Equal(900m, Calc(24.01, 1000, s).Amount);
    }

    [Fact]
    public void calculate_zero_window_is_inside_only_when_visit_started_or_now()
    {
        var s = new CancellationSettings(0, 0, 100, false, 0);
        Assert.Equal(0m, Calc(0, 1000, s).Amount);
        Assert.Equal(1000m, Calc(0.01, 1000, s).Amount);
    }

    [Fact]
    public void calculate_rounds_down_to_cents()
    {
        // 0.03 * 50% = 0.015 -> 0.01 (вниз, а не від нуля як раніше)
        Assert.Equal(0.01m, Calc(1, 0.03, Def).Amount);
        // 100.01 * 50% * 85% = 42.504250 -> 42.50
        Assert.Equal(42.50m, CancellationPolicy.Calculate(Now.AddHours(1), Now, 100.01m, new CancellationSettings(12, 50, 100, true, 15)).Amount);
        // 0.99 * 100% * 99% = 0.9801 -> 0.98
        Assert.Equal(0.98m, CancellationPolicy.Calculate(Now.AddHours(48), Now, 0.99m, new CancellationSettings(12, 50, 100, true, 1)).Amount);
    }
}

public class CancellationServiceTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeBookingStore _store = new();
    private readonly SpyPayments _pay = new();
    private CancellationService Sut(CancellationSettings? s = null) => new(_store, _pay, FakeSettings.Service(s), new FakeClock(Now));

    [Fact]
    public async Task cancel_refunds_50_percent_when_within_12_hours()
    {
        var a = _store.Seed(Now.AddHours(10));
        var r = await Sut().CancelAsync(a.Id, default);
        Assert.True(r.IsOk);
        Assert.Equal(500m, r.Value!.RefundAmount);
        Assert.Equal(50, r.Value.RefundPercent);
        Assert.Equal("cancelled", r.Value.Appointment.Status);
        Assert.Equal([500m], _pay.Refunds);
        Assert.Equal("refunded", _store.Payments[a.Id].Status);
    }

    [Fact]
    public async Task cancel_refunds_in_full_when_more_than_12_hours()
    {
        var a = _store.Seed(Now.AddHours(30));
        var r = await Sut().CancelAsync(a.Id, default);
        Assert.Equal(1000m, r.Value!.RefundAmount);
    }

    [Fact]
    public async Task cancel_uses_tenant_settings_with_fee()
    {
        var a = _store.Seed(Now.AddHours(10));
        var r = await Sut(new CancellationSettings(12, 50, 100, true, 10)).CancelAsync(a.Id, default);
        Assert.Equal(450m, r.Value!.RefundAmount);
        Assert.Equal(50, r.Value.RefundPercent);
        Assert.Equal(10, r.Value.FeePercent);
        Assert.Equal([450m], _pay.Refunds);
    }

    [Fact]
    public async Task cancel_with_custom_window_treats_30h_as_inside()
    {
        var a = _store.Seed(Now.AddHours(30));
        var r = await Sut(new CancellationSettings(48, 20, 100, false, 0)).CancelAsync(a.Id, default);
        Assert.Equal(200m, r.Value!.RefundAmount);
    }

    [Fact]
    public async Task cancel_with_zero_percent_in_window_skips_refund_call()
    {
        var a = _store.Seed(Now.AddHours(1));
        var r = await Sut(new CancellationSettings(12, 0, 100, false, 0)).CancelAsync(a.Id, default);
        Assert.True(r.IsOk);
        Assert.Equal(0m, r.Value!.RefundAmount);
        Assert.Empty(_pay.Refunds);
        Assert.Equal("paid", _store.Payments[a.Id].Status); // коштів не повернуто
        Assert.Equal("cancelled", r.Value.Appointment.Status);
    }

    [Fact]
    public async Task cancel_does_not_refund_cash_payment_not_taken()
    {
        var a = _store.Seed(Now.AddHours(1), method: "cash", paymentStatus: "pending");
        var r = await Sut().CancelAsync(a.Id, default);
        Assert.True(r.IsOk);
        Assert.Equal(0m, r.Value!.RefundAmount);
        Assert.Empty(_pay.Refunds);
    }

    [Fact]
    public async Task cancel_frees_the_slot_for_the_specialist()
    {
        var a = _store.Seed(Now.AddHours(1));
        await Sut().CancelAsync(a.Id, default);
        var busy = await _store.GetBusyAsync([a.SpecialistId], a.StartsAt, a.EndsAt, null, default);
        Assert.Empty(busy);
    }

    [Fact]
    public async Task cancel_returns_not_found_for_unknown_appointment()
    {
        var r = await Sut().CancelAsync(Guid.NewGuid(), default);
        Assert.Equal(ErrorKind.NotFound, r.Error!.Kind);
    }

    [Fact]
    public async Task cancel_returns_conflict_when_already_cancelled()
    {
        var a = _store.Seed(Now.AddHours(1), status: "cancelled");
        var r = await Sut().CancelAsync(a.Id, default);
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
    }

    [Fact]
    public async Task cancel_returns_validation_when_completed()
    {
        var a = _store.Seed(Now.AddHours(-3), status: "completed");
        var r = await Sut().CancelAsync(a.Id, default);
        Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
    }

    [Fact]
    public async Task cancel_keeps_appointment_when_refund_fails()
    {
        _pay.RefundOk = false;
        var a = _store.Seed(Now.AddHours(1));
        var r = await Sut().CancelAsync(a.Id, default);
        Assert.Equal(ErrorKind.PaymentFailed, r.Error!.Kind);
        Assert.Equal("confirmed", _store.Appointments.Single().Status);
    }
}

public class CancellationSettingsServiceTests
{
    private readonly FakeCancellationSettingsStore _store = new();
    private CancellationSettingsService Sut() => new(_store);

    private static UpdateCancellationSettingsRequest Req(int? w = 12, int? i = 50, int? o = 100, bool? d = false, int? f = 0) => new(w, i, o, d, f);

    [Fact]
    public async Task get_returns_defaults_when_row_missing()
    {
        var s = await Sut().GetAsync(default);
        Assert.Equal(new CancellationSettings(12, 50, 100, false, 0), s);
    }

    [Fact]
    public async Task update_saves_and_get_returns_saved()
    {
        var r = await Sut().UpdateAsync(Req(24, 30, 90, true, 15), default);
        Assert.True(r.IsOk);
        Assert.Equal(new CancellationSettings(24, 30, 90, true, 15), await Sut().GetAsync(default));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(720, 100, 100, 100)]
    public async Task update_accepts_boundary_values(int w, int i, int o, int f) =>
        Assert.True((await Sut().UpdateAsync(Req(w, i, o, true, f), default)).IsOk);

    [Theory]
    [InlineData(-1, 50, 100, 0, "invalid_window_hours")]
    [InlineData(721, 50, 100, 0, "invalid_window_hours")]
    [InlineData(12, -1, 100, 0, "invalid_refund_percent")]
    [InlineData(12, 101, 100, 0, "invalid_refund_percent")]
    [InlineData(12, 50, -1, 0, "invalid_refund_percent")]
    [InlineData(12, 50, 101, 0, "invalid_refund_percent")]
    [InlineData(12, 50, 100, -1, "invalid_fee_percent")]
    [InlineData(12, 50, 100, 101, "invalid_fee_percent")]
    public async Task update_rejects_out_of_range_values_with_validation_error(int w, int i, int o, int f, string code)
    {
        var r = await Sut().UpdateAsync(Req(w, i, o, true, f), default);
        Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
        Assert.Equal(code, r.Error.Code);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task update_rejects_fee_out_of_range_even_when_deductFee_off()
    {
        var r = await Sut().UpdateAsync(Req(f: 150, d: false), default);
        Assert.Equal("invalid_fee_percent", r.Error!.Code);
    }

    [Fact]
    public async Task update_rejects_missing_fields_instead_of_defaulting_to_zero()
    {
        foreach (var req in new[] { Req(w: null), Req(i: null), Req(o: null), Req(d: null), Req(f: null) })
            Assert.Equal("settings_incomplete", (await Sut().UpdateAsync(req, default)).Error!.Code);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task terms_hide_feePercent_when_deductFee_off()
    {
        var sut = Sut();
        await sut.UpdateAsync(Req(d: false, f: 20), default);
        Assert.Equal(new CancellationTerms(12, 50, 100, false, 0), await sut.GetTermsAsync(default));
    }

    [Fact]
    public async Task slots_and_appointments_carry_cancellation_terms()
    {
        var store = new FakeBookingStore();
        var sut = new BookingService(store, new SpyPayments(), FakeSettings.Service(new CancellationSettings(6, 25, 100, true, 10)),
            new FakeClock(new DateTimeOffset(2030, 1, 6, 8, 0, 0, TimeSpan.Zero)));
        var expected = new CancellationTerms(6, 25, 100, true, 10);

        var slots = await sut.GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, new DateOnly(2030, 1, 7), default);
        Assert.NotEmpty(slots.Value!);
        Assert.All(slots.Value!, s => Assert.Equal(expected, s.Cancellation));

        var start = new DateTimeOffset(2030, 1, 7, 10, 0, 0, TimeSpan.Zero);
        var created = await sut.CreateAsync(new CreateAppointmentRequest(FakeBookingStore.Location, FakeBookingStore.Specialist,
            FakeBookingStore.Service90, start, new ClientInput(null, "Олена", "+380501112233", null), "none", "cash", null), default);
        Assert.True(created.IsOk);
        Assert.Equal(expected, created.Value!.Cancellation);
        Assert.Equal(expected, (await sut.GetAsync(created.Value.Id, default)).Value!.Cancellation);
        var list = await sut.ListAsync(start.AddDays(-1), start.AddDays(1), null, null, default);
        Assert.NotEmpty(list);
        Assert.All(list, a => Assert.Equal(expected, a.Cancellation));
    }
}
