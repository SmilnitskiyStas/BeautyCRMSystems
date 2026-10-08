using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyStaff;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IStaffStore (TASK-691): профілі працівників, призначені послуги, графік, відсутності. Усе під RLS tenant (app.tenant_id).
// Note відсутності чутлива: у цьому файлі її ніде не логуємо й не вносимо в повідомлення винятків.
public sealed partial class EfBeautyStore : IStaffStore
{
    // ---------- профілі ----------

    public async Task<IReadOnlyList<StaffSpecialistRecord>> ListSpecialistsAsync(CancellationToken ct) =>
        await LoadStaffAsync(null, ct);

    public async Task<StaffSpecialistRecord?> GetSpecialistAsync(Guid id, CancellationToken ct) =>
        (await LoadStaffAsync(id, ct)).FirstOrDefault();

    private async Task<IReadOnlyList<StaffSpecialistRecord>> LoadStaffAsync(Guid? id, CancellationToken ct)
    {
        var specialists = await db.Specialists.AsNoTracking().Where(s => id == null || s.Id == id)
            .OrderBy(s => s.FullName).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.FullName, s.Phone, s.Position, s.PhotoUrl, s.IsActive }).ToListAsync(ct);
        if (specialists.Count == 0) return [];

        var roles = (await db.Users.AsNoTracking().Where(u => u.SpecialistId != null && (id == null || u.SpecialistId == id))
                .Select(u => new { SpecialistId = u.SpecialistId!.Value, u.Role }).ToListAsync(ct))
            .GroupBy(u => u.SpecialistId).ToDictionary(g => g.Key, g => g.First().Role);
        var services = (await db.SpecialistServices.AsNoTracking().Where(x => id == null || x.SpecialistId == id)
                .Select(x => new { x.SpecialistId, x.ServiceId, x.Service!.Name }).ToListAsync(ct))
            .GroupBy(x => x.SpecialistId).ToDictionary(g => g.Key,
                g => (IReadOnlyList<SpecialistServiceRef>)g.OrderBy(x => x.Name).Select(x => new SpecialistServiceRef(x.ServiceId, x.Name)).ToList());
        var locations = (await db.SpecialistLocations.AsNoTracking().Where(x => id == null || x.SpecialistId == id)
                .Select(x => new { x.SpecialistId, x.LocationId, LocationName = x.Location!.Name, x.IsActive, x.WorkingHours }).ToListAsync(ct))
            .GroupBy(x => x.SpecialistId).ToDictionary(g => g.Key,
                g => (IReadOnlyList<StaffLocationRecord>)g.OrderBy(x => x.LocationName)
                    .Select(x => new StaffLocationRecord(x.LocationId, x.LocationName, x.IsActive, x.WorkingHours)).ToList());

        return specialists.Select(s => new StaffSpecialistRecord(
            s.Id, s.FullName, s.Phone, s.Position, s.PhotoUrl, s.IsActive, roles.GetValueOrDefault(s.Id),
            services.GetValueOrDefault(s.Id) ?? [], locations.GetValueOrDefault(s.Id) ?? [])).ToList();
    }

    public async Task<SpecialistLink?> GetSpecialistLinkAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Specialists.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.IsActive }).FirstOrDefaultAsync(ct);
        if (s is null) return null;
        var role = await db.Users.AsNoTracking().Where(u => u.SpecialistId == id).Select(u => u.Role).FirstOrDefaultAsync(ct);
        return new SpecialistLink(s.Id, s.IsActive, role);
    }

    public async Task<IReadOnlySet<Guid>> ExistingServiceIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? new HashSet<Guid>()
            : (await db.Services.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();

    public async Task<IReadOnlySet<Guid>> ExistingLocationIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? new HashSet<Guid>()
            : (await db.Locations.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct)).ToHashSet();

    public async Task<Guid> CreateSpecialistAsync(NewSpecialist n, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        db.Specialists.Add(new Specialist { Id = id, FullName = n.Name, Phone = n.Phone, Position = n.Position, IsActive = true });
        foreach (var locationId in n.LocationIds)
            db.SpecialistLocations.Add(new SpecialistLocation
            {
                Id = Guid.NewGuid(), SpecialistId = id, LocationId = locationId, WorkingHours = n.WorkingHoursJson, IsActive = true,
            });
        foreach (var serviceId in n.ServiceIds)
            db.SpecialistServices.Add(new SpecialistServiceLink { SpecialistId = id, ServiceId = serviceId });
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task UpdateSpecialistAsync(
        Guid id, string name, string? phone, string? position, bool? isActive, DateTimeOffset now, CancellationToken ct)
    {
        // Одна транзакція під advisory-lock майстра: профіль + (при деактивації) користувач, refresh-токени, запрошення.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SpecialistLock.AcquireAsync(db, id, ct);
        if (isActive == false) await SpecialistLock.AcquireForInvitesAsync(db, id, ct); // серіалізує з видачею запрошень
        var s = await db.Specialists.FirstAsync(x => x.Id == id, ct);
        s.FullName = name;
        s.Phone = phone;
        s.Position = position;
        if (isActive is { } active) s.IsActive = active;
        await db.SaveChangesAsync(ct);

        if (isActive == false)
        {
            var at = now.ToUniversalTime();
            // Порядок важливий (READ COMMITTED): спершу запрошення — паралельне прийняття, що вже тримає рядок, дочекається
            // коміту/відхилення, а наступний UPDATE користувачів побачить щойно створеного користувача.
            await db.Invites.Where(i => i.SpecialistId == id && i.AcceptedAt == null && i.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(i => i.RevokedAt, at), ct);
            // owner не вимикається разом із профілем (інакше власник міг би замкнути себе); ним керує /api/users/{id}/status.
            await db.Users.Where(u => u.SpecialistId == id && u.Role != Roles.Owner && u.IsActive)
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false).SetProperty(u => u.UpdatedAt, at), ct);
            await db.RefreshTokens
                .Where(t => t.RevokedAt == null && db.Users.Any(u => u.Id == t.UserId && u.SpecialistId == id && u.Role != Roles.Owner))
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, at), ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task ReplaceServicesAsync(Guid specialistId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct)
    {
        var wanted = serviceIds.ToHashSet();
        var current = await db.SpecialistServices.Where(x => x.SpecialistId == specialistId).ToListAsync(ct);
        db.SpecialistServices.RemoveRange(current.Where(x => !wanted.Contains(x.ServiceId)));
        var have = current.Select(x => x.ServiceId).ToHashSet();
        foreach (var serviceId in wanted.Where(w => !have.Contains(w)))
            db.SpecialistServices.Add(new SpecialistServiceLink { SpecialistId = specialistId, ServiceId = serviceId });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetScheduleAsync(Guid specialistId, Guid locationId, string workingHoursJson, CancellationToken ct)
    {
        var sl = await db.SpecialistLocations.FirstOrDefaultAsync(x => x.SpecialistId == specialistId && x.LocationId == locationId, ct);
        if (sl is null)
            db.SpecialistLocations.Add(new SpecialistLocation
            {
                Id = Guid.NewGuid(), SpecialistId = specialistId, LocationId = locationId, WorkingHours = workingHoursJson, IsActive = true,
            });
        else
        {
            sl.WorkingHours = workingHoursJson;
            sl.IsActive = true;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<LocationAssignOutcome> AssignLocationAsync(Guid specialistId, Guid locationId, string? workingHoursJson, CancellationToken ct)
    {
        var sl = await db.SpecialistLocations.FirstOrDefaultAsync(x => x.SpecialistId == specialistId && x.LocationId == locationId, ct);
        if (sl is { IsActive: true }) return LocationAssignOutcome.AlreadyAssigned;
        if (sl is null)
            db.SpecialistLocations.Add(new SpecialistLocation
            {
                Id = Guid.NewGuid(), SpecialistId = specialistId, LocationId = locationId, WorkingHours = workingHoursJson, IsActive = true,
            });
        else
        {
            sl.IsActive = true; // повторна активація: збережений графік лишається, якщо новий не переданий
            if (workingHoursJson is not null) sl.WorkingHours = workingHoursJson;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear(); // паралельне додавання того самого закладу
            return LocationAssignOutcome.AlreadyAssigned;
        }
        return LocationAssignOutcome.Assigned;
    }

    public async Task<LocationRemoveOutcome> RemoveLocationAsync(Guid specialistId, Guid locationId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var now = nowUtc.ToUniversalTime();
        // Умовний UPDATE: NOT EXISTS майбутніх активних записів перевіряється в тому ж операторі, що й деактивація.
        var changed = await db.SpecialistLocations
            .Where(x => x.SpecialistId == specialistId && x.LocationId == locationId && x.IsActive
                        && !db.Appointments.Any(a => a.SpecialistId == specialistId && a.LocationId == locationId && a.StartsAt > now
                                                     && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed)))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);
        if (changed == 1) return LocationRemoveOutcome.Removed;
        return await db.SpecialistLocations.AnyAsync(x => x.SpecialistId == specialistId && x.LocationId == locationId && x.IsActive, ct)
            ? LocationRemoveOutcome.HasFutureAppointments
            : LocationRemoveOutcome.NotAssigned;
    }

    // ---------- відсутності ----------

    private static AbsenceRecord ToRecord(SpecialistAbsence a) => new(
        a.Id, a.SpecialistId, a.Type, a.DateFrom, a.DateTo, a.Status, a.Note, a.RequestedByUserId, a.DecidedByUserId, a.DecidedAt);

    public async Task<AbsenceRecord?> GetAbsenceAsync(Guid id, CancellationToken ct) =>
        await db.SpecialistAbsences.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } a ? ToRecord(a) : null;

    public async Task<IReadOnlyList<AbsenceRecord>> ListAbsencesAsync(DateOnly from, DateOnly to, Guid? specialistId, CancellationToken ct) =>
        (await db.SpecialistAbsences.AsNoTracking()
            .Where(a => a.DateTo >= from && a.DateFrom <= to && (specialistId == null || a.SpecialistId == specialistId))
            .OrderBy(a => a.DateFrom).ThenBy(a => a.Id).ToListAsync(ct)).Select(ToRecord).ToList();

    public Task<bool> HasActiveAbsenceOverlapAsync(Guid specialistId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.SpecialistAbsences.AsNoTracking().AnyAsync(a => a.SpecialistId == specialistId
            && (a.Status == AbsenceValues.Requested || a.Status == AbsenceValues.Approved)
            && a.DateFrom <= to && a.DateTo >= from, ct);

    public async Task<AbsenceAddResult> AddAbsenceAsync(NewAbsence n, int? maxRequested, ConflictWindow? window, CancellationToken ct)
    {
        // Lock майстра серіалізує створення відсутності із записом/переносом: запис, доданий до коміту, потрапить у conflicts[],
        // а доданий після — побачить відсутність і буде відхилений (див. AddAppointmentAsync).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SpecialistLock.AcquireAsync(db, n.SpecialistId, ct);
        if (maxRequested is { } max && n.Status == AbsenceValues.Requested
            && await db.SpecialistAbsences.CountAsync(a => a.SpecialistId == n.SpecialistId && a.Status == AbsenceValues.Requested, ct) >= max)
            return new AbsenceAddResult(AbsenceAddOutcome.TooManyRequests, null);

        var entity = new SpecialistAbsence
        {
            Id = Guid.NewGuid(), SpecialistId = n.SpecialistId, Type = n.Type, DateFrom = n.DateFrom, DateTo = n.DateTo,
            Status = n.Status, Note = n.Note, RequestedByUserId = n.RequestedByUserId, DecidedByUserId = n.DecidedByUserId,
            DecidedAt = n.DecidedAt,
        };
        db.SpecialistAbsences.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: ExclusionViolation })
        {
            db.ChangeTracker.Clear(); // паралельне створення перетнулося: exclusion constraint
            return new AbsenceAddResult(AbsenceAddOutcome.Overlap, null);
        }
        var record = ToRecord(entity) with { Candidates = window is null ? null : await ListActiveAppointmentsAsync(n.SpecialistId, window, ct) };
        await tx.CommitAsync(ct);
        return new AbsenceAddResult(AbsenceAddOutcome.Ok, record);
    }

    public async Task<AbsenceRecord?> TransitionAbsenceAsync(
        Guid id, IReadOnlyCollection<string> fromStatuses, string toStatus, Guid? by, DateTimeOffset? at, ConflictWindow? window,
        CancellationToken ct)
    {
        var specialistId = await db.SpecialistAbsences.AsNoTracking().Where(a => a.Id == id).Select(a => (Guid?)a.SpecialistId).FirstOrDefaultAsync(ct);
        if (specialistId is null) return null;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SpecialistLock.AcquireAsync(db, specialistId.Value, ct);
        var from = fromStatuses.ToArray();
        var atUtc = at?.ToUniversalTime();
        var query = db.SpecialistAbsences.Where(a => a.Id == id && from.Contains(a.Status));
        var changed = toStatus switch
        {
            AbsenceValues.Approved or AbsenceValues.Rejected => await query.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, toStatus)
                .SetProperty(a => a.DecidedByUserId, by)
                .SetProperty(a => a.DecidedAt, atUtc), ct),
            AbsenceValues.Cancelled => await query.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, toStatus)
                .SetProperty(a => a.CancelledByUserId, by)
                .SetProperty(a => a.CancelledAt, atUtc), ct),
            _ => await query.ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, toStatus), ct),
        };
        if (changed == 0) return null;
        var record = await GetAbsenceAsync(id, ct);
        if (record is not null && window is not null)
            record = record with { Candidates = await ListActiveAppointmentsAsync(specialistId.Value, window, ct) };
        await tx.CommitAsync(ct);
        return record;
    }

    private async Task<IReadOnlyList<AppointmentCandidate>> ListActiveAppointmentsAsync(Guid specialistId, ConflictWindow window, CancellationToken ct)
    {
        var f = window.FromUtc.ToUniversalTime();
        var t = window.ToUtc.ToUniversalTime();
        return (await db.Appointments.AsNoTracking()
                .Where(a => a.SpecialistId == specialistId && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed)
                            && a.StartsAt >= f && a.StartsAt < t)
                .Select(a => new { a.Id, a.StartsAt, ServiceName = a.Service!.Name, Timezone = a.Location!.Timezone })
                .ToListAsync(ct))
            .Select(a => new AppointmentCandidate(a.Id, a.StartsAt, a.ServiceName, a.Timezone)).ToList();
    }
}
