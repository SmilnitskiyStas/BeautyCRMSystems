using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyBooking;

/// <summary>
/// Налаштування скасування tenant-а (TASK-685). Відсутній рядок у БД = <see cref="Defaults"/>.
/// <c>FeePercent</c> діє лише при <c>DeductFee = true</c>.
/// </summary>
public sealed record CancellationSettings(
    int WindowHours, int RefundPercentInWindow, int RefundPercentOutside, bool DeductFee, int FeePercent)
{
    public const int MaxWindowHours = 24 * 30;

    public static readonly CancellationSettings Defaults = new(12, 50, 100, false, 0);
}

/// <summary>Умови скасування для UI запису (слоти / запис), щоб текст не хардкодився на клієнті.</summary>
public sealed record CancellationTerms(
    int WindowHours, int RefundPercentInWindow, int RefundPercentOutside, bool DeductFee, int FeePercent)
{
    public static CancellationTerms From(CancellationSettings s) =>
        new(s.WindowHours, s.RefundPercentInWindow, s.RefundPercentOutside, s.DeductFee, s.DeductFee ? s.FeePercent : 0);
}

/// <summary>PUT-тіло: усі поля обов'язкові (відсутнє поле = 422, а не мовчазний 0%).</summary>
public sealed record UpdateCancellationSettingsRequest(
    int? WindowHours, int? RefundPercentInWindow, int? RefundPercentOutside, bool? DeductFee, int? FeePercent);

/// <summary>Порт даних. Реалізація — Infrastructure (beauty_cancellation_settings, RLS за tenant).</summary>
public interface ICancellationSettingsStore
{
    /// <summary>Null — tenant ще не налаштовував (діють значення за замовчуванням).</summary>
    Task<CancellationSettings?> GetCancellationSettingsAsync(CancellationToken ct);
    Task SaveCancellationSettingsAsync(CancellationSettings settings, CancellationToken ct);
}

public sealed class CancellationSettingsService(ICancellationSettingsStore store)
{
    public async Task<CancellationSettings> GetAsync(CancellationToken ct) =>
        await store.GetCancellationSettingsAsync(ct) ?? CancellationSettings.Defaults;

    public async Task<CancellationTerms> GetTermsAsync(CancellationToken ct) => CancellationTerms.From(await GetAsync(ct));

    public async Task<Result<CancellationSettings>> UpdateAsync(UpdateCancellationSettingsRequest r, CancellationToken ct)
    {
        if (r.WindowHours is null || r.RefundPercentInWindow is null || r.RefundPercentOutside is null
            || r.DeductFee is null || r.FeePercent is null)
            return Error.Validation("settings_incomplete",
                "windowHours, refundPercentInWindow, refundPercentOutside, deductFee and feePercent are required.");
        if (r.WindowHours is < 0 or > CancellationSettings.MaxWindowHours)
            return Error.Validation("invalid_window_hours", $"windowHours must be 0..{CancellationSettings.MaxWindowHours}.");
        if (r.RefundPercentInWindow is < 0 or > 100 || r.RefundPercentOutside is < 0 or > 100)
            return Error.Validation("invalid_refund_percent", "Refund percent must be 0..100.");
        if (r.FeePercent is < 0 or > 100)
            return Error.Validation("invalid_fee_percent", "feePercent must be 0..100.");

        var settings = new CancellationSettings(
            r.WindowHours.Value, r.RefundPercentInWindow.Value, r.RefundPercentOutside.Value, r.DeductFee.Value, r.FeePercent.Value);
        await store.SaveCancellationSettingsAsync(settings, ct);
        return settings;
    }
}
