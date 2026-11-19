namespace BeautyCrm.Infrastructure.Data.Entities;

// Склад, етап 2b: Telegram-сповіщення власнику (ADR-025 §12, контракт §32.24.2, TASK-895). Лише схема: сервіси/store - TASK-890/896.
// RLS ENABLE+FORCE, CHECK форми станів, append-only тригери журналу - в міграції beauty_inventory_stage2b.

/// <summary>
/// Прив'язка Telegram-чату співробітника (один рядок = одна спроба): issued -> claimed -> active -> revoked. У revoked дані чату стерто.
/// chat_id зберігається лише зашифрованим (ISecretProtector, ключ каналів) + HMAC-хеш для пошуку; без username/імені/телефону.
/// </summary>
public class StaffTelegramLink : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid ChannelId { get; set; }
    /// <summary>issued | claimed | active | revoked (CHECK форми кожного стану).</summary>
    public string Status { get; set; } = "issued";
    /// <summary>SHA-256 від 32 байт токена (32 байти); NULL після активації/відкликання.</summary>
    public byte[]? TokenHash { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    /// <summary>AES-256-GCM (формат beauty_channels.credentials_encrypted); NULL у issued і revoked.</summary>
    public string? ChatIdEncrypted { get; set; }
    /// <summary>HMAC-SHA256 для пошуку (32 байти); NULL разом із ChatIdEncrypted.</summary>
    public byte[]? ChatIdHash { get; set; }
    /// <summary>^inv-tg-v[0-9]+$.</summary>
    public string ConsentVersion { get; set; } = null!;
    public DateTimeOffset ConsentAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? BotConfirmedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Лише для revoked (CHECK): user_unlink | bot_stop | declined | replaced | expired | bot_blocked | chat_not_found | user_deactivated | role_changed | tenant_inactive.</summary>
    public string? RevokeReason { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }
}

/// <summary>Append-only журнал подій прив'язки (тригер + REVOKE). Без chat_id, хешу, токена, імені/username.</summary>
public class StaffTelegramLinkEvent : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LinkId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>issued | claimed | activated | declined | revoked | rejected.</summary>
    public string Event { get; set; } = null!;
    /// <summary>Для revoked - revoke_reason; для rejected - expired | used | channel_mismatch | chat_conflict | user_inactive | disabled (перевіряє застосунок; БД - довжина).</summary>
    public string? Reason { get; set; }
    /// <summary>user | bot | system.</summary>
    public string Actor { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Службова черга відправки (transactional outbox, ADR-003). Адреса (chat_id) береться з прив'язки в момент відправки.</summary>
public class StaffNotification : TenantEntity
{
    public Guid LinkId { get; set; }
    /// <summary>alert | digest.</summary>
    public string Kind { get; set; } = null!;
    /// <summary>1..4096 символів.</summary>
    public string Body { get; set; } = null!;
    /// <summary>pending | sending | sent | failed | cancelled.</summary>
    public string Status { get; set; } = "pending";
    /// <summary>0..10.</summary>
    public short Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>Лише код помилки (без тексту відповіді).</summary>
    public string? LastError { get; set; }
    public string? ExternalMessageId { get; set; }
    public string DedupeKey { get; set; } = null!;
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Які алерти пішли якому адресату (PK tenant+notification+alert; UNIQUE tenant+link+alert).</summary>
public class StaffNotificationAlert : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid AlertId { get; set; }
    public Guid LinkId { get; set; }
}
