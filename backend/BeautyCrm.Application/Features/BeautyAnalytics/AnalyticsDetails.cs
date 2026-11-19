using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyAnalytics;

// ---- відповідь GET /analytics/network/details (§24.6.3). Жодних даних клієнта (ім'я, телефон, email, clientId, нотатки). ----

public sealed record DetailsRef(Guid Id, string Name);
public sealed record DetailsLocationRef(Guid Id, string Name, string Timezone);

public sealed record DetailsSummary(
    int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue, decimal DiscountTotal, decimal Cost, decimal MasterPayout,
    decimal NetIncome, decimal? MarginPercent, int WithoutCost, decimal RevenueWithoutCost);

public sealed record DetailsLocation(
    Guid LocationId, string Name, string Timezone, int Completed, decimal Revenue, decimal Cost, decimal MasterPayout, decimal NetIncome,
    decimal? MarginPercent, int WithoutCost);

/// <summary>Cost/MasterPercent/MasterPayout = null, коли CostMissing (знімка собівартості немає).</summary>
public sealed record DetailsItem(
    Guid AppointmentId, string LocalDate, string LocalTime, DetailsRef Location, DetailsRef Service, DetailsRef Specialist,
    decimal PriceOriginal, decimal Discount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DetailsRef? Promotion,
    decimal PriceFinal, decimal? Cost, decimal? MasterPercent, decimal? MasterPayout, decimal Net, bool CostMissing);

public sealed record DetailsReport(
    string FromDate, string ToDate, string Timezone,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DetailsLocationRef? Location,
    DetailsSummary Summary, IReadOnlyList<DetailsLocation> Locations, IReadOnlyList<DetailsItem> Items, bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);

// ---- рядки порту даних ----

/// <summary>Агрегат заклад: усі статуси у лічильниках, фінанси - лише completed.</summary>
public sealed record DetailsLocationRow(
    Guid LocationId, string Name, string Timezone, int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue,
    decimal DiscountTotal, decimal Cost, decimal MasterPayout, int WithoutCost, decimal RevenueWithoutCost);

public sealed record DetailsItemRow(
    Guid AppointmentId, DateTimeOffset StartsAt, string LocalDate, string LocalTime, Guid LocationId, string LocationName,
    Guid ServiceId, string ServiceName, Guid SpecialistId, string SpecialistName, Guid? PromotionId, string? PromotionName,
    decimal PriceOriginal, decimal PriceFinal, decimal? CostSnapshot, decimal? MasterPercentSnapshot, decimal? MasterPayoutSnapshot);

public interface IAnalyticsDetailsStore
{
    /// <summary>Заклади з >= 1 записом (будь-який статус), чия локальна дата (зона закладу) ∈ [from, to].</summary>
    Task<IReadOnlyList<DetailsLocationRow>> DetailsByLocationAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct);

    /// <summary>Completed записи за (starts_at, id) після курсора; повертає до <paramref name="take"/> рядків.</summary>
    Task<IReadOnlyList<DetailsItemRow>> DetailsPageAsync(
        DateOnly from, DateOnly to, Guid? locationId, DateTimeOffset? afterStartsAt, Guid? afterId, int take, CancellationToken ct);
}

/// <summary>
/// Деталі періоду (день або кошик тижня/місяця з series) для owner/admin (§24.6.3). Підсумок збігається з точкою series за той самий
/// діапазон (ті самі правила дня й формули, §24.5). Записи - курсорна пагінація (starts_at, id).
/// </summary>
public sealed class AnalyticsDetailsService(IAnalyticsStore refStore, IAnalyticsDetailsStore store)
{
    public const int MaxDays = 31;
    public const int DefaultTake = 100;
    public const int MaxTake = 200;

    private static readonly Regex DateLiteral = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static Error InvalidRange(string message) => Error.Validation("invalid_range", message);

