using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

public enum InboundUnsubscribeKind { None, Stop, MarketingOff }

/// <summary>
/// TASK-790 (H1, security-review-marketing-2026-10): детермінована відписка НА ВХОДІ (webhook і polling), незалежна від ШІ-конвеєра
/// (<c>ChatAssistant:Enabled</c>, <c>assistant.enabled</c>). «Стоп» / <c>/stop</c> / <c>i:stop</c> -> <see cref="MarketingConsentService.StopAsync"/>
/// (none/stop + unsubscribed); «не надсилати акції» / «без акцій» / <c>i:mkoff</c> -> рівень none (source=chat). Викликається в тій самій
/// транзакції, що й збереження вхідного. Клієнта ще немає: «Стоп» ставить <c>conversations.reminders_stop_at</c> (лінивий клієнт отримає none/stop, L3).
/// Відповідь клієнту тут не надсилається: її дає конвеєр (шаблон), коли він увімкнений; інакше повідомлення позначається processed.
/// </summary>
public sealed class InboundUnsubscribeHandler(BeautyDbContext db, MarketingConsentService consents, TimeProvider clock, ILogger<InboundUnsubscribeHandler> log)
{
    public static InboundUnsubscribeKind Classify(InboundMessage m, bool promptActive)
    {
        switch (m.Kind)
        {
            case InboundKinds.Text:
                if (ReminderCommands.IsStop(m.Text, promptActive)) return InboundUnsubscribeKind.Stop;
                return ChatMarketingParser.IsOffText(m.Text) ? InboundUnsubscribeKind.MarketingOff : InboundUnsubscribeKind.None;
            case InboundKinds.Command:
                return ChatCommands.Parse(m.Text) == ChatCommand.Stop ? InboundUnsubscribeKind.Stop : InboundUnsubscribeKind.None;
            case InboundKinds.Callback:
                return m.CallbackData switch
                {
                    ButtonVerbs.Stop => InboundUnsubscribeKind.Stop,
                    ButtonVerbs.MarketingOff => InboundUnsubscribeKind.MarketingOff,
                    _ => InboundUnsubscribeKind.None,
                };
            default:
                return InboundUnsubscribeKind.None;
        }
    }

    /// <summary>Застосовує відписку для розмови (рядок розмови вже збережено). Ідемпотентно; помилка сервісу згоди лише логується.</summary>
    public async Task ApplyAsync(Guid conversationId, Guid? clientId, InboundMessage m, InboundUnsubscribeKind kind, CancellationToken ct)
    {
        var via = m.Kind switch { InboundKinds.Callback => MarketingConsentVias.Button, InboundKinds.Command => MarketingConsentVias.Command, _ => MarketingConsentVias.Text };
        if (clientId is not { } cid)
        {
            if (kind == InboundUnsubscribeKind.Stop)
                await db.Conversations.Where(c => c.Id == conversationId && c.RemindersStopAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.RemindersStopAt, (DateTimeOffset?)clock.GetUtcNow()), ct);
            return;
        }
        if (kind == InboundUnsubscribeKind.Stop)
        {
            var r = await consents.StopAsync(cid, via, conversationId, ct);
            if (!r.IsOk) log.LogWarning("inbound_stop_failed conversation={ConversationId} code={Code}", conversationId, r.Error!.Code);
            return;
        }
        // i:mkoff під повідомленням кампанії: атрибуція кампанії (як у конвеєрі), лише для одержувача цієї розмови й клієнта
        Guid? campaignId = null;
        if (m.Kind == InboundKinds.Callback && !string.IsNullOrEmpty(m.CallbackMessageRef))
            campaignId = await (from kb in db.ChatKeyboards.AsNoTracking()
                                join msg in db.Messages.AsNoTracking() on kb.MessageId equals msg.Id
                                join rec in db.CampaignRecipients.AsNoTracking() on kb.CampaignRecipientId equals rec.Id
                                where kb.ConversationId == conversationId && msg.ConversationId == conversationId && msg.ExternalMessageId == m.CallbackMessageRef
                                    && rec.ConversationId == conversationId && rec.ClientId == cid
                                select (Guid?)rec.CampaignId).FirstOrDefaultAsync(ct);
        var set = await consents.SetConsentAsync(new SetConsentCommand(cid, MarketingConsentChannels.Telegram, MarketingConsentLevels.None,
            MarketingConsentSources.Chat, via, CampaignId: campaignId, ConversationId: conversationId), ct);
        if (!set.IsOk) log.LogWarning("inbound_marketing_off_failed conversation={ConversationId} code={Code}", conversationId, set.Error!.Code);
    }
}
