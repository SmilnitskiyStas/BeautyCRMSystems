using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

public sealed record StaffChannelRef(Guid Id, string Name);
public sealed record StaffRevokeInfo(string Reason, DateTimeOffset At);

/// <summary>§32.24.8: без chat_id, username і токена.</summary>
public sealed record TelegramLinkStatusDto(
    bool FeatureEnabled, string State, StaffChannelRef? Channel, DateTimeOffset? ExpiresAt, DateTimeOffset? ActivatedAt,
    DateTimeOffset? LastDeliveryAt, StaffRevokeInfo? LastRevoke, string ConsentVersion);

public sealed record IssueTelegramLinkRequest(bool? Consent, string? ConsentVersion, Guid? ChannelId);
public sealed record TelegramLinkIssuedDto(Guid LinkId, string DeepLink, DateTimeOffset ExpiresAt);

/// <summary>
/// Прив'язка Telegram власника (етап 2b, ADR-025 §12, §32.24.3/§32.24.8): API адмінки. Дія завжди над власним user_id актора.
/// Бот-сторона (claim/confirm/stop) - <see cref="StaffChatGate"/>.
/// </summary>
public sealed class StaffTelegramLinkService(
    BeautyDbContext db, TenantContext tenant, TimeProvider clock, StaffTelegramOptions options, StaffTelegramLimits limits,
    ISecretProtector secrets, StaffChatCodec codec, IStaffTelegramTransport transport, ILogger<StaffTelegramLinkService> log)
{
    public static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(15);
    public const int IssuePerHour = 5;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static Error Err(ErrorKind k, string code, string msg, IReadOnlyDictionary<string, object?>? extras = null) => new(k, code, msg, null, extras);

    private bool IsActiveOwner(Guid userId) =>
        db.Users.AsNoTracking().Any(u => u.Id == userId && u.IsActive && u.State == "active" && u.Role == Roles.Owner);

    public async Task<Result<TelegramLinkStatusDto>> GetStatusAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Role != Roles.Owner) return Err(ErrorKind.Forbidden, "forbidden_role", "Only the owner can use Telegram alerts.");
        var now = clock.GetUtcNow();
        var open = await db.StaffTelegramLinks.AsNoTracking()
            .Where(l => l.UserId == actor.UserId && StaffTelegramRevocation.OpenStatuses.Contains(l.Status))
            .Select(l => new { l.Status, l.ChannelId, l.TokenExpiresAt, l.ActivatedAt, l.LastDeliveryAt }).FirstOrDefaultAsync(ct);
        // issued/claimed із простроченим токеном для UI вже не діють (фізично закриє воркер)
        if (open is not null && open.Status != "active" && open.TokenExpiresAt <= now) open = null;
        if (open is null)
        {
            var since = now.AddDays(-30);
            var last = await db.StaffTelegramLinks.AsNoTracking()
                .Where(l => l.UserId == actor.UserId && l.Status == "revoked" && l.RevokedAt >= since)
                .OrderByDescending(l => l.RevokedAt).Select(l => new { l.RevokeReason, l.RevokedAt }).FirstOrDefaultAsync(ct);
            return new TelegramLinkStatusDto(options.Active, "none", null, null, null, null,
                last is null ? null : new StaffRevokeInfo(last.RevokeReason!, last.RevokedAt!.Value), StaffTelegramOptions.ConsentVersion);
        }
        var ch = await db.Channels.AsNoTracking().Where(c => c.Id == open.ChannelId).Select(c => new StaffChannelRef(c.Id, c.Name)).FirstOrDefaultAsync(ct);
        var state = open.Status switch { "issued" => "pending", "claimed" => "awaitingConfirmation", _ => "active" };
        return new TelegramLinkStatusDto(options.Active, state, ch, open.Status == "active" ? null : open.TokenExpiresAt, open.ActivatedAt, open.LastDeliveryAt,
            null, StaffTelegramOptions.ConsentVersion);
    }

    public async Task<Result<TelegramLinkIssuedDto>> IssueAsync(Actor actor, IssueTelegramLinkRequest? req, CancellationToken ct)
    {
        if (actor.Role != Roles.Owner) return Err(ErrorKind.Forbidden, "forbidden_role", "Only the owner can use Telegram alerts.");
        if (!options.Active) return Err(ErrorKind.Forbidden, "telegram_alerts_disabled", "Telegram alerts are not available.");
        if (!IsActiveOwner(actor.UserId)) return Err(ErrorKind.Forbidden, "forbidden_role", "Only the owner can use Telegram alerts.");
        if (req?.Consent != true) return Err(ErrorKind.Validation, "consent_required", "Consent is required.");
        if (req.ConsentVersion != StaffTelegramOptions.ConsentVersion)
            return Err(ErrorKind.Validation, "consent_version_outdated", "The consent text has been updated.",
                new Dictionary<string, object?> { ["currentVersion"] = StaffTelegramOptions.ConsentVersion });

        // Канал (2b-Q13)
        var telegram = await db.Channels.AsNoTracking().Where(c => c.Type == ChannelType.Telegram)
            .Select(c => new { c.Id, c.Name, c.IsActive, c.CredentialsEncrypted }).ToListAsync(ct);
        var active = telegram.Where(c => c.IsActive).ToList();
        var chosen = req.ChannelId is { } wanted ? telegram.FirstOrDefault(c => c.Id == wanted) : null;
        if (req.ChannelId is not null)
        {
            if (chosen is null) return Err(ErrorKind.NotFound, "channel_not_found", "Bot not found.");
            if (!chosen.IsActive) return Err(ErrorKind.Validation, "telegram_channel_unavailable", "No active Telegram bot.");
        }
        else if (active.Count == 0) return Err(ErrorKind.Validation, "telegram_channel_unavailable", "No active Telegram bot.");
        else if (active.Count > 1)
            return Err(ErrorKind.Validation, "telegram_channel_required", "Choose the bot for alerts.",
                new Dictionary<string, object?> { ["channels"] = active.Select(c => new { id = c.Id, name = c.Name }).ToList() });
        else chosen = active[0];

        if (!limits.TryHit($"issue:{actor.TenantId:N}:{actor.UserId:N}", IssuePerHour, Hour))
            return Err(ErrorKind.TooManyRequests, "rate_limited", "Too many attempts. Try again later.");
        // getMe до будь-яких змін у БД
        string? bot;
        try
        {
            var c = ChannelSecrets.Read(secrets, chosen!.CredentialsEncrypted);
            bot = await transport.GetBotUsernameAsync(new ResolvedChannel(actor.TenantId, chosen.Id, ChannelIds.Telegram,
                new ChannelCredentials(c.Token, c.WebhookSecret, c.AppSecret, c.VerifyToken)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("staff_tg_bot_unavailable error={ErrorType}", ex.GetType().Name);
            bot = null;
        }
        if (string.IsNullOrWhiteSpace(bot)) return Err(ErrorKind.Unavailable, "telegram_bot_unavailable", "Telegram is unavailable.");

        var now = clock.GetUtcNow();
        var (token, hash) = StaffLinkToken.Generate();
        var link = new StaffTelegramLink
        {
            Id = Guid.NewGuid(), UserId = actor.UserId, ChannelId = chosen.Id, Status = "issued", TokenHash = hash,
            TokenExpiresAt = now + TokenTtl, ConsentVersion = StaffTelegramOptions.ConsentVersion, ConsentAt = now, CreatedAt = now, UpdatedAt = now,
        };
        try
        {
            await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            var existing = await db.StaffTelegramLinks.AsNoTracking()
                .Where(l => l.UserId == actor.UserId && StaffTelegramRevocation.OpenStatuses.Contains(l.Status)).Select(l => new { l.Id, l.Status }).ToListAsync(ct);
            if (existing.Any(e => e.Status == "active"))
                return Err(ErrorKind.Conflict, "telegram_already_linked", "Telegram is already linked.");
            foreach (var e in existing)
                await StaffTelegramRevocation.RevokeOneAsync(db, e.Id, actor.UserId, "replaced", "user", actor.UserId, now, ct);
            db.StaffTelegramLinks.Add(link);
            await db.SaveChangesAsync(ct);
            StaffTelegramRevocation.AddEvent(db, link.Id, actor.UserId, "issued", null, "user", actor.UserId, now);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Err(ErrorKind.Conflict, "telegram_already_linked", "Telegram is already linked.");
        }
        log.LogInformation("staff_tg_link_issued link={LinkId} user={UserId}", link.Id, actor.UserId);
        return new TelegramLinkIssuedDto(link.Id, $"https://t.me/{bot}?start=inv_{token}", link.TokenExpiresAt!.Value);
    }

    /// <summary>Ідемпотентно; працює і за вимкненого прапора. Для рядка з чатом після коміту best-effort надсилає «Сповіщення вимкнено».</summary>
    public async Task<Result<bool>> RevokeAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Role != Roles.Owner) return Err(ErrorKind.Forbidden, "forbidden_role", "Only the owner can use Telegram alerts.");
        var now = clock.GetUtcNow();
        var rows = await db.StaffTelegramLinks.AsNoTracking()
            .Where(l => l.UserId == actor.UserId && StaffTelegramRevocation.OpenStatuses.Contains(l.Status))
            .Select(l => new { l.Id, l.ChannelId, l.ChatIdEncrypted }).ToListAsync(ct);
        foreach (var r in rows)
        {
            // розшифрування до стирання (лише в пам'яті); збій розшифрування не блокує відв'язку
            string? chat = null;
            try { chat = r.ChatIdEncrypted is null ? null : codec.Decrypt(r.ChatIdEncrypted); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            if (!await StaffTelegramRevocation.RevokeOneAsync(db, r.Id, actor.UserId, "user_unlink", "user", actor.UserId, now, ct)) continue;
            log.LogInformation("staff_tg_link_revoked link={LinkId} reason={Reason}", r.Id, "user_unlink");
            if (chat is not null) await TryNotifyAsync(actor.TenantId, r.ChannelId, chat, StaffTelegramTexts.Unlinked, ct);
        }
        return true;
    }

    private async Task TryNotifyAsync(Guid tenantId, Guid channelId, string chatId, string text, CancellationToken ct)
    {
        try
        {
            var ch = await db.Channels.AsNoTracking().Where(c => c.Id == channelId && c.IsActive).Select(c => new { c.CredentialsEncrypted }).FirstOrDefaultAsync(ct);
            if (ch is null) return;
            var c = ChannelSecrets.Read(secrets, ch.CredentialsEncrypted);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(InboundIngestCore.AckTimeout);
            await transport.SendTextAsync(new ResolvedChannel(tenantId, channelId, ChannelIds.Telegram,
                new ChannelCredentials(c.Token, c.WebhookSecret, c.AppSecret, c.VerifyToken)), chatId, text, null, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogWarning("staff_tg_command_failed reason={Reason}", "unlink_notify_" + ex.GetType().Name); }
    }

    internal static bool IsUniqueViolation(Exception ex) =>
        ex is PostgresException { SqlState: "23505" } || ex is DbUpdateException { InnerException: PostgresException { SqlState: "23505" } };
}
