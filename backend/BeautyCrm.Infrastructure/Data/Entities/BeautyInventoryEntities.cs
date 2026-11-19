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

/// <summary>Товар мережі (каталог). Рівні й партії - по закладах.</summary>
public class Product : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Category { get; set; }
    public ProductUnit Unit { get; set; }
    public string? Barcode { get; set; }
    public ProductPurpose Purpose { get; set; }
    public Guid? SupplierId { get; set; }
    public decimal? PurchasePrice { get; set; }
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
    public decimal Quantity { get; set; }
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
    public Guid? BatchId { get; set; }
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
    public DateOnly? SurplusExpiresOn { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
