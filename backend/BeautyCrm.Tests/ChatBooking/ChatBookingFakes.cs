using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Tests.Beauty;

namespace BeautyCrm.Tests.ChatBooking;

/// <summary>
/// Потокобезпечна обгортка над FakeBookingStore: усе під одним локом (емулює атомарність БД) і унікальність Idempotency-Key
/// (другий запис із тим самим ключем = Duplicated, як unique-індекс).
/// </summary>
internal sealed class SerializedBookingStore(FakeBookingStore inner) : IBookingStore
{
    private readonly object _gate = new();
    public FakeBookingStore Inner => inner;
    public Dictionary<string, (Guid AppointmentId, string RequestHash)> Keys { get; } = new();
    public int AddCalls;

    private T L<T>(Func<T> f) { lock (_gate) return f(); }

    public IdempotencyHit? Find(string keyHash) =>
        L(() => Keys.TryGetValue(keyHash, out var v) ? new IdempotencyHit(v.AppointmentId, v.RequestHash) : null);

    public List<AppointmentDto> Snapshot() => L(() => inner.Appointments.ToList());

    public Task<ServiceInfo?> GetServiceAsync(Guid serviceId, CancellationToken ct) => L(() => inner.GetServiceAsync(serviceId, ct));
    public Task<LocationInfo?> GetLocationAsync(Guid locationId, CancellationToken ct) => L(() => inner.GetLocationAsync(locationId, ct));
    public Task<IReadOnlyList<SpecialistSchedule>> GetSchedulesAsync(Guid locationId, Guid? specialistId, CancellationToken ct) =>
        L(() => inner.GetSchedulesAsync(locationId, specialistId, ct));
    public Task<IReadOnlyList<(Guid SpecialistId, TimeRange Range)>> GetBusyAsync(
        IReadOnlyCollection<Guid> specialistIds, DateTimeOffset from, DateTimeOffset to, Guid? excludeAppointmentId, CancellationToken ct) =>
        L(() => inner.GetBusyAsync(specialistIds, from, to, excludeAppointmentId, ct));
    public Task<decimal?> GetPriceAsync(Guid serviceId, Guid locationId, CancellationToken ct) => L(() => inner.GetPriceAsync(serviceId, locationId, ct));
    public Task<IReadOnlyList<PromotionRule>> GetPromotionRulesAsync(CancellationToken ct) => L(() => inner.GetPromotionRulesAsync(ct));
    public Task<Guid?> ResolveClientAsync(ClientInput client, CancellationToken ct) => L(() => inner.ResolveClientAsync(client, ct));

    public Task<StoreResult<AppointmentDto>> AddAppointmentAsync(NewAppointment appointment, CancellationToken ct)
    {
        lock (_gate)
        {
            AddCalls++;
            if (appointment.Public is { } p && Keys.ContainsKey(p.IdempotencyKeyHash)) return Task.FromResult(StoreResult<AppointmentDto>.Duplicated());
            var r = inner.AddAppointmentAsync(appointment, ct).Result;
            if (appointment.Public is { } pub && r.Value is { } v) Keys[pub.IdempotencyKeyHash] = (v.Id, pub.RequestHash);
            return Task.FromResult(r);
        }
    }

    public Task<AppointmentDto?> GetAppointmentAsync(Guid id, CancellationToken ct) => L(() => inner.GetAppointmentAsync(id, ct));
    public Task<IReadOnlyList<AppointmentDto>> ListAppointmentsAsync(DateTimeOffset from, DateTimeOffset to, Guid? locationId, Guid? specialistId, bool includeCancelled, CancellationToken ct) =>
        L(() => inner.ListAppointmentsAsync(from, to, locationId, specialistId, includeCancelled, ct));
    public Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset newStart, DateTimeOffset? reminderAt, CancellationToken ct) =>
        L(() => inner.RescheduleAsync(id, newStart, reminderAt, ct));
    public Task<AppointmentDto?> SetStatusAsync(Guid id, string status, CancellationToken ct) => L(() => inner.SetStatusAsync(id, status, ct));
    public Task<bool> TryClaimCancelAsync(Guid id, DateTimeOffset at, CancelOrigin origin, string? reason, CancellationToken ct) =>
        L(() => inner.TryClaimCancelAsync(id, at, origin, reason, ct));
    public Task ReleaseCancelAsync(Guid id, string previousStatus, CancellationToken ct) => L(() => inner.ReleaseCancelAsync(id, previousStatus, ct));
    public Task<AppointmentDto?> MarkCancelledAsync(Guid id, DateTimeOffset at, CancellationToken ct) => L(() => inner.MarkCancelledAsync(id, at, ct));
    public Task<PaymentRecord?> GetPaymentAsync(Guid appointmentId, CancellationToken ct) => L(() => inner.GetPaymentAsync(appointmentId, ct));
    public Task UpdatePaymentAsync(Guid paymentId, string status, string? providerPaymentId, DateTimeOffset? paidAt, CancellationToken ct) =>
        L(() => inner.UpdatePaymentAsync(paymentId, status, providerPaymentId, paidAt, ct));
}

