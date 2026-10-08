using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyStaff;

namespace BeautyCrm.Tests.Beauty;

/// <summary>TASK-691: слоти/запис з урахуванням відсутностей і призначених послуг (юніт, без БД).</summary>
public class StaffSlotsAndBookingTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero); // неділя
    private static readonly DateOnly Monday = new(2030, 1, 7);
    private static readonly DateOnly Tuesday = new(2030, 1, 8);

    private readonly FakeBookingStore _store = new();
    private BookingService Sut() => new(_store, new SpyPayments(), FakeSettings.Service(), new FakeClock(Now));

    private static CreateAppointmentRequest Req(DateTimeOffset start) =>
        new(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90, start,
            new ClientInput(null, "Олена", "+380501112233", null), "none", "cash", "admin");

    private static SpecialistSchedule Schedule(IReadOnlyList<AbsenceSpan>? absences = null, IReadOnlyList<Guid>? services = null) =>
        new(FakeBookingStore.Specialist, "UTC", FakeBookingStore.Mon9To18, absences, services);

    // ---- SlotCalculator ----

    [Fact]
    public void compute_returns_no_slots_on_absence_day_but_slots_on_neighbour_day()
    {
        var s = Schedule([new AbsenceSpan(Monday, Monday)]);
        Assert.Empty(SlotCalculator.Compute(s, Monday, 60, [], Now, FakeBookingStore.Service90));
        Assert.NotEmpty(SlotCalculator.Compute(s, Tuesday, 60, [], Now, FakeBookingStore.Service90));
    }

    [Fact]
    public void compute_treats_multi_day_absence_inclusively()
    {
        var s = Schedule([new AbsenceSpan(Monday, Tuesday)]);
        Assert.Empty(SlotCalculator.Compute(s, Monday, 60, [], Now));
        Assert.Empty(SlotCalculator.Compute(s, Tuesday, 60, [], Now));
    }

    [Fact]
    public void compute_returns_no_slots_for_service_not_assigned_to_specialist()
    {
        var s = Schedule(services: [Guid.NewGuid()]);
        Assert.Empty(SlotCalculator.Compute(s, Monday, 60, [], Now, FakeBookingStore.Service90));
        Assert.NotEmpty(SlotCalculator.Compute(Schedule(services: [FakeBookingStore.Service90]), Monday, 60, [], Now, FakeBookingStore.Service90));
    }

    [Fact]
    public void compute_returns_no_slots_when_specialist_has_no_assigned_services_at_all()
    {
        Assert.Empty(SlotCalculator.Compute(Schedule(services: []), Monday, 60, [], Now, FakeBookingStore.Service90));
    }

    [Fact]
    public void check_returns_unavailable_for_absence_and_unassigned_service()
    {
        var start = new DateTimeOffset(2030, 1, 7, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(SlotCheck.Unavailable,
            SlotCalculator.Check(Schedule([new AbsenceSpan(Monday, Monday)]), start, 60, [], Now, FakeBookingStore.Service90));
        Assert.Equal(SlotCheck.Unavailable,
            SlotCalculator.Check(Schedule(services: [Guid.NewGuid()]), start, 60, [], Now, FakeBookingStore.Service90));
        Assert.Equal(SlotCheck.Ok, SlotCalculator.Check(Schedule(), start, 60, [], Now, FakeBookingStore.Service90));
    }

    [Theory]
    [InlineData("""{"mon":[{"from":"09:00","to":"18:00"}]}""", true)]
    [InlineData("""{}""", false)]
    [InlineData("""{"mon":[]}""", false)]
    [InlineData(null, false)]
    [InlineData("not json", false)]
    public void hasWorkingHours_detects_bookable_schedules(string? json, bool expected) =>
        Assert.Equal(expected, SlotCalculator.HasWorkingHours(json));

    // ---- BookingService ----

    [Fact]
    public async Task getSlots_skips_absence_day()
    {
        _store.Absences = [new AbsenceSpan(Monday, Monday)];
        var r = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Monday, default);
        Assert.Empty(r.Value!);
    }

    [Fact]
    public async Task getSlots_skips_specialist_without_assigned_service()
    {
        _store.AssignedServices = [];
        var r = await Sut().GetSlotsAsync(FakeBookingStore.Location, null, FakeBookingStore.Service90, Monday, default);
        Assert.Empty(r.Value!);
    }

    [Fact]
    public async Task create_returns_409_specialist_unavailable_on_absence_day()
    {
        _store.Absences = [new AbsenceSpan(Monday, Monday)];
        var r = await Sut().CreateAsync(Req(new DateTimeOffset(2030, 1, 7, 10, 0, 0, TimeSpan.Zero)), default);
        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.Equal("specialist_unavailable", r.Error.Code);
        Assert.Empty(_store.Appointments);
    }

    [Fact]
    public async Task create_returns_409_specialist_unavailable_for_unassigned_service()
    {
        _store.AssignedServices = [Guid.NewGuid()];
        var r = await Sut().CreateAsync(Req(new DateTimeOffset(2030, 1, 7, 10, 0, 0, TimeSpan.Zero)), default);
        Assert.Equal("specialist_unavailable", r.Error!.Code);
    }

    [Fact]
    public async Task patch_reschedule_into_absence_day_is_409()
    {
        var created = await Sut().CreateAsync(Req(new DateTimeOffset(2030, 1, 7, 10, 0, 0, TimeSpan.Zero)), default);
        _store.Absences = [new AbsenceSpan(Tuesday, Tuesday)];
        var r = await Sut().PatchAsync(created.Value!.Id,
            new PatchAppointmentRequest(new DateTimeOffset(2030, 1, 8, 10, 0, 0, TimeSpan.Zero), null), default);
        Assert.Equal("specialist_unavailable", r.Error!.Code);
    }

    [Fact]
    public async Task create_still_works_when_absence_is_on_another_day()
    {
        _store.Absences = [new AbsenceSpan(Tuesday, Tuesday)];
        _store.AssignedServices = [FakeBookingStore.Service90];
        var r = await Sut().CreateAsync(Req(new DateTimeOffset(2030, 1, 7, 10, 0, 0, TimeSpan.Zero)), default);
        Assert.True(r.IsOk);
    }
}

