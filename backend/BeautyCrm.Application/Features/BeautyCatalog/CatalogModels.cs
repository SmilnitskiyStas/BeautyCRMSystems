using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyCatalog;

// ---- послуги й ціни ----
public sealed record ServiceDto(Guid Id, string Name, string? Description, string? Category, int DurationMinutes, bool IsActive, decimal? NetworkPrice);
public sealed record UpsertServiceRequest(string Name, string? Description, string? Category, int DurationMinutes, bool IsActive = true);
/// <summary>LocationId == null: мережева ціна; інакше override закладу.</summary>
public sealed record ServicePriceDto(Guid ServiceId, Guid? LocationId, decimal Price);
public sealed record SetPriceRequest(Guid? LocationId, decimal Price);

// ---- акції ----
public sealed record PromotionDto(
    Guid Id, string Name, string? Description, string DiscountType, decimal DiscountValue,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool IsActive, IReadOnlyList<Guid> LocationIds, IReadOnlyList<Guid> ServiceIds);
public sealed record UpsertPromotionRequest(
    string Name, string? Description, string DiscountType, decimal DiscountValue,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool IsActive, IReadOnlyList<Guid>? LocationIds, IReadOnlyList<Guid>? ServiceIds);
public sealed record PricePreviewItem(
    Guid ServiceId, string ServiceName, decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string? PromotionName);
public sealed record PricedService(Guid ServiceId, string Name, decimal Price);

public interface ICatalogStore
{
    Task<IReadOnlyList<ServiceDto>> ListServicesAsync(bool includeInactive, CancellationToken ct);
    Task<ServiceDto?> GetServiceAsync(Guid id, CancellationToken ct);
    Task<ServiceDto> CreateServiceAsync(UpsertServiceRequest req, CancellationToken ct);
    Task<ServiceDto?> UpdateServiceAsync(Guid id, UpsertServiceRequest req, CancellationToken ct);
    Task<IReadOnlyList<ServicePriceDto>> ListPricesAsync(Guid serviceId, CancellationToken ct);
    Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct);
    Task<ServicePriceDto> UpsertPriceAsync(Guid serviceId, Guid? locationId, decimal price, CancellationToken ct);
    /// <summary>Активні послуги з ціною для закладу (override або мережева); без ціни — пропускаються.</summary>
    Task<IReadOnlyList<PricedService>> ListPricedServicesAsync(Guid locationId, CancellationToken ct);

    Task<IReadOnlyList<PromotionDto>> ListPromotionsAsync(CancellationToken ct);
    Task<PromotionDto?> GetPromotionAsync(Guid id, CancellationToken ct);
    Task<bool> ReferencesExistAsync(IReadOnlyCollection<Guid> locationIds, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct);
    Task<PromotionDto> CreatePromotionAsync(UpsertPromotionRequest req, CancellationToken ct);
    Task<PromotionDto?> UpdatePromotionAsync(Guid id, UpsertPromotionRequest req, CancellationToken ct);
}
