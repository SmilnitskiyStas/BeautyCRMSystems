using System.Text.Json;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

// TASK-782 (§26.9, ADR-015): згода на маркетинг і купон у чаті. Усе в коді, без LLM; під Marketing:Enabled (за замовч. false).
// Винятки, що діють завжди (§26.2): відписка (i:mkoff, текст «не надсилати акції», «Стоп»). Код купона ніколи не потрапляє в логи, журнал,
// історію LLM і тіло збереженого повідомлення (замінюється на «[код купона]» у транзакції відповіді).
public sealed partial class ConversationTurnProcessor
{
    private MarketingConsentService Consents => services.GetRequiredService<MarketingConsentService>();
    private CouponService Coupons => services.GetRequiredService<CouponService>();
    private ICouponStore CouponStore => services.GetRequiredService<ICouponStore>();
    private ChatCouponOffers Offers => services.GetRequiredService<ChatCouponOffers>();

    /// <summary><c>Marketing:Enabled</c>: пропозиція згоди, меню, кнопка <c>co</c>, коди текстом.</summary>
    private bool MarketingOn => ButtonsOpts.MarketingEnabled;

    /// <summary>T-stop за §22.7 (Зміна TASK-782): з маркетингом «Стоп» вимикає й акції.</summary>
    private string StopText => MarketingOn ? ChatTemplates.StopMarketing : ChatTemplates.Stop;

    private AiActionRecord MarketingJournal(Turn t, string tool, object payload) =>
        new(Guid.NewGuid(), t.Conv.TenantId, tool, $"conversation:{t.Conv.Id}", t.Now, AiActionStatus.Done, false, t.Mode,
            JsonSerializer.Serialize(payload), t.Conv.Id);

    private static CommitSpec WithTxWork(CommitSpec spec, Func<CancellationToken, Task> next)
    {
        var prev = spec.TxWork;
        return spec with { TxWork = async c => { if (prev is not null) await prev(c); await next(c); } };
    }

    // ---------------------------------------------------------------- меню

    /// <summary>T-marketing-menu (поточний рівень) + клавіатура <c>marketing_consent</c>; клієнта немає - T-marketing-no-client.</summary>
    private async Task<Reply> MarketingMenuReplyAsync(Turn t, CancellationToken ct)
    {
        if (t.Conv.ClientId is not { } clientId) return new Reply(ChatTemplates.MarketingNoClient, MessageSenderType.System);
        var buttons = ButtonsAvailable(t.Conv);
        var level = await db.MarketingConsents.AsNoTracking()
            .Where(m => m.ClientId == clientId && m.Channel == MarketingConsentChannels.Telegram).Select(m => m.Level).FirstOrDefaultAsync(ct);
        return new Reply(ChatTemplates.MarketingMenu(level ?? MarketingConsentLevels.None, level is not null, buttons), MessageSenderType.System,
            Keyboard: buttons ? KeyboardBuilder.MarketingConsent(opts.MarketingConsentTextVersion) : null);
    }

