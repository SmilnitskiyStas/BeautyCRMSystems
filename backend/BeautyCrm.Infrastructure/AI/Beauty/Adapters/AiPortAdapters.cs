using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.EntityFrameworkCore;
using Cat = BeautyCrm.Application.Features.BeautyCatalog;
using Ent = BeautyCrm.Infrastructure.Data.Entities;

namespace BeautyCrm.Infrastructure.AI.Beauty.Adapters;

// Реалізації портів AI (AiPorts.cs) поверх сервісів Application: AI не ходить у БД напряму для запису/слотів,
// а викликає ті самі сервіси, що й API (однакові правила перетину, цін, нагадувань, скасування).

public sealed class FindFreeSlotsAdapter(BookingService booking, BeautyDbContext db, TenantContext tenant) : IFindFreeSlotsPort
{
    public async Task<IReadOnlyList<SlotDto>> FindAsync(Guid tenantId, Guid serviceId, Guid? staffId, DateOnly date, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var locations = await LocationsOf(db, staffId, ct);
        var slots = new List<SlotDto>();
        foreach (var loc in locations)
        {
            var r = await booking.GetSlotsAsync(loc, staffId, serviceId, date, ct);
            if (r.IsOk) slots.AddRange(r.Value!.Select(s => new SlotDto(s.SpecialistId, s.StartsAt, s.EndsAt)));
        }
        return slots.OrderBy(s => s.Start).ToList();
    }

    internal static async Task<List<Guid>> LocationsOf(BeautyDbContext db, Guid? staffId, CancellationToken ct) =>
        await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.IsActive && (staffId == null || sl.SpecialistId == staffId))
            .Select(sl => sl.LocationId).Distinct().ToListAsync(ct);
}

public sealed class CreateAppointmentAdapter(
    BookingService booking, CancellationService cancellation, BeautyDbContext db, TenantContext tenant) : ICreateAppointmentPort
{
    public async Task<AppointmentCreated> CreateAsync(
        Guid tenantId, Guid clientId, Guid serviceId, Guid staffId, DateTimeOffset start, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        // Порт не знає закладу: пробуємо заклади майстра, доки час не підійде за графіком.
        string? lastError = null;
        foreach (var loc in await db.SpecialistLocations.AsNoTracking()
                     .Where(sl => sl.IsActive && sl.SpecialistId == staffId).Select(sl => sl.LocationId).ToListAsync(ct))
        {
            var r = await booking.CreateAsync(new CreateAppointmentRequest(
                loc, staffId, serviceId, start, new ClientInput(clientId, null, null, null), "none", "cash", "online"), ct);
            if (r.IsOk) return new AppointmentCreated(r.Value!.Id);
            lastError = r.Error!.Code;
            if (r.Error.Code is not ("outside_working_hours" or "specialist_not_at_location")) break;
        }
        throw new InvalidOperationException(lastError ?? "specialist_not_at_location");
    }

    public async Task CancelAsync(Guid tenantId, Guid appointmentId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var r = await cancellation.CancelAsync(appointmentId, ct);
        if (!r.IsOk) throw new InvalidOperationException(r.Error!.Code);
    }
}

public sealed class GetPricesAdapter(Cat.CatalogService catalog, TenantContext tenant) : IGetPricesPort
{
    public async Task<IReadOnlyList<PriceDto>> GetAsync(Guid tenantId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        return (await catalog.ListServicesAsync(false, ct)).Where(s => s.NetworkPrice is not null)
            .Select(s => new PriceDto(s.Id, s.Name, s.NetworkPrice!.Value, s.DurationMinutes)).ToList();
    }
}

public sealed class GetActivePromotionsAdapter(Cat.PromotionService promotions, TenantContext tenant, TimeProvider clock) : IGetActivePromotionsPort
{
    public async Task<IReadOnlyList<PromotionDto>> GetAsync(Guid tenantId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var now = clock.GetUtcNow();
        // Порт оперує відсотками: фіксовані знижки не віддаємо.
        return (await promotions.ListAsync(ct))
            .Where(p => p.IsActive && p.DiscountType == "percent" && (p.StartsAt is null || p.StartsAt <= now) && (p.EndsAt is null || now < p.EndsAt))
            .Select(p => new PromotionDto(p.Id, p.Name, p.DiscountValue, p.EndsAt)).ToList();
    }
}

