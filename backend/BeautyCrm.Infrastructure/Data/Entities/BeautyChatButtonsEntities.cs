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
    /// <summary>TASK-836 (ADR-020 §28.10): для kind=flow_* - потік і його версія після переходу. NULL flow_id допустимий лише для не-active клавіатури.</summary>
    public Guid? FlowId { get; set; }
    public int? FlowVersion { get; set; }

    public ChatBookingFlow? Flow { get; set; }
    public Conversation? Conversation { get; set; }
    public Message? Message { get; set; }
    public ChatBookingProposal? Proposal { get; set; }
    public Appointment? Appointment { get; set; }
    public CampaignRecipient? CampaignRecipient { get; set; }
    public Message? UsedByMessage { get; set; }
}

/// <summary>
/// Автомат детермінованого запису в чаті (TASK-836, ADR-020 §2.4). Одна активна на розмову (частковий UNIQUE).
/// Рухається лише кодом; оптимістичний захист - <see cref="Version"/> (CAS WHERE version = @v AND status = 'active').
/// ПД у рядку немає, окрім зв'язку з розмовою.
/// </summary>
public class ChatBookingFlow : TenantEntity
{
    public Guid ConversationId { get; set; }
    /// <summary>active | completed | cancelled | expired | superseded.</summary>
    public string Status { get; set; } = "active";
    /// <summary>last_location | location | service | specialist | time | day | confirm (§28.3).</summary>
    public string Step { get; set; } = "location";
    public int Version { get; set; }
    public Guid? ServiceId { get; set; }
    public Guid? SpecialistId { get; set; }
    public Guid? LocationId { get; set; }
    /// <summary>Заклад останнього запису, запропонований кроком last_location (NULL = крок не показували). Координат немає.</summary>
    public Guid? SuggestedLastLocationId { get; set; }
    /// <summary>Локальна дата закладу.</summary>
    public DateOnly? Day { get; set; }
    /// <summary>Бажаний час доби (префіл LLM), локальний час закладу.</summary>
    public TimeOnly? PreferredTime { get; set; }
    /// <summary>Курсор початку слотів (крок time).</summary>
    public DateTimeOffset? CursorAt { get; set; }
    /// <summary>Курсор сторінок списків/днів.</summary>
    public string? ListCursor { get; set; }
    public string[] SkippedSteps { get; set; } = [];
    public Guid? ProposalId { get; set; }
    /// <summary>button | command | llm | text.</summary>
    public string Origin { get; set; } = "button";
    public string? CancelReason { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public Conversation? Conversation { get; set; }
    public Service? Service { get; set; }
    public Specialist? Specialist { get; set; }
    public Location? Location { get; set; }
    public Location? SuggestedLastLocation { get; set; }
    public ChatBookingProposal? Proposal { get; set; }
}
