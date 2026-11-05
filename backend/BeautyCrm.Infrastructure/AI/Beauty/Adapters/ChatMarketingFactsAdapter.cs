using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.AI.Beauty.Adapters;

/// <summary>
/// TASK-782 (§26.9.3, «ШІ»): рядки маркетингу для блоку «ФАКТИ ЗАКЛАДУ». Формує КОД з БД свого tenant: правило про згоду (лише за
/// <c>Marketing:Enabled</c>) і дійсна знижка за купоном розмови (без коду купона: лише знижка, послуги, дата). Модель не бачить кодів
/// і не отримує підстав вигадувати знижки. Збій читання поглинається викликачем (<see cref="ChatContextBuilder"/>).
/// </summary>
public sealed class ChatMarketingFactsAdapter(
    BeautyDbContext db, TenantContext tenant, ChatButtonsOptions buttons, ChatCouponOffers offers, CouponService coupons, TimeProvider clock)
    : IChatMarketingFactsPort
{
    public async Task<IReadOnlyList<string>> GetLinesAsync(Guid tenantId, Guid? conversationId, Guid? locationId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var lines = new List<string>();
        if (buttons.MarketingEnabled) lines.Add("- Розсилки акцій — лише за згодою клієнта; керувати — /marketing.");
        if (conversationId is not { } cid || await offers.GetActiveAsync(cid, ct) is not { CouponId: { } couponId } offer) return lines;

        var clientId = await db.Conversations.AsNoTracking().Where(c => c.Id == cid).Select(c => c.ClientId).FirstOrDefaultAsync(ct);
        var resolved = await coupons.ResolveAsync(new CouponUse(CouponScope.Chat, CouponId: couponId, CampaignId: offer.CampaignId), clientId, clock.GetUtcNow(), ct);
        if (!resolved.IsOk) return lines;
        var view = await offers.DescribeAsync(resolved.Value!, locationId, ct);
        lines.Add($"- У клієнта активна знижка за купоном: {view.Discount} на {view.Services} до {view.Until}; ціну з купоном рахує система в підсумку.");
        return lines;
    }
}
