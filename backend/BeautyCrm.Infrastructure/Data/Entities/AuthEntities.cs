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

/// <summary>Користувач tenant. Роль: owner | admin | specialist. Пароль зберігається лише як хеш.</summary>
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

    public Specialist? Specialist { get; set; }
}

/// <summary>Запрошення адміністратора/спеціаліста. У БД лише SHA-256 хеш токена.</summary>
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
