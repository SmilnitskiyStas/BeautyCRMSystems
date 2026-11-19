namespace BeautyCrm.Application.Features.BeautyChatFlow;

/// <summary>
/// Відстань між двома точками (формула haversine, R = 6371 км) без PostGIS (ADR-020, §28.9). Чиста функція: нічого не зберігає й не логує.
/// Координати клієнта передаються лише параметрами в межах одного виклику.
/// </summary>
public static class GeoDistance
{
    public const double EarthRadiusKm = 6371.0;

    public static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double deg) => deg * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>
    /// Заклади за зростанням відстані до (<paramref name="lat"/>, <paramref name="lon"/>) (рівність → назва, id), далі заклади без координат за назвою, id.
    /// Відстань повертається лише для закладів з координатами (null - немає).
    /// </summary>
    public static IReadOnlyList<(FlowLocationItem Location, double? Km)> Sort(IReadOnlyList<FlowLocationItem> locations, double lat, double lon)
    {
        var with = locations.Where(l => l.HasCoordinates)
            .Select(l => (Location: l, Km: (double?)Km(lat, lon, l.Latitude!.Value, l.Longitude!.Value)))
            .OrderBy(x => Math.Round(x.Km!.Value, 3)).ThenBy(x => x.Location.Name, StringComparer.Ordinal).ThenBy(x => x.Location.Id);
        var without = locations.Where(l => !l.HasCoordinates)
            .OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id).Select(l => (Location: l, Km: (double?)null));
        return with.Concat(without).ToList();
    }
}
