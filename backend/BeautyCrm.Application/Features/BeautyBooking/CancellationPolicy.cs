namespace BeautyCrm.Application.Features.BeautyBooking;

/// <param name="Amount">Сума повернення клієнту.</param>
/// <param name="Percent">Відсоток повернення за вікном (до комісії).</param>
/// <param name="FeePercent">Застосована комісія, % від суми повернення (0, якщо deductFee вимкнено).</param>
public sealed record RefundCalculation(decimal Amount, int Percent, int FeePercent);

/// <summary>
/// Розрахунок повернення за налаштуваннями tenant-а (TASK-685; раніше A2 — константи 12 год / 50%).
/// Межа вікна включна: <c>startsAt - now &lt;= WindowHours</c> (включно з уже розпочатим візитом) -> <c>RefundPercentInWindow</c>,
/// інакше <c>RefundPercentOutside</c>.
/// Формула: refund = paid * percent/100 * (100 - fee)/100, де fee = FeePercent лише при DeductFee.
/// Округлення — до копійок ВНИЗ (до нуля): клієнту ніколи не повертається більше за розрахункове.
/// Множення виконується до ділення (без проміжного округлення).
/// </summary>
public static class CancellationPolicy
{
    public static RefundCalculation Calculate(DateTimeOffset startsAt, DateTimeOffset now, decimal amountPaid, CancellationSettings s)
    {
        var inWindow = startsAt - now <= TimeSpan.FromHours(s.WindowHours);
        var percent = inWindow ? s.RefundPercentInWindow : s.RefundPercentOutside;
        var fee = s.DeductFee ? s.FeePercent : 0;
        if (amountPaid <= 0) return new RefundCalculation(0m, percent, fee);

        var amount = Math.Round(amountPaid * percent * (100 - fee) / 10_000m, 2, MidpointRounding.ToZero);
        return new RefundCalculation(amount, percent, fee);
    }
}
