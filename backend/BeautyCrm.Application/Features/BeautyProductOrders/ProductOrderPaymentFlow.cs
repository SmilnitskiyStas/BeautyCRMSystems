using BeautyCrm.Application.Features.BeautyBooking;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyProductOrders;

/// <summary>
/// Спільні кроки після claim оплати (публічний P4 і CRM reconcile). Після успішного claim усе виконується з власним timeout-токеном,
/// а не з токеном запиту: обрив клієнтом не залишає списані кошти без фіналізації. Логи без ПД (лише тип винятку).
/// </summary>
internal static class ProductOrderPaymentFlow
{
    public static readonly TimeSpan PostClaimTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Відмова провайдера з явним кодом (card_declined...). Порожній код = результат невідомий.</summary>
    public static bool IsExplicitDecline(PaymentResult r) => !r.Success && !string.IsNullOrWhiteSpace(r.Error);

    /// <summary>
    /// Компенсація stock_unavailable: намір (коротка транзакція) -> RefundAsync -> CompensateAsync. Збій/виняток/timeout = лишаємо «в процесі»
    /// (платіж paid + маркер наміру), reconcile лише повторить повернення. true = замовлення refunded.
    /// </summary>
    public static async Task<bool> CompensateAsync(
        IProductOrderStore store, IPaymentService payments, ILogger? log, TimeProvider clock,
        Guid orderId, Guid paymentId, decimal total, DateTimeOffset claimStartedAt, bool intentAlreadyMarked)
    {
        using var cts = new CancellationTokenSource(PostClaimTimeout);
        try
        {
            if (!intentAlreadyMarked && !await store.BeginCompensationAsync(orderId, paymentId, claimStartedAt, clock.GetUtcNow(), cts.Token))
            {
                log?.LogError("product_order_compensation_failed order={OrderId} reason={Reason}", orderId, "claim_lost");
                return false;
            }
            var refund = await payments.RefundAsync(paymentId, total, cts.Token);
            if (!refund.Success)
            {
                log?.LogError("product_order_compensation_failed order={OrderId} reason={Reason}", orderId, "refund_not_confirmed");
                return false;
            }
            if (!await store.CompensateAsync(orderId, paymentId, claimStartedAt, clock.GetUtcNow(), cts.Token))
            {
                log?.LogError("product_order_compensation_failed order={OrderId} reason={Reason}", orderId, "state_changed");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.LogError("product_order_compensation_failed order={OrderId} error={Error}", orderId, ex.GetType().Name);
            return false;
        }
    }
}
