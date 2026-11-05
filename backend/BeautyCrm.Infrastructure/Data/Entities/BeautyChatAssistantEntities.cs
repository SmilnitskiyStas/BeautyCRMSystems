namespace BeautyCrm.Infrastructure.Data.Entities;

/// <summary>
/// Пропозиція запису з чату (TASK-724, §21.4/§21.6, ADR-011 §2.5). Статус: prepared, shown, потім
/// confirmed|rejected|expired|superseded|failed. Токен не зберігається, лише SHA-256 (hex, 64).
/// </summary>
public class ChatBookingProposal : TenantEntity
{
    public Guid ConversationId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid LocationId { get; set; }
    public Guid SpecialistId { get; set; }
    public Guid ServiceId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public int DurationMinutes { get; set; }
    public decimal PriceOriginal { get; set; }
    public decimal PriceFinal { get; set; }
    public Guid? PromotionId { get; set; }
    /// <summary>TASK-776 (§26.3): купон і кампанія пропозиції (FK RESTRICT).</summary>
    public Guid? CouponId { get; set; }
    public Guid? CampaignId { get; set; }
    public string Status { get; set; } = "prepared";
    public string ConfirmationTokenHash { get; set; } = null!;
    public Guid? SummaryMessageId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? AppointmentId { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public Conversation? Conversation { get; set; }
    public Client? Client { get; set; }
    public Location? Location { get; set; }
    public Specialist? Specialist { get; set; }
    public Service? Service { get; set; }
    public Promotion? Promotion { get; set; }
    public Coupon? Coupon { get; set; }
    public Campaign? Campaign { get; set; }
    public Message? SummaryMessage { get; set; }
    public Appointment? Appointment { get; set; }
}

/// <summary>Offset getUpdates і lease споживача для каналу Telegram (TASK-724, §21.4). PK (tenant_id, channel_id).</summary>
public class ChannelPollState : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid ChannelId { get; set; }
    public long LastUpdateId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Channel? Channel { get; set; }
}
