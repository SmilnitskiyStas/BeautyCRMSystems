using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Tests.Beauty;
using BeautyCrm.Tests.ChatBooking;

namespace BeautyCrm.Tests.Reminders;

internal sealed class FakeReminderContext : IReminderContextStore
{
    public ReminderSettings Settings = ReminderSettings.Default;
    public bool PhoneAvailable = true;
    public bool TelegramAvailable;
    public ClientReminderPreference? Preference;
    public bool ClientFound = true;

    public Task<ReminderSettings> GetSettingsAsync(CancellationToken ct) => Task.FromResult(Settings);
    public Task<ReminderClientContext> GetClientContextAsync(ClientInput client, CancellationToken ct) =>
        Task.FromResult(new ReminderClientContext(PhoneAvailable, TelegramAvailable, Preference, ClientFound));
}

/// <summary>TASK-737 (§22.4, §22.6.1, §22.12 сценарії 1, 2, 13, 21): ReminderPlanner у BookingService / ChatBookingService.</summary>
public class ReminderBookingServiceTests
{
    // Неділя 2030-01-06 08:00 UTC; графік FakeBookingStore: пн-вт 09:00-18:00 UTC (зона закладу UTC).
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(int h, int m = 0, int day = 7) => new(2030, 1, day, h, m, 0, TimeSpan.Zero);

    private readonly FakeBookingStore _store = new();
    private readonly FakeReminderContext _ctx = new();
    private BookingService Sut(DateTimeOffset? now = null, bool withContext = true) =>
        new(_store, new SpyPayments(), FakeSettings.Service(), new FakeClock(now ?? Now), withContext ? _ctx : null);

    private static CreateAppointmentRequest Req(
        DateTimeOffset start, string? reminder = null, ReminderPlanRequest? reminders = null, Guid? clientId = null, string? phone = "+380501112233") =>
        new(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90, start,
            new ClientInput(clientId, clientId is null ? "Олена" : null, clientId is null ? phone : null, null), reminder, "cash", null, Reminders: reminders);

    private PlannedReminders PlanOf(Result<AppointmentDto> r) => _store.Plans[r.Value!.Id];

    // ---------- сценарій 1: кілька точок, дзеркало ----------

    [Fact]
    public async Task staff_telegram_plan_with_two_offsets_creates_two_points_and_a_custom_mirror()
    {
        _ctx.TelegramAvailable = true;
        var r = await Sut().CreateAsync(Req(T(14), reminders: new("telegram", [1440, 120])), default);

        Assert.True(r.IsOk, r.Error?.Code);
        var plan = PlanOf(r);
        Assert.Equal("telegram", plan.Method);
        Assert.Equal([(short)120, (short)1440], plan.Offsets);
        Assert.Equal(2, plan.Points.Count);
        Assert.Equal(T(12), plan.Points.Single(p => p.OffsetMinutes == 120).DueAt);
        Assert.Equal(T(14, day: 6), plan.Points.Single(p => p.OffsetMinutes == 1440).DueAt);
        Assert.Equal("custom", r.Value!.ReminderOption);
        Assert.Equal("telegram", r.Value.Reminders!.Method);
        Assert.Equal([120, 1440], r.Value.Reminders.OffsetsMinutes);
    }

    [Theory]
    [InlineData(60, "1h")]
    [InlineData(120, "2h")]
    public async Task staff_single_standard_offset_keeps_the_legacy_mirror(int offset, string mirror)
    {
        _ctx.TelegramAvailable = true;
        var r = await Sut().CreateAsync(Req(T(14), reminders: new("telegram", [offset])), default);
        Assert.Equal(mirror, r.Value!.ReminderOption);
    }

    [Fact]
    public async Task staff_none_plan_creates_no_points_and_the_none_mirror()
    {
        var r = await Sut().CreateAsync(Req(T(14), reminders: new("none", [])), default);
        Assert.Equal("none", r.Value!.ReminderOption);
        Assert.Empty(PlanOf(r).Points);
        Assert.Empty(_store.Reminders);
    }

    [Fact]
    public async Task staff_call_plan_creates_one_call_point_without_message_rows()
    {
        var r = await Sut().CreateAsync(Req(T(14), reminders: new("call", [1440])), default);
        var plan = PlanOf(r);
        var p = Assert.Single(plan.Points);
        Assert.Equal(ReminderPointKind.Call, p.Kind);
        Assert.Equal(T(14, day: 6), p.DueAt);
        Assert.Equal("custom", r.Value!.ReminderOption);
        Assert.Empty(_store.Reminders); // legacy-точка лише для повідомлень
    }

