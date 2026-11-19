using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyInventory;

/// <summary>
/// Норми матеріалів послуг (§32.16): мережевий набір + override закладу (замінює весь набір). Читання - inventory.view &gt;= own (override
/// закладів лише зі scope), зміна - owner/admin з inventory.manage = all. Валідація на межі, бізнес-правила тут, атомарність - у порту.
/// </summary>
public sealed class InventoryNormsService(IInventoryNormsStore store, TimeProvider clock, ILogger<InventoryNormsService>? logger = null)
{
    public const int MaxLines = 50;
    public const decimal MaxQuantity = 100_000m;

    private static readonly Error ServiceNotFound = Error.NotFound("service_not_found", "Service not found.");
    private static readonly Error LocationNotFound = Error.NotFound("location_not_found", "Location not found.");
    private static readonly Error ProductNotFound = Error.NotFound("product_not_found", "Product not found.");
    private static readonly Error MaterialsNotFound = Error.NotFound("materials_not_found", "Materials not found.");

    private static Error V(string code, string message, int? line = null) =>
        line is null
            ? Error.Validation(code, message)
            : Error.Validation(code, message) with { Extras = new Dictionary<string, object?> { ["line"] = line.Value } };

    private static Error NotFoundLine(Error e, int line) => e with { Extras = new Dictionary<string, object?> { ["line"] = line } };

