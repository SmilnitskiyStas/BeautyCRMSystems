using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Tests.Beauty;

namespace BeautyCrm.Tests.ChatBooking;

/// <summary>TASK-726 (§21.6, §21.10 сценарії 6, 8-10, 19): ChatBookingService на in-memory портах.</summary>
public sealed class ChatBookingServiceTests
{
    // Понеділок 2030-01-07 08:00 UTC; графік у FakeBookingStore: пн-вт 09:00-18:00 UTC.
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tue10 = new(2030, 1, 8, 10, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeBookingStore Fake = new();
        public SerializedBookingStore Store;
        public FakeChatStore Chat;
        public FakeClock Clock = new(Now);
        public OnlineBookingCore Core;
        public ChatBookingService Svc;
        public BookingService Booking;

        public Harness(PublicBookingOptions? options = null)
        {
            Store = new SerializedBookingStore(Fake);
            Chat = new FakeChatStore(Store);
            Booking = new BookingService(Store, new SpyPayments(), FakeSettings.Service(), Clock);
            Core = new OnlineBookingCore(new FakePublicStore(Store), Booking, options ?? new PublicBookingOptions(), Clock);
            Svc = new ChatBookingService(Chat, new FakePublicStore(Store), Store, Core, new ChatBookingOptions(), Clock);
        }

        public Task<Result<ChatPrepared>> PrepareAsync(Guid conv, DateTimeOffset? at = null) => Svc.PrepareAsync(
            new ChatPrepareRequest(conv, FakeBookingStore.Location, FakeBookingStore.Service90, FakeBookingStore.Specialist, at ?? Tue10), default);

        /// <summary>prepare + підсумок показано клієнту.</summary>
        public async Task<ChatPrepared> ShownAsync(Guid conv, DateTimeOffset? at = null)
        {
            var r = await PrepareAsync(conv, at);
            Assert.True(r.IsOk, r.Error?.Code);
            Assert.True(await Svc.MarkShownAsync(conv, r.Value!.ProposalId, Guid.NewGuid(), default));
            return r.Value;
        }

        public Task<Result<ChatConfirmed>> ConfirmAsync(Guid conv, Guid proposal, bool confirmed = true, TelegramProfile? profile = null, string? token = null) =>
            Svc.ConfirmAsync(new ChatConfirmRequest(conv, proposal, confirmed, profile ?? new TelegramProfile("Анна", "Коваль", "anna"), token), default);

        public List<AppointmentDto> ChatAppointments() => Store.Snapshot().Where(a => a.Source == "telegram").ToList();
    }

    // ---------- етап 1 ----------

    [Fact]
    public async Task prepare_returns_summary_stores_hash_only_and_creates_nothing()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var prepared = (await h.PrepareAsync(conv)).Value!;

        Assert.Equal("Манікюр", prepared.Summary.ServiceName);
        Assert.Equal("Марина", prepared.Summary.SpecialistName);
        Assert.Equal("Центр", prepared.Summary.LocationName);
        Assert.Equal("Вул. 1", prepared.Summary.Address);
        Assert.Equal(new DateOnly(2030, 1, 8), prepared.Summary.LocalDate);
        Assert.Equal(new TimeOnly(10, 0), prepared.Summary.LocalTime);
        Assert.Equal(1000m, prepared.Summary.PriceFinal);
        Assert.Equal(Now.AddMinutes(15), prepared.ExpiresAt);

        var stored = h.Chat.Get(prepared.ProposalId);
        Assert.Equal("prepared", stored.Status);
        Assert.Equal(PublicTokenService.Hash(prepared.ConfirmationToken), stored.TokenHash);
        Assert.NotEqual(prepared.ConfirmationToken, stored.TokenHash);
        Assert.DoesNotContain(prepared.ConfirmationToken, prepared.ToString());
        Assert.DoesNotContain(prepared.ConfirmationToken, System.Text.Json.JsonSerializer.Serialize(prepared));

