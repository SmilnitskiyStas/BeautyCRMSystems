using System.Security.Cryptography;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

/// <summary>
/// Клавіатури вихідних повідомлень (§23.3, §23.5, ADR-013 §2.2): створення (токен 128 біт, options, TTL), перевірка натискання у
/// порядку §23.5 (формат -> токен під RLS -> збіг розмови -> збіг message_ref -> verb/index -> статус/TTL -> політика handoff),
/// CAS <c>active -> used</c>, відкликання, протермінування, вибірка для sweeper. Транспорту не торкається: повертає типізований результат.
/// Усі запити йдуть під RLS tenant-а контексту; чужий tenant = токен не знайдено. Логи: лише id і коди (без токена, callback_data,
/// chat id, підписів і текстів). Виконується в поточній транзакції контексту або у власній.
/// </summary>
public sealed class ChatKeyboardService(BeautyDbContext db, ChatButtonsOptions opts, TimeProvider clock, ILogger<ChatKeyboardService> log)
{
    private static readonly TimeSpan DefaultReminderChoiceTtl = TimeSpan.FromHours(24);

    // ------------------------------------------------------------------ створення

    /// <summary>
    /// Створює клавіатуру для вихідного повідомлення (UNIQUE на message_id). Нова клавіатура booking_confirm / slot_pick /
    /// reminder_choice / cancel_confirm відкликає попередню активну того ж виду в розмові. Помилки: <c>conversation_not_found</c>,
    /// <c>message_not_found</c>, <c>keyboard_invalid</c>, <c>proposal_not_found</c>, <c>appointment_not_found</c>.
    /// </summary>
    public async Task<Result<CreatedKeyboard>> CreateAsync(NewKeyboard req, CancellationToken ct)
    {
        if (!KeyboardKinds.All.Contains(req.Kind) || req.Options.Count is 0 or > ChatKeyboardLayout.MaxButtons
            || req.Options.Any(o => string.IsNullOrWhiteSpace(o.Label) || !ButtonVerbs.AllowedFor(req.Kind, o.Verb))
            || (req.Kind == KeyboardKinds.BookingConfirm && req.ProposalId is null)
            || (req.Kind is KeyboardKinds.AppointmentActions or KeyboardKinds.CancelConfirm or KeyboardKinds.ReminderChoice && req.AppointmentId is null))
        {
            log.LogWarning("keyboard_invalid conversation={Conversation} kind={Kind}", req.ConversationId, req.Kind);
            return Error.Validation("keyboard_invalid", "Keyboard is invalid.");
        }

        var now = clock.GetUtcNow();
        var conv = await db.Conversations.AsNoTracking().Where(c => c.Id == req.ConversationId).Select(c => new { c.Id, c.ClientId }).FirstOrDefaultAsync(ct);
        if (conv is null) return Error.NotFound("conversation_not_found", "Conversation not found.");
        if (!await db.Messages.AsNoTracking().AnyAsync(m => m.Id == req.MessageId && m.ConversationId == req.ConversationId
                && m.Direction == MessageDirection.Outbound, ct))
            return Error.NotFound("message_not_found", "Message not found.");

        DateTimeOffset? proposalExpires = null;
        if (req.ProposalId is { } pid)
        {
            proposalExpires = await db.ChatBookingProposals.AsNoTracking().Where(p => p.Id == pid && p.ConversationId == req.ConversationId)
                .Select(p => (DateTimeOffset?)p.ExpiresAt).FirstOrDefaultAsync(ct);
            if (proposalExpires is null) return Error.NotFound("proposal_not_found", "Proposal not found.");
        }
        DateTimeOffset? apptStarts = null;
        if (req.AppointmentId is { } aid)
        {
            // Запис мусить належати клієнту цієї розмови (клавіатура не може вказувати на чужий запис).
            apptStarts = conv.ClientId is { } cid
                ? await db.Appointments.AsNoTracking().Where(a => a.Id == aid && a.ClientId == cid).Select(a => (DateTimeOffset?)a.StartsAt).FirstOrDefaultAsync(ct)
                : null;
            if (apptStarts is null) return Error.NotFound("appointment_not_found", "Appointment not found.");
        }
        if (req.Options.Any(o => o.AppointmentId != null) && !await OptionAppointmentsBelongAsync(conv.ClientId, req.Options, ct))
            return Error.NotFound("appointment_not_found", "Appointment not found.");

        var expires = req.ExpiresAt ?? req.Kind switch
        {
            KeyboardKinds.SlotPick => now.AddMinutes(opts.SlotPickTtlMinutes),
            KeyboardKinds.AppointmentList => now.AddMinutes(opts.ListTtlMinutes),
            KeyboardKinds.CancelConfirm => now.AddMinutes(opts.CancelConfirmTtlMinutes),
            KeyboardKinds.BookingConfirm => proposalExpires!.Value,
            KeyboardKinds.AppointmentActions => apptStarts!.Value,
            _ => now.Add(DefaultReminderChoiceTtl),
        };
        if (expires <= now) return Error.Validation("keyboard_invalid", "Keyboard is already expired.");

        var inOwnTx = db.Database.CurrentTransaction is null;
        await using var tx = inOwnTx ? await db.Database.BeginTransactionAsync(ct) : null;
        var revoked = new List<Guid>();
        if (KeyboardKinds.ReplacesPrevious(req.Kind))
        {
            revoked = await db.ChatKeyboards.Where(k => k.ConversationId == req.ConversationId && k.Kind == req.Kind && k.Status == KeyboardStatuses.Active)
                .Select(k => k.Id).ToListAsync(ct);
            if (revoked.Count > 0)
                await db.ChatKeyboards.Where(k => revoked.Contains(k.Id) && k.Status == KeyboardStatuses.Active)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, KeyboardStatuses.Revoked).SetProperty(k => k.UpdatedAt, now), ct);
        }

        var entity = new ChatKeyboard
        {
            Id = Guid.NewGuid(), ConversationId = req.ConversationId, MessageId = req.MessageId, Kind = req.Kind, Token = NewToken(),
            Options = KeyboardJson.Serialize(req.Options.Select(o => o with { Label = ChatKeyboardLayout.SanitizeLabel(o.Label) }).ToList()),
            ProposalId = req.ProposalId, AppointmentId = req.AppointmentId, Status = KeyboardStatuses.Active, ExpiresAt = expires,
        };
        db.ChatKeyboards.Add(entity);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        db.Entry(entity).State = EntityState.Detached;
        log.LogInformation("chat_keyboard_created conversation={Conversation} keyboard={Keyboard} kind={Kind}", req.ConversationId, entity.Id, req.Kind);
        return new CreatedKeyboard(entity.Id, entity.Token, expires, revoked);
    }

    private async Task<bool> OptionAppointmentsBelongAsync(Guid? clientId, IReadOnlyList<KeyboardOption> options, CancellationToken ct)
    {
        if (clientId is not { } cid) return false;
        var ids = options.Where(o => o.AppointmentId != null).Select(o => o.AppointmentId!.Value).Distinct().ToList();
        return await db.Appointments.AsNoTracking().CountAsync(a => ids.Contains(a.Id) && a.ClientId == cid, ct) == ids.Count;
    }

    /// <summary>22 символи base64url з 16 байт CSPRNG (128 біт).</summary>
    internal static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ------------------------------------------------------------------ перевірка натискання (§23.5)

    /// <summary>
    /// Кроки 1-8 §23.5 для розмови, у якій прийшло натискання (розмову транспорт визначає за chat.id каналу). Побічних ефектів у транспорті
    /// немає; єдиний запис - перехід протермінованої клавіатури в <c>expired</c>. Доменні перевірки (9) і CAS (10) - у <see cref="ChatButtonActions"/>.
    /// </summary>
    /// <param name="inboundMessageId">Вхідний callback-рядок: повторна обробка того самого натискання (клавіатура вже використана ним) = Replay.</param>
    public async Task<CallbackResolution> ResolveCallbackAsync(
        Guid conversationId, string? callbackData, string? callbackMessageRef, Guid? inboundMessageId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        CallbackResolution Done(CallbackOutcome outcome, CallbackAddress? a = null, KeyboardInfo? kb = null, KeyboardOption? opt = null, int pos = 0, bool replay = false)
        {
            var r = new CallbackResolution(outcome, a?.Verb ?? "", a?.Index, kb, opt, pos, replay, now);
            var line = "chat_callback conversation={Conversation} keyboard={Keyboard} kind={Kind} verb={Verb} result={Result}";
            var level = outcome is CallbackOutcome.ForeignConversation or CallbackOutcome.RefMismatch ? LogLevel.Warning : LogLevel.Information;
            log.Log(level, line, conversationId, kb?.Id.ToString() ?? "-", kb?.Kind ?? "-", a?.Verb ?? "-", r.Code);
            return r;
        }

        // 1. формат
        if (!CallbackAddress.TryParse(callbackData, out var addr)) return Done(CallbackOutcome.InvalidFormat);

        var conv = await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId)
            .Select(c => new { c.AiState, c.AiPausedUntil, c.HandoffAt, c.StaffRepliedAt, ChannelSettings = c.Channel!.Settings }).FirstOrDefaultAsync(ct);
        if (conv is null) return Done(CallbackOutcome.UnknownToken, addr);
        var handoff = conv.AiState == "handoff"
            && (conv.AiPausedUntil is null || conv.AiPausedUntil > now
                || (conv.StaffRepliedAt is { } replied && (conv.HandoffAt is null || replied >= conv.HandoffAt)));

        // L10 (TASK-759): кнопки вимкнено глобально (ChatButtons:Enabled) або на каналі (settings.assistant.buttons=false).
        var buttonsOn = opts.Enabled && ChannelButtonsAllowed(conv.ChannelSettings);

        // 2. безстанова дія: далі лише політика handoff (частотний ліміт - у конвеєрі)
        if (addr.IsStateless)
        {
            // «Стоп» - відписка від власних нагадувань, діє завжди; решта безстанових кнопок старої клавіатури -> Disabled + прибрати розмітку.
            if (!buttonsOn && addr.Verb != ButtonVerbs.Stop)
                return Done(CallbackOutcome.Disabled, addr, await KeyboardOfMessageAsync(conversationId, callbackMessageRef, ct));
            return handoff && !ButtonVerbs.AllowedInHandoff(addr.Verb) ? Done(CallbackOutcome.HandoffBlocked, addr) : Done(CallbackOutcome.Stateless, addr);
        }

        // 3. токен під RLS tenant-а каналу (чужий tenant = не знайдено)
        var k = await db.ChatKeyboards.AsNoTracking().FirstOrDefaultAsync(x => x.Token == addr.Token, ct);
        if (k is null) return Done(CallbackOutcome.UnknownToken, addr);

        // 4. збіг розмови (клавіатуру чужої розмови не розкриваємо й не чіпаємо)
        if (k.ConversationId != conversationId) return Done(CallbackOutcome.ForeignConversation, addr);
        var info = new KeyboardInfo(k.Id, k.Kind, k.Status, k.MessageId, k.ProposalId, k.AppointmentId, k.ExpiresAt, k.UsedAt, k.UsedByMessageId);

        // 5. повідомлення клавіатури відправлене; якщо відомий external_message_id - збіг із message_ref
        var msg = await db.Messages.AsNoTracking().Where(m => m.Id == k.MessageId && m.ConversationId == conversationId)
            .Select(m => new { m.Status, m.ExternalMessageId }).FirstOrDefaultAsync(ct);
        if (msg is null || msg.Status != "sent" || (msg.ExternalMessageId is { } ext && ext != callbackMessageRef))
            return Done(CallbackOutcome.RefMismatch, addr, info);

        // 6. verb дозволений для виду; index є в options (і відсутній, де не потрібен)
        var options = KeyboardJson.TryParse(k.Options);
        if (options is null || !ButtonVerbs.AllowedFor(k.Kind, addr.Verb)) return Done(CallbackOutcome.InvalidOption, addr, info);
        int pos;
        if (ButtonVerbs.NeedsIndex(k.Kind, addr.Verb))
        {
            if (addr.Index is not { } i || i >= options.Count || options[i].Verb != addr.Verb) return Done(CallbackOutcome.InvalidOption, addr, info);
            pos = i;
        }
        else
        {
            if (addr.Index is not null) return Done(CallbackOutcome.InvalidOption, addr, info);
            pos = options.ToList().FindIndex(o => o.Verb == addr.Verb);
            if (pos < 0) return Done(CallbackOutcome.InvalidOption, addr, info);
        }
        var option = options[pos];

        // 7. статус і TTL (протермінована активна -> expired)
        var replay = k.Status == KeyboardStatuses.Used && inboundMessageId is { } inbound && k.UsedByMessageId == inbound;
        if (k.Status == KeyboardStatuses.Used && !replay) return Done(CallbackOutcome.Used, addr, info, option, pos);
        if (k.Status == KeyboardStatuses.Revoked) return Done(CallbackOutcome.Revoked, addr, info, option, pos);
        if (k.Status == KeyboardStatuses.Expired) return Done(CallbackOutcome.Expired, addr, info, option, pos);
        if (!replay && now >= k.ExpiresAt)
        {
            await db.ChatKeyboards.Where(x => x.Id == k.Id && x.Status == KeyboardStatuses.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, KeyboardStatuses.Expired).SetProperty(x => x.UpdatedAt, now), ct);
            return Done(CallbackOutcome.Expired, addr, info with { Status = KeyboardStatuses.Expired }, option, pos);
        }

        // вимкнені кнопки: стара клавіатура -> Stale + прибирання
        if (!buttonsOn) return Done(CallbackOutcome.Disabled, addr, info, option, pos);

        // 8. політика handoff
        if (handoff && !ButtonVerbs.AllowedInHandoff(addr.Verb)) return Done(CallbackOutcome.HandoffBlocked, addr, info, option, pos);

        return Done(CallbackOutcome.Valid, addr, info, option, pos, replay);
    }

    /// <summary>Кнопки каналу не вимкнено (<c>settings.assistant.buttons=false</c>); порожні/зіпсовані налаштування - дозволено.</summary>
    internal static bool ChannelButtonsAllowed(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings)) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(settings);
            return !(doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("assistant", out var a)
                     && a.ValueKind == System.Text.Json.JsonValueKind.Object && a.TryGetProperty("buttons", out var b)
                     && b.ValueKind == System.Text.Json.JsonValueKind.False);
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    /// <summary>Клавіатура повідомлення цієї розмови за його external_message_id (для безстанових кнопок старої клавіатури); без збігу - null.</summary>
    private async Task<KeyboardInfo?> KeyboardOfMessageAsync(Guid conversationId, string? messageRef, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(messageRef)) return null;
        var k = await (from kb in db.ChatKeyboards.AsNoTracking()
                       join m in db.Messages.AsNoTracking() on kb.MessageId equals m.Id
                       where kb.ConversationId == conversationId && m.ConversationId == conversationId && m.ExternalMessageId == messageRef
                       select kb).FirstOrDefaultAsync(ct);
        return k is null ? null : new KeyboardInfo(k.Id, k.Kind, k.Status, k.MessageId, k.ProposalId, k.AppointmentId, k.ExpiresAt, k.UsedAt, k.UsedByMessageId);
    }

    // ------------------------------------------------------------------ CAS і зміна стану

    /// <summary>
    /// Крок 10 §23.5: <c>UPDATE ... SET status='used' WHERE id=@id AND status='active' AND expires_at&gt;now</c>.
    /// true лише для ОДНОГО з паралельних натискань; false = вже використана/відкликана/протермінована.
    /// </summary>
    public async Task<bool> TryConsumeAsync(Guid keyboardId, int optionPosition, Guid usedByMessageId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pos = (short)Math.Clamp(optionPosition, 0, 9);
        return await db.ChatKeyboards.Where(k => k.Id == keyboardId && k.Status == KeyboardStatuses.Active && k.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, KeyboardStatuses.Used).SetProperty(k => k.UsedAt, now)
                .SetProperty(k => k.UsedOption, (short?)pos).SetProperty(k => k.UsedByMessageId, (Guid?)usedByMessageId)
                .SetProperty(k => k.UpdatedAt, now), ct) == 1;
    }

    /// <summary>Відкликає активні клавіатури розмови заданих видів (вхід у handoff: booking_confirm + slot_pick; зняте питання: reminder_choice). Повертає id.</summary>
    public Task<IReadOnlyList<Guid>> RevokeActiveAsync(Guid conversationId, IReadOnlyCollection<string> kinds, CancellationToken ct) =>
        RevokeAsync(db.ChatKeyboards.Where(k => k.ConversationId == conversationId && kinds.Contains(k.Kind)), ct);

    /// <summary>Пропозиція покинула shown|prepared (текст, відмова, supersede): її booking_confirm відкликається.</summary>
    public Task<IReadOnlyList<Guid>> RevokeForProposalAsync(Guid conversationId, Guid proposalId, CancellationToken ct) =>
        RevokeAsync(db.ChatKeyboards.Where(k => k.ConversationId == conversationId && k.ProposalId == proposalId), ct);

    private async Task<IReadOnlyList<Guid>> RevokeAsync(IQueryable<ChatKeyboard> scope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await scope.Where(k => k.Status == KeyboardStatuses.Active).Select(k => k.Id).ToListAsync(ct);
        if (ids.Count == 0) return ids;
        // Умовний UPDATE: клавіатуру, яку тим часом використали, не чіпаємо.
        var revoked = await db.ChatKeyboards.Where(k => ids.Contains(k.Id) && k.Status == KeyboardStatuses.Active).Select(k => k.Id).ToListAsync(ct);
        await db.ChatKeyboards.Where(k => revoked.Contains(k.Id) && k.Status == KeyboardStatuses.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, KeyboardStatuses.Revoked).SetProperty(k => k.UpdatedAt, now), ct);
        return revoked;
    }

    /// <summary>Sweeper: активні з <c>expires_at &lt;= now</c> -> expired (до <paramref name="batch"/> за виклик). Повертає кількість.</summary>
    public async Task<int> ExpireDueAsync(int batch, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await db.ChatKeyboards.Where(k => k.Status == KeyboardStatuses.Active && k.ExpiresAt <= now).OrderBy(k => k.ExpiresAt)
            .Select(k => k.Id).Take(Math.Clamp(batch, 1, 1000)).ToListAsync(ct);
        return ids.Count == 0 ? 0 : await db.ChatKeyboards.Where(k => ids.Contains(k.Id) && k.Status == KeyboardStatuses.Active && k.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, KeyboardStatuses.Expired).SetProperty(k => k.UpdatedAt, now), ct);
    }

    // ------------------------------------------------------------------ прибирання розмітки (sweeper, §23.4)

    /// <summary>
    /// Клавіатури не в <c>active</c>, розмітку яких ще не прибрано: <c>clear_attempts &lt; 3</c>, <c>updated_at &gt; now-24h</c>. Ті, що без
    /// <c>external_message_id</c>, закриваються одразу (<c>markup_cleared_at=now</c>) і не повертаються.
    /// </summary>
    public async Task<IReadOnlyList<PendingClear>> ListPendingClearAsync(int take, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-24);
        var rows = await (from k in db.ChatKeyboards.AsNoTracking()
                          where k.Status != KeyboardStatuses.Active && k.MarkupClearedAt == null && k.ClearAttempts < 3 && k.UpdatedAt > since
                          join m in db.Messages.AsNoTracking() on k.MessageId equals m.Id
                          join c in db.Conversations.AsNoTracking() on k.ConversationId equals c.Id
                          orderby k.UpdatedAt
                          select new { k.Id, c.ChannelId, c.ExternalChatId, m.ExternalMessageId })
            .Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var withoutRef = rows.Where(r => string.IsNullOrEmpty(r.ExternalMessageId)).Select(r => r.Id).ToList();
        if (withoutRef.Count > 0) await MarkClearedAsync(withoutRef, ct);
        return rows.Where(r => !string.IsNullOrEmpty(r.ExternalMessageId))
            .Select(r => new PendingClear(r.Id, r.ChannelId, r.ExternalChatId, r.ExternalMessageId!)).ToList();
    }

    /// <summary>Розмітку прибрано (успіх або «message is not modified» / «not found»).</summary>
    public Task<int> MarkClearedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.ChatKeyboards.Where(k => ids.Contains(k.Id) && k.MarkupClearedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.MarkupClearedAt, (DateTimeOffset?)now).SetProperty(k => k.UpdatedAt, now), ct);
    }

    /// <summary>Невдала спроба прибрати розмітку (5xx, мережа): ще одна спроба до 3.</summary>
    public Task<int> RecordClearFailureAsync(Guid keyboardId, CancellationToken ct) =>
        db.ChatKeyboards.Where(k => k.Id == keyboardId && k.MarkupClearedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ClearAttempts, k => (short)(k.ClearAttempts + 1)), ct);

    /// <summary>Заміна <c>"[кнопка]"</c> на <c>"[кнопка] {підпис}"</c> у вхідному callback (inbox, §23.6); підпис - з options у БД. Лише inbound callback.</summary>
    public Task<int> SetCallbackBodyAsync(Guid inboundMessageId, string? label, CancellationToken ct)
    {
        var body = CallbackBody.Build(label);
        return db.Messages.Where(m => m.Id == inboundMessageId && m.Kind == "callback" && m.Direction == MessageDirection.Inbound)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Body, body), ct);
    }
}
