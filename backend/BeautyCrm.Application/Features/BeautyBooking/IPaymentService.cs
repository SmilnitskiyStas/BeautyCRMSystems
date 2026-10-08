namespace BeautyCrm.Application.Features.BeautyBooking;

public sealed record PaymentResult(bool Success, string? ProviderPaymentId, string? Error);
public sealed record RefundResult(bool Success, string? ProviderRefundId, string? Error);

/// <summary>Єдина точка оплати (картка = онлайн-передоплата). Реальний провайдер підміняє mock у DI.</summary>
public interface IPaymentService
{
    Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct);
    /// <summary><paramref name="paymentId"/> (beauty_payments.id) є ключем ідемпотентності повернення: повторний виклик з тим самим id
    /// не повинен повертати кошти вдруге (реальний провайдер передає його як Idempotency-Key).</summary>
    Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct);
}

/// <summary>Mock: завжди успіх, детерміновані id.</summary>
public sealed class MockPaymentService : IPaymentService
{
    public Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct) =>
        Task.FromResult(new PaymentResult(true, $"mock_pay_{appointmentId:N}", null));

    public Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct) =>
        Task.FromResult(new RefundResult(true, $"mock_refund_{paymentId:N}", null));
}
