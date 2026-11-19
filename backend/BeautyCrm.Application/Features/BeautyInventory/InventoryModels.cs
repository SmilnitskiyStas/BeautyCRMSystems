using System.Text.Json.Serialization;

namespace BeautyCrm.Application.Features.BeautyInventory;

// Склад (ADR-025, контракт §32.6). Гроші: number <= 2 знаки, кількості <= 3 знаки; поля з † (ціни) присутні лише за inventory.costs >= view:
// сервіс обнуляє їх, а JsonIgnore(WhenWritingNull) прибирає з JSON (ніколи не 0).

public sealed record InventoryNamedRef(Guid Id, string Name);

public sealed record InventoryLocationDto(Guid Id, string Name, bool IsActive, string Timezone, bool CanManage);

public sealed record InventoryScopeDto(IReadOnlyList<InventoryLocationDto> Locations, string Costs, bool CanEditCatalog, int ExpiringSoonDays);

public sealed record ProductRef(Guid Id, string Name, string Unit, string Purpose, string? Category, string? Barcode, bool IsActive);

public record ProductDto(
    Guid Id, string Name, string? Category, string Unit, string? Barcode, string Purpose, InventoryNamedRef? Supplier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? PurchasePrice,
    decimal? MinStock, bool TrackExpiry, bool IsActive, string? Note, bool HasMovements,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record LocationSettingDto(Guid LocationId, decimal? MinStock, bool IsEnabled, DateTimeOffset UpdatedAt);

public sealed record ProductDetailDto(
    Guid Id, string Name, string? Category, string Unit, string? Barcode, string Purpose, InventoryNamedRef? Supplier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? PurchasePrice,
    decimal? MinStock, bool TrackExpiry, bool IsActive, string? Note, bool HasMovements,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<LocationSettingDto> LocationSettings)
    : ProductDto(Id, Name, Category, Unit, Barcode, Purpose, Supplier, PurchasePrice, MinStock, TrackExpiry, IsActive, Note, HasMovements, CreatedAt, UpdatedAt);

public sealed record SupplierDto(Guid Id, string Name, string? ContactName, string? Phone, string? Email, string? Note, bool IsActive, int ProductCount);

public sealed record StockRowDto(
    ProductRef Product, Guid LocationId, decimal Quantity, decimal? MinStock, bool IsLow, bool IsEnabled,
    DateOnly? NearestExpiresOn, decimal ExpiredQuantity, decimal ExpiringQuantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ValueIncomplete = null);

public sealed record StockSummaryLocationQty(Guid LocationId, decimal Quantity, bool IsLow);

public sealed record StockSummaryRowDto(
    ProductRef Product, decimal TotalQuantity, bool IsLowAnywhere, IReadOnlyList<StockSummaryLocationQty> Locations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ValueIncomplete = null);

public sealed record BatchDto(
    Guid Id, ProductRef Product, Guid LocationId, DateTimeOffset ReceivedAt, DateOnly? ExpiresOn, string? LotNumber,
    decimal InitialQuantity, decimal Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? UnitCost,
    InventoryNamedRef? Supplier, Guid OperationId, Guid? SourceBatchId, string Status, int? DaysToExpiry);

public sealed record MovementDto(
    Guid Id, Guid OperationId, string OperationKind, string Type, DateTimeOffset OccurredAt, Guid LocationId, ProductRef Product,
    Guid? BatchId, decimal Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? UnitCost,
    string? Reason, string? Note, Guid? CounterpartLocationId, Guid? StocktakeId, InventoryNamedRef? CreatedBy);

public record OperationSummaryDto(
    Guid Id, string Kind, Guid LocationId, Guid? ToLocationId, string? Reason, string? DocumentNumber, DateTimeOffset CreatedAt,
    InventoryNamedRef? CreatedBy, int ProductCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? TotalCost);

public sealed record OperationDto(
    Guid Id, string Kind, Guid LocationId, Guid? ToLocationId, string? Reason, string? DocumentNumber, DateTimeOffset CreatedAt,
    InventoryNamedRef? CreatedBy, int ProductCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? TotalCost,
    InventoryNamedRef? Supplier, DateOnly? ReceivedOn, string? Note, Guid? StocktakeId, IReadOnlyList<MovementDto> Movements)
    : OperationSummaryDto(Id, Kind, LocationId, ToLocationId, Reason, DocumentNumber, CreatedAt, CreatedBy, ProductCount, TotalCost);

public sealed record StocktakeLineDto(ProductRef Product, decimal? ExpectedQuantity, decimal? CountedQuantity, decimal? Difference, DateOnly? SurplusExpiresOn);

public record StocktakeSummaryDto(
    Guid Id, Guid LocationId, string Status, DateTimeOffset CreatedAt, InventoryNamedRef CreatedBy, DateTimeOffset? PostedAt, int LineCount, int CountedCount);

public sealed record StocktakeDto(
    Guid Id, Guid LocationId, string Status, DateTimeOffset CreatedAt, InventoryNamedRef CreatedBy, DateTimeOffset? PostedAt, int LineCount, int CountedCount,
    string? Note, int Version, InventoryNamedRef? PostedBy, DateTimeOffset? CancelledAt, IReadOnlyList<StocktakeLineDto> Lines)
    : StocktakeSummaryDto(Id, LocationId, Status, CreatedAt, CreatedBy, PostedAt, LineCount, CountedCount);

public sealed record StocktakePostDto(StocktakeDto Stocktake, OperationDto? Operation);

public sealed record Page<T>(IReadOnlyList<T> Items, bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor = null);

public sealed record StockLocationBrief(Guid Id, string Name, string Timezone, bool IsActive);
public sealed record StockPageDto(StockLocationBrief Location, IReadOnlyList<StockRowDto> Items, bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor = null);
public sealed record StockSummaryLocation(Guid Id, string Name, bool IsActive);
public sealed record StockSummaryPageDto(IReadOnlyList<StockSummaryLocation> Locations, IReadOnlyList<StockSummaryRowDto> Items, bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor = null);

/// <summary>Результат операції: Replayed = повтор того ж Idempotency-Key (200 + Idempotent-Replayed), інакше 201.</summary>
public sealed record OperationResult(OperationDto Operation, bool Replayed);

// ---------- запити (§32.6) ----------

public sealed record ProductRequest(string? Name, string? Category, string? Unit, string? Barcode, string? Purpose, Guid? SupplierId,
    decimal? PurchasePrice, decimal? MinStock, bool? TrackExpiry, bool? IsActive, string? Note);

public sealed record SupplierRequest(string? Name, string? ContactName, string? Phone, string? Email, string? Note, bool? IsActive);

public sealed record LocationSettingRequest(decimal? MinStock, bool? IsEnabled);

public sealed record ReceiptLineRequest(Guid? ProductId, decimal? Quantity, decimal? UnitCost, string? ExpiresOn, string? LotNumber);
public sealed record ReceiptRequest(Guid? LocationId, Guid? SupplierId, string? DocumentNumber, string? ReceivedOn, string? Note, IReadOnlyList<ReceiptLineRequest>? Lines);

public sealed record StockLineRequest(Guid? ProductId, decimal? Quantity, Guid? BatchId);
public sealed record WriteOffRequest(Guid? LocationId, string? Reason, string? Note, IReadOnlyList<StockLineRequest>? Lines);
public sealed record TransferRequest(Guid? FromLocationId, Guid? ToLocationId, string? Note, IReadOnlyList<StockLineRequest>? Lines);

public sealed record StocktakeCreateRequest(Guid? LocationId, string? Note, IReadOnlyList<Guid>? ProductIds);
public sealed record StocktakeLineInput(Guid? ProductId, decimal? CountedQuantity, string? SurplusExpiresOn);
public sealed record StocktakeLinesRequest(int? ExpectedVersion, IReadOnlyList<StocktakeLineInput>? Lines);
public sealed record StocktakeVersionRequest(int? ExpectedVersion);

// ---------- запити списків ----------

public sealed record ProductListQuery(string? Search, string? Category, string? Purpose, Guid? SupplierId, string? Active, string? Cursor, int? Take);
public sealed record SupplierListQuery(string? Search, string? Active, string? Cursor, int? Take);
public sealed record StockListQuery(Guid? LocationId, string? Search, string? Category, string? Purpose, string? State, int? ExpiringWithinDays, string? Cursor, int? Take);
public sealed record SummaryListQuery(string? Search, string? Category, string? Purpose, bool? LowOnly, string? Cursor, int? Take);
public sealed record BatchListQuery(Guid? LocationId, Guid? ProductId, string? Status, int? ExpiringWithinDays, string? Cursor, int? Take);
public sealed record MovementListQuery(Guid? LocationId, Guid? ProductId, string? Type, Guid? OperationId, string? From, string? To, string? Cursor, int? Take);
public sealed record OperationListQuery(string? Kind, Guid? LocationId, string? From, string? To, string? Cursor, int? Take);
public sealed record StocktakeListQuery(Guid? LocationId, string? Status, string? Cursor, int? Take);
