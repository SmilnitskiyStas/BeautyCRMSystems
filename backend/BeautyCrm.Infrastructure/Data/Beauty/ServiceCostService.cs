using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

public sealed record CostUpdatedByDto(Guid Id, string Name);

/// <summary>Cost = {costAmount, masterPercent, updatedAt, updatedBy?} (§24.6.1).</summary>
public sealed record ServiceCostDto(decimal CostAmount, decimal? MasterPercent, DateTimeOffset UpdatedAt, CostUpdatedByDto? UpdatedBy);

/// <summary>Cost &amp; {locationId, locationName}.</summary>
public sealed record LocationCostDto(
    Guid LocationId, string LocationName, decimal CostAmount, decimal? MasterPercent, DateTimeOffset UpdatedAt, CostUpdatedByDto? UpdatedBy);

public sealed record ServiceCostsDto(Guid ServiceId, ServiceCostDto? Network, IReadOnlyList<LocationCostDto> Locations);

public sealed record ServiceCostListItemDto(
    Guid ServiceId, string ServiceName, bool IsActive, ServiceCostDto? Network, IReadOnlyList<LocationCostDto> Locations);

public sealed record ServiceCostListDto(IReadOnlyList<ServiceCostListItemDto> Items);

/// <summary>PUT: costAmount обов'язковий (відсутній -> 422 invalid_cost); locationId null/відсутній = мережа.</summary>
public sealed record PutServiceCostRequest(Guid? LocationId, decimal? CostAmount, decimal? MasterPercent);

/// <summary>Cost &amp; {locationId}.</summary>
public sealed record PutServiceCostResultDto(
    Guid? LocationId, decimal CostAmount, decimal? MasterPercent, DateTimeOffset UpdatedAt, CostUpdatedByDto? UpdatedBy);

/// <summary>
/// TASK-763 (§24.6.1, ADR-014 §2.1): CRUD собівартості послуг (мережа + override закладу) з аудитом без значень.
/// Доступ owner/admin (specialist -> 403 forbidden_role; фільтр на контролері, тут повторна перевірка). Tenant лише з Actor і scope
/// (RLS FORCE); чужий serviceId/locationId -> 404. Логи й аудит несуть id, дію та назви змінених полів - суми й відсотки не логуються.
/// </summary>
public sealed class ServiceCostService(BeautyDbContext db, TenantContext tenant, ILogger<ServiceCostService> log)
{
    private const string UniqueViolation = "23505";
    public const decimal MaxCost = 1_000_000m;
    public const string ForbiddenRole = "forbidden_role";
    private const string FieldCost = "cost_amount", FieldPercent = "master_percent";

    private Error? Guard(Actor actor)
    {
        TenantGuard.Ensure(tenant, actor.TenantId);
        return actor.Role is Roles.Owner or Roles.Admin
            ? null
            : Error.Forbidden(ForbiddenRole, "This action is not available for your role.");
    }

    private static Error ServiceNotFound() => Error.NotFound("service_not_found", "Service not found.");
    private static Error LocationNotFound() => Error.NotFound("location_not_found", "Location not found.");

    // ------------------------------------------------------------------ читання

