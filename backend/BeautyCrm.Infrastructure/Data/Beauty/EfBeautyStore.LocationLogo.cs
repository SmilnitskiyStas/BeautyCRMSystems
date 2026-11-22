using BeautyCrm.Application.Features.BeautyLocations;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// ILocationLogoStore: ключ логотипу закладу під RLS tenant. Блокування - рядок закладу FOR UPDATE (як у PUT /locations).
public sealed partial class EfBeautyStore : ILocationLogoStore
{
    public async Task<LogoWriteResult> SetLogoAsync(Guid locationId, string? newKey, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var l = await LockLocationForUpdateAsync(locationId, ct);
        if (l is null) return new(false);
        var old = l.LogoStorageKey;
        if (old != newKey)
        {
            l.LogoStorageKey = newKey;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(true, ToLocationDto(l), old);
    }
}
