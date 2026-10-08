using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyClients;
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
}
