using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// Порт запису з чату над EF (TASK-726, §21.6). Tenant задає RLS; кожен метод із conversationId фільтрує за розмовою,
/// тож пропозиція чужої розмови недосяжна. Статуси змінюються умовним UPDATE (CAS).
/// </summary>
public sealed class EfChatBookingStore(BeautyDbContext db) : IChatBookingStore
{
    private static readonly string[] Active = [ChatProposalStatus.Prepared, ChatProposalStatus.Shown];
    private static readonly string[] Terminal = [ChatProposalStatus.Confirmed, ChatProposalStatus.Rejected, ChatProposalStatus.Expired,
        ChatProposalStatus.Superseded, ChatProposalStatus.Failed];

    public Task<ChatConversationRef?> GetConversationAsync(Guid conversationId, CancellationToken ct) =>
        db.Conversations.AsNoTracking().Where(c => c.Id == conversationId)
            .Select(c => new ChatConversationRef(c.Id, c.ClientId)).FirstOrDefaultAsync(ct);

    public async Task<ChatProposal> CreateProposalAsync(NewChatProposal p, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ChatBookingProposals.Where(x => x.ConversationId == p.ConversationId && Active.Contains(x.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatProposalStatus.Superseded).SetProperty(x => x.DecidedAt, p.CreatedAt), ct);
        db.ChatBookingProposals.Add(new ChatBookingProposal
        {
            Id = p.Id, ConversationId = p.ConversationId, ClientId = p.ClientId, LocationId = p.LocationId, SpecialistId = p.SpecialistId,
            ServiceId = p.ServiceId, StartsAt = p.StartsAt.ToUniversalTime(), DurationMinutes = p.DurationMinutes,
            PriceOriginal = p.PriceOriginal, PriceFinal = p.PriceFinal, PromotionId = p.PromotionId, Status = ChatProposalStatus.Prepared,
            ConfirmationTokenHash = p.TokenHash, ExpiresAt = p.ExpiresAt.ToUniversalTime(), CouponId = p.CouponId, CampaignId = p.CampaignId,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return (await GetProposalAsync(p.ConversationId, p.Id, ct))!;
    }

    public async Task<ChatProposal?> GetProposalAsync(Guid conversationId, Guid proposalId, CancellationToken ct) =>
        Map(await db.ChatBookingProposals.AsNoTracking().FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.Id == proposalId, ct));

    public async Task<ChatProposal?> FindActiveProposalAsync(Guid conversationId, CancellationToken ct) =>
        Map(await db.ChatBookingProposals.AsNoTracking()
            .Where(x => x.ConversationId == conversationId && Active.Contains(x.Status)).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct));

    public async Task<bool> MarkShownAsync(Guid conversationId, Guid proposalId, Guid summaryMessageId, CancellationToken ct) =>
        await db.ChatBookingProposals.Where(x => x.ConversationId == conversationId && x.Id == proposalId && x.Status == ChatProposalStatus.Prepared)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatProposalStatus.Shown).SetProperty(x => x.SummaryMessageId, summaryMessageId), ct) == 1;

    public async Task<bool> TryTransitionAsync(Guid conversationId, Guid proposalId, string from, string to, string? failureCode, DateTimeOffset at, CancellationToken ct)
    {
        var decided = Terminal.Contains(to) ? (DateTimeOffset?)at.ToUniversalTime() : null;
        return await db.ChatBookingProposals.Where(x => x.ConversationId == conversationId && x.Id == proposalId && x.Status == from)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, to).SetProperty(x => x.FailureCode, failureCode).SetProperty(x => x.DecidedAt, decided), ct) == 1;
    }

    public async Task<int> SupersedeActiveAsync(Guid conversationId, DateTimeOffset at, CancellationToken ct)
    {
        var atUtc = at.ToUniversalTime();
        return await db.ChatBookingProposals.Where(x => x.ConversationId == conversationId && Active.Contains(x.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatProposalStatus.Superseded).SetProperty(x => x.DecidedAt, atUtc), ct);
    }

    public async Task<Guid> EnsureClientAsync(Guid conversationId, string fullName, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Блокування рядка розмови серіалізує паралельні виклики: клієнт створюється один раз.
        var existing = (await db.Database.SqlQuery<Guid?>(
            $"""SELECT client_id AS "Value" FROM beauty_conversations WHERE id = {conversationId} FOR UPDATE""").ToListAsync(ct)).FirstOrDefault();
        if (existing is { } id)
        {
            await tx.CommitAsync(ct);
            return id;
        }
        // «Стоп» до першого запису (§22.7): розмова має reminders_stop_at - лінивий клієнт одразу отримує вподобання none/stop і відписку.
        var stopAt = await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId).Select(c => c.RemindersStopAt).FirstOrDefaultAsync(ct);
        var client = new Client { Id = Guid.NewGuid(), FullName = fullName, Phone = null, Email = null, MarketingConsent = false, Unsubscribed = stopAt is not null };
        db.Clients.Add(client);
        if (stopAt is not null)
            db.ClientContactPreferences.Add(new ClientContactPreference
            {
                ClientId = client.Id, Method = "none", OffsetsMinutes = [], Source = "stop", TelegramConversationId = conversationId,
                CreatedAt = stopAt.Value, UpdatedAt = stopAt.Value,
            });
        await db.SaveChangesAsync(ct);
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ClientId, client.Id), ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return client.Id;
    }

    public async Task CompleteAsync(Guid conversationId, Guid proposalId, Guid appointmentId, Guid clientId, DateTimeOffset at, CancellationToken ct)
    {
        var atUtc = at.ToUniversalTime();
        await db.ChatBookingProposals.Where(x => x.ConversationId == conversationId && x.Id == proposalId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppointmentId, appointmentId).SetProperty(x => x.ClientId, clientId)
                .SetProperty(x => x.DecidedAt, atUtc), ct);
    }

    public Task<int> CountActiveByClientAsync(Guid clientId, DateTimeOffset now, CancellationToken ct) =>
        db.Appointments.AsNoTracking().CountAsync(a => a.ClientId == clientId
            && (a.Source == AppointmentSource.Telegram || a.Source == AppointmentSource.Instagram) && a.StartsAt > now
            && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed), ct);

    public Task<int> CountRecentByClientAsync(Guid clientId, DateTimeOffset since, CancellationToken ct) =>
        db.Appointments.AsNoTracking().CountAsync(a => a.ClientId == clientId
            && (a.Source == AppointmentSource.Telegram || a.Source == AppointmentSource.Instagram) && a.CreatedAt > since, ct);

    private static ChatProposal? Map(ChatBookingProposal? x) => x is null ? null : new ChatProposal(
        x.Id, x.ConversationId, x.ClientId, x.LocationId, x.SpecialistId, x.ServiceId, x.StartsAt, x.DurationMinutes, x.PriceOriginal,
        x.PriceFinal, x.PromotionId, x.Status, x.ConfirmationTokenHash, x.SummaryMessageId, x.ExpiresAt, x.AppointmentId, x.FailureCode,
        x.CreatedAt, x.DecidedAt, x.CouponId, x.CampaignId);
}

public static class ChatBookingDataExtensions
{
    /// <summary>Порт запису з чату. Потребує AddBeautyData.</summary>
    public static IServiceCollection AddBeautyChatBookingData(this IServiceCollection services) =>
        services.AddScoped<IChatBookingStore, EfChatBookingStore>();
}