    private static Error? NeedView(Actor a) =>
        a.Access.AtLeast(PermissionKeys.InventoryView, PermissionLevels.Own) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryView, PermissionLevels.Own);

    /// <summary>Лише owner/admin і inventory.manage = all (deputy зі збереженим правом - forbidden_role).</summary>
    private static Error? NeedManageAll(Actor a)
    {
        if (a.Role is not (Roles.Owner or Roles.Admin)) return Error.Forbidden("forbidden_role", "This action is not available for your role.");
        return a.Access.AtLeast(PermissionKeys.InventoryManage, PermissionLevels.All) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryManage, PermissionLevels.All);
    }

    private static bool Scale3(decimal v) => decimal.Round(v, 3) == v;

    private static ProductRef Ref(ProductDto p) => new(p.Id, p.Name, p.Unit, p.Purpose, p.Category, p.Barcode, p.IsActive);

    // ---------- читання ----------

    public async Task<Result<ServiceMaterialsList>> ListAsync(Actor actor, bool includeInactive, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await InventoryScope.ResolveAsync(actor, PermissionKeys.InventoryView, store.SpecialistLocationIdsAsync, ct);
        var sets = (await store.ListMaterialSetsAsync(null, ct)).Where(s => s.LocationId is null || scope.Contains(s.LocationId.Value)).ToList();
        var services = await store.GetServicesAsync([.. sets.Select(s => s.ServiceId).Distinct()], ct);
        var chosen = sets.GroupBy(s => s.ServiceId).Where(g => services.TryGetValue(g.Key, out var sv) && (includeInactive || sv.IsActive))
            .Select(g => services[g.Key]).OrderBy(s => s.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(s => s.Id).ToList();
        var ctx = await BuildContextAsync(sets, ct);
        var items = chosen.Select(s => Build(s, sets.Where(x => x.ServiceId == s.Id).ToList(), ctx)).ToList();
        return new ServiceMaterialsList(items);
    }

    public async Task<Result<ServiceMaterialsDto>> GetAsync(Actor actor, Guid serviceId, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var services = await store.GetServicesAsync([serviceId], ct);
        if (!services.TryGetValue(serviceId, out var service)) return ServiceNotFound;
        var scope = await InventoryScope.ResolveAsync(actor, PermissionKeys.InventoryView, store.SpecialistLocationIdsAsync, ct);
        var sets = (await store.ListMaterialSetsAsync(serviceId, ct)).Where(s => s.LocationId is null || scope.Contains(s.LocationId.Value)).ToList();
        return Build(service, sets, await BuildContextAsync(sets, ct));
    }

    private sealed record Ctx(IReadOnlyDictionary<Guid, ProductDto> Products, IReadOnlyDictionary<Guid, LocationRow> Locations);

    private async Task<Ctx> BuildContextAsync(IReadOnlyCollection<MaterialSetRow> sets, CancellationToken ct) =>
        new(await store.GetProductsAsync([.. sets.SelectMany(s => s.Lines.Select(l => l.ProductId)).Distinct()], ct),
            (await store.ListInventoryLocationsAsync(ct)).ToDictionary(l => l.Id));

    private static MaterialSetDto ToSet(MaterialSetRow s, Ctx ctx) =>
        new(s.ServiceId, s.LocationId,
            [.. s.Lines.Where(l => ctx.Products.ContainsKey(l.ProductId)).Select(l => new MaterialLineDto(Ref(ctx.Products[l.ProductId]), l.Quantity))
                .OrderBy(l => l.Product.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(l => l.Product.Id)],
            s.UpdatedAt, s.UpdatedByName is null ? null : new InventoryNamedRef(s.UpdatedByUserId, s.UpdatedByName));

    private static ServiceMaterialsDto Build(ServiceRow service, IReadOnlyList<MaterialSetRow> sets, Ctx ctx)
    {
        var network = sets.FirstOrDefault(s => s.LocationId is null);
        var locs = sets.Where(s => s.LocationId is not null && ctx.Locations.ContainsKey(s.LocationId.Value))
            .Select(s =>
            {
                var d = ToSet(s, ctx);
                return new LocationMaterialSetDto(d.ServiceId, d.LocationId, d.Lines, d.UpdatedAt, d.UpdatedBy, ctx.Locations[s.LocationId!.Value].Name);
            })
            .OrderBy(l => l.LocationName.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(l => l.LocationId).ToList();
        return new ServiceMaterialsDto(service.Id, service.Name, service.IsActive, network is null ? null : ToSet(network, ctx), locs);
    }

    // ---------- зміна ----------

    public async Task<Result<MaterialSetDto>> PutAsync(Actor actor, Guid serviceId, PutMaterialsRequest? req, CancellationToken ct)
    {
        if (NeedManageAll(actor) is { } f) return f;

        // форма
        var lines = req?.Lines;
        if (lines is null) return V("invalid_lines", "lines is required.");
        if (req!.LocationId is null && lines.Count == 0) return V("invalid_lines", "A network set needs at least one line (use DELETE to remove it).");
        if (lines.Count > MaxLines) return V("too_many_lines", $"At most {MaxLines} lines.");
        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l is null || l.ProductId is null) return V("invalid_lines", "productId is required.", i);
            if (l.Quantity is not { } q || q <= 0 || q > MaxQuantity || !Scale3(q)) return V("invalid_quantity", "Quantity must be 0 < q <= 100000 with at most 3 decimals.", i);
            if (!seen.Add(l.ProductId.Value)) return V("duplicate_line", "A product must not repeat in a set.", i);
        }

        // послуга, заклад
        var services = await store.GetServicesAsync([serviceId], ct);
        if (!services.ContainsKey(serviceId)) return ServiceNotFound;
        if (req.LocationId is { } lid && (await store.ListInventoryLocationsAsync(ct)).All(l => l.Id != lid)) return LocationNotFound;

        // товари
        var ids = lines.Select(l => l.ProductId!.Value).ToList();
        var products = await store.GetProductsAsync(ids, ct);
        for (var i = 0; i < lines.Count; i++)
            if (!products.ContainsKey(lines[i].ProductId!.Value)) return NotFoundLine(ProductNotFound, i);
        for (var i = 0; i < lines.Count; i++)
        {
            var p = products[lines[i].ProductId!.Value];
            if (!p.IsActive || p.Purpose is not ("salon_use" or "both")) return V("product_not_for_salon_use", "The product is not for salon use.", i);
        }
        if (req.LocationId is { } loc && lines.Count > 0)
        {
            var enabled = await store.GetEnabledMapAsync(loc, ids, ct);
            for (var i = 0; i < lines.Count; i++)
                if (enabled.TryGetValue(lines[i].ProductId!.Value, out var on) && !on) return V("product_disabled_for_location", "The product is disabled for this location.", i);
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var q = lines[i].Quantity!.Value;
            if (products[lines[i].ProductId!.Value].Unit is "pcs" or "pack" && q != decimal.Truncate(q)) return V("invalid_quantity", "Quantity must be a whole number for this unit.", i);
        }

        var rows = lines.Select(l => new MaterialLineRow(l.ProductId!.Value, l.Quantity!.Value)).ToList();
        var r = await store.PutMaterialSetAsync(serviceId, req.LocationId, rows, actor.UserId, clock.GetUtcNow(), ct);
        switch (r.Status)
        {
            case MaterialPutStatus.ServiceNotFound: return ServiceNotFound;
            case MaterialPutStatus.LocationNotFound: return LocationNotFound;
            case MaterialPutStatus.Rejected:
                return r.Code switch
                {
                    "product_not_found" => r.Line is { } nl ? NotFoundLine(ProductNotFound, nl) : ProductNotFound,
                    "product_disabled_for_location" => V(r.Code, "The product is disabled for this location.", r.Line),
                    _ => V("product_not_for_salon_use", "The product is not for salon use.", r.Line),
                };
        }
        logger?.LogInformation("inventory_materials_changed service={Service} location={Location} lines={Lines} user={User}",
            serviceId, req.LocationId?.ToString() ?? "-", rows.Count, actor.UserId);
        var ctx = await BuildContextAsync([r.Set!], ct);
        return ToSet(r.Set!, ctx);
    }

    public async Task<Result<bool>> DeleteAsync(Actor actor, Guid serviceId, Guid? locationId, CancellationToken ct)
    {
        if (NeedManageAll(actor) is { } f) return f;
        if (!(await store.GetServicesAsync([serviceId], ct)).ContainsKey(serviceId)) return ServiceNotFound;
        if (locationId is { } lid && (await store.ListInventoryLocationsAsync(ct)).All(l => l.Id != lid)) return LocationNotFound;
        if (!await store.DeleteMaterialSetAsync(serviceId, locationId, ct)) return MaterialsNotFound;
        logger?.LogInformation("inventory_materials_changed service={Service} location={Location} lines={Lines} user={User}",
            serviceId, locationId?.ToString() ?? "-", 0, actor.UserId);
        return true;
    }
}
