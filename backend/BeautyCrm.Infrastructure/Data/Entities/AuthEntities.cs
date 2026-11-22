namespace BeautyCrm.Infrastructure.Data.Entities;

/// <summary>
/// Бізнес-клієнт платформи (салон, мережа). Створюється лише оператором платформи.
/// Не має tenant_id: сам Id є ідентифікатором tenant. RLS: рядок видно лише власному tenant
/// (+ вузька SELECT-політика пошуку за slug під час логіну).
/// </summary>
public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    /// <summary>Стабільний ідентифікатор для логіну (латиниця, цифри, дефіс).</summary>
    public string Slug { get; set; } = null!;
    /// <summary>Увімкнені модулі (beauty_booking, ...). Порожній масив = жоден модуль.</summary>
    public string[] Modules { get; set; } = [];
    /// <summary>active | suspended.</summary>
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>ADR-018: id підприємства в зовнішній системі (ShelfGuard). NULL для tenant, створених вручну.</summary>
    public string? ExternalRef { get; set; }
    /// <summary>ADR-018: джерело <see cref="ExternalRef"/> (лише 'shelfguard'). Задається разом із ExternalRef.</summary>
    public string? ExternalSource { get; set; }
    /// <summary>ADR-018: коли призупинено; NULL для активного tenant (CHECK: не NULL лише при status = suspended).</summary>
    public DateTimeOffset? SuspendedAt { get; set; }
    /// <summary>ADR-018: причина призупинення (до 500 символів).</summary>
    public string? SuspendedReason { get; set; }
}

