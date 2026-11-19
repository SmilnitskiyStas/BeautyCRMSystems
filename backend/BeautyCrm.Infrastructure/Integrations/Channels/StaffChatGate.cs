using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <param name="Staff">Службові повідомлення (прив'язка власника): НЕ потрапляють у beauty_conversations/messages/клієнтів/ШІ.</param>
/// <param name="Client">Решта: клієнтський потік без змін.</param>
public sealed record StaffPartition(IReadOnlyList<InboundMessage> Staff, IReadOnlyList<InboundMessage> Client);

/// <summary>Guard вхідних (§32.24.4): відокремлює службові чати власників від клієнтського потоку до збереження.</summary>
public interface IStaffChatGate
{
    /// <summary>Fail-closed: помилка запиту кидає виняток на весь пакет.</summary>
    Task<StaffPartition> PartitionAsync(IReadOnlyList<InboundMessage> parsed, CancellationToken ct);

    /// <summary>Обробник службових у пам'яті (вміст не зберігається). Помилки обробника логуються й не перекидаються.</summary>
    Task HandleAsync(IReadOnlyList<InboundMessage> staff, CancellationToken ct);
}

public sealed partial class StaffChatGate(
    BeautyDbContext db, ChannelRequestContext context, TimeProvider clock, StaffTelegramOptions options, StaffTelegramLimits limits,
    StaffChatCodec codec, IStaffTelegramTransport transport, ILogger<StaffChatGate> log) : IStaffChatGate
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan HintWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SvcReplyWindow = TimeSpan.FromMinutes(10);
    public const int ClaimPerChatPerHour = 10, FailedPerChannelPerHour = 60, SvcReplyPerWindow = 5;

    [GeneratedRegex(@"^i[yn]:")]
    private static partial Regex ServiceCallback();

    /// <summary>Службова форма: <c>/start inv_…</c> після TrimStart, без регістру, з необов'язковим <c>@bot</c> (QA-1/I5).</summary>
    [GeneratedRegex(@"^/start(@[A-Za-z0-9_]*)?\s+inv_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartInv();

    private static bool IsStartInv(string? text) => text is not null && StartInv().IsMatch(text.TrimStart());

    private static bool IsServiceShape(InboundMessage m) =>
        (m.Kind == InboundKinds.Callback && (m.CallbackData is { } d && ServiceCallback().IsMatch(d)))
        || (m.Kind is InboundKinds.Command or InboundKinds.Text && IsStartInv(m.Text));

    public async Task<StaffPartition> PartitionAsync(IReadOnlyList<InboundMessage> parsed, CancellationToken ct)
    {
        var idx = Enumerable.Range(0, parsed.Count)
            .Where(i => !parsed[i].AckOnly && parsed[i].Channel == ChannelIds.Telegram && !string.IsNullOrEmpty(parsed[i].ExternalConversationId)).ToList();
        if (idx.Count == 0) return new StaffPartition([], parsed);
        var channel = context.Value ?? throw new InvalidOperationException("No channel context for staff chat guard."); // fail-closed

        var staff = new HashSet<int>(idx.Where(i => IsServiceShape(parsed[i])));
        // QA-2: після /start inv_… того ж чату в межах пакета решта його повідомлень лишається в службовому потоці
        // (claim виконується пізніше, у HandleAsync, тож БД ще не знає про claimed). Fail-closed: пакет, а не окреме повідомлення.
        var startChats = new HashSet<string>();
        foreach (var i in idx)
        {
            var p = parsed[i];
            if (staff.Contains(i)) { if (p.Kind != InboundKinds.Callback) startChats.Add(p.ExternalConversationId); }
            else if (startChats.Contains(p.ExternalConversationId)) staff.Add(i);
        }
        var rest = idx.Where(i => !staff.Contains(i)).ToList();
        if (rest.Count > 0)
        {
            var byHash = rest.Select(i => parsed[i].ExternalConversationId).Distinct()
                .ToDictionary(c => Convert.ToHexString(codec.Hash(channel.ChannelId, c)), c => c);
            var hashes = byHash.Keys.Select(Convert.FromHexString).ToList();
            var cid = channel.ChannelId;
            var found = await db.StaffTelegramLinks.AsNoTracking()
                .Where(l => l.ChannelId == cid && (l.Status == "claimed" || l.Status == "active") && hashes.Contains(l.ChatIdHash!))
                .Select(l => l.ChatIdHash).ToListAsync(ct);
            var staffChats = found.Select(h => byHash.GetValueOrDefault(Convert.ToHexString(h!))).Where(c => c is not null).ToHashSet();
            foreach (var i in rest) if (staffChats.Contains(parsed[i].ExternalConversationId)) staff.Add(i);
        }
        return new StaffPartition(
            Enumerable.Range(0, parsed.Count).Where(staff.Contains).Select(i => parsed[i]).ToList(),
            Enumerable.Range(0, parsed.Count).Where(i => !staff.Contains(i)).Select(i => parsed[i]).ToList());
    }

    public async Task HandleAsync(IReadOnlyList<InboundMessage> staff, CancellationToken ct)
    {
        var channel = context.Value;
        if (channel is null) return;
        foreach (var m in staff)
        {
            try { await HandleOneAsync(m, channel, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                log.LogWarning("staff_tg_command_failed reason={Reason}", ex.GetType().Name); // без вмісту й chat id
            }
        }
    }

    private async Task HandleOneAsync(InboundMessage m, ResolvedChannel ch, CancellationToken ct)
    {
        var chatId = m.ExternalConversationId;
        if (m.Kind == InboundKinds.Callback) { await HandleCallbackAsync(m, ch, ct); return; }
        var text = (m.Text ?? "").Trim();
        if (IsStartInv(text))
        {
            if (m.Forwarded) // L1: переслане посилання не приймаємо (токен не спалюється)
            {
                log.LogInformation("staff_tg_token_rejected channel={ChannelId} reason={Reason}", ch.ChannelId, "forwarded");
                await ReplyLimitedAsync(ch, chatId, StaffTelegramTexts.InvalidLink, ct);
                return;
            }
            await ClaimAsync(chatId, text, ch, ct);
            return;
        }
        var cmd = text.Split(' ', 2)[0];
        var at = cmd.IndexOf('@');
        if (at > 0) cmd = cmd[..at];
        switch (cmd.ToLowerInvariant())
        {
            case "/stop": await StopAsync(chatId, ch, ct); break;
            case "/status": await StatusAsync(chatId, ch, ct); break;
            default: await HintAsync(chatId, ch, ct); break;
        }
    }

    // ---------------------------------------------------------------- claim

    private async Task ClaimAsync(string chatId, string text, ResolvedChannel ch, CancellationToken ct)
    {
        if (!limits.TryHit($"claim-chat:{ch.ChannelId:N}:{chatId}", ClaimPerChatPerHour, Hour)) return; // мовчки
        var failKey = $"claim-fail:{ch.ChannelId:N}";
        if (limits.Count(failKey, Hour) >= FailedPerChannelPerHour) return;

        async Task Fail()
        {
            limits.Add(failKey, Hour);
            await ReplyAsync(ch, chatId, StaffTelegramTexts.InvalidLink, null, ct);
        }

        var token = text[StartInv().Match(text).Length..];
        if (!StaffLinkToken.TryHash(token, out var hash)) { await Fail(); return; }
        var now = clock.GetUtcNow();
        var link = await db.StaffTelegramLinks.AsNoTracking().Where(l => l.TokenHash == hash)
            .Select(l => new { l.Id, l.UserId, l.ChannelId, l.Status, l.TokenExpiresAt, l.ChatIdHash }).FirstOrDefaultAsync(ct);
        if (link is null)
        {
            log.LogInformation("staff_tg_token_rejected channel={ChannelId} reason={Reason}", ch.ChannelId, "unknown");
            await Fail();
            return;
        }

        async Task Reject(string reason)
        {
            StaffTelegramRevocation.AddEvent(db, link.Id, link.UserId, "rejected", reason, "bot", null, now);
            await db.SaveChangesAsync(ct);
            log.LogInformation("staff_tg_token_rejected channel={ChannelId} reason={Reason}", ch.ChannelId, reason);
        }

        if (!options.IsActiveFor(ch.TenantId) || !await InventoryModuleGate.IsEnabledAsync(db, ct))
        {
            await Reject("disabled");
            await ReplyAsync(ch, chatId, StaffTelegramTexts.Disabled, null, ct);
            return;
        }
        if (link.ChannelId != ch.ChannelId) { await Reject("channel_mismatch"); await Fail(); return; }
        var chatHash = codec.Hash(ch.ChannelId, chatId);
        if (link.Status == "claimed" && link.ChatIdHash is { } h0 && h0.AsSpan().SequenceEqual(chatHash) && link.TokenExpiresAt > now)
        {
            await SendConfirmPromptAsync(ch, chatId, link.Id, ct); // ідемпотентно: повторний /start тим самим чатом
            return;
        }
        if (link.Status != "issued") { await Reject("used"); await Fail(); return; }
        if (link.TokenExpiresAt <= now) { await Reject("expired"); await Fail(); return; }
        var uid = link.UserId;
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == uid && u.IsActive && u.State == "active" && u.Role == Roles.Owner, ct))
        { await Reject("user_inactive"); await Fail(); return; }
        if (await db.StaffTelegramLinks.AsNoTracking().AnyAsync(l => l.ChannelId == ch.ChannelId && l.ChatIdHash == chatHash
                && (l.Status == "claimed" || l.Status == "active") && l.UserId != uid, ct))
        { await Reject("chat_conflict"); await Fail(); return; }

        int n;
        var enc = codec.Encrypt(chatId);
        var linkId = link.Id;
        try
        {
            n = await db.StaffTelegramLinks
                .Where(l => l.Id == linkId && l.ChannelId == ch.ChannelId && l.Status == "issued" && l.TokenHash == hash && l.TokenExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "claimed").SetProperty(l => l.ChatIdEncrypted, enc)
                    .SetProperty(l => l.ChatIdHash, chatHash).SetProperty(l => l.ClaimedAt, now).SetProperty(l => l.UpdatedAt, now), ct);
        }
        catch (Exception ex) when (StaffTelegramLinkService.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            await Reject("chat_conflict");
            await Fail();
            return;
        }
        if (n == 0) { await Reject("used"); await Fail(); return; } // програно гонку (інший чат / відкликано)
        StaffTelegramRevocation.AddEvent(db, link.Id, uid, "claimed", null, "bot", null, now);
        await db.SaveChangesAsync(ct);
        log.LogInformation("staff_tg_link_claimed link={LinkId}", link.Id);
        await SendConfirmPromptAsync(ch, chatId, link.Id, ct);
    }

    private async Task SendConfirmPromptAsync(ResolvedChannel ch, string chatId, Guid linkId, CancellationToken ct)
    {
        var name = await db.Channels.AsNoTracking().Where(c => c.Id == ch.ChannelId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var id22 = StaffLinkToken.LinkId22(linkId);
        var actions = new ChatActionLayout([[new ChatActionButton(StaffTelegramTexts.Confirm, $"iy:{id22}"), new ChatActionButton(StaffTelegramTexts.Cancel, $"in:{id22}")]]);
        await ReplyAsync(ch, chatId, StaffTelegramTexts.ConsentPrompt(name), actions, ct);
    }

    // ---------------------------------------------------------------- callbacks

    private async Task HandleCallbackAsync(InboundMessage m, ResolvedChannel ch, CancellationToken ct)
    {
        var chatId = m.ExternalConversationId;
        var data = m.CallbackData;
        if (data is null || data.Length < 25 || !ServiceCallback().IsMatch(data) || !StaffLinkToken.TryParseLinkId22(data.Substring(3, 22), out var linkId))
        {
            await HintAsync(chatId, ch, ct);
            return;
        }
        var verb = data[..2];
        var now = clock.GetUtcNow();
        var chatHash = codec.Hash(ch.ChannelId, chatId);
        var cid = ch.ChannelId;
        var row = await db.StaffTelegramLinks.AsNoTracking().Where(l => l.Id == linkId && l.ChannelId == cid)
            .Select(l => new { l.Id, l.UserId, l.Status, l.TokenExpiresAt, l.ChatIdHash }).FirstOrDefaultAsync(ct);
        if (row is null || row.ChatIdHash is not { } h || !h.AsSpan().SequenceEqual(chatHash) || row.Status is not ("claimed" or "active"))
        {
            await ReplyLimitedAsync(ch, chatId, StaffTelegramTexts.InvalidLink, ct); // чужий чат / не той стан: без деталей; L3 - з лімітом
            return;
        }

        if (verb == "in")
        {
            if (row.Status != "claimed") { await HintAsync(chatId, ch, ct); return; }
            if (await StaffTelegramRevocation.RevokeOneAsync(db, row.Id, row.UserId, "declined", "bot", null, now, ct))
                log.LogInformation("staff_tg_link_revoked link={LinkId} reason={Reason}", row.Id, "declined");
            await ReplyAsync(ch, chatId, StaffTelegramTexts.Declined, null, ct);
            return;
        }

        // iy
        if (row.Status == "active") { await ReplyAsync(ch, chatId, StaffTelegramTexts.Done(await OpenAlertsAsync(ct)), null, ct); return; }
        if (row.TokenExpiresAt <= now)
        {
            StaffTelegramRevocation.AddEvent(db, row.Id, row.UserId, "rejected", "expired", "bot", null, now);
            await db.SaveChangesAsync(ct);
            await ReplyAsync(ch, chatId, StaffTelegramTexts.InvalidLink, null, ct);
            return;
        }
        if (!options.IsActiveFor(ch.TenantId) || !await InventoryModuleGate.IsEnabledAsync(db, ct))
        {
            StaffTelegramRevocation.AddEvent(db, row.Id, row.UserId, "rejected", "disabled", "bot", null, now);
            await db.SaveChangesAsync(ct);
            await ReplyAsync(ch, chatId, StaffTelegramTexts.Disabled, null, ct);
            return;
        }
        var uid = row.UserId;
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == uid && u.IsActive && u.State == "active" && u.Role == Roles.Owner, ct))
        {
            StaffTelegramRevocation.AddEvent(db, row.Id, uid, "rejected", "user_inactive", "bot", null, now);
            await db.SaveChangesAsync(ct);
            await ReplyAsync(ch, chatId, StaffTelegramTexts.InvalidLink, null, ct);
            return;
        }
        var n = await db.StaffTelegramLinks.Where(l => l.Id == linkId && l.ChannelId == cid && l.Status == "claimed" && l.ChatIdHash == chatHash && l.TokenExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "active").SetProperty(l => l.TokenHash, (byte[]?)null).SetProperty(l => l.TokenExpiresAt, (DateTimeOffset?)null)
                .SetProperty(l => l.BotConfirmedAt, now).SetProperty(l => l.ActivatedAt, now).SetProperty(l => l.UpdatedAt, now), ct);
        if (n == 0) { await ReplyAsync(ch, chatId, StaffTelegramTexts.InvalidLink, null, ct); return; }
        StaffTelegramRevocation.AddEvent(db, linkId, uid, "activated", null, "bot", null, now);
        await db.SaveChangesAsync(ct);
        log.LogInformation("staff_tg_link_activated link={LinkId}", linkId);
        await ReplyAsync(ch, chatId, StaffTelegramTexts.Activated(await OpenAlertsAsync(ct)), null, ct);
    }

    private Task<int> OpenAlertsAsync(CancellationToken ct) => db.StaffAlerts.AsNoTracking().CountAsync(a => a.Status == "open", ct);

    // ---------------------------------------------------------------- /stop /status hint

    private async Task<(Guid Id, Guid UserId, string Status, DateTimeOffset? ActivatedAt)?> FindByChatAsync(string chatId, ResolvedChannel ch, CancellationToken ct)
    {
        var chatHash = codec.Hash(ch.ChannelId, chatId);
        var cid = ch.ChannelId;
        var r = await db.StaffTelegramLinks.AsNoTracking()
            .Where(l => l.ChannelId == cid && l.ChatIdHash == chatHash && (l.Status == "claimed" || l.Status == "active"))
            .Select(l => new { l.Id, l.UserId, l.Status, l.ActivatedAt }).FirstOrDefaultAsync(ct);
        return r is null ? null : (r.Id, r.UserId, r.Status, r.ActivatedAt);
    }

    private async Task StopAsync(string chatId, ResolvedChannel ch, CancellationToken ct)
    {
        if (await FindByChatAsync(chatId, ch, ct) is not { } l) return;
        if (await StaffTelegramRevocation.RevokeOneAsync(db, l.Id, l.UserId, "bot_stop", "bot", null, clock.GetUtcNow(), ct))
            log.LogInformation("staff_tg_link_revoked link={LinkId} reason={Reason}", l.Id, "bot_stop");
        await ReplyAsync(ch, chatId, StaffTelegramTexts.Stopped, null, ct);
    }

    private async Task StatusAsync(string chatId, ResolvedChannel ch, CancellationToken ct)
    {
        if (await FindByChatAsync(chatId, ch, ct) is not { } l) return;
        await ReplyAsync(ch, chatId, StaffTelegramTexts.Status(options.IsActiveFor(ch.TenantId), l.ActivatedAt, l.Status == "claimed"), null, ct);
    }

    private async Task HintAsync(string chatId, ResolvedChannel ch, CancellationToken ct)
    {
        if (await FindByChatAsync(chatId, ch, ct) is not { } l) { await ReplyLimitedAsync(ch, chatId, StaffTelegramTexts.InvalidLink, ct); return; }
        if (limits.TryHit($"hint:{l.Id:N}", 1, HintWindow)) await ReplyAsync(ch, chatId, StaffTelegramTexts.Hint, null, ct);
    }

    /// <summary>L3: відповіді на підроблені callback / неприв'язані чати - не частіше SvcReplyPerWindow за вікно на чат (мовчки).</summary>
    private async Task ReplyLimitedAsync(ResolvedChannel ch, string chatId, string text, CancellationToken ct)
    {
        if (!limits.TryHit($"svc-reply:{ch.ChannelId:N}:{chatId}", SvcReplyPerWindow, SvcReplyWindow)) return;
        await ReplyAsync(ch, chatId, text, null, ct);
    }

    // ---------------------------------------------------------------- transport

    /// <summary>Пряма best-effort відправка (тайм-аут 3 с, без повторів); збій лише в лозі.</summary>
    private async Task ReplyAsync(ResolvedChannel ch, string chatId, string text, ChatActionLayout? actions, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(InboundIngestCore.AckTimeout);
            await transport.SendTextAsync(ch, chatId, text, actions, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogWarning("staff_tg_command_failed reason={Reason}", "reply_" + ((ex as PermanentChannelException)?.Code ?? ex.GetType().Name)); }
    }
}