internal sealed class FakePublicStore(SerializedBookingStore bookings) : IPublicBookingStore
{
    public Task<IReadOnlyList<PublicLocationDto>> ListActiveLocationsAsync(DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PublicLocationDto>>([new PublicLocationDto(FakeBookingStore.Location, "Центр", "Вул. 1", null, bookings.Inner.Timezone)]);
    public Task<IReadOnlyList<PublicSpecialistDto>> ListSpecialistsAsync(Guid locationId, Guid? serviceId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PublicSpecialistDto>>(locationId == FakeBookingStore.Location && (serviceId is null || serviceId == FakeBookingStore.Service90)
            ? [new PublicSpecialistDto(FakeBookingStore.Specialist, "Марина", "Майстер", null)] : []);
    public Task<IReadOnlyList<PublicServiceBase>> ListServicesAsync(Guid locationId, Guid? specialistId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PublicServiceBase>>(locationId == FakeBookingStore.Location && bookings.Inner.Price is { } price
            ? [new PublicServiceBase(FakeBookingStore.Service90, "Манікюр", null, null, bookings.Inner.ServiceDuration, price)] : []);
    public Task<Guid?> FindAppointmentIdByTokenHashAsync(string tokenHash, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<IdempotencyHit?> FindByIdempotencyKeyAsync(string keyHash, CancellationToken ct) => Task.FromResult(bookings.Find(keyHash));
    public Task<int> CountActiveOnlineByPhoneAsync(string phone, DateTimeOffset now, CancellationToken ct) => Task.FromResult(0);
    public Task<int> CountRecentOnlineByPhoneAsync(string phone, DateTimeOffset since, CancellationToken ct) => Task.FromResult(0);
}

internal sealed class FakeChatStore(SerializedBookingStore bookings) : IChatBookingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Guid?> _conversations = new();
    private readonly Dictionary<Guid, ChatProposal> _proposals = new();
    public List<(Guid Id, string Name)> CreatedClients { get; } = [];

    private static bool IsActive(ChatProposal p) => p.Status == "prepared" || p.Status == "shown";
    private static bool IsChatSource(string s) => s == "telegram" || s == "instagram";

    public Guid NewConversation(Guid? clientId = null)
    {
        var id = Guid.NewGuid();
        lock (_gate) _conversations[id] = clientId;
        return id;
    }

    public Guid? ClientOf(Guid conversationId) { lock (_gate) return _conversations[conversationId]; }
    public ChatProposal Get(Guid id) { lock (_gate) return _proposals[id]; }
    public void ResetAppointmentLink(Guid proposalId)
    {
        lock (_gate) _proposals[proposalId] = _proposals[proposalId] with { AppointmentId = null };
    }

    public int ClientCount { get { lock (_gate) return CreatedClients.Count; } }

    public Task<ChatConversationRef?> GetConversationAsync(Guid conversationId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_conversations.TryGetValue(conversationId, out var c) ? new ChatConversationRef(conversationId, c) : null);
    }

    public Task<ChatProposal> CreateProposalAsync(NewChatProposal p, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var x in _proposals.Values.Where(x => x.ConversationId == p.ConversationId && IsActive(x)).ToList())
                _proposals[x.Id] = x with { Status = "superseded", DecidedAt = p.CreatedAt };
            var created = new ChatProposal(p.Id, p.ConversationId, p.ClientId, p.LocationId, p.SpecialistId, p.ServiceId, p.StartsAt, p.DurationMinutes,
                p.PriceOriginal, p.PriceFinal, p.PromotionId, "prepared", p.TokenHash, null, p.ExpiresAt, null, null, p.CreatedAt, null);
            _proposals[p.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<ChatProposal?> GetProposalAsync(Guid conversationId, Guid proposalId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_proposals.TryGetValue(proposalId, out var p) && p.ConversationId == conversationId ? p : null);
    }

    public Task<ChatProposal?> FindActiveProposalAsync(Guid conversationId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_proposals.Values.FirstOrDefault(x => x.ConversationId == conversationId && IsActive(x)));
    }

    public Task<bool> MarkShownAsync(Guid conversationId, Guid proposalId, Guid summaryMessageId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_proposals.TryGetValue(proposalId, out var p) || p.ConversationId != conversationId || p.Status != "prepared") return Task.FromResult(false);
            _proposals[proposalId] = p with { Status = "shown", SummaryMessageId = summaryMessageId };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryTransitionAsync(Guid conversationId, Guid proposalId, string from, string to, string? failureCode, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_proposals.TryGetValue(proposalId, out var p) || p.ConversationId != conversationId || p.Status != from) return Task.FromResult(false);
            _proposals[proposalId] = p with { Status = to, FailureCode = failureCode, DecidedAt = at };
            return Task.FromResult(true);
        }
    }

    public Task<int> SupersedeActiveAsync(Guid conversationId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var x in _proposals.Values.Where(x => x.ConversationId == conversationId && IsActive(x)).ToList())
            {
                _proposals[x.Id] = x with { Status = "superseded", DecidedAt = at };
                n++;
            }
            return Task.FromResult(n);
        }
    }

    public Task<Guid> EnsureClientAsync(Guid conversationId, string fullName, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_conversations[conversationId] is { } existing) return Task.FromResult(existing);
            var id = Guid.NewGuid();
            CreatedClients.Add((id, fullName));
            _conversations[conversationId] = id;
            return Task.FromResult(id);
        }
    }

    public Task CompleteAsync(Guid conversationId, Guid proposalId, Guid appointmentId, Guid clientId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var p = _proposals[proposalId];
            if (p.ConversationId == conversationId) _proposals[proposalId] = p with { AppointmentId = appointmentId, ClientId = clientId, DecidedAt = at };
        }
        return Task.CompletedTask;
    }

    public Task<int> CountActiveByClientAsync(Guid clientId, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(bookings.Snapshot().Count(a => a.ClientId == clientId && IsChatSource(a.Source) && a.StartsAt > now
            && (a.Status == "pending" || a.Status == "confirmed")));

    public Task<int> CountRecentByClientAsync(Guid clientId, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(bookings.Snapshot().Count(a => a.ClientId == clientId && IsChatSource(a.Source)));
}
