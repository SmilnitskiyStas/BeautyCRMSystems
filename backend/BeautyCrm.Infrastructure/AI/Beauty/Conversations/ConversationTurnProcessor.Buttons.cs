using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

// TASK-753 (§23.5-§23.8, ADR-013): кнопки й slash-команди в конвеєрі. Усе без LLM; клавіатури формує лише код (ChatKeyboardBuilder).
public sealed partial class ConversationTurnProcessor
{
    private const string CallbackKeyPrefix = "cb-reply:";
    private static readonly TimeSpan StaleThrottle = TimeSpan.FromMinutes(10);

    private static string CallbackKey(Guid inboundCallbackId) => $"{CallbackKeyPrefix}{inboundCallbackId}";

    // ---------------------------------------------------------------- допоміжне

    /// <summary>Перший сегмент пакета (§23.6): callback - один; інакше всі суміжні не-callback.</summary>
    private static List<Message> FirstSegment(List<Message> batch) =>
        batch[0].Kind == InboundKinds.Callback ? [batch[0]] : batch.TakeWhile(m => m.Kind != InboundKinds.Callback).ToList();

    /// <summary>Кнопки показуються: глобально ввімкнені, адаптер каналу підтримує actions, канал не вимкнув (<c>assistant.buttons=false</c>).</summary>
    private bool ButtonsAvailable(Conversation conv) =>
        ButtonsOpts.Enabled && ChannelButtonsAllowed(conv.Channel?.Settings)
        && services.GetService<ChannelRegistry>()?.Find(ChannelIds.Telegram) is { SupportsActions: true };

    private static bool ChannelButtonsAllowed(string? settings) => ChatKeyboardService.ChannelButtonsAllowed(settings);

    private AiActionRecord HandoffRecord(Turn t, string reason, string source) =>
        new(Guid.NewGuid(), t.Conv.TenantId, "handoff", $"conversation:{t.Conv.Id}", t.Now, AiActionStatus.HandedOff, false, t.Mode,
            JsonSerializer.Serialize(new { reason, source }), t.Conv.Id);

    private async Task RevokeProposalKeyboardsAsync(Turn t, Guid proposalId, CancellationToken ct) =>
        t.ToClear.AddRange(await Keyboards.RevokeForProposalAsync(t.Conv.Id, proposalId, ct));

