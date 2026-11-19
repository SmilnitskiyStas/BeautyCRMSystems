using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyCatalog;

public sealed class CatalogService(ICatalogStore store)
{
    public Task<IReadOnlyList<ServiceDto>> ListServicesAsync(bool includeInactive, CancellationToken ct) =>
        store.ListServicesAsync(includeInactive, ct);

    public async Task<Result<ServiceDto>> CreateServiceAsync(UpsertServiceRequest req, CancellationToken ct) =>
        ValidateService(req) ?? (Result<ServiceDto>)await store.CreateServiceAsync(req, ct);

    public async Task<Result<ServiceDto>> UpdateServiceAsync(Guid id, UpsertServiceRequest req, CancellationToken ct) =>
        ValidateService(req) ?? (await store.UpdateServiceAsync(id, req, ct) is { } s
            ? s : Error.NotFound("service_not_found", "Service not found."));

    public async Task<Result<IReadOnlyList<ServicePriceDto>>> ListPricesAsync(Guid serviceId, CancellationToken ct) =>
        await store.GetServiceAsync(serviceId, ct) is null
            ? Error.NotFound("service_not_found", "Service not found.")
            : Result(await store.ListPricesAsync(serviceId, ct));

    public async Task<Result<ServicePriceDto>> SetPriceAsync(Guid serviceId, SetPriceRequest req, CancellationToken ct)
    {
        if (req.Price < 0 || req.Price > 1_000_000m) return Error.Validation("invalid_price", "Price must be between 0 and 1,000,000.");
        if (await store.GetServiceAsync(serviceId, ct) is null) return Error.NotFound("service_not_found", "Service not found.");
        if (req.LocationId is { } loc && !await store.LocationExistsAsync(loc, ct))
            return Error.NotFound("location_not_found", "Location not found.");
        return await store.UpsertPriceAsync(serviceId, req.LocationId, Math.Round(req.Price, 2), ct);
    }

    private static Result<IReadOnlyList<ServicePriceDto>> Result(IReadOnlyList<ServicePriceDto> v) => new(v, null);

    private static Result<ServiceDto>? ValidateService(UpsertServiceRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) return Error.Validation("invalid_name", "Name is required (max 200).");
        if (r.DurationMinutes is < 5 or > 720) return Error.Validation("invalid_duration", "Duration must be 5..720 minutes.");
        return null;
    }
}

public sealed class PromotionService(ICatalogStore store, TimeProvider clock)
{
    public Task<IReadOnlyList<PromotionDto>> ListAsync(CancellationToken ct) => store.ListPromotionsAsync(ct);

    public async Task<Result<PromotionDto>> CreateAsync(UpsertPromotionRequest req, CancellationToken ct)
    {
        if (await ValidateAsync(req, ct) is { } err) return err;
        return await store.CreatePromotionAsync(Normalize(req), ct);
    }

    public async Task<Result<PromotionDto>> UpdateAsync(Guid id, UpsertPromotionRequest req, CancellationToken ct)
    {
        if (await ValidateAsync(req, ct) is { } err) return err;
        // §26.6: акція активної кампанії (scheduled|sending) - умови знижки не змінюються; isActive=false дозволено.
        // TASK-790 (L6): перевірка й зміна під замком акції, який бере й confirm кампанії.
        return await store.UnderPromotionLockAsync<Result<PromotionDto>>(id, async () =>
        {
            if (await store.GetPromotionAsync(id, ct) is { } current && await store.HasActiveCampaignAsync(id, ct) && ChangesTerms(current, req))
                return Error.Conflict("promotion_in_campaign", "The promotion is used by an active campaign; only isActive=false is allowed.");
            return await store.UpdatePromotionAsync(id, Normalize(req), ct) is { } p
                ? p : Error.NotFound("promotion_not_found", "Promotion not found.");
        }, ct);
    }

    /// <summary>
    /// Перерахунок цін закладу з акціями. Без promotionId — усі активні акції на момент at;
    /// з promotionId — лише ця акція (можна переглянути ще неактивну/майбутню).
    /// </summary>
    public async Task<Result<IReadOnlyList<PricePreviewItem>>> PreviewAsync(
        Guid locationId, Guid? promotionId, DateTimeOffset? at, CancellationToken ct)
    {
        if (!await store.LocationExistsAsync(locationId, ct)) return Error.NotFound("location_not_found", "Location not found.");
        var when = at ?? clock.GetUtcNow();

        IReadOnlyList<PromotionDto> promos;
        if (promotionId is { } pid)
        {
            var one = await store.GetPromotionAsync(pid, ct);
            if (one is null) return Error.NotFound("promotion_not_found", "Promotion not found.");
            promos = [one with { IsActive = true, StartsAt = null, EndsAt = null }];
        }
        else promos = (await store.ListPromotionsAsync(ct)).Where(p => p.Access == "public").ToList(); // code_only діє лише через купон

        var rules = promos.Select(p => new PromotionRule(p.Id, p.Name, p.DiscountType, p.DiscountValue, p.StartsAt, p.EndsAt,
            p.IsActive, p.LocationIds, p.ServiceIds)).ToList();
        var items = (await store.ListPricedServicesAsync(locationId, ct))
            .Select(s =>
            {
                var q = PromotionPricing.Quote(s.Price, locationId, s.ServiceId, when, rules);
                return new PricePreviewItem(s.ServiceId, s.Name, q.Original, q.Final, q.PromotionId, q.PromotionName);
            }).ToList();
        return Result<IReadOnlyList<PricePreviewItem>>.Ok(items);
    }

    private static bool ChangesTerms(PromotionDto cur, UpsertPromotionRequest r) =>
        cur.DiscountType != r.DiscountType || cur.DiscountValue != r.DiscountValue || cur.StartsAt != r.StartsAt || cur.EndsAt != r.EndsAt
        || (r.Access is not null && r.Access != cur.Access)
        || !cur.LocationIds.ToHashSet().SetEquals(r.LocationIds ?? []) || !cur.ServiceIds.ToHashSet().SetEquals(r.ServiceIds ?? []);

    private static UpsertPromotionRequest Normalize(UpsertPromotionRequest r) =>
        r with { LocationIds = r.LocationIds?.Distinct().ToList() ?? [], ServiceIds = r.ServiceIds?.Distinct().ToList() ?? [] };

    private async Task<Error?> ValidateAsync(UpsertPromotionRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) return Error.Validation("invalid_name", "Name is required (max 200).");
        if (r.DiscountType == "percent" ? r.DiscountValue is <= 0 or > 100 : r.DiscountType == "fixed" ? r.DiscountValue <= 0 : true)
            return Error.Validation("invalid_discount", "discountType must be percent (0..100] or fixed (>0).");
        if (r.Access is not null and not ("public" or "code_only")) return Error.Validation("invalid_access", "access must be public or code_only.");
        if (r.StartsAt is not null && r.EndsAt is not null && r.EndsAt <= r.StartsAt)
            return Error.Validation("invalid_period", "endsAt must be after startsAt.");
        if (!await store.ReferencesExistAsync(r.LocationIds ?? [], r.ServiceIds ?? [], ct))
            return Error.Validation("unknown_reference", "Unknown location or service in the restriction lists.");
        return null;
    }
}
