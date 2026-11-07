using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

// TASK-838 (ADR-020, §28): детермінований запис кнопками. Кроки без LLM: цей файл і ChatBookingFlowService не залежать від шару ШІ
// (гарантує архітектурний тест). Усі переходи - подвійний CAS (клавіатура + потік за version) в одній транзакції ходу (CommitSpec.PreWork).
public sealed partial class ConversationTurnProcessor
{
    /// <summary>Програно CAS клавіатури або потоку: транзакція відкочується, переможець уже відповів.</summary>
    private sealed class FlowCasLostException : Exception;

    private ChatFlowOptions? FlowOpts => services.GetService<ChatFlowOptions>();
    private ChatBookingFlowService Flows => services.GetRequiredService<ChatBookingFlowService>();

    /// <summary>§28.1: ChatFlow:Enabled І кнопки доступні (ChatButtons:Enabled, прапор кнопок каналу, адаптер з actions).</summary>
    private bool FlowAvailable(Conversation conv) => FlowOpts is { Enabled: true } && ButtonsAvailable(conv);

    private static FlowContext FlowCtx(Conversation c) => new(c.Id, c.ClientId, c.Channel?.LocationId);

    private void FlowLog(Turn t, FlowState? s, string via, string result) =>
        log.LogInformation("chat_flow conversation={Conversation} flow={Flow} step={Step} version={Version} via={Via} result={Result}",
            t.Conv.Id, s?.Id.ToString() ?? "-", s?.Step ?? "-", s?.Version ?? -1, via, result);

    private Reply FlowReply(FlowResult r, DateTimeOffset now, string suffix = "") =>
        new(FlowTemplates.StepText(r.View!), MessageSenderType.System,
            Keyboard: KeyboardBuilder.FlowStep(r.View!, r.Next!.Id, r.Next.Version, r.Next.ExpiresAt, now), KeySuffix: suffix);

    /// <summary>Спека ходу потоку: у транзакції СПОЧАТКУ CAS клавіатури (якщо це натискання), далі CAS/створення потоку, далі відповіді з новою клавіатурою.</summary>
    private CommitSpec FlowSpec(Turn t, CallbackResolution? res, FlowState? before, FlowResult? r, IReadOnlyList<Reply> replies, bool consume = true)
    {
        var spec = res is null ? new CommitSpec(replies) : CbSpec(t, res, replies.ToArray());
        return spec with
        {
            PreWork = async c =>
            {
                if (consume && res is { Keyboard: { } kb, Replay: false } && !await Keyboards.TryConsumeAsync(kb.Id, res.OptionPosition, t.Batch[0].Id, c))
                    throw new FlowCasLostException();
                if (before is null && r is { IsNew: true })
                    t.ToClear.AddRange(await Keyboards.RevokeActiveAsync(t.Conv.Id, KeyboardKinds.FlowKinds, c));
                if (r is not null && !await Flows.PersistAsync(r, before, c)) throw new FlowCasLostException();
            },
        };
    }

    // ------------------------------------------------------------ старт

    private async Task StartFlowAsync(Turn t, CallbackResolution? res, string origin, CancellationToken ct)
    {
        var r = await Flows.StartAsync(FlowCtx(t.Conv), origin, null, ct);
        var reply = r.Next!.Status != FlowStatuses.Active
            ? new Reply(FlowTemplates.NoServices, MessageSenderType.System, Keyboard: KeyboardBuilder.HumanOnly())
            : FlowReply(r, t.Now);
        FlowLog(t, r.Next, origin, r.Next.Status == FlowStatuses.Active ? "ok" : "no_services");
        await CommitManyAsync(t, FlowSpec(t, res, null, r, [reply], consume: false), ct);
    }

    // ------------------------------------------------------------ натискання f*

