using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Application.Features.BeautyProductOrders;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IProductOrderAdminStore (ADR-026, контракт §33.13/§33.16, TASK-903). Усе під RLS tenant. Повернення оплаченого: транзакція
// замовлення FOR UPDATE -> рівні -> товари FOR SHARE (порядок §33.16); sale_return дзеркалить рухи sale (та сама партія, unit_cost).
public sealed partial class EfBeautyStore : IProductOrderAdminStore
{
    private async Task<List<AdminOrderRow>> ToAdminRowsAsync(IReadOnlyList<ProductOrder> orders, CancellationToken ct)
    {
        if (orders.Count == 0) return [];
        var ids = orders.Select(o => o.Id).ToList();
        var locIds = orders.Select(o => o.LocationId).Distinct().ToList();
        var locs = await db.Locations.AsNoTracking().Where(l => locIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var agg = (await db.ProductOrderLines.AsNoTracking().Where(l => ids.Contains(l.OrderId))
                .GroupBy(l => l.OrderId).Select(g => new { g.Key, Lines = g.Count(), Items = g.Sum(x => x.Quantity) }).ToListAsync(ct))
            .ToDictionary(x => x.Key);
        return [.. orders.Select(o => new AdminOrderRow(
            o.Id, o.OrderNumber, EnumText<ProductOrderStatus>.ToDb(o.Status), o.LocationId, locs.GetValueOrDefault(o.LocationId) ?? "", o.GuestName, o.GuestPhone,
            o.ClientId, o.TotalAmount, agg.TryGetValue(o.Id, out var a) ? a.Lines : 0, a is null ? 0 : agg[o.Id].Items, o.CreatedAt, o.ReservationExpiresAt,
            o.PaymentStartedAt, o.RefundStartedAt, o.PaidAt, o.FulfilledAt, o.ClosedAt,
            o.CloseReason is { } cr ? EnumText<ProductOrderCloseReason>.ToDb(cr) : null))];
    }

    public async Task<IReadOnlyList<AdminOrderRow>> ListProductOrdersAsync(AdminOrderFilter f, string[]? after, int take, CancellationToken ct)
    {
        var now = f.Now;
        var q = db.ProductOrders.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } scope) q = q.Where(o => scope.Contains(o.LocationId));
        if (f.LocationId is { } lid) q = q.Where(o => o.LocationId == lid);
        if (f.Number is { } number) q = q.Where(o => o.OrderNumber == number);
        if (f.From is { } from) q = q.Where(o => o.CreatedAt >= from);
        if (f.To is { } to) q = q.Where(o => o.CreatedAt <= to);
        // фільтр за ЕФЕКТИВНИМ статусом (§33.16): pending_payment без оплати в процесі й з минулим резервом = expired
        q = f.Status switch
        {
            "open" => q.Where(o => o.Status == ProductOrderStatus.Paid
                || (o.Status == ProductOrderStatus.PendingPayment && (o.PaymentStartedAt != null || o.ReservationExpiresAt > now))),
            "pending_payment" => q.Where(o => o.Status == ProductOrderStatus.PendingPayment && (o.PaymentStartedAt != null || o.ReservationExpiresAt > now)),
            "paid" => q.Where(o => o.Status == ProductOrderStatus.Paid),
            "fulfilled" => q.Where(o => o.Status == ProductOrderStatus.Fulfilled),
            "cancelled" => q.Where(o => o.Status == ProductOrderStatus.Cancelled),
            "refunded" => q.Where(o => o.Status == ProductOrderStatus.Refunded),
            "expired" => q.Where(o => o.Status == ProductOrderStatus.Expired
                || (o.Status == ProductOrderStatus.PendingPayment && o.PaymentStartedAt == null && o.ReservationExpiresAt <= now)),
            _ => q,
        };
        if (after is not null)
        {
            var at = new DateTimeOffset(long.Parse(after[0], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
            var id = Guid.Parse(after[1]);
            q = q.Where(o => o.CreatedAt < at || (o.CreatedAt == at && o.Id.CompareTo(id) < 0));
        }
        var rows = await q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(take).ToListAsync(ct);
        return await ToAdminRowsAsync(rows, ct);
    }

    public async Task<int> CountPaidProductOrdersAsync(IReadOnlyList<Guid>? locationIds, int cap, CancellationToken ct)
    {
        var q = db.ProductOrders.AsNoTracking().Where(o => o.Status == ProductOrderStatus.Paid);
        if (locationIds is { } ids) q = q.Where(o => ids.Contains(o.LocationId));
        return await q.Take(cap).CountAsync(ct);
    }

    public async Task<AdminOrderDetail?> GetProductOrderDetailAsync(Guid id, CancellationToken ct)
    {
        var o = await db.ProductOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        var row = (await ToAdminRowsAsync([o], ct))[0];
        var lines = await db.ProductOrderLines.AsNoTracking().Where(l => l.OrderId == id).OrderBy(l => l.LineNo).ToListAsync(ct);
        var pids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var payment = await db.Payments.AsNoTracking().Where(p => p.ProductOrderId == id)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Select(p => new { p.Status }).FirstOrDefaultAsync(ct);
        var ops = await db.StockOperations.AsNoTracking().Where(x => x.ProductOrderId == id).Select(x => new { x.Id, x.Kind }).ToListAsync(ct);
        var userIds = new[] { o.FulfilledByUserId, o.CancelledByUserId }.Where(u => u != null).Select(u => u!.Value);
        var users = await UserNamesAsync(userIds, ct);
        return new AdminOrderDetail(
            row,
            [.. lines.Select(l => new AdminOrderLine(ToRef(products[l.ProductId]), l.ProductName, EnumText<ProductUnit>.ToDb(l.Unit), l.Quantity, l.UnitPrice, l.LineTotal))],
            o.RefundAmount, o.CancelNote, Named(o.FulfilledByUserId, users), Named(o.CancelledByUserId, users),
            payment is null ? null : EnumText<PaymentStatus>.ToDb(payment.Status), o.PaymentAttempts, o.PaymentId,
            ops.FirstOrDefault(x => x.Kind == StockOperationKind.Sale)?.Id, ops.FirstOrDefault(x => x.Kind == StockOperationKind.SaleReturn)?.Id,
            EnumText<ProductOrderSource>.ToDb(o.Source));
    }

    public async Task<bool> FulfillProductOrderAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var n = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_product_orders SET status = 'fulfilled', fulfilled_at = {now}, fulfilled_by_user_id = {userId}, updated_at = {now}
            WHERE id = {id} AND status = 'paid' AND refund_started_at IS NULL", ct);
        db.ChangeTracker.Clear();
        return n == 1;
    }

