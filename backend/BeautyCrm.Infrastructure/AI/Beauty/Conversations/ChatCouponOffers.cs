using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>Пропозиція розмови після кнопки кампанії <c>co</c> / коду текстом (§26.9.3): <c>offer_*</c> у <c>beauty_conversations</c>.</summary>
public sealed record ChatOffer(Guid? CampaignId, Guid? CouponId, DateTimeOffset Until);

/// <summary>Людський опис знижки купона для шаблонів: «15%», «манікюр, педикюр», «30.11.2026».</summary>
public sealed record OfferView(string Discount, string Services, string Until);

/// <summary>
/// TASK-782 (§26.6, §26.9.3): пропозиція розмови зі знижкою. Купон у запис передає КОД (не модель): <see cref="PrepareAsync"/> бере
/// дійсну пропозицію (<c>offer_until &gt; now</c>) і додає <c>CouponId/CampaignId</c> до <see cref="ChatPrepareRequest"/>. Код купона тут
/// не з'являється: працюємо з id. Недійсний купон при підготовці запису знімає пропозицію й дає звичайну ціну.
/// </summary>
public sealed class ChatCouponOffers(BeautyDbContext db, CouponOptions options, ChatPromptOptions prompt, TimeProvider clock)
{
    public async Task<ChatOffer?> GetActiveAsync(Guid conversationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId && c.OfferUntil != null && c.OfferUntil > now)
            .Select(c => new ChatOffer(c.OfferCampaignId, c.OfferCouponId, c.OfferUntil!.Value)).FirstOrDefaultAsync(ct);
    }

    /// <summary>min(now + ConversationHoldHours, кінець купона | акції).</summary>
    public DateTimeOffset HoldUntil(DateTimeOffset now, DateTimeOffset? couponEnd)
    {
        var hold = now.AddHours(options.ConversationHoldHours);
        return couponEnd is { } end && end < hold ? end : hold;
    }

    public Task SetAsync(Guid conversationId, Guid? campaignId, Guid? couponId, DateTimeOffset until, CancellationToken ct) =>
        db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.OfferCampaignId, campaignId).SetProperty(c => c.OfferCouponId, couponId)
            .SetProperty(c => c.OfferUntil, (DateTimeOffset?)until.ToUniversalTime()), ct);

    public Task ClearAsync(Guid conversationId, CancellationToken ct) =>
        db.Conversations.Where(c => c.Id == conversationId && (c.OfferUntil != null || c.OfferCouponId != null || c.OfferCampaignId != null))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OfferCampaignId, (Guid?)null).SetProperty(c => c.OfferCouponId, (Guid?)null)
                .SetProperty(c => c.OfferUntil, (DateTimeOffset?)null), ct);

    public async Task<OfferView> DescribeAsync(CouponCandidate coupon, Guid? locationId, CancellationToken ct)
    {
        var ids = coupon.Rule.ServiceIds.ToList();
        var names = ids.Count == 0 ? [] : await db.Services.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct);
        string? tz = locationId is { } lid ? await db.Locations.AsNoTracking().Where(l => l.Id == lid).Select(l => l.Timezone).FirstOrDefaultAsync(ct) : null;
        var end = coupon.Rule.EndsAt ?? clock.GetUtcNow().AddHours(options.ConversationHoldHours);
        return new OfferView(ChatTemplates.DiscountText(coupon.Rule.DiscountType, coupon.Rule.DiscountValue, prompt.CurrencyLabel),
            ChatTemplates.ServicesText(names), ChatTemplates.UntilDate(end, tz));
    }

    /// <summary>
    /// <see cref="ChatBookingService.PrepareAsync"/> з купоном пропозиції розмови. Якщо купон при підготовці вже недійсний
    /// (<c>coupon_invalid|coupon_locked</c>) - пропозиція знімається, підсумок рахується за звичайною ціною.
    /// </summary>
    public async Task<Result<ChatPrepared>> PrepareAsync(ChatBookingService booking, ChatPrepareRequest req, CancellationToken ct)
    {
        if (req.CouponId is not null || await GetActiveAsync(req.ConversationId, ct) is not { CouponId: { } couponId } offer)
            return await booking.PrepareAsync(req, ct);
        var r = await booking.PrepareAsync(req with { CouponId = couponId, CampaignId = offer.CampaignId }, ct);
        if (r.IsOk || r.Error!.Code is not (CouponService.Invalid or CouponService.Locked)) return r;
        await ClearAsync(req.ConversationId, ct);
        return await booking.PrepareAsync(req, ct);
    }
}