    [Fact]
    public async Task plan_uses_the_location_timezone_for_quiet_hours()
    {
        _store.Timezone = "Europe/Kyiv"; // зима: UTC+2; графік закладу в локальному часі
        _store.WorkingHours = """{"mon":[{"from":"09:00","to":"18:00"}],"tue":[{"from":"09:00","to":"18:00"}]}""";
        _ctx.TelegramAvailable = true;
        var start = new DateTimeOffset(2030, 1, 7, 7, 30, 0, TimeSpan.Zero); // 09:30 Kyiv
        var r = await Sut().CreateAsync(Req(start, reminders: new("telegram", [120])), default);
        Assert.True(r.IsOk, r.Error?.Code);
        var p = Assert.Single(PlanOf(r).Points);
        Assert.Equal(new DateTimeOffset(2030, 1, 7, 6, 0, 0, TimeSpan.Zero), p.DueAt); // 08:00 Kyiv
    }

    // ---------- сценарій 2: сумісність ----------

    [Theory]
    [InlineData("1h", 60)]
    [InlineData("2h", 120)]
    public async Task legacy_reminder_becomes_an_auto_plan(string legacy, int offset)
    {
        var r = await Sut().CreateAsync(Req(T(14), legacy), default);
        var plan = PlanOf(r);
        Assert.Equal("auto", plan.Method);
        Assert.Equal([(short)offset], plan.Offsets);
        Assert.Equal(legacy, r.Value!.ReminderOption);
        Assert.Equal(T(14).AddMinutes(-offset), Assert.Single(_store.Reminders).At);
    }

    [Fact]
    public async Task legacy_none_ignores_the_client_preference()
    {
        _ctx.Preference = new("telegram", [60]);
        var r = await Sut().CreateAsync(Req(T(14), "none"), default);
        Assert.Equal("none", PlanOf(r).Method);
        Assert.Empty(PlanOf(r).Points);
    }

    [Fact]
    public async Task reminders_together_with_reminder_is_a_validation_error_reminder_conflict()
    {
        var r = await Sut().CreateAsync(Req(T(14), "1h", new("telegram", [60])), default);
        Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
        Assert.Equal("reminder_conflict", r.Error.Code);
        Assert.Empty(_store.Appointments);
    }

    [Fact]
    public async Task unknown_legacy_reminder_is_still_rejected_and_null_is_allowed()
    {
        Assert.Equal("invalid_reminder", (await Sut().CreateAsync(Req(T(14), "3h"), default)).Error!.Code);
        Assert.True((await Sut().CreateAsync(Req(T(14)), default)).IsOk);
    }

    // ---------- валідація reminders (422) ----------

    [Theory]
    [InlineData("sms", new[] { 60 }, "invalid_reminder_method")]
    [InlineData("auto", new[] { 60 }, "invalid_reminder_method")]
    [InlineData("telegram", new int[] { }, "invalid_reminder_offsets")]
    [InlineData("telegram", new[] { 45 }, "invalid_reminder_offsets")]
    [InlineData("call", new[] { 60, 120 }, "invalid_reminder_offsets")]
    [InlineData("none", new[] { 60 }, "invalid_reminder_offsets")]
    public async Task invalid_reminders_are_422_and_checked_before_the_slot(string method, int[] offsets, string code)
    {
        var r = await Sut().CreateAsync(Req(T(10, day: 5), reminders: new(method, offsets)), default); // слот у минулому
        Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
        Assert.Equal(code, r.Error.Code);
    }

    [Fact]
    public async Task client_required_is_checked_before_reminders()
    {
        var req = new CreateAppointmentRequest(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90, T(14),
            new ClientInput(null, null, null, null), null, "cash", null, Reminders: new("sms", []));
        Assert.Equal("client_required", (await Sut().CreateAsync(req, default)).Error!.Code);
    }

    // ---------- 409 ----------

    [Fact]
    public async Task telegram_without_a_telegram_chat_is_409_telegram_unavailable()
    {
        var r = await Sut().CreateAsync(Req(T(14), reminders: new("telegram", [60])), default);
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.Equal("telegram_unavailable", r.Error.Code);
        Assert.Empty(_store.Appointments);
    }

    [Fact]
    public async Task telegram_disabled_for_the_tenant_is_409_before_availability()
    {
        _ctx.Settings = ReminderSettings.Default with { TelegramEnabled = false };
        Assert.Equal("telegram_reminders_disabled", (await Sut().CreateAsync(Req(T(14), reminders: new("telegram", [60])), default)).Error!.Code);
    }

    [Fact]
    public async Task call_without_phone_or_with_calls_disabled_is_409()
    {
        _ctx.PhoneAvailable = false;
        var noPhone = await Sut().CreateAsync(Req(T(14), reminders: new("call", [60]), clientId: FakeBookingStore.Client), default);
        Assert.Equal(ErrorKind.Conflict, noPhone.Error!.Kind);
        Assert.Equal("phone_required", noPhone.Error.Code);

        _ctx.PhoneAvailable = true;
        _ctx.Settings = ReminderSettings.Default with { CallEnabled = false };
        Assert.Equal("call_reminders_disabled", (await Sut().CreateAsync(Req(T(14), reminders: new("call", [60])), default)).Error!.Code);
    }

