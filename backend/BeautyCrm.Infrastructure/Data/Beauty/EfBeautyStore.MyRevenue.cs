using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

public sealed partial class EfBeautyStore : IMyRevenueStore, IClientScope
{
    internal sealed class MyRevenueRowEntity
    {
        public Guid LocationId { get; set; }
        public string Name { get; set; } = "";
        public int Completed { get; set; }
        public decimal Revenue { get; set; }
    }

    /// <summary>§30.9.7: Σ price_final completed записів власного specialist_id; день належить локальній даті свого закладу (§19.1).</summary>
    public async Task<IReadOnlyList<MyRevenueLocation>> ByLocationAsync(Guid specialistId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (wf, wt) = UtcWindow(from, to);
        var rows = await db.Database.SqlQuery<MyRevenueRowEntity>($"""
            SELECT a.location_id AS "location_id", l.name AS "name", count(*)::int AS "completed",
                   COALESCE(sum(a.price_final), 0) AS "revenue"
            FROM beauty_appointments a
            JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id
            WHERE a.specialist_id = {specialistId} AND a.status = 'completed'
              AND a.starts_at >= {wf} AND a.starts_at < {wt}
              AND (a.starts_at AT TIME ZONE l.timezone)::date BETWEEN {from} AND {to}
            GROUP BY a.location_id, l.name
            ORDER BY l.name, a.location_id
            """).ToListAsync(ct);
        return rows.Select(r => new MyRevenueLocation(r.LocationId, r.Name, r.Completed, r.Revenue)).ToList();
    }

    /// <inheritdoc />
    public async Task<Guid?> MatchByPhoneAsync(string? phone, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(phone) ? null : (await FindClientIdsByPhoneAsync(phone.Trim(), null, 1, ct)).Select(x => (Guid?)x).FirstOrDefault();

    /// <inheritdoc />
    public async Task<bool> InScopeAsync(Actor actor, Guid clientId, CancellationToken ct)
    {
        if (actor.Access.AtLeast(PermissionKeys.ClientsView, PermissionLevels.All)) return true;
        if (!actor.Access.AtLeast(PermissionKeys.ClientsView, PermissionLevels.Own)) return false;
        if (actor.SpecialistId is not { } own || actor.Access.SpecialistInactive) return false;
        var me = actor.UserId;
        return await db.Appointments.AsNoTracking().AnyAsync(a =>
            a.ClientId == clientId && a.SpecialistId == own
            && !(a.Status == Entities.AppointmentStatus.Cancelled && a.CreatedByUserId == me), ct);
    }
}
