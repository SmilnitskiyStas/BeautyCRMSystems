using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyAnalytics;

public sealed record MyRevenueLocation(Guid LocationId, string Name, int Completed, decimal Revenue);

/// <summary>§30.9.7: власний виторг майстра за період (лише completed власного specialist_id, без собівартості й виплат).</summary>
public sealed record MyRevenueDto(
    string FromDate, string ToDate, Guid? SpecialistId, int Completed, decimal Revenue, IReadOnlyList<MyRevenueLocation> ByLocation);

/// <summary>Порт даних: completed записи майстра за локальними датами закладів, згруповані по закладу.</summary>
public interface IMyRevenueStore
{
    Task<IReadOnlyList<MyRevenueLocation>> ByLocationAsync(Guid specialistId, DateOnly from, DateOnly to, CancellationToken ct);
}

public sealed class MyRevenueService(IMyRevenueStore store)
{
    public const int MaxDays = 92;

    public async Task<Result<MyRevenueDto>> GetAsync(Actor actor, string? from, string? to, CancellationToken ct)
    {
        if (!TryParse(from, out var f) || !TryParse(to, out var t))
            return Error.Validation("invalid_range", "from and to are required (yyyy-MM-dd).");
        if (t < f || t.DayNumber - f.DayNumber + 1 > MaxDays)
            return Error.Validation("invalid_range", $"to must not precede from and the range must not exceed {MaxDays} days.");
        if (f.Year is < 2000 or > 2100 || t.Year is < 2000 or > 2100)
            return Error.Validation("invalid_range", "The year must be within 2000..2100.");

        var fromText = f.ToString("yyyy-MM-dd");
        var toText = t.ToString("yyyy-MM-dd");
        // без прив'язки до майстра або з неактивним профілем (M7): порожня відповідь, не помилка
        if (actor.SpecialistId is not { } sp || actor.Access.SpecialistInactive)
            return new MyRevenueDto(fromText, toText, actor.SpecialistId, 0, 0m, []);

        var rows = await store.ByLocationAsync(sp, f, t, ct);
        return new MyRevenueDto(fromText, toText, sp, rows.Sum(r => r.Completed), rows.Sum(r => r.Revenue), rows);
    }

    private static bool TryParse(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
}