    [Fact]
    public async Task none_plan_skips_the_availability_checks_and_unknown_client_is_left_to_client_not_found()
    {
        _ctx.Settings = ReminderSettings.Default with { TelegramEnabled = false, CallEnabled = false };
        Assert.True((await Sut().CreateAsync(Req(T(14), reminders: new("none", [])), default)).IsOk);

        _ctx.ClientFound = false;
        _store.ClientExists = false;
        var r = await Sut().CreateAsync(Req(T(16), reminders: new("call", [60]), clientId: Guid.NewGuid()), default);
        Assert.Equal("client_not_found", r.Error!.Code);
    }

    // ---------- сценарій 13: вподобання ----------

    [Fact]
    public async Task without_explicit_fields_the_client_preference_defines_the_plan()
    {
        _ctx.Preference = new("telegram", [120, 1440]);
        var r = await Sut().CreateAsync(Req(T(14)), default);
        var plan = PlanOf(r);
        Assert.Equal("telegram", plan.Method);
        Assert.Equal(2, plan.Points.Count);
        Assert.Equal("custom", r.Value!.ReminderOption);
    }

    [Fact]
    public async Task without_explicit_fields_and_without_preference_the_plan_is_none()
    {
        var r = await Sut().CreateAsync(Req(T(14)), default);
        Assert.Equal("none", PlanOf(r).Method);
        Assert.Empty(PlanOf(r).Points);
        Assert.Equal("none", r.Value!.ReminderOption);
    }

    [Fact]
    public async Task call_preference_without_a_phone_is_planned_as_skipped_no_phone_not_rejected()
    {
        _ctx.Preference = new("call", [1440]);
        _ctx.PhoneAvailable = false;
        var r = await Sut().CreateAsync(Req(T(14), clientId: FakeBookingStore.Client), default);
        Assert.True(r.IsOk);
        Assert.Equal(ReminderSkipReasons.NoPhone, Assert.Single(PlanOf(r).Points).SkipReason);
    }

    // ---------- перенос ----------

    [Fact]
    public async Task patch_replans_the_same_plan_from_the_new_start()
    {
        _ctx.TelegramAvailable = true;
        var sut = Sut();
        var a = (await sut.CreateAsync(Req(T(14), reminders: new("telegram", [120, 1440])), default)).Value!;
        var moved = await sut.PatchAsync(a.Id, new PatchAppointmentRequest(T(16), null), default);

        Assert.True(moved.IsOk, moved.Error?.Code);
        var plan = _store.Plans[a.Id];
        Assert.Equal(T(16), plan.StartsAt);
        Assert.Equal("telegram", plan.Method);
        Assert.Equal(T(14), plan.Points.Single(p => p.OffsetMinutes == 120).DueAt);
        Assert.Equal(T(16, day: 6), plan.Points.Single(p => p.OffsetMinutes == 1440).DueAt);
    }

    [Fact]
    public async Task patch_of_a_none_plan_stays_without_points()
    {
        var sut = Sut();
        var a = (await sut.CreateAsync(Req(T(14), "none"), default)).Value!;
        await sut.PatchAsync(a.Id, new PatchAppointmentRequest(T(16), null), default);
        Assert.Empty(_store.Plans[a.Id].Points);
    }

    [Fact]
    public async Task patch_of_a_call_plan_replans_the_call_task_for_the_new_time()
    {
        var sut = Sut();
        var a = (await sut.CreateAsync(Req(T(14), reminders: new("call", [120])), default)).Value!;
        await sut.PatchAsync(a.Id, new PatchAppointmentRequest(T(16), null), default);
        var p = Assert.Single(_store.Plans[a.Id].Points);
        Assert.Equal(ReminderPointKind.Call, p.Kind);
        Assert.Equal(T(14), p.DueAt);
    }

    [Fact]
    public async Task patch_of_a_legacy_dto_without_reminders_uses_the_option_mirror()
    {
        var seeded = _store.Seed(T(14), "confirmed", paid: 0m) with { ReminderOption = "2h" }; // як запис до TASK-737
        _store.Appointments[0] = seeded;
        var moved = await Sut().PatchAsync(seeded.Id, new PatchAppointmentRequest(T(16), null), default);
        Assert.True(moved.IsOk, moved.Error?.Code);
        var p = Assert.Single(_store.Plans[seeded.Id].Points);
        Assert.Equal(T(14), p.DueAt);
        Assert.Equal("auto", _store.Plans[seeded.Id].Method);
    }

