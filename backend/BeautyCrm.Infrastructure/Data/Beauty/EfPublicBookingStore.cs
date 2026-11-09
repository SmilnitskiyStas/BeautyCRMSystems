using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Application.Features.BeautyStaff;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// Порт публічного запису над EF. Tenant задає RLS (app.tenant_id встановлюється з slug до першого запиту);
/// жодної нової політики не потрібно. Проєкції навмисно вузькі: нічого, окрім публічних полів каталогу.
/// </summary>
public sealed class EfPublicBookingStore(BeautyDbContext db) : IPublicBookingStore
{
    public async Task<IReadOnlyList<PublicLocationDto>> ListActiveLocationsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name)
            .Select(l => new { l.Id, l.Name, l.Address, l.Phone, l.Timezone, l.ClosedWeekdays, l.WorkingHours }).ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Закриття на майбутні 366 днів у зоні кожного закладу; причина (reason) навмисно не вибирається.
        var since = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-2));
        var until = DateOnly.FromDateTime(now.UtcDateTime.AddDays(369));
        var ids = rows.Select(r => r.Id).ToList();
        var closures = (await db.LocationClosures.AsNoTracking()
                .Where(c => ids.Contains(c.LocationId) && c.DateTo >= since && c.DateFrom <= until)
                .Select(c => new { c.LocationId, c.DateFrom, c.DateTo }).ToListAsync(ct))
            .ToLookup(c => c.LocationId);

        return rows.Select(r =>
        {
            var today = SlotCalculator.LocalDate(now, r.Timezone);
            var last = today.AddDays(366);
            var list = closures[r.Id].Where(c => c.DateTo >= today && c.DateFrom <= last).OrderBy(c => c.DateFrom)
                .Select(c => new PublicClosureDto(c.DateFrom, c.DateTo)).ToList();
            return new PublicLocationDto(r.Id, r.Name, r.Address, r.Phone, r.Timezone, r.ClosedWeekdays, list,
                WorkingHoursValidator.Parse(r.WorkingHours));
        }).ToList();
    }

    /// <summary>
    /// Придатні до запису майстри закладу: активні, заклад-зв'язок активний, у графіку є робочий інтервал
    /// і призначена хоча б одна активна послуга (TASK-691). Інших публічно не показуємо.
    /// </summary>
    private async Task<List<Guid>> EligibleSpecialistIdsAsync(Guid locationId, CancellationToken ct)
    {
        var rows = await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.LocationId == locationId && sl.IsActive && sl.Specialist!.IsActive
                         && db.SpecialistServices.Any(x => x.SpecialistId == sl.SpecialistId && x.Service!.IsActive))
            .Select(sl => new { sl.SpecialistId, sl.WorkingHours }).ToListAsync(ct);
        return rows.Where(r => SlotCalculator.HasWorkingHours(r.WorkingHours)).Select(r => r.SpecialistId).Distinct().ToList();
    }

    public async Task<PublicReviewsPage?> ListReviewsAsync(Guid specialistId, int limit, int offset, CancellationToken ct)
    {
        // RLS обмежує tenant-ом; неіснуючий, чужий або неактивний майстер -> null (404).
        if (!await db.Specialists.AsNoTracking().AnyAsync(s => s.Id == specialistId && s.IsActive, ct)) return null;
        var q = db.SpecialistReviews.AsNoTracking().Where(r => r.SpecialistId == specialistId && r.IsPublished);
        var total = await q.CountAsync(ct);
        // Проєкція лише публічних полів: client_id/appointment_id у відповідь не потрапляють.
        var items = await q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(offset).Take(limit)
            .Select(r => new PublicReviewDto(r.Id, r.AuthorName, r.Rating, r.Text, r.CreatedAt)).ToListAsync(ct);
        return new PublicReviewsPage(total, items);
    }

    public async Task<IReadOnlyList<PublicSpecialistRow>> ListSpecialistsAsync(Guid locationId, Guid? serviceId, CancellationToken ct)
    {
        var ids = await EligibleSpecialistIdsAsync(locationId, ct);
        if (serviceId is { } sid)
        {
            var offering = await db.SpecialistServices.AsNoTracking().Where(x => x.ServiceId == sid && ids.Contains(x.SpecialistId))
                .Select(x => x.SpecialistId).ToListAsync(ct);
            ids = offering;
        }
        var specialists = await db.Specialists.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.FullName)
            .Select(s => new { s.Id, s.FullName, Title = s.Position ?? s.Title, s.PhotoUrl, s.PracticeStartYear, s.Bio })
            .ToListAsync(ct);
        if (specialists.Count == 0) return [];
        // Один агрегат за всіма майстрами (без N+1); лише опубліковані відгуки.
        var stats = (await db.SpecialistReviews.AsNoTracking().Where(r => r.IsPublished && ids.Contains(r.SpecialistId))
                .GroupBy(r => r.SpecialistId)
                .Select(g => new { Id = g.Key, Count = g.Count(), Avg = g.Average(r => (double)r.Rating) }).ToListAsync(ct))
            .ToDictionary(x => x.Id);
        return specialists.Select(s => new PublicSpecialistRow(
            s.Id, s.FullName, s.Title, s.PhotoUrl, s.PracticeStartYear, s.Bio,
            stats.TryGetValue(s.Id, out var st) ? st.Avg : null, stats.TryGetValue(s.Id, out var st2) ? st2.Count : 0)).ToList();
    }

    public async Task<IReadOnlyList<PublicServiceBase>> ListServicesAsync(Guid locationId, Guid? specialistId, CancellationToken ct)
    {
        var ids = await EligibleSpecialistIdsAsync(locationId, ct);
        if (specialistId is { } spid) ids = ids.Where(i => i == spid).ToList();
        var offered = await db.SpecialistServices.AsNoTracking().Where(x => ids.Contains(x.SpecialistId))
            .Select(x => x.ServiceId).Distinct().ToListAsync(ct);
        var services = await db.Services.AsNoTracking().Where(s => s.IsActive && offered.Contains(s.Id))
            .OrderBy(s => s.Category).ThenBy(s => s.Name).ToListAsync(ct);
        var prices = await db.ServicePrices.AsNoTracking()
            .Where(p => p.LocationId == locationId || p.LocationId == null)
            .Select(p => new { p.ServiceId, p.LocationId, p.Price }).ToListAsync(ct);
        var result = new List<PublicServiceBase>();
        foreach (var s in services)
        {
            var mine = prices.Where(p => p.ServiceId == s.Id).ToList();
            var price = mine.FirstOrDefault(p => p.LocationId == locationId)?.Price ?? mine.FirstOrDefault(p => p.LocationId == null)?.Price;
            if (price is not null)
                result.Add(new PublicServiceBase(s.Id, s.Name, s.Description, s.Category, s.DurationMinutes, price.Value));
        }
        return result;
    }

    public async Task<Guid?> FindAppointmentIdByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        await db.Appointments.AsNoTracking().Where(a => a.PublicTokenHash == tokenHash)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);

    public async Task<IdempotencyHit?> FindByIdempotencyKeyAsync(string keyHash, CancellationToken ct)
    {
        var row = await db.Appointments.AsNoTracking().Where(a => a.IdempotencyKeyHash == keyHash)
            .Select(a => new { a.Id, a.IdempotencyRequestHash }).FirstOrDefaultAsync(ct);
        return row is null ? null : new IdempotencyHit(row.Id, row.IdempotencyRequestHash ?? string.Empty);
    }

    public Task<int> CountRecentOnlineByPhoneAsync(string phone, DateTimeOffset since, CancellationToken ct) =>
        db.Appointments.AsNoTracking().CountAsync(a => a.Source == AppointmentSource.Online && a.CreatedAt > since
            && a.Client!.Phone == phone && a.Client.DeletedAt == null, ct);

    public Task<int> CountRecentOnlineByLocationAsync(Guid locationId, DateTimeOffset since, CancellationToken ct) =>
        db.Appointments.AsNoTracking().CountAsync(a => a.Source == AppointmentSource.Online && a.CreatedAt > since
            && a.LocationId == locationId, ct);

    public Task<int> CountActiveOnlineByPhoneAsync(string phone, DateTimeOffset now, CancellationToken ct) =>
        db.Appointments.AsNoTracking().CountAsync(a =>
            a.Source == AppointmentSource.Online && a.StartsAt > now
            && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed)
            && a.Client!.Phone == phone && a.Client.DeletedAt == null, ct);

    public Task<string?> GetClientEmailAsync(Guid clientId, CancellationToken ct) =>
        db.Clients.AsNoTracking().Where(c => c.Id == clientId && c.DeletedAt == null).Select(c => c.Email).FirstOrDefaultAsync(ct);

    public Task<string?> GetLocationAddressAsync(Guid locationId, CancellationToken ct) =>
        db.Locations.AsNoTracking().Where(l => l.Id == locationId).Select(l => l.Address).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Контракт для worker: рядок beauty_messages (outbound, system, status 'pending', idempotency_key) у розмові email-каналу tenant-а;
    /// beauty_conversations.external_chat_id = адреса. Канал/розмова створюються ліниво; advisory-лок tenant-а серіалізує
    /// find-or-create (немає unique-індексу на канал), повтор того самого ключа ідемпотентний. last_message_at лишається NULL,
    /// тож месенджер-розмова клієнта (з last_message_at) має пріоритет у CLIENT_SQL воркера, а email — запасний канал.
    /// </summary>
    public async Task<bool> QueueEmailMessageAsync(Guid clientId, string email, string idempotencyKey, string body, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended('beauty_email_outbox:' || current_setting('app.tenant_id', true), 0))", ct);
            if (await db.Messages.AnyAsync(m => m.IdempotencyKey == idempotencyKey, ct)) return false;

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.Type == ChannelType.Email && c.IsActive, ct);
            if (channel is null)
            {
                channel = new Channel { Id = Guid.NewGuid(), Type = ChannelType.Email, Name = "Email", IsActive = true };
                db.Channels.Add(channel);
            }
            var conv = await db.Conversations.FirstOrDefaultAsync(c => c.ChannelId == channel.Id && c.ExternalChatId == email, ct);
            if (conv is null)
            {
                conv = new Conversation { Id = Guid.NewGuid(), ChannelId = channel.Id, ClientId = clientId, ExternalChatId = email };
                db.Conversations.Add(conv);
            }
            db.Messages.Add(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System,
                Body = body, Status = "pending", IdempotencyKey = idempotencyKey,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}

public static class PublicBookingDataExtensions
{
    /// <summary>Порт даних публічного запису. Потребує AddBeautyData.</summary>
    public static IServiceCollection AddBeautyPublicBookingData(this IServiceCollection services) =>
        services.AddScoped<IPublicBookingStore, EfPublicBookingStore>();
}
