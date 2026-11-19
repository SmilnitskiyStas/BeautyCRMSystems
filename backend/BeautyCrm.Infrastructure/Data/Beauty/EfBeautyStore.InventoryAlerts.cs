using System.Globalization;
using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IStaffAlertStore / IStaffAlertWriter (ADR-025 §11, §32.19, TASK-886): сповіщення персоналу. Усе під RLS tenant. ListInventoryLocationsAsync і
// SpecialistLocationIdsAsync спільні з IInventoryStore. Writer виконується в ПОТОЧНІЙ транзакції контексту (автосписання викликає його в своїй).
public sealed partial class EfBeautyStore : IStaffAlertStore, IStaffAlertWriter
{
    // ---------- writer ----------

    /// <summary>INSERT ... ON CONFLICT (tenant_id, dedupe_key) DO NOTHING: не ламає транзакцію виклику при повторі.</summary>
    public async Task<bool> CreateAsync(StaffAlertDraft d, DateTimeOffset now, CancellationToken ct)
    {
        var n = await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO beauty_staff_alerts (id, tenant_id, kind, location_id, product_id, batch_id, operation_id, quantity, threshold, expires_on, days_bucket,
                                 dedupe_key, status, created_at, updated_at)
VALUES ({Guid.NewGuid()}, NULLIF(current_setting('app.tenant_id', true), '')::uuid, {d.Kind}, {d.LocationId}, {d.ProductId}, {d.BatchId}::uuid, {d.OperationId}::uuid,
        {d.Quantity}, {d.Threshold}::numeric, {d.ExpiresOn}::date, {d.DaysBucket}::smallint, {d.DedupeKey}, 'open', {now}, {now})
ON CONFLICT (tenant_id, dedupe_key) DO NOTHING", ct);
        return n > 0;
    }

    public Task<bool> CreateShortageAlertAsync(Guid locationId, Guid productId, Guid operationId, decimal shortageQuantity, DateTimeOffset now, CancellationToken ct) =>
        CreateAsync(new StaffAlertDraft(StaffAlertKinds.Shortage, locationId, productId, null, operationId, shortageQuantity, null, null, null,
            StaffAlertKeys.Shortage(operationId, productId)), now, ct);

    // ---------- читання ----------

    private async Task<List<StaffAlertDto>> MapAlertsAsync(IReadOnlyList<StaffAlert> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var locIds = rows.Select(r => r.LocationId).Distinct().ToList();
        var prodIds = rows.Select(r => r.ProductId).Distinct().ToList();
        var batchIds = rows.Where(r => r.BatchId != null).Select(r => r.BatchId!.Value).Distinct().ToList();
        var userIds = rows.Where(r => r.ReadByUserId != null).Select(r => r.ReadByUserId!.Value).ToList();
        var locs = await db.Locations.AsNoTracking().Where(l => locIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var prods = (await db.Products.AsNoTracking().Where(p => prodIds.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id, ToRef);
        var batches = batchIds.Count == 0 ? new Dictionary<Guid, StaffAlertBatchDto>()
            : await db.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => new StaffAlertBatchDto(b.Id, b.ExpiresOn, b.LotNumber), ct);
        var users = await UserNamesAsync(userIds, ct);
        return [.. rows.Select(r => new StaffAlertDto(r.Id, r.Kind, r.Status, new InventoryNamedRef(r.LocationId, locs.GetValueOrDefault(r.LocationId, "")), prods[r.ProductId],
            r.BatchId is { } b ? batches.GetValueOrDefault(b) : null, r.OperationId, r.Quantity, r.Threshold, r.ExpiresOn, r.DaysBucket, r.CreatedAt, r.ReadAt,
            Named(r.ReadByUserId, users), r.ResolvedAt))];
    }

    public async Task<IReadOnlyList<StaffAlertDto>> ListStaffAlertsAsync(StaffAlertFilter f, string[]? after, int take, CancellationToken ct)
    {
        var statuses = f.Statuses.ToArray();
        var q = db.StaffAlerts.AsNoTracking().Where(a => statuses.Contains(a.Status));
        if (f.LocationIds is { } ids) q = q.Where(a => ids.Contains(a.LocationId));
        if (f.LocationId is { } loc) q = q.Where(a => a.LocationId == loc);
        if (f.Kind is { } kind) q = q.Where(a => a.Kind == kind);
        if (after is not null)
        {
            var t = new DateTimeOffset(long.Parse(after[0], CultureInfo.InvariantCulture), TimeSpan.Zero);
            var aid = Guid.Parse(after[1]);
            q = q.Where(a => a.CreatedAt < t || (a.CreatedAt == t && a.Id.CompareTo(aid) < 0));
        }
        var rows = await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take).ToListAsync(ct);
        return await MapAlertsAsync(rows, ct);
    }

    public async Task<StaffAlertDto?> GetStaffAlertAsync(Guid id, CancellationToken ct)
    {
        var a = await db.StaffAlerts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is null ? null : (await MapAlertsAsync([a], ct))[0];
    }

    public async Task<int> CountOpenAlertsAsync(IReadOnlyList<Guid>? locationIds, int cap, CancellationToken ct)
    {
        var q = db.StaffAlerts.AsNoTracking().Where(a => a.Status == "open");
        if (locationIds is { } ids) q = q.Where(a => ids.Contains(a.LocationId));
        return await q.Take(cap).CountAsync(ct);
    }

    public async Task<StaffAlertDto?> MarkAlertReadAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await db.StaffAlerts.Where(a => a.Id == id && a.Status == "open")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "read").SetProperty(a => a.ReadAt, now).SetProperty(a => a.ReadByUserId, userId)
                .SetProperty(a => a.UpdatedAt, now), ct);
        return await GetStaffAlertAsync(id, ct);
    }

    public async Task<int> MarkAllAlertsReadAsync(IReadOnlyList<Guid>? locationIds, Guid? locationId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var q = db.StaffAlerts.Where(a => a.Status == "open");
        if (locationIds is { } ids) q = q.Where(a => ids.Contains(a.LocationId));
        if (locationId is { } loc) q = q.Where(a => a.LocationId == loc);
        return await q.ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "read").SetProperty(a => a.ReadAt, now).SetProperty(a => a.ReadByUserId, userId)
            .SetProperty(a => a.UpdatedAt, now), ct);
    }
}