    private async Task HandleFlowCallbackAsync(Turn t, CallbackResolution res, CancellationToken ct)
    {
        var conv = t.Conv;
        if (!t.AutoReplies)
        {
            await CommitManyAsync(t, CbSpec(t, res), ct); // suggest: f* -> processed без відповіді
            return;
        }
        var kb = res.Keyboard!;
        var flow = kb.FlowId is { } fid ? await Flows.GetAsync(fid, conv.Id, ct) : null;
        if (flow is null || !flow.IsActive || flow.Version != kb.FlowVersion || res.Option is not { } opt)
        {
            FlowLog(t, flow, "button", "stale_step");
            await CommitManyAsync(t, CbSpec(t, res, await StaleReplyAsync(t, kb.Kind, ct)), ct);
            return;
        }
        if (res.Verb == ButtonVerbs.FlowTime)
        {
            await HandleFlowSlotAsync(t, res, flow, opt, ct);
            return;
        }
        var r = await Flows.ApplyAsync(flow, FlowCtx(conv), new FlowEvent(res.Verb, opt.ServiceId, opt.SpecialistId, opt.LocationId, opt.Date, opt.StartsAt, opt.Nav), ct);
        FlowLog(t, flow, "button", r.Code);
        await CommitFlowResultAsync(t, res, flow, r, ct);
    }

    private async Task CommitFlowResultAsync(Turn t, CallbackResolution res, FlowState flow, FlowResult r, CancellationToken ct)
    {
        switch (r.Code)
        {
            case FlowResultCodes.Invalid:
                await CommitManyAsync(t, CbSpec(t, res, await StaleReplyAsync(t, res.Keyboard?.Kind, ct)), ct);
                return;
            case FlowResultCodes.Cancelled:
                await CommitManyAsync(t, FlowSpec(t, res, flow, r, [new Reply(FlowTemplates.Cancelled, MessageSenderType.System)]), ct);
                return;
            case FlowResultCodes.NoServices:
                await CommitManyAsync(t, FlowSpec(t, res, flow, r, [new Reply(FlowTemplates.NoServices, MessageSenderType.System, Keyboard: KeyboardBuilder.HumanOnly())]), ct);
                return;
            default:
                await CommitManyAsync(t, FlowSpec(t, res, flow, r, [FlowReply(r, t.Now)]), ct);
                return;
        }
    }

    /// <summary>ft: PrepareAsync (з flow_id) -> T-summary + booking_confirm, потік -> confirm. slot_not_offered|invalid_start -> option_unavailable зі свіжим time.</summary>
    private async Task HandleFlowSlotAsync(Turn t, CallbackResolution res, FlowState flow, KeyboardOption opt, CancellationToken ct)
    {
        var conv = t.Conv;
        var ctx = FlowCtx(conv);
        var prep = await services.GetRequiredService<ChatCouponOffers>().PrepareAsync(Booking,
            new ChatPrepareRequest(conv.Id, flow.LocationId!.Value, flow.ServiceId!.Value, flow.SpecialistId!.Value, opt.StartsAt!.Value, FlowId: flow.Id), ct);
        if (prep.IsOk)
        {
            var p = prep.Value!;
            var s = p.Summary;
            var dto = new ChatBookingSummaryDto(s.ServiceName, s.SpecialistName, s.LocationName, s.Address, s.LocalDate, s.LocalTime, s.Timezone,
                s.DurationMinutes, s.PriceOriginal, s.PriceFinal, s.PromotionName, s.CouponOutcome);
            var ttl = Math.Max(1, (int)Math.Round((p.ExpiresAt - t.Now).TotalMinutes));
            var summary = new Reply(ChatSummaryText.Format(dto, ttl, promptOpts.CurrencyLabel, buttons: true), MessageSenderType.System,
                ShowProposalId: p.ProposalId, Keyboard: KeyboardBuilder.BookingConfirm(p.ProposalId), KeySuffix: ":s");
            var confirm = Flows.ToConfirm(flow, p.ProposalId);
            FlowLog(t, flow, "button", "ok");
            var journal1 = new AiActionRecord(Guid.NewGuid(), conv.TenantId, ClientChatToolNames.PrepareBooking, $"proposal:{p.ProposalId}", t.Now,
                AiActionStatus.Done, false, t.Mode, JsonSerializer.Serialize(new { via = "flow", flowId = flow.Id }), conv.Id);
            await CommitManyAsync(t, FlowSpec(t, res, flow, confirm, [summary]) with { Journal = [journal1] }, ct);
            return;
        }
        if (prep.Error!.Code is "slot_not_offered" or "invalid_start")
        {
            var r = await Flows.SlotUnavailableAsync(flow, ctx, opt.StartsAt!.Value, ct);
            FlowLog(t, flow, "button", FlowResultCodes.OptionUnavailable);
            await CommitFlowResultAsync(t, res, flow, r, ct);
            return;
        }
        var again = await Flows.ReshowAsync(flow, ctx, null, ct);
        FlowLog(t, flow, "button", "prepare_failed");
        await CommitManyAsync(t, FlowSpec(t, res, flow, again,
            [new Reply(ChatTemplates.Fallback, MessageSenderType.System, Keyboard: KeyboardBuilder.HumanOnly()), FlowReply(again, t.Now, ":f")]), ct);
    }

