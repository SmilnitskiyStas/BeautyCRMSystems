namespace BeautyCrm.Infrastructure.Data.Entities;

/// <summary>Every beauty_* row belongs to exactly one tenant; RLS filters by app.tenant_id.</summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}

public abstract class TenantEntity : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ---------- Catalog: locations, specialists, services ----------

public class Location : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    /// <summary>IANA timezone id, e.g. "Europe/Kyiv".</summary>
    public string Timezone { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}

public class Specialist : TenantEntity
{
    public string FullName { get; set; } = null!;
    public string? Title { get; set; }
    /// <summary>Посада (керування працівниками, TASK-691); для публічного запису пріоритетніша за Title.</summary>
    public string? Position { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Послуга, призначена майстру. Майстер пропонує лише призначені послуги. PK (tenant_id, specialist_id, service_id).</summary>
public class SpecialistServiceLink : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid SpecialistId { get; set; }
    public Guid ServiceId { get; set; }

    public Specialist? Specialist { get; set; }
    public Service? Service { get; set; }
}

/// <summary>
/// Відсутність майстра повними днями (включно, у часовій зоні закладу). Type: sick|vacation|day_off|other;
/// Status: requested|approved|rejected|cancelled. Note — ЧУТЛИВІ дані: не логувати, не віддавати без права.
/// </summary>
public class SpecialistAbsence : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SpecialistId { get; set; }
    public string Type { get; set; } = null!;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string Status { get; set; } = null!;
    public string? Note { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    /// <summary>Хто й коли скасував (status = cancelled; TASK-696). null для відсутностей, скасованих до цієї міграції.</summary>
    public Guid? CancelledByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Specialist? Specialist { get; set; }
}

/// <summary>Specialist working at a location + weekly schedule there (jsonb).</summary>
public class SpecialistLocation : TenantEntity
{
    public Guid SpecialistId { get; set; }
    public Guid LocationId { get; set; }
    /// <summary>jsonb: weekly working hours at this location.</summary>
    public string? WorkingHours { get; set; }
    public bool IsActive { get; set; } = true;

    public Specialist? Specialist { get; set; }
    public Location? Location { get; set; }
}

public class Service : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public int DurationMinutes { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>LocationId == null: network-wide price; otherwise a per-location override.</summary>
public class ServicePrice : TenantEntity
{
    public Guid ServiceId { get; set; }
    public Guid? LocationId { get; set; }
    public decimal Price { get; set; }

    public Service? Service { get; set; }
    public Location? Location { get; set; }
}

// ---------- Clients ----------

public class Client : TenantEntity
{
    public string FullName { get; set; } = null!;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DateOnly? BirthDate { get; set; }
    /// <summary>Згода на маркетингові розсилки (AI-аудиторії/кампанії працюють лише зі згодою).</summary>
    public bool MarketingConsent { get; set; }
    public bool Unsubscribed { get; set; }
    /// <summary>Soft delete marker (personal data: keep history, hide from lists).</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

public class ClientNote : TenantEntity
{
    public Guid ClientId { get; set; }
    /// <summary>Platform user id of the author (users live outside beauty_* schema).</summary>
    public Guid? AuthorUserId { get; set; }
    public string Body { get; set; } = null!;

