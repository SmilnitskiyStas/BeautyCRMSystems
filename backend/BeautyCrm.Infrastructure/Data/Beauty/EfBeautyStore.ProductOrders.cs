using System.Linq.Expressions;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Application.Features.BeautyProductOrders;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IProductOrderStore (ADR-026, контракт §33.16, TASK-902). Усе під RLS tenant. Порядок блокувань (без циклів):
//   створення: advisory tenant -> рівні (location, product) за зростанням product_id -> товари/налаштування FOR SHARE;
//   фіналізація оплати: замовлення FOR UPDATE -> рівні -> товари FOR SHARE (етап 1: LockLevelsAsync/LockProductsAsync).
// Резерв НЕ зберігається колонкою: рахується з рядків замовлень (pending_payment і (резерв не минув або оплата в процесі)).
public sealed partial class EfBeautyStore : IProductOrderStore
{
    // ---------- читання ----------

    private async Task<ProductOrderView?> LoadOrderViewAsync(Expression<Func<ProductOrder, bool>> predicate, CancellationToken ct)
    {
        var o = await db.ProductOrders.AsNoTracking().Where(predicate).FirstOrDefaultAsync(ct);
        if (o is null) return null;
        var loc = await db.Locations.AsNoTracking().Where(l => l.Id == o.LocationId).Select(l => new { l.Name, l.Address }).FirstAsync(ct);
        var lines = await db.ProductOrderLines.AsNoTracking().Where(l => l.OrderId == o.Id).OrderBy(l => l.LineNo).ToListAsync(ct);
        return new ProductOrderView(
            o.Id, o.LocationId, loc.Name, loc.Address, o.OrderNumber, EnumText<ProductOrderStatus>.ToDb(o.Status), o.GuestName, o.GuestPhone,
            o.TotalAmount, o.CreatedAt, o.ReservationExpiresAt, o.PaymentAttempts, o.PaymentStartedAt, o.PaidAt, o.FulfilledAt, o.ClosedAt,
            o.CloseReason is { } cr ? EnumText<ProductOrderCloseReason>.ToDb(cr) : null, o.RefundAmount, o.PaymentId, o.IdempotencyRequestHash,
            [.. lines.Select(l => new ProductOrderLineView(l.ProductId, l.ProductName, EnumText<ProductUnit>.ToDb(l.Unit), l.Quantity, l.UnitPrice, l.LineTotal))]);
    }

    public Task<ProductOrderView?> GetOrderAsync(Guid orderId, CancellationToken ct) => LoadOrderViewAsync(o => o.Id == orderId, ct);

    public Task<ProductOrderView?> FindByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        LoadOrderViewAsync(o => o.PublicTokenHash == tokenHash, ct);

    public Task<ProductOrderView?> FindByIdempotencyKeyAsync(string keyHash, CancellationToken ct) =>
        LoadOrderViewAsync(o => o.IdempotencyKeyHash == keyHash, ct);

    public async Task<OrderLocationRow?> GetActiveLocationAsync(Guid locationId, CancellationToken ct) =>
        await db.Locations.AsNoTracking().Where(l => l.Id == locationId && l.IsActive)
            .Select(l => new OrderLocationRow(l.Id, l.Name, l.Address, l.Timezone)).FirstOrDefaultAsync(ct);

    private static bool Sellable(Product? p, bool disabledInLocation) =>
        p is { IsActive: true, SalePrice: > 0 } && p.Purpose is ProductPurpose.Sale or ProductPurpose.Both && !disabledInLocation;