public class WorkingHoursValidatorTests
{
    private static string? Norm(string json) => WorkingHoursValidator.TryNormalize(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void accepts_and_normalizes_valid_schedule()
    {
        var n = Norm("""{"tue":[{"from":"14:00","to":"18:00"},{"from":"09:00","to":"13:00"}],"mon":[]}""");
        Assert.Equal("""{"tue":[{"from":"09:00","to":"13:00"},{"from":"14:00","to":"18:00"}]}""", n);
    }

    [Fact]
    public void accepts_empty_object_as_no_working_days() => Assert.Equal("{}", Norm("{}"));

    [Theory]
    [InlineData("""{"funday":[{"from":"09:00","to":"18:00"}]}""")]
    [InlineData("""{"mon":[{"from":"9:00","to":"18:00"}]}""")]
    [InlineData("""{"mon":[{"from":"18:00","to":"09:00"}]}""")]
    [InlineData("""{"mon":[{"from":"09:00","to":"09:00"}]}""")]
    [InlineData("""{"mon":[{"from":"09:00","to":"13:00"},{"from":"12:00","to":"18:00"}]}""")]
    [InlineData("""{"mon":[{"from":"09:00","to":"18:00","extra":"x"}]}""")]
    [InlineData("""{"mon":[{"from":"09:00"}]}""")]
    [InlineData("""{"mon":{"from":"09:00","to":"18:00"}}""")]
    [InlineData("""{"mon":[{"from":9,"to":18}]}""")]
    [InlineData("""{"mon":[{"from":"24:00","to":"25:00"}]}""")]
    [InlineData("""[]""")]
    [InlineData("""{"mon":[],"mon":[]}""")]
    public void rejects_invalid_schedule(string json) => Assert.Null(Norm(json));

    [Fact]
    public void rejects_missing_value() => Assert.Null(WorkingHoursValidator.TryNormalize(null));
}

/// <summary>In-memory IStaffStore для юніт-тестів сервісів.</summary>
internal sealed class FakeStaffStore : IStaffStore
{
    public readonly List<StaffSpecialistRecord> Specialists = [];
    public readonly List<AbsenceRecord> Absences = [];
    public readonly List<AppointmentCandidate> Appointments = [];
    public HashSet<Guid> ServiceIds = [];
    public HashSet<Guid> LocationIds = [];

