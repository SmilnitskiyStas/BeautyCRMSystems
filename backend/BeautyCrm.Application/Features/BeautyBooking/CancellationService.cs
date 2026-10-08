using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyBooking;

public sealed class CancellationService(
    IBookingStore store, IPaymentService payments, CancellationSettingsService settings, TimeProvider clock)
{
    public async Task<Result<CancelResult>> CancelAsync(Guid appointmentId, CancellationToken ct)
    {
        var appt = await store.GetAppointmentAsync(appointmentId, ct);
        if (appt is null) return Error.NotFound("appointment_not_found", "Appointment not found.");
        if (appt.Status == "cancelled") return Error.Conflict("already_cancelled", "Appointment is already cancelled.");
        if (appt.Status is "completed" or "no_show")
            return Error.Validation("cannot_cancel", $"Appointment with status '{appt.Status}' cannot be cancelled.");

        var now = clock.GetUtcNow();
        var payment = await store.GetPaymentAsync(appointmentId, ct);
        var paid = payment is { Status: "paid" } ? payment.Amount : 0m;
        var calc = CancellationPolicy.Calculate(appt.StartsAt, now, paid, await settings.GetAsync(ct));

        // 1) Атомарний claim статусу ПЕРЕД поверненням коштів: з двох паралельних cancel переможе рівно один.
        if (!await store.TryClaimCancelAsync(appointmentId, now, ct))
        {
            var current = await store.GetAppointmentAsync(appointmentId, ct);
            return current switch
            {
                null => Error.NotFound("appointment_not_found", "Appointment not found."),
                { Status: "cancelled" } => Error.Conflict("already_cancelled", "Appointment is already cancelled."),
                _ => Error.Validation("cannot_cancel", $"Appointment with status '{current.Status}' cannot be cancelled."),
            };
        }

        // 2) Повернення коштів; ключ ідемпотентності провайдера = payment.Id (повтор не поверне двічі).
        if (calc.Amount > 0 && payment is not null)
        {
            var r = await payments.RefundAsync(payment.Id, calc.Amount, ct);
            if (!r.Success)
            {
                await store.ReleaseCancelAsync(appointmentId, appt.Status, ct); // компенсація: запис лишається активним
                return Error.PaymentFailed("refund_failed", r.Error ?? "Refund failed.");
            }
            await store.UpdatePaymentAsync(payment.Id, "refunded", payment.ProviderPaymentId, null, ct);
        }

        var cancelled = await store.MarkCancelledAsync(appointmentId, now, ct);
        return cancelled is null
            ? Error.NotFound("appointment_not_found", "Appointment not found.")
            : new CancelResult(cancelled, calc.Amount, calc.Percent, calc.FeePercent);
    }
}
