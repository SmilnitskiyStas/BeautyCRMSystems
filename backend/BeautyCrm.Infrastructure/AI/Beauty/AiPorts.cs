namespace BeautyCrm.Infrastructure.AI.Beauty;

// Порти до Application: tools викликають сервіси, не БД. Реалізації надає Application (Хвиля B);
// до того — Fakes/.

public sealed record SlotDto(Guid StaffId, DateTimeOffset Start, DateTimeOffset End);
public sealed record PriceDto(Guid ServiceId, string Name, decimal Price, int DurationMinutes);
public sealed record PromotionDto(Guid Id, string Title, decimal DiscountPercent, DateTimeOffset? EndsAt);
public sealed record ClientContextDto(
    Guid ClientId, string Name, string? Notes, int Visits, DateTimeOffset? NextAppointment,
    bool MarketingConsent, bool Unsubscribed);
public sealed record AudienceMemberDto(Guid ClientId, string Channel, bool MarketingConsent, bool Unsubscribed);
public sealed record AppointmentCreated(Guid AppointmentId);
public sealed record PromotionProposal(Guid ProposalId);

public interface IFindFreeSlotsPort
{
    Task<IReadOnlyList<SlotDto>> FindAsync(Guid tenantId, Guid serviceId, Guid? staffId, DateOnly date, CancellationToken ct);
}
public interface ICreateAppointmentPort
{
    Task<AppointmentCreated> CreateAsync(Guid tenantId, Guid clientId, Guid serviceId, Guid staffId, DateTimeOffset start, CancellationToken ct);
    Task CancelAsync(Guid tenantId, Guid appointmentId, CancellationToken ct);
}
public interface IGetPricesPort
{
    Task<IReadOnlyList<PriceDto>> GetAsync(Guid tenantId, CancellationToken ct);
}
public interface IGetActivePromotionsPort
{
    Task<IReadOnlyList<PromotionDto>> GetAsync(Guid tenantId, CancellationToken ct);
}
public interface IGetClientContextPort
{
    Task<ClientContextDto?> GetAsync(Guid tenantId, Guid clientId, CancellationToken ct);
}
public interface IDraftReplyPort
{
    /// <summary>Зберігає чернетку відповіді в розмові (не надсилає).</summary>
    Task<Guid> SaveDraftAsync(Guid tenantId, Guid clientId, string text, CancellationToken ct);
    /// <summary>Надсилає клієнту (лише auto або після підтвердження людиною).</summary>
    Task SendAsync(Guid tenantId, Guid draftId, CancellationToken ct);
}
public interface ISuggestPromotionPort
{
    Task<PromotionProposal> ProposeAsync(Guid tenantId, string goal, decimal discountPercent, string text, CancellationToken ct);
}
public interface IBuildAudiencePort
{
    Task<IReadOnlyList<AudienceMemberDto>> BuildAsync(Guid tenantId, string segment, CancellationToken ct);
}

public interface IAiActionJournal
{
    Task AppendAsync(AiActionRecord record, CancellationToken ct);
    Task<AiActionRecord?> GetAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task UpdateAsync(AiActionRecord record, CancellationToken ct);
    /// <summary>
    /// Атомарний compare-and-swap статусу (UPDATE ... WHERE status = from): true лише для одного з паралельних викликів.
    /// Захищає approve / reject / revert від подвійного виконання.
    /// </summary>
    Task<bool> TryTransitionAsync(Guid tenantId, Guid id, string from, string to, CancellationToken ct);
}
