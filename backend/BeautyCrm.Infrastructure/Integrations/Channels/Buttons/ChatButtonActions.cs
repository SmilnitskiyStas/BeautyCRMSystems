using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AiStatus = BeautyCrm.Infrastructure.AI.Beauty.AiActionStatus;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

/// <param name="InboundMessageId">Вхідний callback-рядок: <c>used_by_message_id</c> клавіатури.</param>
/// <param name="Profile">Профіль Telegram для імені нового клієнта (як у текстовому «Так»).</param>
public sealed record ButtonActionContext(Guid ConversationId, Guid InboundMessageId, CallbackResolution Resolution, TelegramProfile? Profile = null);

/// <summary>Коди результату дії -> шаблон відповіді (TASK-753). Уся логіка вибору шаблону - у конвеєрі; тут лише факти.</summary>
public static class ButtonResultCodes
{
    public const string Confirmed = "confirmed";                 // T-confirmed (Confirmed.ReminderChoiceNeeded -> reminder_choice, інакше appointment_actions)
    public const string AlreadyConfirmed = "already_confirmed";  // повтор: запис уже існує
    public const string SlotTaken = "slot_taken";                // T-slot-taken
    public const string PriceChanged = "price_changed";          // T-price-changed
    public const string Limit = "limit";                         // T-limit + handoff booking_limit
    public const string InProgress = "in_progress";              // T-in-progress
    public const string Failed = "failed";                       // T-handoff + handoff booking_failed
    public const string Expired = "expired";                     // T-expired
    public const string ProposalNotActive = "proposal_not_active"; // T-action-stale
    public const string Declined = "declined";                   // T-declined
    public const string OtherTime = "other_time";                // T-other-time
    public const string SummaryReady = "summary_ready";          // Prepared -> T-summary + booking_confirm
    public const string PrepareFailed = "prepare_failed";        // T-fallback + [Передати адміністратору]
    public const string CancelScreen = "cancel_screen";          // T-cancel-confirm + cancel_confirm
    public const string Cancelled = "cancelled";                 // T-cancelled
    public const string CancelKept = "cancel_kept";              // T-cancel-kept
    public const string CancelUnavailable = "cancel_unavailable"; // T-cancel-unavailable
    public const string CancelLimit = "cancel_limit";            // T-cancel-limit + handoff cancel_limit
    public const string CancelFailed = "cancel_failed";          // T-handoff + handoff cancel_failed
    public const string AttendOk = "attend_ok";                  // T-attend-ok
    public const string AttendNoop = "attend_noop";              // без відповіді
    public const string AppointmentInactive = "appointment_inactive"; // T-appointment-inactive
    public const string RescheduleHandoff = "reschedule_handoff"; // T-reschedule-handoff + handoff reschedule_request
    public const string ReminderChoice = "reminder_choice";      // Option.Method/Offsets -> ReminderChoiceApplier (клавіатуру вже споживано)
    public const string MyList = "my_list";                      // T-my-list + appointment_list
    public const string MyEmpty = "my_empty";                    // T-my-empty
    public const string BookStart = "book_start";                // i:book
    public const string Help = "help";                           // i:help
    public const string Human = "human_requested";               // i:human -> handoff human_requested
    public const string Stop = "stop";                           // i:stop -> ReminderStopHandler
    public const string MarketingConsentChoice = "marketing_consent"; // mc: CAS виконано; домен (TASK-782) ставить SetAsync з ConsentLevel/ConsentTextVersion
    public const string CampaignOffer = "campaign_offer";        // co: без CAS; домен (TASK-782) перевіряє купон і ставить пропозицію розмови
    public const string MarketingMenu = "marketing_menu";        // i:mkt -> T-marketing-menu
    public const string MarketingOff = "marketing_off";          // i:mkoff -> SetAsync(none); CampaignRecipientId - з повідомлення кампанії (якщо є)
    public const string CouponUnavailable = "coupon_unavailable"; // TASK-782: купон на підтвердженні недійсний -> T-coupon-unavailable, звичайна ціна
    public const string Used = "used";                           // CAS програно (гонка двох натискань)
    public const string Stale = "stale";                         // результат не Valid / недозволений verb
}

