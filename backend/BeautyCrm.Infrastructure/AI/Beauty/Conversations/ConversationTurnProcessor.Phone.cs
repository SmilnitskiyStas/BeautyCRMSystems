using System.Text.Json;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

// TASK-770 (§25.5-§25.6, ADR-014 частина B): розмовна логіка збору телефону для дзвінка. Усе в коді, без LLM; під ChatPhone:Enabled (за замовч. false).
// Номер: лише в beauty_clients (через ClientPhoneService) і тимчасово в beauty_messages.contact_phone; ніколи в тілі повідомлення (після
// обробки), журналі, логах, історії LLM. У логах і журналі лише коди.
public sealed partial class ConversationTurnProcessor
{
    private static readonly TimeSpan ContactUnexpectedThrottle = TimeSpan.FromMinutes(10);

    private ChatPhoneOptions? _phoneOpts;
    private ChatPhoneOptions PhoneOpts => _phoneOpts ??= services.GetService<ChatPhoneOptions>() ?? new ChatPhoneOptions();
    private bool PhoneOn => PhoneOpts.Enabled;
    private ClientPhoneService PhoneService => services.GetRequiredService<ClientPhoneService>();
    private IContactPhoneStore Contacts => services.GetRequiredService<IContactPhoneStore>();
    private IReminderContextStore ReminderContext => services.GetRequiredService<IReminderContextStore>();

    private static bool PhonePromptActive(Conversation c, DateTimeOffset now) =>
        c.PendingPrompt == ReminderChoiceMenu.PhonePromptKind && c.PendingPromptExpiresAt is { } e && now < e && c.ClientId is not null;

    /// <summary>reply-клавіатура (request_contact) можлива лише в адаптері, що підтримує actions; інакше текстовий варіант питання (§25.6).</summary>
    private bool ReplyKeyboardSupported =>
        services.GetService<ChannelRegistry>()?.Find(ChannelIds.Telegram) is { SupportsActions: true };

    private void LogPhone(Conversation conv, string trigger, string result) =>
        log.LogInformation("chat_phone conversation={ConversationId} trigger={Trigger} result={Result}", conv.Id, trigger, result);

    private AiActionRecord PhoneJournal(Turn t, string tool, object payload) =>
        new(Guid.NewGuid(), t.Conv.TenantId, tool, $"conversation:{t.Conv.Id}", t.Now, AiActionStatus.Done, false, t.Mode,
            JsonSerializer.Serialize(payload), t.Conv.Id);

    // ---------------------------------------------------------------- допоміжне

    /// <summary>Контакт - окремий хід (кожен окремо); поки висить питання про номер - по одному повідомленню (§25.5.2).</summary>
    private List<Message> NarrowForPhone(List<Message> batch, Conversation conv, DateTimeOffset now)
    {
        if (!PhoneOn || batch.Count <= 1) return batch;
        var contact = batch.FindIndex(m => m.Kind == InboundKinds.Contact);
        var narrowed = contact == 0 ? [batch[0]] : contact > 0 ? batch.Take(contact).ToList() : batch;
        return narrowed.Count > 1 && PhonePromptActive(conv, now) ? [narrowed[0]] : narrowed;
    }

    /// <summary>Номер контакту не зберігається (вимкнено, пауза handoff): обнуляємо тимчасове поле.</summary>
    private async Task ClearContactPhonesAsync(IEnumerable<Message> batch, CancellationToken ct)
    {
        foreach (var m in batch.Where(m => m.Kind == InboundKinds.Contact))
            await Contacts.ClearContactPhoneAsync(m.Id, ct);
    }

