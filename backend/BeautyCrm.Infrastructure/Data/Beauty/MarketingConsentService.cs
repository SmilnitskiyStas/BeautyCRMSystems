using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// TASK-777 (§26.4, ADR-015): маркетингова згода клієнта. Єдиний шлях запису - <see cref="SetConsentAsync"/>: в одній транзакції під
/// <c>pg_advisory_xact_lock</c> клієнта upsert стану + append-only подія + похідний <c>beauty_clients.marketing_consent</c>
/// (true лише для full/selective по telegram). Рішення про розсилку приймається за таблицею згод, не за прапорцем.
/// Tenant лише з <see cref="TenantContext"/> (RLS FORCE); чужий id -> 404. Якщо виклик уже в транзакції (чат-конвеєр), приєднується до неї.
/// Логи: id, коди й рівні; <c>basisNote</c>, імена й тексти не логуються.
/// </summary>
public sealed class MarketingConsentService(
    BeautyDbContext db, TenantContext tenant, IConfiguration config, TimeProvider clock, ILogger<MarketingConsentService> log)
{
    public const int MaxHistory = 50, MaxBasisNote = 200;
    private static readonly string[] Bases = ["written", "in_person", "other"];

    /// <summary><c>Marketing:AdminGrantEnabled</c> (за замовчуванням false): чи може адмін ПІДНІМАТИ згоду (з підставою).</summary>
    public bool AdminGrantEnabled => bool.TryParse(config["Marketing:AdminGrantEnabled"], out var on) && on;

    // ------------------------------------------------------------------ запис (єдина точка)

    public async Task<Result<ConsentChange>> SetConsentAsync(SetConsentCommand c, CancellationToken ct)
    {
        if (Validate(c) is { } invalid) return invalid;
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant is not resolved.");

        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var result = await SetCoreAsync(tenantId, c, ct);
        if (own is not null) await own.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// «Стоп» (§26.4): <c>none</c> (source=stop) для всіх наявних каналів і для telegram (навіть без рядка - для історії)
    /// + <c>unsubscribed=true</c> (існуюча логіка §22.7). Ідемпотентний.
    /// </summary>
    public async Task<Result<IReadOnlyList<ConsentChange>>> StopAsync(
        Guid clientId, string via = MarketingConsentVias.Command, Guid? conversationId = null, CancellationToken ct = default)
    {
        if (via is not (MarketingConsentVias.Button or MarketingConsentVias.Command or MarketingConsentVias.Text))
            return Error.Validation("invalid_via", "via must be button, command or text.");
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant is not resolved.");

        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockAsync(tenantId, clientId, ct);
        if (!await ClientExistsAsync(clientId, ct)) return ClientNotFound();

        var channels = await db.MarketingConsents.AsNoTracking().Where(m => m.ClientId == clientId).Select(m => m.Channel).ToListAsync(ct);
        if (!channels.Contains(MarketingConsentChannels.Telegram)) channels.Add(MarketingConsentChannels.Telegram);
        var changes = new List<ConsentChange>();
        foreach (var channel in channels.Order(StringComparer.Ordinal))
        {
            var r = await SetCoreAsync(tenantId, new SetConsentCommand(clientId, channel, MarketingConsentLevels.None,
                MarketingConsentSources.Stop, via, ConversationId: conversationId), ct, locked: true);
            if (!r.IsOk) return r.Error!;
            changes.Add(r.Value!);
        }
        await db.Clients.Where(x => x.Id == clientId && x.DeletedAt == null && !x.Unsubscribed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Unsubscribed, true), ct);
        if (own is not null) await own.CommitAsync(ct);
        log.LogInformation("marketing_stop client={ClientId} via={Via} changed={Changed}", clientId, via, changes.Count(x => x.Changed));
        return changes;
    }

    /// <summary>Пропозицію згоди показано: <c>beauty_clients.marketing_offered_at = now</c> (кулдаун <c>Marketing:OfferCooldownDays</c>). false = клієнта немає.</summary>
    public async Task<bool> MarkOfferedAsync(Guid clientId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Clients.Where(x => x.Id == clientId && x.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MarketingOfferedAt, (DateTimeOffset?)now), ct) > 0;
    }

    private static Error? Validate(SetConsentCommand c)
    {
        if (c.Channel is not (MarketingConsentChannels.Telegram or MarketingConsentChannels.Push))
            return Error.Validation(MarketingConsentErrors.ChannelUnsupported, "Unknown channel.");
        if (!MarketingConsentLevels.IsValid(c.Level))
            return Error.Validation(MarketingConsentErrors.InvalidLevel, "level must be full, selective or none.");
        switch (c.Source)
        {
            case MarketingConsentSources.Chat:
                if (c.Via is not (MarketingConsentVias.Button or MarketingConsentVias.Command or MarketingConsentVias.Text))
                    return Error.Validation("invalid_via", "Chat consent needs via button, command or text.");
                if (c.Level != MarketingConsentLevels.None && string.IsNullOrWhiteSpace(c.TextVersion))
                    return Error.Validation(MarketingConsentErrors.TextVersionRequired, "textVersion is required for a chat consent.");
                break;
            case MarketingConsentSources.Admin:
                if (c.ActorUserId is null) return Error.Validation("actor_required", "Admin change needs an actor.");
                if (c.Basis is not null && Array.IndexOf(Bases, c.Basis) < 0)
                    return Error.Validation(MarketingConsentErrors.InvalidBasis, "basis must be written, in_person or other.");
                if (c.Basis is not null && !ValidNote(c.BasisNote))
                    return Error.Validation(MarketingConsentErrors.InvalidBasisNote, $"basisNote must be 1..{MaxBasisNote} characters without control characters.");
                break;
            case MarketingConsentSources.Stop:
                if (c.Level != MarketingConsentLevels.None) return Error.Validation(MarketingConsentErrors.InvalidLevel, "Stop sets none only.");
                break;
            default:
                return Error.Validation("invalid_source", "source must be chat, admin or stop.");
        }
        if (c.Via is not (null or MarketingConsentVias.Button or MarketingConsentVias.Command or MarketingConsentVias.Text or MarketingConsentVias.Api))
            return Error.Validation("invalid_via", "Unknown via.");
        return null;
    }

    private static bool ValidNote(string? note) =>
        note is not null && note.Trim() is { Length: > 0 } t && t.EnumerateRunes().Count() <= MaxBasisNote && !t.Any(ch => char.IsControl(ch));

    private async Task<Result<ConsentChange>> SetCoreAsync(Guid tenantId, SetConsentCommand c, CancellationToken ct, bool locked = false)
    {
        if (!locked)
        {
            await LockAsync(tenantId, c.ClientId, ct);
            if (!await ClientExistsAsync(c.ClientId, ct)) return ClientNotFound();
        }
        var current = await db.MarketingConsents.AsNoTracking().Where(m => m.ClientId == c.ClientId && m.Channel == c.Channel).FirstOrDefaultAsync(ct);
        var from = current?.Level;
        if (from == c.Level) return new ConsentChange(false, from, c.Level); // ідемпотентно: нічого не пишеться

        var basis = c.Source == MarketingConsentSources.Admin ? c.Basis : null;
        var note = basis is null ? null : c.BasisNote!.Trim();
        if (c.Source == MarketingConsentSources.Admin && MarketingConsentLevels.Rank(c.Level) > MarketingConsentLevels.Rank(from))
        {
            if (basis is null) return Error.Validation(MarketingConsentErrors.BasisRequired, "Raising a consent needs a basis and a note.");
            if (!AdminGrantEnabled) return Error.Conflict(MarketingConsentErrors.AdminGrantDisabled, "Admins cannot raise a client's marketing consent.");
        }

        var now = clock.GetUtcNow();
        var actor = c.Source == MarketingConsentSources.Admin ? c.ActorUserId : null;
        var version = c.Source == MarketingConsentSources.Admin ? null : c.TextVersion;
        if (current is null)
        {
            db.MarketingConsents.Add(new MarketingConsent
            {
                TenantId = tenantId, ClientId = c.ClientId, Channel = c.Channel, Level = c.Level, Source = c.Source, TextVersion = version,
                UpdatedByUserId = actor, ConversationId = c.ConversationId, CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            await db.MarketingConsents.Where(m => m.ClientId == c.ClientId && m.Channel == c.Channel)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Level, c.Level).SetProperty(m => m.Source, c.Source)
                    .SetProperty(m => m.TextVersion, version).SetProperty(m => m.UpdatedByUserId, actor)
                    .SetProperty(m => m.ConversationId, c.ConversationId).SetProperty(m => m.UpdatedAt, now), ct);
        }
        db.MarketingConsentEvents.Add(new MarketingConsentEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClientId = c.ClientId, Channel = c.Channel, LevelFrom = from, LevelTo = c.Level,
            Source = c.Source, Via = c.Via, TextVersion = version, ActorUserId = actor, Basis = basis, BasisNote = note,
            CampaignId = c.CampaignId, ConversationId = c.ConversationId, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        DetachConsentEntries();

        // Похідний прапорець: true лише для full/selective по telegram. Push його не змінює.
        if (c.Channel == MarketingConsentChannels.Telegram)
        {
            var derived = c.Level != MarketingConsentLevels.None;
            var resubscribe = c.Source == MarketingConsentSources.Chat && derived;
            var client = db.Clients.Where(x => x.Id == c.ClientId && x.DeletedAt == null);
            await client.ExecuteUpdateAsync(s => s.SetProperty(x => x.MarketingConsent, derived), ct);
            // явне підвищення клієнтом скидає unsubscribed (остання явна воля); вподобання нагадувань не змінюється
            if (resubscribe) await client.ExecuteUpdateAsync(s => s.SetProperty(x => x.Unsubscribed, false), ct);
        }
        log.LogInformation("marketing_consent_changed client={ClientId} channel={Channel} from={From} to={To} source={Source} via={Via} user={UserId}",
            c.ClientId, c.Channel, from ?? "-", c.Level, c.Source, c.Via ?? "-", actor?.ToString() ?? "-");
        return new ConsentChange(true, from, c.Level);
    }

    private void DetachConsentEntries()
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is MarketingConsent or MarketingConsentEvent).ToList())
            e.State = EntityState.Detached;
    }

    private async Task LockAsync(Guid tenantId, Guid clientId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"marketing-consent:" + tenantId + ":" + clientId}, 0))", ct);

    private Task<bool> ClientExistsAsync(Guid clientId, CancellationToken ct) =>
        db.Clients.AsNoTracking().AnyAsync(x => x.Id == clientId && x.DeletedAt == null, ct);

    private static Error ClientNotFound() => Error.NotFound(MarketingConsentErrors.ClientNotFound, "Client not found.");

    // ------------------------------------------------------------------ читання (картка клієнта)

    /// <summary>Поточний стан + історія (≤ 50, нові першими). Рядка немає = <c>state=unset</c>, level none. Не залежить від ролі (виклик після Guard).</summary>
    public async Task<Result<MarketingConsentDto>> GetConsentAsync(Guid clientId, bool includeBasisNote, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().Where(x => x.Id == clientId && x.DeletedAt == null)
            .Select(x => new { x.MarketingOfferedAt }).FirstOrDefaultAsync(ct);
        if (client is null) return ClientNotFound();

        var rows = await db.MarketingConsents.AsNoTracking().Where(m => m.ClientId == clientId)
            .Select(m => new { m.Channel, m.Level, m.Source, m.TextVersion, m.UpdatedAt, m.UpdatedByUserId }).ToListAsync(ct);
        var events = await db.MarketingConsentEvents.AsNoTracking().Where(e => e.ClientId == clientId)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(MaxHistory)
            .Select(e => new { e.CreatedAt, e.Channel, e.LevelFrom, e.LevelTo, e.Source, e.Via, e.ActorUserId, e.Basis, e.BasisNote, e.CampaignId }).ToListAsync(ct);

        var userIds = rows.Where(r => r.UpdatedByUserId != null).Select(r => r.UpdatedByUserId!.Value)
            .Concat(events.Where(e => e.ActorUserId != null).Select(e => e.ActorUserId!.Value)).Distinct().ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, UserRefDto>()
            : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new UserRefDto(u.Id, u.FullName)).ToDictionaryAsync(u => u.Id, ct);
        UserRefDto? User(Guid? id) => id is { } g ? users.GetValueOrDefault(g) : null;

        var channels = new List<MarketingConsentChannelDto>();
        foreach (var ch in new[] { MarketingConsentChannels.Telegram, MarketingConsentChannels.Push })
        {
            var r = rows.FirstOrDefault(x => x.Channel == ch);
            if (r is not null)
                channels.Add(new MarketingConsentChannelDto(ch, r.Level, "set", r.Source, r.TextVersion, r.UpdatedAt, User(r.UpdatedByUserId)));
            else if (ch == MarketingConsentChannels.Telegram)
                channels.Add(new MarketingConsentChannelDto(ch, MarketingConsentLevels.None, "unset", null, null, null, null));
        }
        var telegram = await db.Conversations.AsNoTracking()
            .AnyAsync(cv => cv.ClientId == clientId && cv.Channel!.Type == ChannelType.Telegram && cv.Channel.IsActive, ct);
        var history = events.Select(e => new MarketingConsentEventDto(e.CreatedAt, e.Channel, e.LevelFrom, e.LevelTo, e.Source, e.Via,
            User(e.ActorUserId), e.Basis, includeBasisNote ? e.BasisNote : null, e.CampaignId)).ToList();
        return new MarketingConsentDto(channels, client.MarketingOfferedAt, telegram, history);
    }

    // ------------------------------------------------------------------ staff API (owner/admin)

    private Error? Guard(Actor actor)
    {
        TenantGuard.Ensure(tenant, actor.TenantId);
        return actor.Role is Roles.Owner or Roles.Admin
            ? null
            : Error.Forbidden(MarketingConsentErrors.ForbiddenRole, "This action is not available for your role.");
    }

    public async Task<Result<MarketingConsentDto>> GetAsync(Actor actor, Guid clientId, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        return await GetConsentAsync(clientId, includeBasisNote: actor.Role == Roles.Owner, ct);
    }

    /// <summary>PUT картки: source=admin, via=api. Зниження - завжди; підвищення - лише з підставою й <c>Marketing:AdminGrantEnabled=true</c>.</summary>
    public async Task<Result<MarketingConsentDto>> PutAsync(Actor actor, Guid clientId, PutMarketingConsentRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!await ClientExistsAsync(clientId, ct)) return ClientNotFound();
        if (req.Channel != MarketingConsentChannels.Telegram)
            return Error.Validation(MarketingConsentErrors.ChannelUnsupported, "Only the telegram channel is supported.");
        if (!MarketingConsentLevels.IsValid(req.Level))
            return Error.Validation(MarketingConsentErrors.InvalidLevel, "level must be full, selective or none.");

        var set = await SetConsentAsync(new SetConsentCommand(clientId, req.Channel, req.Level!, MarketingConsentSources.Admin,
            MarketingConsentVias.Api, ActorUserId: actor.UserId, Basis: req.Basis, BasisNote: req.BasisNote), ct);
        if (!set.IsOk) return set.Error!;
        return await GetConsentAsync(clientId, includeBasisNote: actor.Role == Roles.Owner, ct);
    }
}
