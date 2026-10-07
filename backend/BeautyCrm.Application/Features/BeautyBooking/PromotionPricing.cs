namespace BeautyCrm.Application.Features.BeautyBooking;

/// <summary>Порожні LocationIds/ServiceIds = акція діє на всі заклади/послуги.</summary>
public sealed record PromotionRule(
    Guid Id, string Name, string DiscountType, decimal DiscountValue, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    bool IsActive, IReadOnlyCollection<Guid> LocationIds, IReadOnlyCollection<Guid> ServiceIds);

public sealed record PriceQuote(decimal Original, decimal Final, Guid? PromotionId, string? PromotionName);

public static class PromotionPricing
{
    public static bool Applies(PromotionRule p, Guid locationId, Guid serviceId, DateTimeOffset at) =>
        p.IsActive
        && (p.StartsAt is null || p.StartsAt <= at)
        && (p.EndsAt is null || at < p.EndsAt)
        && (p.LocationIds.Count == 0 || p.LocationIds.Contains(locationId))
        && (p.ServiceIds.Count == 0 || p.ServiceIds.Contains(serviceId));

    public static decimal Discount(PromotionRule p, decimal price)
    {
        var d = p.DiscountType == "percent" ? price * p.DiscountValue / 100m : p.DiscountValue;
        return Math.Min(Math.Max(d, 0m), price);
    }

    /// <summary>Акції не складаються: береться та, що дає найбільшу знижку.</summary>
    public static PriceQuote Quote(decimal original, Guid locationId, Guid serviceId, DateTimeOffset at, IEnumerable<PromotionRule> rules)
    {
        PromotionRule? best = null;
        var bestDiscount = 0m;
        foreach (var r in rules.Where(r => Applies(r, locationId, serviceId, at)))
        {
            var d = Discount(r, original);
            if (d > bestDiscount) { best = r; bestDiscount = d; }
        }
        var final = Math.Round(original - bestDiscount, 2, MidpointRounding.AwayFromZero);
        return new PriceQuote(original, final, best?.Id, best?.Name);
    }
}
