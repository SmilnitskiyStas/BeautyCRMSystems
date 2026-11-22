using BeautyCrm.Application.Features.BeautyBooking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyProductOrders;

/// <summary>Нормалізований статус рахунку провайдера (ADR-028): Paid - гроші списано, Failed - остаточна відмова/строк минув, Pending - ще ні.</summary>
public enum ProviderPaymentState { Pending, Paid, Failed }

/// <summary>Перевірене (підпис провайдера) повідомлення про зміну статусу рахунку. <paramref name="AmountMinor"/> - сума в копійках.</summary>
public sealed record ProviderPaymentNotification(Guid PaymentId, string ProviderPaymentId, ProviderPaymentState State, long? AmountMinor);

public enum NotificationOutcome { Applied, AlreadyApplied, Ignored }

/// <summary>
/// Вебхук провайдера оплати для замовлень товарів (ADR-028). Повідомлення - лише тригер до тих самих переходів, що й P4/reconcile
/// (FinalizePaidAsync / MarkPaymentFailedAsync з CAS за чинним claim), тому повтор, перестановка і гонка з reconcile безпечні:
/// списання не більше одного, фіналізація не більше однієї. Tenant уже визначено (RLS). Логи без ПД, сум і токенів.
/// </summary>
public sealed class ProductOrderPaymentNotifications(
    IProductOrderStore store, IPaymentRefStore refs, IPaymentService payments, TimeProvider clock, ILogger<ProductOrderPaymentNotifications> log)
{
    public async Task<NotificationOutcome> HandleAsync(ProviderPaymentNotification n, CancellationToken ct)
    {
        var p = await refs.GetPaymentRefAsync(n.PaymentId, ct);
        if (p is not { ProductOrderId: { } orderId }) return Ignore(n, "unknown_payment");
        if (!string.Equals(p.ProviderPaymentId, n.ProviderPaymentId, StringComparison.Ordinal)) return Ignore(n, "invoice_mismatch");
        if (n.AmountMinor is { } minor && minor != ToMinor(p.Amount)) return Ignore(n, "amount_mismatch", error: true);
        if (n.State == ProviderPaymentState.Pending) return NotificationOutcome.Ignored;

        // Після перевірки - власний timeout: провайдер може обірвати з'єднання, а фіналізація має дійти до кінця.
        using var cts = new CancellationTokenSource(ProductOrderPaymentFlow.PostClaimTimeout);
        var order = await store.GetOrderAsync(orderId, cts.Token);
        if (order is null) return Ignore(n, "unknown_order");

        if (n.State == ProviderPaymentState.Failed)
        {
            if (order is { Status: "pending_payment", PaymentStartedAt: { } claimAt } && p.Status == "pending"
                && await store.MarkPaymentFailedAsync(orderId, p.PaymentId, claimAt, cts.Token))
            {
                log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", orderId, order.PaymentAttempts, "failed");
                return NotificationOutcome.Applied;
            }
            return NotificationOutcome.AlreadyApplied;
        }

        // Paid
        if (p.Status == "refunded") return NotificationOutcome.AlreadyApplied; // компенсація/повернення вже завершені
        if (order.Status is "paid" or "fulfilled" or "refunded" && order.PaymentId == p.PaymentId) return NotificationOutcome.AlreadyApplied;
        if (order is { Status: "pending_payment", PaymentStartedAt: { } startedAt })
        {
            var fin = await store.FinalizePaidAsync(orderId, p.PaymentId, startedAt, n.ProviderPaymentId, clock.GetUtcNow(), cts.Token);
            switch (fin.Kind)
            {
                case FinalizeKind.Paid:
                    log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", orderId, fin.Order?.PaymentAttempts ?? order.PaymentAttempts, "paid");
                    return NotificationOutcome.Applied;
                case FinalizeKind.AlreadyPaid:
                    return NotificationOutcome.AlreadyApplied;
                case FinalizeKind.StockUnavailable:
                    log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", orderId, order.PaymentAttempts, "stock_unavailable");
                    await ProductOrderPaymentFlow.CompensateAsync(store, payments, log, clock, orderId, p.PaymentId, order.Total, startedAt, false);
                    return NotificationOutcome.Applied;
                case FinalizeKind.ClaimLost:
                    // claim перехопив reconcile (він дозавершить тим самим PaymentId) або платіж уже в компенсації
                    return NotificationOutcome.AlreadyApplied;
            }
        }

        // Гроші списано, але замовлення вже закрите / не чекає цієї оплати: не залишаємо їх у салоні - повертаємо 100% (ідемпотентно за PaymentId).
        log.LogError("product_order_payment_orphan order={OrderId} status={Status}", orderId, order.Status);
        try
        {
            var refund = await payments.RefundAsync(p.PaymentId, p.Amount, cts.Token);
            if (!refund.Success) log.LogError("product_order_compensation_failed order={OrderId} reason={Reason}", orderId, "orphan_refund_not_confirmed");
        }
        catch (Exception ex)
        {
            log.LogError("product_order_compensation_failed order={OrderId} error={Error}", orderId, ex.GetType().Name);
        }
        return NotificationOutcome.Applied;
    }

    public static long ToMinor(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private NotificationOutcome Ignore(ProviderPaymentNotification n, string reason, bool error = false)
    {
        if (error) log.LogError("payment_webhook_ignored payment={PaymentId} reason={Reason}", n.PaymentId, reason);
        else log.LogWarning("payment_webhook_ignored payment={PaymentId} reason={Reason}", n.PaymentId, reason);
        return NotificationOutcome.Ignored;
    }
}

public static class ProductOrderPaymentNotificationsExtensions
{
    public static IServiceCollection AddProductOrderPaymentNotifications(this IServiceCollection services) =>
        services.AddScoped<ProductOrderPaymentNotifications>();
}