public sealed record ButtonActionResult(string Code)
{
    public ChatConfirmed? Confirmed { get; init; }
    public ChatPrepared? Prepared { get; init; }
    public Guid? ProposalId { get; init; }
    public ChatAppointmentInfo? Appointment { get; init; }
    public CancelPolicyPreview? Policy { get; init; }
    public IReadOnlyList<ChatAppointmentInfo>? Appointments { get; init; }
    public KeyboardOption? Option { get; init; }
    /// <summary>TASK-781: mc - рівень і версія тексту з options у БД; co / i:mkoff - одержувач кампанії (i:mkoff - лише за повідомленням кампанії цієї розмови).</summary>
    public string? ConsentLevel { get; init; }
    public string? ConsentTextVersion { get; init; }
    public Guid? CampaignRecipientId { get; init; }
    /// <summary>booking_limit | booking_failed | cancel_limit | cancel_failed | reschedule_request | human_requested.</summary>
    public string? HandoffReason { get; init; }
}

/// <summary>
/// Доменні дії за кнопками (§23.7), викликає конвеєр (TASK-753) після <see cref="ChatKeyboardService.ResolveCallbackAsync"/> == Valid.
/// Порядок: доменні перевірки (крок 9) -> CAS клавіатури (крок 10) -> ефект. Без LLM. Тільки tenant + розмова + клієнт розмови;
/// чужий запис / розмова = відмова без витоку. Підтвердження - той самий <see cref="ChatBookingService.ConfirmAsync"/>, що й текстове «Так».
/// Повторна обробка того самого натискання (<c>Resolution.Replay</c>) ефект не дублює: запис за ключем пропозиції, скасування - клієнтом.
/// </summary>
public sealed class ChatButtonActions(
    BeautyDbContext db, TenantContext tenant, ChatKeyboardService keyboards, IServiceProvider services, ClientChatAppointments appointments,
    IAiActionJournal journal, AiTenantRuntime runtime, TimeProvider clock)
{
    /// <summary>Лениво: <see cref="ChatBookingService"/> реєструє API (запис з чату), тож DI без нього лишається валідним.</summary>
    private ChatBookingService booking => services.GetRequiredService<ChatBookingService>();

    public async Task<ButtonActionResult> ExecuteAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        var res = ctx.Resolution;
        if (!res.IsValid) return new ButtonActionResult(ButtonResultCodes.Stale);
        if (res.Outcome == CallbackOutcome.Stateless) return await StatelessAsync(ctx, ct);

        return res.Verb switch
        {
            ButtonVerbs.ConfirmBooking => await ConfirmAsync(ctx, ct),
            ButtonVerbs.DeclineBooking => await DeclineAsync(ctx, ButtonResultCodes.Declined, ct),
            ButtonVerbs.OtherTime => await DeclineAsync(ctx, ButtonResultCodes.OtherTime, ct),
            ButtonVerbs.SlotPick => await SlotPickAsync(ctx, ct),
            ButtonVerbs.ReminderChoice => await ReminderChoiceAsync(ctx, ct),
            ButtonVerbs.CancelAsk => await CancelAskAsync(ctx, ct),
            ButtonVerbs.Attend => await AttendAsync(ctx, ct),
            ButtonVerbs.Reschedule => await RescheduleAsync(ctx, ct),
            ButtonVerbs.CancelYes => await CancelYesAsync(ctx, ct),
            ButtonVerbs.CancelNo => await CancelNoAsync(ctx, ct),
            ButtonVerbs.MarketingConsent => await MarketingConsentAsync(ctx, ct),
            ButtonVerbs.CampaignOffer => new ButtonActionResult(ButtonResultCodes.CampaignOffer)
                { CampaignRecipientId = res.CampaignRecipientId }, // не споживає; ефекти (купон, пропозиція розмови) - домен
            _ => new ButtonActionResult(ButtonResultCodes.Stale),
        };
    }

    private static Guid? AppointmentOf(CallbackResolution r) => r.Option?.AppointmentId ?? r.Keyboard?.AppointmentId;

    /// <summary>CAS клавіатури; для повторної обробки того самого натискання - уже виконано.</summary>
    private async Task<bool> ConsumeAsync(ButtonActionContext ctx, CancellationToken ct) =>
        ctx.Resolution.Replay || await keyboards.TryConsumeAsync(ctx.Resolution.Keyboard!.Id, ctx.Resolution.OptionPosition, ctx.InboundMessageId, ct);

    // ------------------------------------------------------------------ пропозиція

    private async Task<ChatProposalRow?> ShownProposalAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        var kb = ctx.Resolution.Keyboard!;
        if (kb.ProposalId is not { } pid) return null;
        var p = await db.ChatBookingProposals.AsNoTracking().Where(x => x.Id == pid && x.ConversationId == ctx.ConversationId)
            .Select(x => new ChatProposalRow(x.Id, x.Status, x.SummaryMessageId, x.ExpiresAt)).FirstOrDefaultAsync(ct);
        // Підтверджується саме та пропозиція, яку бачив клієнт: summary_message_id = повідомлення цієї клавіатури (ADR-013 §2.3).
        if (p is null || p.SummaryMessageId != kb.MessageId) return null;
        var statusOk = ctx.Resolution.Replay
            ? p.Status is ChatProposalStatus.Shown or ChatProposalStatus.Confirmed or ChatProposalStatus.Rejected
            : p.Status == ChatProposalStatus.Shown;
        return statusOk ? p : null;
    }

    private sealed record ChatProposalRow(Guid Id, string Status, Guid? SummaryMessageId, DateTimeOffset ExpiresAt);

    private async Task<ButtonActionResult> ConfirmAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        if (await ShownProposalAsync(ctx, ct) is not { } p) return new ButtonActionResult(ButtonResultCodes.ProposalNotActive);
        if (!ctx.Resolution.Replay && clock.GetUtcNow() >= p.ExpiresAt)
        {
            await booking.ExpireAsync(ctx.ConversationId, p.Id, ct);
            return new ButtonActionResult(ButtonResultCodes.Expired);
        }
        if (!await ConsumeAsync(ctx, ct)) return new ButtonActionResult(ButtonResultCodes.Used);

        var r = await booking.ConfirmAsync(new ChatConfirmRequest(ctx.ConversationId, p.Id, ClientConfirmed: true, ctx.Profile), ct);
        if (r.IsOk)
        {
            var confirmed = r.Value!;
            // Повтор ТОГО САМОГО натискання (падіння до відповіді): запис уже є, але клієнт ще не отримав T-confirmed.
            if (confirmed.AlreadyConfirmed && !ctx.Resolution.Replay)
                return new ButtonActionResult(ButtonResultCodes.AlreadyConfirmed) { Confirmed = confirmed, ProposalId = p.Id };
            await JournalConfirmAsync(ctx.ConversationId, p.Id, confirmed.Appointment.Id, ct);
            return new ButtonActionResult(ButtonResultCodes.Confirmed) { Confirmed = confirmed, ProposalId = p.Id };
        }
        return r.Error!.Code switch
        {
            "slot_unavailable" or "slot_in_past" or "invalid_start" => new ButtonActionResult(ButtonResultCodes.SlotTaken),
            "price_changed" => new ButtonActionResult(ButtonResultCodes.PriceChanged),
            "coupon_unavailable" or "coupon_invalid" => new ButtonActionResult(ButtonResultCodes.CouponUnavailable), // TASK-782 (§26.9.3)
            "proposal_expired" => new ButtonActionResult(ButtonResultCodes.Expired),
            "confirmation_in_progress" => new ButtonActionResult(ButtonResultCodes.InProgress),
            "booking_limit_reached" => new ButtonActionResult(ButtonResultCodes.Limit) { HandoffReason = "booking_limit" },
            "proposal_not_found" or "proposal_not_shown" or "proposal_not_active" => new ButtonActionResult(ButtonResultCodes.ProposalNotActive),
            _ => new ButtonActionResult(ButtonResultCodes.Failed) { HandoffReason = "booking_failed" },
        };
    }

    private async Task<ButtonActionResult> DeclineAsync(ButtonActionContext ctx, string code, CancellationToken ct)
    {
        if (await ShownProposalAsync(ctx, ct) is not { } p) return new ButtonActionResult(ButtonResultCodes.ProposalNotActive);
        if (!ctx.Resolution.Replay && clock.GetUtcNow() >= p.ExpiresAt)
        {
            await booking.ExpireAsync(ctx.ConversationId, p.Id, ct);
            return new ButtonActionResult(ButtonResultCodes.Expired);
        }
        if (!await ConsumeAsync(ctx, ct)) return new ButtonActionResult(ButtonResultCodes.Used);
        // Replay: пропозиція вже rejected - це успіх попередньої спроби.
        var declined = p.Status == ChatProposalStatus.Rejected || await booking.DeclineAsync(ctx.ConversationId, p.Id, ct);
        return declined ? new ButtonActionResult(code) { ProposalId = p.Id } : new ButtonActionResult(ButtonResultCodes.ProposalNotActive);
    }

    private async Task<ButtonActionResult> SlotPickAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        var o = ctx.Resolution.Option!;
        if (o.LocationId is not { } loc || o.ServiceId is not { } svc || o.SpecialistId is not { } spec || o.StartsAt is not { } at)
            return new ButtonActionResult(ButtonResultCodes.Stale);
        if (!await ConsumeAsync(ctx, ct)) return new ButtonActionResult(ButtonResultCodes.Used);

        // TASK-782 (§26.9.3): купон із пропозиції розмови додає код (як і для prepare_booking моделі)
        var r = await services.GetRequiredService<BeautyCrm.Infrastructure.AI.Beauty.Conversations.ChatCouponOffers>()
            .PrepareAsync(booking, new ChatPrepareRequest(ctx.ConversationId, loc, svc, spec, at), ct);
        if (r.IsOk) return new ButtonActionResult(ButtonResultCodes.SummaryReady) { Prepared = r.Value, ProposalId = r.Value!.ProposalId };
        return r.Error!.Code is "slot_not_offered" or "invalid_start"
            ? new ButtonActionResult(ButtonResultCodes.SlotTaken)
            : new ButtonActionResult(ButtonResultCodes.PrepareFailed);
    }

    // ------------------------------------------------------------------ нагадування

    /// <summary>Лише передумови й CAS; сам вибір застосовує <c>ReminderChoiceApplier</c> (TASK-740) з <see cref="ButtonActionResult.Option"/>.</summary>
    private async Task<ButtonActionResult> ReminderChoiceAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        var o = ctx.Resolution.Option!;
        if (o.Method is not ("none" or "telegram" or "call")) return new ButtonActionResult(ButtonResultCodes.Stale);
        if (ctx.Resolution.Keyboard!.AppointmentId is not { } id || await appointments.GetOwnActiveAsync(ctx.ConversationId, id, ct) is not { } appt)
            return new ButtonActionResult(ButtonResultCodes.AppointmentInactive);
        if (!await ConsumeAsync(ctx, ct)) return new ButtonActionResult(ButtonResultCodes.Used);
        return new ButtonActionResult(ButtonResultCodes.ReminderChoice) { Option = o, Appointment = appt };
    }

    // ------------------------------------------------------------------ згода (§26.9.2)

    /// <summary>Лише CAS одноразовості; запис згоди (SetAsync, source=chat, via=button) виконує домен (TASK-782) з <see cref="ButtonActionResult.ConsentLevel"/>.</summary>
    private async Task<ButtonActionResult> MarketingConsentAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        var res = ctx.Resolution;
        if (res.ConsentLevel is not { } level || res.ConsentTextVersion is not { } version) return new ButtonActionResult(ButtonResultCodes.Stale);
        if (!await ConsumeAsync(ctx, ct)) return new ButtonActionResult(ButtonResultCodes.Used);
        return new ButtonActionResult(ButtonResultCodes.MarketingConsentChoice) { ConsentLevel = level, ConsentTextVersion = version, Option = res.Option };
    }

    // ------------------------------------------------------------------ записи клієнта

    private async Task<ButtonActionResult> CancelAskAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        if (AppointmentOf(ctx.Resolution) is not { } id) return new ButtonActionResult(ButtonResultCodes.CancelUnavailable);
        var c = await appointments.CheckCancelAsync(ctx.ConversationId, id, enforceLimit: false, ct);
        return c.Outcome == ChatCancelOutcome.Ok
            ? new ButtonActionResult(ButtonResultCodes.CancelScreen) { Appointment = c.Appointment, Policy = c.Policy }
            : new ButtonActionResult(ButtonResultCodes.CancelUnavailable);
    }

    private async Task<ButtonActionResult> CancelYesAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        if (AppointmentOf(ctx.Resolution) is not { } id) return new ButtonActionResult(ButtonResultCodes.CancelUnavailable);
        if (!ctx.Resolution.Replay)
        {
            var c = await appointments.CheckCancelAsync(ctx.ConversationId, id, enforceLimit: true, ct);
            if (c.Outcome == ChatCancelOutcome.Limit) return new ButtonActionResult(ButtonResultCodes.CancelLimit) { HandoffReason = c.HandoffReason };
            if (c.Outcome != ChatCancelOutcome.Ok) return new ButtonActionResult(ButtonResultCodes.CancelUnavailable);
            if (!await ConsumeAsync(ctx, ct)) return new ButtonActionResult(ButtonResultCodes.Used);
        }
        var r = await appointments.PerformCancelAsync(ctx.ConversationId, id, "button", ctx.Resolution.Replay, ct);
        return r.Outcome switch
        {
            ChatCancelOutcome.Ok when r.Cancelled => new ButtonActionResult(ButtonResultCodes.Cancelled) { Appointment = r.Appointment, Policy = r.Policy },
            ChatCancelOutcome.RefundFailed => new ButtonActionResult(ButtonResultCodes.CancelFailed) { Appointment = r.Appointment, HandoffReason = r.HandoffReason },
            ChatCancelOutcome.Limit => new ButtonActionResult(ButtonResultCodes.CancelLimit) { HandoffReason = r.HandoffReason },
            _ => new ButtonActionResult(ButtonResultCodes.CancelUnavailable),
        };
    }

    private async Task<ButtonActionResult> CancelNoAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        if (AppointmentOf(ctx.Resolution) is not { } id || await appointments.GetOwnActiveAsync(ctx.ConversationId, id, ct) is not { } appt)
            return new ButtonActionResult(ButtonResultCodes.CancelUnavailable);
        if (!await ConsumeAsync(ctx, ct)) return new ButtonActionResult(ButtonResultCodes.Used);
        return new ButtonActionResult(ButtonResultCodes.CancelKept) { Appointment = appt };
    }

    private async Task<ButtonActionResult> AttendAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        if (AppointmentOf(ctx.Resolution) is not { } id) return new ButtonActionResult(ButtonResultCodes.AppointmentInactive);
        var r = await appointments.ConfirmAttendanceAsync(ctx.ConversationId, id, "button", ct); // клавіатура не споживається
        return r.Outcome switch
        {
            ChatAttendOutcome.Confirmed => new ButtonActionResult(ButtonResultCodes.AttendOk) { Appointment = r.Appointment },
            ChatAttendOutcome.AlreadyConfirmed => new ButtonActionResult(ButtonResultCodes.AttendNoop) { Appointment = r.Appointment },
            _ => new ButtonActionResult(ButtonResultCodes.AppointmentInactive),
        };
    }

    private async Task<ButtonActionResult> RescheduleAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        if (AppointmentOf(ctx.Resolution) is not { } id || await appointments.GetOwnActiveAsync(ctx.ConversationId, id, ct) is not { } appt)
            return new ButtonActionResult(ButtonResultCodes.AppointmentInactive);
        // V1: самостійного перенесення немає - ознака handoff reschedule_request (стан розмови змінює конвеєр).
        return new ButtonActionResult(ButtonResultCodes.RescheduleHandoff) { Appointment = appt, HandoffReason = "reschedule_request" };
    }

    private async Task<ButtonActionResult> StatelessAsync(ButtonActionContext ctx, CancellationToken ct)
    {
        switch (ctx.Resolution.Verb)
        {
            case ButtonVerbs.My:
                var list = await appointments.ListUpcomingAsync(ctx.ConversationId, ct);
                return list.Items.Count == 0
                    ? new ButtonActionResult(ButtonResultCodes.MyEmpty)
                    : new ButtonActionResult(ButtonResultCodes.MyList) { Appointments = list.Items };
            case ButtonVerbs.Book: return new ButtonActionResult(ButtonResultCodes.BookStart);
            case ButtonVerbs.Help: return new ButtonActionResult(ButtonResultCodes.Help);
            case ButtonVerbs.Human: return new ButtonActionResult(ButtonResultCodes.Human) { HandoffReason = "human_requested" };
            case ButtonVerbs.Stop: return new ButtonActionResult(ButtonResultCodes.Stop);
            case ButtonVerbs.MarketingMenu: return new ButtonActionResult(ButtonResultCodes.MarketingMenu);
            case ButtonVerbs.MarketingOff:
                return new ButtonActionResult(ButtonResultCodes.MarketingOff) { CampaignRecipientId = ctx.Resolution.Keyboard?.CampaignRecipientId };
            default: return new ButtonActionResult(ButtonResultCodes.Stale);
        }
    }

    // ------------------------------------------------------------------ журнал

    /// <summary>Як у текстовому шляху (<c>confirm_booking</c>, payload з proposalId) + <c>via</c>; повтор не дублюється.</summary>
    private async Task JournalConfirmAsync(Guid conversationId, Guid proposalId, Guid appointmentId, CancellationToken ct)
    {
        var id = proposalId.ToString();
        var existing = await db.AiActions.AsNoTracking().Where(a => a.ConversationId == conversationId && a.ToolName == ChatButtonJournal.ConfirmBooking)
            .Select(a => a.Payload).ToListAsync(ct);
        if (existing.Any(p => p.Contains(id, StringComparison.OrdinalIgnoreCase))) return;
        var tenantId = tenant.TenantId ?? throw new TenantScopeMissingException();
        var mode = await runtime.GetModeAsync(tenantId, ct);
        await journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), tenantId, ChatButtonJournal.ConfirmBooking, $"appointment:{appointmentId}",
            clock.GetUtcNow(), AiStatus.Done, true, mode, JsonSerializer.Serialize(new { proposalId, via = "button" }), conversationId), ct);
    }
}
