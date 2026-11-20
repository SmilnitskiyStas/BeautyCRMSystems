using System.Globalization;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyProductOrders;

/// <summary>
/// CRM замовлень товарів (ADR-026 §2.5/§2.8/§2.10, контракт §33.13, §33.16). Права inventory.view (читання) / inventory.manage (дії), scope закладів як у складі
/// (чужий заклад -> 404 product_order_not_found). Від <c>ProductOrders:Enabled</c> НЕ залежить: вимкнення функції не блокує видачу й повернення оплачених.
/// Скасування оплаченого: claim -> RefundAsync (100%, поза транзакцією БД) -> транзакція sale_return + статус refunded. Логи без ПД і сум (§33.18).
/// </summary>
public sealed class ProductOrderAdminService(
    IProductOrderAdminStore admin, IProductOrderStore orders, IInventoryStore inventory, IPaymentService payments, ProductOrdersOptions options,
    TimeProvider clock, ILogger<ProductOrderAdminService>? log = null)
{
    public const int DefaultTake = 50, MaxTake = 200, MaxRangeDays = 366, MaxCancelReason = 300, CountsCap = 1000;
    private const string PendingPayment = "pending_payment";
    private static readonly string[] Statuses = ["open", "pending_payment", "paid", "fulfilled", "cancelled", "expired", "refunded", "all"];
    private static readonly Error NotFound = Error.NotFound("product_order_not_found", "Product order not found.");
    private static readonly Error LocationNotFound = Error.NotFound("location_not_found", "Location not found.");

    private TimeSpan Stale => TimeSpan.FromSeconds(options.PaymentStaleSeconds);

    private static Error? NeedView(Actor a) =>
        a.Access.AtLeast(PermissionKeys.InventoryView, PermissionLevels.Own) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryView, PermissionLevels.Own);

    private static Error? NeedManage(Actor a) =>
        a.Access.AtLeast(PermissionKeys.InventoryManage, PermissionLevels.Own) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryManage, PermissionLevels.Own);

    private Task<InventoryScope> ScopeAsync(Actor actor, string key, CancellationToken ct) =>
        InventoryScope.ResolveAsync(actor, key, inventory.SpecialistLocationIdsAsync, ct);

    // ---------- відображення ----------

    private static string Effective(AdminOrderRow r, DateTimeOffset now) =>
        ProductOrderService.EffectiveStatus(r.Status, r.PaymentStartedAt, r.ReservationExpiresAt, now);

    private static bool IsExpiredPending(AdminOrderRow r, string status) => status == "expired" && r.Status == PendingPayment;

    private static OrderCustomerDto Customer(Actor actor, AdminOrderRow r) =>
        ContactMasking.CanSeeContacts(actor) ? new(r.GuestName, r.GuestPhone, null) : new(r.GuestName, null, ContactMasking.MaskPhone(r.GuestPhone));

    private static ProductOrderSummaryDto Summary(Actor actor, AdminOrderRow r, DateTimeOffset now)
    {
        var status = Effective(r, now);
        var pending = status == PendingPayment;
        var expiredPending = IsExpiredPending(r, status);
        return new ProductOrderSummaryDto(
            r.Id, r.Number, status, new OrderLocationRefDto(r.LocationId, r.LocationName), Customer(actor, r), r.LineCount, r.ItemCount, r.Total,
            ProductOrderService.Currency, r.CreatedAt, pending ? r.ReservationExpiresAt : null, r.PaidAt, r.FulfilledAt,
            expiredPending ? r.ReservationExpiresAt : r.ClosedAt, expiredPending ? "expired" : r.CloseReason,
            pending && r.PaymentStartedAt is not null, status == "paid" && r.RefundStartedAt is not null);
    }

    private static ProductOrderDto Detail(Actor actor, AdminOrderDetail d, DateTimeOffset now)
    {
        var s = Summary(actor, d.Row, now);
        var seesClient = actor.Access.AtLeast(PermissionKeys.ClientsView, PermissionLevels.All);
        return new ProductOrderDto(
            s.Id, s.Number, s.Status, s.Location, s.Customer, s.LineCount, s.ItemCount, s.Total, s.Currency, s.CreatedAt, s.ReservationExpiresAt, s.PaidAt,
            s.FulfilledAt, s.ClosedAt, s.CloseReason, s.PaymentInProgress, s.RefundInProgress,
            [.. d.Lines.Select(l => new ProductOrderLineDto(l.Product, l.Name, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal))],
            d.RefundAmount, d.CancelNote, d.FulfilledBy, d.CancelledBy, seesClient ? d.Row.ClientId : null,
            d.PaymentStatus is null ? null : new ProductOrderPaymentDto(d.PaymentStatus, d.PaymentAttempts), d.SaleOperationId, d.ReturnOperationId, d.Source);
    }

    // ---------- читання ----------

    private static Result<(DateTimeOffset From, DateTimeOffset To)?> Range(string? from, string? to, bool defaults, DateTimeOffset now)
    {
        DateTimeOffset? f = null, t = null;
        if (from is not null)
        {
            if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x)) return Error.Validation("invalid_range", "from is invalid.");
            f = x.ToUniversalTime();
        }
        if (to is not null)
        {
            if (!DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x)) return Error.Validation("invalid_range", "to is invalid.");
            t = x.ToUniversalTime();
        }
        if (defaults)
        {
            t ??= now;
            f ??= t.Value.AddDays(-30);
        }
        if (f is { } a && t is { } b && (a > b || b - a > TimeSpan.FromDays(MaxRangeDays)))
            return Error.Validation("invalid_range", $"from..to must be a valid range of at most {MaxRangeDays} days.");
        return Result<(DateTimeOffset, DateTimeOffset)?>.Ok(f is null && t is null ? null : (f ?? DateTimeOffset.MinValue, t ?? DateTimeOffset.MaxValue));
    }

    public async Task<Result<Page<ProductOrderSummaryDto>>> ListAsync(Actor actor, ProductOrderListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var status = (q.Status ?? "open").Trim().ToLowerInvariant();
        if (!Statuses.Contains(status)) return Error.Validation("invalid_status", "status is invalid.");
        int? number = null;
        if (!string.IsNullOrWhiteSpace(q.Number))
        {
            if (!int.TryParse(q.Number.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
                return Error.Validation("invalid_number", "number must be a positive integer.");
            number = n;
        }
        var scope = await ScopeAsync(actor, PermissionKeys.InventoryView, ct);
        if (q.LocationId is { } lid && (!scope.Contains(lid) || (await inventory.ListInventoryLocationsAsync(ct)).All(l => l.Id != lid))) return LocationNotFound;
        var now = clock.GetUtcNow();
        // пошук за точним номером не обмежується датою за замовчуванням
        var range = Range(q.From, q.To, number is null, now);
        if (!range.IsOk) return range.Error!;
        var r = range.Value;

        var hash = InventoryService.ParamHash("product-orders", status, q.LocationId, number, q.From is null ? null : r?.From.UtcTicks, q.To is null ? null : r?.To.UtcTicks);
        var after = InventoryService.DecodeCursor(q.Cursor, hash, "tg");
        if (!after.IsOk) return after.Error!;
        var take = Math.Clamp(q.Take ?? DefaultTake, 1, MaxTake);
        var locations = scope.LocationFilter;
        List<AdminOrderRow> rows = locations is { Count: 0 }
            ? []
            : [.. await admin.ListProductOrdersAsync(new AdminOrderFilter(locations, q.LocationId, status, number, r?.From, r?.To, now), after.Value, take + 1, ct)];
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(take);
        var next = hasMore ? InventoryService.EncodeCursor(hash, rows[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), rows[^1].Id.ToString()) : null;
        return new Page<ProductOrderSummaryDto>([.. rows.Select(x => Summary(actor, x, now))], hasMore, next);
    }

    public async Task<Result<ProductOrderCountsDto>> CountsAsync(Actor actor, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ScopeAsync(actor, PermissionKeys.InventoryView, ct);
        var ids = scope.LocationFilter;
        return new ProductOrderCountsDto(ids is { Count: 0 } ? 0 : await admin.CountPaidProductOrdersAsync(ids, CountsCap, ct));
    }

    public async Task<Result<ProductOrderDto>> GetAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var d = await LoadAsync(actor, id, false, ct);
        if (!d.IsOk) return d.Error!;
        return Detail(actor, d.Value!, clock.GetUtcNow());
    }

    /// <summary>Замовлення в scope перегляду (і керування, якщо <paramref name="manage"/>); інакше 404 без розкриття.</summary>
    private async Task<Result<AdminOrderDetail>> LoadAsync(Actor actor, Guid id, bool manage, CancellationToken ct)
    {
        var d = await admin.GetProductOrderDetailAsync(id, ct);
        if (d is null) return NotFound;
        var view = await ScopeAsync(actor, PermissionKeys.InventoryView, ct);
        if (!view.Contains(d.Row.LocationId)) return NotFound;
        if (manage && !(await ScopeAsync(actor, PermissionKeys.InventoryManage, ct)).Contains(d.Row.LocationId)) return NotFound;
        return d;
    }

    private async Task<Result<AdminOrderDetail>> ReloadAsync(Guid id, CancellationToken ct) =>
        await admin.GetProductOrderDetailAsync(id, ct) is { } d ? d : NotFound;

    // ---------- видача ----------

    public async Task<Result<ProductOrderDto>> FulfillAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var loaded = await LoadAsync(actor, id, true, ct);
        if (!loaded.IsOk) return loaded.Error!;
        var now = clock.GetUtcNow();
        var d = loaded.Value!;
        var verdict = FulfillVerdict(d.Row, now);
        if (verdict is null && await admin.FulfillProductOrderAsync(id, actor.UserId, now, ct))
        {
            log?.LogInformation("product_order_fulfilled order={OrderId} user={UserId}", id, actor.UserId);
            return Detail(actor, (await ReloadAsync(id, ct)).Value!, now);
        }
        // гонка або вже видано: перечитуємо
        var fresh = await ReloadAsync(id, ct);
        if (!fresh.IsOk) return fresh.Error!;
        verdict = FulfillVerdict(fresh.Value!.Row, now);
        if (Effective(fresh.Value.Row, now) == "fulfilled") return Detail(actor, fresh.Value, now);
        return verdict ?? Error.Conflict("refund_in_progress", "A refund is in progress.");
    }

    /// <summary>null = можна видати; fulfilled обробляє виклик (noop).</summary>
    private static Error? FulfillVerdict(AdminOrderRow r, DateTimeOffset now)
    {
        var status = Effective(r, now);
        if (status == "fulfilled") return Error.Conflict("order_fulfilled", "The order is already fulfilled.");
        if (status != "paid") return Error.Conflict("order_not_paid", "Only a paid order can be fulfilled.");
        if (r.RefundStartedAt is not null) return Error.Conflict("refund_in_progress", "A refund is in progress.");
        return null;
    }

    // ---------- скасування ----------

    public async Task<Result<ProductOrderCancelDto>> CancelAsync(Actor actor, Guid id, CancelProductOrderRequest? req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var note = string.IsNullOrWhiteSpace(req?.Reason) ? null : req!.Reason!.Trim();
        if (note is { Length: > MaxCancelReason } || note?.Any(char.IsControl) == true)
            return Error.Validation("invalid_reason", $"reason must be at most {MaxCancelReason} characters.");
        var loaded = await LoadAsync(actor, id, true, ct);
        if (!loaded.IsOk) return loaded.Error!;
        var now = clock.GetUtcNow();
        var d = loaded.Value!;
        var status = Effective(d.Row, now);

        if (status == PendingPayment && d.Row.PaymentStartedAt is null
            && await orders.CancelUnpaidAsync(id, "staff", actor.UserId, note, now, ct))
        {
            log?.LogInformation("product_order_cancelled order={OrderId} by={By} refund={Refund}", id, "staff", false);
            return new ProductOrderCancelDto(Detail(actor, (await ReloadAsync(id, ct)).Value!, now), 0m);
        }
        if (status == "paid" && d.PaymentId is { } paymentId && (d.Row.RefundStartedAt is null || d.Row.RefundStartedAt < now - Stale))
            return await CancelPaidAsync(actor, d, paymentId, note, now, ct);
        return await OutcomeAsync(actor, id, now, ct);
    }

    /// <summary>Стан без дій: ідемпотентна повторна відповідь або помилка за поточним станом.</summary>
    private async Task<Result<ProductOrderCancelDto>> OutcomeAsync(Actor actor, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var fresh = await ReloadAsync(id, ct);
        if (!fresh.IsOk) return fresh.Error!;
        var d = fresh.Value!;
        var status = Effective(d.Row, now);
        if (status is "cancelled" or "refunded" && d.Row.CloseReason == "staff")
            return new ProductOrderCancelDto(Detail(actor, d, now), status == "refunded" ? d.RefundAmount ?? 0m : 0m);
        return status switch
        {
            "fulfilled" => Error.Conflict("order_fulfilled", "The order is already fulfilled."),
            "cancelled" or "refunded" or "expired" => Error.Conflict("order_closed", "The order is closed."),
            "paid" => Error.Conflict("refund_in_progress", "A refund is in progress."),
            _ => Error.Conflict("payment_in_progress", "Payment is in progress. Use reconcile-payment if it is stuck."),
        };
    }

    private async Task<Result<ProductOrderCancelDto>> CancelPaidAsync(Actor actor, AdminOrderDetail d, Guid paymentId, string? note, DateTimeOffset now, CancellationToken ct)
    {
        var id = d.Row.Id;
        if (!await admin.ClaimProductOrderRefundAsync(id, now, Stale, ct)) return await OutcomeAsync(actor, id, now, ct);

        var refunded = false;
        try
        {
            refunded = (await payments.RefundAsync(paymentId, d.Row.Total, ct)).Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "product_order_refund_failed order={OrderId} user={UserId}", id, actor.UserId);
        }
        if (!refunded)
        {
            await admin.ReleaseProductOrderRefundClaimAsync(id, CancellationToken.None);
            log?.LogWarning("product_order_refund_failed order={OrderId} user={UserId}", id, actor.UserId);
            return Error.PaymentFailed("refund_failed", "The refund failed. The order is unchanged.");
        }

        // Гроші повернено: фіксація не повинна скасовуватись токеном запиту (інакше claim лишиться до stale, повтор доведе до кінця).
        await admin.FinalizeProductOrderRefundAsync(id, actor.UserId, note, clock.GetUtcNow(), CancellationToken.None);
        log?.LogInformation("product_order_cancelled order={OrderId} by={By} refund={Refund}", id, "staff", true);
        var after = (await ReloadAsync(id, CancellationToken.None)).Value!;
        return new ProductOrderCancelDto(Detail(actor, after, clock.GetUtcNow()), after.RefundAmount ?? d.Row.Total);
    }

    // ---------- reconcile ----------

    public async Task<Result<ProductOrderDto>> ReconcilePaymentAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var loaded = await LoadAsync(actor, id, true, ct);
        if (!loaded.IsOk) return loaded.Error!;
        var now = clock.GetUtcNow();
        var r = loaded.Value!.Row;
        if (r.Status != PendingPayment || r.PaymentStartedAt is not { } started)
            return Error.Conflict("nothing_to_reconcile", "There is no payment in progress.");
        if (started >= now - Stale) return Error.Conflict("payment_in_progress", "Payment is in progress.");

        var claim = await orders.ClaimPaymentAsync(id, now, Stale, options.MaxPaymentAttempts, ct);
        if (claim.Kind == ClaimKind.NoClaim) return Error.Conflict("payment_in_progress", "Payment is in progress.");
        var order = claim.Order!;
        log?.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", id, order.PaymentAttempts, "reconciled");

        string? provider = null;
        if (!(claim.Kind == ClaimKind.Reconcile && claim.PaymentStatus == "paid"))
        {
            try
            {
                var charge = await payments.ChargeAsync(new PaymentCharge(claim.PaymentId, PaymentPurpose.ProductOrder, id, order.Total, ProductOrderService.Currency), ct);
                if (!charge.Success)
                {
                    await orders.MarkPaymentFailedAsync(id, claim.PaymentId, ct);
                    return Detail(actor, (await ReloadAsync(id, ct)).Value!, clock.GetUtcNow());
                }
                provider = charge.ProviderPaymentId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // лишається «в процесі»: наступний reconcile повторить Charge з тим самим PaymentId
                log?.LogError(ex, "product_order_reconcile_failed order={OrderId}", id);
                return Detail(actor, (await ReloadAsync(id, ct)).Value!, clock.GetUtcNow());
            }
        }

        var fin = await orders.FinalizePaidAsync(id, claim.PaymentId, provider, clock.GetUtcNow(), ct);
        if (fin.Kind == FinalizeKind.StockUnavailable)
        {
            var refunded = false;
            try
            {
                refunded = (await payments.RefundAsync(claim.PaymentId, order.Total, ct)).Success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogError(ex, "product_order_compensation_failed order={OrderId}", id);
            }
            if (refunded) await orders.CompensateAsync(id, claim.PaymentId, clock.GetUtcNow(), ct);
            else log?.LogError("product_order_compensation_failed order={OrderId}", id);
        }
        return Detail(actor, (await ReloadAsync(id, ct)).Value!, clock.GetUtcNow());
    }
}