    /// <summary>Стан пропозиції дзвінка в меню нагадувань (§25.5.1). ChatPhone вимкнено: Available (телефон і call_enabled) або Disabled, як до §25.</summary>
    private async Task<CallOffer> CallOfferAsync(Guid? clientId, CancellationToken ct)
    {
        if (!PhoneOn) return await ReminderApplier.CallAvailableAsync(clientId, ct) ? CallOffer.Available : CallOffer.Disabled;
        if (clientId is not { } id || !(await ReminderContext.GetSettingsAsync(ct)).CallEnabled) return CallOffer.Disabled;
        return await PhoneService.GetRequestStateAsync(_tenantId, id, ct) switch
        {
            PhoneRequestState.HasPhone => CallOffer.Available,
            PhoneRequestState.Allowed => CallOffer.NeedsPhone,
            PhoneRequestState.Cooldown => CallOffer.Cooldown,
            _ => CallOffer.Disabled,
        };
    }

    private bool AnswerAllowed(Turn t)
    {
        if (limiter.IsTemplateLimited(t.Conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute)) return false;
        limiter.RecordTemplate(t.Conv.Id);
        return true;
    }

    /// <summary>Шаблонна відповідь у межах ліміту (M3): понад ліміт - один T-rate-limited на хвилину, далі тиша.</summary>
    private async Task<CommitSpec> GatedTemplateAsync(Turn t, string body, CancellationToken ct) =>
        await TemplateGateAsync(t, true, ct) switch
        {
            TemplateGate.Allow => new CommitSpec([new Reply(body, MessageSenderType.System)]),
            TemplateGate.Notice => new CommitSpec([new Reply(ChatTemplates.RateLimited, MessageSenderType.System)]),
            _ => new CommitSpec([]) { InboundStatus = Skipped },
        };