    public StaffSpecialistRecord AddSpecialist(string? linkedRole = null, bool active = true)
    {
        var s = new StaffSpecialistRecord(Guid.NewGuid(), "Master", "+380501112233", "Stylist", null, active, linkedRole, [], []);
        Specialists.Add(s);
        return s;
    }

    public Task<IReadOnlyList<StaffSpecialistRecord>> ListSpecialistsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StaffSpecialistRecord>>(Specialists.ToList());
    public Task<StaffSpecialistRecord?> GetSpecialistAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Specialists.FirstOrDefault(s => s.Id == id));
    public Task<SpecialistLink?> GetSpecialistLinkAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Specialists.FirstOrDefault(s => s.Id == id) is { } s ? new SpecialistLink(s.Id, s.IsActive, s.LinkedUserRole) : null);
    public Task<IReadOnlySet<Guid>> ExistingServiceIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(ids.Where(ServiceIds.Contains).ToHashSet());
    public Task<IReadOnlySet<Guid>> ExistingLocationIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(ids.Where(LocationIds.Contains).ToHashSet());

    public Task<LocationAssignOutcome> AssignLocationAsync(Guid specialistId, Guid locationId, string? workingHoursJson, CancellationToken ct) =>
        Task.FromResult(LocationAssignOutcome.Assigned);
    public Task<LocationRemoveOutcome> RemoveLocationAsync(Guid specialistId, Guid locationId, DateTimeOffset nowUtc, CancellationToken ct) =>
        Task.FromResult(LocationRemoveOutcome.Removed);

    public Task<Guid> CreateSpecialistAsync(NewSpecialist n, CancellationToken ct)
    {
        var s = new StaffSpecialistRecord(Guid.NewGuid(), n.Name, n.Phone, n.Position, null, true, null, [], []);
        Specialists.Add(s);
        return Task.FromResult(s.Id);
    }
    public Task UpdateSpecialistAsync(Guid id, string name, string? phone, string? position, bool? isActive, DateTimeOffset now, CancellationToken ct)
    {
        var i = Specialists.FindIndex(s => s.Id == id);
        Specialists[i] = Specialists[i] with { FullName = name, Phone = phone, Position = position, IsActive = isActive ?? Specialists[i].IsActive };
        return Task.CompletedTask;
    }
    public Task ReplaceServicesAsync(Guid specialistId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct) => Task.CompletedTask;
    public Task SetScheduleAsync(Guid specialistId, Guid locationId, string workingHoursJson, CancellationToken ct) => Task.CompletedTask;

    public Task<AbsenceRecord?> GetAbsenceAsync(Guid id, CancellationToken ct) => Task.FromResult(Absences.FirstOrDefault(a => a.Id == id));
    public Task<IReadOnlyList<AbsenceRecord>> ListAbsencesAsync(DateOnly from, DateOnly to, Guid? specialistId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AbsenceRecord>>(Absences
            .Where(a => a.DateTo >= from && a.DateFrom <= to && (specialistId is null || a.SpecialistId == specialistId)).ToList());
    public Task<bool> HasActiveAbsenceOverlapAsync(Guid specialistId, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult(Absences.Any(a => a.SpecialistId == specialistId && a.Status is "requested" or "approved"
                                          && a.DateFrom <= to && a.DateTo >= from));
    public Task<AbsenceAddResult> AddAbsenceAsync(NewAbsence n, int? maxRequested, ConflictWindow? window, CancellationToken ct)
    {
        if (maxRequested is { } max && n.Status == "requested"
            && Absences.Count(x => x.SpecialistId == n.SpecialistId && x.Status == "requested") >= max)
            return Task.FromResult(new AbsenceAddResult(AbsenceAddOutcome.TooManyRequests, null));
        var a = new AbsenceRecord(Guid.NewGuid(), n.SpecialistId, n.Type, n.DateFrom, n.DateTo, n.Status, n.Note,
            n.RequestedByUserId, n.DecidedByUserId, n.DecidedAt);
        Absences.Add(a);
        return Task.FromResult(new AbsenceAddResult(AbsenceAddOutcome.Ok, a with { Candidates = Candidates(window) }));
    }
    public Task<AbsenceRecord?> TransitionAbsenceAsync(
        Guid id, IReadOnlyCollection<string> from, string to, Guid? by, DateTimeOffset? at, ConflictWindow? window, CancellationToken ct)
    {
        var i = Absences.FindIndex(a => a.Id == id && from.Contains(a.Status));
        if (i < 0) return Task.FromResult<AbsenceRecord?>(null);
        Absences[i] = Absences[i] with { Status = to };
        LastTransition = (to, by, at);
        return Task.FromResult<AbsenceRecord?>(Absences[i] with { Candidates = Candidates(window) });
    }
    public (string Status, Guid? By, DateTimeOffset? At)? LastTransition;
    private IReadOnlyList<AppointmentCandidate>? Candidates(ConflictWindow? w) =>
        w is null ? null : Appointments.Where(a => a.StartsAt >= w.FromUtc && a.StartsAt < w.ToUtc).ToList();
}

public class AbsenceServiceTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly D7 = new(2030, 1, 7);

    private readonly FakeStaffStore _store = new();
    private AbsenceService Sut() => new(_store, new FakeClock(Now));

    private static Actor Owner() => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Owner, null);
    private static Actor Admin() => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Admin, null);
    private static Actor Specialist(Guid specialistId) => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Specialist, specialistId);

    private static CreateAbsenceRequest Req(DateOnly from, DateOnly to, string type = "sick", string? note = "private note") =>
        new(type, from, to, note);

    // ---- права ----

    [Fact]
    public async Task create_by_manager_is_approved_immediately_with_conflicts()
    {
        var sp = _store.AddSpecialist();
        _store.Appointments.Add(new AppointmentCandidate(Guid.NewGuid(), new DateTimeOffset(2030, 1, 8, 10, 0, 0, TimeSpan.Zero), "Манікюр", "UTC"));
        _store.Appointments.Add(new AppointmentCandidate(Guid.NewGuid(), new DateTimeOffset(2030, 1, 20, 10, 0, 0, TimeSpan.Zero), "Стрижка", "UTC"));
        var r = await Sut().CreateAsync(Admin(), sp.Id, Req(D7, D7.AddDays(2)), default);

        Assert.True(r.IsOk);
        Assert.Equal("approved", r.Value!.Status);
        var conflict = Assert.Single(r.Value.Conflicts!);
        Assert.Equal("Манікюр", conflict.ServiceName);
    }

    [Fact]
    public async Task conflicts_use_local_date_of_the_location_timezone()
    {
        var sp = _store.AddSpecialist();
        // 2030-01-07 23:30 UTC = 2030-01-08 01:30 у Києві (UTC+2) -> потрапляє на 08.01, а не на 07.01
        _store.Appointments.Add(new AppointmentCandidate(Guid.NewGuid(), new DateTimeOffset(2030, 1, 7, 23, 30, 0, TimeSpan.Zero), "Нічна", "Europe/Kyiv"));
        var onSeventh = await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7), default);
        Assert.Empty(onSeventh.Value!.Conflicts!);
        var onEighth = await Sut().CreateAsync(Owner(), sp.Id, Req(D7.AddDays(1), D7.AddDays(1)), default);
        Assert.Single(onEighth.Value!.Conflicts!);
    }

    [Fact]
    public async Task create_by_specialist_for_self_is_requested_without_conflicts()
    {
        var sp = _store.AddSpecialist();
        var r = await Sut().CreateAsync(Specialist(sp.Id), sp.Id, Req(D7, D7), default);
        Assert.Equal("requested", r.Value!.Status);
        Assert.Null(r.Value.Conflicts);
    }

    [Fact]
    public async Task create_by_specialist_for_another_specialist_is_forbidden()
    {
        var mine = _store.AddSpecialist();
        var other = _store.AddSpecialist();
        var r = await Sut().CreateAsync(Specialist(mine.Id), other.Id, Req(D7, D7), default);
        Assert.Equal(ErrorKind.Forbidden, r.Error!.Kind);
        Assert.Empty(_store.Absences);
    }

    [Fact]
    public async Task create_by_specialist_without_profile_is_forbidden()
    {
        var sp = _store.AddSpecialist();
        var r = await Sut().CreateAsync(new Actor(Guid.NewGuid(), Guid.NewGuid(), Roles.Specialist, null), sp.Id, Req(D7, D7), default);
        Assert.Equal(ErrorKind.Forbidden, r.Error!.Kind);
    }

    [Fact]
    public async Task create_for_unknown_specialist_is_not_found_for_manager()
    {
        var r = await Sut().CreateAsync(Owner(), Guid.NewGuid(), Req(D7, D7), default);
        Assert.Equal(ErrorKind.NotFound, r.Error!.Kind);
    }

    [Fact]
    public async Task admin_cannot_create_absence_for_specialist_linked_to_owner()
    {
        var ownerProfile = _store.AddSpecialist(Roles.Owner);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().CreateAsync(Admin(), ownerProfile.Id, Req(D7, D7), default)).Error!.Kind);
        Assert.True((await Sut().CreateAsync(Owner(), ownerProfile.Id, Req(D7, D7), default)).IsOk);
    }

    // ---- валідація й перетини ----

    [Theory]
    [InlineData("holiday")]
    [InlineData("")]
    public async Task create_rejects_unknown_type(string type)
    {
        var sp = _store.AddSpecialist();
        var r = await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7, type), default);
        Assert.Equal("invalid_type", r.Error!.Code);
    }

    [Fact]
    public async Task create_rejects_inverted_dates_too_long_span_and_long_note()
    {
        var sp = _store.AddSpecialist();
        Assert.Equal("invalid_dates", (await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7.AddDays(-1)), default)).Error!.Code);
        Assert.Equal("invalid_dates", (await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7.AddDays(400)), default)).Error!.Code);
        Assert.Equal("invalid_note", (await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7, note: new string('x', 501)), default)).Error!.Code);
        Assert.True((await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7, note: new string('x', 500)), default)).IsOk);
    }

    [Fact]
    public async Task overlapping_active_absence_is_409_but_adjacent_or_cancelled_is_fine()
    {
        var sp = _store.AddSpecialist();
        var first = await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7.AddDays(2)), default);

        var overlap = await Sut().CreateAsync(Owner(), sp.Id, Req(D7.AddDays(2), D7.AddDays(4)), default);
        Assert.Equal(ErrorKind.Conflict, overlap.Error!.Kind);
        Assert.Equal("absence_overlap", overlap.Error.Code);

        Assert.True((await Sut().CreateAsync(Owner(), sp.Id, Req(D7.AddDays(3), D7.AddDays(4)), default)).IsOk); // впритул

        await Sut().CancelAsync(Owner(), first.Value!.Id, default);
        Assert.True((await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7.AddDays(2)), default)).IsOk); // скасована не блокує
    }

    [Fact]
    public async Task pending_request_also_blocks_overlapping_request()
    {
        var sp = _store.AddSpecialist();
        var me = Specialist(sp.Id);
        Assert.True((await Sut().CreateAsync(me, sp.Id, Req(D7, D7), default)).IsOk);
        Assert.Equal("absence_overlap", (await Sut().CreateAsync(me, sp.Id, Req(D7, D7), default)).Error!.Code);
    }

    // ---- видимість note ----

    [Fact]
    public async Task note_is_visible_to_managers_and_author_but_hidden_from_other_staff()
    {
        var a = _store.AddSpecialist();
        var b = _store.AddSpecialist();
        var author = Specialist(a.Id);
        var created = await Sut().CreateAsync(author, a.Id, Req(D7, D7, note: "my diagnosis"), default);
        await Sut().ApproveAsync(Owner(), created.Value!.Id, default); // колегам видно лише затверджені

        string? NoteSeenBy(Actor viewer) =>
            Sut().ListAsync(viewer, D7, D7, null, default).Result.Value!.Single().Note;

        Assert.Equal("my diagnosis", NoteSeenBy(author));
        Assert.Equal("my diagnosis", NoteSeenBy(Owner()));
        Assert.Equal("my diagnosis", NoteSeenBy(Admin()));
        Assert.Null(NoteSeenBy(Specialist(b.Id)));
    }

    [Fact]
    public async Task note_of_manager_created_absence_is_hidden_from_the_absent_specialist_and_not_serialized()
    {
        var sp = _store.AddSpecialist();
        var created = await Sut().CreateAsync(Admin(), sp.Id, Req(D7, D7, note: "hr note"), default);
        var seen = (await Sut().ListAsync(Specialist(sp.Id), D7, D7, null, default)).Value!.Single();

        Assert.Null(seen.Note);
        Assert.DoesNotContain("note", JsonSerializer.Serialize(seen, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hr note", created.Value!.Note);
    }

    [Fact]
    public async Task specialist_sees_only_approved_absences_of_colleagues_but_all_of_own()
    {
        var a = _store.AddSpecialist();
        var b = _store.AddSpecialist();
        await Sut().CreateAsync(Specialist(b.Id), b.Id, Req(D7, D7), default);                    // requested
        await Sut().CreateAsync(Owner(), b.Id, Req(D7.AddDays(5), D7.AddDays(5)), default);       // approved
        await Sut().CreateAsync(Specialist(a.Id), a.Id, Req(D7.AddDays(10), D7.AddDays(10)), default); // own requested

        var list = (await Sut().ListAsync(Specialist(a.Id), D7, D7.AddDays(30), null, default)).Value!;
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, x => x.SpecialistId == b.Id && x.Status == "requested");
        Assert.Equal(3, (await Sut().ListAsync(Owner(), D7, D7.AddDays(30), null, default)).Value!.Count);
    }

    [Fact]
    public async Task list_rejects_inverted_or_too_wide_range()
    {
        Assert.Equal("invalid_range", (await Sut().ListAsync(Owner(), D7, D7.AddDays(-1), null, default)).Error!.Code);
        Assert.Equal("invalid_range", (await Sut().ListAsync(Owner(), D7, D7.AddDays(400), null, default)).Error!.Code);
    }

    // ---- рішення ----

    [Fact]
    public async Task approve_returns_conflicts_and_only_works_on_pending_request()
    {
        var sp = _store.AddSpecialist();
        _store.Appointments.Add(new AppointmentCandidate(Guid.NewGuid(), new DateTimeOffset(2030, 1, 7, 10, 0, 0, TimeSpan.Zero), "Манікюр", "UTC"));
        var request = await Sut().CreateAsync(Specialist(sp.Id), sp.Id, Req(D7, D7), default);

        var approved = await Sut().ApproveAsync(Admin(), request.Value!.Id, default);
        Assert.Equal("approved", approved.Value!.Status);
        Assert.Single(approved.Value.Conflicts!);

        var again = await Sut().ApproveAsync(Admin(), request.Value.Id, default);
        Assert.Equal("absence_not_pending", again.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, again.Error.Kind);
    }

    [Fact]
    public async Task reject_sets_rejected_and_frees_the_period()
    {
        var sp = _store.AddSpecialist();
        var request = await Sut().CreateAsync(Specialist(sp.Id), sp.Id, Req(D7, D7), default);
        Assert.Equal("rejected", (await Sut().RejectAsync(Owner(), request.Value!.Id, default)).Value!.Status);
        Assert.True((await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7), default)).IsOk);
    }

    [Fact]
    public async Task specialist_cannot_approve_or_reject()
    {
        var sp = _store.AddSpecialist();
        var request = await Sut().CreateAsync(Specialist(sp.Id), sp.Id, Req(D7, D7), default);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().ApproveAsync(Specialist(sp.Id), request.Value!.Id, default)).Error!.Kind);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().RejectAsync(Specialist(sp.Id), request.Value.Id, default)).Error!.Kind);
        Assert.Equal("requested", _store.Absences.Single().Status);
    }

    [Fact]
    public async Task approve_unknown_absence_is_not_found()
    {
        Assert.Equal(ErrorKind.NotFound, (await Sut().ApproveAsync(Owner(), Guid.NewGuid(), default)).Error!.Kind);
    }

    // ---- скасування ----

    [Fact]
    public async Task author_can_cancel_own_pending_request_but_not_after_approval()
    {
        var sp = _store.AddSpecialist();
        var me = Specialist(sp.Id);
        var first = await Sut().CreateAsync(me, sp.Id, Req(D7, D7), default);
        Assert.Equal("cancelled", (await Sut().CancelAsync(me, first.Value!.Id, default)).Value!.Status);

        var second = await Sut().CreateAsync(me, sp.Id, Req(D7, D7), default);
        await Sut().ApproveAsync(Owner(), second.Value!.Id, default);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().CancelAsync(me, second.Value.Id, default)).Error!.Kind);
        Assert.Equal("cancelled", (await Sut().CancelAsync(Admin(), second.Value.Id, default)).Value!.Status);
    }

    [Fact]
    public async Task other_specialist_cannot_cancel_foreign_request_and_gets_not_found()
    {
        var a = _store.AddSpecialist();
        var b = _store.AddSpecialist();
        var request = await Sut().CreateAsync(Specialist(a.Id), a.Id, Req(D7, D7), default);
        var r = await Sut().CancelAsync(Specialist(b.Id), request.Value!.Id, default);
        Assert.Equal(ErrorKind.NotFound, r.Error!.Kind);
        Assert.Equal("requested", _store.Absences.Single().Status);
    }

    [Fact]
    public async Task cancel_twice_is_conflict()
    {
        var sp = _store.AddSpecialist();
        var created = await Sut().CreateAsync(Owner(), sp.Id, Req(D7, D7), default);
        await Sut().CancelAsync(Owner(), created.Value!.Id, default);
        Assert.Equal("absence_closed", (await Sut().CancelAsync(Owner(), created.Value.Id, default)).Error!.Code);
    }
}