        Assert.Empty(h.Store.Snapshot());
        Assert.Equal(0, h.Chat.ClientCount);
        Assert.Null(h.Chat.ClientOf(conv));
    }

    [Fact]
    public async Task prepare_unknown_conversation_service_location_or_specialist_is_not_found()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        Assert.Equal("conversation_not_found", (await h.PrepareAsync(Guid.NewGuid())).Error!.Code);
        Assert.Equal("location_not_found", (await h.Svc.PrepareAsync(new ChatPrepareRequest(conv, Guid.NewGuid(), FakeBookingStore.Service90, FakeBookingStore.Specialist, Tue10), default)).Error!.Code);
        Assert.Equal("service_not_found", (await h.Svc.PrepareAsync(new ChatPrepareRequest(conv, FakeBookingStore.Location, Guid.NewGuid(), FakeBookingStore.Specialist, Tue10), default)).Error!.Code);
        Assert.Equal("specialist_not_found", (await h.Svc.PrepareAsync(new ChatPrepareRequest(conv, FakeBookingStore.Location, FakeBookingStore.Service90, Guid.NewGuid(), Tue10), default)).Error!.Code);
    }

    [Fact]
    public async Task prepare_rejects_off_grid_start_beyond_horizon_and_not_offered_slots()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        Assert.Equal("invalid_start", (await h.PrepareAsync(conv, Tue10.AddMinutes(7))).Error!.Code);
        Assert.Equal("invalid_start", (await h.PrepareAsync(conv, Now.AddDays(181))).Error!.Code);
        Assert.Equal("slot_not_offered", (await h.PrepareAsync(conv, new DateTimeOffset(2030, 1, 8, 20, 0, 0, TimeSpan.Zero))).Error!.Code);
        Assert.Equal("slot_not_offered", (await h.PrepareAsync(conv, new DateTimeOffset(2030, 1, 9, 10, 0, 0, TimeSpan.Zero))).Error!.Code); // середа - вихідний майстра
        h.Fake.Appointments.Add(Appt(Tue10, "confirmed"));
        Assert.Equal("slot_not_offered", (await h.PrepareAsync(conv, Tue10.AddMinutes(30))).Error!.Code);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("location_hours")]
    [InlineData("absence")]
    [InlineData("closed_weekday")]
    [InlineData("closure_date")]
    [InlineData("busy")]
    [InlineData("service_not_assigned")]
    public async Task prepare_accepts_exactly_the_slots_that_the_public_booking_offers(string scenario)
    {
        var h = new Harness();
        var date = new DateOnly(2030, 1, 8);
        switch (scenario)
        {
            case "location_hours": h.Fake.LocationHours = """{"tue":[{"from":"10:00","to":"14:00"}]}"""; break;
            case "absence": h.Fake.Absences = [new AbsenceSpan(date, date)]; break;
            case "closed_weekday": h.Fake.ClosedWeekdays = ["tue"]; break;
            case "closure_date": h.Fake.Closures = [new AbsenceSpan(date, date)]; break;
            case "busy": h.Fake.Appointments.Add(Appt(Tue10.AddMinutes(60), "confirmed")); break;
            case "service_not_assigned": h.Fake.AssignedServices = []; break;
        }
        var offered = (await h.Core.GetSlotsAsync(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90, date, default))
            .Value!.Select(s => s.StartsAt.UtcTicks).ToHashSet();

        var accepted = new HashSet<long>();
        var conv = h.Chat.NewConversation();
        for (var t = new DateTimeOffset(2030, 1, 8, 0, 0, 0, TimeSpan.Zero); t < new DateTimeOffset(2030, 1, 9, 0, 0, 0, TimeSpan.Zero); t = t.AddMinutes(15))
            if ((await h.PrepareAsync(conv, t)).IsOk) accepted.Add(t.UtcTicks);

        Assert.Equal(offered, accepted);
        if (scenario is "none" or "location_hours" or "busy") Assert.NotEmpty(accepted); else Assert.Empty(accepted);
    }

    [Fact]
    public async Task new_prepare_supersedes_the_previous_active_proposal()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var first = await h.ShownAsync(conv);
        var second = (await h.PrepareAsync(conv, Tue10.AddMinutes(30))).Value!;
        Assert.Equal("superseded", h.Chat.Get(first.ProposalId).Status);
        Assert.Equal("prepared", h.Chat.Get(second.ProposalId).Status);
        Assert.Equal("proposal_not_active", (await h.ConfirmAsync(conv, first.ProposalId)).Error!.Code);
    }

    // ---------- етап 2 ----------

    [Fact]
    public async Task confirm_creates_telegram_cash_confirmed_booking_with_lazy_client_without_phone()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        Assert.Equal(0, h.Chat.ClientCount); // до підтвердження клієнта немає

        var r = await h.ConfirmAsync(conv, p.ProposalId);

        Assert.True(r.IsOk, r.Error?.Code);
        Assert.False(r.Value!.AlreadyConfirmed);
        var a = Assert.Single(h.ChatAppointments());
        Assert.Equal(a.Id, r.Value.Appointment.Id);
        Assert.Equal("confirmed", a.Status);
        Assert.Equal("telegram", a.Source);
        Assert.Equal("cash", a.PaymentMethod);
        Assert.Equal("none", a.ReminderOption);
        Assert.Equal(p.Summary.PriceFinal, a.PriceFinal);
        Assert.Equal(Tue10, a.StartsAt);

        var client = Assert.Single(h.Chat.CreatedClients);
        Assert.Equal("Анна Коваль", client.Name);
        Assert.Equal(client.Id, a.ClientId);
        Assert.Equal(client.Id, h.Chat.ClientOf(conv));
        var stored = h.Chat.Get(p.ProposalId);
        Assert.Equal("confirmed", stored.Status);
        Assert.Equal(a.Id, stored.AppointmentId);
        Assert.Equal(client.Id, stored.ClientId);
        Assert.Contains(PublicTokenService.Hash("chat:" + p.ProposalId), h.Store.Keys.Keys);
    }

    [Fact]
    public async Task confirm_without_client_confirmation_creates_nothing()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        var r = await h.ConfirmAsync(conv, p.ProposalId, confirmed: false);
        Assert.Equal("confirmation_required", r.Error!.Code);
        Assert.Empty(h.Store.Snapshot());
        Assert.Equal(0, h.Chat.ClientCount);
        Assert.Equal("shown", h.Chat.Get(p.ProposalId).Status);
    }

    [Fact]
    public async Task confirm_requires_a_shown_proposal()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = (await h.PrepareAsync(conv)).Value!;
        Assert.Equal("proposal_not_shown", (await h.ConfirmAsync(conv, p.ProposalId)).Error!.Code);
        Assert.Empty(h.Store.Snapshot());
        Assert.Equal(0, h.Chat.ClientCount);
    }

    [Fact]
    public async Task confirm_on_existing_conversation_client_does_not_create_another_client()
    {
        var h = new Harness();
        var existing = Guid.NewGuid();
        var conv = h.Chat.NewConversation(existing);
        var p = await h.ShownAsync(conv);
        var r = await h.ConfirmAsync(conv, p.ProposalId);
        Assert.Equal(existing, r.Value!.Appointment.ClientId);
        Assert.Equal(0, h.Chat.ClientCount);
    }

    [Fact]
    public async Task confirm_with_changed_price_returns_price_changed_and_creates_nothing()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        h.Fake.Promotions.Add(new PromotionRule(Guid.NewGuid(), "Знижка", "percent", 10, null, null, true, [], []));

        var r = await h.ConfirmAsync(conv, p.ProposalId);

        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.Equal("price_changed", r.Error.Code);
        Assert.Empty(h.Store.Snapshot());
        var stored = h.Chat.Get(p.ProposalId);
        Assert.Equal("failed", stored.Status);
        Assert.Equal("price_changed", stored.FailureCode);
    }

    [Fact]
    public async Task confirm_when_slot_taken_in_between_returns_slot_unavailable()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        h.Fake.Appointments.Add(Appt(Tue10, "confirmed"));

        var r = await h.ConfirmAsync(conv, p.ProposalId);

        Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
        Assert.Equal("slot_unavailable", r.Error.Code);
        Assert.Empty(h.ChatAppointments());
        Assert.Equal("failed", h.Chat.Get(p.ProposalId).Status);
        Assert.Equal("slot_unavailable", h.Chat.Get(p.ProposalId).FailureCode);
    }

    [Theory]
    [InlineData("closed_weekday")]
    [InlineData("closure_date")]
    [InlineData("location_hours")]
    [InlineData("absence")]
    [InlineData("service_not_assigned")]
    public async Task confirm_masks_closure_hours_and_specialist_reasons_as_slot_unavailable(string reason)
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        var date = new DateOnly(2030, 1, 8);
        switch (reason)
        {
            case "closed_weekday": h.Fake.ClosedWeekdays = ["tue"]; break;
            case "closure_date": h.Fake.Closures = [new AbsenceSpan(date, date)]; break;
            case "location_hours": h.Fake.LocationHours = """{"tue":[{"from":"13:00","to":"17:00"}]}"""; break;
            case "absence": h.Fake.Absences = [new AbsenceSpan(date, date)]; break;
            case "service_not_assigned": h.Fake.AssignedServices = []; break;
        }
        var r = await h.ConfirmAsync(conv, p.ProposalId);
        Assert.Equal("slot_unavailable", r.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, r.Error.Kind);
        Assert.Empty(h.ChatAppointments());
    }

    [Fact]
    public async Task confirm_after_ttl_expires_the_proposal()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        h.Clock.Now = p.ExpiresAt; // межа: now >= expires_at

        var r = await h.ConfirmAsync(conv, p.ProposalId);

        Assert.Equal("proposal_expired", r.Error!.Code);
        Assert.Equal("expired", h.Chat.Get(p.ProposalId).Status);
        Assert.Empty(h.Store.Snapshot());
        Assert.Equal(0, h.Chat.ClientCount);
        Assert.Equal("proposal_expired", (await h.ConfirmAsync(conv, p.ProposalId)).Error!.Code);
    }

    [Fact]
    public async Task confirm_just_before_ttl_succeeds()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        h.Clock.Now = p.ExpiresAt.AddSeconds(-1);
        Assert.True((await h.ConfirmAsync(conv, p.ProposalId)).IsOk);
    }

    [Fact]
    public async Task repeated_confirm_returns_the_same_booking_without_creating_a_new_one()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        var first = (await h.ConfirmAsync(conv, p.ProposalId)).Value!;
        var again = (await h.ConfirmAsync(conv, p.ProposalId)).Value!;

        Assert.False(first.AlreadyConfirmed);
        Assert.True(again.AlreadyConfirmed);
        Assert.Equal(first.Appointment.Id, again.Appointment.Id);
        Assert.Single(h.ChatAppointments());
        Assert.Equal(1, h.Chat.ClientCount);
    }

    [Fact]
    public async Task confirm_retry_after_crash_between_cas_and_booking_does_not_duplicate()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        var first = (await h.ConfirmAsync(conv, p.ProposalId)).Value!;
        // Падіння після створення запису до CompleteAsync: пропозиція confirmed без appointment_id.
        h.Chat.ResetAppointmentLink(p.ProposalId);

        var again = (await h.ConfirmAsync(conv, p.ProposalId)).Value!;

        Assert.Equal(first.Appointment.Id, again.Appointment.Id);
        Assert.Single(h.ChatAppointments());
        Assert.Equal(first.Appointment.Id, h.Chat.Get(p.ProposalId).AppointmentId);
    }

    [Fact]
    public async Task confirm_restarts_after_crash_before_booking_once_stale()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        Assert.True(await h.Chat.TryTransitionAsync(conv, p.ProposalId, "shown", "confirmed", null, Now, default)); // CAS без запису
        h.Clock.Now = Now.AddMinutes(1);

        var r = await h.ConfirmAsync(conv, p.ProposalId);

        Assert.True(r.IsOk, r.Error?.Code);
        Assert.Single(h.ChatAppointments());
    }

    [Fact]
    public async Task confirm_of_foreign_conversation_or_wrong_token_is_not_found()
    {
        var h = new Harness();
        var convA = h.Chat.NewConversation();
        var convB = h.Chat.NewConversation();
        var p = await h.ShownAsync(convA);

        Assert.Equal("proposal_not_found", (await h.ConfirmAsync(convB, p.ProposalId)).Error!.Code);
        Assert.Equal("proposal_not_found", (await h.ConfirmAsync(convA, p.ProposalId, token: "00000000000000000000000000000000")).Error!.Code);
        Assert.Equal("proposal_not_found", (await h.ConfirmAsync(convA, Guid.NewGuid())).Error!.Code);
        Assert.Empty(h.Store.Snapshot());
        Assert.Equal(0, h.Chat.ClientCount);
        Assert.Equal("shown", h.Chat.Get(p.ProposalId).Status);

        Assert.True((await h.ConfirmAsync(convA, p.ProposalId, token: p.ConfirmationToken)).IsOk); // свій токен працює
    }

    [Fact]
    public async Task declined_superseded_and_failed_proposals_cannot_be_confirmed()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);
        Assert.True(await h.Svc.DeclineAsync(conv, p.ProposalId, default));
        Assert.Equal("proposal_not_active", (await h.ConfirmAsync(conv, p.ProposalId)).Error!.Code);

        var q = await h.ShownAsync(conv);
        Assert.Equal(1, await h.Svc.SupersedeAsync(conv, default));
        Assert.Equal("proposal_not_active", (await h.ConfirmAsync(conv, q.ProposalId)).Error!.Code);
        Assert.Empty(h.Store.Snapshot());
    }

    [Fact]
    public async Task parallel_confirms_of_one_proposal_create_exactly_one_booking()
    {
        var h = new Harness();
        var conv = h.Chat.NewConversation();
        var p = await h.ShownAsync(conv);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => h.ConfirmAsync(conv, p.ProposalId))));

        Assert.Single(h.ChatAppointments());
        Assert.Equal(1, h.Chat.ClientCount);
        Assert.Equal(1, results.Count(r => r.IsOk && !r.Value!.AlreadyConfirmed));
        Assert.All(results, r => Assert.True(r.IsOk || r.Error!.Code == "confirmation_in_progress", r.Error?.Code));
    }

    [Fact]
    public async Task parallel_confirms_of_different_conversations_for_the_same_slot_create_exactly_one_booking()
    {
        var h = new Harness();
        var convs = Enumerable.Range(0, 5).Select(_ => h.Chat.NewConversation()).ToList();
        var proposals = new List<(Guid Conv, Guid Id)>();
        foreach (var c in convs) proposals.Add((c, (await h.ShownAsync(c)).ProposalId));

        var results = await Task.WhenAll(proposals.Select(x => Task.Run(() => h.ConfirmAsync(x.Conv, x.Id))));

        Assert.Single(h.ChatAppointments());
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal("slot_unavailable", r.Error!.Code));
    }

    [Fact]
    public async Task limits_are_per_client_not_per_phone()
    {
        var h = new Harness();
        var heavy = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            h.Fake.Appointments.Add(Appt(new DateTimeOffset(2030, 1, 14, 9 + i, 0, 0, TimeSpan.Zero), "confirmed", heavy));
        var convHeavy = h.Chat.NewConversation(heavy);
        var convOther = h.Chat.NewConversation(); // інший клієнт (без телефону)

        var pHeavy = await h.ShownAsync(convHeavy);
        var r = await h.ConfirmAsync(convHeavy, pHeavy.ProposalId);
        Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
        Assert.Equal("booking_limit_reached", r.Error.Code);
        Assert.Equal(3, h.ChatAppointments().Count); // лише попередньо засіяні
        Assert.Equal("booking_limit_reached", h.Chat.Get(pHeavy.ProposalId).FailureCode);

        var pOther = await h.ShownAsync(convOther, Tue10.AddMinutes(60));
        Assert.True((await h.ConfirmAsync(convOther, pOther.ProposalId)).IsOk);
    }

    [Fact]
    public async Task hourly_creation_limit_counts_cancelled_bookings_of_the_client()
    {
        var h = new Harness();
        var client = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            h.Fake.Appointments.Add(Appt(new DateTimeOffset(2030, 1, 14, 9 + i, 0, 0, TimeSpan.Zero), "cancelled", client));
        var conv = h.Chat.NewConversation(client);
        var p = await h.ShownAsync(conv);
        Assert.Equal("booking_limit_reached", (await h.ConfirmAsync(conv, p.ProposalId)).Error!.Code);
    }

    // ---------- ім'я клієнта ----------

    [Theory]
    [InlineData("Анна", "Коваль", "anna", "Анна Коваль")]
    [InlineData("Анна", null, null, "Анна")]
    [InlineData(null, null, "@anna_k", "anna_k")]
    [InlineData("A", null, "x", ChatClientName.Fallback)]
    [InlineData(null, null, null, ChatClientName.Fallback)]
    [InlineData("  Ан\u0000на\n", " Ко\tваль ", null, "Анна Коваль")]
    public void client_name_from_telegram_profile(string? first, string? last, string? user, string expected) =>
        Assert.Equal(expected, ChatClientName.Build(new TelegramProfile(first, last, user)));

    [Fact]
    public void client_name_is_truncated_to_100_characters() =>
        Assert.Equal(100, ChatClientName.Build(new TelegramProfile(new string('Я', 300), null, null)).Length);

    // ---------- ExpectedPriceFinal у BookingService ----------

    [Fact]
    public async Task booking_service_expected_price_mismatch_is_409_price_changed_and_match_succeeds()
    {
        var h = new Harness();
        CreateAppointmentRequest Req(decimal? price) => new(FakeBookingStore.Location, FakeBookingStore.Specialist, FakeBookingStore.Service90, Tue10,
            new ClientInput(null, "Анна", "+380501234567", null), "none", "cash", "admin", price);

        var bad = await h.Booking.CreateAsync(Req(999m), default);
        Assert.Equal("price_changed", bad.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, bad.Error.Kind);
        Assert.Empty(h.Store.Snapshot());
        Assert.True((await h.Booking.CreateAsync(Req(1000m), default)).IsOk);
    }

    private static AppointmentDto Appt(DateTimeOffset start, string status, Guid? client = null) => new(
        Guid.NewGuid(), FakeBookingStore.Location, "Центр", FakeBookingStore.Specialist, "Марина", FakeBookingStore.Service90, "Манікюр",
        client ?? Guid.NewGuid(), "Клієнт", start, start.AddMinutes(90), 90, status, client is null ? "admin" : "telegram", 1000m, 1000m, null, "none", "cash");
}