    private async Task<Guid?> NearestAppointmentAsync(Guid clientId, DateTimeOffset now, CancellationToken ct) =>
        await db.Appointments.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.StartsAt > now && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.StartsAt).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);

    // ---------------------------------------------------------------- тригери (§25.5.1)

    /// <summary>
    /// «Зателефонувати» без номера (<paramref name="explicitTrigger"/>=false) або явне «змінити номер / спосіб» (true, обходить паузу).
    /// null - питання не потрібне (телефон є): викликач іде старим шляхом.
    /// </summary>
    private async Task<CommitSpec?> CallWithoutPhoneAsync(Turn t, Guid? appointmentId, bool explicitTrigger, CancellationToken ct)
    {
        var conv = t.Conv;
        if (conv.ClientId is not { } clientId)
        {
            LogPhone(conv, "-", "no_client");
            return await GatedTemplateAsync(t, ChatTemplates.PhoneNoClient, ct);
        }
        if (!(await ReminderContext.GetSettingsAsync(ct)).CallEnabled)
        {
            LogPhone(conv, "-", "disabled");
            return await GatedTemplateAsync(t, ChatTemplates.CallDisabled, ct);
        }
        switch (await PhoneService.GetRequestStateAsync(_tenantId, clientId, ct))
        {
            case PhoneRequestState.ClientNotFound:
                return await GatedTemplateAsync(t, ChatTemplates.PhoneNoClient, ct);
            case PhoneRequestState.HasPhone:
                if (!explicitTrigger) return null;
                appointmentId = null; // «змінити номер» за наявного: лише номер, дзвінок не переобираємо
                break;
            case PhoneRequestState.Cooldown when !explicitTrigger:
                LogPhone(conv, "call_choice", "cooldown");
                return await GatedTemplateAsync(t, ChatTemplates.ReminderCallUnavailablePhone, ct);
            default:
                if (explicitTrigger) appointmentId = await NearestAppointmentAsync(clientId, t.Now, ct);
                break;
        }
        return await PhoneRequestSpecAsync(t, clientId, appointmentId, explicitTrigger ? "explicit" : "call_choice", ct);
    }

    /// <summary>Показ питання про номер (§25.5.1): одна транзакція - питання, phone_requested_at, rc -> revoked, T-phone-request з reply-клавіатурою, журнал.</summary>
    private async Task<CommitSpec> PhoneRequestSpecAsync(Turn t, Guid clientId, Guid? appointmentId, string trigger, CancellationToken ct)
    {
        var conv = t.Conv;
        var gate = await TemplateGateAsync(t, true, ct);
        if (gate == TemplateGate.Notice) return new CommitSpec([new Reply(ChatTemplates.RateLimited, MessageSenderType.System)]);
        if (gate == TemplateGate.Silent) return new CommitSpec([]) { InboundStatus = Skipped };

        var expires = t.Now.AddHours(PhoneOpts.PromptTtlHours);
        if (appointmentId is { } aid)
        {
            var starts = await db.Appointments.AsNoTracking().Where(a => a.Id == aid && a.ClientId == clientId && a.StartsAt > t.Now)
                .Select(a => (DateTimeOffset?)a.StartsAt).FirstOrDefaultAsync(ct);
            if (starts is { } s) expires = s < expires ? s : expires;
            else appointmentId = null;
        }

        var buttons = ReplyKeyboardSupported;
        var reply = new Reply(ChatTemplates.PhoneRequest(appointmentId is not null, buttons), MessageSenderType.System,
            ActionsJson: buttons ? ChatReplyKeyboard.ContactRequest().ToActionsJson() : null);

        if (PhonePromptActive(conv, t.Now))
        {
            // Активне питання + новий тригер: той самий текст (throttle однакових шаблонів), питання і phone_requested_at не зсуваються.
            if (await RecentTemplateAsync(conv.Id, reply.Body, opts.IdenticalTemplateThrottle, t.Now, ct)) return new CommitSpec([]);
            return new CommitSpec([reply]) { OpenReplyKeyboard = buttons };
        }

        LogPhone(conv, trigger, "prompted");
        var via = t.Batch[0].Kind == InboundKinds.Callback ? "button" : "text";
        return new CommitSpec([reply])
        {
            Prompt = new PromptState(appointmentId, expires, ReminderChoiceMenu.PhonePromptKind),
            OpenReplyKeyboard = buttons,
            RevokeReminderKeyboard = true,
            TxWork = async c => { await PhoneService.MarkPhoneRequestedAsync(_tenantId, clientId, c); },
            Journal = [PhoneJournal(t, ChatJournal.PhonePrompt, new { appointmentId, trigger, via })],
        };
    }

    // ---------------------------------------------------------------- текст (крок 4a')

    /// <returns>true - хід завершено тут; false - іти далі (крок 4a…).</returns>
    private async Task<bool> HandlePhoneChatAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        if (!PhoneOn || t.Batch.Count != 1 || texts.Count != 1) return false;
        var conv = t.Conv;
        var msg = texts[0];
        if (conv.PendingPrompt == ReminderChoiceMenu.PhonePromptKind) return await TryPhoneAnswerAsync(t, msg, false, ct);
        if (!ChatPhoneParser.IsChangeNumber(msg.Body)) return false;
        return await CommitExplicitChangeAsync(t, ct);
    }

    private async Task<bool> CommitExplicitChangeAsync(Turn t, CancellationToken ct)
    {
        var spec = await CallWithoutPhoneAsync(t, null, true, ct);
        if (spec is null) return false;
        await CommitManyAsync(t, spec, ct);
        return true;
    }

    // ---------------------------------------------------------------- відповідь на питання / контакт (§25.5.2)

    /// <summary>
    /// Контакт або текст під питанням про номер. <paramref name="paused"/> - пауза handoff: текст, що не є відповіддю, не чіпається (його тримає викликач).
    /// </summary>
    /// <returns>true - повідомлення оброблено й зафіксовано; false - це не відповідь (текст іде далі / held).</returns>
    private async Task<bool> TryPhoneAnswerAsync(Turn t, Message msg, bool paused, CancellationToken ct)
    {
        var conv = t.Conv;
        var isContact = msg.Kind == InboundKinds.Contact;

        if (!PhonePromptActive(conv, t.Now))
        {
            if (!paused && conv.PendingPrompt == ReminderChoiceMenu.PhonePromptKind)
            {
                // TTL минув: питання знімається (для cooldown це відмова); повідомлення йде звичайним шляхом
                LogPhone(conv, "-", "expired");
                await ClearPendingPromptAsync(conv.Id, ct);
                conv.PendingPrompt = null; conv.PendingPromptAppointmentId = null; conv.PendingPromptExpiresAt = null;
            }
            if (!isContact || paused) return false;
            await CommitManyAsync(t, UnexpectedContactSpec(t, msg), ct); // §25.5.4
            return true;
        }

        var clientId = conv.ClientId!.Value;
        if (isContact)
        {
            var verdict = (await Contacts.ReadAsync(msg.Id, ct))?.Verdict;
            switch (verdict)
            {
                case ContactVerdicts.Own:
                    await CommitSaveAsync(t, msg, clientId, null, PhoneSources.TelegramContact, ct);
                    break;
                case ContactVerdicts.Foreign:
                    LogPhone(conv, "-", "foreign");
                    await CommitManyAsync(t, AnswerSpec(t, ChatTemplates.PhoneForeign, msg), ct);
                    break;
                default:
                    LogPhone(conv, "-", "invalid");
                    await CommitManyAsync(t, AnswerSpec(t, ChatTemplates.PhoneInvalid, msg), ct);
                    break;
            }
            return true;
        }

        var parsed = ChatPhoneParser.Parse(msg.Body);
        switch (parsed.Kind)
        {
            case PhoneParseKind.Phone:
                await CommitSaveAsync(t, msg, clientId, parsed.E164, PhoneSources.ManualChat, ct);
                return true;
            case PhoneParseKind.Invalid:
                LogPhone(conv, "-", "invalid");
                await CommitManyAsync(t, AnswerSpec(t, ChatTemplates.PhoneInvalid, msg, redact: true), ct);
                return true;
            case PhoneParseKind.Decline:
                await CommitDeclineAsync(t, msg, clientId, ct);
                return true;
            case PhoneParseKind.ChangeNumber:
                return await CommitExplicitChangeAsync(t, ct);
            default: // NotAPhone: питання знімається, хід іде далі (reply-клавіатура зникне з першою відповіддю, §25.5.5); під паузою - held
                if (paused) return false;
                await ClearPendingPromptAsync(conv.Id, ct);
                conv.PendingPrompt = null; conv.PendingPromptAppointmentId = null; conv.PendingPromptExpiresAt = null;
                return false;
        }
    }

    /// <summary>Коротка відповідь шаблоном (foreign / invalid): питання й reply-клавіатура лишаються; контакт обнуляється в тій самій транзакції.</summary>
    private CommitSpec AnswerSpec(Turn t, string body, Message msg, bool redact = false) => new(AnswerAllowed(t) ? [new Reply(body, MessageSenderType.System)] : [])
    {
        RedactInboundBody = redact ? ChatTemplates.PhoneBodyRedacted : null,
        TxWork = msg.Kind == InboundKinds.Contact ? c => Contacts.ClearContactPhoneAsync(msg.Id, c) : null,
    };

    /// <summary>Контакт без питання (§25.5.4): номер не зберігається; T-contact-unexpected не частіше разу на 10 хв; повтор уже збереженого номера - тихо.</summary>
    private CommitSpec UnexpectedContactSpec(Turn t, Message msg)
    {
        var conv = t.Conv;
        return new CommitSpec([])
        {
            ComposeReplies = async c =>
            {
                var phone = await Contacts.TakeAsync(msg.Id, c); // читаємо й обнуляємо; далі лише порівняння
                if (phone is not null && conv.ClientId is { } cid)
                {
                    var current = await db.Clients.AsNoTracking().Where(x => x.Id == cid && x.DeletedAt == null).Select(x => x.Phone).FirstOrDefaultAsync(c);
                    if (ClientPhoneService.SameNumber(current, phone))
                    {
                        LogPhone(conv, "-", "unchanged");
                        return new ComposedReplies([]);
                    }
                }
                phone = null;
                LogPhone(conv, "-", "unexpected");
                if (!t.AutoReplies || await RecentTemplateAsync(conv.Id, ChatTemplates.ContactUnexpected, ContactUnexpectedThrottle, t.Now, c) || !AnswerAllowed(t))
                    return new ComposedReplies([]);
                return new ComposedReplies([new Reply(ChatTemplates.ContactUnexpected, MessageSenderType.System)]);
            },
        };
    }

    // ---------------------------------------------------------------- збереження (§25.5.3)

    private async Task CommitSaveAsync(Turn t, Message msg, Guid clientId, string? manualE164, string source, CancellationToken ct)
    {
        var conv = t.Conv;
        var isContact = msg.Kind == InboundKinds.Contact;
        var via = isContact ? "contact" : "text";
        var appointmentId = conv.PendingPromptAppointmentId;
        var allowed = AnswerAllowed(t);

        await CommitManyAsync(t, new CommitSpec([])
        {
            RedactInboundBody = isContact ? null : ChatTemplates.PhoneBodyRedacted,
            ComposeReplies = async c =>
            {
                var phone = isContact ? await Contacts.TakeAsync(msg.Id, c) : manualE164;
                if (phone is null)
                {
                    LogPhone(conv, "-", "invalid"); // номер недоступний (обнулений sweeper-ом / повторна обробка): питання лишається
                    return new ComposedReplies(allowed ? [new Reply(ChatTemplates.PhoneInvalid, MessageSenderType.System)] : []);
                }
                var saved = await PhoneService.SavePhoneAsync(_tenantId, clientId, phone, source, consent: true, conv.Id, c);
                phone = null;

                if (saved.Outcome == PhoneSaveOutcome.InvalidPhone)
                {
                    LogPhone(conv, "-", "invalid");
                    return new ComposedReplies(allowed ? [new Reply(ChatTemplates.PhoneInvalid, MessageSenderType.System)] : []);
                }
                if (!saved.IsSuccess)
                {
                    LogPhone(conv, "-", "no_client");
                    await ClearPendingPromptAsync(conv.Id, c);
                    return new ComposedReplies(allowed ? [new Reply(ChatTemplates.PhoneNoClient, MessageSenderType.System, ActionsJson: ChatActionLayout.RemoveReplyJson)] : []);
                }

                var result = saved.Outcome switch
                {
                    PhoneSaveOutcome.Saved => "saved",
                    PhoneSaveOutcome.Unchanged => "unchanged",
                    _ => "pending_conflict",
                };
                LogPhone(conv, "-", result);
                if (saved.Outcome == PhoneSaveOutcome.PendingConflict)
                    await journal.AppendAsync(PhoneJournal(t, ChatJournal.PhoneConflict, new { via }), c);

                // Дзвінок обирається, якщо питання було про конкретний запис (телефон уже є, тож CallAvailable); номер у конфлікті теж не заважає.
                (DateTimeOffset StartsAt, string? Timezone)? callFor = null;
                if (appointmentId is { } aid)
                {
                    var applied = await ReminderApplier.ApplyAsync(conv.Id, aid, ReminderChoice.Call, "chat", c, via);
                    if (applied.Applied)
                    {
                        var a = await db.Appointments.AsNoTracking().Where(x => x.Id == aid).Select(x => new { x.StartsAt, x.Location!.Timezone }).FirstAsync(c);
                        callFor = (a.StartsAt, a.Timezone);
                    }
                }
                await ClearPendingPromptAsync(conv.Id, c);
                await journal.AppendAsync(PhoneJournal(t, ChatJournal.PhoneSaved,
                    new { result, source, via, appointmentId, duplicateLinked = saved.DuplicateLinked }), c);

                var body = saved.Outcome == PhoneSaveOutcome.PendingConflict ? ChatTemplates.PhonePending
                    : callFor is { } call ? ChatTemplates.PhoneSavedCall(call.StartsAt, call.Timezone) : ChatTemplates.PhoneSaved;
                return new ComposedReplies(allowed ? [new Reply(body, MessageSenderType.System, ActionsJson: ChatActionLayout.RemoveReplyJson)] : []);
            },
        }, ct);
    }

    // ---------------------------------------------------------------- відмова (§25.5.2)

    private async Task CommitDeclineAsync(Turn t, Message msg, Guid clientId, CancellationToken ct)
    {
        var conv = t.Conv;
        var appointmentId = conv.PendingPromptAppointmentId;
        var allowed = AnswerAllowed(t);
        LogPhone(conv, "-", "declined");
        await CommitManyAsync(t, new CommitSpec([])
        {
            ComposeReplies = async c =>
            {
                await PhoneService.MarkPhoneRequestedAsync(_tenantId, clientId, c); // пауза після відмови
                if (!allowed)
                {
                    await ClearPendingPromptAsync(conv.Id, c);
                    return new ComposedReplies([]);
                }
                var replies = new List<Reply> { new(ChatTemplates.PhoneDeclined, MessageSenderType.System, ActionsJson: ChatActionLayout.RemoveReplyJson) };
                PromptState? next = null;
                if (appointmentId is { } aid)
                {
                    var appt = await db.Appointments.AsNoTracking()
                        .Where(a => a.Id == aid && a.ClientId == clientId && a.StartsAt > t.Now
                                    && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
                        .Select(a => new { a.StartsAt }).FirstOrDefaultAsync(c);
                    if (appt is not null)
                    {
                        // T-reminder-offer без дзвінка + rc: вибір нагадування для запису лишається
                        var options = ReminderChoiceMenu.Options(CallOffer.Disabled);
                        next = NewPrompt(aid, appt.StartsAt, t.Now);
                        var buttons = ButtonsAvailable(conv);
                        replies.Add(new Reply(ChatTemplates.ReminderOffer(options, buttons), MessageSenderType.System,
                            Keyboard: buttons ? KeyboardBuilder.ReminderChoice(aid, options, next.ExpiresAt) : null, KeySuffix: ":rc"));
                    }
                }
                if (next is null) await ClearPendingPromptAsync(conv.Id, c);
                return new ComposedReplies(replies, next);
            },
        }, ct);
    }

    // ---------------------------------------------------------------- прибирання reply-клавіатури (§25.5.5)

    /// <summary>
    /// Відкрита reply-клавіатура (питання про номер) зникає з першою відповіддю ходу без inline-кнопок, коли питання вже не висить;
    /// відповідь, що сама несе removeReply, лише скидає позначку. Ту саму транзакцію, що й відповіді.
    /// </summary>
    private async Task UpdateReplyKeyboardStateAsync(Guid conversationId, bool open, List<(Guid Id, Reply Reply)> created, DateTimeOffset now, CancellationToken ct)
    {
        if (open)
        {
            await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ReplyKeyboardOpenAt, (DateTimeOffset?)now), ct);
            return;
        }
        var explicitRemove = created.Any(x => x.Reply.ActionsJson == ChatActionLayout.RemoveReplyJson);
        if (created.Count == 0 && !explicitRemove) return;
        var state = await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId)
            .Select(c => new { c.ReplyKeyboardOpenAt, c.PendingPrompt }).FirstAsync(ct);
        if (state.ReplyKeyboardOpenAt is null) return;
        if (!explicitRemove)
        {
            if (state.PendingPrompt == ReminderChoiceMenu.PhonePromptKind) return;
            var target = created.FirstOrDefault(x => !x.Reply.Draft && x.Reply.Keyboard is null && x.Reply.ActionsJson is null);
            if (target.Id == Guid.Empty) return;
            await db.Messages.Where(m => m.Id == target.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Actions, ChatActionLayout.RemoveReplyJson), ct);
        }
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ReplyKeyboardOpenAt, (DateTimeOffset?)null), ct);
    }
}
