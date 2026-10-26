using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyClients;
using BeautyCrm.Application.Features.BeautyStaff;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

public sealed partial class EfBeautyStore
{
    // ---------- клієнти ----------

    public async Task<IReadOnlyList<ClientDto>> ListAsync(string? search, int skip, int take, CancellationToken ct)
    {
        var q = db.Clients.AsNoTracking().Where(c => c.DeletedAt == null);
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            q = q.Where(c => EF.Functions.ILike(c.FullName, pattern) || (c.Phone != null && EF.Functions.ILike(c.Phone, pattern)));
        }
        var rows = await q.OrderBy(c => c.FullName).Skip(skip).Take(take).Select(c => new
        {
            Client = c,
            Visits = db.Appointments.Count(a => a.ClientId == c.Id && a.Status == AppointmentStatus.Completed),
            LastVisit = db.Appointments.Where(a => a.ClientId == c.Id && a.Status == AppointmentStatus.Completed)
                .Max(a => (DateTimeOffset?)a.StartsAt),
            Cancelled = db.Appointments.Count(a => a.ClientId == c.Id && a.Status == AppointmentStatus.Cancelled),
            CancelledByClient = db.Appointments.Count(a => a.ClientId == c.Id && a.Status == AppointmentStatus.Cancelled
                && a.CancelledByType == CancelledByTypes.Client),
        }).ToListAsync(ct);
        return rows.Select(r => ToDto(r.Client, r.Visits, r.LastVisit, r.Cancelled, r.CancelledByClient)).ToList();
    }

    public async Task<ClientDetailDto?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (c is null) return null;
        var history = await ProjectAsync(db.Appointments.Where(a => a.ClientId == id).OrderByDescending(a => a.StartsAt), ct);
        var completed = history.Where(h => h.Status == "completed").ToList();
        var cancelled = history.Where(h => h.Status == "cancelled").ToList(); // історія містить і скасовані (§16), дані не видаляються
        var notes = await db.ClientNotes.AsNoTracking().Where(n => n.ClientId == id).OrderByDescending(n => n.CreatedAt)
            .Select(n => new ClientNoteDto(n.Id, n.AuthorUserId, n.Body, n.CreatedAt)).ToListAsync(ct);
        return new ClientDetailDto(
            ToDto(c, completed.Count, completed.Count == 0 ? null : completed.Max(h => h.StartsAt),
                cancelled.Count, cancelled.Count(h => h.CancelledBy?.Type == CancelledByTypes.Client)), notes, history);
    }

    public Task<bool> PhoneExistsAsync(string phone, CancellationToken ct) =>
        db.Clients.AnyAsync(c => c.Phone == phone && c.DeletedAt == null, ct);

    public async Task<ClientDto> CreateAsync(CreateClientRequest r, CancellationToken ct)
    {
        var c = new Client
        {
            Id = Guid.NewGuid(), FullName = r.FullName, Phone = r.Phone, Email = r.Email, BirthDate = r.BirthDate,
            MarketingConsent = r.MarketingConsent,
        };
        db.Clients.Add(c);
        await db.SaveChangesAsync(ct);
        return ToDto(c, 0, null);
    }

    public async Task<ClientNoteDto?> AddNoteAsync(Guid clientId, Guid? authorUserId, string body, CancellationToken ct)
    {
        if (!await db.Clients.AnyAsync(c => c.Id == clientId && c.DeletedAt == null, ct)) return null;
        var n = new ClientNote { Id = Guid.NewGuid(), ClientId = clientId, AuthorUserId = authorUserId, Body = body };
        db.ClientNotes.Add(n);
        await db.SaveChangesAsync(ct);
        return new ClientNoteDto(n.Id, n.AuthorUserId, n.Body, n.CreatedAt);
    }

    private static ClientDto ToDto(Client c, int visits, DateTimeOffset? last, int cancelled = 0, int cancelledByClient = 0) =>
        new(c.Id, c.FullName, c.Phone, c.Email, c.BirthDate, c.MarketingConsent, c.Unsubscribed, visits, last, cancelled, cancelledByClient);

    // ---------- аналітика ----------

    public async Task<NetworkAnalytics> NetworkAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var groups = await db.Appointments.AsNoTracking().Where(a => a.StartsAt >= from && a.StartsAt < to)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Revenue = g.Sum(a => a.PriceFinal) }).ToListAsync(ct);
        int Count(AppointmentStatus s) => groups.FirstOrDefault(g => g.Status == s)?.Count ?? 0;
        var total = groups.Sum(g => g.Count);
        var completed = Count(AppointmentStatus.Completed);
        var revenue = groups.FirstOrDefault(g => g.Status == AppointmentStatus.Completed)?.Revenue ?? 0m;
        var newClients = await db.Clients.CountAsync(c => c.CreatedAt >= from && c.CreatedAt < to && c.DeletedAt == null, ct);
        return new NetworkAnalytics(from, to, total, completed, Count(AppointmentStatus.Cancelled), Count(AppointmentStatus.NoShow),
            revenue, completed == 0 ? 0m : Math.Round(revenue / completed, 2),
            total == 0 ? 0m : Math.Round((decimal)Count(AppointmentStatus.Cancelled) / total, 4), newClients);
    }

    public async Task<IReadOnlyList<LocationAnalytics>> LocationsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await db.Appointments.AsNoTracking().Where(a => a.StartsAt >= from && a.StartsAt < to)
            .GroupBy(a => a.LocationId)
            .Select(g => new
            {
                LocationId = g.Key, Total = g.Count(),
                Completed = g.Count(a => a.Status == AppointmentStatus.Completed),
                Cancelled = g.Count(a => a.Status == AppointmentStatus.Cancelled),
                Revenue = g.Sum(a => a.Status == AppointmentStatus.Completed ? a.PriceFinal : 0m),
            }).ToListAsync(ct);
        var names = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return rows.Select(r => new LocationAnalytics(r.LocationId, names.GetValueOrDefault(r.LocationId, "?"), r.Total, r.Completed, r.Cancelled, r.Revenue))
            .OrderByDescending(r => r.Revenue).ToList();
    }

    public async Task<IReadOnlyList<PromotionAnalytics>> PromotionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => a.StartsAt >= from && a.StartsAt < to && a.PromotionId != null && a.Status != AppointmentStatus.Cancelled)
            .GroupBy(a => a.PromotionId!.Value)
            .Select(g => new
            {
                PromotionId = g.Key, Uses = g.Count(), Discount = g.Sum(a => a.PriceOriginal - a.PriceFinal),
                Revenue = g.Sum(a => a.Status == AppointmentStatus.Completed ? a.PriceFinal : 0m),
            }).ToListAsync(ct);
        var names = await db.Promotions.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(r => new PromotionAnalytics(r.PromotionId, names.GetValueOrDefault(r.PromotionId, "?"), r.Uses, r.Discount, r.Revenue))
            .OrderByDescending(r => r.Uses).ToList();
    }

    // ---------- аналітика: датний режим (§19) ----------
    //
    // Запис належить локальному дню СВОГО закладу: (starts_at AT TIME ZONE locations.timezone)::date. Попередній фільтр за UTC-вікном
    // [from 00:00 - 14 год, to+1 00:00 + 14 год) покриває будь-яку зону й дозволяє використати індекси (tenant_id, location_id, starts_at).
    // Tenant ізольовано RLS (сесійний app.tenant_id), тож явного фільтра за tenant_id немає.

    internal sealed class DailyRowEntity
    {
        public Guid LocationId { get; set; }
        public DateOnly Date { get; set; }
        public string Status { get; set; } = "";
        public int Count { get; set; }
        public decimal Revenue { get; set; }
    }

    internal sealed class BusyRowEntity
    {
        public Guid LocationId { get; set; }
        public Guid SpecialistId { get; set; }
        public int Minutes { get; set; }
    }

    internal sealed class SpecialistStatEntity
    {
        public Guid SpecialistId { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
        public string Status { get; set; } = "";
        public int Count { get; set; }
        public decimal Revenue { get; set; }
    }

    internal sealed class ServiceStatEntity
    {
        public Guid ServiceId { get; set; }
        public string Name { get; set; } = "";
        public int Completed { get; set; }
        public decimal Revenue { get; set; }
    }

    internal sealed class PromotionStatEntity
    {
        public Guid PromotionId { get; set; }
        public string Name { get; set; } = "";
        public int Uses { get; set; }
        public decimal Discount { get; set; }
        public decimal Revenue { get; set; }
    }

    private static (DateTimeOffset From, DateTimeOffset To) UtcWindow(DateOnly from, DateOnly to) =>
        (new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddHours(-14),
         new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddHours(14));

    public async Task<IReadOnlyList<AnalyticsLocation>> AnalyticsLocationsAsync(CancellationToken ct) =>
        await db.Locations.AsNoTracking().OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new AnalyticsLocation(l.Id, l.Name, l.Timezone, l.IsActive, l.ClosedWeekdays, l.WorkingHours)).ToListAsync(ct);

    public async Task<IReadOnlyList<DailyRow>> DailyAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct)
    {
        var (wf, wt) = UtcWindow(from, to);
        var hasLocation = locationId is not null;
        var location = locationId ?? Guid.Empty;
        var rows = await db.Database.SqlQuery<DailyRowEntity>($"""
            SELECT a.location_id AS "location_id",
                   (a.starts_at AT TIME ZONE l.timezone)::date AS "date",
                   a.status AS "status",
                   count(*)::int AS "count",
                   COALESCE(sum(CASE WHEN a.status = 'completed' THEN a.price_final ELSE 0 END), 0) AS "revenue"
            FROM beauty_appointments a
            JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id
            WHERE a.starts_at >= {wf} AND a.starts_at < {wt}
              AND ({hasLocation} = false OR a.location_id = {location})
              AND (a.starts_at AT TIME ZONE l.timezone)::date BETWEEN {from} AND {to}
            GROUP BY a.location_id, (a.starts_at AT TIME ZONE l.timezone)::date, a.status
            """).ToListAsync(ct);
        return rows.Select(r => new DailyRow(r.LocationId, r.Date, r.Status, r.Count, r.Revenue)).ToList();
    }

    public async Task<IReadOnlyList<BusyRow>> BusyAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct)
    {
        var (wf, wt) = UtcWindow(from, to);
        var hasLocation = locationId is not null;
        var location = locationId ?? Guid.Empty;
        var rows = await db.Database.SqlQuery<BusyRowEntity>($"""
            SELECT a.location_id AS "location_id", a.specialist_id AS "specialist_id", COALESCE(sum(a.duration_minutes), 0)::int AS "minutes"
            FROM beauty_appointments a
            JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id
            WHERE a.starts_at >= {wf} AND a.starts_at < {wt}
              AND a.status IN ('pending', 'confirmed', 'completed')
              AND ({hasLocation} = false OR a.location_id = {location})
              AND (a.starts_at AT TIME ZONE l.timezone)::date BETWEEN {from} AND {to}
            GROUP BY a.location_id, a.specialist_id
            """).ToListAsync(ct);
        return rows.Select(r => new BusyRow(r.LocationId, r.SpecialistId, r.Minutes)).ToList();
    }

    public async Task<IReadOnlyList<AnalyticsLink>> LinksAsync(Guid? locationId, CancellationToken ct) =>
        await db.SpecialistLocations.AsNoTracking().Where(sl => locationId == null || sl.LocationId == locationId)
            .Select(sl => new AnalyticsLink(sl.SpecialistId, sl.Specialist!.FullName, sl.Specialist!.IsActive, sl.LocationId, sl.IsActive, sl.WorkingHours))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AnalyticsSpan>> ClosuresAsync(DateOnly from, DateOnly to, Guid? locationId, CancellationToken ct) =>
        await db.LocationClosures.AsNoTracking()
            .Where(c => c.DateTo >= from && c.DateFrom <= to && (locationId == null || c.LocationId == locationId))
            .Select(c => new AnalyticsSpan(c.LocationId, c.DateFrom, c.DateTo)).ToListAsync(ct);

    public async Task<IReadOnlyList<AnalyticsSpan>> ApprovedAbsencesAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.SpecialistAbsences.AsNoTracking()
            .Where(a => a.Status == AbsenceValues.Approved && a.DateTo >= from && a.DateFrom <= to)
            .Select(a => new AnalyticsSpan(a.SpecialistId, a.DateFrom, a.DateTo)).ToListAsync(ct);

    public Task<int> NewClientsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        return db.Clients.AsNoTracking().CountAsync(c => c.DeletedAt == null && c.CreatedAt >= from && c.CreatedAt < to, ct);
    }

    public async Task<IReadOnlyList<PromotionStatRow>> PromotionStatsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (wf, wt) = UtcWindow(from, to);
        var rows = await db.Database.SqlQuery<PromotionStatEntity>($"""
            SELECT a.promotion_id AS "promotion_id", p.name AS "name", count(*)::int AS "uses",
                   COALESCE(sum(a.price_original - a.price_final), 0) AS "discount",
                   COALESCE(sum(CASE WHEN a.status = 'completed' THEN a.price_final ELSE 0 END), 0) AS "revenue"
            FROM beauty_appointments a
            JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id
            JOIN beauty_promotions p ON p.tenant_id = a.tenant_id AND p.id = a.promotion_id
            WHERE a.starts_at >= {wf} AND a.starts_at < {wt}
              AND a.promotion_id IS NOT NULL AND a.status <> 'cancelled'
              AND (a.starts_at AT TIME ZONE l.timezone)::date BETWEEN {from} AND {to}
            GROUP BY a.promotion_id, p.name
            """).ToListAsync(ct);
        return rows.Select(r => new PromotionStatRow(r.PromotionId, r.Name, r.Uses, r.Discount, r.Revenue)).ToList();
    }

    public async Task<IReadOnlyList<SpecialistStatRow>> SpecialistStatsAsync(DateOnly from, DateOnly to, Guid locationId, CancellationToken ct)
    {
        var (wf, wt) = UtcWindow(from, to);
        var rows = await db.Database.SqlQuery<SpecialistStatEntity>($"""
            SELECT a.specialist_id AS "specialist_id", s.full_name AS "name", s.is_active AS "is_active", a.status AS "status",
                   count(*)::int AS "count",
                   COALESCE(sum(CASE WHEN a.status = 'completed' THEN a.price_final ELSE 0 END), 0) AS "revenue"
            FROM beauty_appointments a
            JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id
            JOIN beauty_specialists s ON s.tenant_id = a.tenant_id AND s.id = a.specialist_id
            WHERE a.starts_at >= {wf} AND a.starts_at < {wt} AND a.location_id = {locationId}
              AND (a.starts_at AT TIME ZONE l.timezone)::date BETWEEN {from} AND {to}
            GROUP BY a.specialist_id, s.full_name, s.is_active, a.status
            """).ToListAsync(ct);
        return rows.Select(r => new SpecialistStatRow(r.SpecialistId, r.Name, r.IsActive, r.Status, r.Count, r.Revenue)).ToList();
    }

    public async Task<IReadOnlyList<ServiceStatRow>> TopServicesAsync(DateOnly from, DateOnly to, Guid locationId, int take, CancellationToken ct)
    {
        var (wf, wt) = UtcWindow(from, to);
        var rows = await db.Database.SqlQuery<ServiceStatEntity>($"""
            SELECT a.service_id AS "service_id", sv.name AS "name", count(*)::int AS "completed", COALESCE(sum(a.price_final), 0) AS "revenue"
            FROM beauty_appointments a
            JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id
            JOIN beauty_services sv ON sv.tenant_id = a.tenant_id AND sv.id = a.service_id
            WHERE a.starts_at >= {wf} AND a.starts_at < {wt} AND a.location_id = {locationId} AND a.status = 'completed'
              AND (a.starts_at AT TIME ZONE l.timezone)::date BETWEEN {from} AND {to}
            GROUP BY a.service_id, sv.name


            """).ToListAsync(ct);
        return rows.Select(r => new ServiceStatRow(r.ServiceId, r.Name, r.Completed, r.Revenue))
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.ServiceId).Take(take).ToList();
    }
}
