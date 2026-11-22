namespace BeautyCrm.Application.Features.BeautyBooking;

/// <summary>
/// Результат списання. <paramref name="Pending"/> = провайдер прийняв запит, але оплату завершує клієнт на сторінці провайдера
/// (ADR-028: рахунок monobank); <paramref name="CheckoutUrl"/> - куди його відправити (null при перевірці вже створеного рахунку).
/// Остаточний результат приходить вебхуком або наступним reconcile.
/// </summary>
public sealed record PaymentResult(bool Success, string? ProviderPaymentId, string? Error, bool Pending = false, string? CheckoutUrl = null)
{
    public static PaymentResult AwaitingCustomer(string providerPaymentId, string? checkoutUrl) => new(false, providerPaymentId, null, true, checkoutUrl);
}
public sealed record RefundResult(bool Success, string? ProviderRefundId, string? Error);

/// <summary>Що оплачується: запис (передоплата) або замовлення товарів (ADR-026 §2.7).</summary>
public enum PaymentPurpose { Appointment, ProductOrder }

/// <summary>
/// Запит на списання. <paramref name="PaymentId"/> (beauty_payments.id) є ключем ідемпотентності: повторний виклик з тим самим id
/// не списує кошти вдруге (реальний провайдер передає його як Idempotency-Key). <paramref name="SubjectId"/> - запис або замовлення.
/// <paramref name="TenantSlug"/> - для адреси вебхука провайдера з переадресацією (ADR-028); null = новий рахунок не створюється.
/// </summary>
public sealed record PaymentCharge(Guid PaymentId, PaymentPurpose Purpose, Guid SubjectId, decimal Amount, string Currency, string? TenantSlug = null);

/// <summary>Збережений платіж (beauty_payments) для провайдера: зв'язок PaymentId -> рахунок провайдера. Під RLS поточного tenant.</summary>
public sealed record StoredPaymentRef(Guid PaymentId, Guid? AppointmentId, Guid? ProductOrderId, decimal Amount, string Status, string? Provider, string? ProviderPaymentId);

/// <summary>Порт для провайдерів з рахунком (ADR-028): знайти платіж і прив'язати до нього id рахунку провайдера.</summary>
public interface IPaymentRefStore
{
    Task<StoredPaymentRef?> GetPaymentRefAsync(Guid paymentId, CancellationToken ct);

    /// <summary>Лише для pending-платежу без прив'язаного рахунку; false = платіж уже має рахунок або не pending.</summary>
    Task<bool> AttachProviderRefAsync(Guid paymentId, string provider, string providerPaymentId, CancellationToken ct);
}

/// <summary>Єдина точка оплати (картка = онлайн-передоплата). Реальний провайдер підміняє mock у DI.</summary>
public interface IPaymentService
{
    Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct);

    /// <summary>
    /// ADR-026 §2.7: списання за <see cref="PaymentCharge"/>, ідемпотентне за <c>PaymentId</c>. За замовчуванням `not_supported`
    /// (провайдер без підтримки замовлень товарів відмовляє, а не списує мовчки).
    /// </summary>
    Task<PaymentResult> ChargeAsync(PaymentCharge charge, CancellationToken ct) =>
        Task.FromResult(new PaymentResult(false, null, "not_supported"));

    /// <summary><paramref name="paymentId"/> (beauty_payments.id) є ключем ідемпотентності повернення: повторний виклик з тим самим id
    /// не повинен повертати кошти вдруге (реальний провайдер передає його як Idempotency-Key).</summary>
    Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct);
}

/// <summary>Mock: запис - завжди успіх; замовлення товарів - успіх, окрім сум з копійками «.13» (відтворення payment_failed для QA).</summary>
public sealed class MockPaymentService : IPaymentService
{
    public Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct) =>
        Task.FromResult(new PaymentResult(true, $"mock_pay_{appointmentId:N}", null));

    public Task<PaymentResult> ChargeAsync(PaymentCharge charge, CancellationToken ct)
    {
        if (charge.Purpose == PaymentPurpose.ProductOrder && IsDeclinedAmount(charge.Amount))
            return Task.FromResult(new PaymentResult(false, null, "card_declined"));
        return Task.FromResult(new PaymentResult(true, $"mock_pay_{charge.PaymentId:N}", null));
    }

    /// <summary>Копійки рівно 13 (100.13, 7.13).</summary>
    public static bool IsDeclinedAmount(decimal amount) => decimal.Round(amount * 100m, 0) % 100m == 13m;

    public Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct) =>
        Task.FromResult(new RefundResult(true, $"mock_refund_{paymentId:N}", null));
}
