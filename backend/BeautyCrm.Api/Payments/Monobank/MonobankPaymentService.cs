using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyProductOrders;

namespace BeautyCrm.Api.Payments.Monobank;

/// <summary>
/// <see cref="IPaymentService"/> через рахунки monobank acquiring (ADR-028), лише для замовлень товарів. Передоплата запису лишається на mock
/// (поза першим релізом). Ідемпотентність за PaymentId: id рахунку прив'язується до beauty_payments; повторний Charge того самого PaymentId
/// не створює другий рахунок, а читає статус наявного. Повернення - «Скасування оплати» з extRef = PaymentId.
/// </summary>
public sealed class MonobankPaymentService(
    MonobankClient api, IPaymentRefStore refs, MonobankOptions options, MockPaymentService fallback, ILogger<MonobankPaymentService> log) : IPaymentService
{
    // Запис (ADR-028 §scope): синхронне списання лишається mock.
    public Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct) => fallback.ChargeAsync(appointmentId, amount, ct);

    public async Task<PaymentResult> ChargeAsync(PaymentCharge charge, CancellationToken ct)
    {
        if (charge.Purpose != PaymentPurpose.ProductOrder) return await fallback.ChargeAsync(charge, ct);
        if (!string.Equals(charge.Currency, "UAH", StringComparison.OrdinalIgnoreCase)) return new(false, null, "unsupported_currency");

        var stored = await refs.GetPaymentRefAsync(charge.PaymentId, ct);
        if (stored?.ProviderPaymentId is { } existing) return await StatusAsync(existing, ct);
        if (charge.TenantSlug is not { } slug) return new(false, null, "invoice_not_created"); // reconcile з CRM без рахунку: явна відмова, клієнт повторить

        MonobankCreatedInvoice invoice;
        try
        {
            invoice = await api.CreateInvoiceAsync(
                ProductOrderPaymentNotifications.ToMinor(charge.Amount), charge.PaymentId.ToString("N"), "Оплата замовлення",
                options.RedirectUrl, options.WebhookUrl(slug), options.InvoiceValiditySeconds, ct);
        }
        catch (MonobankRejectedException ex)
        {
            log.LogError("monobank_invoice_rejected payment={PaymentId} status={Status} code={Code}", charge.PaymentId, (int)ex.Status, ex.ErrCode);
            return new(false, null, "provider_rejected");
        }
        // інші винятки (мережа/5xx/timeout) летять далі: результат невідомий, claim лишається, reconcile створить рахунок наново

        if (!await refs.AttachProviderRefAsync(charge.PaymentId, MonobankOptions.ProviderName, invoice.InvoiceId, CancellationToken.None))
        {
            // платіж уже має рахунок (паралельний reconcile) або не pending: цей рахунок клієнту не показуємо
            log.LogWarning("monobank_invoice_attach_lost payment={PaymentId}", charge.PaymentId);
            return new(false, null, null); // невідомо -> «в процесі»
        }
        log.LogInformation("monobank_invoice_created payment={PaymentId} test={Test}", charge.PaymentId, options.TestMode);
        return PaymentResult.AwaitingCustomer(invoice.InvoiceId, invoice.PageUrl);
    }

    private async Task<PaymentResult> StatusAsync(string invoiceId, CancellationToken ct)
    {
        MonobankInvoiceStatus s;
        try
        {
            s = await api.GetStatusAsync(invoiceId, ct);
        }
        catch (MonobankRejectedException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            return new(false, invoiceId, "invoice_not_found");
        }
        return MapState(s.Status) switch
        {
            ProviderPaymentState.Paid => new(true, invoiceId, null),
            ProviderPaymentState.Failed => new(false, invoiceId, s.Status == "expired" ? "invoice_expired" : "card_declined"),
            _ => PaymentResult.AwaitingCustomer(invoiceId, null),
        };
    }

    /// <summary>Статуси рахунку: created, processing, hold -> Pending; success -> Paid; failure, expired, reversed -> Failed.</summary>
    public static ProviderPaymentState MapState(string? status) => status switch
    {
        "success" => ProviderPaymentState.Paid,
        "failure" or "expired" or "reversed" => ProviderPaymentState.Failed,
        _ => ProviderPaymentState.Pending,
    };

    public async Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct)
    {
        var stored = await refs.GetPaymentRefAsync(paymentId, ct);
        if (stored is null) return new(false, null, "payment_not_found");
        // запис і платежі, проведені до перемикання на monobank, повертає mock
        if (stored.ProductOrderId is null || stored.ProviderPaymentId is not { } invoiceId || invoiceId.StartsWith("mock_", StringComparison.Ordinal))
            return await fallback.RefundAsync(paymentId, amount, ct);

        var extRef = paymentId.ToString("N");
        var status = await api.GetStatusAsync(invoiceId, ct);
        if (status.Status == "reversed") return new(true, extRef, null); // уже повернено (повтор після збою)
        if (status.Status != "success") return new(false, null, "not_refundable");

        try
        {
            var r = await api.CancelAsync(invoiceId, extRef, ProductOrderPaymentNotifications.ToMinor(amount), ct);
            return r.Status switch
            {
                "success" or "processing" => new(true, extRef, null), // processing: повернення прийнято банком
                "failure" => new(false, null, "refund_failed"),
                _ => new(false, null, null), // невідомо -> повтор
            };
        }
        catch (MonobankRejectedException ex)
        {
            log.LogError("monobank_refund_rejected payment={PaymentId} status={Status} code={Code}", paymentId, (int)ex.Status, ex.ErrCode);
            return new(false, null, "refund_failed");
        }
    }
}
