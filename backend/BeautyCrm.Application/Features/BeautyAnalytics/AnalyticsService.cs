using System.Globalization;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyAnalytics;

/// <summary>Revenue = сума price_final завершених записів; скасовані й no_show не враховуються.</summary>
public sealed record NetworkAnalytics(
    DateTimeOffset From, DateTimeOffset To, int Appointments, int Completed, int Cancelled, int NoShow,
    decimal Revenue, decimal AverageCheck, decimal CancellationRate, int NewClients);
public sealed record LocationAnalytics(
    Guid LocationId, string Name, int Appointments, int Completed, int Cancelled, decimal Revenue);
public sealed record PromotionAnalytics(
    Guid PromotionId, string Name, int Uses, decimal DiscountTotal, decimal Revenue);

public interface IAnalyticsStore
{
    // ---- legacy-режим (v0.19, заморожено): [from, to) за UTC-миттєвостями ----
    Task<NetworkAnalytics> NetworkAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<LocationAnalytics>> LocationsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<PromotionAnalytics>> PromotionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    // ---- датний режим (§19): усі вибірки фіксованої кількості, не залежать від довжини діапазону ----
    Task<IReadOnlyList<AnalyticsLocation>> AnalyticsLocationsAsync(CancellationToken ct);
    /// <summary>Записи, чия локальна дата (зона закладу) ∈ [from, to], згруповані заклад × дата × статус.</summary>
    Task<IReadOnlyList<DailyRow>> DailyAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct);
    Task<IReadOnlyList<BusyRow>> BusyAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct);
    Task<IReadOnlyList<AnalyticsLink>> LinksAsync(Guid? locationId, CancellationToken ct);
    /// <summary>Закриття закладів, що перетинають [from, to].</summary>
    Task<IReadOnlyList<AnalyticsSpan>> ClosuresAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct);
    /// <summary>Лише затверджені (approved) відсутності, що перетинають [from, to].</summary>
    Task<IReadOnlyList<AnalyticsSpan>> ApprovedAbsencesAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<int> NewClientsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    Task<IReadOnlyList<PromotionStatRow>> PromotionStatsAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<SpecialistStatRow>> SpecialistStatsAsync(DateOnly from, DateOnly to, Guid locationId, CancellationToken ct);
    Task<IReadOnlyList<ServiceStatRow>> TopServicesAsync(DateOnly from, DateOnly to, Guid locationId, int take, CancellationToken ct);
}

/// <summary>
/// Аналітика (§9, §19). Два режими from/to: значення yyyy-MM-dd = датний (локальні дні закладів), значення з часом = legacy
/// (заморожений, відповіді як у v0.19).
/// </summary>
public sealed class AnalyticsService(IAnalyticsStore store, TimeProvider clock)
{
    public const int MaxDays = 366;
    private const int MaxServices = 50;

    private static readonly Regex DateLiteral = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // =====================================================================================================================
    // Публічні операції
    // =====================================================================================================================

    public async Task<Result<object>> NetworkAsync(string? from, string? to, bool compare, CancellationToken ct)
    {
        var parsed = Parse(from, to);
        if (parsed.Error is not null) return parsed.Error;
        if (parsed.Value!.Legacy)
        {
            if (LegacyRange(parsed.Value, out var f, out var t) is { } e) return e;
            return Result<object>.Ok(await store.NetworkAsync(f, t, ct));
        }

        var re = await ReferenceAsync(ct);
        var range = Resolve(parsed.Value, re);
        if (range.Error is not null) return range.Error;
        var cur = range.Value;
        var prev = compare ? Previous(cur) : (DateRange?)null;

        var totals = await TotalsAsync(cur, re, ct);
        NetworkPrevious? previous = null;
        if (prev is { } p)
        {
            var pt = await TotalsAsync(p, re, ct);
            previous = new NetworkPrevious(Iso(p.From), Iso(p.To), pt.Appointments, pt.Completed, pt.Cancelled, pt.NoShow, pt.Revenue,
                pt.AverageCheck, pt.CancellationRate, pt.NewClients);
        }
        return Result<object>.Ok(new NetworkReport(Iso(cur.From), Iso(cur.To), re.TimezoneId,
            SlotCalculator.ToOffset(cur.From, TimeOnly.MinValue, re.Zone), SlotCalculator.ToOffset(cur.To.AddDays(1), TimeOnly.MinValue, re.Zone),
            totals.Appointments, totals.Completed, totals.Cancelled, totals.NoShow, totals.Revenue, totals.AverageCheck,
            totals.CancellationRate, totals.NewClients, previous));
    }