    // ---------- чат: вподобання й ознака «потрібен вибір» ----------

    private sealed class ChatHarness
    {
        private static readonly DateTimeOffset ChatNow = new(2030, 1, 7, 8, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Tue10 = new(2030, 1, 8, 10, 0, 0, TimeSpan.Zero);

        public readonly FakeBookingStore Fake = new();
        public readonly FakeReminderContext Ctx = new() { TelegramAvailable = true };
        public readonly FakeChatStore Chat;
        public readonly ChatBookingService Svc;

        public ChatHarness(bool withContext = true)
        {
            var store = new SerializedBookingStore(Fake);
            var clock = new FakeClock(ChatNow);
            Chat = new FakeChatStore(store);
            var booking = new BookingService(store, new SpyPayments(), FakeSettings.Service(), clock, withContext ? Ctx : null);
            var core = new OnlineBookingCore(new FakePublicStore(store), booking, new PublicBookingOptions(), clock);
            Svc = new ChatBookingService(Chat, new FakePublicStore(store), store, core, new ChatBookingOptions(), clock, withContext ? Ctx : null);
        }

        public async Task<Result<ChatConfirmed>> BookAsync()
        {
            var conv = Chat.NewConversation();
            var prepared = (await Svc.PrepareAsync(new ChatPrepareRequest(conv, FakeBookingStore.Location, FakeBookingStore.Service90, FakeBookingStore.Specialist, Tue10), default)).Value!;
            await Svc.MarkShownAsync(conv, prepared.ProposalId, Guid.NewGuid(), default);
            return await Svc.ConfirmAsync(new ChatConfirmRequest(conv, prepared.ProposalId, true, new TelegramProfile("Анна", "Коваль", "anna")), default);
        }
    }

    [Fact]
    public async Task chat_booking_without_a_preference_creates_no_reminders_and_signals_that_a_choice_is_needed()
    {
        var h = new ChatHarness();
        var r = await h.BookAsync();

        Assert.True(r.IsOk, r.Error?.Code);
        Assert.True(r.Value!.ReminderChoiceNeeded);
        Assert.Equal("none", r.Value.Appointment.ReminderOption);
        var plan = h.Fake.Plans[r.Value.Appointment.Id];
        Assert.Equal("none", plan.Method);
        Assert.Empty(plan.Points);
        Assert.Empty(h.Fake.Reminders);
    }

    [Fact]
    public async Task chat_booking_with_a_preference_plans_by_it_without_asking()
    {
        var h = new ChatHarness();
        h.Ctx.Preference = new("telegram", [120, 1440]);
        var r = await h.BookAsync();

        Assert.False(r.Value!.ReminderChoiceNeeded);
        var plan = h.Fake.Plans[r.Value.Appointment.Id];
        Assert.Equal("telegram", plan.Method);
        Assert.Equal(2, plan.Points.Count);
        Assert.Equal("custom", r.Value.Appointment.ReminderOption);
    }

    [Fact]
    public async Task chat_booking_with_a_none_preference_does_not_ask_again()
    {
        var h = new ChatHarness();
        h.Ctx.Preference = new("none", []);
        var r = await h.BookAsync();
        Assert.False(r.Value!.ReminderChoiceNeeded);
        Assert.Empty(h.Fake.Plans[r.Value.Appointment.Id].Points);
    }

    [Fact]
    public async Task chat_booking_without_the_reminder_port_still_asks_for_a_choice()
    {
        var h = new ChatHarness(withContext: false);
        var r = await h.BookAsync();
        Assert.True(r.Value!.ReminderChoiceNeeded);
        Assert.Equal("none", r.Value.Appointment.ReminderOption);
    }

    [Fact]
    public async Task chat_replay_keeps_the_choice_signal()
    {
        var h = new ChatHarness();
        var conv = h.Chat.NewConversation();
        var prepared = (await h.Svc.PrepareAsync(new ChatPrepareRequest(conv, FakeBookingStore.Location, FakeBookingStore.Service90,
            FakeBookingStore.Specialist, new DateTimeOffset(2030, 1, 8, 10, 0, 0, TimeSpan.Zero)), default)).Value!;
        await h.Svc.MarkShownAsync(conv, prepared.ProposalId, Guid.NewGuid(), default);
        var req = new ChatConfirmRequest(conv, prepared.ProposalId, true, new TelegramProfile("Анна", "Коваль", "anna"));
        var first = await h.Svc.ConfirmAsync(req, default);
        var second = await h.Svc.ConfirmAsync(req, default);

        Assert.True(second.Value!.AlreadyConfirmed);
        Assert.True(second.Value.ReminderChoiceNeeded);
        Assert.Equal(first.Value!.Appointment.Id, second.Value.Appointment.Id);
    }
}
