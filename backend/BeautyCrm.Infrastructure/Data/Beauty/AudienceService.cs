using BeautyCrm.Application.Features.BeautyMarketing;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>Нормалізований сегмент (дефолти підставлені): lapsed 60 днів; frequent 3 completed за 90 днів.</summary>
public sealed record AudienceSpec(
    string Type, Guid? PromotionId, string Kind, int Days, Guid? ServiceId, int MinVisits, int PeriodDays, Guid? LocationId);

/// <summary>Результат: придатні клієнти (лише для підтвердження, в API не віддаються) і кількості.</summary>
public sealed record AudienceResult(int Matched, IReadOnlyList<Guid> Eligible, AudienceExclusions Excluded);

/// <summary>
/// Аудиторії (§26.5, ADR-015 §2.2): сегмент -> придатність. Один запит і для перегляду, і для підтвердження. Усе в .NET (EF/Npgsql під RLS tenant-а).
/// Придатний клієнт: не видалений, має розмову в активному Telegram-каналі, активну згоду потрібного рівня (selective - лише promo
/// з перетином послуг із «улюбленими»; news - лише full), не відписаний («Стоп» = none) і не отримував розсилку впродовж інтервалу частоти.
/// Порядок причин виключення: noTelegram, unsubscribed, noConsent, levelMismatch, frequency (клієнт рахується за першою).
/// </summary>
public sealed class AudienceService(BeautyDbContext db, IConfiguration config)
{
    private int Cfg(string key, int fallback) => int.TryParse(config[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private bool CfgBool(string key) => bool.TryParse(config[key], out var b) && b;

    public const int DefaultLapsedDays = 60, DefaultMinVisits = 3, DefaultPeriodDays = 90;

    public int FavoriteServicesDays => Math.Clamp(Cfg("Marketing:FavoriteServicesDays", 365), 30, 1095);
    public int ClientMinIntervalDays => Math.Clamp(Cfg("Marketing:ClientMinIntervalDays", 7), 1, 60);

    /// <summary>Перевірка й нормалізація сегмента (<c>invalid_segment</c>); false - помилка (причина в <paramref name="error"/>).</summary>
    public static bool TryNormalize(SegmentDto? seg, out (string Kind, int Days, Guid? ServiceId, int MinVisits, int PeriodDays) n, out string error)
    {
        n = default;
        error = "";
        if (seg?.Kind is not { } kind || Array.IndexOf(SegmentKinds.All, kind) < 0)
        {
            error = "segment.kind must be lapsed, frequent, new or all_consented";
            return false;
        }
        switch (kind)
        {
            case SegmentKinds.Lapsed:
                if (seg.MinVisits is not null || seg.PeriodDays is not null) { error = "minVisits and periodDays are not used by lapsed"; return false; }
                var days = seg.Days ?? DefaultLapsedDays;
                if (days is < 1 or > 730) { error = "days must be 1..730"; return false; }
                n = (kind, days, seg.ServiceId, 0, 0);
                return true;
            case SegmentKinds.Frequent:
                if (seg.Days is not null || seg.ServiceId is not null) { error = "days and serviceId are not used by frequent"; return false; }
                var min = seg.MinVisits ?? DefaultMinVisits;
                var period = seg.PeriodDays ?? DefaultPeriodDays;
                if (min is < 1 or > 100) { error = "minVisits must be 1..100"; return false; }
                if (period is < 1 or > 730) { error = "periodDays must be 1..730"; return false; }
                n = (kind, 0, null, min, period);
                return true;
            default:
                if (seg.Days is not null || seg.ServiceId is not null || seg.MinVisits is not null || seg.PeriodDays is not null)
                {
                    error = "this segment kind takes no parameters";
                    return false;
                }
                n = (kind, 0, null, 0, 0);
                return true;
        }
    }

    public async Task<AudienceResult> ComputeAsync(AudienceSpec s, DateTimeOffset now, CancellationToken ct)
    {
        var matchedIds = await MatchSegmentAsync(s, now, ct);
        if (matchedIds.Count == 0) return new AudienceResult(0, [], new AudienceExclusions(0, 0, 0, 0, 0));

        var telegram = (await db.Conversations.AsNoTracking()
            .Where(c => c.ClientId != null && matchedIds.Contains(c.ClientId.Value) && c.Channel!.Type == ChannelType.Telegram && c.Channel.IsActive)
            .Select(c => c.ClientId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var levels = (await db.MarketingConsents.AsNoTracking()
            .Where(m => m.Channel == "telegram" && matchedIds.Contains(m.ClientId))
            .Select(m => new { m.ClientId, m.Level }).ToListAsync(ct)).ToDictionary(x => x.ClientId, x => x.Level);

        // TASK-790 (L2): як у воркері (consentVerdict) - beauty_clients.unsubscribed або вподобання нагадувань none теж виключають («unsubscribed»)
        var optedOut = (await db.Clients.AsNoTracking().Where(c => matchedIds.Contains(c.Id) && c.Unsubscribed).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        optedOut.UnionWith(await db.ClientContactPreferences.AsNoTracking().Where(p => p.Method == "none" && matchedIds.Contains(p.ClientId))
            .Select(p => p.ClientId).ToListAsync(ct));

        // «улюблені» послуги - лише для selective у promo-кампанії з обмеженням послуг акції
        HashSet<(Guid Client, Guid Service)> favorites = [];
        HashSet<Guid> promoServices = [];
        if (s.Type == CampaignTypes.Promo && s.PromotionId is { } pid)
        {
            promoServices = (await db.PromotionServices.AsNoTracking().Where(x => x.PromotionId == pid).Select(x => x.ServiceId).ToListAsync(ct)).ToHashSet();
            var selectiveIds = levels.Where(kv => kv.Value == "selective").Select(kv => kv.Key).ToList();
            if (promoServices.Count > 0 && selectiveIds.Count > 0)
            {
                var since = now.AddDays(-FavoriteServicesDays);
                var psList = promoServices.ToList();
                favorites = (await db.Appointments.AsNoTracking()
                    .Where(a => a.Status == AppointmentStatus.Completed && a.StartsAt > since && selectiveIds.Contains(a.ClientId) && psList.Contains(a.ServiceId))
                    .Select(a => new { a.ClientId, a.ServiceId }).Distinct().ToListAsync(ct)).Select(x => (x.ClientId, x.ServiceId)).ToHashSet();
            }
        }

        var freqSince = now.AddDays(-ClientMinIntervalDays);
        var recent = (await db.CampaignRecipients.AsNoTracking()
            .Where(r => (r.Status == "queued" || r.Status == "sent") && r.QueuedAt > freqSince && matchedIds.Contains(r.ClientId))
            .Select(r => r.ClientId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var key in await db.Messages.AsNoTracking()
                     .Where(m => m.IdempotencyKey != null && m.IdempotencyKey.StartsWith("winback:") && m.CreatedAt > freqSince && m.Status != "failed")
                     .Select(m => m.IdempotencyKey!).ToListAsync(ct))
        {
            var parts = key.Split(':');
            if (parts.Length >= 2 && Guid.TryParse(parts[1], out var cid)) recent.Add(cid);
        }

        int noTelegram = 0, unsubscribed = 0, noConsent = 0, levelMismatch = 0, frequency = 0;
        var eligible = new List<Guid>();
        foreach (var id in matchedIds)
        {
            if (!telegram.Contains(id)) { noTelegram++; continue; }
            if (!levels.TryGetValue(id, out var level)) { noConsent++; continue; }
            if (level == "none" || optedOut.Contains(id)) { unsubscribed++; continue; }
            if (level == "selective"
                && (s.Type != CampaignTypes.Promo || (promoServices.Count > 0 && !promoServices.Any(sv => favorites.Contains((id, sv))))))
            {
                levelMismatch++;
                continue;
            }
            if (recent.Contains(id)) { frequency++; continue; }
            eligible.Add(id);
        }
        return new AudienceResult(matchedIds.Count, eligible, new AudienceExclusions(noTelegram, unsubscribed, noConsent, levelMismatch, frequency));
    }

    /// <summary>Сегмент (§26.5): межі днів абсолютні (N × 24 год від now), включні для «давності».</summary>
    private async Task<List<Guid>> MatchSegmentAsync(AudienceSpec s, DateTimeOffset now, CancellationToken ct)
    {
        var completed = db.Appointments.AsNoTracking().Where(a => a.Status == AppointmentStatus.Completed);
        var scoped = s.LocationId is { } loc ? completed.Where(a => a.LocationId == loc) : completed;
        var future = db.Appointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed) && a.StartsAt > now);
        var clients = db.Clients.AsNoTracking().Where(c => c.DeletedAt == null);
        var hasLoc = s.LocationId is not null;

        IQueryable<Client> q;
        switch (s.Kind)
        {
            case SegmentKinds.Lapsed when s.ServiceId is { } svc:
            {
                var cutoff = (DateTimeOffset?)now.AddDays(-s.Days);
                q = clients.Where(c => scoped.Where(a => a.ClientId == c.Id && a.ServiceId == svc).Max(a => (DateTimeOffset?)a.StartsAt) <= cutoff
                    && !future.Any(a => a.ClientId == c.Id && a.ServiceId == svc));
                break;
            }
            case SegmentKinds.Lapsed:
            {
                var cutoff = (DateTimeOffset?)now.AddDays(-s.Days);
                q = clients.Where(c => completed.Where(a => a.ClientId == c.Id).Max(a => (DateTimeOffset?)a.StartsAt) <= cutoff
                    && !future.Any(a => a.ClientId == c.Id)
                    && (!hasLoc || scoped.Any(a => a.ClientId == c.Id)));
                break;
            }
            case SegmentKinds.Frequent:
            {
                var since = now.AddDays(-s.PeriodDays);
                var min = s.MinVisits;
                q = clients.Where(c => scoped.Count(a => a.ClientId == c.Id && a.StartsAt > since) >= min);
                break;
            }
            case SegmentKinds.New:
                q = clients.Where(c => completed.Count(a => a.ClientId == c.Id) == 1 && (!hasLoc || scoped.Any(a => a.ClientId == c.Id)));
                break;
            default:
                q = clients.Where(c => !hasLoc || scoped.Any(a => a.ClientId == c.Id));
                break;
        }
        return await q.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
    }
}
