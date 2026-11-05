namespace BeautyCrm.Infrastructure.Data.Entities;

// TASK-736 (§22.3, ADR-012): вподобання клієнта, задачі «зателефонувати», налаштування нагадувань tenant.
// Персональних даних тут немає за дизайном: телефон і імена не копіюються (береться з beauty_clients у момент запиту),
// skip_reason / closed_reason - лише коди; note задачі - єдиний вільний текст (≤ 300), його не логують.

/// <summary>
/// Вподобання нагадувань клієнта, 1:1 (PK tenant_id + client_id). Відсутній рядок = «не обрано» (не те саме, що method = none).
/// </summary>
public class ClientContactPreference : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    /// <summary>none | telegram | call.</summary>
    public string Method { get; set; } = null!;
    /// <summary>Підмножина {60,120,1440}; для call рівно одна точка; для none порожньо.</summary>
    public short[] OffsetsMinutes { get; set; } = [];
    public Guid? TelegramConversationId { get; set; }
    /// <summary>chat | admin | stop | web.</summary>
    public string Source { get; set; } = null!;
    /// <summary>Лише для source = admin.</summary>
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Client? Client { get; set; }
}

/// <summary>Задача адміністратору «зателефонувати клієнту». Телефон не копіюється (читається з клієнта в момент запиту).</summary>
public class CallTask : TenantEntity
{
    public Guid AppointmentId { get; set; }
    public Guid ClientId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? ReminderId { get; set; }
    /// <summary>reminder | delivery_failed.</summary>
    public string Reason { get; set; } = null!;
    public short? OffsetMinutes { get; set; }
    public DateTimeOffset DueAt { get; set; }
    /// <summary>open | done | skipped | cancelled.</summary>
    public string Status { get; set; } = "open";
    /// <summary>Лише код (replanned, appointment_inactive, ...).</summary>
    public string? ClosedReason { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Вільний текст адміністратора (≤ 300); чутливе, не логувати.</summary>
    public string? Note { get; set; }

    public Appointment? Appointment { get; set; }
    public Client? Client { get; set; }
    public Location? Location { get; set; }
    public Reminder? Reminder { get; set; }
}

/// <summary>Налаштування нагадувань tenant (PK tenant_id). Немає рядка = значення за замовчуванням у коді (22:00-08:00, обидва канали увімкнено).</summary>
public class ReminderSettingsRow : ITenantEntity
{
    public Guid TenantId { get; set; }
    public bool TelegramEnabled { get; set; } = true;
    public bool CallEnabled { get; set; } = true;
    public TimeOnly QuietStart { get; set; } = new(22, 0);
    public TimeOnly QuietEnd { get; set; } = new(8, 0);
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
