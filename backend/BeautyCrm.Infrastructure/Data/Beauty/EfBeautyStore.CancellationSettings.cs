using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

public sealed partial class EfBeautyStore
{
    private const string UniqueViolation = "23505";

    public async Task<CancellationSettings?> GetCancellationSettingsAsync(CancellationToken ct)
    {
        var r = await db.CancellationSettings.AsNoTracking().FirstOrDefaultAsync(ct); // RLS: лише рядок свого tenant
        return r is null ? null : new CancellationSettings(r.WindowHours, r.RefundPercentInWindow, r.RefundPercentOutside, r.DeductFee, r.FeePercent);
    }

    public async Task SaveCancellationSettingsAsync(CancellationSettings s, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.CancellationSettings.FirstOrDefaultAsync(ct);
            if (row is null) db.CancellationSettings.Add(row = new CancellationSettingsRow { Id = Guid.NewGuid() });
            row.WindowHours = s.WindowHours;
            row.RefundPercentInWindow = s.RefundPercentInWindow;
            row.RefundPercentOutside = s.RefundPercentOutside;
            row.DeductFee = s.DeductFee;
            row.FeePercent = s.FeePercent;
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            // гонка двох перших PUT: unique(tenant_id) -> повторити як оновлення
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
