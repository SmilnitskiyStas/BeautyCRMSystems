using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Application.Features.BeautyMarketing;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// Кампанії розсилок (TASK-779, §26.7, §26.11.3, §26.11.5, ADR-015): чернетка -> перегляд -> одне атомарне підтвердження (Idempotency-Key +
/// <c>revision</c> з перегляду, фіксація одержувачів) -> скасування, статистика. Лише owner/admin. Tenant - з <see cref="Actor"/> і RLS;
/// чужий id -> 404. Відправку веде воркер (він не обчислює сегмент і сам генерує персональні купони при постановці в чергу).
/// Логи: id, коди й лічильники - без текстів, кодів купонів, імен і ПД.
/// </summary>
public sealed class CampaignService(
    BeautyDbContext db, TenantContext tenant, AudienceService audience, IConfiguration config, CouponAttemptLimiter limiter,
    TimeProvider clock, ILogger<CampaignService> log)
{
    private int Cfg(string key, int fallback) => int.TryParse(config[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private bool CfgBool(string key) => bool.TryParse(config[key], out var b) && b;

    public const int DefaultTake = 50, MaxTake = 100;
    private static readonly string[] Types = [CampaignTypes.Promo, CampaignTypes.News];
    private static readonly string[] CouponModeList = [CouponModes.None, CouponModes.Personal, CouponModes.Shared];

    // ------------------------------------------------------------------ конфіг (§26.2)

    public bool Enabled => CfgBool("Marketing:Enabled");
    public int MaxRecipients => Math.Max(1, Cfg("Marketing:MaxRecipientsPerCampaign", 5000));
    public int MaxScheduleDaysAhead => Math.Max(1, Cfg("Marketing:MaxScheduleDaysAhead", 30));
    public int DailyLimit => Math.Max(1, Cfg("Marketing:DailyLimit", 200));
    public int UnsubscribeAttributionDays => Math.Max(1, Cfg("Marketing:UnsubscribeAttributionDays", 7));
    private int WriteLimit => Math.Max(1, Cfg("Marketing:WritePerUserPerMinute", 30));
    private int PreviewLimit => Math.Max(1, Cfg("Marketing:PreviewPerUserPerMinute", 20));
    private string Currency => config["ChatAssistant:CurrencyLabel"] is { Length: > 0 } c ? c.Trim() : "грн";

    private Error? Guard(Actor actor)
    {
        TenantGuard.Ensure(tenant, actor.TenantId);
        return actor.Role is Roles.Owner or Roles.Admin
            ? null
            : Error.Forbidden(ReminderContactsErrors.ForbiddenRole, "This action is not available for your role.");
    }

    private Error? RateWrite(Actor a) =>
        limiter.TryHit($"mkt-write:{a.UserId}", WriteLimit, TimeSpan.FromMinutes(1)) ? null : Error.TooMany("rate_limited", "Too many requests. Try again later.");

    private Error? RatePreview(Actor a) =>
        limiter.TryHit($"mkt-preview:{a.UserId}", PreviewLimit, TimeSpan.FromMinutes(1)) ? null : Error.TooMany("rate_limited", "Too many requests. Try again later.");

    private static Error NotFound() => Error.NotFound("campaign_not_found", "Campaign not found.");

    private Task LockAsync(Guid tenantId, Guid campaignId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"campaign:" + tenantId + ":" + campaignId}, 0))", ct);

    // ------------------------------------------------------------------ перегляд аудиторії без кампанії

    public async Task<Result<AudiencePreviewDto>> PreviewAudienceAsync(Actor actor, AudiencePreviewRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (RatePreview(actor) is { } limited) return limited;
        var type = req.Type ?? CampaignTypes.Promo;
        if (Array.IndexOf(Types, type) < 0) return Error.Validation("invalid_type", "type must be promo or news.");
        if (type == CampaignTypes.News && req.PromotionId is not null) return Error.Validation("invalid_type", "news campaigns take no promotion.");
        if (req.PromotionId is { } pid && !await db.Promotions.AsNoTracking().AnyAsync(p => p.Id == pid, ct))
            return Error.NotFound("promotion_not_found", "Promotion not found.");
        if (!AudienceService.TryNormalize(req.Segment, out var seg, out var why)) return Error.Validation("invalid_segment", why);
        if (await CheckRefsAsync(seg.ServiceId, req.LocationId, ct) is { } bad) return bad;
        var spec = new AudienceSpec(type, req.PromotionId, seg.Kind, seg.Days, seg.ServiceId, seg.MinVisits, seg.PeriodDays, req.LocationId);
        var r = await audience.ComputeAsync(spec, clock.GetUtcNow(), ct);
        return new AudiencePreviewDto(r.Matched, r.Eligible.Count, r.Excluded);
    }

    private async Task<Error?> CheckRefsAsync(Guid? serviceId, Guid? locationId, CancellationToken ct)
    {
        if (serviceId is { } s && !await db.Services.AsNoTracking().AnyAsync(x => x.Id == s, ct))
            return Error.NotFound("service_not_found", "Service not found.");
        if (locationId is { } l && !await db.Locations.AsNoTracking().AnyAsync(x => x.Id == l, ct))
            return Error.NotFound("location_not_found", "Location not found.");
        return null;
    }

    // ------------------------------------------------------------------ CRUD чернетки

    private sealed record Valid(CampaignRequest R, string Name, string Type, (string Kind, int Days, Guid? ServiceId, int MinVisits, int PeriodDays) Seg,
        DateTimeOffset? CouponTo);

    private async Task<Result<Valid>> ValidateAsync(CampaignRequest req, DateTimeOffset now, CancellationToken ct)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) return Error.Validation("invalid_name", "name must be 1..200 characters.");
        if (req.Type is not { } type || Array.IndexOf(Types, type) < 0) return Error.Validation("invalid_type", "type must be promo or news.");
        var mode = req.CouponMode ?? CouponModes.None;
        if (Array.IndexOf(CouponModeList, mode) < 0) return Error.Validation("invalid_coupon_mode", "couponMode must be none, personal or shared.");

        Promotion? promo = null;
        if (type == CampaignTypes.News)
        {
            if (req.PromotionId is not null) return Error.Validation("invalid_type", "news campaigns take no promotion.");
            if (mode != CouponModes.None) return Error.Validation("invalid_coupon_mode", "news campaigns take no coupons.");
        }
        else
        {
            if (req.PromotionId is not { } pid) return Error.Validation("promotion_required", "promotionId is required for promo campaigns.");
            promo = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (promo is null) return Error.Validation("promotion_not_found", "Promotion not found.");
            if (mode == CouponModes.None && promo.Access != "public")
                return Error.Validation("promotion_must_be_public", "A campaign without a coupon needs a public promotion.");
            if (mode != CouponModes.None && promo.Access != "code_only")
                return Error.Validation("promotion_not_code_only", "A campaign with a coupon needs a code_only promotion.");
        }

        if (!AudienceService.TryNormalize(req.Segment, out var seg, out var why)) return Error.Validation("invalid_segment", why);
        if (seg.ServiceId is { } sid && !await db.Services.AsNoTracking().AnyAsync(x => x.Id == sid, ct))
            return Error.Validation("service_not_found", "Service not found.");
        if (req.LocationId is { } lid && !await db.Locations.AsNoTracking().AnyAsync(x => x.Id == lid, ct))
            return Error.Validation("location_not_found", "Location not found.");

        if (CampaignTemplate.Validate(req.Template, type, mode) is { } reason) return Error.Validation("invalid_template", reason);

        DateTimeOffset? couponTo = null;
        switch (mode)
        {
            case CouponModes.None:
                if (req.SharedCouponId is not null || req.CouponValidFrom is not null || req.CouponValidTo is not null)
                    return Error.Validation("invalid_coupon_mode", "Coupon fields require couponMode personal or shared.");
                break;
            case CouponModes.Shared:
            {
                if (req.CouponValidFrom is not null || req.CouponValidTo is not null)
                    return Error.Validation("invalid_coupon_mode", "A shared coupon has its own period.");
                if (req.SharedCouponId is not { } cid) return Error.Validation("coupon_not_found", "sharedCouponId is required.");
                var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
                if (coupon is null) return Error.Validation("coupon_not_found", "Coupon not found.");
                if (coupon.Kind != "shared") return Error.Validation("coupon_not_shared", "The coupon is not a shared coupon.");
                if (coupon.PromotionId != promo!.Id) return Error.Validation("coupon_promotion_mismatch", "The coupon belongs to another promotion.");
                break;
            }
            default:
            {
                if (req.SharedCouponId is not null) return Error.Validation("invalid_coupon_mode", "sharedCouponId is only for shared coupons.");
                var from = req.CouponValidFrom;
                // персональний купон: строк кампанії; за замовчуванням період акції, без кінця акції - 30 днів від створення (§26.6)
                couponTo = req.CouponValidTo ?? promo!.EndsAt ?? now.AddDays(30);
                if (from is { } f && couponTo <= f) return Error.Validation("invalid_coupon_period", "couponValidTo must be after couponValidFrom.");
                if ((from is { } f2 && promo!.StartsAt is { } ps && f2 < ps) || (promo!.EndsAt is { } pe && couponTo > pe))
                    return Error.Validation("coupon_period_outside_promotion", "The coupon period must lie within the promotion period.");
                break;
            }
        }
        return new Valid(req, name, type, seg, couponTo);
    }

    private static void Apply(Campaign c, Valid v)
    {
        var r = v.R;
        c.Name = v.Name;
        c.Type = v.Type;
        c.PromotionId = v.Type == CampaignTypes.Promo ? r.PromotionId : null;
        c.SegmentKind = v.Seg.Kind;
        c.SegmentDays = v.Seg.Kind == SegmentKinds.Lapsed ? (short)v.Seg.Days : null;
        c.SegmentServiceId = v.Seg.Kind == SegmentKinds.Lapsed ? v.Seg.ServiceId : null;
        c.SegmentMinVisits = v.Seg.Kind == SegmentKinds.Frequent ? (short)v.Seg.MinVisits : null;
        c.SegmentPeriodDays = v.Seg.Kind == SegmentKinds.Frequent ? (short)v.Seg.PeriodDays : null;
        c.LocationId = r.LocationId;
        c.Template = r.Template!.Replace("\r\n", "\n").Trim();
        c.CouponMode = r.CouponMode ?? CouponModes.None;
        c.SharedCouponId = c.CouponMode == CouponModes.Shared ? r.SharedCouponId : null;
        c.CouponValidFrom = c.CouponMode == CouponModes.Personal ? r.CouponValidFrom?.ToUniversalTime() : null;
        c.CouponValidTo = c.CouponMode == CouponModes.Personal ? v.CouponTo?.ToUniversalTime() : null;
        c.ScheduledAt = r.ScheduledAt?.ToUniversalTime();
    }

    public async Task<Result<CampaignDto>> CreateAsync(Actor actor, CampaignRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (RateWrite(actor) is { } limited) return limited;
        var now = clock.GetUtcNow();
        var v = await ValidateAsync(req, now, ct);
        if (!v.IsOk) return v.Error!;
        var c = new Campaign { Id = Guid.NewGuid(), TenantId = actor.TenantId, Status = CampaignStatuses.Draft, Revision = 1, CreatedByUserId = actor.UserId };
        Apply(c, v.Value!);
        db.Campaigns.Add(c);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        log.LogInformation("campaign_created campaign={CampaignId} user={UserId}", c.Id, actor.UserId);
        return ToDto(await db.Campaigns.AsNoTracking().FirstAsync(x => x.Id == c.Id, ct));
    }

    public async Task<Result<CampaignDto>> UpdateAsync(Actor actor, Guid id, CampaignRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (RateWrite(actor) is { } limited) return limited;
        var now = clock.GetUtcNow();
        if (!await db.Campaigns.AsNoTracking().AnyAsync(x => x.Id == id, ct)) return NotFound(); // чужий/відсутній id - 404 до валідації тіла
        var v = await ValidateAsync(req, now, ct);
        if (!v.IsOk) return v.Error!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(actor.TenantId, id, ct);
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != CampaignStatuses.Draft) return Error.Conflict("campaign_not_draft", "Only a draft campaign can be edited.");
        Apply(c, v.Value!);
        c.Revision++;
        c.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        log.LogInformation("campaign_updated campaign={CampaignId} user={UserId}", id, actor.UserId);
        return ToDto(await db.Campaigns.AsNoTracking().FirstAsync(x => x.Id == id, ct));
    }

    public async Task<Result<bool>> DeleteAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (RateWrite(actor) is { } limited) return limited;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(actor.TenantId, id, ct);
        var status = await db.Campaigns.AsNoTracking().Where(x => x.Id == id).Select(x => x.Status).FirstOrDefaultAsync(ct);
        if (status is null) return NotFound();
        if (status != CampaignStatuses.Draft) return Error.Conflict("campaign_not_draft", "Only a draft campaign can be deleted.");
        await db.Campaigns.Where(x => x.Id == id && x.Status == CampaignStatuses.Draft).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("campaign_deleted campaign={CampaignId} user={UserId}", id, actor.UserId);
        return true;
    }

    public async Task<Result<CampaignDto>> GetAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? NotFound() : ToDto(c);
    }

    public async Task<Result<CampaignListDto>> ListAsync(Actor actor, string? status, string? cursor, int? take, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (status is not null && Array.IndexOf(CampaignStatuses.All, status) < 0) return Error.Validation("invalid_status", "Unknown status.");
        var n = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        var q = db.Campaigns.AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(c => c.Status == status);
        if (cursor is not null)
        {
            if (!long.TryParse(cursor, CultureInfo.InvariantCulture, out var ticks) || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
                return Error.Validation("invalid_cursor", "Invalid cursor.");
            var at = new DateTimeOffset(ticks, TimeSpan.Zero);
            q = q.Where(c => c.CreatedAt < at);
        }
        var rows = await q.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(n + 1)
            .Select(c => new
            {
                c.Id, c.Name, c.Type, c.Status, c.PromotionId, c.ScheduledAt, c.RecipientsTotal, c.CreatedAt,
                PromotionName = db.Promotions.Where(p => p.Id == c.PromotionId).Select(p => p.Name).FirstOrDefault(),
                Sent = db.CampaignRecipients.Count(r => r.CampaignId == c.Id && r.Status == "sent"),
            }).ToListAsync(ct);
        var page = rows.Take(n).ToList();
        var next = rows.Count > n ? page[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) : null;
        return new CampaignListDto(
            page.Select(r => new CampaignListItemDto(r.Id, r.Name, r.Type, r.Status,
                r.PromotionId is { } pid ? new NamedRef(pid, r.PromotionName ?? "") : null, r.ScheduledAt, r.RecipientsTotal, r.Sent, r.CreatedAt)).ToList(),
            next);
    }

    private static CampaignDto ToDto(Campaign c) => new(
        c.Id, c.Name, c.Type, c.Status, c.Revision, c.PromotionId,
        new SegmentDto(c.SegmentKind, c.SegmentDays, c.SegmentServiceId, c.SegmentMinVisits, c.SegmentPeriodDays), c.LocationId, c.Template,
        c.CouponMode, c.SharedCouponId, c.CouponValidFrom, c.CouponValidTo, c.ScheduledAt, c.RecipientsTotal, c.ConfirmedByUserId, c.ConfirmedAt,
        c.CancelledByUserId, c.CancelledAt, c.StartedAt, c.FinishedAt, c.CreatedByUserId, c.CreatedAt);

    private static AudienceSpec SpecOf(Campaign c) => new(
        c.Type, c.PromotionId, c.SegmentKind, c.SegmentDays ?? 0, c.SegmentServiceId, c.SegmentMinVisits ?? 0, c.SegmentPeriodDays ?? 0, c.LocationId);

    // ------------------------------------------------------------------ перегляд кампанії

    public async Task<Result<CampaignPreviewDto>> PreviewAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (RatePreview(actor) is { } limited) return limited;
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != CampaignStatuses.Draft) return Error.Conflict("campaign_not_draft", "Only a draft campaign can be previewed.");

        var now = clock.GetUtcNow();
        var a = await audience.ComputeAsync(SpecOf(c), now, ct);
        var daily = DailyLimit;
        var estimatedDays = a.Eligible.Count == 0 ? 0 : (int)Math.Ceiling(a.Eligible.Count / (double)daily);

        var promo = c.PromotionId is { } pid ? await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
        var services = promo is null
            ? []
            : await (from ps in db.PromotionServices.AsNoTracking() where ps.PromotionId == promo.Id
                     join s in db.Services.AsNoTracking() on ps.ServiceId equals s.Id orderby s.Name select s.Name).ToListAsync(ct);
        var shared = c.SharedCouponId is { } sc ? await db.Coupons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sc, ct) : null;
        var zone = await HomeZoneAsync(c.LocationId, ct);

        var (from, to) = c.CouponMode switch
        {
            CouponModes.Shared => ((DateTimeOffset?)shared?.ValidFrom, (DateTimeOffset?)shared?.ValidTo),
            CouponModes.Personal => (c.CouponValidFrom ?? promo?.StartsAt, c.CouponValidTo),
            _ => (promo?.StartsAt, promo?.EndsAt),
        };
        var code = c.CouponMode switch
        {
            CouponModes.Shared => shared?.Code,
            CouponModes.Personal => CouponCodes.Display("ABCDEFGHJK"),
            _ => null,
        };
        var withCoupon = c.CouponMode != CouponModes.None;
        var sample = CampaignTemplate.Render(new CampaignTemplate.SampleInput(
            c.Template, c.Type,
            promo is null ? "" : CampaignTemplate.FormatDiscount(promo.DiscountType == DiscountType.Percent ? "percent" : "fixed", promo.DiscountValue, Currency),
            promo is null ? "" : CampaignTemplate.FormatServices(services),
            from is { } f ? DateText(f, zone) : "сьогодні",
            to is { } t ? DateText(t.AddSeconds(-1), zone) : "без обмеження строку",
            code, Buttons: true));

        var start = c.ScheduledAt is { } s0 && s0 > now ? s0 : now;
        var warnings = new List<string>();
        if (promo?.EndsAt is { } pe && pe < start.AddDays(3 + estimatedDays)) warnings.Add("promotion_ends_soon");
        if (c.CouponMode != CouponModes.None && to is { } ct0 && ct0 <= start.AddDays(Math.Max(1, estimatedDays))) warnings.Add("coupon_expires_before_send");
        if (a.Eligible.Count > daily) warnings.Add("exceeds_daily_limit");
        if (!await AnyButtonsChannelAsync(ct)) warnings.Add("no_buttons_channel");

        return new CampaignPreviewDto(
            c.Revision, sample, [withCoupon ? "🎟 Записатися зі знижкою" : "📅 Записатися", "🔕 Не надсилати акції"], a.Matched, a.Eligible.Count,
            a.Excluded, new CampaignLimitsDto(daily, audience.ClientMinIntervalDays, estimatedDays), new CampaignWindowDto("09:00", "20:00"), warnings);
    }

    private async Task<bool> AnyButtonsChannelAsync(CancellationToken ct)
    {
        var settings = await db.Channels.AsNoTracking().Where(ch => ch.Type == ChannelType.Telegram && ch.IsActive).Select(ch => ch.Settings).ToListAsync(ct);
        foreach (var s in settings)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("assistant", out var a) && a.ValueKind == JsonValueKind.Object
                    && a.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True
                    && !(a.TryGetProperty("buttons", out var b) && b.ValueKind == JsonValueKind.False))
                    return true;
            }
            catch (JsonException)
            {
            }
        }
        return false;
    }

    private async Task<TimeZoneInfo> HomeZoneAsync(Guid? locationId, CancellationToken ct)
    {
        var tz = locationId is { } l ? await db.Locations.AsNoTracking().Where(x => x.Id == l).Select(x => x.Timezone).FirstOrDefaultAsync(ct) : null;
        tz ??= await db.Locations.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => x.Timezone).FirstOrDefaultAsync(ct);
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz ?? "Europe/Kyiv"); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static string DateText(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ підтвердження (human gate)

    private static string KeyHash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>
    /// Одна транзакція під advisory lock кампанії: перевірки -> CAS <c>draft -> scheduled</c> -> знімок одержувачів. Повтор того ж ключа й
    /// тіла - 200 з поточним станом; той самий ключ з іншим тілом - 409 <c>idempotency_conflict</c>; інший ключ після підтвердження -
    /// 409 <c>campaign_not_draft</c> (гонка двох підтверджень дає рівно одного переможця).
    /// </summary>
    public async Task<Result<CampaignDto>> ConfirmAsync(Actor actor, Guid id, string idempotencyKey, ConfirmCampaignRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (RateWrite(actor) is { } limited) return limited;
        if (req.Revision is not { } revision) return Error.Validation("revision_required", "revision from the preview is required.");
        var keyHash = KeyHash(idempotencyKey);
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(actor.TenantId, id, ct);
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();

        if (c.Status != CampaignStatuses.Draft)
        {
            if (c.ConfirmKeyHash != keyHash) return Error.Conflict("campaign_not_draft", "The campaign is no longer a draft.");
            var sameAt = req.ScheduledAt is not { } at || (c.ScheduledAt is { } sa && Math.Abs((at - sa).TotalMilliseconds) < 1);
            return revision == c.Revision && sameAt
                ? ToDto(c)
                : Error.Conflict("idempotency_conflict", "This Idempotency-Key was already used with a different request.");
        }
        if (revision != c.Revision) return Error.Conflict("revision_changed", "The campaign changed after the preview; preview it again.");

        var scheduled = req.ScheduledAt?.ToUniversalTime() ?? c.ScheduledAt ?? now;
        if (scheduled < now.AddMinutes(-5) || scheduled > now.AddDays(MaxScheduleDaysAhead))
            return Error.Validation("invalid_schedule", $"scheduledAt must be within the next {MaxScheduleDaysAhead} days.");
        if (scheduled < now) scheduled = now;

        if (c.PromotionId is { } pid)
        {
            var promo = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (promo is null || !promo.IsActive || (promo.EndsAt is { } pe && pe <= scheduled))
                return Error.Conflict("promotion_inactive", "The promotion is not active or ends before the campaign starts.");
            if (c.CouponMode == CouponModes.None ? promo.Access != "public" : promo.Access != "code_only")
                return Error.Validation(c.CouponMode == CouponModes.None ? "promotion_must_be_public" : "promotion_not_code_only", "The promotion access does not fit the coupon mode.");
            if (await db.Campaigns.AsNoTracking().AnyAsync(x => x.Id != id && x.PromotionId == pid
                    && (x.Status == CampaignStatuses.Scheduled || x.Status == CampaignStatuses.Sending), ct))
                return Error.Conflict("promotion_in_campaign", "The promotion is already used by another scheduled or sending campaign.");
        }
        if (c.CouponMode == CouponModes.Shared)
        {
            var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.SharedCouponId, ct);
            if (coupon is null || coupon.Status != "active" || coupon.ValidTo <= scheduled || (coupon.MaxUses is { } mu && coupon.UsesCount >= mu))
                return Error.Conflict("coupon_unavailable", "The shared coupon is revoked, expired or exhausted.");
        }
        else if (c.CouponMode == CouponModes.Personal && (c.CouponValidTo is not { } cvt || cvt <= scheduled))
            return Error.Conflict("coupon_unavailable", "The coupon period ends before the campaign starts.");

        var a = await audience.ComputeAsync(SpecOf(c), now, ct);
        if (a.Eligible.Count == 0) return Error.Conflict("audience_empty", "No eligible recipients.");
        if (a.Eligible.Count > MaxRecipients) return Error.Conflict("audience_too_large", $"More than {MaxRecipients} recipients.");

        var rows = await db.Campaigns.Where(x => x.Id == id && x.Status == CampaignStatuses.Draft && x.Revision == revision)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CampaignStatuses.Scheduled).SetProperty(x => x.ScheduledAt, scheduled)
                .SetProperty(x => x.ConfirmedByUserId, actor.UserId).SetProperty(x => x.ConfirmedAt, now).SetProperty(x => x.ConfirmKeyHash, keyHash)
                .SetProperty(x => x.RecipientsTotal, a.Eligible.Count).SetProperty(x => x.UpdatedAt, now), ct);
        if (rows == 0) return Error.Conflict("campaign_not_draft", "The campaign is no longer a draft.");

        foreach (var clientId in a.Eligible)
            db.CampaignRecipients.Add(new CampaignRecipient { Id = Guid.NewGuid(), TenantId = actor.TenantId, CampaignId = id, ClientId = clientId, Status = "pending" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        log.LogInformation("campaign_confirmed campaign={CampaignId} user={UserId} recipients={Recipients}", id, actor.UserId, a.Eligible.Count);
        return ToDto(await db.Campaigns.AsNoTracking().FirstAsync(x => x.Id == id, ct));
    }

    // ------------------------------------------------------------------ скасування

    /// <summary>
    /// Ідемпотентно. <c>pending</c> одержувачі -> <c>skipped:campaign_cancelled</c>; <c>queued</c> відсікає воркер перед відправкою
    /// (guard в outbox: повідомлення <c>failed</c>, одержувач <c>skipped</c>, персональний купон <c>revoked</c>); доставлені купони чинні.
    /// </summary>
    public async Task<Result<CampaignDto>> CancelAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (RateWrite(actor) is { } limited) return limited;
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(actor.TenantId, id, ct);
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status == CampaignStatuses.Cancelled) return ToDto(c);
        if (c.Status == CampaignStatuses.Done) return Error.Conflict("campaign_finished", "The campaign is already finished.");

        var rows = await db.Campaigns.Where(x => x.Id == id && (x.Status == CampaignStatuses.Draft || x.Status == CampaignStatuses.Scheduled || x.Status == CampaignStatuses.Sending))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CampaignStatuses.Cancelled).SetProperty(x => x.CancelledByUserId, actor.UserId)
                .SetProperty(x => x.CancelledAt, now).SetProperty(x => x.UpdatedAt, now), ct);
        if (rows == 0) return Error.Conflict("campaign_finished", "The campaign is already finished.");
        await db.CampaignRecipients.Where(r => r.CampaignId == id && r.Status == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "skipped").SetProperty(r => r.SkipReason, "campaign_cancelled").SetProperty(r => r.UpdatedAt, now), ct);
        await tx.CommitAsync(ct);
        log.LogInformation("campaign_cancelled campaign={CampaignId} user={UserId}", id, actor.UserId);
        return ToDto(await db.Campaigns.AsNoTracking().FirstAsync(x => x.Id == id, ct));
    }

    // ------------------------------------------------------------------ статистика й купони

    /// <summary>
    /// Записи кампанії = <c>campaign_id</c> запису або купон кампанії; «записи» не враховують скасовані через збій оплати (без автора скасування).
    /// Виручка = Σ <c>price_final</c> completed-записів кампанії. Список одержувачів не віддається. «Переглянуто» - завжди null.
    /// </summary>
    public async Task<Result<CampaignStatsDto>> StatsAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!await db.Campaigns.AsNoTracking().AnyAsync(x => x.Id == id, ct)) return NotFound();

        var groups = await db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id)
            .GroupBy(r => new { r.Status, r.SkipReason }).Select(g => new { g.Key.Status, g.Key.SkipReason, N = g.Count() }).ToListAsync(ct);
        int Sum(string status) => groups.Where(g => g.Status == status).Sum(g => g.N);
        var skipped = groups.Where(g => g.Status == "skipped").GroupBy(g => g.SkipReason ?? "unknown").ToDictionary(g => g.Key, g => g.Sum(x => x.N));
        var failedRows = groups.Where(g => g.Status == "failed").ToList();
        var failed = new CampaignFailedCounts(
            failedRows.Where(g => g.SkipReason == "bot_blocked").Sum(g => g.N), failedRows.Where(g => g.SkipReason == "chat_not_found").Sum(g => g.N),
            failedRows.Where(g => g.SkipReason is not ("bot_blocked" or "chat_not_found")).Sum(g => g.N));

        var clicks = await db.CampaignRecipients.AsNoTracking().CountAsync(r => r.CampaignId == id && r.ClickedAt != null, ct);

        var attributed = db.Appointments.AsNoTracking().Where(a => a.CampaignId == id
            || (a.CouponId != null && db.Coupons.Any(k => k.Id == a.CouponId && k.CampaignId == id)));
        var live = attributed.Where(a => !(a.Status == AppointmentStatus.Cancelled && a.CancelledByType == null));
        var bookings = await live.CountAsync(ct);
        var withCoupon = await live.CountAsync(a => a.CouponId != null, ct);
        var active = await db.CouponRedemptions.AsNoTracking().CountAsync(r => r.Status == "active" && attributed.Any(a => a.Id == r.AppointmentId), ct);
        var revenue = await attributed.Where(a => a.Status == AppointmentStatus.Completed).SumAsync(a => (decimal?)a.PriceFinal, ct) ?? 0m;

        var window = TimeSpan.FromDays(UnsubscribeAttributionDays);
        var sent = await db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id && r.SentAt != null)
            .Select(r => new { r.ClientId, SentAt = r.SentAt!.Value }).ToListAsync(ct);
        var sentClients = sent.Select(s => s.ClientId).ToList();
        var events = await db.MarketingConsentEvents.AsNoTracking()
            .Where(e => e.LevelTo == "none" && (e.CampaignId == id || sentClients.Contains(e.ClientId)))
            .Select(e => new { e.ClientId, e.CampaignId, e.CreatedAt }).ToListAsync(ct);
        var unsub = events.Where(e => e.CampaignId == id
                || sent.Any(s => s.ClientId == e.ClientId && e.CreatedAt >= s.SentAt && e.CreatedAt <= s.SentAt + window))
            .Select(e => e.ClientId).Distinct().Count();

        return new CampaignStatsDto(
            groups.Sum(g => g.N), new CampaignStatusCounts(Sum("pending"), Sum("queued"), Sum("sent"), Sum("failed"), Sum("skipped")), failed, skipped,
            clicks, bookings, withCoupon, active, revenue, unsub);
    }

    /// <summary>Лише кількості (без кодів): випущені купони кампанії, погашені (active), повернені (released), відкликані.</summary>
    public async Task<Result<CampaignCouponSummaryDto>> CouponSummaryAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!await db.Campaigns.AsNoTracking().AnyAsync(x => x.Id == id, ct)) return NotFound();
        var coupons = db.Coupons.AsNoTracking().Where(k => k.CampaignId == id);
        return new CampaignCouponSummaryDto(
            await coupons.CountAsync(ct),
            await db.CouponRedemptions.AsNoTracking().CountAsync(r => r.Status == "active" && coupons.Any(k => k.Id == r.CouponId), ct),
            await db.CouponRedemptions.AsNoTracking().CountAsync(r => r.Status == "released" && coupons.Any(k => k.Id == r.CouponId), ct),
            await coupons.CountAsync(k => k.Status == "revoked", ct));
    }
}
