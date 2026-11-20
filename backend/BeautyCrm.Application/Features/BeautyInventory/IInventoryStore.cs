namespace BeautyCrm.Application.Features.BeautyInventory;

public sealed record LocationRow(Guid Id, string Name, bool IsActive, string Timezone);

/// <summary>Нормалізований вміст товару (після валідації й злиття PUT). Unit/Purpose - коди §32.3.</summary>
public sealed record ProductData(string Name, string? Category, string Unit, string? Barcode, string Purpose, Guid? SupplierId,
    decimal? PurchasePrice, decimal? SalePrice, decimal? MinStock, bool TrackExpiry, bool IsActive, string? Note);

public sealed record SupplierData(string Name, string? ContactName, string? Phone, string? Email, string? Note, bool IsActive);

public enum CatalogWriteOutcome { Ok, NotFound, BarcodeTaken, NameTaken, UnitLocked, PurposeLocked }

public sealed record ProductWriteResult(CatalogWriteOutcome Outcome, ProductDetailDto? Product = null);
public sealed record SupplierWriteResult(CatalogWriteOutcome Outcome, SupplierDto? Supplier = null);

/// <summary>Фільтр залишків закладу (виконується в БД). State: all|low|in_stock|out_of_stock.</summary>
public sealed record StockFilter(Guid LocationId, DateOnly Today, int ExpiringWithinDays, string State, string? Search, string? Category, string? Purpose, bool OnlyExpiring);

/// <summary>Фільтр зведення по закладах (виконується в БД).</summary>
public sealed record SummaryFilter(IReadOnlyList<Guid> LocationIds, string? Search, string? Category, string? Purpose, bool LowOnly);

/// <summary>Сирий рядок залишків закладу: ефективний мінімум і ознаки рахує сервіс.</summary>
public sealed record StockRaw(ProductRef Product, decimal Quantity, decimal? ProductMinStock, decimal? SettingMinStock, bool IsEnabled,
    DateOnly? NearestExpiresOn, decimal ExpiredQuantity, decimal ExpiringQuantity, decimal Value, bool ValueIncomplete, decimal Shortage = 0);

/// <summary>Сирий рядок зведення: пара (товар, заклад).</summary>
public sealed record SummaryRaw(ProductRef Product, Guid LocationId, decimal Quantity, decimal? ProductMinStock, decimal? SettingMinStock,
    bool IsEnabled, decimal Value, bool ValueIncomplete, decimal Shortage = 0);

public sealed record BatchRef(Guid Id, Guid LocationId, Guid ProductId, DateOnly? ExpiresOn);

/// <summary>Партія для планувальника FEFO (стан під блокуванням рівня).</summary>
public sealed record BatchRow(Guid Id, Guid ProductId, DateOnly? ExpiresOn, DateTimeOffset ReceivedAt, decimal Quantity,
    decimal? UnitCost, string? LotNumber, Guid? SupplierId);

public sealed record BatchFilter(IReadOnlyList<(IReadOnlyList<Guid> LocationIds, DateOnly Today)> Locations, Guid? ProductId, string Status, int ExpiringWithinDays);

public sealed record MovementFilter(IReadOnlyList<Guid>? LocationIds, Guid? ProductId, string? Type, Guid? OperationId, DateTimeOffset From, DateTimeOffset To);

