using System.Net;

namespace BeautyCrm.Infrastructure.Data.Entities;

// ADR-018 (TASK-816): control plane ShelfGuard. Перші три сутності - ПЛАТФОРМНІ таблиці без tenant RLS
// (доступ лише з platform/auth-шляху); PlatformFeedback - звичайна tenant-таблиця (ENABLE + FORCE RLS).
// Жодна з платформних сутностей не реалізує ITenantEntity: tenant_id не штампується автоматично.

/// <summary>Одноразове запрошення власника. У БД лише sha256(token) (32 байти); токен ніколи не зберігається.</summary>
public class PlatformOwnerInvite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public byte[] TokenHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Хто видав: "shelfguard:{actor.id}" або "platform_key". Без PII.</summary>
    public string CreatedByActor { get; set; } = null!;
}

/// <summary>
/// Журнал platform-дій. Append-only: UPDATE/DELETE заборонено тригером і відкликано в ролі застосунку (grants).
/// <see cref="Details"/> не містить секретів, токенів, паролів, email (лише user_id) і тексту заявок.
/// </summary>
public class PlatformAuditRow
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>Див. CHECK ck_platform_audit_log_action.</summary>
    public string Action { get; set; } = null!;
    /// <summary>Без FK: журнал переживає будь-які зміни реєстру tenant.</summary>
    public Guid? TenantId { get; set; }
    /// <summary>shelfguard_admin | platform_key | owner | system.</summary>
    public string ActorType { get; set; } = null!;
    public string? ActorId { get; set; }
    public string? ActorDisplay { get; set; }
    /// <summary>kid ключа control plane (не секрет і не відбиток).</summary>
    public string? KeyId { get; set; }
    public string? RequestId { get; set; }
    public IPAddress? Ip { get; set; }
    /// <summary>JSON-об'єкт (jsonb). За замовчуванням "{}".</summary>
    public string Details { get; set; } = "{}";
}

/// <summary>Використані nonce control plane (захист від replay). PK (key_id, nonce); очищення > 15 хв робить worker.</summary>
public class ControlPlaneNonce
{
    public string KeyId { get; set; } = null!;
    public string Nonce { get; set; } = null!;
    public DateTimeOffset SeenAt { get; set; }
}

/// <summary>
/// Заявка «співпраця / покращити / не працює» з адмінки Beauty для провайдера (ADR-018 §3.4). Рядок одночасно є
/// чергою доставки (немає окремої outbox-таблиці): worker бере pending з next_attempt_at &lt;= now().
/// </summary>
public class PlatformFeedback : TenantEntity
{
    public Guid UserId { get; set; }
    /// <summary>cooperation | improvement | bug.</summary>
    public string Type { get; set; } = null!;
    /// <summary>low | normal | high | critical.</summary>
    public string Severity { get; set; } = "normal";
    public string Message { get; set; } = null!;
    public string? PageUrl { get; set; }
    /// <summary>pending | delivered | failed.</summary>
    public string DeliveryStatus { get; set; } = "pending";
    public int Attempts { get; set; }
    /// <summary>Коли worker може повторити доставку (експоненційна затримка).</summary>
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>Короткий код/клас помилки доставки (до 200 символів); без тексту заявки й секретів.</summary>
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? ExternalLeadId { get; set; }

    public User? User { get; set; }
}
