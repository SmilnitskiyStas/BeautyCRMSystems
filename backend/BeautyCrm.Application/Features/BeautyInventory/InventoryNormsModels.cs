namespace BeautyCrm.Application.Features.BeautyInventory;

// Норми матеріалів послуг (ADR-025 §11, контракт §32.16, TASK-884). Кількості <= 3 знаки; без цін.

public sealed record MaterialLineDto(ProductRef Product, decimal Quantity);

public record MaterialSetDto(Guid ServiceId, Guid? LocationId, IReadOnlyList<MaterialLineDto> Lines, DateTimeOffset UpdatedAt, InventoryNamedRef? UpdatedBy);

public sealed record LocationMaterialSetDto(Guid ServiceId, Guid? LocationId, IReadOnlyList<MaterialLineDto> Lines, DateTimeOffset UpdatedAt,
    InventoryNamedRef? UpdatedBy, string LocationName) : MaterialSetDto(ServiceId, LocationId, Lines, UpdatedAt, UpdatedBy);

public sealed record ServiceMaterialsDto(Guid ServiceId, string ServiceName, bool ServiceIsActive, MaterialSetDto? Network,
    IReadOnlyList<LocationMaterialSetDto> Locations);

public sealed record ServiceMaterialsList(IReadOnlyList<ServiceMaterialsDto> Items);

public sealed record MaterialLineRequest(Guid? ProductId, decimal? Quantity);

public sealed record PutMaterialsRequest(Guid? LocationId, IReadOnlyList<MaterialLineRequest>? Lines);

// ---------- порт даних ----------

public sealed record ServiceRow(Guid Id, string Name, bool IsActive);

public sealed record MaterialLineRow(Guid ProductId, decimal Quantity);

/// <summary>Набір норм (LocationId == null - мережевий). UpdatedByName - ім'я користувача (null, якщо невідоме).</summary>
public sealed record MaterialSetRow(Guid ServiceId, Guid? LocationId, DateTimeOffset UpdatedAt, Guid UpdatedByUserId, string? UpdatedByName,
    IReadOnlyList<MaterialLineRow> Lines);

public enum MaterialPutStatus { Ok, ServiceNotFound, LocationNotFound, Rejected }

/// <summary>Rejected: перевірка під блокуванням товарів не пройшла (Code/Line - індекс рядка запиту з 0).</summary>
public sealed record MaterialPutResult(MaterialPutStatus Status, MaterialSetRow? Set = null, string? Code = null, int? Line = null);

/// <summary>
/// Порт норм матеріалів (§32.16). Усе під RLS tenant. <see cref="PutMaterialSetAsync"/> - ОДНА транзакція: товари FOR SHARE (конфліктує з
/// UpdateProductAsync FOR UPDATE: зміна purpose/одиниці не проскочить), повторна перевірка активності/призначення/дозволу в закладі,
/// upsert заголовка (блокування рядка), DELETE+INSERT рядків. Останній запис перемагає.
/// </summary>
public interface IInventoryNormsStore
{
    Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct);
    Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid specialistId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, ProductDto>> GetProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    /// <summary>Лише наявні налаштування (productId -> isEnabled); немає рядка = увімкнено.</summary>
    Task<IReadOnlyDictionary<Guid, bool>> GetEnabledMapAsync(Guid locationId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, ServiceRow>> GetServicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    /// <summary>Усі набори послуги (serviceId != null) або всі набори tenant.</summary>
    Task<IReadOnlyList<MaterialSetRow>> ListMaterialSetsAsync(Guid? serviceId, CancellationToken ct);
    Task<MaterialPutResult> PutMaterialSetAsync(Guid serviceId, Guid? locationId, IReadOnlyList<MaterialLineRow> lines, Guid userId, DateTimeOffset now, CancellationToken ct);
    /// <summary>false - набору немає.</summary>
    Task<bool> DeleteMaterialSetAsync(Guid serviceId, Guid? locationId, CancellationToken ct);
}