public sealed class GetClientContextAdapter(BeautyDbContext db, TenantContext tenant, TimeProvider clock) : IGetClientContextPort
{
    public async Task<ClientContextDto?> GetAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var c = await db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId && x.DeletedAt == null, ct);
        if (c is null) return null;
        var now = clock.GetUtcNow();
        var visits = await db.Appointments.CountAsync(a => a.ClientId == clientId && a.Status == AppointmentStatus.Completed, ct);
        var next = await db.Appointments.Where(a => a.ClientId == clientId && a.StartsAt > now
                && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .MinAsync(a => (DateTimeOffset?)a.StartsAt, ct);
        var notes = await db.ClientNotes.AsNoTracking().Where(n => n.ClientId == clientId)
            .OrderByDescending(n => n.CreatedAt).Take(5).Select(n => n.Body).ToListAsync(ct);
        return new ClientContextDto(c.Id, c.FullName, notes.Count == 0 ? null : string.Join(" | ", notes), visits, next,
            c.MarketingConsent, c.Unsubscribed);
    }
}

public sealed class DraftReplyAdapter(BeautyDbContext db, TenantContext tenant, TimeProvider clock, OutboundDispatcher dispatcher) : IDraftReplyPort
{
    public async Task<Guid> SaveDraftAsync(Guid tenantId, Guid clientId, string text, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var conversationId = await db.Conversations.Where(c => c.ClientId == clientId)
            .OrderByDescending(c => c.LastMessageAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("client_has_no_conversation");
        var msg = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, Direction = MessageDirection.Outbound,
            SenderType = MessageSenderType.Ai, Body = text, Status = "draft", SentAt = clock.GetUtcNow(),
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg.Id;
    }

    public async Task SendAsync(Guid tenantId, Guid draftId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        // Атомарний claim draft -> sending (а не pending): воркер бере лише 'pending', тож те саме повідомлення не піде двічі
        // (inline тут і з воркера). Повторний SendAsync того ж чернеткового id програє claim -> draft_not_found.
        var claimed = await db.Messages.Where(m => m.Id == draftId && m.Status == "draft")
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "sending"), ct);
        if (claimed != 1) throw new InvalidOperationException("draft_not_found");
        OutboxResult outcome;
        try
        {
            outcome = await dispatcher.DispatchAsync(tenantId, draftId, ct);
        }
        catch
        {
            // Збій до результату відправки: віддаємо воркеру на повтор (pending), а не лишаємо у sending назавжди.
            await db.Messages.Where(m => m.Id == draftId && m.Status == "sending")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending"), CancellationToken.None);
            throw;
        }
        if (outcome is OutboxResult.Failed or OutboxResult.NotFound) throw new InvalidOperationException($"send_{outcome}");
    }
}

public sealed class SuggestPromotionAdapter(Cat.PromotionService promotions, TenantContext tenant) : ISuggestPromotionPort
{
    /// <summary>Пропозиція = неактивна акція (is_active = false); менеджер активує її в адмінці.</summary>
    public async Task<PromotionProposal> ProposeAsync(Guid tenantId, string goal, decimal discountPercent, string text, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var name = goal.Length > 200 ? goal[..200] : goal;
        var r = await promotions.CreateAsync(
            new Cat.UpsertPromotionRequest(name, text, "percent", discountPercent, null, null, false, [], []), ct);
        return r.IsOk ? new PromotionProposal(r.Value!.Id) : throw new InvalidOperationException(r.Error!.Code);
    }
}

public sealed class BuildAudienceAdapter(BeautyDbContext db, TenantContext tenant, TimeProvider clock) : IBuildAudiencePort
{
    private const int MaxAudience = 1000;

    /// <summary>Сегменти: "all" або "inactive_{N}d" (без завершених візитів за N днів). Лише клієнти зі згодою й без відписки.</summary>
    public async Task<IReadOnlyList<AudienceMemberDto>> BuildAsync(Guid tenantId, string segment, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var q = db.Clients.AsNoTracking().Where(c => c.DeletedAt == null && c.MarketingConsent && !c.Unsubscribed);
        if (segment.StartsWith("inactive_", StringComparison.OrdinalIgnoreCase) && segment.EndsWith('d')
            && int.TryParse(segment["inactive_".Length..^1], out var days) && days > 0)
        {
            var cutoff = clock.GetUtcNow().AddDays(-days);
            q = q.Where(c => !db.Appointments.Any(a => a.ClientId == c.Id && a.Status == AppointmentStatus.Completed && a.StartsAt >= cutoff));
        }
        var rows = await q.OrderBy(c => c.FullName).Take(MaxAudience).Select(c => new
        {
            c.Id,
            Channel = db.Conversations.Where(v => v.ClientId == c.Id).OrderByDescending(v => v.LastMessageAt)
                .Select(v => (ChannelType?)v.Channel!.Type).FirstOrDefault(),
        }).ToListAsync(ct);
        return rows.Select(r => new AudienceMemberDto(r.Id, r.Channel is { } t ? EnumText<ChannelType>.ToDb(t) : "none", true, false)).ToList();
    }
}
