using System.Text.Json;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
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

public enum TurnRunResult { Disabled, NotFound, Busy, Done }

/// <summary>
/// Конвеєр ходу розмови клієнта (контракт §21.5-§21.6, ADR-011 §2.2-§2.5). Один виклик = lease розмови + цикл ходів, поки є вхідні
/// `received`. Порядок кроків детермінований, у коді; LLM викликається лише на кроці 8. «Так» клієнта перевіряє КОД (етап 2), модель
/// не може створити запис. Tenant береться з довіреного джерела (канал/sweeper), встановлюється явно; <see cref="TenantMismatchException"/>
/// і <see cref="TenantScopeMissingException"/> не перехоплюються. У логах немає тексту клієнта, токенів і ключів.
/// </summary>
public sealed partial class ConversationTurnProcessor(
    BeautyDbContext db, TenantContext tenant, ChatAssistantOptions opts, ChatPromptOptions promptOpts, AiTenantRuntime runtime,
    BeautyAssistant assistant, IAiActionJournal journal, OutboundDispatcher dispatcher, ChatRateLimiter limiter,
    ConversationSignals signals, TimeProvider clock, ILogger<ConversationTurnProcessor> log, IServiceProvider services)
{
    private const string Received = "received";
    private const string Processed = "processed";
    private const string Skipped = "skipped";
    private const string Held = "held";
    private const string KeyPrefix = "ai-reply:";
    /// <summary>Позначка у `last_error` вхідного: хід відкладено через тимчасову причину (M2); повтор після <see cref="ChatAssistantOptions.RateLimitRetryDelay"/>.</summary>
    internal const string DeferredMarker = "deferred";

    private Guid _owner;
    private Guid _tenantId;

    // Ліниві залежності: ValidateOnBuild без публічного запису (BeautyDiTests) не ламається, як у ClientChatPortAdapter.
    private ChatBookingService Booking => services.GetRequiredService<ChatBookingService>();
    private IBookingStore Bookings => services.GetRequiredService<IBookingStore>();
    private ChatBookingOptions BookingOptions => services.GetRequiredService<ChatBookingOptions>();
    private ChatSummaryTracker Summaries => services.GetRequiredService<ChatSummaryTracker>();
    private ReminderChoiceApplier ReminderApplier => services.GetRequiredService<ReminderChoiceApplier>();
    private ReminderStopHandler ReminderStop => services.GetRequiredService<ReminderStopHandler>();
    private ChatButtonsOptions ButtonsOpts => services.GetRequiredService<ChatButtonsOptions>();
    private ChatKeyboardService Keyboards => services.GetRequiredService<ChatKeyboardService>();
    private ChatButtonActions ButtonActions => services.GetRequiredService<ChatButtonActions>();
    private ClientChatAppointments ClientAppointments => services.GetRequiredService<ClientChatAppointments>();
    private ChatCallbackRateLimiter CallbackLimiter => services.GetRequiredService<ChatCallbackRateLimiter>();
    private KeyboardMarkupCleaner Cleaner => services.GetRequiredService<KeyboardMarkupCleaner>();
    private ChatKeyboardBuilder KeyboardBuilder => services.GetRequiredService<ChatKeyboardBuilder>();

    [GeneratedRegex(@"скасу[йв]|скасувати|скасуван|перенес|перенести|відмінит\w* (мій )?запис|відміна запис|cancel|resched", RegexOptions.IgnoreCase)]
    private static partial Regex CancelOrMoveRx();

    [GeneratedRegex(@"політик\w*|(?:умов|правил)\w*\s+(?:скасуван|поверненн|перенесен)\w*|штраф\w*|чи\s+повертаєте|чи\s+повертаються|" +
                    @"за\s+скільки\s+(?:годин\s+)?(?:можна|треба|потрібно)\s+(?:скасув|перенес)\w*|що\s+(?:буде|станеться)\s*,?\s*якщо\s+(?:я\s+)?(?:скасу|не\s+прийд|запізн)\w*|" +
                    @"скільки\s+(?:\S+\s+){0,2}(?:поверн|утрим)\w+|який\s+відсоток|чи\s+(?:буде|є)\s+(?:\S+\s+){0,2}(?:штраф|поверненн)\w*",
        RegexOptions.IgnoreCase)]
    private static partial Regex PolicyQuestionRx();

    [GeneratedRegex(@"скасуй\w*|скасувати\s+(?:мій|запис|візит)|(?:хочу|потрібно|треба|прошу|мені\s+треба)\s+(?:скасувати|перенести)|перенесіть|\bперенеси\b|відмініть|відмінити\s+(?:мій\s+)?запис|cancel\s+my",
        RegexOptions.IgnoreCase)]
    private static partial Regex CancelActionRx();

    /// <summary>Інформаційне питання про умови скасування/повернення, а не прохання скасувати чи перенести запис.</summary>
    internal static bool IsPolicyQuestion(string text) => PolicyQuestionRx().IsMatch(text) && !CancelActionRx().IsMatch(text);

    private sealed class LeaseLostException : Exception;

    private sealed class SummaryNotShownException : Exception;

    /// <summary>Вибір нагадування не застосовано всередині транзакції відповіді (запис скасовано, дзвінок недоступний): транзакція відкочується.</summary>
    private sealed class ReminderNotAppliedException(ReminderApplyResult result) : Exception
    {
        public ReminderApplyResult Result { get; } = result;
    }

    /// <summary>
    /// Питання, яке ставиться в транзакції відповіді: про нагадування (§22.7, запис обов'язковий) або про номер телефону
    /// (§25.5.1, <see cref="ReminderChoiceMenu.PhonePromptKind"/>, запис може бути null).
    /// </summary>
    private sealed record PromptState(Guid? AppointmentId, DateTimeOffset ExpiresAt, string Kind = ReminderChoiceMenu.PromptKind);

    /// <summary>Відповіді, обчислені всередині транзакції ходу (після дій над даними клієнта), і питання, яке вони залишають.</summary>
    private sealed record ComposedReplies(IReadOnlyList<Reply> Replies, PromptState? Prompt = null);

    private sealed record Turn(Conversation Conv, List<Message> Batch, AiMode Mode, DateTimeOffset Now)
    {
        /// <summary>Клавіатури, розмітку яких треба прибрати після коміту (відкликані/використані/протерміновані), §23.4.</summary>
        public List<Guid> ToClear { get; } = [];

        /// <summary>suggest не надсилає клієнту нічого автоматично (§21.5 п. 3).</summary>
        public bool AutoReplies => Mode != AiMode.Suggest;
    }

    /// <param name="Keyboard">План клавіатури, яку КОД прикріплює до цього повідомлення (TASK-753); null - без кнопок.</param>
    /// <param name="KeySuffix">"" для основного повідомлення, ":s" для окремого підсумку (§23.6).</param>
    /// <param name="ActionsJson">Готовий JSON `actions` (reply-клавіатура / removeReply, §25.4); взаємовиключний з <paramref name="Keyboard"/>.</param>
    private sealed record Reply(string Body, MessageSenderType Sender, bool Draft = false, Guid? ShowProposalId = null,
        KeyboardPlan? Keyboard = null, string KeySuffix = "", string? ActionsJson = null);

    /// <summary>Усе, що фіксується однією транзакцією ходу (відповіді, стан розмови, вхідні, журнал).</summary>
    private sealed record CommitSpec(IReadOnlyList<Reply> Replies)
    {
        public string? HandoffReason { get; init; }
        public IEnumerable<AiActionRecord>? Journal { get; init; }
        public PromptState? Prompt { get; init; }
        public Func<CancellationToken, Task>? TxWork { get; init; }
        /// <summary>Статус вхідних пакета після коміту: processed | held | skipped.</summary>
        public string InboundStatus { get; init; } = Processed;
        /// <summary>Вхідний callback: ключ відповіді <c>cb-reply:{id}</c> і заміна тіла на «[кнопка] підпис».</summary>
        public Guid? CallbackInboundId { get; init; }
        public string? CallbackLabel { get; init; }
        /// <summary>Вхід у handoff / held: відкликати booking_confirm і slot_pick.</summary>
        public bool RevokeAiKeyboards { get; init; }
        /// <summary>awaiting_staff_since = coalesce(…, now) без зміни стану ШІ (held, §22.8).</summary>
        public bool Hold { get; init; }
        /// <summary>
        /// Відповіді обчислюються всередині транзакції ПЕРЕД вставкою (дії над номером клієнта, §25.5.3): замінюють <see cref="Replies"/>;
        /// питання з результату перекриває <see cref="Prompt"/>.
        /// </summary>
        public Func<CancellationToken, Task<ComposedReplies>>? ComposeReplies { get; init; }
        /// <summary>У клієнта відкрилась reply-клавіатура (питання про номер): reply_keyboard_open_at = now (§25.5.1).</summary>
        public bool OpenReplyKeyboard { get; init; }
        /// <summary>Питання про номер замінило меню нагадувань: клавіатура reminder_choice -> revoked (§25.5.1).</summary>
        public bool RevokeReminderKeyboard { get; init; }
        /// <summary>Тіло вхідних пакета замінюється (номер телефону не лишається в БД, §25.5.2).</summary>
        public string? RedactInboundBody { get; init; }
    }

    // ---------------------------------------------------------------- запуск

    public async Task<TurnRunResult> RunAsync(Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        // tenant з довіреного джерела (канал при ingest/polling або sweeper по відомому каналу); порожній scope -> встановлюємо, інакше звіряємо
        TenantGuard.EnsureOrSetForTrustedJob(tenant, tenantId);
        if (!opts.Enabled) return TurnRunResult.Disabled;
        if (!await db.Conversations.AsNoTracking().AnyAsync(c => c.Id == conversationId, ct)) return TurnRunResult.NotFound; // RLS: чужа = немає

        _owner = Guid.NewGuid();
        _tenantId = tenantId;
        _marketingModule = ButtonsOpts.MarketingEnabled && await services.GetRequiredService<MarketingModuleGate>().IsEnabledAsync(ct);
        if (!await TryLeaseAsync(conversationId, ct)) return TurnRunResult.Busy;
        try
        {
            for (var i = 0; i < opts.MaxTurnsPerRun; i++)
            {
                db.ChangeTracker.Clear();
                var batch = await LoadBatchAsync(conversationId, ct);
                if (batch.Count == 0) break;
                try
                {
                    if (await RunTurnAsync(conversationId, batch, ct)) break; // хід відкладено: повтор із затримкою, не крутимо цикл
                }
                catch (LeaseLostException)
                {
                    log.LogWarning("chat_lease_lost conversation={ConversationId}", conversationId);
                    break;
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
                {
                    await RegisterFailureAsync(batch);
                    throw;
                }
            }
        }
        finally
        {
            await ReleaseLeaseAsync(conversationId);
        }
        return TurnRunResult.Done;
    }

    // ---------------------------------------------------------------- хід

    /// <returns>true - хід відкладено (вхідні лишилися `received`, повтор пізніше); false - хід завершено.</returns>
    private async Task<bool> RunTurnAsync(Guid conversationId, List<Message> batch, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // M2: хід, відкладений через тимчасову причину, не повторюється раніше за паузу (сигнали ingest і sweeper не обходять її).
        if (batch.Any(m => m.LastError == DeferredMarker && now - m.UpdatedAt < opts.RateLimitRetryDelay)) return true;
        var conv = await db.Conversations.AsNoTracking().Include(c => c.Channel).FirstAsync(c => c.Id == conversationId, ct);

        var stale = batch.Where(m => now - m.SentAt > opts.MaxInboundAge).ToList();
        if (stale.Count > 0)
        {
            await SetStatusAsync(stale, Skipped, ct);
            batch = batch.Except(stale).ToList();
            if (batch.Count == 0) return false;
        }

        // Сегменти (§23.6): кожен callback - окремий хід, суміжні текст/команди/медіа - один. Решта лишається `received` для наступного ходу.
        batch = FirstSegment(batch);
        var isCallback = batch[0].Kind == InboundKinds.Callback;

        // «Стоп» (§22.7) діє в будь-який момент, також під час паузи: пакет ділиться на межі команди, решта йде наступним ходом цього запуску.
        var (narrowed, stopOnly) = isCallback ? (batch, false) : SplitAtStop(batch, PromptActive(conv, now));
        batch = narrowed;
        if (!isCallback && !stopOnly) batch = NarrowForPhone(batch, conv, now); // §25.5.2: контакт - окремий хід; під питанням про номер - по одному повідомленню
        if (!isCallback && !stopOnly) batch = await NarrowForCouponAsync(batch, ct); // §26.9.3: повідомлення з кодом купона - окремий хід (код не йде до моделі)
        if (!isCallback && !MarketingOn) await RedactCodeBodiesAsync(batch, ct); // TASK-790 (L10): маркетинг вимкнено - код усе одно не зберігається у тілі

        // Відповідь на цей пакет уже є (повтор після падіння/гонка lease): лише позначити, LLM не викликати.
        var existingKey = isCallback ? CallbackKey(batch[0].Id) : ReplyKey(conv.Id, batch[^1].Id);
        if (await db.Messages.AnyAsync(m => m.IdempotencyKey == existingKey || m.IdempotencyKey == existingKey + ":s", ct))
        {
            await SetStatusAsync(batch, Processed, ct);
            return false;
        }

        // 1. вимкнено на каналі (глобальний вимикач перевірено на вході)
        var channel = conv.Channel!;
        if (!channel.IsActive || channel.Type != ChannelType.Telegram || !ChannelAssistantEnabled(channel.Settings))
        {
            await SetStatusAsync(batch, Skipped, ct);
            return false;
        }

        var mode = await runtime.GetModeAsync(conv.TenantId, ct);
        if (isCallback)
        {
            await HandleCallbackAsync(new Turn(conv, batch, mode, now), ct);
            return false;
        }
        if (stopOnly)
        {
            await HandleStopAsync(new Turn(conv, batch, mode, now), ct);
            return false;
        }

        // 2. пауза після handoff (§22.8): вхідні `held` (видимі менеджеру), без відповіді
        if (conv.AiState == "handoff")
        {
            // Після відповіді менеджера ai_paused_until = NULL (безстроково, до «Повернути ШІ»). Таймер повертає ШІ лише якщо менеджер
            // не відповідав: захист на випадок, коли пауза ще має термін, але відповідь менеджера вже була після handoff.
            var staffReplied = conv.StaffRepliedAt is { } replied && (conv.HandoffAt is null || replied >= conv.HandoffAt);
            if (conv.AiPausedUntil is null || conv.AiPausedUntil > now || staffReplied)
            {
                var paused = new Turn(conv, batch, mode, now);
                // §25.5.6: відповідь на питання про номер - детермінована дія над власними даними, виконується й під час паузи
                if (PhoneOn && batch.Count == 1 && await TryPhoneAnswerAsync(paused, batch[0], true, ct)) return false;
                // §26.9.1-§26.9.2: у handoff дозволені /marketing (меню) і відписка текстом
                if (batch.Count == 1 && await TryMarketingInPauseAsync(paused, batch[0], ct)) return false;
                await ClearContactPhonesAsync(batch, ct); // контакт під паузою не зберігається (§25.5.4)
                await HoldWithNoticeAsync(paused, ct);
                return false;
            }
            // Таймер минув без відповіді менеджера: ШІ повертається, «потрібна відповідь» (awaiting_staff_since) лишається, held не чіпаємо.
            await db.Conversations.Where(c => c.Id == conv.Id && c.AiState == "handoff")
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AiState, "active").SetProperty(c => c.HandoffReason, (string?)null)
                    .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)null), ct);
        }

        var t = new Turn(conv, batch, mode, now);
        var texts = batch.Where(m => m.Kind == InboundKinds.Text).ToList();

        // 3a. контакт Telegram (§25.5.2/§25.5.4): відповідь на питання про номер або «контакт без питання»; ChatPhone вимкнено - як unsupported
        if (PhoneOn && batch.Count == 1 && batch[0].Kind == InboundKinds.Contact)
        {
            await TryPhoneAnswerAsync(t, batch[0], false, ct);
            return false;
        }
        if (!PhoneOn) await ClearContactPhonesAsync(batch, ct);

        // 4. команди й медіа: шаблон без LLM
        if (texts.Count == 0)
        {
            await HandleNonTextAsync(t, ct);
            return false;
        }

        // 4a'. телефон для дзвінка (§25.5.1-§25.5.3): відповідь на питання про номер і «змінити номер» (код, без LLM, також у suggest)
        if (await HandlePhoneChatAsync(t, texts, ct)) return false;

        // 4a. нагадування: меню «нагадування» і відповідь на питання про нагадування (код, без LLM, поза лімітами LLM)
        if (await HandleReminderChatAsync(t, texts, ct)) return false;

        // 4b. код купона текстом (§26.9.3): до моделі; код маскується в збереженому тілі. 4c. меню «акції» і відписка текстом (§26.9.2)
        if (await HandleCouponCodeAsync(t, texts, ct)) return false;
        if (await HandleMarketingTextAsync(t, texts, ct)) return false;

        // 5. підтвердження запису (до LLM, у коді)
        if (await HandleActiveProposalAsync(t, texts, ct)) return false;

        // 6. ліміти розмови: ходи клієнта за хвилину («надсилаєте дуже часто»), бюджет запитів LLM (хвилина/доба) - окремо (TASK-735)
        if (limiter.IsTurnLimited(conv.Id, opts.MaxTurnsPerConversationPerMinute)
            || limiter.IsLimited(conv.Id, opts.MaxCallsPerConversationPerMinute))
        {
            await ReplyRateLimitedAsync(t, TimeSpan.FromMinutes(1), ChatTemplates.RateLimited, ct);
            return false;
        }
        if (await DailyLimitReachedAsync(conv.Id, now, ct))
        {
            await ReplyRateLimitedAsync(t, TimeSpan.FromHours(1), ChatTemplates.RateLimited, ct);
            return false;
        }

        // 7. правила передачі менеджеру (скарга, оплата, людина, скасування/перенесення)
        foreach (var m in texts)
        {
            var prepared = PromptInjectionGuard.Prepare(m.Body);
            var decision = HandoffRules.Evaluate(prepared);
            if (decision.Handoff) { await HandoffAsync(t, decision.Reason!, null, ct); return false; }
            // Питання про ПОЛІТИКУ скасування («Яка політика скасування?») - не прохання скасувати: відповідає ШІ за блоком ФАКТИ ЗАКЛАДУ (TASK-741).
            if (CancelOrMoveRx().IsMatch(prepared) && !IsPolicyQuestion(prepared)) { await HandoffAsync(t, "cancel_request", null, ct); return false; }
        }

        // 7a. бюджет чату (M2): нові розмови каналу за годину, власний ключ без лімітів, частка місячного ліміту tenant
        if (await NewConversationOverLimitAsync(conv, now, ct))
        {
            await ReplyRateLimitedAsync(t, TimeSpan.FromHours(1), ChatTemplates.Busy, ct);
            return false;
        }
        var gate = await runtime.GetChatGateAsync(conv.TenantId, ct);
        if (gate.State == ChatAssistantGate.LimitsRequired)
        {
            await HandoffAsync(t, "limits_required", ChatTemplates.AiUnavailable, ct);
            return false;
        }
        if (gate.State == ChatAssistantGate.Ready && gate.RequestLimit is { } requestLimit && await ChatBudgetExhaustedAsync(requestLimit, now, ct))
        {
            await HandoffAsync(t, "chat_budget", ChatTemplates.AiUnavailable, ct);
            return false;
        }

        // 8-10. модель
        return await RunModelAsync(t, texts, ct);
    }

    // ---------------------------------------------------------------- handoff: held, «Стоп»

    private static bool PromptActive(Conversation c, DateTimeOffset now) =>
        c.PendingPrompt == ReminderChoiceMenu.PromptKind && c.PendingPromptExpiresAt is { } e && now < e;

    /// <summary>
    /// Ділить пакет на межі команди «Стоп»: початок пакета без «Стоп» - звичайний хід; пакет, що починається зі «Стоп», - лише послідовність
    /// команд (stopOnly). Решта лишається `received` для наступного ходу, порядок вхідних зберігається.
    /// </summary>
    private static (List<Message> Batch, bool StopOnly) SplitAtStop(List<Message> batch, bool promptActive)
    {
        bool IsStop(Message m) => (m.Kind == InboundKinds.Text && ReminderCommands.IsStop(m.Body, promptActive))
            || (m.Kind == InboundKinds.Command && ChatCommands.Parse(m.Body) == ChatCommand.Stop);
        var first = batch.FindIndex(m => IsStop(m));
        if (first < 0) return (batch, false);
        return first > 0 ? (batch.Take(first).ToList(), false) : (batch.TakeWhile(IsStop).ToList(), true);
    }

    /// <summary>Вхідні під час паузи (§22.8): `held` + `awaiting_staff_since = coalesce(…, now)` в одній транзакції. Клієнту нічого не надсилається.</summary>
    private async Task HoldAsync(Guid conversationId, List<Message> batch, DateTimeOffset now, CancellationToken ct)
    {
        var ids = batch.Select(m => m.Id).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RedactCodeBodiesAsync(batch, ct); // TASK-790 (L10): код купона не лишається у тілі, яке бачить менеджер
        await db.Messages.Where(m => ids.Contains(m.Id) && m.Status == Received)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, Held).SetProperty(m => m.UpdatedAt, now), ct);
        await db.Conversations.Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AwaitingStaffSince, c => c.AwaitingStaffSince ?? now), ct);
        await tx.CommitAsync(ct);
    }

    private async Task HandleStopAsync(Turn t, CancellationToken ct)
    {
        // suggest: ефект є, відповіді клієнту немає (§22.7); у handoff відповідь T-stop надсилається.
        // /stop (команда) - детермінована дія над власними даними: шаблон відповідає й у suggest (§23.6).
        var viaCommand = t.Batch.Any(m => m.Kind == InboundKinds.Command);
        // M3: ефект «Стоп» (відписка) діє завжди; відповідь - лише в межах ліміту шаблонних відповідей (понад ліміт - тиша, без T-rate-limited).
        Reply? reply = null;
        if ((t.AutoReplies || viaCommand) && !limiter.IsTemplateLimited(t.Conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute))
        {
            limiter.RecordTemplate(t.Conv.Id);
            reply = new Reply(StopText, MessageSenderType.System);
        }
        await CommitAsync(t, reply, null, null, ct, txWork: c => ReminderStop.StopAsync(t.Conv.Id, c, viaCommand ? "command" : "text"));
    }

    // ---------------------------------------------------------------- крок 4a

    /// <returns>true - хід завершено тут; false - іти далі (крок 5…).</returns>
    private async Task<bool> HandleReminderChatAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        if (!t.AutoReplies || t.Batch.Count != 1 || texts.Count != 1) return false;
        var conv = t.Conv;
        var single = texts[0];

        if (ReminderCommands.IsShowMenu(single.Body)) return await ShowReminderMenuAsync(t, ct);
        if (conv.PendingPrompt != ReminderChoiceMenu.PromptKind) return false;

        if (!PromptActive(conv, t.Now) || conv.PendingPromptAppointmentId is not { } appointmentId)
        {
            await ClearPendingPromptAsync(conv.Id, ct); // TTL минув: не розбираємо, питання знімаємо
            return false;
        }

        var parsed = ReminderChoiceParser.Parse(single.Body);
        switch (parsed.Kind)
        {
            case ReminderParseKind.NotAChoice:
                await ClearPendingPromptAsync(conv.Id, ct);
                return false;
            case ReminderParseKind.Invalid:
                if (await PassTemplateGateAsync(t, ct))
                    await CommitAsync(t, new Reply(ChatTemplates.ReminderInvalid, MessageSenderType.System), null, null, ct);
                return true;
        }

        var choice = parsed.Choice!;
        if (choice.Method == ReminderMethods.Call && !await ReminderApplier.CallAvailableAsync(conv.ClientId, ct))
        {
            // §25.5.1: «зателефонувати» без номера - запит номера (або пауза/вимкнено), а не відмова
            if (PhoneOn && await CallWithoutPhoneAsync(t, appointmentId, explicitTrigger: false, ct) is { } phoneSpec)
            {
                await CommitManyAsync(t, phoneSpec, ct);
                return true;
            }
            if (await PassTemplateGateAsync(t, ct))
                await CommitAsync(t, new Reply(ChatTemplates.ReminderCallUnavailable, MessageSenderType.System), null, null, ct);
            return true;
        }
        try
        {
            // §26.9.2: після T-reminder-set (вибір зроблено) - пропозиція згоди окремим повідомленням
            var setSpec = await WithMarketingOfferAsync(t, new CommitSpec([new Reply(ChatTemplates.ReminderSet(choice), MessageSenderType.System)])
            {
                TxWork = async c =>
                {
                    var result = await ReminderApplier.ApplyAsync(conv.Id, appointmentId, choice, "chat", c);
                    if (!result.Applied) throw new ReminderNotAppliedException(result);
                },
            }, conv.ClientId, ct);
            await CommitManyAsync(t, setSpec, ct);
        }
        catch (ReminderNotAppliedException ex)
        {
            // Гонка: запис скасовано/завершено між питанням і відповіддю (або дзвінок став недоступним). Транзакція відкотилась.
            var keepPrompt = ex.Result.Outcome == ReminderApplyOutcome.CallUnavailable;
            await CommitAsync(t, new Reply(ex.Result.Reply, MessageSenderType.System), null, null, ct,
                txWork: keepPrompt ? null : c => ClearPendingPromptAsync(conv.Id, c));
        }
        return true;
    }

    /// <summary>Команда «нагадування»: меню для найближчого майбутнього активного запису клієнта (новий pending_prompt).</summary>
    private async Task<bool> ShowReminderMenuAsync(Turn t, CancellationToken ct)
    {
        if (!await PassTemplateGateAsync(t, ct)) return true; // M3: ліміт шаблонних відповідей
        var conv = t.Conv;
        var next = conv.ClientId is not { } clientId ? null : await db.Appointments.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.StartsAt > t.Now && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.StartsAt)
            .Select(a => new { a.Id, a.StartsAt, ServiceName = a.Service!.Name, a.Location!.Timezone }).FirstOrDefaultAsync(ct);
        if (next is null)
        {
            await CommitAsync(t, new Reply(ChatTemplates.ReminderNoAppointments, MessageSenderType.System), null, null, ct);
            return true;
        }
        var offer = await CallOfferAsync(conv.ClientId, ct);
        var options = ReminderChoiceMenu.Options(offer);
        var prompt = NewPrompt(next.Id, next.StartsAt, t.Now);
        var buttons = ButtonsAvailable(conv);
        await CommitAsync(t, new Reply(ChatTemplates.ReminderOfferFor(next.ServiceName, next.StartsAt, next.Timezone, options, buttons, offer), MessageSenderType.System,
                Keyboard: buttons ? KeyboardBuilder.ReminderChoice(next.Id, options, prompt.ExpiresAt, offer) : null),
            null, null, ct, prompt: prompt);
        return true;
    }

    private PromptState NewPrompt(Guid appointmentId, DateTimeOffset startsAt, DateTimeOffset now)
    {
        var ttl = now.AddHours(opts.ReminderPromptTtlHours);
        return new PromptState(appointmentId, startsAt < ttl ? startsAt : ttl);
    }

    private Task ClearPendingPromptAsync(Guid conversationId, CancellationToken ct) =>
        db.Conversations.Where(c => c.Id == conversationId && c.PendingPrompt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingPrompt, (string?)null)
                .SetProperty(c => c.PendingPromptAppointmentId, (Guid?)null).SetProperty(c => c.PendingPromptExpiresAt, (DateTimeOffset?)null), ct);

    /// <summary>
    /// T-confirmed + (вподобання немає) T-reminder-offer в ОДНОМУ повідомленні з питанням (pending_prompt у транзакції відповіді), інакше T-confirmed + рядок про нагадування.
    /// </summary>
    private async Task<(string Body, PromptState? Prompt, KeyboardPlan? Keyboard)> ComposeConfirmedAsync(
        Turn t, AppointmentDto appt, bool reminderChoiceNeeded, CancellationToken ct)
    {
        var buttons = ButtonsAvailable(t.Conv);
        var body = ChatTemplates.Confirmed(appt, promptOpts.CurrencyLabel, buttons);
        if (reminderChoiceNeeded)
        {
            var offer = await CallOfferAsync(appt.ClientId, ct);
            var options = ReminderChoiceMenu.Options(offer);
            var prompt = NewPrompt(appt.Id, appt.StartsAt, t.Now);
            return (body + "\n\n" + ChatTemplates.ReminderOffer(options, buttons, offer), prompt,
                buttons ? KeyboardBuilder.ReminderChoice(appt.Id, options, prompt.ExpiresAt, offer) : null);
        }
        var line = appt.Reminders is null ? body : body + "\n" + ChatTemplates.ReminderLine(appt.Reminders);
        return (line, null, buttons && ButtonsOpts.CancelEnabled ? KeyboardBuilder.AppointmentActions(appt.Id) : null);
    }

    // ---------------------------------------------------------------- крок 4

    private async Task HandleNonTextAsync(Turn t, CancellationToken ct)
    {
        // M3: команди й медіа нової розмови понад ліміт нових розмов каналу за годину - як і шлях LLM (один T-busy на годину, далі тиша).
        if (await NewConversationOverLimitAsync(t.Conv, t.Now, ct))
        {
            await ReplyRateLimitedAsync(t, TimeSpan.FromHours(1), ChatTemplates.Busy, ct);
            return;
        }

        // Slash-команди (§23.7 п. 9): код без LLM.
        var command = t.Batch.Where(m => m.Kind == InboundKinds.Command).Select(m => ChatCommands.Parse(m.Body))
            .FirstOrDefault(c => c is not null and not ChatCommand.Unknown && (c != ChatCommand.Marketing || MarketingOn)); // /marketing за Marketing:Enabled=false - T-text-only (§26.2)
        if (command is { } known)
        {
            await HandleCommandAsync(t, known, ct);
            return;
        }
        if (!t.AutoReplies) { await SetStatusAsync(t.Batch, Processed, ct); return; }
        if (await RecentTemplateAsync(t.Conv.Id, ChatTemplates.TextOnly, opts.TextOnlyThrottle, t.Now, ct))
        {
            await SetStatusAsync(t.Batch, Processed, ct);
            return;
        }
        if (!await PassTemplateGateAsync(t, ct)) return;
        await CommitAsync(t, new Reply(ChatTemplates.TextOnly, MessageSenderType.System), null, null, ct);
    }

    // ---------------------------------------------------------------- M3: ліміт шаблонних відповідей (TASK-759)

    private enum TemplateGate { Allow, Notice, Silent }

    /// <summary>
    /// Шаблонні відповіді без LLM (команди, меню нагадувань, текст-fallback, інформаційні кнопки) обмежені на розмову за хвилину
    /// (<see cref="ChatAssistantOptions.MaxTemplateRepliesPerConversationPerMinute"/>). Понад ліміт: один T-rate-limited на хвилину, далі тиша.
    /// </summary>
    private async Task<TemplateGate> TemplateGateAsync(Turn t, bool record, CancellationToken ct)
    {
        var id = t.Conv.Id;
        if (!limiter.IsTemplateLimited(id, opts.MaxTemplateRepliesPerConversationPerMinute))
        {
            if (record) limiter.RecordTemplate(id);
            return TemplateGate.Allow;
        }
        return !t.AutoReplies || await RecentTemplateAsync(id, ChatTemplates.RateLimited, TimeSpan.FromMinutes(1), t.Now, ct)
            ? TemplateGate.Silent : TemplateGate.Notice;
    }

    /// <returns>true - відповідь дозволено; false - хід уже завершено тут (T-rate-limited або тиша, вхідні processed/skipped).</returns>
    private async Task<bool> PassTemplateGateAsync(Turn t, CancellationToken ct)
    {
        switch (await TemplateGateAsync(t, true, ct))
        {
            case TemplateGate.Allow:
                return true;
            case TemplateGate.Notice:
                await CommitAsync(t, new Reply(ChatTemplates.RateLimited, MessageSenderType.System), null, null, ct);
                return false;
            default:
                await SetStatusAsync(t.Batch, Skipped, ct);
                return false;
        }
    }

    /// <summary>Та сама довідкова відповідь (/start, /help, /book) уже була нещодавно: повтор тихо `processed`.</summary>
    private async Task<bool> IdenticalRecentAsync(Turn t, string template, CancellationToken ct)
    {
        if (!await RecentTemplateAsync(t.Conv.Id, template, opts.IdenticalTemplateThrottle, t.Now, ct)) return false;
        await SetStatusAsync(t.Batch, Processed, ct);
        return true;
    }

    private static bool IsStart(string body)
    {
        var s = body.TrimStart();
        return s.StartsWith("/start", StringComparison.OrdinalIgnoreCase) && (s.Length == 6 || s[6] is ' ' or '@');
    }

    // ---------------------------------------------------------------- крок 5

    /// <returns>true - хід завершено тут (відповідь зафіксовано); false - іти далі до LLM.</returns>
    private async Task<bool> HandleActiveProposalAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        var conv = t.Conv;
        var single = t.Batch.Count == 1 && texts.Count == 1 ? texts[0] : null;
        var active = await Booking.FindActiveAsync(conv.Id, ct);
        // M1: «Так» зараховується лише до ДОСТАВЛЕНОГО підсумку. Підсумок міг дійти не нашим шляхом (sweeper, Node-воркер): ліниво
        // переводимо prepared -> shown, якщо повідомлення вже `sent`. Недоставлений підсумок лишає пропозицію `prepared`.
        if (active is { Status: ChatProposalStatus.Prepared, SummaryMessageId: { } summaryId } && await Summaries.PromoteAsync(summaryId, ct))
            active = await Booking.FindActiveAsync(conv.Id, ct);

        if (active is null)
        {
            // Повторне «Так» після підтвердженого запису (або повтор після падіння між записом і відповіддю): запису вдруге немає.
            return t.AutoReplies && single is not null && ChatConfirmationText.IsYes(single.Body) && await RepeatYesAsync(t, ct);
        }

        if (t.AutoReplies && single is not null && active.Status == ChatProposalStatus.Shown
            && await IsFirstAfterSummaryAsync(conv.Id, active, single, ct))
        {
            if (ChatConfirmationText.IsYes(single.Body))
            {
                if (t.Now >= active.ExpiresAt)
                {
                    await Booking.ExpireAsync(conv.Id, active.Id, ct);
                    await RevokeProposalKeyboardsAsync(t, active.Id, ct);
                    await CommitAsync(t, new Reply(ChatTemplates.Expired, MessageSenderType.System), null, null, ct);
                    return true;
                }
                if (await ConfirmAsync(t, active.Id, ct)) return true;
            }
            else if (ChatConfirmationText.IsNo(single.Body))
            {
                await Booking.DeclineAsync(conv.Id, active.Id, ct);
                await RevokeProposalKeyboardsAsync(t, active.Id, ct);
                await CommitAsync(t, new Reply(ChatTemplates.Declined, MessageSenderType.System), null, null, ct);
                return true;
            }
        }

        // Будь-що інше (інший текст, пакет > 1, «Так» не першим, suggest): активна пропозиція втрачає чинність, нове «Так» - лише на новий підсумок.
        await Booking.SupersedeAsync(conv.Id, ct);
        await RevokeProposalKeyboardsAsync(t, active.Id, ct);
        return false;
    }

    /// <summary>
    /// Повідомлення клієнта - перше вхідне ПІСЛЯ ДОСТАВЛЕНОГО підсумку. Опорна точка - момент доставки (`sent_at` підсумку, який ставить
    /// відправник після відповіді Telegram), а не вставки; порядок за серверним часом вставки вхідного, не за датою Telegram.
    /// Telegram message_id відповіді не зберігається (немає колонки), тож порівняння з message_id підсумку не робиться.
    /// </summary>
    private async Task<bool> IsFirstAfterSummaryAsync(Guid conversationId, ChatProposal p, Message msg, CancellationToken ct)
    {
        if (p.SummaryMessageId is not { } sid) return false;
        var summaryAt = await db.Messages.AsNoTracking().Where(m => m.Id == sid && m.ConversationId == conversationId && m.Status == "sent")
            .Select(m => (DateTimeOffset?)m.SentAt).FirstOrDefaultAsync(ct);
        if (summaryAt is not { } at || msg.CreatedAt <= at) return false;
        var earlier = await db.Messages.AsNoTracking().AnyAsync(m => m.ConversationId == conversationId && m.Direction == MessageDirection.Inbound
            && m.Id != msg.Id && m.CreatedAt > at && m.CreatedAt < msg.CreatedAt, ct);
        return !earlier;
    }

    /// <returns>true - відповідь зафіксовано; false - пропозиція вже недійсна, іти до LLM.</returns>
    private async Task<bool> ConfirmAsync(Turn t, Guid proposalId, CancellationToken ct)
    {
        var name = SafeProfileName(signals.SenderName(t.Conv.Id));
        var profile = name is null ? null : new TelegramProfile(name, null, null);
        var r = await Booking.ConfirmAsync(new ChatConfirmRequest(t.Conv.Id, proposalId, ClientConfirmed: true, profile), ct);
        await RevokeProposalKeyboardsAsync(t, proposalId, ct); // пропозиція покинула shown (текстом): її кнопки гаснуть
        if (r.IsOk)
        {
            var confirmed = r.Value!;
            if (confirmed.AlreadyConfirmed)
                await CommitAsync(t, new Reply(IsCancelled(confirmed.Appointment) ? ChatTemplates.AppointmentCancelled : ChatTemplates.AlreadyConfirmed,
                    MessageSenderType.System), null, null, ct);
            else
            {
                var (body, prompt, keyboard) = await ComposeConfirmedAsync(t, confirmed.Appointment, confirmed.ReminderChoiceNeeded, ct);
                await CommitManyAsync(t, await ConfirmedSpecAsync(t, new CommitSpec([]) { Journal = [ConfirmJournal(t, proposalId, confirmed.Appointment.Id)] },
                    confirmed.Appointment, body, prompt, keyboard, ct), ct);
            }
            return true;
        }

        switch (r.Error!.Code)
        {
            case "coupon_unavailable" or "coupon_invalid":
                // §26.9.3: купон на підтвердженні недійсний -> пропозиція розмови знімається, наступний підсумок - за звичайною ціною
                await CommitAsync(t, new Reply(ChatTemplates.CouponUnavailable, MessageSenderType.System), null, null, ct, txWork: c => Offers.ClearAsync(t.Conv.Id, c));
                return true;
            case "slot_unavailable" or "slot_in_past" or "invalid_start":
                await CommitAsync(t, new Reply(ChatTemplates.SlotTaken, MessageSenderType.System), null, null, ct);
                return true;
            case "price_changed":
                await CommitAsync(t, new Reply(ChatTemplates.PriceChanged, MessageSenderType.System), null, null, ct);
                return true;
            case "proposal_expired":
                await CommitAsync(t, new Reply(ChatTemplates.Expired, MessageSenderType.System), null, null, ct);
                return true;
            case "confirmation_in_progress":
                await CommitAsync(t, new Reply(ChatTemplates.InProgress, MessageSenderType.System), null, null, ct);
                return true;
            case "booking_limit_reached":
                await HandoffAsync(t, "booking_limit", ChatTemplates.Limit, ct);
                return true;
            case "proposal_not_found" or "proposal_not_shown" or "proposal_not_active":
                return false;
            default:
                await HandoffAsync(t, "booking_failed", null, ct);
                return true;
        }
    }

    /// <summary>Повторне «Так» на вже підтверджену пропозицію: T-already-confirmed; якщо підтвердження клієнту так і не пішло (падіння) - T-confirmed.</summary>
    private async Task<bool> RepeatYesAsync(Turn t, CancellationToken ct)
    {
        var p = await db.ChatBookingProposals.AsNoTracking()
            .Where(x => x.ConversationId == t.Conv.Id && x.Status == ChatProposalStatus.Confirmed && x.SummaryMessageId != null)
            .OrderByDescending(x => x.DecidedAt).FirstOrDefaultAsync(ct);
        if (p?.DecidedAt is not { } decidedAt || t.Now - decidedAt > TimeSpan.FromMinutes(BookingOptions.ProposalTtlMinutes)) return false;

        // Падіння між CAS «confirmed» і створенням запису: повторне «Так» доводить створення до кінця (ідемпотентно, без дубля).
        if (p.AppointmentId is null) return await ConfirmAsync(t, p.Id, ct);

        var summaryId = p.SummaryMessageId!.Value;
        var summaryAt = await db.Messages.AsNoTracking().Where(m => m.Id == summaryId).Select(m => m.CreatedAt).FirstAsync(ct);
        // Після підтвердження розмова пішла далі (ШІ відповідав на інше): «Так» вже не про цей запис.
        if (await db.Messages.AsNoTracking().AnyAsync(m => m.ConversationId == t.Conv.Id && m.Direction == MessageDirection.Outbound
                && m.SenderType == MessageSenderType.Ai && m.Id != summaryId && m.CreatedAt > summaryAt, ct)) return false;

        // L7: «Так» перевіряє ПОТОЧНИЙ стан запису (його могли скасувати), а не лише факт підтвердження.
        if (await Bookings.GetAppointmentAsync(p.AppointmentId!.Value, ct) is not { } appt) return false;
        if (IsCancelled(appt))
        {
            await CommitAsync(t, new Reply(ChatTemplates.AppointmentCancelled, MessageSenderType.System), null, null, ct);
            return true;
        }

        var confirmationSent = await db.Messages.AsNoTracking().AnyAsync(m => m.ConversationId == t.Conv.Id && m.Direction == MessageDirection.Outbound
            && m.SenderType == MessageSenderType.System && m.Body.StartsWith(ChatTemplates.ConfirmedHeader) && m.CreatedAt > summaryAt, ct);
        if (confirmationSent)
        {
            await CommitAsync(t, new Reply(ChatTemplates.AlreadyConfirmed, MessageSenderType.System), null, null, ct);
            return true;
        }
        // Підтвердження клієнту не пішло (падіння): надсилаємо T-confirmed; запис журналу confirm_booking не дублюємо (його могли відкотити).
        var journaled = await ConfirmJournaledAsync(t.Conv.Id, p.Id, ct);
        var needChoice = !await db.ClientContactPreferences.AsNoTracking().AnyAsync(x => x.ClientId == appt.ClientId, ct);
        var (body, prompt, keyboard) = await ComposeConfirmedAsync(t, appt, needChoice, ct);
        await CommitManyAsync(t, await ConfirmedSpecAsync(t, new CommitSpec([]) { Journal = journaled ? null : [ConfirmJournal(t, p.Id, appt.Id)] },
            appt, body, prompt, keyboard, ct), ct);
        return true;
    }

    private static bool IsCancelled(AppointmentDto a) => string.Equals(a.Status, "cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Чи є вже запис журналу confirm_booking для цієї пропозиції (за proposalId у payload).</summary>
    private async Task<bool> ConfirmJournaledAsync(Guid conversationId, Guid proposalId, CancellationToken ct)
    {
        var payloads = await db.AiActions.AsNoTracking().Where(a => a.ConversationId == conversationId && a.ToolName == "confirm_booking")
            .Select(a => a.Payload).ToListAsync(ct);
        var id = proposalId.ToString();
        return payloads.Any(p => p.Contains(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ім'я з профілю Telegram - недовірений ввід (L6): нормалізація, знешкодження тегів і міток, один рядок, без дужок і службових
    /// символів, до 60 кодових точок; схоже на інструкцію або порожнє -> null (буде «Клієнт Telegram»).
    /// </summary>
    public static string? SafeProfileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = AiTextNormalizer.NeutralizeLabels(AiTextNormalizer.NeutralizeTags(AiTextNormalizer.Clean(raw)));
        s = string.Concat(s.Where(c => c is not ('<' or '>' or '[' or ']' or '{' or '}' or '`' or '|' or '\\')));
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        s = AiTextNormalizer.TruncateCodePoints(s, 60).Trim();
        if (s.Length < 2 || PromptInjectionGuard.IsSuspicious(s)) return null;
        return s;
    }

    private AiActionRecord ConfirmJournal(Turn t, Guid proposalId, Guid appointmentId) =>
        new(Guid.NewGuid(), t.Conv.TenantId, "confirm_booking", $"appointment:{appointmentId}", t.Now, AiActionStatus.Done, true, t.Mode,
            JsonSerializer.Serialize(new { proposalId, via = "text" }), t.Conv.Id);

    // ---------------------------------------------------------------- крок 6

    private async Task ReplyRateLimitedAsync(Turn t, TimeSpan window, string template, CancellationToken ct)
    {
        if (!t.AutoReplies || await RecentTemplateAsync(t.Conv.Id, template, window, t.Now, ct))
        {
            await SetStatusAsync(t.Batch, Skipped, ct); // раз на вікно; далі тиша
            return;
        }
        await CommitAsync(t, new Reply(template, MessageSenderType.System), null, null, ct);
    }

    /// <summary>
    /// Добовий ліміт - у БД (переживає рестарт): запити LLM розмови за 24 год з журналу обліку (<see cref="ChatJournal.LlmUsage"/>), а не
    /// кількість відповідей (M2): хід з tools коштує кілька запитів, а відкладені/упалі ходи теж.
    /// </summary>
    private async Task<bool> DailyLimitReachedAsync(Guid conversationId, DateTimeOffset now, CancellationToken ct) =>
        await SumLlmCallsAsync(conversationId, now.AddHours(-24), ct) >= opts.MaxCallsPerConversationPerDay;

    /// <summary>Сума запитів LLM чату з журналу обліку: за розмовою (conversationId) або за весь tenant (null) від моменту <paramref name="since"/>.</summary>
    private async Task<long> SumLlmCallsAsync(Guid? conversationId, DateTimeOffset since, CancellationToken ct)
    {
        var rows = conversationId is { } cid
            ? db.Database.SqlQuery<long>($@"SELECT COALESCE(SUM((payload->>'calls')::bigint), 0) AS ""Value"" FROM beauty_ai_actions
                WHERE tool_name = {ChatJournal.LlmUsage} AND conversation_id = {cid} AND created_at > {since}")
            : db.Database.SqlQuery<long>($@"SELECT COALESCE(SUM((payload->>'calls')::bigint), 0) AS ""Value"" FROM beauty_ai_actions
                WHERE tool_name = {ChatJournal.LlmUsage} AND created_at >= {since}");
        return (await rows.ToListAsync(ct)).FirstOrDefault();
    }

    /// <summary>Частка місячного ліміту запитів tenant, відведена чату (<see cref="ChatAssistantOptions.TenantMonthlyShare"/>), вичерпана.</summary>
    private async Task<bool> ChatBudgetExhaustedAsync(long monthlyRequestLimit, DateTimeOffset now, CancellationToken ct)
    {
        var budget = Math.Max(1, (long)Math.Ceiling(monthlyRequestLimit * opts.TenantMonthlyShare));
        var periodStart = new DateTimeOffset(AiTenantRuntime.PeriodOf(now).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return await SumLlmCallsAsync(null, periodStart, ct) >= budget;
    }

    /// <summary>
    /// Розмова створена за останню годину і перед нею вже було `MaxNewConversationsPerChannelPerHour` нових розмов каналу: ШІ їй не відповідає
    /// (кожен акаунт Telegram - нова розмова, тож без цього ліміту можна множити витрати акаунтами).
    /// </summary>
    private async Task<bool> NewConversationOverLimitAsync(Conversation conv, DateTimeOffset now, CancellationToken ct)
    {
        var from = now.AddHours(-1);
        if (conv.CreatedAt <= from) return false;
        var earlier = await db.Conversations.AsNoTracking()
            .CountAsync(c => c.ChannelId == conv.ChannelId && c.CreatedAt > from && c.CreatedAt < conv.CreatedAt, ct);
        return earlier >= opts.MaxNewConversationsPerChannelPerHour;
    }

    private Task<bool> RecentTemplateAsync(Guid conversationId, string body, TimeSpan window, DateTimeOffset now, CancellationToken ct) =>
        db.Messages.AsNoTracking().AnyAsync(m => m.ConversationId == conversationId && m.Direction == MessageDirection.Outbound
            && m.SenderType == MessageSenderType.System && m.Body == body && m.CreatedAt > now - window, ct);

    // ---------------------------------------------------------------- кроки 8-10

    /// <returns>true - хід відкладено (тимчасова причина); false - хід завершено.</returns>
    private async Task<bool> RunModelAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        var conv = t.Conv;
        var last = texts[^1];
        var history = await BuildHistoryAsync(t, texts, ct);

        await RenewLeaseAsync(conv.Id, ct);
        limiter.RecordTurn(conv.Id); // хід клієнта (TASK-735): лічильник «надсилаєте дуже часто» окремо від запитів LLM
        limiter.Record(conv.Id);
        var request = new AssistantRequest(
            conv.TenantId, conv.ClientId, last.Body, Mode: null, Scope: AiScope.ClientChat, ConversationId: conv.Id,
            History: history, DefaultLocationId: conv.Channel!.LocationId);
        var result = await assistant.HandleAsync(request, ct);
        await RenewLeaseAsync(conv.Id, ct);
        if (result.LlmCalls > 1) limiter.Record(conv.Id, result.LlmCalls - 1); // ітерації tools теж запити (M2)

        // Облік запитів LLM у журналі: джерело добового ліміту розмови й місячного бюджету чату.
        var extra = new List<AiActionRecord>();
        AiActionRecord Usage(int calls) =>
            new(Guid.NewGuid(), conv.TenantId, ChatJournal.LlmUsage, $"conversation:{conv.Id}", t.Now, AiActionStatus.Done,
                false, t.Mode, JsonSerializer.Serialize(new { calls }), conv.Id);
        var usageAt = -1;
        if (result.LlmCalls > 0)
        {
            usageAt = extra.Count;
            extra.Add(Usage(result.LlmCalls));
        }
        if (result.Deferred)
        {
            await DeferAsync(t, extra, ct);
            return true;
        }
        if (last.Body.EnumerateRunes().Count() > PromptInjectionGuard.MaxClientMessageCodePoints)
            extra.Add(new AiActionRecord(Guid.NewGuid(), conv.TenantId, "input_truncated", $"conversation:{conv.Id}", t.Now, AiActionStatus.Flagged,
                false, t.Mode, JsonSerializer.Serialize(new { limit = PromptInjectionGuard.MaxClientMessageCodePoints }), conv.Id));

        // Handoff: правила й помилки провайдера журналює BeautyAssistant, request_handoff - tool; помилки tools - тут.
        if (result.HandedOff)
        {
            var reason = result.HandoffReason ?? "other";
            await HandoffAsync(t, reason, reason == BeautyAssistant.HandoffAiUnavailable ? ChatTemplates.AiUnavailable : null, ct, extra, journalHandoff: false);
            return false;
        }
        if (result.Chat is { ToolErrors: >= 3 })
        {
            await HandoffAsync(t, "tool_errors", null, ct, extra);
            return false;
        }

        // L1: canary-перевірка відповіді моделі (витік ядра/ChatRules/додатку, ключі). Збіг = T-fallback + запис `flagged`, текст не показується.
        var addendum = (await runtime.GetEffectiveAsync(conv.TenantId, ct)).PromptAddendum;
        var check = ChatOutputGuard.Inspect(result.ReplyText, addendum);
        if (check.FlagReason is { } flagReason)
            extra.Add(new AiActionRecord(Guid.NewGuid(), conv.TenantId, ChatJournal.OutputFlagged, $"conversation:{conv.Id}", t.Now,
                AiActionStatus.Flagged, false, t.Mode, JsonSerializer.Serialize(new { reason = flagReason }), conv.Id));
        var text = check.Text;

        // E. Порожня/службова відповідь моделі (лише UUID, пунктуація): один повтор ходу з підказкою; далі - нейтральне прохання уточнити.
        var emptyReply = text is null && check.FlagReason is null && result.Chat?.Prepared is null;
        if (emptyReply)
        {
            await RenewLeaseAsync(conv.Id, ct);
            var retry = await assistant.HandleAsync(request with { RetryEmptyReply = true }, ct);
            await RenewLeaseAsync(conv.Id, ct);
            if (retry.LlmCalls > 0) limiter.Record(conv.Id, retry.LlmCalls);
            var total = result.LlmCalls + retry.LlmCalls;
            if (total > 0)
            {
                var rec = Usage(total);
                if (usageAt >= 0) extra[usageAt] = rec; else extra.Add(rec);
            }
            // Збій повтору (429, недоступність, handoff моделі, помилки tools) хід не ламає: лишається нейтральна фраза.
            if (!retry.Deferred && !retry.HandedOff && retry.Chat is not { ToolErrors: >= 3 })
            {
                result = retry with { LlmCalls = total };
                var retryCheck = ChatOutputGuard.Inspect(result.ReplyText, addendum);
                if (retryCheck.FlagReason is { } retryFlag)
                    extra.Add(new AiActionRecord(Guid.NewGuid(), conv.TenantId, ChatJournal.OutputFlagged, $"conversation:{conv.Id}", t.Now,
                        AiActionStatus.Flagged, false, t.Mode, JsonSerializer.Serialize(new { reason = retryFlag }), conv.Id));
                check = retryCheck;
                text = check.Text;
            }
        }

        // PolicyClaimGuard (§22.9): твердження про дзвінки/канали/нагадування/оплату/повернення, що суперечать фактам закладу або виходять за них.
        // Сирий текст клієнту не показується: T-policy-check + handoff policy_unknown + позначка flagged (код правила, без тексту).
        if (text is not null && check.FlagReason is null
            && ChatOutputGuard.FindPolicyClaim(text, await LoadPolicyFactsAsync(conv, ct)) is { } policyRule)
        {
            extra.Add(new AiActionRecord(Guid.NewGuid(), conv.TenantId, ChatJournal.OutputFlagged, $"conversation:{conv.Id}", t.Now,
                AiActionStatus.Flagged, false, t.Mode, JsonSerializer.Serialize(new { reason = ChatOutputGuard.FlagPolicyClaim, rule = policyRule }), conv.Id));
            await HandoffAsync(t, ClientChatToolNames.PolicyUnknown, ChatTemplates.PolicyCheck, ct, extra);
            return false;
        }
        var noText = text is null && check.FlagReason is null; // порожньо навіть після повтору -> T-clarify, не «не вдалося»
        if (result.Chat is { DraftOnly: true })
        {
            // suggest: нічого клієнту; чернетка для менеджера (його підтвердження надсилає її через /ai/actions/{id}/approve)
            await CommitAsync(t, new Reply(text ?? (noText ? ChatTemplates.Clarify : ChatTemplates.Fallback), MessageSenderType.Ai, Draft: true), null, extra, ct);
            return false;
        }

        var prepared = result.Chat?.Prepared;
        var buttons = t.AutoReplies && ButtonsAvailable(conv);
        if (prepared is null)
        {
            // §23.6 «Слоти»: успішний find_slots без prepare_booking -> slot_pick із ≤ SlotButtons слотів на повідомленні моделі.
            if (buttons && check.FlagReason is null && await SlotsReplyAsync(t, text, result.Chat?.LastSlots, ct) is { } slotsReply)
            {
                await CommitAsync(t, slotsReply, null, extra, ct);
                return false;
            }
            await CommitAsync(t, text is null
                ? new Reply(noText ? ChatTemplates.Clarify : ChatTemplates.Fallback, MessageSenderType.System)
                : new Reply(text, MessageSenderType.Ai), null, extra, ct);
            return false;
        }
        // Підсумок запису - канонічний текст КОДУ; текст моделі лише вступ (його обрізаємо, підсумок - ніколи; разом ≤ 4096 UTF-16).
        // Пропозиція лишається `prepared` до ДОСТАВКИ підсумку (M1): shown - лише після sent.
        if (buttons)
        {
            // §23.6 «Підсумок з кнопками»: текст моделі і T-summary - ДВА повідомлення в одній транзакції; підсумок і кнопки лише від коду.
            var replies = new List<Reply>();
            if (text is not null) replies.Add(new Reply(text, MessageSenderType.Ai));
            replies.Add(new Reply(ChatSummaryText.Format(prepared.Summary, prepared.TtlMinutes, promptOpts.CurrencyLabel, buttons: true), MessageSenderType.System,
                ShowProposalId: prepared.ProposalId, Keyboard: KeyboardBuilder.BookingConfirm(prepared.ProposalId), KeySuffix: ":s"));
            await CommitManyAsync(t, new CommitSpec(replies) { Journal = extra }, ct);
            return false;
        }
        var body = ChatOutputGuard.ComposeWithSummary(text, prepared.SummaryText);
        await CommitAsync(t, new Reply(body, MessageSenderType.Ai, ShowProposalId: prepared.ProposalId), null, extra, ct);
        return false;
    }

    /// <summary>Факти політики свого tenant для вихідного guard. Порт недоступний або збій читання -> консервативні типові (дзвінка немає).</summary>
    private async Task<ChatPolicyFacts> LoadPolicyFactsAsync(Conversation conv, CancellationToken ct)
    {
        if (services.GetService<IChatPolicyFactsPort>() is not { } port) return ChatPolicyFacts.Default;
        try
        {
            return await port.GetAsync(conv.TenantId, conv.ClientId, conv.Channel?.LocationId, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
        {
            return ChatPolicyFacts.Default;
        }
    }

    /// <summary>
    /// Хід відкладено через тимчасову причину (частота tenant/чату, 429 провайдера), M2: вхідні лишаються `received` (позначка + лічильник),
    /// передачі менеджеру на 12 год немає, повтор після `RateLimitRetrySeconds`. Після `MaxDeferrals` відкладень - нейтральний шаблон (без handoff).
    /// </summary>
    private async Task DeferAsync(Turn t, List<AiActionRecord> usage, CancellationToken ct)
    {
        foreach (var r in usage) await journal.AppendAsync(r, ct);
        usage.Clear();
        db.ChangeTracker.Clear();

        var attempts = t.Batch.Max(m => m.Attempts) + 1;
        if (attempts >= opts.MaxDeferrals)
        {
            if (!t.AutoReplies) await SetStatusAsync(t.Batch, Skipped, ct);
            else await CommitAsync(t, new Reply(ChatTemplates.Busy, MessageSenderType.System), null, null, ct);
            return;
        }
        var ids = t.Batch.Select(m => m.Id).ToList();
        var now = clock.GetUtcNow();
        await db.Messages.Where(m => ids.Contains(m.Id) && m.Status == Received).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Attempts, m => m.Attempts + 1).SetProperty(m => m.LastError, DeferredMarker).SetProperty(m => m.UpdatedAt, now), ct);
        log.LogInformation("chat_turn_deferred conversation={ConversationId} attempt={Attempt}", t.Conv.Id, attempts);
    }

    private async Task<List<ChatHistoryItem>> BuildHistoryAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        var batchIds = t.Batch.Select(m => m.Id).ToList();
        var older = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == t.Conv.Id && !batchIds.Contains(m.Id)
                // §25.5.8: контакт (kind=contact) не входить в історію LLM; тіла «[номер телефону]» входять як є
                && ((m.Direction == MessageDirection.Inbound && m.Kind != InboundKinds.Command && m.Kind != InboundKinds.Callback && m.Kind != InboundKinds.Contact && (m.Status == Processed || m.Status == Skipped || m.Status == Held))
                    || (m.Direction == MessageDirection.Outbound && (m.Status == "sent" || m.Status == "pending" || m.Status == "sending"))))
            .OrderByDescending(m => m.CreatedAt).Take(promptOpts.HistoryMessages).ToListAsync(ct);
        older.Reverse();

        // TASK-799: межа епізоду - модель не бачить повідомлень до /book, підтвердження/скасування запису, дій менеджера й довгої тиші (БД не змінюється).
        var batchStart = t.Batch.Min(m => m.CreatedAt);
        var batchEnd = t.Batch.Max(m => m.CreatedAt);
        var candidates = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == t.Conv.Id
                && ((m.Direction == MessageDirection.Inbound && (m.Kind == InboundKinds.Command || m.Kind == InboundKinds.Callback))
                    || (m.Direction == MessageDirection.Outbound && m.SenderType == MessageSenderType.System)))
            .OrderByDescending(m => m.CreatedAt).Take(60)
            .Select(m => new EpisodeCandidate(m.CreatedAt, m.Direction == MessageDirection.Inbound, m.SenderType == MessageSenderType.System,
                m.Kind, m.Body, m.CallbackData)).ToListAsync(ct);
        var managerAt = await db.AiActions.AsNoTracking()
            .Where(a => a.ConversationId == t.Conv.Id && ChatEpisode.ManagerBoundaryTools.Contains(a.ToolName))
            .Select(a => (DateTimeOffset?)a.CreatedAt).MaxAsync(ct);
        var cutoff = ChatEpisode.FindCutoff(candidates, managerAt, older.Select(m => m.CreatedAt).ToList(), batchStart, batchEnd, promptOpts.EpisodeIdleTimeout);
        if (cutoff is { } cut) older = older.Where(m => m.CreatedAt > cut).ToList();

        var items = new List<ChatHistoryItem>();
        foreach (var m in older)
        {
            if (m.Direction == MessageDirection.Inbound)
                items.Add(new ChatHistoryItem(ChatHistoryRole.Client, m.Body, m.Kind == InboundKinds.Unsupported ? ChatHistoryKind.Unsupported : ChatHistoryKind.Normal));
            else
                items.Add(new ChatHistoryItem(ChatHistoryRole.Assistant, m.Body, m.SenderType switch
                {
                    MessageSenderType.System => ChatHistoryKind.Template,
                    MessageSenderType.Staff => ChatHistoryKind.Staff,
                    _ => ChatHistoryKind.Normal,
                }));
        }
        // Пакет швидких повідомлень: усі текстові, крім останнього, йдуть в історію (останнє - поточне).
        foreach (var m in texts.Take(texts.Count - 1)) items.Add(new ChatHistoryItem(ChatHistoryRole.Client, m.Body));
        return items;
    }

    // ---------------------------------------------------------------- handoff

    /// <param name="template">Шаблон клієнту; null = T-handoff. У suggest клієнту нічого не надсилається.</param>
    private async Task HandoffAsync(Turn t, string reason, string? template, CancellationToken ct,
        List<AiActionRecord>? extra = null, bool journalHandoff = true)
    {
        var records = extra ?? [];
        if (journalHandoff)
            records.Add(new AiActionRecord(Guid.NewGuid(), t.Conv.TenantId, "handoff", $"conversation:{t.Conv.Id}", t.Now, AiActionStatus.HandedOff,
                false, t.Mode, JsonSerializer.Serialize(new { reason, source = "pipeline" }), t.Conv.Id));
        var reply = t.AutoReplies ? new Reply(template ?? ChatTemplates.Handoff, MessageSenderType.System) : null;
        await CommitAsync(t, reply, reason, records, ct);
    }

    // ---------------------------------------------------------------- коміт відповіді

    /// <summary>
    /// Одна транзакція: відповідь (ключ ідемпотентності `ai-reply:{conv}:{lastInbound}`) + журнал + handoff + вхідні `processed`
    /// (+ пропозиція prepared -> shown). Після коміту - inline-відправка (успіх -> sent, тимчасова помилка -> pending для воркера).
    /// Тонка обгортка над <see cref="CommitManyAsync"/> (TASK-753: кілька повідомлень, клавіатури, callback).
    /// </summary>
    private Task CommitAsync(Turn t, Reply? reply, string? handoffReason, IEnumerable<AiActionRecord>? journalRecords, CancellationToken ct,
        PromptState? prompt = null, Func<CancellationToken, Task>? txWork = null) =>
        CommitManyAsync(t, new CommitSpec(reply is null ? [] : [reply])
        {
            HandoffReason = handoffReason, Journal = journalRecords, Prompt = prompt, TxWork = txWork, RevokeAiKeyboards = handoffReason is not null,
        }, ct);

    private async Task DispatchInlineAsync(Guid tenantId, Guid messageId, bool isSummary, CancellationToken ct)
    {
        try
        {
            await dispatcher.DispatchAsync(tenantId, messageId, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
        {
            // Збій до результату відправки: віддаємо на повтор (pending), не лишаємо в sending. Лише тип винятку (у тексті може бути URL).
            log.LogWarning("chat_dispatch_failed message={MessageId} error={ErrorType}", messageId, ex.GetType().Name);
            await db.Messages.Where(m => m.Id == messageId && m.Status == "sending")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending").SetProperty(m => m.UpdatedAt, clock.GetUtcNow()), CancellationToken.None);
        }
        if (!isSummary) return;

        // M1: підсумок доставлено -> prepared -> shown; остаточно не доставлено -> пропозиція superseded + нейтральний шаблон клієнту.
        try
        {
            if (await Summaries.OnDispatchedAsync(messageId, ct) is { } followUp)
                await dispatcher.DispatchAsync(tenantId, followUp, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
        {
            log.LogWarning("chat_summary_followup_failed message={MessageId} error={ErrorType}", messageId, ex.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- lease, пакет, допоміжне

    private async Task<bool> TryLeaseAsync(Guid conversationId, CancellationToken ct)
    {
        var secs = opts.LeaseSeconds;
        var n = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE beauty_conversations SET ai_lease_until = now() + {secs} * interval '1 second', ai_lease_owner = {_owner}
               WHERE id = {conversationId} AND (ai_lease_until IS NULL OR ai_lease_until < now())", ct);
        return n == 1;
    }

    private async Task RenewLeaseAsync(Guid conversationId, CancellationToken ct)
    {
        var secs = opts.LeaseSeconds;
        var n = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE beauty_conversations SET ai_lease_until = now() + {secs} * interval '1 second'
               WHERE id = {conversationId} AND ai_lease_owner = {_owner}", ct);
        if (n != 1) throw new LeaseLostException();
    }

    private async Task ReleaseLeaseAsync(Guid conversationId)
    {
        try
        {
            // Окремий scope: після скасування посеред відкриття з'єднання EF міг лишити його відкритим БЕЗ app.tenant_id (інтерцептор не
            // встиг), і RLS мовчки відфільтрував би UPDATE. Нове з'єднання відкривається вже з tenant.
            using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            TenantGuard.EnsureOrSetForTrustedJob(scope.ServiceProvider.GetRequiredService<TenantContext>(), _tenantId);
            await scope.ServiceProvider.GetRequiredService<BeautyDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE beauty_conversations SET ai_lease_until = NULL, ai_lease_owner = NULL
                   WHERE id = {conversationId} AND ai_lease_owner = {_owner}", CancellationToken.None);
        }
        catch (Exception ex) when (ex is not (TenantMismatchException or TenantScopeMissingException))
        {
            log.LogWarning("chat_lease_release_failed conversation={ConversationId} error={ErrorType}", conversationId, ex.GetType().Name);
        }
    }

    private async Task<List<Message>> LoadBatchAsync(Guid conversationId, CancellationToken ct)
    {
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Direction == MessageDirection.Inbound && m.Status == Received)
            .OrderBy(m => m.SentAt).Take(opts.MaxBatch * 2).ToListAsync(ct);
        // Порядок: дата Telegram, потім ЧИСЛОВИЙ message_id (рядковий «42:10» < «42:9»).
        // З натисканнями кнопок порядок - за серверним часом вставки (callback не має дати Telegram), §23.6; інакше - як раніше.
        if (rows.Any(m => m.Kind == InboundKinds.Callback))
            return rows.OrderBy(m => m.CreatedAt).ThenBy(m => NumericId(m.ExternalMessageId)).Take(opts.MaxBatch).ToList();
        return rows.OrderBy(m => m.SentAt).ThenBy(m => NumericId(m.ExternalMessageId)).ThenBy(m => m.CreatedAt).Take(opts.MaxBatch).ToList();
    }

    private static long NumericId(string? external)
    {
        if (string.IsNullOrEmpty(external)) return long.MaxValue;
        if (external.Contains(":cb:", StringComparison.Ordinal)) return 0; // натискання кнопки (§23.4)
        var i = external.LastIndexOf(':');
        return long.TryParse(external.AsSpan(i + 1), out var v) ? v : long.MaxValue;
    }

    private async Task SetStatusAsync(IEnumerable<Message> messages, string status, CancellationToken ct)
    {
        var ids = messages.Select(m => m.Id).ToList();
        var now = clock.GetUtcNow();
        await db.Messages.Where(m => ids.Contains(m.Id) && m.Status == Received)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, status).SetProperty(m => m.UpdatedAt, now), ct);
    }

    /// <summary>Збій ходу: лічильник спроб на пакеті; після MaxTurnAttempts пакет `skipped`, щоб одне «отруєне» повідомлення не крутилось вічно.</summary>
    private async Task RegisterFailureAsync(List<Message> batch)
    {
        try
        {
            db.ChangeTracker.Clear();
            var ids = batch.Select(m => m.Id).ToList();
            await db.Messages.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.Attempts, m => m.Attempts + 1), CancellationToken.None);
            var max = opts.MaxTurnAttempts;
            await db.Messages.Where(m => ids.Contains(m.Id) && m.Status == Received && m.Attempts >= max)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, Skipped), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not (TenantMismatchException or TenantScopeMissingException))
        {
            log.LogWarning("chat_failure_register_failed error={ErrorType}", ex.GetType().Name);
        }
    }

    private static string ReplyKey(Guid conversationId, Guid lastInboundId) => $"{KeyPrefix}{conversationId}:{lastInboundId}";

    internal static bool ChannelAssistantEnabled(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings)) return false;
        try
        {
            using var doc = JsonDocument.Parse(settings);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("assistant", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