public sealed record OperationFilter(IReadOnlyList<Guid>? LocationIds, string? Kind, Guid? LocationId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record StocktakeFilter(IReadOnlyList<Guid>? LocationIds, Guid? LocationId, string? Status);

public sealed record OperationHeader(Guid Id, string Kind, Guid LocationId, Guid? ToLocationId, Guid? SupplierId, string? DocumentNumber,
    DateOnly? ReceivedOn, string? Reason, string? Note, Guid UserId, DateTimeOffset Now, string KeyHash, string RequestHash);

public sealed record ReceiptLine(Guid ProductId, decimal Quantity, decimal? UnitCost, DateOnly? ExpiresOn, string? LotNumber);

public sealed record StockLine(int Line, Guid ProductId, decimal Quantity, Guid? BatchId);

/// <summary>Rejected: перевірка під блокуванням не пройшла (Code/Line у <see cref="StockWriteResult"/>).</summary>
public enum StockWriteStatus { Ok, DuplicateKey, Insufficient, Rejected }

public sealed record StockShortage(int Line, Guid ProductId, Guid? BatchId, decimal Requested, decimal Available, bool BatchExpired = false);

public sealed record StockWriteResult(StockWriteStatus Status, OperationDto? Operation = null, IReadOnlyList<StockShortage>? Shortages = null,
    string? Code = null, int? Line = null);

public sealed record KeyedOperation(OperationDto Operation, string RequestHash);

public enum StocktakeWriteStatus
{
    Ok, NotFound, Closed, VersionConflict, InProgress, ProductNotFound, TooManyLines, Empty, ExpiryRequired, Insufficient, LocationNotFound,
    ExpiryInPast, QuantityOutOfRange, InvalidQuantity,
}

public sealed record StocktakeWriteResult(StocktakeWriteStatus Status, StocktakeDto? Stocktake = null, OperationDto? Operation = null,
    int? CurrentVersion = null, Guid? OtherId = null, int? Line = null, Guid? ProductId = null);

public sealed record StocktakeLineChange(Guid ProductId, decimal? CountedQuantity, DateOnly? SurplusExpiresOn);

/// <summary>
/// Порт даних складу (§32). Усе під RLS поточного tenant. Операції (надходження, списання, переміщення, проведення інвентаризації) -
/// ОДНА транзакція: вставка заголовка, блокування рівнів у порядку (location, product), підбір партій (FEFO через <see cref="FefoPlanner"/>)
/// і перевірка достатності ПІД блокуванням, партії, рухи, рівні. Нестача відкочує все. Журнал рухів і операцій - append-only.
/// </summary>
public interface IInventoryStore
{
    // ---- довідники ----
    Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct);
    /// <summary>Заклади активного профілю майстра (активні прив'язки); неактивний профіль або немає прив'язок - порожньо.</summary>
    Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid specialistId, CancellationToken ct);

    // ---- каталог ----
    /// <summary>Усі товари БЕЗ ознаки руху (HasMovements = false): її додає сервіс для сторінки через <see cref="ProductIdsWithMovementsAsync"/>.</summary>
    Task<IReadOnlyList<ProductDto>> ListProductsAsync(CancellationToken ct);
    /// <summary>Підмножина id, що мають хоч один рух (EXISTS по індексу, лише для переданих id).</summary>
    Task<IReadOnlySet<Guid>> ProductIdsWithMovementsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken ct);
    Task<ProductDto?> GetProductByBarcodeAsync(string code, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, ProductDto>> GetProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken ct);
    Task<ProductWriteResult> CreateProductAsync(ProductData data, Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<ProductWriteResult> UpdateProductAsync(Guid id, ProductData data, Guid userId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Налаштування товару в закладі (мінімум/вимкнення); товар має існувати (інакше null).</summary>
    Task<LocationSettingDto?> UpsertSettingAsync(Guid productId, Guid locationId, decimal? minStock, bool isEnabled, Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<bool> DeleteSettingAsync(Guid productId, Guid locationId, CancellationToken ct);
    /// <summary>Ефективні налаштування (productId, locationId) -> (isEnabled).</summary>
    Task<IReadOnlyDictionary<Guid, bool>> GetEnabledMapAsync(Guid locationId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(CancellationToken ct);
    Task<SupplierDto?> GetSupplierAsync(Guid id, CancellationToken ct);
    Task<SupplierWriteResult> CreateSupplierAsync(SupplierData data, DateTimeOffset now, CancellationToken ct);
    Task<SupplierWriteResult> UpdateSupplierAsync(Guid id, SupplierData data, DateTimeOffset now, CancellationToken ct);

    // ---- залишки ----
    /// <summary>Фільтрація й агрегація в БД; сторінка за (lower(name), id): after = [nameKey, id] (вже перевірені), take рядків упорядковано.</summary>
    Task<IReadOnlyList<StockRaw>> ListStockAsync(StockFilter filter, string[]? after, int take, CancellationToken ct);
    /// <summary>take товарів (за тим самим порядком) з усіма їхніми рядками по закладах; фільтри в БД.</summary>
    Task<IReadOnlyList<SummaryRaw>> ListSummaryAsync(SummaryFilter filter, string[]? after, int take, CancellationToken ct);
    Task<IReadOnlyList<BatchDto>> ListBatchesAsync(BatchFilter filter, string[]? after, int take, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, BatchRef>> GetBatchRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyList<MovementDto>> ListMovementsAsync(MovementFilter filter, string[]? after, int take, CancellationToken ct);
    Task<IReadOnlyList<OperationSummaryDto>> ListOperationsAsync(OperationFilter filter, string[]? after, int take, CancellationToken ct);
    Task<OperationDto?> GetOperationAsync(Guid id, CancellationToken ct);

    // ---- операції ----
    Task<KeyedOperation?> FindOperationByKeyAsync(string keyHash, CancellationToken ct);
    Task<StockWriteResult> PostReceiptAsync(OperationHeader header, IReadOnlyList<ReceiptLine> lines, CancellationToken ct);
    /// <param name="today">Локальна дата закладу-джерела (межа «прострочено»).</param>
    Task<StockWriteResult> PostWriteOffAsync(OperationHeader header, IReadOnlyList<StockLine> lines, DateOnly today, CancellationToken ct);
    Task<StockWriteResult> PostTransferAsync(OperationHeader header, IReadOnlyList<StockLine> lines, DateOnly today, CancellationToken ct);

    // ---- інвентаризації ----
    Task<IReadOnlyList<StocktakeSummaryDto>> ListStocktakesAsync(StocktakeFilter filter, string[]? after, int take, CancellationToken ct);
    Task<StocktakeDto?> GetStocktakeAsync(Guid id, CancellationToken ct);
    Task<StocktakeWriteResult> CreateStocktakeAsync(Guid locationId, string? note, IReadOnlyList<Guid>? productIds, Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<StocktakeWriteResult> PutStocktakeLinesAsync(Guid id, int expectedVersion, IReadOnlyList<StocktakeLineChange> lines, Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<StocktakeWriteResult> PostStocktakeAsync(Guid id, int expectedVersion, Guid userId, DateTimeOffset now, DateOnly today, CancellationToken ct);
    Task<StocktakeWriteResult> CancelStocktakeAsync(Guid id, int expectedVersion, Guid userId, DateTimeOffset now, CancellationToken ct);
}
