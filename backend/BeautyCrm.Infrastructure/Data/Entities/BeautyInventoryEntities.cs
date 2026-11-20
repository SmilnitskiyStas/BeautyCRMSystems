namespace BeautyCrm.Infrastructure.Data.Entities;

// Склад (ADR-025, контракт §32.3, TASK-876). Кількості numeric(14,3), гроші numeric(12,2).
// RLS ENABLE+FORCE і ручні CHECK/індекси - в міграції beauty_inventory.

/// <summary>Постачальник (§32.3). Унікальне ім'я в tenant без урахування регістру (вираз-індекс у міграції).</summary>
public class Supplier : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Категорія товарів (дерево до 3 рівнів, ADR-025). ParentId NULL = корінь. Унікальне ім'я в межах батька без урахування регістру (вираз-індекс у міграції).</summary>
public class ProductCategory : TenantEntity
{
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = null!;
    public int SortOrder { get; set; }
    public bool IsArchived { get; set; }
}

/// <summary>Товар мережі (каталог). Рівні й партії - по закладах.</summary>
public class Product : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Category { get; set; }
    /// <summary>Категорія з дерева (beauty_product_categories); стара текстова Category лишається без змін.</summary>
    public Guid? CategoryId { get; set; }
    public ProductUnit Unit { get; set; }
    public string? Barcode { get; set; }
    public ProductPurpose Purpose { get; set; }
    public Guid? SupplierId { get; set; }
    public decimal? PurchasePrice { get; set; }
    /// <summary>Роздрібна ціна (мережева, одна на tenant); NULL для salon_use і для старих продажних товарів.</summary>
    public decimal? SalePrice { get; set; }
    public decimal? MinStock { get; set; }
    public bool TrackExpiry { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Note { get; set; }
    public Guid UpdatedByUserId { get; set; }
}

/// <summary>Налаштування товару в закладі: свій мінімальний залишок (null = мережевий) і вимкнення в закладі.</summary>
public class ProductLocationSetting : TenantEntity
{
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }
    public decimal? MinStock { get; set; }
    public bool IsEnabled { get; set; } = true;
    public Guid UpdatedByUserId { get; set; }
}

/// <summary>Агрегований рівень (заклад x товар) - точка серіалізації (FOR UPDATE).</summary>
public class StockLevel : TenantEntity
{
    public Guid LocationId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>Quantity >= 0 (партії й рівень не від'ємні). Облікове сальдо = Quantity - ShortageQuantity (§32.17, етап 2a).</summary>
    public decimal Quantity { get; set; }
    /// <summary>Непокрита нестача автосписання (>= 0), гаситься наступним приходом (§32.17).</summary>
    public decimal ShortageQuantity { get; set; }
}

/// <summary>Заголовок операції складу. Append-only для app-ролі (REVOKE UPDATE, DELETE у grants).</summary>
public class StockOperation : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public StockOperationKind Kind { get; set; }
    /// <summary>Заклад операції / джерело переміщення.</summary>
    public Guid LocationId { get; set; }
    public Guid? ToLocationId { get; set; }
    public Guid? SupplierId { get; set; }
    public string? DocumentNumber { get; set; }
    public DateOnly? ReceivedOn { get; set; }
    public WriteOffReason? Reason { get; set; }
    public Guid? StocktakeId { get; set; }
    /// <summary>Запис, що списав матеріали (лише service_consumption; UNIQUE на (tenant, appointment) = ідемпотентність, §32.18).</summary>
    public Guid? AppointmentId { get; set; }
    /// <summary>Замовлення товарів (лише sale | sale_return; UNIQUE (tenant, order, kind) = одна операція продажу й одна повернення, ADR-026 §2.8).</summary>
    public Guid? ProductOrderId { get; set; }
    public string? Note { get; set; }
    /// <summary>null = система (етап 2); в етапі 1 завжди заповнено.</summary>
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? IdempotencyKeyHash { get; set; }
    public string? IdempotencyRequestHash { get; set; }
}

/// <summary>Партія товару в закладі (FEFO). Змінюється лише під блокуванням рівня своєї пари.</summary>
public class StockBatch : TenantEntity
{
    public Guid LocationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid OperationId { get; set; }
    public Guid? SourceBatchId { get; set; }
    public Guid? SupplierId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public string? LotNumber { get; set; }
    public decimal InitialQuantity { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitCost { get; set; }
}

/// <summary>Журнал рухів. Append-only для app-ролі. Quantity зі знаком (+надходження, -вихід).</summary>
public class StockMovement : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid LocationId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>NULL тільки для рухів нестачі (IsShortage), §32.17.</summary>
    public Guid? BatchId { get; set; }
    /// <summary>Рух нестачі/погашення нестачі без партії (CHECK (batch_id IS NULL) = is_shortage).</summary>
    public bool IsShortage { get; set; }
    public StockMovementType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitCost { get; set; }
    public Guid? CreatedByUserId { get; set; }
    /// <summary>Час сервера, не клієнта.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Інвентаризація: draft -> posted | cancelled. Незмінність закритих - в Application (умова status = 'draft').</summary>
public class Stocktake : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LocationId { get; set; }
    public StocktakeStatus Status { get; set; } = StocktakeStatus.Draft;
    public string? Note { get; set; }
    /// <summary>CAS-версія чернетки.</summary>
    public int Version { get; set; } = 1;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public Guid? PostedByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
}