    public async Task<bool> ClaimProductOrderRefundAsync(Guid id, DateTimeOffset now, TimeSpan stale, CancellationToken ct)
    {
        var threshold = now - stale;
        var n = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_product_orders SET refund_started_at = {now}, updated_at = {now}
            WHERE id = {id} AND status = 'paid' AND (refund_started_at IS NULL OR refund_started_at < {threshold})", ct);
        db.ChangeTracker.Clear();
        return n == 1;
    }

    public async Task ReleaseProductOrderRefundClaimAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE beauty_product_orders SET refund_started_at = NULL WHERE id = {id} AND status = 'paid'", ct);
        db.ChangeTracker.Clear();
    }

    public async Task<RefundFinalizeKind> FinalizeProductOrderRefundAsync(Guid id, Guid userId, string? note, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = (await db.ProductOrders.FromSqlInterpolated($"SELECT * FROM beauty_product_orders WHERE id = {id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
        if (order is null || order.Status != ProductOrderStatus.Paid || order.PaymentId is not { } paymentId)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return RefundFinalizeKind.NotPaid;
        }

        // рухи sale цього замовлення (≤ 1 операція sale на замовлення, інваріант §33.16 (1))
        var sales = await (from m in db.StockMovements
                           join o in db.StockOperations on m.OperationId equals o.Id
                           where o.ProductOrderId == id && o.Kind == StockOperationKind.Sale && m.Type == StockMovementType.Sale && !m.IsShortage
                           select m).AsNoTracking().ToListAsync(ct);
        if (sales.Count == 0) throw new InvalidOperationException($"Paid product order {id} has no sale movements.");

        var pids = sales.Select(m => m.ProductId).Distinct().ToList();
        var levels = await LockLevelsAsync(pids.Select(p => (order.LocationId, p)), ct);
        await LockProductsAsync(pids, ct);
        var at = LockedNow(now);
        var batchIds = sales.Where(m => m.BatchId != null).Select(m => m.BatchId!.Value).Distinct().ToList();
        var batches = await db.StockBatches.Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);

        var op = new StockOperation
        {
            Id = Guid.NewGuid(), Kind = StockOperationKind.SaleReturn, LocationId = order.LocationId, ProductOrderId = id, Note = note,
            CreatedByUserId = userId, CreatedAt = at,
        };
        db.StockOperations.Add(op);
        foreach (var m in sales.OrderBy(m => m.OccurredAt).ThenBy(m => m.Id))
        {
            var q = Math.Abs(m.Quantity);
            batches[m.BatchId!.Value].Quantity += q;
            levels[(order.LocationId, m.ProductId)].Quantity += q;
            Move(op, order.LocationId, m.ProductId, m.BatchId, StockMovementType.SaleReturn, q, m.UnitCost, userId, at);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE beauty_payments SET status = 'refunded', updated_at = {at} WHERE id = {paymentId} AND status = 'paid'", ct);
        order.Status = ProductOrderStatus.Refunded;
        order.CloseReason = ProductOrderCloseReason.Staff;
        order.CancelledByUserId = userId;
        order.CancelNote = note;
        order.RefundAmount = order.TotalAmount;
        order.ClosedAt = at;
        order.RefundStartedAt = null;
        order.UpdatedAt = at;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return RefundFinalizeKind.Refunded;
    }
}
