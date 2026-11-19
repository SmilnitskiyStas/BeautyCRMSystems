using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>
/// Доставка підсумку запису (M1). Пропозиція `prepared` лишається такою, поки підсумок не ДОСТАВЛЕНО (повідомлення `sent`):
/// лише тоді вона стає `shown`, і лише тоді «Так» клієнта може її підтвердити. Підсумок, що не дійшов (5xx з повтором, 400/остаточна
/// помилка), не дає «Так» жодної сили: остаточна невдача -> пропозиція `superseded` і нейтральний шаблон клієнту.
/// Зв'язок пропозиції з повідомленням - `summary_message_id`, який виставляється в транзакції відповіді (без нових колонок).
/// Хто б не доставив повідомлення (inline, sweeper .NET або Node-воркер), перехід робиться тут ліниво й ідемпотентно.
/// </summary>
public sealed class ChatSummaryTracker(BeautyDbContext db, TimeProvider clock)
{
    public const string FollowUpKeyPrefix = "ai-reply:sf:";

    /// <summary>prepared -> shown, якщо повідомлення підсумку доставлено (`sent`). true - перехід виконано саме цим викликом.</summary>
    public async Task<bool> PromoteAsync(Guid summaryMessageId, CancellationToken ct) =>
        await db.ChatBookingProposals
            .Where(p => p.SummaryMessageId == summaryMessageId && p.Status == ChatProposalStatus.Prepared
                && db.Messages.Any(m => m.Id == summaryMessageId && m.Status == "sent"))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ChatProposalStatus.Shown), ct) == 1;

    /// <summary>
    /// Після спроби доставки. Доставлено -> `shown`. Остаточно не доставлено (`failed`) -> пропозиція `superseded` і нове вихідне
    /// повідомлення-шаблон (повертається id для відправки; ключ ідемпотентний). Інакше (`pending`/`sending`) - нічого.
    /// </summary>
    /// <returns>id повідомлення-шаблону, яке треба відправити, або null.</returns>
    public async Task<Guid?> OnDispatchedAsync(Guid summaryMessageId, CancellationToken ct)
    {
        if (await PromoteAsync(summaryMessageId, ct)) return null;

        var summary = await db.Messages.AsNoTracking().Where(m => m.Id == summaryMessageId && m.Status == "failed")
            .Select(m => new { m.ConversationId }).FirstOrDefaultAsync(ct);
        if (summary is null) return null;

        var now = clock.GetUtcNow();
        var superseded = await db.ChatBookingProposals
            .Where(p => p.SummaryMessageId == summaryMessageId && p.Status == ChatProposalStatus.Prepared)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ChatProposalStatus.Superseded).SetProperty(p => p.DecidedAt, now), ct);
        if (superseded != 1) return null;
        // Недоставлений підсумок не можна підтвердити й кнопкою (TASK-753): його клавіатура відкликається, розмітку прибере sweeper.
        await db.ChatKeyboards.Where(k => k.MessageId == summaryMessageId && k.Status == "active")
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, "revoked").SetProperty(k => k.UpdatedAt, now), ct);

        var follow = new Message
        {
            Id = Guid.NewGuid(), ConversationId = summary.ConversationId, Direction = MessageDirection.Outbound,
            SenderType = MessageSenderType.System, Body = ChatTemplates.SummaryNotDelivered, Kind = InboundKinds.Text, SentAt = now,
            Status = "sending", IdempotencyKey = FollowUpKeyPrefix + summaryMessageId,
        };
        db.Messages.Add(follow);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear(); // шаблон уже створено паралельним викликом
            return null;
        }
        db.ChangeTracker.Clear();
        return follow.Id;
    }

    /// <summary>Підсумки `prepared`-пропозицій, які вже `sent` або `failed` (доставив Node-воркер/sweeper): для фонового догляду.</summary>
    public Task<List<Guid>> SettledSummariesAsync(int take, CancellationToken ct) =>
        db.ChatBookingProposals.AsNoTracking()
            .Where(p => p.Status == ChatProposalStatus.Prepared && p.SummaryMessageId != null
                && db.Messages.Any(m => m.Id == p.SummaryMessageId && (m.Status == "sent" || m.Status == "failed")))
            .Select(p => p.SummaryMessageId!.Value).Take(take).ToListAsync(ct);
}
