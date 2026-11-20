using System.Text.Json.Serialization;
using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Application.Features.BeautyProductOrders;

// CRM-частина замовлень товарів (ADR-026, контракт §33.13, TASK-903). Цін закупівлі в DTO немає. Телефон - лише за clients.contacts = full.

public sealed record OrderLocationRefDto(Guid Id, string Name);

public sealed record OrderCustomerDto(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phone,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PhoneMasked);

public sealed record ProductOrderSummaryDto(
    Guid Id, int Number, string Status, OrderLocationRefDto Location, OrderCustomerDto Customer, int LineCount, int ItemCount, decimal Total, string Currency,
    DateTimeOffset CreatedAt, DateTimeOffset? ReservationExpiresAt, DateTimeOffset? PaidAt, DateTimeOffset? FulfilledAt, DateTimeOffset? ClosedAt,
    string? CloseReason, bool PaymentInProgress, bool RefundInProgress);

public sealed record ProductOrderLineDto(ProductRef Product, string Name, string Unit, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record ProductOrderPaymentDto(string Status, int Attempts);

public sealed record ProductOrderDto(
    Guid Id, int Number, string Status, OrderLocationRefDto Location, OrderCustomerDto Customer, int LineCount, int ItemCount, decimal Total, string Currency,
    DateTimeOffset CreatedAt, DateTimeOffset? ReservationExpiresAt, DateTimeOffset? PaidAt, DateTimeOffset? FulfilledAt, DateTimeOffset? ClosedAt,
    string? CloseReason, bool PaymentInProgress, bool RefundInProgress,
    IReadOnlyList<ProductOrderLineDto> Lines, decimal? RefundAmount, string? CancelNote, InventoryNamedRef? FulfilledBy, InventoryNamedRef? CancelledBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ClientId,
    ProductOrderPaymentDto? Payment, Guid? SaleOperationId, Guid? ReturnOperationId, string Source);

public sealed record ProductOrderCancelDto(ProductOrderDto Order, decimal RefundAmount);

public sealed record ProductOrderCountsDto(int Paid);

public sealed record CancelProductOrderRequest(string? Reason);

public sealed record ProductOrderListQuery(string? Status, Guid? LocationId, string? From, string? To, string? Number, string? Cursor, int? Take);

// ---------- порт даних (CRM) ----------

public sealed record AdminOrderRow(
    Guid Id, int Number, string Status, Guid LocationId, string LocationName, string GuestName, string GuestPhone, Guid? ClientId, decimal Total,
    int LineCount, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset ReservationExpiresAt, DateTimeOffset? PaymentStartedAt, DateTimeOffset? RefundStartedAt,
    DateTimeOffset? PaidAt, DateTimeOffset? FulfilledAt, DateTimeOffset? ClosedAt, string? CloseReason);

public sealed record AdminOrderLine(ProductRef Product, string Name, string Unit, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record AdminOrderDetail(
    AdminOrderRow Row, IReadOnlyList<AdminOrderLine> Lines, decimal? RefundAmount, string? CancelNote, InventoryNamedRef? FulfilledBy, InventoryNamedRef? CancelledBy,
    string? PaymentStatus, int PaymentAttempts, Guid? PaymentId, Guid? SaleOperationId, Guid? ReturnOperationId, string Source);

/// <param name="LocationIds">null = без обмеження scope.</param>
public sealed record AdminOrderFilter(IReadOnlyList<Guid>? LocationIds, Guid? LocationId, string Status, int? Number, DateTimeOffset? From, DateTimeOffset? To, DateTimeOffset Now);

public enum RefundFinalizeKind { Refunded, NotPaid }

public interface IProductOrderAdminStore
{
    /// <summary>Сортування created_at DESC, id DESC; <paramref name="after"/> = [ticks, id]. Фільтр статусу - за ЕФЕКТИВНИМ статусом (§33.16).</summary>
    Task<IReadOnlyList<AdminOrderRow>> ListProductOrdersAsync(AdminOrderFilter filter, string[]? after, int take, CancellationToken ct);
    Task<int> CountPaidProductOrdersAsync(IReadOnlyList<Guid>? locationIds, int cap, CancellationToken ct);
    Task<AdminOrderDetail?> GetProductOrderDetailAsync(Guid id, CancellationToken ct);
    /// <summary>Умовний UPDATE: status = 'paid' AND refund_started_at IS NULL. true = видано цим викликом.</summary>
    Task<bool> FulfillProductOrderAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Claim повернення: status = 'paid' AND (refund_started_at IS NULL OR &lt; now - stale). Повертає значення claim (refund_started_at) або null.</summary>
    Task<DateTimeOffset?> ClaimProductOrderRefundAsync(Guid id, DateTimeOffset now, TimeSpan stale, CancellationToken ct);
    /// <summary>Скидає claim лише якщо refund_started_at == <paramref name="claimStartedAt"/> (CAS).</summary>
    Task<bool> ReleaseProductOrderRefundClaimAsync(Guid id, DateTimeOffset claimStartedAt, CancellationToken ct);
    /// <summary>Після успішного повернення провайдером: одна транзакція (замовлення -> рівні -> товари): sale_return у ті самі партії, платіж refunded, замовлення refunded.</summary>
    Task<RefundFinalizeKind> FinalizeProductOrderRefundAsync(Guid id, Guid userId, string? note, DateTimeOffset now, CancellationToken ct);
}