/// <summary>Користувач tenant. Роль: owner | admin | deputy | specialist. Пароль зберігається лише як хеш.</summary>
public class User : TenantEntity
{
    /// <summary>Нормалізований (trim + lower) email; унікальний у межах tenant.</summary>
    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    /// <summary>ADR-018: NULL лише для користувача в стані <see cref="State"/> = invited (CHECK у БД). Вхід без хеша неможливий.</summary>
    public string? PasswordHash { get; set; }
    /// <summary>ADR-018: active | invited. invited = власник створений control plane і ще не задав пароль.</summary>
    public string State { get; set; } = "active";
    public string Role { get; set; } = null!;
    /// <summary>Для ролі specialist: профіль майстра, чиї записи бачить користувач.</summary>
    public Guid? SpecialistId { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>ADR-023: +1 за кожної зміни ролі, прав і активності (оптимістичне блокування й синхронізація UI). Починається з 1.</summary>
    public int AccessVersion { get; set; } = 1;
    /// <summary>ADR-024: глобальна ідентичність (людина) цього членства. NOT NULL у БД; пароль і блокування живуть у identities.
    /// Прямого доступу до таблиці identities застосунок не має (deny-all), id повертають лише функції auth_*.</summary>
    public Guid IdentityId { get; set; }
    /// <summary>ADR-024 §31.6 (F-6): токени, видані раніше за цей момент, недійсні (скидання пароля, злиття, звільнення пошти). NULL = без обмеження.</summary>
    public DateTimeOffset? SessionsValidAfter { get; set; }

    public Specialist? Specialist { get; set; }
}

/// <summary>
/// ADR-024 §2.1: глобальна ідентичність (людина): нормалізована пошта, пароль, стан. Без tenant_id, ENABLE + FORCE RLS,
/// політики лише TO beautycrm_identity; доступ застосунку лише через SECURITY DEFINER-функції auth_* (контракт §31.16).
/// Блокування входу тут немає: його тримає <see cref="AuthLoginAttempt"/> за ключем пошти (F-2).
/// </summary>
public class AuthIdentity
{
    public Guid Id { get; set; }
    /// <summary>lower(btrim()); унікальна на платформі.</summary>
    public string Email { get; set; } = null!;
    /// <summary>NULL лише для стану pending (власник ще не задав пароль).</summary>
    public string? PasswordHash { get; set; }
    /// <summary>pending | active.</summary>
    public string State { get; set; } = "active";
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>ADR-024 §2.5: квиток вибору бізнесу. У БД лише SHA-256 токена; одноразовий, коротка TTL. Deny-all, лише функції auth_login_ticket_*.</summary>
public class AuthLoginTicket
{
    public Guid Id { get; set; }
    public Guid IdentityId { get; set; }
    public byte[] TokenHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// ADR-024 §2.8 / контракт §31.7: запит скидання пароля й черга доставки листів (ADR-003, патерн черги-таблиці).
/// Сирого токена в БД немає ніколи (F-9): <see cref="TokenHash"/> NULL до першої спроби доставки, далі лише sha256.
/// Deny-all; воркер працює лише через auth_password_reset_claim_delivery / attach_token / mark_delivery.
/// </summary>
public class AuthPasswordReset
{
    public Guid Id { get; set; }
    public Guid IdentityId { get; set; }
    /// <summary>HMAC-ключ пошти (§31.1) для зняття блокування при consume.</summary>
    public byte[] LoginKey { get; set; } = null!;
    public byte[]? TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    /// <summary>pending | sent | failed (лист із посиланням).</summary>
    public string DeliveryStatus { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>Код помилки (без токена й пошти).</summary>
    public string? LastError { get; set; }
    /// <summary>none | pending | sent | failed (лист «Пароль змінено», §31.7.3).</summary>
    public string NoticeStatus { get; set; } = "none";
    public int NoticeAttempts { get; set; }
    public DateTimeOffset? NoticeNextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// ADR-024 errata E-2 (F-2): єдиний глобальний лічильник невдалих входів за ключем пошти (HMAC-SHA256 з pepper, 32 байти):
/// однаково для наявної, невідомої й pending пошти. Колонок пошти чи identity_id немає. Deny-all, лише функції auth_login_attempt_*.
/// </summary>
public class AuthLoginAttempt
{
    public byte[] LoginKey { get; set; } = null!;
    public int FailedCount { get; set; }
    public DateTimeOffset WindowStartedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// ADR-023 §2.4: поточне (матеріалізоване) значення права користувача. Ключ <c>блок.підпункт</c>, рівень з каталогу в коді;
/// БД перевіряє лише формат. Для owner рядків немає (всі права неявні). Відсутній рядок = найнижчий рівень.
/// </summary>
public class UserPermission : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Permission { get; set; } = null!;
    public string Level { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>NULL = система/міграція.</summary>
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// ADR-023 §2.8 + §30.9.6 (Q-C2): append-only журнал змін доступу. Рядок має цільового користувача або запрошення
/// (CHECK). Без ПІІ клієнтів, паролів, токенів і email запрошених. UPDATE/DELETE заборонені тригером і GRANT-ами.
/// </summary>
public class UserAccessAuditRow : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>NULL = міграція/система.</summary>
    public Guid? ActorUserId { get; set; }
    /// <summary>NULL лише для рядків запрошення (тоді заповнено <see cref="InviteId"/>).</summary>
    public Guid? TargetUserId { get; set; }
    public Guid? InviteId { get; set; }
    /// <summary>permissions_changed | role_changed | preset_reset | invited | invite_revoked | invite_accepted | activated | deactivated | migrated.</summary>
    public string Action { get; set; } = null!;
    /// <summary>jsonb: масив [{permission, from, to}] або об'єкт {role: {from, to}}.</summary>
    public string Changes { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Запрошення адміністратора/заступника/спеціаліста. У БД лише SHA-256 хеш токена.</summary>
public class Invite : TenantEntity
{
    public string Email { get; set; } = null!;
    public string Role { get; set; } = null!;
    public Guid? SpecialistId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? InvitedByUserId { get; set; }
    /// <summary>ADR-023 (B1): jsonb-об'єкт {ключ: рівень}, ПОВНИЙ матеріалізований знімок прав, обрізаний стелею запрошувача. NOT NULL;
    /// "{}" (за замовчуванням БД) = усі найнижчі рівні (fail-closed).</summary>
    public string Permissions { get; set; } = "{}";

    public User? InvitedBy { get; set; }
}

/// <summary>Refresh-токен (ротація при кожному використанні). У БД лише SHA-256 хеш.</summary>
public class RefreshToken : TenantEntity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public User? User { get; set; }
}
