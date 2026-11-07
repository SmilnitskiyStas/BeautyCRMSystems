using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// Сховище стану потоку (TASK-838, §28.6, §28.10). Усі зміни - умовний UPDATE (CAS за <c>version</c> і <c>status = 'active'</c>) у поточній
/// транзакції контексту: коміт ходу атомарно тримає разом CAS клавіатури й потоку. Вибір зберігає лише id (без ПД і координат).
/// </summary>
public sealed class EfChatFlowStore(BeautyDbContext db, TimeProvider clock) : IChatFlowStore
{
    public async Task<FlowState?> GetActiveAsync(Guid conversationId, CancellationToken ct) =>
        Map(await db.ChatBookingFlows.AsNoTracking().FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.Status == FlowStatuses.Active, ct));

    public async Task<FlowState?> GetAsync(Guid flowId, Guid conversationId, CancellationToken ct) =>
        Map(await db.ChatBookingFlows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == flowId && x.ConversationId == conversationId, ct));

    public async Task<IReadOnlyList<Guid>> CreateAsync(FlowState state, DateTimeOffset now, CancellationToken ct)
    {
        var superseded = await db.ChatBookingFlows.Where(x => x.ConversationId == state.ConversationId && x.Status == FlowStatuses.Active)
            .Select(x => x.Id).ToListAsync(ct);
        if (superseded.Count > 0)
            await db.ChatBookingFlows.Where(x => superseded.Contains(x.Id) && x.Status == FlowStatuses.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, FlowStatuses.Superseded).SetProperty(x => x.FinishedAt, now)
                    .SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, now), ct);
        var finished = state.Status != FlowStatuses.Active;
        db.ChatBookingFlows.Add(new ChatBookingFlow
        {
            Id = state.Id, ConversationId = state.ConversationId, Status = state.Status, Step = state.Step, Version = state.Version,
            ServiceId = state.ServiceId, SpecialistId = state.SpecialistId, LocationId = state.LocationId,
            SuggestedLastLocationId = state.SuggestedLastLocationId, Day = state.Day, PreferredTime = state.PreferredTime,
            CursorAt = state.CursorAt?.ToUniversalTime(), ListCursor = state.ListCursor, SkippedSteps = state.SkippedSteps.ToArray(),
            ProposalId = state.ProposalId, Origin = state.Origin, CancelReason = state.CancelReason,
            ExpiresAt = state.ExpiresAt.ToUniversalTime(), FinishedAt = finished ? now : null,
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return superseded;
    }

    public async Task<bool> TryAdvanceAsync(FlowState before, FlowState after, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var finished = after.Status != FlowStatuses.Active;
        var skipped = after.SkippedSteps.ToArray();
        var cursor = after.CursorAt?.ToUniversalTime();
        var expires = after.ExpiresAt.ToUniversalTime();
        var rows = await db.ChatBookingFlows
            .Where(x => x.Id == before.Id && x.Version == before.Version && x.Status == FlowStatuses.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, after.Status).SetProperty(x => x.Step, after.Step).SetProperty(x => x.Version, after.Version)
                .SetProperty(x => x.ServiceId, after.ServiceId).SetProperty(x => x.SpecialistId, after.SpecialistId)
                .SetProperty(x => x.LocationId, after.LocationId).SetProperty(x => x.SuggestedLastLocationId, after.SuggestedLastLocationId)
                .SetProperty(x => x.Day, after.Day).SetProperty(x => x.PreferredTime, after.PreferredTime)
                .SetProperty(x => x.CursorAt, cursor).SetProperty(x => x.ListCursor, after.ListCursor)
                .SetProperty(x => x.SkippedSteps, skipped).SetProperty(x => x.ProposalId, after.ProposalId)
                .SetProperty(x => x.CancelReason, after.CancelReason).SetProperty(x => x.ExpiresAt, expires)
                .SetProperty(x => x.FinishedAt, finished ? now : (DateTimeOffset?)null).SetProperty(x => x.UpdatedAt, now), ct);
        return rows == 1;
    }

    public async Task<bool> FinishAsync(Guid flowId, string status, string? cancelReason, DateTimeOffset now, CancellationToken ct) =>
        await db.ChatBookingFlows.Where(x => x.Id == flowId && x.Status == FlowStatuses.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.CancelReason, cancelReason)
                .SetProperty(x => x.FinishedAt, now).SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, now), ct) == 1;

    public async Task<IReadOnlyList<Guid>> ExpireDueAsync(DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await db.ChatBookingFlows.AsNoTracking().Where(x => x.Status == FlowStatuses.Active && x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt).Select(x => x.Id).Take(batch).ToListAsync(ct);
        if (due.Count == 0) return [];
        var won = new List<Guid>();
        foreach (var id in due)
            if (await FinishAsync(id, FlowStatuses.Expired, null, now, ct)) won.Add(id);
        return won;
    }

    public async Task<int> DeleteFinishedBeforeAsync(DateTimeOffset before, int batch, CancellationToken ct)
    {
        var ids = await db.ChatBookingFlows.AsNoTracking().Where(x => x.Status != FlowStatuses.Active && x.FinishedAt < before)
            .OrderBy(x => x.FinishedAt).Select(x => x.Id).Take(batch).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        return await db.ChatBookingFlows.Where(x => ids.Contains(x.Id) && x.Status != FlowStatuses.Active).ExecuteDeleteAsync(ct);
    }

    private static FlowState? Map(ChatBookingFlow? x) => x is null ? null : new FlowState(
        x.Id, x.ConversationId, x.Status, x.Step, x.Version, x.ServiceId, x.SpecialistId, x.LocationId, x.SuggestedLastLocationId,
        x.Day, x.PreferredTime, x.CursorAt, x.ListCursor, x.SkippedSteps, x.ProposalId, x.Origin, x.ExpiresAt, x.CancelReason);
}
