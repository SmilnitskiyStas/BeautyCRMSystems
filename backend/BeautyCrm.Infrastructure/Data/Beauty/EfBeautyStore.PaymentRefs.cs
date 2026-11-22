using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IPaymentRefStore (ADR-028): зв'язок beauty_payments.id -> рахунок провайдера. Під RLS поточного tenant, без нових колонок
// (provider / provider_payment_id уже є; маркер наміру повернення 'refunding' у provider не стирає provider_payment_id).
public sealed partial class EfBeautyStore : IPaymentRefStore
{
    public async Task<StoredPaymentRef?> GetPaymentRefAsync(Guid paymentId, CancellationToken ct)
    {
        var p = await db.Payments.AsNoTracking().Where(x => x.Id == paymentId).FirstOrDefaultAsync(ct);
        return p is null
            ? null
            : new StoredPaymentRef(p.Id, p.AppointmentId, p.ProductOrderId, p.Amount, EnumText<PaymentStatus>.ToDb(p.Status), p.Provider, p.ProviderPaymentId);
    }

    public async Task<bool> AttachProviderRefAsync(Guid paymentId, string provider, string providerPaymentId, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var n = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_payments SET provider = {provider}, provider_payment_id = {providerPaymentId}, updated_at = {now}
            WHERE id = {paymentId} AND status = 'pending' AND provider_payment_id IS NULL", ct);
        return n == 1;
    }
}