    private async Task ClearMarkupsAsync(Turn t, CancellationToken ct)
    {
        if (t.ToClear.Count == 0) return;
        var ids = t.ToClear.Distinct().ToList();
        t.ToClear.Clear();
        try
        {
            await Cleaner.ClearAsync(ids, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
        {
            log.LogWarning("chat_clear_failed conversation={ConversationId} error={ErrorType}", t.Conv.Id, ex.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- коміт (кілька повідомлень, клавіатури)

    private async Task CommitManyAsync(Turn t, CommitSpec spec, CancellationToken ct)
    {
        var conv = t.Conv;
        var now = clock.GetUtcNow();
        await RenewLeaseAsync(conv.Id, ct);
        var batchIds = t.Batch.Select(m => m.Id).ToList();
        var baseKey = spec.CallbackInboundId is { } cb ? CallbackKey(cb) : ReplyKey(conv.Id, t.Batch[^1].Id);
        var created = new List<(Guid Id, Reply Reply)>();
        var clear = new List<Guid>();

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                // §25.5.3: дії над номером клієнта виконуються ПЕРЕД вставкою відповідей (текст залежить від результату), у цій самій транзакції
                var replies = spec.Replies;
                var composedPrompt = spec.Prompt;
                if (spec.ComposeReplies is not null)
                {
                    var composed = await spec.ComposeReplies(ct);
                    replies = composed.Replies;
                    composedPrompt = composed.Prompt ?? spec.Prompt;
                }
                foreach (var reply in replies)
                {
                    var messageId = Guid.NewGuid();
                    // L8: вихідне Telegram ≤ 4096 одиниць UTF-16. Підсумок запису (":s") обмежений за побудовою; решта обрізається без розриву пари.
                    var body = ChatOutputGuard.TruncateUtf16(reply.Body, ChatOutputGuard.MaxBodyLength);
                    db.Messages.Add(new Message
                    {
                        Id = messageId, ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = reply.Sender,
                        Body = body, Kind = InboundKinds.Text, SentAt = now, Status = reply.Draft ? "draft" : "sending",
                        IdempotencyKey = baseKey + reply.KeySuffix, Actions = reply.ActionsJson,
                    });
                    await db.SaveChangesAsync(ct);
                    // M1: лише зв'язок пропозиції з повідомленням підсумку; prepared -> shown після ДОСТАВКИ (ChatSummaryTracker).
                    if (reply.ShowProposalId is { } pid && await db.ChatBookingProposals
                            .Where(x => x.ConversationId == conv.Id && x.Id == pid && x.Status == ChatProposalStatus.Prepared)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SummaryMessageId, messageId), ct) != 1)
                        throw new SummaryNotShownException();
                    if (reply.Draft)
                        await journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), conv.TenantId, AiToolNames.DraftReply, $"conversation:{conv.Id}", now,
                            AiActionStatus.PendingConfirmation, false, t.Mode,
                            JsonSerializer.Serialize(new { draftId = messageId, text = body, conversationId = conv.Id }), conv.Id), ct);
                    if (reply.Keyboard is { } plan) await AttachKeyboardAsync(conv, messageId, plan, clear, ct);
                    created.Add((messageId, reply));
                }
                foreach (var r in spec.Journal ?? []) await journal.AppendAsync(r, ct);
                if (spec.TxWork is not null) await spec.TxWork(ct); // дія коду в ТІЙ САМІЙ транзакції (вибір нагадування, «Стоп»)

                if (composedPrompt is { } prompt)
                    await db.Conversations.Where(c => c.Id == conv.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.PendingPrompt, prompt.Kind)
                        .SetProperty(c => c.PendingPromptAppointmentId, prompt.AppointmentId)
                        .SetProperty(c => c.PendingPromptExpiresAt, (DateTimeOffset?)prompt.ExpiresAt.ToUniversalTime()), ct);
                if (spec.HandoffReason is { } handoffReason)
                {
                    var reasonCol = handoffReason.Length > 32 ? handoffReason[..32] : handoffReason;
                    var pausedUntil = now.AddHours(opts.HandoffPauseHours);
                    // §22.8: handoff_at, awaiting_staff_since = coalesce(…, now) (позначка «потрібна відповідь»)
                    await db.Conversations.Where(c => c.Id == conv.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.AiState, "handoff").SetProperty(c => c.HandoffReason, reasonCol)
                        .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)pausedUntil)
                        .SetProperty(c => c.HandoffAt, (DateTimeOffset?)now)
                        .SetProperty(c => c.AwaitingStaffSince, c => c.AwaitingStaffSince ?? now), ct);
                }
                else if (spec.Hold)
                    await db.Conversations.Where(c => c.Id == conv.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.AwaitingStaffSince, c => c.AwaitingStaffSince ?? now), ct);

                // §23.6 «Відкликання»: вхід у handoff гасить booking_confirm/slot_pick; знято питання про нагадування - гасне reminder_choice.
                if (spec.RevokeAiKeyboards || spec.HandoffReason is not null)
                    clear.AddRange(await Keyboards.RevokeActiveAsync(conv.Id, [KeyboardKinds.BookingConfirm, KeyboardKinds.SlotPick], ct));
                if (spec.RevokeReminderKeyboard || !await db.Conversations.AnyAsync(c => c.Id == conv.Id && c.PendingPrompt != null, ct))
                    clear.AddRange(await Keyboards.RevokeActiveAsync(conv.Id, [KeyboardKinds.ReminderChoice], ct));
                await UpdateReplyKeyboardStateAsync(conv.Id, spec.OpenReplyKeyboard, created, now, ct);
                if (spec.RedactInboundBody is { } redacted)
                    await db.Messages.Where(m => batchIds.Contains(m.Id))
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Body, redacted), ct);

                if (created.Count > 0)
                    await db.Conversations.Where(c => c.Id == conv.Id && (c.LastMessageAt == null || c.LastMessageAt < now))
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageAt, (DateTimeOffset?)now), ct);
                if (spec.CallbackInboundId is { } inboundId && spec.CallbackLabel is not null)
                    await Keyboards.SetCallbackBodyAsync(inboundId, spec.CallbackLabel, ct);
                var status = spec.InboundStatus;
                await db.Messages.Where(m => batchIds.Contains(m.Id) && m.Status == Received)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, status).SetProperty(m => m.UpdatedAt, now), ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Паралельний запуск уже зафіксував відповідь з цим ключем: другої немає.
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                await SetStatusAsync(t.Batch, Processed, ct);
                return;
            }
            catch (SummaryNotShownException)
            {
                // Підсумок не прив'язався (пропозиція вже не prepared): клієнту не показуємо підсумок, який не можна підтвердити.
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                await CommitManyAsync(t, spec with { Replies = [new Reply(ChatTemplates.Fallback, MessageSenderType.System)] }, ct);
                return;
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw; // tx відкочується при Dispose
            }
        }
        db.ChangeTracker.Clear();
        t.ToClear.AddRange(clear);

        foreach (var (id, reply) in created)
            if (!reply.Draft) await DispatchInlineAsync(conv.TenantId, id, reply.ShowProposalId is not null, ct);
        await ClearMarkupsAsync(t, ct);
    }

    /// <summary>Створює рядок клавіатури (токен, options), будує <c>actions</c> повідомлення. Невалідна розкладка - повідомлення без кнопок (лог <c>keyboard_invalid</c>).</summary>
    private async Task AttachKeyboardAsync(Conversation conv, Guid messageId, KeyboardPlan plan, List<Guid> clear, CancellationToken ct)
    {
        string json;
        if (!plan.IsValid())
        {
            log.LogWarning("keyboard_invalid conversation={ConversationId} kind={Kind}", conv.Id, plan.Kind ?? "-");
            return;
        }
        if (plan.IsStateless) json = ChatKeyboardLayout.ToActionsJson(plan.Render(null));
        else
        {
            var r = await Keyboards.CreateAsync(new NewKeyboard(conv.Id, messageId, plan.Kind!, plan.Options, plan.ExpiresAt, plan.ProposalId, plan.AppointmentId), ct);
            if (!r.IsOk) return; // CreateAsync уже залогував keyboard_invalid; повідомлення піде без кнопок
            clear.AddRange(r.Value!.Revoked);
            json = ChatKeyboardLayout.ToActionsJson(plan.Render(r.Value));
        }
        await db.Messages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Actions, json), ct);
    }

    // ---------------------------------------------------------------- handoff: held + повідомлення

    /// <summary>Вхідні під час паузи: `held`; на відомі slash-команди (крім /stop) - T-action-handoff не частіше разу на 10 хв (§23.6).</summary>
    private async Task HoldWithNoticeAsync(Turn t, CancellationToken ct)
    {
        var isCommand = t.Batch.Any(m => m.Kind == InboundKinds.Command && ChatCommands.Parse(m.Body) is { } c && c != ChatCommand.Unknown);
        if (!isCommand || !t.AutoReplies || await RecentTemplateAsync(t.Conv.Id, ChatTemplates.ActionHandoff, StaleThrottle, t.Now, ct))
        {
            await HoldAsync(t.Conv.Id, t.Batch, t.Now, ct);
            return;
        }
        await CommitManyAsync(t, new CommitSpec([new Reply(ChatTemplates.ActionHandoff, MessageSenderType.System)]) { InboundStatus = Held, Hold = true, RevokeAiKeyboards = true }, ct);
    }

    // ---------------------------------------------------------------- slash-команди

    private async Task HandleCommandAsync(Turn t, ChatCommand command, CancellationToken ct)
    {
        var conv = t.Conv;
        var buttons = ButtonsAvailable(conv);
        switch (command)
        {
            case ChatCommand.Start:
                if (!t.AutoReplies) { await SetStatusAsync(t.Batch, Processed, ct); return; }
                if (await IdenticalRecentAsync(t, ChatTemplates.Greeting, ct) || !await PassTemplateGateAsync(t, ct)) return;
                await CommitAsync(t, new Reply(ChatTemplates.Greeting, MessageSenderType.System, Keyboard: buttons ? KeyboardBuilder.MainMenu() : null), null, null, ct);
                return;
            case ChatCommand.Book:
                if (!t.AutoReplies) { await SetStatusAsync(t.Batch, Processed, ct); return; }
                if (await IdenticalRecentAsync(t, ChatTemplates.BookStart, ct) || !await PassTemplateGateAsync(t, ct)) return;
                await CommitAsync(t, new Reply(ChatTemplates.BookStart, MessageSenderType.System), null, null, ct);
                return;
            case ChatCommand.Help:
                if (!t.AutoReplies) { await SetStatusAsync(t.Batch, Processed, ct); return; }
                if (await IdenticalRecentAsync(t, ChatTemplates.Help, ct) || !await PassTemplateGateAsync(t, ct)) return;
                await CommitAsync(t, new Reply(ChatTemplates.Help, MessageSenderType.System, Keyboard: buttons ? KeyboardBuilder.HumanOnly() : null), null, null, ct);
                return;
            case ChatCommand.My:
                if (!await PassTemplateGateAsync(t, ct)) return;
                await CommitAsync(t, MyListReply((await ClientAppointments.ListUpcomingAsync(conv.Id, ct)).Items, buttons), null, null, ct);
                return;
            case ChatCommand.Cancel:
                await HandleCancelCommandAsync(t, buttons, ct);
                return;
            case ChatCommand.Marketing: // §26.9.2: меню згоди; за Marketing:Enabled=false сюди не доходить (T-text-only), у suggest відповідає шаблон
                if (!await PassTemplateGateAsync(t, ct)) return;
                await CommitAsync(t, await MarketingMenuReplyAsync(t, ct), null, null, ct);
                return;
            default: // Stop обробляє розбиття пакета; сюди не доходить
                await HandleStopAsync(t, ct);
                return;
        }
    }

    private Reply MyListReply(IReadOnlyList<ChatAppointmentInfo> items, bool buttons)
    {
        if (items.Count == 0)
            return new Reply(ChatTemplates.MyEmpty, MessageSenderType.System, Keyboard: buttons ? KeyboardBuilder.BookOnly() : null);
        var text = ChatTemplates.MyList(items.Select(a => (a.StartsAt, a.Timezone, a.ServiceName, a.SpecialistName, a.LocationName)).ToList());
        if (!buttons) text += "\nЩоб скасувати чи змінити запис, напишіть сюди — адміністратор допоможе.";
        return new Reply(text, MessageSenderType.System, Keyboard: buttons ? KeyboardBuilder.AppointmentList(items) : null);
    }

    /// <summary>/cancel (§23.7 п. 6): 0 записів - T-my-empty, 1 - одразу екран, >1 - перелік. Без кнопок або з вимкненим скасуванням - як текстове «скасуйте»: передача адміністратору.</summary>
    private async Task HandleCancelCommandAsync(Turn t, bool buttons, CancellationToken ct)
    {
        if (!buttons || !ButtonsOpts.CancelEnabled)
        {
            await HandoffAsync(t, "cancel_request", null, ct);
            return;
        }
        if (!await PassTemplateGateAsync(t, ct)) return;
        var items = (await ClientAppointments.ListUpcomingAsync(t.Conv.Id, ct)).Items;
        if (items.Count == 1)
        {
            await CommitAsync(t, await CancelScreenReplyAsync(t.Conv.Id, items[0].Id, ct), null, null, ct);
            return;
        }
        await CommitAsync(t, MyListReply(items, true), null, null, ct);
    }

    private async Task<Reply> CancelScreenReplyAsync(Guid conversationId, Guid appointmentId, CancellationToken ct)
    {
        var check = await ClientAppointments.CheckCancelAsync(conversationId, appointmentId, enforceLimit: false, ct);
        if (check.Outcome != ChatCancelOutcome.Ok || check.Appointment is not { } a)
            return new Reply(ChatTemplates.CancelUnavailable, MessageSenderType.System);
        return CancelScreen(a, check.Policy);
    }

    private Reply CancelScreen(ChatAppointmentInfo a, CancelPolicyPreview? policy) =>
        new(ChatTemplates.CancelConfirm(a.ServiceName, a.SpecialistName, a.LocationName, a.StartsAt, a.Timezone, policy?.Text ?? ""),
            MessageSenderType.System, Keyboard: KeyboardBuilder.CancelConfirm(a.Id));

    // ---------------------------------------------------------------- слоти

    private async Task<Reply?> SlotsReplyAsync(Turn t, string? modelText, ChatOfferedSlots? slots, CancellationToken ct)
    {
        if (slots?.Slots is not { Count: > 0 } list) return null;
        var ids = list.Select(s => s.SpecialistId).Distinct().ToList();
        var names = await db.Specialists.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FullName, ct);
        var plan = KeyboardBuilder.SlotPick(slots, names, t.Now);
        if (plan is null || !plan.IsValid()) return null;
        return new Reply((modelText ?? ChatTemplates.SlotsIntro) + "\n\n" + ChatTemplates.SlotsLine,
            modelText is null ? MessageSenderType.System : MessageSenderType.Ai, Keyboard: plan);
    }

    // ---------------------------------------------------------------- callback (§23.5-§23.7)

    private static string? LabelOf(CallbackResolution res) =>
        res.Option?.Label ?? (ButtonVerbs.Stateless.Contains(res.Verb) ? StatelessLabels.BodyLabel(res.Verb) : null);

    private CommitSpec CbSpec(Turn t, CallbackResolution res, params Reply?[] replies) =>
        new(replies.Where(r => r is not null).Select(r => r!).ToList()) { CallbackInboundId = t.Batch[0].Id, CallbackLabel = LabelOf(res) };

    private async Task ResetExpiredHandoffAsync(Conversation conv, DateTimeOffset now, CancellationToken ct)
    {
        if (conv.AiState != "handoff") return;
        var staffReplied = conv.StaffRepliedAt is { } replied && (conv.HandoffAt is null || replied >= conv.HandoffAt);
        if (conv.AiPausedUntil is null || conv.AiPausedUntil > now || staffReplied) return;
        await db.Conversations.Where(c => c.Id == conv.Id && c.AiState == "handoff")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AiState, "active").SetProperty(c => c.HandoffReason, (string?)null)
                .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)null), ct);
    }

    private async Task HandleCallbackAsync(Turn t, CancellationToken ct)
    {
        var conv = t.Conv;
        var cb = t.Batch[0];
        await ResetExpiredHandoffAsync(conv, t.Now, ct);

        // «Стоп» (i:stop) - відписка від власних нагадувань: не підпадає під частотні ліміти (M3), ефект застосовується завжди.
        var parsed = CallbackAddress.TryParse(cb.CallbackData, out var addr);
        var isStop = parsed && addr.Verb is ButtonVerbs.Stop or ButtonVerbs.MarketingOff; // відписки діють завжди (§26.9.2), поза частотним лімітом натискань
        // M3: інформаційні кнопки (довідка, «Мої записи», «Записатися», «Скасувати» зі списку) входять до ліміту шаблонних відповідей.
        var informational = parsed && InformationalVerb(addr.Verb);

        // частотний ліміт натискань: T-rate-limited раз на хвилину, вхідне skipped
        if (!isStop && (!CallbackLimiter.TryAcquire(conv.Id)
                        || (informational && limiter.IsTemplateLimited(conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute))))
        {
            Reply? limited = t.AutoReplies && !await RecentTemplateAsync(conv.Id, ChatTemplates.RateLimited, TimeSpan.FromMinutes(1), t.Now, ct)
                ? new Reply(ChatTemplates.RateLimited, MessageSenderType.System) : null;
            await CommitManyAsync(t, new CommitSpec(limited is null ? [] : [limited]) { CallbackInboundId = cb.Id, InboundStatus = Skipped }, ct);
            return;
        }

        var res = await Keyboards.ResolveCallbackAsync(conv.Id, cb.CallbackData, cb.CallbackMessageRef, cb.Id, ct);
        if (res.ClearMarkup && res.Keyboard is { } found) t.ToClear.Add(found.Id);
        if (!res.IsValid) await HandleRejectedCallbackAsync(t, res, ct);
        else await HandleValidCallbackAsync(t, res, ct);
    }

    /// <summary>Verb, що дає лише шаблонну відповідь без побічного ефекту (M3): ліміт шаблонних відповідей.</summary>
    private static bool InformationalVerb(string verb) =>
        verb is ButtonVerbs.Book or ButtonVerbs.My or ButtonVerbs.Help or ButtonVerbs.CancelAsk or ButtonVerbs.MarketingMenu;

    private static bool SuggestAllows(string verb) =>
        verb is ButtonVerbs.CancelAsk or ButtonVerbs.CancelYes or ButtonVerbs.CancelNo or ButtonVerbs.Attend or ButtonVerbs.ReminderChoice
            or ButtonVerbs.Stop or ButtonVerbs.My or ButtonVerbs.Phone
            // §26.9.1: у suggest mc / i:mkt / i:mkoff відповідають шаблоном; co лише ставить пропозицію розмови (відповідь придушує обробник)
            or ButtonVerbs.MarketingConsent or ButtonVerbs.MarketingMenu or ButtonVerbs.MarketingOff or ButtonVerbs.CampaignOffer;

    private static bool DeterministicKind(string? kind) =>
        kind is KeyboardKinds.AppointmentList or KeyboardKinds.AppointmentActions or KeyboardKinds.CancelConfirm or KeyboardKinds.ReminderChoice;

    /// <summary>T-action-stale: не частіше разу на 10 хв на розмову; у suggest лише для детермінованих клавіатур.</summary>
    private async Task<Reply?> StaleReplyAsync(Turn t, string? kind, CancellationToken ct)
    {
        if (!t.AutoReplies && !DeterministicKind(kind)) return null;
        if (await RecentTemplateAsync(t.Conv.Id, ChatTemplates.ActionStale, StaleThrottle, t.Now, ct)) return null;
        return new Reply(ChatTemplates.ActionStale, MessageSenderType.System);
    }

    private async Task HandleRejectedCallbackAsync(Turn t, CallbackResolution res, CancellationToken ct)
    {
        var conv = t.Conv;
        switch (res.Outcome)
        {
            case CallbackOutcome.HandoffBlocked:
            {
                Reply? notice = t.AutoReplies && !await RecentTemplateAsync(conv.Id, ChatTemplates.ActionHandoff, StaleThrottle, t.Now, ct)
                    ? new Reply(ChatTemplates.ActionHandoff, MessageSenderType.System) : null;
                await CommitManyAsync(t, CbSpec(t, res, notice) with { InboundStatus = Held, Hold = true, RevokeAiKeyboards = true }, ct);
                return;
            }
            case CallbackOutcome.Used when res.Quiet:
                await CommitManyAsync(t, CbSpec(t, res), ct);
                return;
            case CallbackOutcome.Expired when res.Keyboard is { Kind: KeyboardKinds.BookingConfirm } kb:
            {
                if (kb.ProposalId is { } pid) await Booking.ExpireAsync(conv.Id, pid, ct);
                await CommitManyAsync(t, CbSpec(t, res, t.AutoReplies ? new Reply(ChatTemplates.Expired, MessageSenderType.System) : null), ct);
                return;
            }
            default:
                await CommitManyAsync(t, CbSpec(t, res, await StaleReplyAsync(t, res.Keyboard?.Kind, ct)), ct);
                return;
        }
    }

    private bool PromptMatches(Conversation conv, Guid? appointmentId, DateTimeOffset now) =>
        conv.PendingPrompt == ReminderChoiceMenu.PromptKind && appointmentId is not null && conv.PendingPromptAppointmentId == appointmentId
        && conv.PendingPromptExpiresAt is { } e && now < e;

    private async Task HandleValidCallbackAsync(Turn t, CallbackResolution res, CancellationToken ct)
    {
        var conv = t.Conv;
        var verb = res.Verb;
        var kb = res.Keyboard;

        // suggest (§23.6): діють лише детерміновані дії над власними даними; решта - processed без шаблону (i:human лише міняє стан)
        if (!t.AutoReplies && !SuggestAllows(verb))
        {
            var spec = CbSpec(t, res);
            if (verb == ButtonVerbs.Human)
                spec = spec with { HandoffReason = "human_requested", Journal = [HandoffRecord(t, "human_requested", "button")], RevokeAiKeyboards = true };
            await CommitManyAsync(t, spec, ct);
            return;
        }

        if (verb == ButtonVerbs.ReminderChoice)
        {
            // питання зняте (текст, менеджер, TTL) -> кнопка недійсна; дзвінок без телефону/налаштування - без споживання клавіатури
            if (!PromptMatches(conv, kb?.AppointmentId, t.Now))
            {
                t.ToClear.AddRange(await Keyboards.RevokeActiveAsync(conv.Id, [KeyboardKinds.ReminderChoice], ct));
                await CommitManyAsync(t, CbSpec(t, res, await StaleReplyAsync(t, kb?.Kind, ct)), ct);
                return;
            }
            if (res.Option?.Method == ReminderMethods.Call && !await ReminderApplier.CallAvailableAsync(conv.ClientId, ct))
            {
                // §25.5.1: «Зателефонувати» без номера - запит номера (rc гасне), пауза/вимкнено - шаблон
                if (PhoneOn && await CallWithoutPhoneAsync(t, kb?.AppointmentId, explicitTrigger: false, ct) is { } phoneSpec)
                {
                    await CommitManyAsync(t, phoneSpec with { CallbackInboundId = t.Batch[0].Id, CallbackLabel = LabelOf(res) }, ct);
                    return;
                }
                await CommitManyAsync(t, CbSpec(t, res, new Reply(ChatTemplates.ReminderCallUnavailable, MessageSenderType.System)), ct);
                return;
            }
        }

        // «Змінити спосіб» (i:phone, §25.5.1): запит номера в будь-який момент, обходить паузу
        if (verb == ButtonVerbs.Phone)
        {
            var changeSpec = PhoneOn && await CallWithoutPhoneAsync(t, null, explicitTrigger: true, ct) is { } requested
                ? requested with { CallbackInboundId = t.Batch[0].Id, CallbackLabel = LabelOf(res) }
                : CbSpec(t, res, await StaleReplyAsync(t, null, ct));
            await CommitManyAsync(t, changeSpec, ct);
            return;
        }

        if (InformationalVerb(verb))
        {
            // Повтор тієї ж довідки (i:help, i:book) - тихо; решта інформаційних кнопок рахується в ліміті шаблонних відповідей.
            var identical = verb == ButtonVerbs.Help ? ChatTemplates.Help : verb == ButtonVerbs.Book ? ChatTemplates.BookStart : null;
            if (identical is not null && await RecentTemplateAsync(conv.Id, identical, opts.IdenticalTemplateThrottle, t.Now, ct))
            {
                await CommitManyAsync(t, CbSpec(t, res), ct);
                return;
            }
            limiter.RecordTemplate(conv.Id);
        }

        var name = SafeProfileName(signals.SenderName(conv.Id));
        var result = await ButtonActions.ExecuteAsync(
            new ButtonActionContext(conv.Id, t.Batch[0].Id, res, name is null ? null : new TelegramProfile(name, null, null)), ct);
        if (kb is not null && ButtonVerbs.Consuming(verb)) t.ToClear.Add(kb.Id); // споживана клавіатура гасне
        if (kb?.ProposalId is { } proposalId
            && result.Code is ButtonResultCodes.ProposalNotActive or ButtonResultCodes.Expired or ButtonResultCodes.Stale)
            await RevokeProposalKeyboardsAsync(t, proposalId, ct);

        await ApplyActionResultAsync(t, res, result, ct);
    }

    private async Task ApplyActionResultAsync(Turn t, CallbackResolution res, ButtonActionResult r, CancellationToken ct)
    {
        var conv = t.Conv;
        var buttons = ButtonsAvailable(conv);
        Reply Sys(string body, KeyboardPlan? kb = null) => new(body, MessageSenderType.System, Keyboard: kb);
        Task Commit(CommitSpec spec) => CommitManyAsync(t, spec, ct);
        CommitSpec Handoff(Reply? reply) => CbSpec(t, res, reply) with
        {
            HandoffReason = r.HandoffReason, Journal = [HandoffRecord(t, r.HandoffReason!, "button")], RevokeAiKeyboards = true,
        };
        // у suggest handoff лише міняє стан (клієнту нічого, окрім випадків, коли reply явно детермінований)
        Reply? Auto(string body) => t.AutoReplies ? Sys(body) : null;

        switch (r.Code)
        {
            case ButtonResultCodes.Confirmed:
            {
                var c = r.Confirmed!;
                var needChoice = c.ReminderChoiceNeeded
                    || (c.AlreadyConfirmed && !await db.ClientContactPreferences.AsNoTracking().AnyAsync(x => x.ClientId == c.Appointment.ClientId, ct));
                var (body, prompt, keyboard) = await ComposeConfirmedAsync(t, c.Appointment, needChoice, ct);
                await Commit(await ConfirmedSpecAsync(t, CbSpec(t, res), c.Appointment, body, prompt, keyboard, ct));
                return;
            }
            case ButtonResultCodes.MarketingConsentChoice: await ApplyConsentChoiceAsync(t, res, r, ct); return;
            case ButtonResultCodes.MarketingMenu: await Commit(CbSpec(t, res, await MarketingMenuReplyAsync(t, ct))); return;
            case ButtonResultCodes.MarketingOff:
            {
                // §26.9.2: ефект завжди; відповідь - в межах ліміту шаблонів (як «Стоп»)
                var limited = limiter.IsTemplateLimited(conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute);
                if (!limited) limiter.RecordTemplate(conv.Id);
                await Commit(CbSpec(t, res, limited ? null : Sys(ChatTemplates.MarketingOff)) with
                    { TxWork = c => ApplyMarketingOffAsync(t, MarketingConsentVias.Button, r.CampaignRecipientId, c) });
                return;
            }
            case ButtonResultCodes.CampaignOffer: await ApplyCampaignOfferAsync(t, res, r, ct); return;
            case ButtonResultCodes.CouponUnavailable:
                await Commit(CbSpec(t, res, Sys(ChatTemplates.CouponUnavailable)) with { TxWork = c => Offers.ClearAsync(conv.Id, c) });
                return;
            case ButtonResultCodes.AlreadyConfirmed:
                await Commit(CbSpec(t, res, Sys(IsCancelled(r.Confirmed!.Appointment) ? ChatTemplates.AppointmentCancelled : ChatTemplates.AlreadyConfirmed)));
                return;
            case ButtonResultCodes.SlotTaken: await Commit(CbSpec(t, res, Sys(ChatTemplates.SlotTaken))); return;
            case ButtonResultCodes.PriceChanged: await Commit(CbSpec(t, res, Sys(ChatTemplates.PriceChanged))); return;
            case ButtonResultCodes.InProgress: await Commit(CbSpec(t, res, Sys(ChatTemplates.InProgress))); return;
            case ButtonResultCodes.Expired: await Commit(CbSpec(t, res, Sys(ChatTemplates.Expired))); return;
            case ButtonResultCodes.Declined: await Commit(CbSpec(t, res, Sys(ChatTemplates.Declined))); return;
            case ButtonResultCodes.OtherTime: await Commit(CbSpec(t, res, Sys(ChatTemplates.OtherTime))); return;
            case ButtonResultCodes.Limit: await Commit(Handoff(Auto(ChatTemplates.Limit))); return;
            case ButtonResultCodes.Failed: await Commit(Handoff(Auto(ChatTemplates.Handoff))); return;
            case ButtonResultCodes.CancelLimit: await Commit(Handoff(Sys(ChatTemplates.CancelLimit))); return;
            case ButtonResultCodes.CancelFailed: await Commit(Handoff(Sys(ChatTemplates.Handoff))); return;
            case ButtonResultCodes.RescheduleHandoff: await Commit(Handoff(Sys(ChatTemplates.RescheduleHandoff))); return;
            case ButtonResultCodes.Human:
                await Commit(Handoff(Auto(ChatTemplates.Handoff)));
                return;
            case ButtonResultCodes.SummaryReady:
            {
                var p = r.Prepared!;
                var s = p.Summary;
                var dto = new ChatBookingSummaryDto(s.ServiceName, s.SpecialistName, s.LocationName, s.Address, s.LocalDate, s.LocalTime, s.Timezone,
                    s.DurationMinutes, s.PriceOriginal, s.PriceFinal, s.PromotionName, s.CouponOutcome);
                var ttl = Math.Max(1, (int)Math.Round((p.ExpiresAt - t.Now).TotalMinutes));
                await Commit(CbSpec(t, res, new Reply(ChatSummaryText.Format(dto, ttl, promptOpts.CurrencyLabel, buttons: true), MessageSenderType.System,
                    ShowProposalId: p.ProposalId, Keyboard: KeyboardBuilder.BookingConfirm(p.ProposalId), KeySuffix: ":s")));
                return;
            }
            case ButtonResultCodes.PrepareFailed:
                await Commit(CbSpec(t, res, Sys(ChatTemplates.Fallback, buttons ? KeyboardBuilder.HumanOnly() : null)));
                return;
            case ButtonResultCodes.ReminderChoice:
                await ApplyReminderChoiceAsync(t, res, r, ct);
                return;
            case ButtonResultCodes.CancelScreen:
                await Commit(CbSpec(t, res, CancelScreen(r.Appointment!, r.Policy)));
                return;
            case ButtonResultCodes.CancelUnavailable: await Commit(CbSpec(t, res, Sys(ChatTemplates.CancelUnavailable))); return;
            case ButtonResultCodes.Cancelled: await Commit(CbSpec(t, res, Sys(ChatTemplates.Cancelled(r.Appointment!.StartsAt, r.Appointment.Timezone)))); return;
            case ButtonResultCodes.CancelKept: await Commit(CbSpec(t, res, Sys(ChatTemplates.CancelKept(r.Appointment!.StartsAt, r.Appointment.Timezone)))); return;
            case ButtonResultCodes.AttendOk: await Commit(CbSpec(t, res, Sys(ChatTemplates.AttendOk(r.Appointment!.StartsAt, r.Appointment.Timezone)))); return;
            case ButtonResultCodes.AttendNoop: await Commit(CbSpec(t, res)); return;
            case ButtonResultCodes.AppointmentInactive: await Commit(CbSpec(t, res, Sys(ChatTemplates.AppointmentInactive))); return;
            case ButtonResultCodes.MyList or ButtonResultCodes.MyEmpty:
                await Commit(CbSpec(t, res, MyListReply(r.Appointments ?? [], buttons)));
                return;
            case ButtonResultCodes.BookStart: await Commit(CbSpec(t, res, Sys(ChatTemplates.BookStart))); return;
            case ButtonResultCodes.Help: await Commit(CbSpec(t, res, Sys(ChatTemplates.Help, buttons ? KeyboardBuilder.HumanOnly() : null))); return;
            case ButtonResultCodes.Stop:
            {
                // M3: ефект «Стоп» завжди; відповідь - в межах ліміту шаблонних відповідей, понад ліміт - тиша.
                var limited = limiter.IsTemplateLimited(conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute);
                if (!limited) limiter.RecordTemplate(conv.Id);
                await Commit(CbSpec(t, res, limited ? null : Sys(StopText)) with { TxWork = c => ReminderStop.StopAsync(conv.Id, c, "button") });
                return;
            }
            case ButtonResultCodes.Used: // програно CAS у гонці двох натискань: переможець уже відповів
                await Commit(CbSpec(t, res));
                return;
            default: // stale | proposal_not_active
                await Commit(CbSpec(t, res, await StaleReplyAsync(t, res.Keyboard?.Kind, ct)));
                return;
        }
    }

    /// <summary>`rc`: клавіатуру вже спожито; вибір застосовує той самий <see cref="ReminderChoiceApplier"/>, що й текстова відповідь, в одній транзакції з відповіддю.</summary>
    private async Task ApplyReminderChoiceAsync(Turn t, CallbackResolution res, ButtonActionResult r, CancellationToken ct)
    {
        var o = r.Option!;
        var choice = new ReminderChoice(o.Method!, (o.Offsets ?? []).ToList());
        var appointmentId = r.Appointment!.Id;
        try
        {
            var setSpec = CbSpec(t, res, new Reply(ChatTemplates.ReminderSet(choice), MessageSenderType.System)) with
            {
                TxWork = async c =>
                {
                    var applied = await ReminderApplier.ApplyAsync(t.Conv.Id, appointmentId, choice, "chat", c, via: "button");
                    if (!applied.Applied) throw new ReminderNotAppliedException(applied);
                },
            };
            // §26.9.2: після T-reminder-set (вибір зроблено) - пропозиція згоди окремим повідомленням
            await CommitManyAsync(t, await WithMarketingOfferAsync(t, setSpec, t.Conv.ClientId, ct), ct);
        }
        catch (ReminderNotAppliedException ex)
        {
            // Гонка: запис скасовано/завершено між питанням і натисканням. Транзакція відкотилась.
            var keepPrompt = ex.Result.Outcome == ReminderApplyOutcome.CallUnavailable;
            await CommitManyAsync(t, CbSpec(t, res, new Reply(ex.Result.Reply, MessageSenderType.System)) with
            {
                TxWork = keepPrompt ? null : c => ClearPendingPromptAsync(t.Conv.Id, c),
            }, ct);
        }
    }
}
