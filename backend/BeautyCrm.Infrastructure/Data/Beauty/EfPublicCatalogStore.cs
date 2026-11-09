using BeautyCrm.Application.Features.BeautyPublicCatalog;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// ADR-011: єдиний шлях cross-tenant публічного читання — SECURITY DEFINER-функції beauty_public_* (роль beautycrm_catalog,
/// фіксований набір колонок). Окреме підключення без tenant-контексту: runtime-роль без app.tenant_id не бачить жодного
/// рядка таблиць напряму. Координати — лише параметри запиту: не логуються (логування тут немає), не зберігаються.
/// </summary>
public sealed class EfPublicCatalogStore(IConfiguration config) : IPublicCatalogStore
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString(DataServiceExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<PublicLocationRefDto?> ResolveAsync(string code, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT public_code, tenant_slug, location_id, name, address, city, timezone FROM public.beauty_public_resolve_location(@c)", conn);
        cmd.Parameters.AddWithValue("c", code);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRef(r) : null;
    }

    public async Task<IReadOnlyList<PublicNearbyItemDto>> NearbyAsync(double lat, double lng, double radiusKm, int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT public_code, tenant_slug, location_id, name, address, city, timezone, distance_km " +
            "FROM public.beauty_public_nearby_locations(@lat, @lng, @r, @n)", conn);
        cmd.Parameters.AddWithValue("lat", lat);
        cmd.Parameters.AddWithValue("lng", lng);
        cmd.Parameters.AddWithValue("r", radiusKm);
        cmd.Parameters.AddWithValue("n", limit);
        var list = new List<PublicNearbyItemDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var x = ReadRef(r);
            list.Add(new PublicNearbyItemDto(x.PublicCode, x.TenantSlug, x.LocationId, x.Name, x.Address, x.City, x.Timezone, r.GetDecimal(7)));
        }
        return list;
    }

    public async Task<IReadOnlyList<PublicLocationRefDto>> SearchByCityAsync(string city, int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT public_code, tenant_slug, location_id, name, address, city, timezone FROM public.beauty_public_search_locations_by_city(@c, @n)", conn);
        cmd.Parameters.AddWithValue("c", city);
        cmd.Parameters.AddWithValue("n", limit);
        var list = new List<PublicLocationRefDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadRef(r));
        return list;
    }

    private static PublicLocationRefDto ReadRef(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetGuid(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6));
}