    // ------------------------------------------------------------ пропозиція з потоку (by / bn / bo)

    private sealed record FlowProposal(FlowState Flow, ChatProposal Proposal);

    /// <summary>
    /// §28.8: для пропозиції з flow_id потік має бути active, на confirm і вказувати на цю пропозицію. null - пропозиція не з потоку;
    /// другий елемент false - пропозиція з потоку, але потік недійсний (proposal_not_active -> T-action-stale).
    /// </summary>
    private async Task<(FlowProposal? Fp, bool Ok)> FlowProposalGateAsync(Turn t, Guid proposalId, CancellationToken ct)
    {
        var p = await Booking.GetProposalAsync(t.Conv.Id, proposalId, ct);
        if (p?.FlowId is not { } flowId) return (null, true);
        var flow = await Flows.GetActiveAsync(t.Conv.Id, ct);
        if (flow is null || flow.Id != flowId || flow.Step != FlowSteps.Confirm || flow.ProposalId != proposalId) return (null, false);
        return (new FlowProposal(flow, p), true);
    }

    /// <returns>true - відповідь зафіксовано тут; false - іти звичайним шляхом (потік уже завершено/скасовано).</returns>
    private async Task<bool> ApplyFlowProposalOutcomeAsync(Turn t, CallbackResolution res, ButtonActionResult r, FlowProposal fp, CancellationToken ct)
    {
        var ctx = FlowCtx(t.Conv);
        var (flow, p) = (fp.Flow, fp.Proposal);
        switch (r.Code)
        {
            case ButtonResultCodes.Confirmed or ButtonResultCodes.AlreadyConfirmed:
                await Flows.FinishAsync(flow.Id, FlowStatuses.Completed, null, ct);
                FlowLog(t, flow, "button", "completed");
                return false;
            case ButtonResultCodes.Declined:
                await Flows.FinishAsync(flow.Id, FlowStatuses.Cancelled, FlowCancelReasons.Declined, ct);
                FlowLog(t, flow, "button", "declined");
                return false;
            case ButtonResultCodes.OtherTime:
            {
                var back = await Flows.ReturnToTimeAsync(flow, ctx, p.StartsAt, resetDay: false, FlowNotices.OtherTime, ct);
                FlowLog(t, flow, "button", "other_time");
                await CommitManyAsync(t, FlowSpec(t, res, flow, back, [FlowReply(back, t.Now)], consume: false), ct);
                return true;
            }
            case ButtonResultCodes.SlotTaken or ButtonResultCodes.Expired:
            {
                var back = await Flows.ReturnToTimeAsync(flow, ctx, null, resetDay: true, null, ct);
                var text = r.Code == ButtonResultCodes.SlotTaken ? ChatTemplates.SlotTaken : ChatTemplates.Expired;
                FlowLog(t, flow, "button", r.Code);
                await CommitManyAsync(t, FlowSpec(t, res, flow, back, [new Reply(text, MessageSenderType.System), FlowReply(back, t.Now, ":f")], consume: false), ct);
                return true;
            }
            case ButtonResultCodes.PriceChanged:
            {
                var prep = await services.GetRequiredService<ChatCouponOffers>().PrepareAsync(Booking,
                    new ChatPrepareRequest(t.Conv.Id, p.LocationId, p.ServiceId, p.SpecialistId, p.StartsAt, FlowId: flow.Id), ct);
                if (prep.IsOk)
                {
                    var np = prep.Value!;
                    var s = np.Summary;
                    var dto = new ChatBookingSummaryDto(s.ServiceName, s.SpecialistName, s.LocationName, s.Address, s.LocalDate, s.LocalTime, s.Timezone,
                        s.DurationMinutes, s.PriceOriginal, s.PriceFinal, s.PromotionName, s.CouponOutcome);
                    var ttl = Math.Max(1, (int)Math.Round((np.ExpiresAt - t.Now).TotalMinutes));
                    var again = Flows.ToConfirm(flow, np.ProposalId);
                    FlowLog(t, flow, "button", "price_changed");
                    await CommitManyAsync(t, FlowSpec(t, res, flow, again,
                    [
                        new Reply(ChatTemplates.PriceChanged, MessageSenderType.System),
                        new Reply(ChatSummaryText.Format(dto, ttl, promptOpts.CurrencyLabel, buttons: true), MessageSenderType.System,
                            ShowProposalId: np.ProposalId, Keyboard: KeyboardBuilder.BookingConfirm(np.ProposalId), KeySuffix: ":s"),
                    ], consume: false), ct);
                    return true;
                }
                var back = await Flows.ReturnToTimeAsync(flow, ctx, null, resetDay: true, null, ct);
                await CommitManyAsync(t, FlowSpec(t, res, flow, back, [new Reply(ChatTemplates.PriceChanged, MessageSenderType.System), FlowReply(back, t.Now, ":f")], consume: false), ct);
                return true;
            }
            default:
                return false; // limit/failed -> handoff скасує потік; proposal_not_active -> звичайний T-action-stale
        }
    }