    /// <summary>Крок 4c: текстові аліаси меню («акції», «розсилки», «новини й акції» - лише за Marketing:Enabled) і відписка («не надсилати акції», «без акцій» - завжди).</summary>
    private async Task<bool> HandleMarketingTextAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        if (t.Batch.Count != 1 || texts.Count != 1) return false;
        var body = texts[0].Body;
        if (ChatMarketingParser.IsOffText(body))
        {
            await CommitMarketingOffTextAsync(t, ct);
            return true;
        }
        if (!MarketingOn || !t.AutoReplies || !ChatMarketingParser.IsMenuText(body)) return false;
        if (!await PassTemplateGateAsync(t, ct)) return true;
        await CommitAsync(t, await MarketingMenuReplyAsync(t, ct), null, null, ct);
        return true;
    }

    /// <summary>Пауза handoff: дозволені <c>/marketing</c> (меню) і відписка текстом (§26.9.1-§26.9.2); решта лишається held.</summary>
    private async Task<bool> TryMarketingInPauseAsync(Turn paused, Message m, CancellationToken ct)
    {
        if (m.Kind == InboundKinds.Command && ChatCommands.Parse(m.Body) == ChatCommand.Marketing && MarketingOn)
        {
            if (!await PassTemplateGateAsync(paused, ct)) return true;
            await CommitAsync(paused, await MarketingMenuReplyAsync(paused, ct), null, null, ct);
            return true;
        }
        if (m.Kind == InboundKinds.Text && ChatMarketingParser.IsOffText(m.Body))
        {
            await CommitMarketingOffTextAsync(paused, ct);
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- відписка

    /// <summary>Ефект відписки діє завжди; відповідь - у межах ліміту шаблонів (як «Стоп»), також у suggest (дія над власними даними).</summary>
    private async Task CommitMarketingOffTextAsync(Turn t, CancellationToken ct)
    {
        Reply? reply = null;
        if (!limiter.IsTemplateLimited(t.Conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute))
        {
            limiter.RecordTemplate(t.Conv.Id);
            reply = new Reply(ChatTemplates.MarketingOff, MessageSenderType.System);
        }
        await CommitManyAsync(t, new CommitSpec(reply is null ? [] : [reply]) { TxWork = c => ApplyMarketingOffAsync(t, MarketingConsentVias.Text, null, c) }, ct);
    }

    /// <summary>Рівень none лише для маркетингу (source=chat); кампанія - з одержувача, чиє повідомлення натиснуто. Нагадування не змінюються.</summary>
    private async Task ApplyMarketingOffAsync(Turn t, string via, Guid? recipientId, CancellationToken ct)
    {
        if (t.Conv.ClientId is not { } clientId) return; // клієнта немає - розсилок і так немає
        Guid? campaignId = recipientId is { } rid
            ? await db.CampaignRecipients.AsNoTracking().Where(x => x.Id == rid && x.ConversationId == t.Conv.Id && x.ClientId == clientId)
                .Select(x => (Guid?)x.CampaignId).FirstOrDefaultAsync(ct)
            : null;
        await SetChatConsentAsync(t, clientId, MarketingConsentLevels.None, null, via, campaignId, ct);
    }

    private async Task SetChatConsentAsync(Turn t, Guid clientId, string level, string? textVersion, string via, Guid? campaignId, CancellationToken ct)
    {
        var r = await Consents.SetConsentAsync(new SetConsentCommand(clientId, MarketingConsentChannels.Telegram, level, MarketingConsentSources.Chat, via,
            textVersion, CampaignId: campaignId, ConversationId: t.Conv.Id), ct);
        if (!r.IsOk) throw new InvalidOperationException($"marketing consent was not saved: {r.Error!.Code}");
        await journal.AppendAsync(MarketingJournal(t, ChatJournal.MarketingConsent, new { level, via, textVersion, changed = r.Value!.Changed, campaignId }), ct);
    }

    /// <summary><c>mc</c>: клавіатуру вже спожито; запис згоди й відповідь - в одній транзакції (рівень і версія тексту з options у БД).</summary>
    private async Task ApplyConsentChoiceAsync(Turn t, CallbackResolution res, ButtonActionResult r, CancellationToken ct)
    {
        if (t.Conv.ClientId is not { } clientId)
        {
            await CommitManyAsync(t, CbSpec(t, res, new Reply(ChatTemplates.MarketingNoClient, MessageSenderType.System)), ct);
            return;
        }
        var level = r.ConsentLevel!;
        var version = r.ConsentTextVersion;
        await CommitManyAsync(t, CbSpec(t, res, new Reply(ChatTemplates.MarketingSet(level), MessageSenderType.System)) with
        {
            TxWork = c => SetChatConsentAsync(t, clientId, level, version, MarketingConsentVias.Button, null, c),
        }, ct);
    }

    // ---------------------------------------------------------------- пропозиція згоди після запису

    /// <summary>
    /// §26.9.2: пропозиція згоди ОДИН раз (кулдаун <c>Marketing:OfferCooldownDays</c>) після T-confirmed (вибір нагадування вже є) або T-reminder-set.
    /// Умови: Marketing:Enabled, кнопки доступні, є клієнт, рядка згоди telegram немає, режим confirm|auto, не handoff. Null - не пропонуємо.
    /// </summary>
    private async Task<(Reply Reply, AiActionRecord Journal, Guid ClientId)?> MarketingOfferAsync(Turn t, Guid? clientId, CancellationToken ct)
    {
        if (clientId is not { } cid || !MarketingOn || !t.AutoReplies || t.Conv.AiState == "handoff" || !ButtonsAvailable(t.Conv)) return null;
        var client = await db.Clients.AsNoTracking().Where(c => c.Id == cid && c.DeletedAt == null).Select(c => new { c.MarketingOfferedAt }).FirstOrDefaultAsync(ct);
        if (client is null) return null;
        if (client.MarketingOfferedAt is { } at && at > t.Now.AddDays(-opts.MarketingOfferCooldownDays)) return null;
        if (await db.MarketingConsents.AsNoTracking().AnyAsync(m => m.ClientId == cid && m.Channel == MarketingConsentChannels.Telegram, ct)) return null;
        var version = opts.MarketingConsentTextVersion;
        return (new Reply(ChatTemplates.MarketingOffer, MessageSenderType.System, Keyboard: KeyboardBuilder.MarketingConsent(version), KeySuffix: ":m"),
            MarketingJournal(t, ChatJournal.MarketingOffer, new { textVersion = version }), cid);
    }

    /// <summary>Додає окремим повідомленням (ключ <c>…:m</c>) пропозицію згоди; <c>marketing_offered_at</c> ставиться в тій самій транзакції.</summary>
    private async Task<CommitSpec> WithMarketingOfferAsync(Turn t, CommitSpec spec, Guid? clientId, CancellationToken ct)
    {
        if (await MarketingOfferAsync(t, clientId, ct) is not { } offer) return spec;
        var withReply = spec with { Replies = [.. spec.Replies, offer.Reply], Journal = (spec.Journal ?? []).Append(offer.Journal).ToList() };
        return WithTxWork(withReply, async c => { await Consents.MarkOfferedAsync(offer.ClientId, c); });
    }

    /// <summary>
    /// T-confirmed (+ T-reminder-offer) + пропозиція згоди (лише коли вибір нагадування не потрібен) + зняття пропозиції розмови після погашеного
    /// купона (§26.9.3: після успіху <c>offer_* = NULL</c>).
    /// </summary>
    private async Task<CommitSpec> ConfirmedSpecAsync(
        Turn t, CommitSpec baseSpec, AppointmentDto appt, string body, PromptState? prompt, KeyboardPlan? keyboard, CancellationToken ct)
    {
        var spec = baseSpec with { Replies = [.. baseSpec.Replies, new Reply(body, MessageSenderType.System, Keyboard: keyboard)], Prompt = prompt };
        if (prompt is null) spec = await WithMarketingOfferAsync(t, spec, appt.ClientId, ct);
        if (appt.Coupon is not null) spec = WithTxWork(spec, c => Offers.ClearAsync(t.Conv.Id, c));
        return spec;
    }

    // ---------------------------------------------------------------- купон: пропозиція розмови

    private async Task SupersedeActiveProposalAsync(Turn t, CancellationToken ct)
    {
        if (await Booking.FindActiveAsync(t.Conv.Id, ct) is not { } active) return;
        await Booking.SupersedeAsync(t.Conv.Id, ct); // ціна підсумку застаріла: нове «Так» - лише на новий підсумок
        await RevokeProposalKeyboardsAsync(t, active.Id, ct);
    }

    /// <summary>
    /// <c>co</c> (§26.9.3): одержувач кампанії цієї розмови й клієнта (транспорт уже перевірив, тут повторно); купон - персональний
    /// (<c>recipient.coupon_id</c>) або спільний кампанії, перевіряється <see cref="CouponService.ResolveAsync"/>. Успіх - пропозиція розмови
    /// (<c>offer_*</c>), <c>clicked_at</c>, журнал <c>campaign_click</c> (раз); повторне натискання ідемпотентне. У suggest - без відповіді.
    /// </summary>
    private async Task ApplyCampaignOfferAsync(Turn t, CallbackResolution res, ButtonActionResult r, CancellationToken ct)
    {
        var conv = t.Conv;
        var rec = r.CampaignRecipientId is { } rid && conv.ClientId is { } cid
            ? await db.CampaignRecipients.AsNoTracking().Where(x => x.Id == rid && x.ConversationId == conv.Id && x.ClientId == cid)
                .Select(x => new { x.Id, x.CampaignId, x.CouponId, SharedCouponId = x.Campaign!.SharedCouponId }).FirstOrDefaultAsync(ct)
            : null;
        if (rec is null)
        {
            await CommitManyAsync(t, CbSpec(t, res, await StaleReplyAsync(t, res.Keyboard?.Kind, ct)), ct);
            return;
        }

        CouponCandidate? candidate = null;
        if ((rec.CouponId ?? rec.SharedCouponId) is { } couponId)
        {
            var resolved = await Coupons.ResolveAsync(new CouponUse(CouponScope.Chat, CouponId: couponId, CampaignId: rec.CampaignId), conv.ClientId, t.Now, ct);
            if (!resolved.IsOk)
            {
                Reply? unavailable = t.AutoReplies && !limiter.IsTemplateLimited(conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute)
                    ? new Reply(ChatTemplates.CouponUnavailable, MessageSenderType.System, Keyboard: ButtonsAvailable(conv) ? KeyboardBuilder.BookOnly() : null) : null;
                if (unavailable is not null) limiter.RecordTemplate(conv.Id);
                await CommitManyAsync(t, CbSpec(t, res, unavailable), ct);
                return;
            }
            candidate = resolved.Value;
        }

        var current = await Offers.GetActiveAsync(conv.Id, ct);
        var same = current is not null && current.CampaignId == rec.CampaignId && current.CouponId == candidate?.CouponId;
        if (!same) await SupersedeActiveProposalAsync(t, ct);
        var until = Offers.HoldUntil(t.Now, candidate?.Rule.EndsAt);
        string body;
        if (candidate is null) body = ChatTemplates.BookStart;
        else
        {
            var view = await Offers.DescribeAsync(candidate, conv.Channel?.LocationId, ct);
            body = ChatTemplates.CampaignBookStart(view.Discount, view.Services, view.Until);
        }

        Reply? reply = null;
        if (t.AutoReplies && !await RecentTemplateAsync(conv.Id, body, opts.IdenticalTemplateThrottle, t.Now, ct)
            && !limiter.IsTemplateLimited(conv.Id, opts.MaxTemplateRepliesPerConversationPerMinute))
        {
            limiter.RecordTemplate(conv.Id);
            reply = new Reply(body, MessageSenderType.System);
        }
        var recipientId = rec.Id;
        var campaignId = rec.CampaignId;
        var couponToSet = candidate?.CouponId;
        await CommitManyAsync(t, CbSpec(t, res, reply) with
        {
            TxWork = async c =>
            {
                await Offers.SetAsync(conv.Id, campaignId, couponToSet, until, c);
                var firstClick = await db.CampaignRecipients.Where(x => x.Id == recipientId && x.ClickedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClickedAt, (DateTimeOffset?)t.Now), c) > 0;
                if (firstClick) await journal.AppendAsync(MarketingJournal(t, ChatJournal.CampaignClick, new { campaignId, recipientId }), c);
                if (!same) await journal.AppendAsync(MarketingJournal(t, ChatJournal.CouponOffer, new { via = "button", campaignId }), c);
            },
        }, ct);
    }

    // ---------------------------------------------------------------- купон: код текстом (крок 4b)

    /// <summary>Кандидат у коди: вигляд коду (префікс / персональний формат) або збіг хешу загального коду. Без звернення до БД, якщо вигляд очевидний.</summary>
    private async Task<ChatMarketingParser.CodeShape?> CodeCandidateAsync(string? body, CancellationToken ct)
    {
        if (ChatMarketingParser.ParseCode(body) is not { } shape) return null;
        if (shape.LooksLikeCode) return shape;
        return await CouponStore.FindByHashAsync(CouponCodes.Hash(shape.Code), ct) is not null ? shape : null;
    }

    /// <summary>Повідомлення з кодом обробляється окремим ходом: код не має потрапити в пакет, який піде до моделі.</summary>
    private async Task<List<Message>> NarrowForCouponAsync(List<Message> batch, CancellationToken ct)
    {
        if (!MarketingOn || batch.Count <= 1) return batch;
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch[i].Kind != InboundKinds.Text || await CodeCandidateAsync(batch[i].Body, ct) is null) continue;
            return i == 0 ? [batch[0]] : batch.Take(i).ToList();
        }
        return batch;
    }

    /// <summary>
    /// Крок 4b (§26.9.3), до моделі. Успіх - пропозиція розмови + T-coupon-applied; будь-яка невдача - єдина T-coupon-invalid; понад ліміт спроб -
    /// T-coupon-locked раз на годину. Тіло вхідного замінюється на «[код купона]» у тій самій транзакції. У suggest - ефекти без відповіді.
    /// </summary>
    /// <returns>true - хід завершено тут; false - це не код (текст іде далі).</returns>
    private async Task<bool> HandleCouponCodeAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        if (!MarketingOn || t.Batch.Count != 1 || texts.Count != 1) return false;
        if (await CodeCandidateAsync(texts[0].Body, ct) is not { } shape) return false;

        var conv = t.Conv;
        var resolved = await Coupons.ResolveAsync(new CouponUse(CouponScope.Chat, Code: shape.Code, RateKey: conv.Id.ToString()), conv.ClientId, t.Now, ct);
        string? body;
        Func<CancellationToken, Task>? txWork = null;
        if (resolved.IsOk)
        {
            var candidate = resolved.Value!;
            await SupersedeActiveProposalAsync(t, ct);
            var until = Offers.HoldUntil(t.Now, candidate.Rule.EndsAt);
            var view = await Offers.DescribeAsync(candidate, conv.Channel?.LocationId, ct);
            body = ChatTemplates.CouponApplied(view.Discount, view.Services, view.Until);
            var campaignId = candidate.CampaignId;
            txWork = async c =>
            {
                await Offers.SetAsync(conv.Id, campaignId, candidate.CouponId, until, c);
                await journal.AppendAsync(MarketingJournal(t, ChatJournal.CouponOffer, new { via = "text", campaignId }), c);
            };
        }
        else if (resolved.Error!.Code == CouponService.Locked)
            body = await RecentTemplateAsync(conv.Id, ChatTemplates.CouponLocked, TimeSpan.FromHours(1), t.Now, ct) ? null : ChatTemplates.CouponLocked;
        else
            body = ChatTemplates.CouponInvalid;

        var replies = new List<Reply>();
        if (body is not null && t.AutoReplies)
            switch (await TemplateGateAsync(t, true, ct))
            {
                case TemplateGate.Allow: replies.Add(new Reply(body, MessageSenderType.System)); break;
                case TemplateGate.Notice: replies.Add(new Reply(ChatTemplates.RateLimited, MessageSenderType.System)); break;
            }
        await CommitManyAsync(t, new CommitSpec(replies) { RedactInboundBody = ChatMarketingParser.CodeBodyRedacted, TxWork = txWork }, ct);
        return true;
    }
}