    public async Task<Result<object>> LocationsAsync(string? from, string? to, bool compare, CancellationToken ct)
    {
        var parsed = Parse(from, to);
        if (parsed.Error is not null) return parsed.Error;
        if (parsed.Value!.Legacy)
        {
            if (LegacyRange(parsed.Value, out var f, out var t) is { } e) return e;
            return Result<object>.Ok(await store.LocationsAsync(f, t, ct));
        }

        var re = await ReferenceAsync(ct);
        var range = Resolve(parsed.Value, re);
        if (range.Error is not null) return range.Error;
        var cur = range.Value;
        var prev = compare ? Previous(cur) : (DateRange?)null;

        var inputs = await LoadAvailabilityInputsAsync(prev?.From ?? cur.From, cur.To, null, re, ct);
        var current = await LocationPeriodAsync(cur, re, inputs, null, ct);
        var previous = prev is { } p ? await LocationPeriodAsync(p, re, inputs, null, ct) : null;

        var rows = new List<LocationReport>();
        foreach (var loc in re.Locations)
        {
            current.TryGetValue(loc.Id, out var c);
            c ??= LocationStats.Empty;
            var pr = previous is null ? null : previous.GetValueOrDefault(loc.Id, LocationStats.Empty);
            // §19.4: усі активні заклади + неактивні, що мають записи в будь-якому з періодів
            if (!loc.IsActive && c.Appointments == 0 && (pr?.Appointments ?? 0) == 0) continue;
            rows.Add(new LocationReport(loc.Id, loc.Name, c.Appointments, c.Completed, c.Cancelled, c.Revenue, c.NoShow, loc.Timezone,
                loc.IsActive, c.Busy, c.Avail, LoadOf(c.Busy, c.Avail),
                pr is null ? null : new LocationPrevious(pr.Appointments, pr.Completed, pr.Cancelled, pr.NoShow, pr.Revenue, pr.Busy, pr.Avail,
                    LoadOf(pr.Busy, pr.Avail))));
        }
        return Result<object>.Ok(rows.OrderByDescending(r => r.Revenue).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.LocationId).ToList());
    }

    public async Task<Result<object>> PromotionsAsync(string? from, string? to, bool compare, CancellationToken ct)
    {
        var parsed = Parse(from, to);
        if (parsed.Error is not null) return parsed.Error;
        if (parsed.Value!.Legacy)
        {
            if (LegacyRange(parsed.Value, out var f, out var t) is { } e) return e;
            return Result<object>.Ok(await store.PromotionsAsync(f, t, ct));
        }

        var re = await ReferenceAsync(ct);
        var range = Resolve(parsed.Value, re);
        if (range.Error is not null) return range.Error;
        var cur = range.Value;
        var current = await store.PromotionStatsAsync(cur.From, cur.To, ct);
        IReadOnlyDictionary<Guid, PromotionStatRow>? previous = null;
        if (compare)
        {
            var p = Previous(cur);
            previous = (await store.PromotionStatsAsync(p.From, p.To, ct)).ToDictionary(x => x.PromotionId);
        }

        var curById = current.ToDictionary(x => x.PromotionId);
        var ids = curById.Keys.Concat(previous?.Keys ?? []).Distinct();
        var rows = new List<PromotionReport>();
        foreach (var id in ids)
        {
            var c = curById.GetValueOrDefault(id);
            var pr = previous?.GetValueOrDefault(id);
            var name = c?.Name ?? pr!.Name;
            rows.Add(new PromotionReport(id, name, c?.Uses ?? 0, c?.Discount ?? 0m, c?.Revenue ?? 0m,
                previous is null ? null : new PromotionPrevious(pr?.Uses ?? 0, pr?.Discount ?? 0m, pr?.Revenue ?? 0m)));
        }
        return Result<object>.Ok(rows.OrderByDescending(r => r.Uses).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.PromotionId).ToList());
    }

    /// <summary>Динаміка мережі однією відповіддю (§19.2). Лише датний режим.</summary>
    public async Task<Result<SeriesReport>> SeriesAsync(string? from, string? to, CancellationToken ct)
    {
        var parsed = Parse(from, to);
        if (parsed.Error is not null) return parsed.Error;
        if (parsed.Value!.Legacy) return InvalidRange("The series endpoint accepts only yyyy-MM-dd dates.");

        var re = await ReferenceAsync(ct);
        var range = Resolve(parsed.Value, re);
        if (range.Error is not null) return range.Error;
        var r = range.Value;

        var days = (await store.DailyAsync(r.From, r.To, null, ct)).GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.ToList());
        var granularity = r.Days <= 31 ? "day" : r.Days <= 120 ? "week" : "month";
        var points = new List<SeriesPoint>();
        for (var cursor = r.From; cursor <= r.To;)
        {
            var end = BucketEnd(cursor, granularity);
            if (end > r.To) end = r.To;
            int appts = 0, completed = 0, cancelled = 0, noShow = 0;
            var revenue = 0m;
            for (var d = cursor; d <= end; d = d.AddDays(1))
            {
                if (!days.TryGetValue(d, out var rows)) continue;
                foreach (var row in rows)
                {
                    appts += row.Count;
                    revenue += row.Revenue;
                    switch (row.Status)
                    {
                        case "completed": completed += row.Count; break;
                        case "cancelled": cancelled += row.Count; break;
                        case "no_show": noShow += row.Count; break;
                    }
                }
            }
            points.Add(new SeriesPoint(Iso(cursor), Iso(end), appts, completed, cancelled, noShow, revenue));
            cursor = end.AddDays(1);
        }
        return new SeriesReport(Iso(r.From), Iso(r.To), re.TimezoneId, granularity, points);
    }

    /// <summary>Спеціалісти й топ послуг закладу за період, також завантаження (§19.4).</summary>
    public async Task<Result<BreakdownReport>> BreakdownAsync(Guid locationId, string? from, string? to, CancellationToken ct)
    {
        var parsed = Parse(from, to);
        if (parsed.Error is not null) return parsed.Error;
        if (parsed.Value!.Legacy) return InvalidRange("The breakdown endpoint accepts only yyyy-MM-dd dates.");

        var re = await ReferenceAsync(ct);
        var location = re.AllLocations.FirstOrDefault(l => l.Id == locationId);
        if (location is null) return Error.NotFound("location_not_found", "Location not found.");
        var range = Resolve(parsed.Value, re);
        if (range.Error is not null) return range.Error;
        var r = range.Value;

        var inputs = await LoadAvailabilityInputsAsync(r.From, r.To, locationId, re, ct);
        var avail = Availability(r, inputs, locationId);
        var busy = (await store.BusyAsync(r.From, r.To, locationId, ct)).ToDictionary(b => b.SpecialistId, b => b.Minutes);
        var stats = (await store.SpecialistStatsAsync(r.From, r.To, locationId, ct)).GroupBy(s => s.SpecialistId).ToList();
        var services = await store.TopServicesAsync(r.From, r.To, locationId, MaxServices, ct);

        var names = inputs.Links.Where(l => l.LocationId == locationId).ToDictionary(l => l.SpecialistId, l => (l.SpecialistName, l.SpecialistActive));
        var availBySpec = avail.ToDictionary(kv => kv.Key.Spec, kv => kv.Value);

        var specialists = new List<BreakdownSpecialist>();
        var withStats = new HashSet<Guid>();
        foreach (var g in stats)
        {
            withStats.Add(g.Key);
            var first = g.First();
            specialists.Add(ToSpecialist(g.Key, first.Name, first.IsActive, g.ToList(), busy, availBySpec));
        }
        foreach (var (specId, av) in availBySpec)
        {
            if (av <= 0 || withStats.Contains(specId)) continue;
            var (name, active) = names[specId];
            specialists.Add(ToSpecialist(specId, name, active, [], busy, availBySpec));
        }

        var totalBusy = busy.Values.Sum();
        var totalAvail = availBySpec.Values.Sum();
        return new BreakdownReport(location.Id, location.Name, location.Timezone, Iso(r.From), Iso(r.To), totalBusy, totalAvail,
            LoadOf(totalBusy, totalAvail),
            specialists.OrderByDescending(s => s.Revenue).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.SpecialistId).ToList(),
            services.Select(s => new BreakdownService(s.ServiceId, s.Name, s.Completed, s.Revenue)).ToList());
    }

    private static BreakdownSpecialist ToSpecialist(Guid id, string name, bool active, List<SpecialistStatRow> rows,
        IReadOnlyDictionary<Guid, int> busy, IReadOnlyDictionary<Guid, int> avail)
    {
        int Count(string status) => rows.Where(x => x.Status == status).Sum(x => x.Count);
        var b = busy.GetValueOrDefault(id);
        var a = avail.GetValueOrDefault(id);
        return new BreakdownSpecialist(id, name, active, rows.Sum(x => x.Count), Count("completed"), Count("cancelled"), Count("no_show"),
            rows.Where(x => x.Status == "completed").Sum(x => x.Revenue), b, a, LoadOf(b, a));
    }

    // =====================================================================================================================
    // Розбір діапазону (§19.1)
    // =====================================================================================================================

    private enum ValueKind { Absent, Date, Instant }

    private sealed record Parsed(bool Legacy, DateOnly? From, DateOnly? To, DateTimeOffset? FromAt, DateTimeOffset? ToAt);

    private readonly record struct DateRange(DateOnly From, DateOnly To)
    {
        public int Days => To.DayNumber - From.DayNumber + 1;
    }

    private static Error InvalidRange(string message) => Error.Validation("invalid_range", message);

    private static (ValueKind Kind, DateOnly Date, DateTimeOffset At, bool Ok) Classify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (ValueKind.Absent, default, default, true);
        var v = value.Trim();
        if (DateLiteral.IsMatch(v))
        {
            var ok = DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                     && d.Year is >= 2000 and <= 2100;
            return (ValueKind.Date, d, default, ok);
        }
        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? (ValueKind.Instant, default, at, true)
            : (ValueKind.Instant, default, default, false);
    }

    private static Result<Parsed> Parse(string? from, string? to)
    {
        var f = Classify(from);
        var t = Classify(to);
        if (!f.Ok || !t.Ok) return InvalidRange("from and to must be valid dates (yyyy-MM-dd) within 2000..2100.");
        if ((f.Kind == ValueKind.Date && t.Kind == ValueKind.Instant) || (f.Kind == ValueKind.Instant && t.Kind == ValueKind.Date))
            return InvalidRange("from and to must use the same format.");
        if (f.Kind == ValueKind.Instant || t.Kind == ValueKind.Instant)
            return new Parsed(true, null, null, f.Kind == ValueKind.Instant ? f.At : null, t.Kind == ValueKind.Instant ? t.At : null);
        return new Parsed(false, f.Kind == ValueKind.Date ? f.Date : null, t.Kind == ValueKind.Date ? t.Date : null, null, null);
    }

    /// <summary>Legacy (v0.19): за замовчуванням останні 30 днів; додано ліміт 366 днів (§19.1).</summary>
    private Error? LegacyRange(Parsed p, out DateTimeOffset f, out DateTimeOffset t)
    {
        t = p.ToAt ?? clock.GetUtcNow();
        f = p.FromAt ?? t.AddDays(-30);
        if (f >= t) return Error.Validation("invalid_range", "from must be before to.");
        return t - f > TimeSpan.FromDays(MaxDays) ? Error.Validation("invalid_range", "The range must not exceed 366 days.") : null;
    }

    private Result<DateRange> Resolve(Parsed p, Reference re)
    {
        var to = p.To ?? re.Today;
        var from = p.From ?? to.AddDays(-29);
        if (from > to) return InvalidRange("from must not be after to.");
        var range = new DateRange(from, to);
        if (range.Days > MaxDays) return InvalidRange("The range must not exceed 366 days.");
        return range;
    }

    private static DateRange Previous(DateRange r) => new(r.From.AddDays(-r.Days), r.From.AddDays(-1));

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly BucketEnd(DateOnly start, string granularity) => granularity switch
    {
        "day" => start,
        "week" => start.AddDays(6 - (((int)start.DayOfWeek + 6) % 7)), // ISO-тиждень пн-нд
        _ => new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month)),
    };

    // =====================================================================================================================
    // Еталонна зона (як OverviewService): перший активний заклад за Name, далі Id; без активних - UTC.
    // =====================================================================================================================

    private sealed record Reference(IReadOnlyList<AnalyticsLocation> AllLocations, IReadOnlyList<AnalyticsLocation> Locations,
        string TimezoneId, TimeZoneInfo Zone, DateOnly Today);

    private async Task<Reference> ReferenceAsync(CancellationToken ct)
    {
        var all = await store.AnalyticsLocationsAsync(ct);
        var active = all.Where(l => l.IsActive).OrderBy(l => l.Name).ThenBy(l => l.Id).ToList();
        var id = active.Count == 0 ? "UTC" : active[0].Timezone;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        return new Reference(all, all, id, zone, LocalDate(clock.GetUtcNow(), zone));
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // =====================================================================================================================
    // Мережа
    // =====================================================================================================================

    private sealed record Totals(int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue, decimal AverageCheck,
        decimal CancellationRate, int NewClients);

    private async Task<Totals> TotalsAsync(DateRange r, Reference re, CancellationToken ct)
    {
        var rows = await store.DailyAsync(r.From, r.To, null, ct);
        int Count(string s) => rows.Where(x => x.Status == s).Sum(x => x.Count);
        var total = rows.Sum(x => x.Count);
        var completed = Count("completed");
        var cancelled = Count("cancelled");
        var revenue = rows.Where(x => x.Status == "completed").Sum(x => x.Revenue);
        var newClients = await store.NewClientsAsync(
            SlotCalculator.ToOffset(r.From, TimeOnly.MinValue, re.Zone).ToUniversalTime(),
            SlotCalculator.ToOffset(r.To.AddDays(1), TimeOnly.MinValue, re.Zone).ToUniversalTime(), ct);
        return new Totals(total, completed, cancelled, Count("no_show"), revenue,
            completed == 0 ? 0m : Math.Round(revenue / completed, 2),
            total == 0 ? 0m : Math.Round((decimal)cancelled / total, 4), newClients);
    }

    // =====================================================================================================================
    // Заклади й завантаження (§19.4)
    // =====================================================================================================================

    private sealed record LocationStats(int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue, int Busy, int Avail)
    {
        public static readonly LocationStats Empty = new(0, 0, 0, 0, 0m, 0, 0);
    }

    /// <summary>load = avail == 0 ? null : round(min(busy / avail, 1), 4).</summary>
    public static decimal? LoadOf(int busyMinutes, int availableMinutes) =>
        availableMinutes <= 0 ? null : Math.Round(Math.Min((decimal)busyMinutes / availableMinutes, 1m), 4);

    private sealed record AvailabilityInputs(IReadOnlyDictionary<Guid, AnalyticsLocation> Locations, IReadOnlyList<AnalyticsLink> Links,
        ILookup<Guid, AbsenceSpan> ClosuresByLocation, ILookup<Guid, AbsenceSpan> AbsencesBySpecialist);

    private async Task<AvailabilityInputs> LoadAvailabilityInputsAsync(DateOnly from, DateOnly to, Guid? locationId, Reference re, CancellationToken ct)
    {
        var locations = re.AllLocations;
        var links = await store.LinksAsync(locationId, ct);
        var closures = await store.ClosuresAsync(from, to, locationId, ct);
        var absences = await store.ApprovedAbsencesAsync(from, to, ct);
        return new AvailabilityInputs(locations.ToDictionary(l => l.Id), links,
            closures.ToLookup(c => c.OwnerId, c => new AbsenceSpan(c.From, c.To)),
            absences.ToLookup(a => a.OwnerId, a => new AbsenceSpan(a.From, a.To)));
    }

    /// <summary>
    /// avail(s,l) = Σ по локальних днях D: |графік майстра ∩ години закладу| (хвилини настінного часу). День дає 0 за правилами
    /// закладу/відсутності (спільні <see cref="SpecialistSchedule.IsClosedOn"/>/<see cref="SpecialistSchedule.IsAbsentOn"/>);
    /// майбутній день (після "сьогодні" в зоні закладу) неактивного профілю чи зв'язку теж 0.
    /// </summary>
    private Dictionary<(Guid Loc, Guid Spec), int> Availability(DateRange r, AvailabilityInputs inputs, Guid? onlyLocation)
    {
        var result = new Dictionary<(Guid, Guid), int>();
        var now = clock.GetUtcNow();
        var todays = new Dictionary<Guid, DateOnly>();
        var locationHours = new Dictionary<Guid, IReadOnlyList<(TimeOnly From, TimeOnly To)>[]?>();
        foreach (var link in inputs.Links)
        {
            if (onlyLocation is { } only && link.LocationId != only) continue;
            if (!inputs.Locations.TryGetValue(link.LocationId, out var loc)) continue;
            if (!todays.TryGetValue(loc.Id, out var today))
            {
                today = LocalDate(now, TimeZoneInfo.FindSystemTimeZoneById(loc.Timezone));
                todays[loc.Id] = today;
                locationHours[loc.Id] = string.IsNullOrWhiteSpace(loc.WorkingHoursJson) ? null : SlotCalculator.WeeklyIntervals(loc.WorkingHoursJson);
            }

            var weekly = SlotCalculator.WeeklyIntervals(link.WorkingHoursJson);
            var hours = locationHours[loc.Id];
            var schedule = new SpecialistSchedule(link.SpecialistId, loc.Timezone, link.WorkingHoursJson,
                inputs.AbsencesBySpecialist[link.SpecialistId].ToList(), null, loc.ClosedWeekdays,
                inputs.ClosuresByLocation[loc.Id].ToList(), loc.WorkingHoursJson);
            var active = link.SpecialistActive && link.LinkActive;
            var minutes = 0;
            for (var d = r.From; d <= r.To; d = d.AddDays(1))
            {
                if (d > today && !active) continue;
                var day = (int)d.DayOfWeek;
                if (weekly[day].Count == 0) continue;
                if (schedule.IsClosedOn(d) || schedule.IsAbsentOn(d)) continue;
                minutes += SlotCalculator.OverlapMinutes(weekly[day], hours?[day]);
            }
            result[(loc.Id, link.SpecialistId)] = minutes;
        }
        return result;
    }

    private async Task<Dictionary<Guid, LocationStats>> LocationPeriodAsync(
        DateRange r, Reference re, AvailabilityInputs inputs, Guid? locationId, CancellationToken ct)
    {
        var daily = await store.DailyAsync(r.From, r.To, locationId, ct);
        var busy = await store.BusyAsync(r.From, r.To, locationId, ct);
        var avail = Availability(r, inputs, locationId);

        var result = new Dictionary<Guid, LocationStats>();
        foreach (var g in daily.GroupBy(x => x.LocationId))
        {
            int Count(string s) => g.Where(x => x.Status == s).Sum(x => x.Count);
            result[g.Key] = new LocationStats(g.Sum(x => x.Count), Count("completed"), Count("cancelled"), Count("no_show"),
                g.Where(x => x.Status == "completed").Sum(x => x.Revenue), 0, 0);
        }
        foreach (var (loc, minutes) in busy.GroupBy(b => b.LocationId).Select(g => (g.Key, g.Sum(b => b.Minutes))))
            result[loc] = result.GetValueOrDefault(loc, LocationStats.Empty) with { Busy = minutes };
        foreach (var (loc, minutes) in avail.GroupBy(a => a.Key.Loc).Select(g => (g.Key, g.Sum(a => a.Value))))
            result[loc] = result.GetValueOrDefault(loc, LocationStats.Empty) with { Avail = minutes };
        return result;
    }
}