    /// <summary>Текстове «Так»/«Ні»/інше на пропозиції з потоку: завершення потоку (completed / declined / повернення на time без повідомлення).</summary>
    private async Task FlowProposalTextEndedAsync(Turn t, Guid proposalId, string outcome, CancellationToken ct)
    {
        if (FlowOpts is not { Enabled: true }) return;
        var flow = await Flows.GetActiveAsync(t.Conv.Id, ct);
        if (flow is null || flow.ProposalId != proposalId) return;
        switch (outcome)
        {
            case "completed": await Flows.FinishAsync(flow.Id, FlowStatuses.Completed, null, ct); break;
            case "declined": await Flows.FinishAsync(flow.Id, FlowStatuses.Cancelled, FlowCancelReasons.Declined, ct); break;
            default: // superseded: потік повертається на time (його повторить ШІ-конвеєр TASK-840 або наступне натискання «Записатися»)
                var back = await Flows.ReturnToTimeAsync(flow, FlowCtx(t.Conv), null, resetDay: false, null, ct);
                await Flows.PersistAsync(back, flow, ct);
                break;
        }
        FlowLog(t, flow, "text", outcome);
    }

    // ------------------------------------------------------------ термін, handoff, sweeper

    /// <summary>T-flow-expired + [📅 Почати знову], не частіше разу на 10 хв (§28.6).</summary>
    private async Task HandleFlowExpiredAsync(Turn t, CallbackResolution res, CancellationToken ct)
    {
        FlowLog(t, null, "button", "flow_expired");
        Reply? reply = t.AutoReplies && !await RecentTemplateAsync(t.Conv.Id, FlowTemplates.Expired, StaleThrottle, t.Now, ct)
            ? new Reply(FlowTemplates.Expired, MessageSenderType.System,
                Keyboard: new KeyboardPlan(null, [], [[new PlannedButton(FlowTemplates.StartAgain, ButtonVerbs.Book)]]))
            : null;
        await CommitManyAsync(t, CbSpec(t, res, reply), ct);
    }

    /// <summary>Вхід у handoff: активний потік -> cancelled (handoff), його клавіатури відкликаються (у транзакції ходу).</summary>
    private async Task CancelFlowOnHandoffAsync(Turn t, List<Guid> clear, CancellationToken ct)
    {
        if (FlowOpts is null) return;
        var flow = await Flows.GetActiveAsync(t.Conv.Id, ct);
        if (flow is not null) await Flows.FinishAsync(flow.Id, FlowStatuses.Cancelled, FlowCancelReasons.Handoff, ct);
        clear.AddRange(await Keyboards.RevokeActiveAsync(t.Conv.Id, KeyboardKinds.FlowKinds, ct));
    }
}