    public async Task<Result<ServiceCostListDto>> ListAsync(Actor actor, bool includeInactive, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        var services = await db.Services.AsNoTracking().Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.Name).ThenBy(s => s.Id).Select(s => new { s.Id, s.Name, s.IsActive }).ToListAsync(ct);
        var ids = services.Select(s => s.Id).ToList();
        var costs = await LoadCostsAsync(ids, ct);
        var items = services.Select(s =>
        {
            var (network, locs) = costs.TryGetValue(s.Id, out var c) ? c : (null, []);
            return new ServiceCostListItemDto(s.Id, s.Name, s.IsActive, network, locs);
        }).ToList();
        return new ServiceCostListDto(items);
    }

    public async Task<Result<ServiceCostsDto>> GetAsync(Actor actor, Guid serviceId, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!await db.Services.AsNoTracking().AnyAsync(s => s.Id == serviceId, ct)) return ServiceNotFound();
        var costs = await LoadCostsAsync([serviceId], ct);
        var (network, locs) = costs.TryGetValue(serviceId, out var c) ? c : (null, []);
        return new ServiceCostsDto(serviceId, network, locs);
    }

    private async Task<Dictionary<Guid, (ServiceCostDto? Network, IReadOnlyList<LocationCostDto> Locations)>> LoadCostsAsync(
        List<Guid> serviceIds, CancellationToken ct)
    {
        var rows = await db.ServiceCosts.AsNoTracking().Where(c => serviceIds.Contains(c.ServiceId))
            .Select(c => new { c.ServiceId, c.LocationId, c.CostAmount, c.MasterPercent, c.UpdatedAt, c.UpdatedByUserId }).ToListAsync(ct);
        var userIds = rows.Select(r => r.UpdatedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var locIds = rows.Where(r => r.LocationId != null).Select(r => r.LocationId!.Value).Distinct().ToList();
        var locNames = await db.Locations.AsNoTracking().Where(l => locIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        CostUpdatedByDto? By(Guid id) => users.TryGetValue(id, out var n) ? new CostUpdatedByDto(id, n) : null;
        return rows.GroupBy(r => r.ServiceId).ToDictionary(g => g.Key, g => (
            Network: g.Where(r => r.LocationId == null).Select(r => new ServiceCostDto(r.CostAmount, r.MasterPercent, r.UpdatedAt, By(r.UpdatedByUserId)))
                .FirstOrDefault(),
            Locations: (IReadOnlyList<LocationCostDto>)g.Where(r => r.LocationId != null)
                .Select(r => new LocationCostDto(r.LocationId!.Value, locNames.GetValueOrDefault(r.LocationId.Value, ""), r.CostAmount,
                    r.MasterPercent, r.UpdatedAt, By(r.UpdatedByUserId)))
                .OrderBy(l => l.LocationName, StringComparer.Ordinal).ThenBy(l => l.LocationId).ToList()));
    }

    // ------------------------------------------------------------------ запис

    public async Task<Result<PutServiceCostResultDto>> PutAsync(Actor actor, Guid serviceId, PutServiceCostRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (req.CostAmount is not { } cost || cost < 0 || cost > MaxCost || decimal.Round(cost, 2) != cost)
            return Error.Validation("invalid_cost", "costAmount is required: 0..1,000,000 with at most 2 decimal places.");
        if (req.MasterPercent is { } pct && (pct < 0 || pct > 100 || decimal.Round(pct, 2) != pct))
            return Error.Validation("invalid_master_percent", "masterPercent must be 0..100 with at most 2 decimal places.");
        if (!await db.Services.AsNoTracking().AnyAsync(s => s.Id == serviceId, ct)) return ServiceNotFound();
        if (req.LocationId is { } loc && !await db.Locations.AsNoTracking().AnyAsync(l => l.Id == loc, ct)) return LocationNotFound();

        // Паралельні PUT: UNIQUE (service, location) NULLS NOT DISTINCT - програвший вставку повторює шлях як оновлення.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await UpsertOnceAsync(actor, serviceId, req.LocationId, cost, req.MasterPercent, ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation } && attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<Result<PutServiceCostResultDto>> UpsertOnceAsync(
        Actor actor, Guid serviceId, Guid? locationId, decimal cost, decimal? percent, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockRowAsync(serviceId, locationId, ct);
        string[] changed;
        if (row is null)
        {
            row = new ServiceCost
            {
                Id = Guid.NewGuid(), ServiceId = serviceId, LocationId = locationId, CostAmount = cost, MasterPercent = percent,
                UpdatedByUserId = actor.UserId,
            };
            db.ServiceCosts.Add(row);
            changed = percent is null ? [FieldCost] : [FieldCost, FieldPercent];
        }
        else
        {
            var fields = new List<string>(2);
            if (row.CostAmount != cost) fields.Add(FieldCost);
            if (row.MasterPercent != percent) fields.Add(FieldPercent);
            changed = fields.ToArray();
            if (changed.Length > 0)
            {
                row.CostAmount = cost;
                row.MasterPercent = percent;
                row.UpdatedByUserId = actor.UserId;
            }
        }

        if (changed.Length > 0)
        {
            db.ServiceCostAudit.Add(new ServiceCostAuditRow
            {
                Id = Guid.NewGuid(), ServiceId = serviceId, LocationId = locationId, UserId = actor.UserId, Action = "set", ChangedFields = changed,
            });
            await db.SaveChangesAsync(ct);
            LogChanged(actor.UserId, serviceId, locationId, "set", changed);
        }
        await tx.CommitAsync(ct);

        var by = await db.Users.AsNoTracking().Where(u => u.Id == row.UpdatedByUserId).Select(u => new CostUpdatedByDto(u.Id, u.FullName))
            .FirstOrDefaultAsync(ct);
        return new PutServiceCostResultDto(locationId, row.CostAmount, row.MasterPercent, row.UpdatedAt, by);
    }

    public async Task<Result<bool>> DeleteAsync(Actor actor, Guid serviceId, Guid? locationId, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!await db.Services.AsNoTracking().AnyAsync(s => s.Id == serviceId, ct)) return ServiceNotFound();
        if (locationId is { } loc && !await db.Locations.AsNoTracking().AnyAsync(l => l.Id == loc, ct)) return LocationNotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockRowAsync(serviceId, locationId, ct);
        if (row is null) return Error.NotFound("cost_not_found", "Cost is not set.");
        var fields = row.MasterPercent is null ? new[] { FieldCost } : new[] { FieldCost, FieldPercent };
        db.ServiceCosts.Remove(row);
        db.ServiceCostAudit.Add(new ServiceCostAuditRow
        {
            Id = Guid.NewGuid(), ServiceId = serviceId, LocationId = locationId, UserId = actor.UserId, Action = "delete", ChangedFields = fields,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        LogChanged(actor.UserId, serviceId, locationId, "delete", fields);
        return true;
    }

    /// <summary>Рядок (service, location|мережа) під FOR UPDATE; паралельні PUT/DELETE того самого рядка серіалізуються.</summary>
    private Task<ServiceCost?> LockRowAsync(Guid serviceId, Guid? locationId, CancellationToken ct) =>
        db.ServiceCosts.FromSql($"""
            SELECT * FROM beauty_service_costs
            WHERE service_id = {serviceId} AND location_id IS NOT DISTINCT FROM {locationId}::uuid FOR UPDATE
            """).FirstOrDefaultAsync(ct);

    private void LogChanged(Guid userId, Guid serviceId, Guid? locationId, string action, IEnumerable<string> fields) =>
        log.LogInformation("service_cost_updated service={ServiceId} scope={Scope} location={LocationId} user={UserId} action={Action} changed={Changed}",
            serviceId, locationId is null ? "network" : "location", locationId?.ToString() ?? "-", userId, action, string.Join(",", fields));
}
