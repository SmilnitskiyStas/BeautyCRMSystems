using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using AiStatus = BeautyCrm.Infrastructure.AI.Beauty.AiActionStatus;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Inbox;

/// <summary>
/// Inbox розмов для менеджера (TASK-739, §22.6.5, ADR-012 §2.6): список, деталі, відповідь у Telegram через outbox, «Повернути ШІ»,
/// прочитане. Tenant лише з автентифікованого <see cref="Actor"/> і scope (RLS FORCE); чужий id -> 404. Лише owner/admin.
/// Конвеєр ШІ (held, таймер, шаблони) сюди не входить: сервіс лише читає стан і виконує дії менеджера.
/// Логи: id, коди й лічильники; текст повідомлень, імена, телефони, external_chat_id не логуються.
/// </summary>
public sealed partial class InboxService(
    BeautyDbContext db, TenantContext tenant, OutboundDispatcher dispatcher, IAiActionJournal journal, InboxThrottle throttle,
    ConversationSignals signals, TimeProvider clock, ILogger<InboxService> log)
{
    private const string Held = "held";
    private const string Processed = "processed";
    private const int PreviewLength = 120;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static readonly string[] ActiveProposal = [ChatProposalStatus.Prepared, ChatProposalStatus.Shown];

    // ------------------------------------------------------------------ допоміжне

    private Error? Guard(Actor actor)
    {
        TenantGuard.Ensure(tenant, actor.TenantId);
        return actor.Role is Roles.Owner or Roles.Admin
            ? null
            : Error.Forbidden(InboxErrors.ForbiddenRole, "Inbox is available to the owner and admin only.");
    }

    private static Error NotFound() => Error.NotFound(InboxErrors.ConversationNotFound, "Conversation was not found.");

    /// <summary>429: <c>Conflicts</c> несе Retry-After у секундах (API-шар мапить на заголовок і не віддає це поле).</summary>
    private static Error RateLimited(TimeSpan retryAfter) =>
        new(ErrorKind.Conflict, InboxErrors.RateLimited, "Too many requests. Try again later.", Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));

    private static string TitleFor(string? clientName, ChannelType type) => !string.IsNullOrWhiteSpace(clientName) ? clientName : type switch
    {
        ChannelType.Telegram => "Клієнт Telegram",
        ChannelType.Instagram => "Клієнт Instagram",
        ChannelType.Facebook => "Клієнт Facebook",
        ChannelType.WhatsApp => "Клієнт WhatsApp",
        ChannelType.Viber => "Клієнт Viber",
        _ => "Клієнт віджета",
    };

    /// <summary>Натискання кнопки показується лише текстом-міткою <c>"[кнопка] {підпис}"</c>; сирий callback_data у тілі неможливий (§23.10).</summary>
    private static string InboxBody(Message m) =>
        m.Kind == "callback" && !m.Body.StartsWith(CallbackBody.Prefix, StringComparison.Ordinal) ? CallbackBody.Prefix : m.Body;

    private static string Preview(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var head = body.Length > PreviewLength * 2 ? body[..(PreviewLength * 2)] : body;
        var flat = Whitespace().Replace(string.Concat(head.Select(ch => char.IsControl(ch) ? ' ' : ch)), " ").Trim();
        return string.Concat(flat.EnumerateRunes().Take(PreviewLength).Select(r => r.ToString()));
    }

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    internal static string ReplyKey(Guid conversationId, string idempotencyKey) => $"staff-reply:{conversationId}:{Sha256Hex(idempotencyKey)}";

    /// <summary>
    /// Текст відповіді: trim, 1..3500 кодових точок, plain text; керівні символи заборонені (крім \n, CRLF зводиться до LF), одиночні
    /// сурогати відхиляються. null = недійсний.
    /// </summary>
    public static string? NormalizeText(string? raw)
    {
        if (raw is null || raw.Length > InboxOptions.MaxTextLength * 2) return null;
        var t = raw.Replace("\r\n", "\n").Trim();
        if (t.Length == 0) return null;
        var codePoints = 0;
        for (var i = 0; i < t.Length;)
        {
            if (Rune.DecodeFromUtf16(t.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done) return null;
            if (rune.Value != '\n' && Rune.IsControl(rune)) return null;
            if (++codePoints > InboxOptions.MaxTextLength) return null;
            i += consumed;
        }
        return t;
    }

    // ------------------------------------------------------------------ проєкція розмови

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid ChannelId { get; init; }
        public ChannelType ChannelType { get; init; }
        public string ChannelName { get; init; } = "";
        public bool ChannelActive { get; init; }
        public Guid? LocationId { get; init; }
        public string? LocationName { get; init; }
        public Guid? ClientId { get; init; }
        public string? ClientName { get; init; }
        public string AiState { get; init; } = "";
        public string? HandoffReason { get; init; }
        public DateTimeOffset? AiPausedUntil { get; init; }
        public DateTimeOffset? AwaitingStaffSince { get; init; }
        public DateTimeOffset? LastMessageAt { get; init; }
        public DateTimeOffset SortAll { get; init; }
        public string? LastBody { get; init; }
        public MessageSenderType? LastSender { get; init; }
        public int Unread { get; init; }
    }

    private IQueryable<Row> Project(IQueryable<Conversation> q) => q.Select(c => new Row
    {
        Id = c.Id,
        ChannelId = c.ChannelId,
        ChannelType = c.Channel!.Type,
        ChannelName = c.Channel.Name,
        ChannelActive = c.Channel.IsActive,
        LocationId = c.Channel.LocationId,
        LocationName = c.Channel.Location != null ? c.Channel.Location.Name : null,
        ClientId = c.Client != null && c.Client.DeletedAt == null ? c.ClientId : null,
        ClientName = c.Client != null && c.Client.DeletedAt == null ? c.Client.FullName : null,
        AiState = c.AiState,
        HandoffReason = c.HandoffReason,
        AiPausedUntil = c.AiPausedUntil,
        AwaitingStaffSince = c.AwaitingStaffSince,
        LastMessageAt = c.LastMessageAt,
        SortAll = c.LastMessageAt ?? c.CreatedAt,
        // чернетки (suggest) клієнту не надсилались: у прев'ю не потрапляють
        LastBody = db.Messages.Where(m => m.ConversationId == c.Id && m.Status != "draft")
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.CreatedAt).Select(m => m.Body).FirstOrDefault(),
        LastSender = db.Messages.Where(m => m.ConversationId == c.Id && m.Status != "draft")
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.CreatedAt).Select(m => (MessageSenderType?)m.SenderType).FirstOrDefault(),
        Unread = db.Messages.Count(m => m.ConversationId == c.Id && m.Direction == MessageDirection.Inbound
            && (c.StaffLastReadAt == null || m.CreatedAt > c.StaffLastReadAt)),
    });

    private static InboxConversationDto ToDto(Row r) => new(
        r.Id, new InboxChannelDto(r.ChannelId, EnumText<ChannelType>.ToDb(r.ChannelType), r.ChannelName),
        r.LocationId is { } lid ? new InboxLocationDto(lid, r.LocationName ?? "") : null,
        r.ClientId is { } cid ? new InboxClientDto(cid, r.ClientName ?? "") : null,
        TitleFor(r.ClientName, r.ChannelType), r.AiState, r.HandoffReason, r.AiPausedUntil, r.AwaitingStaffSince, r.LastMessageAt,
        Preview(r.LastBody), r.LastSender is { } s ? EnumText<MessageSenderType>.ToDb(s) : null, r.Unread);

    private static InboxConversationDetailDto ToDetailDto(Row r)
    {
        var d = ToDto(r);
        // unsupported важливіше за inactive: тип каналу не зміниться, вмикання каналу не допоможе
        var blocked = r.ChannelType != ChannelType.Telegram ? InboxErrors.ChannelUnsupported : !r.ChannelActive ? InboxErrors.ChannelInactive : null;
        return new InboxConversationDetailDto(d.Id, d.Channel, d.Location, d.Client, d.Title, d.AiState, d.HandoffReason, d.AiPausedUntil,
            d.AwaitingStaffSince, d.LastMessageAt, d.LastMessagePreview, d.LastMessageSenderType, d.UnreadCount, blocked is null, blocked);
    }

    // ------------------------------------------------------------------ список

    private sealed record Cursor(char Mode, long Ticks, Guid Id);

    private static string EncodeCursor(char mode, DateTimeOffset key, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mode}|{key.UtcTicks}|{id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Cursor? DecodeCursor(string s)
    {
        try
        {
            var b64 = s.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 3 || parts[0].Length != 1 || parts[0][0] is not ('n' or 'a')) return null;
            return new Cursor(parts[0][0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), Guid.ParseExact(parts[2], "N"));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<Result<InboxListDto>> ListAsync(
        Actor actor, string? filter, Guid? locationId, string? channelType, string? cursor, int? take, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!throttle.TryAcquireRead(actor.TenantId, actor.UserId, out var wait)) return RateLimited(wait);

        var needsReply = filter is null or "needs_reply";
        if (!needsReply && filter != "all") return Error.Validation(InboxErrors.InvalidFilter, "filter must be needs_reply or all.");
        ChannelType? type = null;
        if (!string.IsNullOrWhiteSpace(channelType))
        {
            if (!EnumText<ChannelType>.Values.Contains(channelType)) return Error.Validation(InboxErrors.InvalidFilter, "Unknown channelType.");
            type = EnumText<ChannelType>.FromDb(channelType);
        }
        Cursor? cur = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            cur = cursor.Length <= 200 ? DecodeCursor(cursor) : null;
            if (cur is null || cur.Mode != (needsReply ? 'n' : 'a')) return Error.Validation(InboxErrors.InvalidCursor, "Cursor is invalid for this filter.");
        }
        var size = Math.Clamp(take ?? 50, 1, 100);

        IQueryable<Conversation> q = db.Conversations.AsNoTracking();
        if (locationId is { } loc) q = q.Where(c => c.Channel!.LocationId == loc);
        if (type is { } t) q = q.Where(c => c.Channel!.Type == t);

        List<Row> rows;
        if (needsReply)
        {
            q = q.Where(c => c.AwaitingStaffSince != null);
            if (cur is not null)
            {
                var key = new DateTimeOffset(cur.Ticks, TimeSpan.Zero);
                var cid = cur.Id;
                q = q.Where(c => c.AwaitingStaffSince > key || (c.AwaitingStaffSince == key && c.Id.CompareTo(cid) > 0));
            }
            rows = await Project(q.OrderBy(c => c.AwaitingStaffSince).ThenBy(c => c.Id)).Take(size + 1).ToListAsync(ct);
        }
        else
        {
            if (cur is not null)
            {
                var key = new DateTimeOffset(cur.Ticks, TimeSpan.Zero);
                var cid = cur.Id;
                q = q.Where(c => (c.LastMessageAt ?? c.CreatedAt) < key || ((c.LastMessageAt ?? c.CreatedAt) == key && c.Id.CompareTo(cid) < 0));
            }
            rows = await Project(q.OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt).ThenByDescending(c => c.Id)).Take(size + 1).ToListAsync(ct);
        }

        string? next = null;
        if (rows.Count > size)
        {
            rows.RemoveAt(size);
            var last = rows[^1];
            next = needsReply ? EncodeCursor('n', last.AwaitingStaffSince!.Value, last.Id) : EncodeCursor('a', last.SortAll, last.Id);
        }
        var needsCount = await db.Conversations.CountAsync(c => c.AwaitingStaffSince != null, ct);
        return new InboxListDto(rows.Select(ToDto).ToList(), next, new InboxListMeta(needsCount));
    }

    // ------------------------------------------------------------------ деталі

    public async Task<Result<InboxDetailDto>> GetAsync(Actor actor, Guid id, Guid? before, int? take, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!throttle.TryAcquireRead(actor.TenantId, actor.UserId, out var wait)) return RateLimited(wait);

        var row = await Project(db.Conversations.AsNoTracking().Where(c => c.Id == id)).FirstOrDefaultAsync(ct);
        if (row is null) return NotFound();
        var size = Math.Clamp(take ?? 100, 1, 100);

        var mq = db.Messages.AsNoTracking().Where(m => m.ConversationId == id);
        if (before is { } b)
        {
            var pivot = await db.Messages.AsNoTracking().Where(m => m.Id == b && m.ConversationId == id)
                .Select(m => new { m.SentAt, m.Id }).FirstOrDefaultAsync(ct);
            if (pivot is null) return Error.NotFound(InboxErrors.MessageNotFound, "Message was not found in this conversation.");
            var pSent = pivot.SentAt;
            var pId = pivot.Id;
            mq = mq.Where(m => m.SentAt < pSent || (m.SentAt == pSent && m.Id.CompareTo(pId) < 0));
        }
        var page = await mq.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(size + 1).ToListAsync(ct);
        var hasMore = page.Count > size;
        if (hasMore) page.RemoveAt(size);
        page.Reverse();

        var userIds = page.Where(m => m.SenderUserId != null).Select(m => m.SenderUserId!.Value).Distinct().ToList();
        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var messages = page.Select(m => new InboxMessageDto(
            m.Id, EnumText<MessageDirection>.ToDb(m.Direction), EnumText<MessageSenderType>.ToDb(m.SenderType),
            m.SenderUserId is { } uid ? new InboxUserDto(uid, names.GetValueOrDefault(uid) ?? "Адміністратор") : null,
            m.Kind, InboxBody(m), m.Status,
            // у відповідь потрапляє лише код, не сирий last_error
            m.Status == "failed" ? (ChannelSendCodes.IsKnown(m.LastError) ? m.LastError : ChannelSendCodes.SendFailed) : null,
            m.SentAt, m.CreatedAt,
            // §23.10: підписи кнопок вихідного (без callback_data і токенів) і підпис натиснутої кнопки
            m.Direction == MessageDirection.Outbound && m.Actions is not null && ChatKeyboardLayout.ReadLabels(m.Actions) is { Count: > 0 } labels ? labels : null,
            m.Kind == "callback" ? CallbackBody.LabelOf(m.Body) : null)).ToList();

        var now = clock.GetUtcNow();
        var proposal = await db.ChatBookingProposals.AsNoTracking()
            .Where(p => p.ConversationId == id && ActiveProposal.Contains(p.Status) && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new InboxProposalDto(p.Status, p.ExpiresAt, p.SummaryMessage != null ? p.SummaryMessage.Body : null))
            .FirstOrDefaultAsync(ct);

        IReadOnlyList<InboxAppointmentDto> upcoming = [];
        if (row.ClientId is { } clientId)
        {
            var appts = await db.Appointments.AsNoTracking()
                .Where(a => a.ClientId == clientId && a.StartsAt > now
                    && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
                .OrderBy(a => a.StartsAt).Take(5)
                .Select(a => new { a.Id, a.StartsAt, Service = a.Service!.Name, Location = a.Location!.Name, a.ReminderMethod, a.ReminderOffsets, a.ClientConfirmedAt })
                .ToListAsync(ct);
            upcoming = appts.Select(a => new InboxAppointmentDto(a.Id, a.StartsAt, a.Service, a.Location,
                new InboxReminderPlanDto(a.ReminderMethod, a.ReminderOffsets), a.ClientConfirmedAt)).ToList();
        }
        return new InboxDetailDto(ToDetailDto(row), messages, hasMore, proposal, upcoming);
    }

    // ------------------------------------------------------------------ відповідь менеджера

    /// <summary>
    /// ADR-012 §2.6: одна транзакція (вихідне staff + стан розмови + held -> processed + пропозиція superseded + pending_prompt NULL),
    /// після коміту inline-відправка. Повтор того самого <paramref name="idempotencyKey"/> з тим самим текстом дає те саме повідомлення.
    /// </summary>
    public async Task<Result<InboxReplyAcceptedDto>> ReplyAsync(Actor actor, Guid id, string idempotencyKey, string? text, CancellationToken ct = default)
    {
        void Audit(string result, Guid? messageId = null) =>
            log.LogInformation("inbox_reply conversation={ConversationId} message={MessageId} user={UserId} result={Result}",
                id, messageId, actor.UserId, result);

        if (Guard(actor) is { } denied) { Audit(denied.Code); return denied; }

        var body = NormalizeText(text);
        if (body is null)
        {
            Audit(InboxErrors.InvalidText);
            return Error.Validation(InboxErrors.InvalidText, $"text must be 1..{InboxOptions.MaxTextLength} characters of plain text without control characters.");
        }

        // L8: 3500 кодових точок можуть дати до 7000 одиниць UTF-16, а Telegram приймає ≤ 4096 - відхиляємо, не обрізаємо мовчки.
        if (body.Length > InboxOptions.MaxUtf16Length)
        {
            Audit(InboxErrors.InvalidLength);
            return Error.Validation(InboxErrors.InvalidLength, $"text must be at most {InboxOptions.MaxUtf16Length} UTF-16 code units (emoji count as 2).");
        }

        var conv = await db.Conversations.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new { c.Id, Type = c.Channel!.Type, Active = c.Channel.IsActive, c.AiState }).FirstOrDefaultAsync(ct);
        if (conv is null) { Audit(InboxErrors.ConversationNotFound); return NotFound(); }
        if (conv.Type != ChannelType.Telegram)
        {
            Audit(InboxErrors.ChannelUnsupported);
            return Error.Conflict(InboxErrors.ChannelUnsupported, "Replies are supported for Telegram only.");
        }
        if (!conv.Active)
        {
            Audit(InboxErrors.ChannelInactive);
            return Error.Conflict(InboxErrors.ChannelInactive, "The channel is not active.");
        }

        var key = ReplyKey(id, idempotencyKey);
        if (await FindReplayAsync(key, body, actor, ct) is { } replay)
        {
            Audit(replay.IsOk ? "idempotent_replay" : replay.Error!.Code, replay.Value?.Message.Id);
            return replay;
        }

        if (!throttle.TryAcquireReply(actor.TenantId, actor.UserId, id, out var wait))
        {
            Audit(InboxErrors.RateLimited);
            return RateLimited(wait);
        }

        var now = clock.GetUtcNow();
        var message = new Message
        {
            Id = Guid.NewGuid(), ConversationId = id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.Staff,
            SenderUserId = actor.UserId, Body = body, Kind = InboundKinds.Text, SentAt = now, Status = "sending", IdempotencyKey = key,
        };
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                db.Messages.Add(message);
                await db.SaveChangesAsync(ct);
                await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.HandoffReason, c => c.AiState == "active" ? "staff_reply" : c.HandoffReason)
                    .SetProperty(c => c.HandoffAt, c => c.AiState == "active" ? now : c.HandoffAt)
                    .SetProperty(c => c.AiState, "handoff")
                    .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)null) // безстроково, до resume-ai
                    .SetProperty(c => c.StaffRepliedAt, now)
                    .SetProperty(c => c.AwaitingStaffSince, (DateTimeOffset?)null)
                    .SetProperty(c => c.StaffLastReadAt, now)
                    .SetProperty(c => c.LastMessageAt, c => c.LastMessageAt == null || c.LastMessageAt < now ? now : c.LastMessageAt)
                    .SetProperty(c => c.PendingPrompt, (string?)null)
                    .SetProperty(c => c.PendingPromptAppointmentId, (Guid?)null)
                    .SetProperty(c => c.PendingPromptExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(c => c.UpdatedAt, now), ct);
                await db.Messages.Where(m => m.ConversationId == id && m.Direction == MessageDirection.Inbound && m.Status == Held)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, Processed).SetProperty(m => m.UpdatedAt, now), ct);
                await db.ChatBookingProposals.Where(p => p.ConversationId == id && ActiveProposal.Contains(p.Status))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ChatProposalStatus.Superseded).SetProperty(p => p.DecidedAt, now), ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // паралельний запит із тим самим ключем зафіксувався першим: повторюємо як replay
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                var raced = await FindReplayAsync(key, body, actor, ct);
                if (raced is null) throw;
                Audit(raced.IsOk ? "idempotent_replay" : raced.Error!.Code, raced.Value?.Message.Id);
                return raced;
            }
        }
        db.ChangeTracker.Clear();
        signals.NoteTenant(actor.TenantId);

        try
        {
            await dispatcher.DispatchAsync(actor.TenantId, message.Id, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
        {
            // збій до результату відправки: pending для sweeper/воркера; лише тип винятку (у тексті може бути URL)
            log.LogWarning("inbox_dispatch_failed conversation={ConversationId} message={MessageId} error={ErrorType}", id, message.Id, ex.GetType().Name);
            await db.Messages.Where(m => m.Id == message.Id && m.Status == "sending")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending").SetProperty(m => m.UpdatedAt, clock.GetUtcNow()), CancellationToken.None);
        }

        var status = await db.Messages.AsNoTracking().Where(m => m.Id == message.Id).Select(m => m.Status).FirstAsync(ct);
        Audit(status == "failed" ? "accepted_failed" : "accepted", message.Id);
        return new InboxReplyAcceptedDto(new InboxReplyMessageDto(message.Id, status));
    }

    /// <returns>null = ключа ще не було; Ok = повтор того самого запиту; помилка = ключ уже використано з іншим текстом/автором.</returns>
    private async Task<Result<InboxReplyAcceptedDto>?> FindReplayAsync(string key, string body, Actor actor, CancellationToken ct)
    {
        var existing = await db.Messages.AsNoTracking().Where(m => m.IdempotencyKey == key)
            .Select(m => new { m.Id, m.Body, m.Status, m.SenderUserId }).FirstOrDefaultAsync(ct);
        if (existing is null) return null;
        if (existing.Body != body || existing.SenderUserId != actor.UserId)
            return Error.Conflict(InboxErrors.IdempotencyConflict, "This Idempotency-Key was already used with a different request.");
        return new InboxReplyAcceptedDto(new InboxReplyMessageDto(existing.Id, existing.Status));
    }

    // ------------------------------------------------------------------ «Повернути ШІ»

    public async Task<Result<bool>> ResumeAiAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!await db.Conversations.AnyAsync(c => c.Id == id, ct)) return NotFound();

        var now = clock.GetUtcNow();
        var changed = 0;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var held = await db.Messages.Where(m => m.ConversationId == id && m.Direction == MessageDirection.Inbound && m.Status == Held)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, Processed).SetProperty(m => m.UpdatedAt, now), ct);
            var state = await db.Conversations
                .Where(c => c.Id == id && (c.AiState != "active" || c.HandoffReason != null || c.AiPausedUntil != null || c.AwaitingStaffSince != null))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.AiState, "active")
                    .SetProperty(c => c.HandoffReason, (string?)null)
                    .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)null)
                    .SetProperty(c => c.AwaitingStaffSince, (DateTimeOffset?)null)
                    .SetProperty(c => c.UpdatedAt, now), ct);
            changed = held + state;
            // ідемпотентність: повторний виклик без змін журнал не засмічує
            if (changed > 0)
                await journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), actor.TenantId, "resume_ai", $"conversation:{id}", now, AiStatus.Done,
                    false, AiMode.Confirm, JsonSerializer.Serialize(new { userId = actor.UserId }), id), ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        log.LogInformation("inbox_resume_ai conversation={ConversationId} user={UserId} changed={Changed}", id, actor.UserId, changed > 0);
        return true;
    }

    // ------------------------------------------------------------------ прочитане

    /// <summary>staff_last_read_at = max(поточне, created_at повідомлення | now): назад не рухається.</summary>
    public async Task<Result<bool>> MarkReadAsync(Actor actor, Guid id, Guid? upToMessageId, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        if (!await db.Conversations.AnyAsync(c => c.Id == id, ct)) return NotFound();

        var upTo = clock.GetUtcNow();
        if (upToMessageId is { } mid)
        {
            var created = await db.Messages.AsNoTracking().Where(m => m.Id == mid && m.ConversationId == id)
                .Select(m => (DateTimeOffset?)m.CreatedAt).FirstOrDefaultAsync(ct);
            if (created is null) return Error.NotFound(InboxErrors.MessageNotFound, "Message was not found in this conversation.");
            upTo = created.Value;
        }
        await db.Conversations.Where(c => c.Id == id && (c.StaffLastReadAt == null || c.StaffLastReadAt < upTo))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.StaffLastReadAt, upTo), ct);
        return true;
    }
}
