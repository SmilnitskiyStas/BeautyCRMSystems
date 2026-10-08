using BeautyCrm.Application.Features.BeautyOverview;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IOverviewStore (TASK-690): довідник закладів і допоміжні вибірки для огляду дня. Усе під RLS tenant.
public sealed partial class EfBeautyStore : IOverviewStore
{
    public async Task<IReadOnlyList<LocationDto>> ListLocationsAsync(CancellationToken ct) =>
        await db.Locations.AsNoTracking().OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new LocationDto(l.Id, l.Name, l.Address, l.Timezone, l.IsActive, l.Phone)).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, int>> GetActiveServiceDurationsAsync(CancellationToken ct) =>
        await db.Services.AsNoTracking().Where(s => s.IsActive).ToDictionaryAsync(s => s.Id, s => s.DurationMinutes, ct);

    public Task<int> CountNewClientsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        return db.Clients.AsNoTracking().CountAsync(c => c.DeletedAt == null && c.CreatedAt >= from && c.CreatedAt < to, ct);
    }
}
