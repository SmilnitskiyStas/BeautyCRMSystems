namespace BeautyCrm.Infrastructure.Data.Entities;

/// <summary>
/// Інлайн-клавіатура вихідного повідомлення (TASK-749, §23.3, ADR-013): одна клавіатура = одне повідомлення.
/// Токен (22 символи base64url, 128 біт) іде в callback_data; дані дії беруться лише з <see cref="Options"/>, пропозиції й запису.
/// Статус: active, потім used|expired|revoked. Токен і options не віддаються жодним API.
/// </summary>
public class ChatKeyboard : TenantEntity
{
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    /// <summary>booking_confirm | slot_pick | reminder_choice | appointment_list | appointment_actions | cancel_confirm | marketing_consent | campaign_offer.</summary>
    public string Kind { get; set; } = null!;
    public string Token { get; set; } = null!;
    /// <summary>jsonb-масив (≤ 10 елементів): дозволені кнопки з серверними посиланнями. Пише і читає лише код.</summary>
    public string Options { get; set; } = "[]";
    public Guid? ProposalId { get; set; }
    public Guid? AppointmentId { get; set; }
    /// <summary>TASK-776 (§26.3): обов'язковий для kind=campaign_offer.</summary>
    public Guid? CampaignRecipientId { get; set; }
    /// <summary>active | used | expired | revoked.</summary>
    public string Status { get; set; } = "active";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public short? UsedOption { get; set; }
    public Guid? UsedByMessageId { get; set; }
    public DateTimeOffset? MarkupClearedAt { get; set; }
    public short ClearAttempts { get; set; }

    public Conversation? Conversation { get; set; }
    public Message? Message { get; set; }
    public ChatBookingProposal? Proposal { get; set; }
    public Appointment? Appointment { get; set; }
    public CampaignRecipient? CampaignRecipient { get; set; }
    public Message? UsedByMessage { get; set; }
}
