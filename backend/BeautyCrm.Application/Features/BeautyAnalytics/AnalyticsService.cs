using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyAnalytics;

/// <summary>Revenue = сума price_final завершених записів; скасовані й no_show не враховуються.</summary>
public sealed record NetworkAnalytics(
    DateTimeOffset From, DateTimeOffset To, int Appointments, int Completed, int Cancelled, int NoShow,
    decimal Revenue, decimal AverageCheck, decimal CancellationRate, int NewClients);
public sealed record LocationAnalytics(
    Guid LocationId, string Name, int Appointments, int Completed, int Cancelled, decimal Revenue);
public sealed record PromotionAnalytics(
    Guid PromotionId, string Name, int Uses, decimal DiscountTotal, decimal Revenue);

public interface IAnalyticsStore
{
    Task<NetworkAnalytics> NetworkAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<LocationAnalytics>> LocationsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<PromotionAnalytics>> PromotionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public sealed class AnalyticsService(IAnalyticsStore store, TimeProvider clock)
{
    public async Task<Result<NetworkAnalytics>> NetworkAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        Range(from, to, out var f, out var t) is { } e ? e : await store.NetworkAsync(f, t, ct);

    public async Task<Result<IReadOnlyList<LocationAnalytics>>> LocationsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        Range(from, to, out var f, out var t) is { } e ? e : Wrap(await store.LocationsAsync(f, t, ct));

    public async Task<Result<IReadOnlyList<PromotionAnalytics>>> PromotionsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        Range(from, to, out var f, out var t) is { } e ? e : Wrap(await store.PromotionsAsync(f, t, ct));

    private static Result<IReadOnlyList<T>> Wrap<T>(IReadOnlyList<T> v) => new(v, null);

    /// <summary>За замовчуванням — останні 30 днів.</summary>
    private Error? Range(DateTimeOffset? from, DateTimeOffset? to, out DateTimeOffset f, out DateTimeOffset t)
    {
        t = to ?? clock.GetUtcNow();
        f = from ?? t.AddDays(-30);
        return f >= t ? Error.Validation("invalid_range", "from must be before to.") : null;
    }
}