    /// <summary>
    /// available = Σ quantity непрострочених партій закладу − reserved (§33.16). reserved = Σ quantity рядків замовлень закладу в pending_payment,
    /// де резерв не минув АБО оплата в процесі. Без блокувань (блокування рівнів - на боці виклику).
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> AvailabilityAsync(Guid locationId, IReadOnlyCollection<Guid> productIds, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        if (productIds.Count == 0) return [];
        var ids = productIds.ToArray();
        var stock = (await db.StockBatches.AsNoTracking()
                .Where(b => b.LocationId == locationId && ids.Contains(b.ProductId) && b.Quantity > 0 && (b.ExpiresOn == null || b.ExpiresOn >= today))
                .GroupBy(b => b.ProductId).Select(g => new { g.Key, Q = g.Sum(b => b.Quantity) }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Q);
        var reserved = (await (from l in db.ProductOrderLines.AsNoTracking()
                               join o in db.ProductOrders.AsNoTracking() on l.OrderId equals o.Id
                               where o.LocationId == locationId && ids.Contains(l.ProductId) && o.Status == ProductOrderStatus.PendingPayment
                                     && (o.ReservationExpiresAt > now || o.PaymentStartedAt != null)
                               group l by l.ProductId into g
                               select new { g.Key, Q = g.Sum(x => x.Quantity) }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => (decimal)x.Q);
        return ids.ToDictionary(id => id, id => stock.GetValueOrDefault(id) - reserved.GetValueOrDefault(id));
    }

    // ---------- P1: каталог ----------

    public async Task<CatalogData?> ListCatalogAsync(Guid locationId, DateTimeOffset now, CancellationToken ct)
    {
        var loc = await GetActiveLocationAsync(locationId, ct);
        if (loc is null) return null;
        var disabled = (await db.ProductLocationSettings.AsNoTracking().Where(s => s.LocationId == locationId && !s.IsEnabled)
            .Select(s => s.ProductId).ToListAsync(ct)).ToHashSet();
        var products = (await db.Products.AsNoTracking()
                .Where(p => p.IsActive && (p.Purpose == ProductPurpose.Sale || p.Purpose == ProductPurpose.Both) && p.SalePrice > 0).ToListAsync(ct))
            .Where(p => !disabled.Contains(p.Id)).ToList();
        var paths = await CategoryPathsAsync(ct);
        var avail = await AvailabilityAsync(locationId, [.. products.Select(p => p.Id)], SlotCalculator.LocalDate(now, loc.Timezone), now, ct);

        var rows = products.Select(p => new CatalogRow(
                p.Id, p.Name, EnumText<ProductUnit>.ToDb(p.Unit), p.SalePrice!.Value,
                p.CategoryId is { } cid && paths.TryGetValue(cid, out var path) ? path : string.IsNullOrWhiteSpace(p.Category) ? null : p.Category.Trim(),
                avail.GetValueOrDefault(p.Id) >= 1m))
            .OrderBy(r => r.CategoryPath is null).ThenBy(r => r.CategoryPath?.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(r => r.Id).ToList();
        var truncated = rows.Count > ProductOrdersOptions.MaxCatalogItems;
        return new CatalogData(loc, truncated ? rows[..ProductOrdersOptions.MaxCatalogItems] : rows, truncated);
    }

    // ---------- P2: створення ----------

    public async Task<CreateOrderOutcome> CreateAsync(NewProductOrder n, CreateLimits lim, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        async Task<CreateOrderOutcome> Done(CreateOrderOutcome r)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return r;
        }

        // 1. advisory tenant: серіалізує номер, ліміти й повторну перевірку ідемпотентного ключа
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('product_orders:' || coalesce(current_setting('app.tenant_id', true), ''), 0))", ct);
        if (await LoadOrderViewAsync(o => o.IdempotencyKeyHash == n.KeyHash, ct) is { } hit) return await Done(new(CreateStatus.Replay, hit));

        // 2. заклад і товари (попередній відбір без блокувань)
        if (await GetActiveLocationAsync(n.LocationId, ct) is not { } loc) return await Done(new(CreateStatus.LocationNotFound));
        var pids = n.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var disabled0 = (await db.ProductLocationSettings.AsNoTracking().Where(s => s.LocationId == n.LocationId && pids.Contains(s.ProductId) && !s.IsEnabled)
            .Select(s => s.ProductId).ToListAsync(ct)).ToHashSet();
        var bad = pids.Where(id => !Sellable(products.GetValueOrDefault(id), disabled0.Contains(id))).ToList();
        if (bad.Count > 0) return await Done(new(CreateStatus.ProductUnavailable, ProductIds: bad));
        var total = n.Lines.Sum(l => products[l.ProductId].SalePrice!.Value * l.Quantity);
        if (total > lim.MaxOrderTotal) return await Done(new(CreateStatus.TotalTooLarge));
        if (n.ExpectedTotal is { } expected && expected != total) return await Done(new(CreateStatus.PriceChanged, Total: total));

        // 3. ліміти (під advisory tenant)
        var hourAgo = now.AddHours(-1);
        if (lim.MaxCreatesPerLocationPerHour > 0
            && await db.ProductOrders.AsNoTracking().CountAsync(o => o.LocationId == n.LocationId && o.CreatedAt > hourAgo, ct) >= lim.MaxCreatesPerLocationPerHour)
            return await Done(new(CreateStatus.LimitReached, LimitScope: "location_hourly"));
        if (await db.ProductOrders.AsNoTracking().CountAsync(o => o.GuestPhone == n.GuestPhone && o.Status == ProductOrderStatus.PendingPayment
                && (o.ReservationExpiresAt > now || o.PaymentStartedAt != null), ct) >= lim.MaxPendingPerPhone)
            return await Done(new(CreateStatus.LimitReached, LimitScope: "phone_active"));
        if (await db.ProductOrders.AsNoTracking().CountAsync(o => o.GuestPhone == n.GuestPhone && o.CreatedAt > hourAgo, ct) >= lim.MaxCreatesPerPhonePerHour)
            return await Done(new(CreateStatus.LimitReached, LimitScope: "phone_hourly"));

        // 4. рівні FOR UPDATE (лише наявні; рядка немає -> available = 0, рядок не створюється) за зростанням product_id
        foreach (var pid in pids.Distinct().OrderBy(x => x))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM beauty_stock_levels WHERE location_id = {n.LocationId} AND product_id = {pid} FOR UPDATE", ct);
        // 5. товари й налаштування FOR SHARE (після рівнів, як у етапі 1) - повторна перевірка й знімок цін під блокуванням
        var locked = await LockProductsAsync(pids, ct);
        var enabled = await LockEnabledAsync(n.LocationId, pids, ct);
        var bad2 = pids.Where(id => !Sellable(locked.GetValueOrDefault(id), enabled.TryGetValue(id, out var en) && !en)).ToList();
        if (bad2.Count > 0) return await Done(new(CreateStatus.ProductUnavailable, ProductIds: bad2));
        var total2 = n.Lines.Sum(l => locked[l.ProductId].SalePrice!.Value * l.Quantity);
        if (total2 != total) return await Done(new(total2 > lim.MaxOrderTotal ? CreateStatus.TotalTooLarge : CreateStatus.PriceChanged, Total: total2));

        // 6. наявність: available - reserved >= quantity для кожного рядка
        var avail = await AvailabilityAsync(n.LocationId, pids, SlotCalculator.LocalDate(now, loc.Timezone), now, ct);
        var short_ = n.Lines.Where(l => avail.GetValueOrDefault(l.ProductId) < l.Quantity).Select(l => l.ProductId).ToList();
        if (short_.Count > 0) return await Done(new(CreateStatus.InsufficientStock, ProductIds: short_));

        // 7. номер + вставка (знімки назви, одиниці, ціни)
        var number = (await db.ProductOrders.AsNoTracking().MaxAsync(o => (int?)o.OrderNumber, ct) ?? 0) + 1;
        db.ProductOrders.Add(new ProductOrder
        {
            Id = n.OrderId, LocationId = n.LocationId, OrderNumber = number, PublicTokenHash = n.TokenHash, IdempotencyKeyHash = n.KeyHash,
            IdempotencyRequestHash = n.RequestHash, Status = ProductOrderStatus.PendingPayment, Source = EnumText<ProductOrderSource>.FromDb(n.Source),
            GuestName = n.GuestName, GuestPhone = n.GuestPhone, TotalAmount = total, Currency = "UAH",
            ReservationExpiresAt = now.AddMinutes(lim.ReservationMinutes), CreatedAt = now, UpdatedAt = now,
        });
        for (var i = 0; i < n.Lines.Count; i++)
        {
            var l = n.Lines[i];
            var p = locked[l.ProductId];
            var price = p.SalePrice!.Value;
            db.ProductOrderLines.Add(new ProductOrderLine
            {
                Id = Guid.NewGuid(), OrderId = n.OrderId, LineNo = (short)(i + 1), ProductId = p.Id, ProductName = p.Name, Unit = p.Unit,
                Quantity = l.Quantity, UnitPrice = price, LineTotal = price * l.Quantity, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(CreateStatus.Created, await GetOrderAsync(n.OrderId, ct));
    }

    // ---------- P4: оплата ----------

    public async Task<ClaimOutcome> ClaimPaymentAsync(Guid orderId, DateTimeOffset now, TimeSpan stale, int maxAttempts, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_product_orders SET payment_started_at = {now}, payment_attempts = payment_attempts + 1, updated_at = {now}
            WHERE id = {orderId} AND status = 'pending_payment' AND payment_started_at IS NULL AND reservation_expires_at > {now}
              AND payment_attempts < {maxAttempts}", ct);
        if (claimed == 1)
        {
            var total = await db.ProductOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.TotalAmount).FirstAsync(ct);
            var payment = NewOrderPayment(orderId, total);
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            return new(ClaimKind.Claimed, await GetOrderAsync(orderId, ct), payment.Id, "pending");
        }

        var view = await GetOrderAsync(orderId, ct);
        if (view is { Status: "pending_payment", PaymentStartedAt: { } started } && started < now - stale)
        {
            // reconcile: перехоплення завислої оплати (CAS за старим payment_started_at); спробу не збільшуємо
            var taken = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE beauty_product_orders SET payment_started_at = {now}, updated_at = {now}
                WHERE id = {orderId} AND status = 'pending_payment' AND payment_started_at = {started}", ct);
            if (taken == 1)
            {
                var last = await db.Payments.AsNoTracking().Where(p => p.ProductOrderId == orderId && (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Paid))
                    .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).FirstOrDefaultAsync(ct);
                if (last is null)
                {
                    last = NewOrderPayment(orderId, view.Total);
                    db.Payments.Add(last);
                    await db.SaveChangesAsync(ct);
                }
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                return new(ClaimKind.Reconcile, await GetOrderAsync(orderId, ct), last.Id, EnumText<PaymentStatus>.ToDb(last.Status));
            }
            view = await GetOrderAsync(orderId, ct);
        }
        await tx.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return new(ClaimKind.NoClaim, view);
    }

    private static Payment NewOrderPayment(Guid orderId, decimal total) => new()
    {
        Id = Guid.NewGuid(), ProductOrderId = orderId, Amount = total, Method = BeautyCrm.Infrastructure.Data.Entities.PaymentMethod.Card, Status = PaymentStatus.Pending,
    };

    public async Task MarkPaymentFailedAsync(Guid orderId, Guid paymentId, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_payments SET status = 'failed', updated_at = {now} WHERE id = {paymentId} AND status = 'pending'", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE beauty_product_orders SET payment_started_at = NULL, updated_at = {now} WHERE id = {orderId} AND status = 'pending_payment'", ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<FinalizeOutcome> FinalizePaidAsync(Guid orderId, Guid paymentId, string? providerPaymentId, DateTimeOffset now, CancellationToken ct)
    {
        // Фаза 1: гроші списано - фіксуємо це окремо (рівно один paid-платіж; повтор ідемпотентний). Далі, навіть при збої, reconcile дозавершить.
        await using (var tx1 = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE beauty_payments SET status = 'paid', provider_payment_id = coalesce(provider_payment_id, {providerPaymentId}), paid_at = coalesce(paid_at, {now}), updated_at = {now}
                WHERE id = {paymentId} AND status = 'pending'", ct);
            await tx1.CommitAsync(ct);
        }

        // Фаза 2: замовлення FOR UPDATE -> рівні -> товари FOR SHARE -> FEFO (без прострочених) -> операція sale, рухи, клієнт, статус paid.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        async Task<FinalizeOutcome> Done(FinalizeKind kind)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new(kind, await GetOrderAsync(orderId, ct));
        }

        var order = (await db.ProductOrders.FromSqlInterpolated($"SELECT * FROM beauty_product_orders WHERE id = {orderId} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
        if (order is null) return await Done(FinalizeKind.NotPending);
        if (order.Status is ProductOrderStatus.Paid or ProductOrderStatus.Fulfilled) return await Done(FinalizeKind.AlreadyPaid);
        if (order.Status != ProductOrderStatus.PendingPayment) return await Done(FinalizeKind.NotPending);

        var lines = await db.ProductOrderLines.AsNoTracking().Where(l => l.OrderId == orderId).OrderBy(l => l.LineNo).ToListAsync(ct);
        var pids = lines.Select(l => l.ProductId).ToList();
        var levels = await LockLevelsAsync(pids.Select(p => (order.LocationId, p)), ct);
        var at = LockedNow(now);
        await LockProductsAsync(pids, ct);

        var tz = await db.Locations.AsNoTracking().Where(l => l.Id == order.LocationId).Select(l => l.Timezone).SingleAsync(ct);
        var today = SlotCalculator.LocalDate(at, tz);
        var batches = await db.StockBatches.Where(b => b.LocationId == order.LocationId && pids.Contains(b.ProductId) && b.Quantity > 0).ToDictionaryAsync(b => b.Id, ct);
        var plan = FefoPlanner.Allocate([.. lines.Select((l, i) => new StockLine(i, l.ProductId, l.Quantity, null))], [.. batches.Values.Select(ToRow)], skipExpired: true, today);
        if (plan.Shortages.Count > 0) return await Done(FinalizeKind.StockUnavailable);

        var op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Sale, LocationId = order.LocationId, ProductOrderId = orderId, CreatedByUserId = null, CreatedAt = at };
        db.StockOperations.Add(op);
        foreach (var t in plan.Takes)
        {
            batches[t.BatchId].Quantity -= t.Quantity;
            levels[(order.LocationId, t.ProductId)].Quantity -= t.Quantity;
            Move(op, order.LocationId, t.ProductId, t.BatchId, StockMovementType.Sale, -t.Quantity, t.UnitCost, null, at);
        }
        // клієнт CRM знаходиться/створюється за телефоном ЛИШЕ тут (після оплати); ім'я наявного не перезаписується
        order.ClientId = await ResolveClientAsync(new ClientInput(null, order.GuestName, order.GuestPhone, null, false, PhoneSources.WebForm), ct);
        order.Status = ProductOrderStatus.Paid;
        order.PaidAt = at;
        order.PaymentId = paymentId;
        order.PaymentStartedAt = null;
        order.UpdatedAt = at;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(FinalizeKind.Paid, await GetOrderAsync(orderId, ct));
    }

    public async Task CompensateAsync(Guid orderId, Guid paymentId, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_product_orders SET status = 'refunded', close_reason = 'stock_unavailable', closed_at = {now}, refund_amount = total_amount,
                   payment_id = {paymentId}, payment_started_at = NULL, updated_at = {now}
            WHERE id = {orderId} AND status = 'pending_payment'", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_payments SET status = 'refunded', updated_at = {now} WHERE id = {paymentId} AND status = 'paid'", ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    // ---------- P5: скасування неоплаченого ----------

    public async Task<bool> CancelUnpaidAsync(Guid orderId, string by, Guid? userId, string? note, DateTimeOffset now, CancellationToken ct)
    {
        var n = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_product_orders SET status = 'cancelled', close_reason = {by}, closed_at = {now}, cancelled_by_user_id = {userId}, cancel_note = {note}, updated_at = {now}
            WHERE id = {orderId} AND status = 'pending_payment' AND payment_started_at IS NULL AND reservation_expires_at > {now}", ct);
        db.ChangeTracker.Clear();
        return n == 1;
    }
}