    public async Task<Result<DetailsReport>> DetailsAsync(
        string? date, string? from, string? to, Guid? locationId, string? cursor, int? take, CancellationToken ct)
    {
        var range = ParseRange(date, from, to);
        if (range.Error is not null) return range.Error;
        var (f, t) = range.Value;

        var locations = await refStore.AnalyticsLocationsAsync(ct);
        DetailsLocationRef? filter = null;
        if (locationId is { } lid)
        {
            var l = locations.FirstOrDefault(x => x.Id == lid);
            if (l is null) return Error.NotFound("location_not_found", "Location not found.");
            filter = new DetailsLocationRef(l.Id, l.Name, l.Timezone);
        }

        var hash = ParamsHash(f, t, locationId);
        DateTimeOffset? afterAt = null;
        Guid? afterId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryParseCursor(cursor, hash, out var at, out var id)) return Error.Validation("invalid_cursor", "The cursor is invalid.");
            afterAt = at;
            afterId = id;
        }

        var size = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        var active = locations.Where(l => l.IsActive).OrderBy(l => l.Name).ThenBy(l => l.Id).FirstOrDefault();
        var timezone = active?.Timezone ?? "UTC";

        var rows = await store.DetailsByLocationAsync(f, t, locationId, ct);
        var page = await store.DetailsPageAsync(f, t, locationId, afterAt, afterId, size + 1, ct);

        var hasMore = page.Count > size;
        var shown = page.Take(size).ToList();
        var items = shown.Select(ToItem).ToList();
        var next = hasMore ? MakeCursor(shown[^1].StartsAt, shown[^1].AppointmentId, hash) : null;

        var revenue = rows.Sum(r => r.Revenue);
        var cost = rows.Sum(r => r.Cost);
        var payout = rows.Sum(r => r.MasterPayout);
        var net = revenue - cost - payout;
        var summary = new DetailsSummary(rows.Sum(r => r.Appointments), rows.Sum(r => r.Completed), rows.Sum(r => r.Cancelled),
            rows.Sum(r => r.NoShow), revenue, rows.Sum(r => r.DiscountTotal), cost, payout, net, AnalyticsService.MarginOf(revenue, net),
            rows.Sum(r => r.WithoutCost), rows.Sum(r => r.RevenueWithoutCost));
        var byLocation = rows
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.LocationId)
            .Select(r =>
            {
                var n = r.Revenue - r.Cost - r.MasterPayout;
                return new DetailsLocation(r.LocationId, r.Name, r.Timezone, r.Completed, r.Revenue, r.Cost, r.MasterPayout, n,
                    AnalyticsService.MarginOf(r.Revenue, n), r.WithoutCost);
            }).ToList();

        return new DetailsReport(Iso(f), Iso(t), timezone, filter, summary, byLocation, items, hasMore, next);
    }

    // ---------- розбір діапазону ----------

    private static Result<(DateOnly From, DateOnly To)> ParseRange(string? date, string? from, string? to)
    {
        var hasDate = !string.IsNullOrWhiteSpace(date);
        var hasFrom = !string.IsNullOrWhiteSpace(from);
        var hasTo = !string.IsNullOrWhiteSpace(to);
        if (hasDate && (hasFrom || hasTo)) return InvalidRange("Use either date or from and to.");
        if (hasDate)
        {
            return TryDate(date!, out var d) ? (d, d) : Error.Validation("invalid_date", "date must be a valid yyyy-MM-dd date (2000..2100).");
        }
        if (!hasFrom || !hasTo) return InvalidRange("date or both from and to are required.");
        if (!TryDate(from!, out var f) || !TryDate(to!, out var t))
            return InvalidRange("from and to must be valid dates (yyyy-MM-dd) within 2000..2100.");
        if (f > t) return InvalidRange("from must not be after to.");
        if (t.DayNumber - f.DayNumber + 1 > MaxDays) return InvalidRange("The range must not exceed 31 days.");
        return (f, t);
    }

    private static bool TryDate(string value, out DateOnly d)
    {
        d = default;
        var v = value.Trim();
        return DateLiteral.IsMatch(v)
               && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
               && d.Year is >= 2000 and <= 2100;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------- курсор: base64url("{utcTicks}|{id}|{hash параметрів}") ----------

    private static string ParamsHash(DateOnly from, DateOnly to, Guid? locationId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Iso(from)}|{Iso(to)}|{locationId:N}")))[..12];

    private static string MakeCursor(DateTimeOffset startsAt, Guid id, string hash)
    {
        var raw = Encoding.UTF8.GetBytes($"{startsAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:N}|{hash}");
        return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryParseCursor(string cursor, string expectedHash, out DateTimeOffset startsAt, out Guid id)
    {
        startsAt = default;
        id = default;
        if (cursor.Length > 256) return false;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 3 || parts[2] != expectedHash) return false;
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks is < 0 or > 3155378975999999999L) return false;
            if (!Guid.TryParseExact(parts[1], "N", out id)) return false;
            startsAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---------- мапінг ----------

    private static DetailsItem ToItem(DetailsItemRow r)
    {
        var missing = r.CostSnapshot is null;
        return new DetailsItem(r.AppointmentId, r.LocalDate, r.LocalTime, new DetailsRef(r.LocationId, r.LocationName),
            new DetailsRef(r.ServiceId, r.ServiceName), new DetailsRef(r.SpecialistId, r.SpecialistName), r.PriceOriginal,
            r.PriceOriginal - r.PriceFinal, r.PromotionId is { } pid ? new DetailsRef(pid, r.PromotionName ?? "") : null, r.PriceFinal,
            r.CostSnapshot, missing ? null : r.MasterPercentSnapshot, missing ? null : r.MasterPayoutSnapshot,
            r.PriceFinal - (r.CostSnapshot ?? 0m) - (r.MasterPayoutSnapshot ?? 0m), missing);
    }
}
