using System.Globalization;
using System.Text.Json.Serialization;

namespace BeautyCrm.Application.Features.BeautyInventory;

// Сповіщення персоналу (ADR-025 §11, контракт §32.19, TASK-886). Жодних ПД клієнтів, appointment_id, цін.

public sealed record StaffAlertBatchDto(Guid Id, DateOnly? ExpiresOn, string? LotNumber);

public sealed record StaffAlertDto(
    Guid Id, string Kind, string Status, InventoryNamedRef Location, ProductRef Product, StaffAlertBatchDto? Batch, Guid? OperationId,
    decimal Quantity, decimal? Threshold, DateOnly? ExpiresOn, int? DaysBucket, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt,
    InventoryNamedRef? ReadBy, DateTimeOffset? ResolvedAt);

public sealed record AlertCountDto(int Count);

public sealed record AlertsUpdatedDto(int Updated);

public sealed record StaffAlertListQuery(string? Status, string? Kind, Guid? LocationId, string? Cursor, int? Take);

/// <summary>Фільтр списку: LocationIds == null - без обмеження scope; Statuses - дозволені статуси.</summary>
public sealed record StaffAlertFilter(IReadOnlyList<Guid>? LocationIds, IReadOnlyList<string> Statuses, string? Kind, Guid? LocationId);

/// <summary>Нове сповіщення (знімок значень §32.19). Kind: low_stock | expiring | shortage; DedupeKey унікальний у tenant.</summary>
public sealed record StaffAlertDraft(string Kind, Guid LocationId, Guid ProductId, Guid? BatchId, Guid? OperationId, decimal Quantity,
    decimal? Threshold, DateOnly? ExpiresOn, short? DaysBucket, string DedupeKey);

public static class StaffAlertKinds
{
    public const string LowStock = "low_stock", Expiring = "expiring", Shortage = "shortage";
    public static readonly string[] All = [LowStock, Expiring, Shortage];
}

public static class StaffAlertKeys
{
    public static string LowStock(Guid locationId, Guid productId, DateOnly localDate) =>
        $"low_stock:{locationId}:{productId}:{localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    public static string Expiring(Guid batchId, int bucket) => $"expiring:{batchId}:{bucket.ToString(CultureInfo.InvariantCulture)}";

    public static string Shortage(Guid operationId, Guid productId) => $"shortage:{operationId}:{productId}";
}

/// <summary>
/// Спільне створення сповіщень (§32.19). Ідемпотентне за (tenant, dedupe_key): повтор - no-op (false). Реалізація EF виконується в
/// ПОТОЧНІЙ транзакції контексту (автосписання викликає її всередині своєї транзакції: нестача й сповіщення атомарні).
/// </summary>
public interface IStaffAlertWriter
{
    /// <summary>true - вставлено; false - уже існує (ON CONFLICT DO NOTHING).</summary>
    Task<bool> CreateAsync(StaffAlertDraft draft, DateTimeOffset now, CancellationToken ct);

    /// <summary>shortage: dedupe_key = shortage:{operationId}:{productId}; quantity = u (непокрита частина); без appointment_id і ПД.</summary>
    Task<bool> CreateShortageAlertAsync(Guid locationId, Guid productId, Guid operationId, decimal shortageQuantity, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Порт читання й позначення прочитаним (§32.19). Усе під RLS tenant.</summary>
public interface IStaffAlertStore
{
    Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct);
    Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid specialistId, CancellationToken ct);

    /// <summary>Сортування created_at DESC, id DESC; after = [ticks, id] (вже перевірені), take рядків.</summary>
    Task<IReadOnlyList<StaffAlertDto>> ListStaffAlertsAsync(StaffAlertFilter filter, string[]? after, int take, CancellationToken ct);
    Task<StaffAlertDto?> GetStaffAlertAsync(Guid id, CancellationToken ct);
    /// <summary>Кількість open у закладах (null = усі), не більше cap.</summary>
    Task<int> CountOpenAlertsAsync(IReadOnlyList<Guid>? locationIds, int cap, CancellationToken ct);
    /// <summary>open -> read (read/resolved не змінюються). Повертає актуальний стан або null, якщо немає.</summary>
    Task<StaffAlertDto?> MarkAlertReadAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Усі open у закладах scope (null = усі) або в одному закладі; повертає кількість змінених.</summary>
    Task<int> MarkAllAlertsReadAsync(IReadOnlyList<Guid>? locationIds, Guid? locationId, Guid userId, DateTimeOffset now, CancellationToken ct);
}