public class StaffServiceTests
{
    private readonly FakeStaffStore _store = new();
    private StaffService Sut() => new(_store, TimeProvider.System);

    private static Actor Owner() => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Owner, null);
    private static Actor Admin() => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Admin, null);
    private static Actor Specialist() => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Specialist, Guid.NewGuid());

    private static readonly JsonElement Hours = JsonDocument.Parse("""{"mon":[{"from":"09:00","to":"18:00"}]}""").RootElement;

    [Fact]
    public async Task specialist_cannot_create_update_assign_or_schedule()
    {
        var sp = _store.AddSpecialist();
        var me = Specialist();
        Assert.Equal(ErrorKind.Forbidden, (await Sut().CreateAsync(me, new CreateSpecialistRequest("X", null, null, null, null, null), default)).Error!.Kind);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().UpdateAsync(me, sp.Id, new UpdateSpecialistRequest("X", null, null, null), default)).Error!.Kind);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().SetServicesAsync(me, sp.Id, new SetSpecialistServicesRequest([]), default)).Error!.Kind);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().SetScheduleAsync(me, sp.Id, new SetScheduleRequest(Guid.NewGuid(), Hours), default)).Error!.Kind);
        Assert.Equal("Master", _store.Specialists.Single().FullName);
    }

    [Fact]
    public async Task admin_cannot_change_profile_linked_to_owner_but_owner_can()
    {
        var ownerProfile = _store.AddSpecialist(Roles.Owner);
        var req = new UpdateSpecialistRequest("Renamed", null, null, null);
        Assert.Equal(ErrorKind.Forbidden, (await Sut().UpdateAsync(Admin(), ownerProfile.Id, req, default)).Error!.Kind);
        Assert.Equal("Master", _store.Specialists.Single().FullName);
        Assert.Equal("Renamed", (await Sut().UpdateAsync(Owner(), ownerProfile.Id, req, default)).Value!.Name);
    }

    [Fact]
    public async Task admin_can_change_regular_specialist_and_deactivate_without_touching_isActive_when_null()
    {
        var sp = _store.AddSpecialist(Roles.Specialist);
        var kept = await Sut().UpdateAsync(Admin(), sp.Id, new UpdateSpecialistRequest("New", "+380 50 111", "Colorist", null), default);
        Assert.True(kept.Value!.IsActive);
        var off = await Sut().UpdateAsync(Admin(), sp.Id, new UpdateSpecialistRequest("New", null, null, false), default);
        Assert.False(off.Value!.IsActive);
    }

    [Fact]
    public async Task update_unknown_specialist_is_not_found()
    {
        Assert.Equal(ErrorKind.NotFound, (await Sut().UpdateAsync(Owner(), Guid.NewGuid(), new UpdateSpecialistRequest("X", null, null, null), default)).Error!.Kind);
    }

    [Fact]
    public async Task specialist_directory_hides_phone_and_account_flag_from_specialists()
    {
        _store.AddSpecialist(Roles.Specialist);
        var asSpecialist = (await Sut().ListAsync(Specialist(), default)).Single();
        Assert.Null(asSpecialist.Phone);
        Assert.Null(asSpecialist.HasAccount);
        var json = JsonSerializer.Serialize(asSpecialist, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);

        var asAdmin = (await Sut().ListAsync(Admin(), default)).Single();
        Assert.Equal("+380501112233", asAdmin.Phone);
        Assert.True(asAdmin.HasAccount);
    }

    [Fact]
    public async Task create_validates_name_phone_hours_and_references()
    {
        var loc = Guid.NewGuid();
        var svc = Guid.NewGuid();
        _store.LocationIds.Add(loc);
        _store.ServiceIds.Add(svc);

        Assert.Equal("invalid_name", (await Sut().CreateAsync(Owner(), new CreateSpecialistRequest("  ", null, null, null, null, null), default)).Error!.Code);
        Assert.Equal("invalid_phone", (await Sut().CreateAsync(Owner(), new CreateSpecialistRequest("A", "abc", null, null, null, null), default)).Error!.Code);
        Assert.Equal("location_not_found", (await Sut().CreateAsync(Owner(), new CreateSpecialistRequest("A", null, null, [Guid.NewGuid()], null, null), default)).Error!.Code);
        Assert.Equal("service_not_found", (await Sut().CreateAsync(Owner(), new CreateSpecialistRequest("A", null, null, null, [Guid.NewGuid()], null), default)).Error!.Code);
        Assert.Equal("location_required", (await Sut().CreateAsync(Owner(), new CreateSpecialistRequest("A", null, null, null, null, Hours), default)).Error!.Code);
        var bad = JsonDocument.Parse("""{"mon":[{"from":"18:00","to":"09:00"}]}""").RootElement;
        Assert.Equal("invalid_working_hours", (await Sut().CreateAsync(Owner(), new CreateSpecialistRequest("A", null, null, [loc], null, bad), default)).Error!.Code);

        var ok = await Sut().CreateAsync(Owner(), new CreateSpecialistRequest(" Anna ", "+380501112233", "Stylist", [loc, loc], [svc], Hours), default);
        Assert.True(ok.IsOk);
        Assert.Equal("Anna", ok.Value!.Name);
    }

    [Fact]
    public async Task setSchedule_rejects_invalid_format_and_unknown_location()
    {
        var sp = _store.AddSpecialist();
        var loc = Guid.NewGuid();
        _store.LocationIds.Add(loc);
        var bad = JsonDocument.Parse("""{"mon":"09:00-18:00"}""").RootElement;
        Assert.Equal("invalid_working_hours", (await Sut().SetScheduleAsync(Owner(), sp.Id, new SetScheduleRequest(loc, bad), default)).Error!.Code);
        Assert.Equal("invalid_working_hours", (await Sut().SetScheduleAsync(Owner(), sp.Id, new SetScheduleRequest(loc, null), default)).Error!.Code);
        Assert.Equal(ErrorKind.NotFound, (await Sut().SetScheduleAsync(Owner(), sp.Id, new SetScheduleRequest(Guid.NewGuid(), Hours), default)).Error!.Kind);
        Assert.True((await Sut().SetScheduleAsync(Owner(), sp.Id, new SetScheduleRequest(loc, Hours), default)).IsOk);
    }
}
