namespace BeautyCrm.Application.Features.BeautyBooking;

/// <summary>Порожні LocationIds/ServiceIds = акція діє на всі заклади/послуги.</summary>
public sealed record PromotionRule(
    Guid Id, string Name, string DiscountType, decimal DiscountValue, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    bool IsActive, IReadOnlyCollection<Guid> LocationIds, IReadOnlyCollection<Guid> ServiceIds);

public sealed record PriceQuote(decimal Original, decimal Final, Guid? PromotionId, string? PromotionName);

/// <summary>
/// Купон як кандидат у ціноутворення (§26.6). <paramref name="Rule"/> - акція купона з періодом, звуженим до строку купона
/// (StartsAt = max, EndsAt = min), і IsActive = акція активна ∧ купон active ∧ є вільні використання.
/// </summary>
public sealed record CouponCandidate(Guid CouponId, string Kind, string CodeHint, string PromotionName, Guid? CampaignId, PromotionRule Rule);

public static class CouponOutcomes
{
    public const string Applied = "applied";
    public const string NotApplicable = "not_applicable";
    public const string NotBetter = "not_better";
    public const string None = "none";
}

/// <param name="Outcome">applied | not_applicable | not_better | none (купона немає).</param>
public sealed record CouponQuote(PriceQuote Quote, Guid? CouponId, string Outcome);

/// <summary>Погашення в транзакції створення запису: CAS uses_count + рядок погашення (§26.6). <paramref name="At"/> - момент перевірки строку.</summary>
public sealed record NewCouponRedemption(Guid CouponId, Guid? CampaignId, decimal DiscountAmount, DateTimeOffset At);

/// <summary>Купон на записі (адитивно в AppointmentDto): код не віддається, лише останні 3 символи.</summary>
public sealed record AppointmentCouponDto(Guid Id, string Kind, string CodeHint, string PromotionName, string Outcome);

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

    /// <summary>
    /// Ціна з необов'язковим купоном (§26.6). <paramref name="publicRules"/> - лише акції access=public. Одна знижка на запис:
    /// береться більша, купон перемагає лише СТРОГО меншою фінальною ціною (інакше не споживається: not_better / not_applicable).
    /// </summary>
    public static CouponQuote QuoteWithCoupon(
        decimal original, Guid locationId, Guid serviceId, DateTimeOffset at, IEnumerable<PromotionRule> publicRules, CouponCandidate? coupon)
    {
        var baseQuote = Quote(original, locationId, serviceId, at, publicRules);
        if (coupon is null) return new CouponQuote(baseQuote, null, CouponOutcomes.None);
        if (!Applies(coupon.Rule, locationId, serviceId, at)) return new CouponQuote(baseQuote, coupon.CouponId, CouponOutcomes.NotApplicable);

        var couponFinal = Math.Round(original - Discount(coupon.Rule, original), 2, MidpointRounding.AwayFromZero);
        return couponFinal < baseQuote.Final
            ? new CouponQuote(new PriceQuote(original, couponFinal, coupon.Rule.Id, coupon.PromotionName), coupon.CouponId, CouponOutcomes.Applied)
            : new CouponQuote(baseQuote, coupon.CouponId, CouponOutcomes.NotBetter);
    }
}
