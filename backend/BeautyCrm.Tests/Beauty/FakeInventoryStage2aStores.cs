using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>In-memory <see cref="IInventoryNormsStore"/> (TASK-884): та сама семантика PUT (повна заміна набору), без транзакцій.</summary>
internal sealed class FakeNormsStore : IInventoryNormsStore
{
    public readonly List<LocationRow> Locations = [];
    public readonly Dictionary<Guid, List<Guid>> SpecLocations = [];
    public readonly Dictionary<Guid, ProductDto> Products = [];
    public readonly HashSet<(Guid Loc, Guid Prod)> Disabled = [];
    public readonly Dictionary<Guid, ServiceRow> Services = [];
    public readonly List<MaterialSetRow> Sets = [];
    /// <summary>Хук гонки: викликається в PutMaterialSetAsync перед записом; повернений код = відмова під блокуванням.</summary>
    public Func<(string Code, int Line)?>? UnderLock;

    public LocationRow AddLocation(string name = "Main") { var l = new LocationRow(Guid.NewGuid(), name, true, "Europe/Kyiv"); Locations.Add(l); return l; }

    public ServiceRow AddService(string name = "Haircut", bool active = true) { var s = new ServiceRow(Guid.NewGuid(), name, active); Services[s.Id] = s; return s; }

