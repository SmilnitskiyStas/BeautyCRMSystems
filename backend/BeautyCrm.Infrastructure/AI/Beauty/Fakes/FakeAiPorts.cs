namespace BeautyCrm.Infrastructure.AI.Beauty.Fakes;

/// <summary>Fake-реалізації портів для тестів і локальної розробки, доки Application не надав справжні.</summary>
public sealed class FakeAiPorts :
    IFindFreeSlotsPort, ICreateAppointmentPort, IGetPricesPort, IGetActivePromotionsPort,
    IGetClientContextPort, IDraftReplyPort, ISuggestPromotionPort, IBuildAudiencePort
{
    public List<Guid> CreatedAppointments { get; } = new();
    public List<Guid> CancelledAppointments { get; } = new();
    public List<(Guid Id, string Text)> Drafts { get; } = new();
    public List<Guid> SentDrafts { get; } = new();
    public List<(string Goal, decimal Pct)> Proposals { get; } = new();
    public List<AudienceMemberDto> Audience { get; } = new();

    public Task<IReadOnlyList<SlotDto>> FindAsync(Guid t, Guid s, Guid? st, DateOnly d, CancellationToken ct)
    {
        var start = new DateTimeOffset(d.Year, d.Month, d.Day, 17, 0, 0, TimeSpan.Zero);
        return Task.FromResult<IReadOnlyList<SlotDto>>(new[] { new SlotDto(st ?? Guid.Empty, start, start.AddMinutes(90)) });
    }

    public Task<AppointmentCreated> CreateAsync(Guid t, Guid c, Guid s, Guid st, DateTimeOffset start, CancellationToken ct)
    {
        var id = Guid.NewGuid(); CreatedAppointments.Add(id);
        return Task.FromResult(new AppointmentCreated(id));
    }

    public Task CancelAsync(Guid t, Guid appointmentId, CancellationToken ct)
    {
        CancelledAppointments.Add(appointmentId); return Task.CompletedTask;
    }

    Task<IReadOnlyList<PriceDto>> IGetPricesPort.GetAsync(Guid t, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PriceDto>>(new[] { new PriceDto(Guid.Empty, "Манікюр з гель-лаком", 900m, 90) });

    Task<IReadOnlyList<PromotionDto>> IGetActivePromotionsPort.GetAsync(Guid t, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PromotionDto>>(new[] { new PromotionDto(Guid.Empty, "Будні -15%", 15m, null) });

    public Task<ClientContextDto?> GetAsync(Guid t, Guid clientId, CancellationToken ct) =>
        Task.FromResult<ClientContextDto?>(new ClientContextDto(clientId, "Олена", "Алергія на латекс", 24, null, true, false));

    public Task<Guid> SaveDraftAsync(Guid t, Guid c, string text, CancellationToken ct)
    {
        var id = Guid.NewGuid(); Drafts.Add((id, text)); return Task.FromResult(id);
    }

    public Task SendAsync(Guid t, Guid draftId, CancellationToken ct)
    {
        SentDrafts.Add(draftId); return Task.CompletedTask;
    }

    public Task<PromotionProposal> ProposeAsync(Guid t, string goal, decimal pct, string text, CancellationToken ct)
    {
        Proposals.Add((goal, pct)); return Task.FromResult(new PromotionProposal(Guid.NewGuid()));
    }

    public Task<IReadOnlyList<AudienceMemberDto>> BuildAsync(Guid t, string segment, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AudienceMemberDto>>(Audience.ToList());
}

public sealed class InMemoryAiActionJournal : IAiActionJournal
{
    private readonly List<AiActionRecord> _items = new();
    public IReadOnlyList<AiActionRecord> Items { get { lock (_items) return _items.ToList(); } }

    public Task AppendAsync(AiActionRecord r, CancellationToken ct)
    {
        lock (_items) _items.Add(r);
        return Task.CompletedTask;
    }

    public Task<AiActionRecord?> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        lock (_items) return Task.FromResult(_items.FirstOrDefault(x => x.Id == id && x.TenantId == tenantId));
    }

    public Task UpdateAsync(AiActionRecord r, CancellationToken ct)
    {
        lock (_items)
        {
            var i = _items.FindIndex(x => x.Id == r.Id && x.TenantId == r.TenantId);
            if (i >= 0) _items[i] = r;
        }
        return Task.CompletedTask;
    }
}