public class StocktakeLine : TenantEntity
{
    public Guid StocktakeId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>null = «не рахували».</summary>
    public decimal? CountedQuantity { get; set; }
    /// <summary>Пишеться при проведенні.</summary>
    public decimal? ExpectedQuantity { get; set; }
    /// <summary>Знімок дефіциту при проведенні (§32.17).</summary>
    public decimal? ExpectedShortage { get; set; }
    public DateOnly? SurplusExpiresOn { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

// ---- Склад, етап 2a (ADR-025 §11, контракт §32.16-§32.19, TASK-883). RLS ENABLE+FORCE і ручні частини - в міграції beauty_inventory_stage2a ----

/// <summary>Набір норм матеріалів послуги: LocationId == null - мережевий, інакше override закладу (замінює весь набір, §32.16).</summary>
public class ServiceMaterialSet : TenantEntity
{
    public Guid ServiceId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid UpdatedByUserId { get; set; }
}

/// <summary>Рядок норми: товар і кількість на одну послугу (в одиниці товару).</summary>
public class ServiceMaterial : TenantEntity
{
    public Guid SetId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>0 &lt; q &lt;= 100000, 3 знаки.</summary>
    public decimal Quantity { get; set; }
}

/// <summary>Сповіщення персоналу (§32.19): low_stock | expiring | shortage; без ПД клієнтів і без appointment_id.</summary>
public class StaffAlert : TenantEntity
{
    /// <summary>low_stock | expiring | shortage (CHECK).</summary>
    public string Kind { get; set; } = null!;
    public Guid LocationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? OperationId { get; set; }
    /// <summary>Знімок: low_stock - сальдо; expiring - кількість партії; shortage - непокрита частина.</summary>
    public decimal Quantity { get; set; }
    public decimal? Threshold { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    /// <summary>0..365; 0 = прострочено (лише expiring).</summary>
    public short? DaysBucket { get; set; }
    public string DedupeKey { get; set; } = null!;
    /// <summary>open | read | resolved (CHECK).</summary>
    public string Status { get; set; } = "open";
    public DateTimeOffset? ReadAt { get; set; }
    public Guid? ReadByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

// ---- Замовлення товарів клієнтами (ADR-026, контракт §33.15, TASK-901). RLS ENABLE+FORCE і ручні частини - в міграції beauty_product_orders ----

/// <summary>Замовлення товарів гостем (самовивіз). Без DELETE для app-ролі; воркер - лише колонковий TTL.</summary>
public class ProductOrder : TenantEntity
{
    public Guid LocationId { get; set; }
    /// <summary>Послідовний у tenant (max+1 під advisory-блокуванням); публічно нічого не відкриває.</summary>
    public int OrderNumber { get; set; }
    public string PublicTokenHash { get; set; } = null!;
    public string IdempotencyKeyHash { get; set; } = null!;
    public string IdempotencyRequestHash { get; set; } = null!;
    public ProductOrderStatus Status { get; set; } = ProductOrderStatus.PendingPayment;
    public ProductOrderSource Source { get; set; }
    public string GuestName { get; set; } = null!;
    /// <summary>E.164.</summary>
    public string GuestPhone { get; set; } = null!;
    /// <summary>Заповнюється при успішній оплаті.</summary>
    public Guid? ClientId { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "UAH";
    public DateTimeOffset ReservationExpiresAt { get; set; }
    public short PaymentAttempts { get; set; }
    public DateTimeOffset? PaymentStartedAt { get; set; }
    public DateTimeOffset? RefundStartedAt { get; set; }
    /// <summary>Успішний платіж (beauty_payments.id).</summary>
    public Guid? PaymentId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? FulfilledAt { get; set; }
    public Guid? FulfilledByUserId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public ProductOrderCloseReason? CloseReason { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelNote { get; set; }
    public decimal? RefundAmount { get; set; }
}

/// <summary>Рядок замовлення: знімки назви/одиниці/ціни. Append-only для app-ролі (REVOKE UPDATE, DELETE).</summary>
public class ProductOrderLine : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public short LineNo { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public ProductUnit Unit { get; set; }
    /// <summary>Ціле 1..10.</summary>
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