    public ProductDto AddProduct(string name = "Dye", string unit = "ml", string purpose = "both", bool active = true)
    {
        var p = new ProductDto(Guid.NewGuid(), name, null, unit, null, purpose, null, null, null, false, active, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        Products[p.Id] = p;
        return p;
    }

    public Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LocationRow>>(Locations);
    public Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid specialistId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(SpecLocations.GetValueOrDefault(specialistId) ?? []);
    public Task<IReadOnlyDictionary<Guid, ProductDto>> GetProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ProductDto>>(Products.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    public Task<IReadOnlyDictionary<Guid, bool>> GetEnabledMapAsync(Guid locationId, IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, bool>>(productIds.Where(p => Disabled.Contains((locationId, p))).ToDictionary(p => p, _ => false));
    public Task<IReadOnlyDictionary<Guid, ServiceRow>> GetServicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ServiceRow>>(Services.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    public Task<IReadOnlyList<MaterialSetRow>> ListMaterialSetsAsync(Guid? serviceId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MaterialSetRow>>([.. Sets.Where(s => serviceId is null || s.ServiceId == serviceId)]);

    public Task<MaterialPutResult> PutMaterialSetAsync(Guid serviceId, Guid? locationId, IReadOnlyList<MaterialLineRow> lines, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (UnderLock?.Invoke() is { } rej) return Task.FromResult(new MaterialPutResult(MaterialPutStatus.Rejected, null, rej.Code, rej.Line));
        Sets.RemoveAll(s => s.ServiceId == serviceId && s.LocationId == locationId);
        var row = new MaterialSetRow(serviceId, locationId, now, userId, "Admin User", [.. lines]);
        Sets.Add(row);
        return Task.FromResult(new MaterialPutResult(MaterialPutStatus.Ok, row));
    }

    public Task<bool> DeleteMaterialSetAsync(Guid serviceId, Guid? locationId, CancellationToken ct) =>
        Task.FromResult(Sets.RemoveAll(s => s.ServiceId == serviceId && s.LocationId == locationId) > 0);
}

/// <summary>In-memory <see cref="IStaffAlertStore"/> + <see cref="IStaffAlertWriter"/> (TASK-886): dedupe за ключем, keyset за (created_at, id) DESC.</summary>
internal sealed class FakeAlertStore : IStaffAlertStore, IStaffAlertWriter
{
    public readonly List<LocationRow> Locations = [];
    public readonly Dictionary<Guid, List<Guid>> SpecLocations = [];
    private readonly List<StaffAlertDto> _alerts = [];
    private readonly HashSet<string> _keys = [];
    public IReadOnlyList<StaffAlertDto> Alerts => _alerts;

    public LocationRow AddLocation(string name = "Main") { var l = new LocationRow(Guid.NewGuid(), name, true, "Europe/Kyiv"); Locations.Add(l); return l; }

    public ProductRef Product { get; } = new(Guid.NewGuid(), "Dye", "ml", "both", null, null, true);

    public StaffAlertDto Seed(Guid locationId, string kind = "low_stock", string status = "open", DateTimeOffset? createdAt = null, string? key = null)
    {
        var a = new StaffAlertDto(Guid.NewGuid(), kind, status, new InventoryNamedRef(locationId, Locations.First(l => l.Id == locationId).Name), Product,
            kind == "expiring" ? new StaffAlertBatchDto(Guid.NewGuid(), null, null) : null, kind == "shortage" ? Guid.NewGuid() : null, 1m, null, null, null,
            createdAt ?? DateTimeOffset.UnixEpoch, null, null, status == "resolved" ? DateTimeOffset.UnixEpoch : null);
        _alerts.Add(a);
        _keys.Add(key ?? "k:" + a.Id);
        return a;
    }

    public Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LocationRow>>(Locations);
    public Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid specialistId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(SpecLocations.GetValueOrDefault(specialistId) ?? []);

    public Task<IReadOnlyList<StaffAlertDto>> ListStaffAlertsAsync(StaffAlertFilter f, string[]? after, int take, CancellationToken ct)
    {
        IEnumerable<StaffAlertDto> q = _alerts.Where(a => f.Statuses.Contains(a.Status) && (f.LocationIds is null || f.LocationIds.Contains(a.Location.Id))
            && (f.LocationId is null || a.Location.Id == f.LocationId) && (f.Kind is null || a.Kind == f.Kind))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id);
        if (after is not null)
        {
            var t = new DateTimeOffset(long.Parse(after[0]), TimeSpan.Zero);
            var id = Guid.Parse(after[1]);
            q = q.Where(a => a.CreatedAt < t || (a.CreatedAt == t && a.Id.CompareTo(id) < 0));
        }
        return Task.FromResult<IReadOnlyList<StaffAlertDto>>([.. q.Take(take)]);
    }

    public Task<StaffAlertDto?> GetStaffAlertAsync(Guid id, CancellationToken ct) => Task.FromResult(_alerts.FirstOrDefault(a => a.Id == id));

    public Task<int> CountOpenAlertsAsync(IReadOnlyList<Guid>? locationIds, int cap, CancellationToken ct) =>
        Task.FromResult(Math.Min(cap, _alerts.Count(a => a.Status == "open" && (locationIds is null || locationIds.Contains(a.Location.Id)))));

    private void Update(StaffAlertDto a, Guid userId, DateTimeOffset now) =>
        _alerts[_alerts.IndexOf(a)] = a with { Status = "read", ReadAt = now, ReadBy = new InventoryNamedRef(userId, "User") };

    public Task<StaffAlertDto?> MarkAlertReadAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var a = _alerts.FirstOrDefault(x => x.Id == id);
        if (a is null) return Task.FromResult<StaffAlertDto?>(null);
        if (a.Status == "open") Update(a, userId, now);
        return Task.FromResult<StaffAlertDto?>(_alerts.First(x => x.Id == id));
    }

    public Task<int> MarkAllAlertsReadAsync(IReadOnlyList<Guid>? locationIds, Guid? locationId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var hit = _alerts.Where(a => a.Status == "open" && (locationIds is null || locationIds.Contains(a.Location.Id)) && (locationId is null || a.Location.Id == locationId)).ToList();
        foreach (var a in hit) Update(a, userId, now);
        return Task.FromResult(hit.Count);
    }

    public Task<bool> CreateAsync(StaffAlertDraft d, DateTimeOffset now, CancellationToken ct)
    {
        if (!_keys.Add(d.DedupeKey)) return Task.FromResult(false);
        _alerts.Add(new StaffAlertDto(Guid.NewGuid(), d.Kind, "open", new InventoryNamedRef(d.LocationId, "L"), Product, null, d.OperationId, d.Quantity, d.Threshold,
            d.ExpiresOn, d.DaysBucket, now, null, null, null));
        return Task.FromResult(true);
    }

    public Task<bool> CreateShortageAlertAsync(Guid locationId, Guid productId, Guid operationId, decimal shortageQuantity, DateTimeOffset now, CancellationToken ct) =>
        CreateAsync(new StaffAlertDraft(StaffAlertKinds.Shortage, locationId, productId, null, operationId, shortageQuantity, null, null, null,
            StaffAlertKeys.Shortage(operationId, productId)), now, ct);
}