    public Client? Client { get; set; }
}

// ---------- Promotions ----------

public class Promotion : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DiscountType DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public class PromotionLocation : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid PromotionId { get; set; }
    public Guid LocationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Promotion? Promotion { get; set; }
    public Location? Location { get; set; }
}

public class PromotionService : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid PromotionId { get; set; }
    public Guid ServiceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Promotion? Promotion { get; set; }
    public Service? Service { get; set; }
}

// ---------- Appointments ----------

public class Appointment : TenantEntity
{
    public Guid LocationId { get; set; }
    public Guid SpecialistId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid ClientId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    /// <summary>Copied from the service at booking time.</summary>
    public int DurationMinutes { get; set; }
    /// <summary>Set by DB trigger: starts_at + duration_minutes. Read-only for the app.</summary>
    public DateTimeOffset EndsAt { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Pending;
    public AppointmentSource Source { get; set; }
    public decimal PriceOriginal { get; set; }
    public decimal PriceFinal { get; set; }
    public Guid? PromotionId { get; set; }
    public ReminderOption ReminderOption { get; set; } = ReminderOption.None;
    public PaymentMethod? PaymentMethod { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    /// <summary>Хто скасував (TASK-697): client | staff | system; null лише для нескасованих.</summary>
    public string? CancelledByType { get; set; }
    /// <summary>Користувач-керівник/працівник, що скасував (лише для staff).</summary>
    public Guid? CancelledByUserId { get; set; }
    /// <summary>Необов'язкова причина скасування (до 300 символів); чутливе — лише керівникам.</summary>
    public string? CancelReason { get; set; }
    /// <summary>SHA-256 (hex) публічного токена перегляду/скасування запису (TASK-688); сам токен не зберігається.</summary>
    public string? PublicTokenHash { get; set; }
    /// <summary>SHA-256 (hex) заголовка Idempotency-Key публічного POST; унікальний у межах tenant.</summary>
    public string? IdempotencyKeyHash { get; set; }
    /// <summary>SHA-256 (hex) відбитка тіла запиту: повтор ключа з іншим тілом відхиляється.</summary>
    public string? IdempotencyRequestHash { get; set; }

    public Location? Location { get; set; }
    public Specialist? Specialist { get; set; }
    public Service? Service { get; set; }
    public Client? Client { get; set; }
    public Promotion? Promotion { get; set; }
}

// ---------- Channels & conversations ----------

public class Channel : TenantEntity
{
    public Guid? LocationId { get; set; }
    public ChannelType Type { get; set; }
    public string Name { get; set; } = null!;
    /// <summary>Ciphertext only — encryption/decryption happens in the channels integration layer.</summary>
    public string? CredentialsEncrypted { get; set; }
    /// <summary>Last 4 chars of the secret, for masked display in the API.</summary>
    public string? CredentialsLast4 { get; set; }
    /// <summary>jsonb: non-secret channel settings.</summary>
    public string? Settings { get; set; }
    public bool IsActive { get; set; } = true;

    public Location? Location { get; set; }
}

public class Conversation : TenantEntity
{
    public Guid ChannelId { get; set; }
    public Guid? ClientId { get; set; }
    /// <summary>Chat/thread id in the external channel.</summary>
    public string ExternalChatId { get; set; } = null!;
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public DateTimeOffset? LastMessageAt { get; set; }

    public Channel? Channel { get; set; }
    public Client? Client { get; set; }
}

public class Message : TenantEntity
{
    public Guid ConversationId { get; set; }
    public MessageDirection Direction { get; set; }
    public MessageSenderType SenderType { get; set; }
    public string Body { get; set; } = null!;
    public string? ExternalMessageId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    /// <summary>received (inbound) | draft | pending (outbox) | sent | failed.</summary>
    public string Status { get; set; } = "received";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>Dedup of webhook redeliveries, e.g. "telegram:12345"; unique per tenant when set.</summary>
    public string? IdempotencyKey { get; set; }

    public Conversation? Conversation { get; set; }
}

public class AiAction : TenantEntity
{
    public Guid? ConversationId { get; set; }
    public Guid? AppointmentId { get; set; }
    public string ToolName { get; set; } = null!;
    /// <summary>jsonb: tool arguments proposed by the AI.</summary>
    public string Payload { get; set; } = null!;
    public AiActionStatus Status { get; set; } = AiActionStatus.Proposed;
    /// <summary>jsonb: execution result.</summary>
    public string? Result { get; set; }
    public string? Error { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }

    public Conversation? Conversation { get; set; }
    public Appointment? Appointment { get; set; }
}

// ---------- Reminders & payments ----------

public class Reminder : TenantEntity
{
    public Guid AppointmentId { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public ReminderStatus Status { get; set; } = ReminderStatus.Scheduled;
    public DateTimeOffset? SentAt { get; set; }
    public string? Error { get; set; }

    public Appointment? Appointment { get; set; }
}

public class Payment : TenantEntity
{
    public Guid AppointmentId { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? Provider { get; set; }
    public string? ProviderPaymentId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    public Appointment? Appointment { get; set; }
}

// ---------- Cancellation settings (TASK-685): один рядок на tenant, відсутній рядок = значення за замовчуванням ----------

public class CancellationSettingsRow : TenantEntity
{
    public int WindowHours { get; set; } = 12;
    public int RefundPercentInWindow { get; set; } = 50;
    public int RefundPercentOutside { get; set; } = 100;
    public bool DeductFee { get; set; }
    public int FeePercent { get; set; }
}
