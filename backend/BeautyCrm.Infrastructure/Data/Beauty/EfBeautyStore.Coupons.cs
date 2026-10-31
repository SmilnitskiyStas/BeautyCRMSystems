using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>Купони (TASK-778, §26.6): порт <see cref="ICouponStore"/> над EF. Tenant задає RLS; хеш коду - UNIQUE (tenant, code_hash).</summary>
public sealed partial class EfBeautyStore : ICouponStore
{
    public async Task<CouponLookup?> FindByHashAsync(string codeHash, CancellationToken ct) =>
        await LookupAsync(db.Coupons.AsNoTracking().Where(c => c.CodeHash == codeHash), ct);

    public async Task<CouponLookup?> FindByIdAsync(Guid id, CancellationToken ct) =>
        await LookupAsync(db.Coupons.AsNoTracking().Where(c => c.Id == id), ct);

    private async Task<CouponLookup?> LookupAsync(IQueryable<Coupon> q, CancellationToken ct)
    {
        var c = await q.FirstOrDefaultAsync(ct);
        if (c is null) return null;
        var p = await db.Promotions.AsNoTracking().FirstAsync(x => x.Id == c.PromotionId, ct);
        var locs = await db.PromotionLocations.AsNoTracking().Where(x => x.PromotionId == p.Id).Select(x => x.LocationId).ToListAsync(ct);
        var svcs = await db.PromotionServices.AsNoTracking().Where(x => x.PromotionId == p.Id).Select(x => x.ServiceId).ToListAsync(ct);
        return new CouponLookup(ToInfo(c),
            new PromotionRule(p.Id, p.Name, EnumText<DiscountType>.ToDb(p.DiscountType), p.DiscountValue, p.StartsAt, p.EndsAt, p.IsActive, locs, svcs),
            p.Access);
    }

    private static CouponInfo ToInfo(Coupon c) => new(c.Id, c.Kind, c.PromotionId, c.CampaignId, c.ClientId, c.CodeHint, c.Code,
        c.ValidFrom, c.ValidTo, c.MaxUses, c.UsesCount, c.Status);

    public Task<bool> HasActiveRedemptionAsync(Guid couponId, Guid clientId, CancellationToken ct) =>
        db.CouponRedemptions.AsNoTracking().AnyAsync(r => r.CouponId == couponId && r.ClientId == clientId && r.Status == "active", ct);

    public Task<string?> GetPromotionAccessAsync(Guid promotionId, CancellationToken ct) =>
        db.Promotions.AsNoTracking().Where(p => p.Id == promotionId).Select(p => (string?)p.Access).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SharedCouponDto>> ListSharedAsync(Guid? promotionId, CancellationToken ct) =>
        (await db.Coupons.AsNoTracking().Where(c => c.Kind == "shared" && (promotionId == null || c.PromotionId == promotionId))
            .OrderByDescending(c => c.CreatedAt).ToListAsync(ct)).Select(ToShared).ToList();

    private static SharedCouponDto ToShared(Coupon c) =>
        new(c.Id, c.Kind, c.PromotionId, c.Code!, c.ValidFrom, c.ValidTo, c.MaxUses, c.UsesCount, c.Status);

    public async Task<SharedCouponDto?> CreateSharedAsync(NewSharedCoupon n, CancellationToken ct)
    {
        var c = new Coupon
        {
            Id = Guid.NewGuid(), Kind = "shared", PromotionId = n.PromotionId, Code = n.Code, CodeHash = n.CodeHash, CodeHint = n.CodeHint,
            ValidFrom = n.ValidFrom.ToUniversalTime(), ValidTo = n.ValidTo.ToUniversalTime(), MaxUses = n.MaxUses, CreatedByUserId = n.CreatedByUserId,
        };
        db.Coupons.Add(c);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            return null;
        }
        var saved = await db.Coupons.AsNoTracking().FirstAsync(x => x.Id == c.Id, ct);
        return ToShared(saved);
    }

    public async Task<bool> RevokeSharedAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Coupons.AsNoTracking().AnyAsync(c => c.Id == id && c.Kind == "shared", ct)) return false;
        await db.Coupons.Where(c => c.Id == id && c.Kind == "shared" && c.Status != "revoked")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "revoked").SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), ct);
        return true;
    }

    public async Task<bool> InsertPersonalAsync(NewPersonalCoupon n, CancellationToken ct)
    {
        db.Coupons.Add(new Coupon
        {
            Id = n.Id, Kind = "personal", PromotionId = n.PromotionId, CampaignId = n.CampaignId, ClientId = n.ClientId, Code = null,
            CodeHash = n.CodeHash, CodeHint = n.CodeHint, ValidFrom = n.ValidFrom.ToUniversalTime(), ValidTo = n.ValidTo.ToUniversalTime(),
            MaxUses = 1, CreatedByUserId = n.CreatedByUserId,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_beauty_coupons_code_hash" })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<ClientCouponDto>?> ListForClientAsync(Guid clientId, CancellationToken ct)
    {
        if (!await db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId && c.DeletedAt == null, ct)) return null;
        var rows = await (from c in db.Coupons.AsNoTracking()
                          where c.ClientId == clientId && c.Kind == "personal"
                          select new
                          {
                              c.Id, c.Kind, c.CodeHint, c.PromotionId, c.CampaignId, c.ValidFrom, c.ValidTo, c.Status, c.CreatedAt,
                              PromotionName = db.Promotions.Where(p => p.Id == c.PromotionId).Select(p => p.Name).FirstOrDefault(),
                              CampaignName = db.Campaigns.Where(x => x.Id == c.CampaignId).Select(x => x.Name).FirstOrDefault(),
                              Used = db.CouponRedemptions.Where(r => r.CouponId == c.Id && r.Status == "active")
                                  .Select(r => new { r.CreatedAt, r.AppointmentId }).FirstOrDefault(),
                          }).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(r => new ClientCouponDto(r.Id, r.Kind, r.CodeHint, new NamedRefDto(r.PromotionId, r.PromotionName ?? ""),
            r.CampaignId is { } cid ? new NamedRefDto(cid, r.CampaignName ?? "") : null, r.ValidFrom, r.ValidTo, r.Status,
            r.Used?.CreatedAt, r.Used?.AppointmentId)).ToList();
    }

    public Task<int> ReleaseForAppointmentAsync(Guid appointmentId, string reason, DateTimeOffset at, CancellationToken ct) =>
        ReleaseCouponCoreAsync(appointmentId, reason, at, ct);

    /// <summary>active -> released і uses_count - 1 одним оператором (ідемпотентно); виконується в поточній транзакції.</summary>
    private Task<int> ReleaseCouponCoreAsync(Guid appointmentId, string reason, DateTimeOffset at, CancellationToken ct)
    {
        var atUtc = at.ToUniversalTime();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH r AS (UPDATE beauty_coupon_redemptions SET status = 'released', release_reason = {reason}, released_at = {atUtc}
                       WHERE appointment_id = {appointmentId} AND status = 'active' RETURNING coupon_id)
            UPDATE beauty_coupons c SET uses_count = c.uses_count - 1, updated_at = {atUtc} FROM r WHERE c.id = r.coupon_id
            """, ct);
    }
}
