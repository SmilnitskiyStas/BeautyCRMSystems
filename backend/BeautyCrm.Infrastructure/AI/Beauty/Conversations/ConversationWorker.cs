using System.Collections.Concurrent;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>
/// Hosted service конвеєра розмов (ADR-011 §2.2): споживає сигнали ingest, запускає <see cref="ConversationTurnProcessor"/> у новому scope
/// на кожну розмову (tenant ставиться явно з довіреного сигналу), плюс recovery-sweeper і reaper. `ChatAssistant:Enabled=false`
/// (default): сервіс нічого не робить, вхідні лишаються `received`.
/// Sweeper обходить лише tenants, відомі цьому процесу з ingest/polling і каналів зі списку polling: SECURITY DEFINER-функції немає
/// (TASK-724), тож webhook-канали на проді після рестарту відновлюються з наступним повідомленням.
/// </summary>
public sealed class ConversationWorker(
    IServiceScopeFactory scopes, ConversationSignals signals, ChatAssistantOptions opts, TelegramPollingOptions polling,
    TimeProvider clock, ILogger<ConversationWorker> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, bool> _inflight = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!opts.Enabled)
        {
            log.LogInformation("chat_assistant_disabled: ChatAssistant:Enabled=false, inbound messages stay 'received'");
            return;
        }
        var consumers = Enumerable.Range(0, opts.Workers).Select(_ => Task.Run(() => ConsumeAsync(stop), CancellationToken.None));
        await Task.WhenAll(consumers.Append(Task.Run(() => SweepLoopAsync(stop), CancellationToken.None)));
    }

    private async Task ConsumeAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var s in signals.Reader.ReadAllAsync(stop)) await HandleAsync(s.TenantId, s.ConversationId, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>Один хід розмови в окремому scope. Помилки логуються (лише тип), не поглинаються мовчки; безпекові події - Critical.</summary>
    public async Task<TurnRunResult?> HandleAsync(Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        // Одна розмова - один виконавець у процесі; сигнал, що прийшов під час ходу, ставить прапорець і виконується після.
        if (!_inflight.TryAdd(conversationId, false))
        {
            _inflight[conversationId] = true;
            return null;
        }
        TurnRunResult? last = null;
        try
        {
            do
            {
                _inflight[conversationId] = false;
                last = await RunOnceAsync(tenantId, conversationId, ct);
            } while (_inflight.TryGetValue(conversationId, out var again) && again && !ct.IsCancellationRequested);
        }
        finally
        {
            _inflight.TryRemove(conversationId, out _);
        }
        return last;
    }

    private async Task<TurnRunResult?> RunOnceAsync(Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ConversationTurnProcessor>().RunAsync(tenantId, conversationId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            var security = ex is TenantMismatchException or TenantScopeMissingException;
            log.Log(security ? LogLevel.Critical : LogLevel.Error, "chat_turn_failed conversation={ConversationId} error={ErrorType}",
                conversationId, ex.GetType().Name);
            return null;
        }
    }

    // ---------------------------------------------------------------- sweeper + reaper

    private async Task SweepLoopAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(opts.SweepIntervalSeconds), stop);
                await SweepOnceAsync(stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>Один прохід: `received` без lease (старші за SweepMinAge) -> хід; `sending` > 5 хв -> `pending`; `pending` відповіді ШІ -> повтор.</summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        foreach (var tenantId in await KnownTenantsAsync(ct))
        {
            try
            {
                var conversations = await SweepTenantAsync(tenantId, ct);
                foreach (var id in conversations) await HandleAsync(tenantId, id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var security = ex is TenantMismatchException or TenantScopeMissingException;
                log.Log(security ? LogLevel.Critical : LogLevel.Error, "chat_sweep_failed tenant={TenantId} error={ErrorType}", tenantId, ex.GetType().Name);
            }
        }
    }

    private async Task<IReadOnlyCollection<Guid>> KnownTenantsAsync(CancellationToken ct)
    {
        var tenants = new HashSet<Guid>(signals.KnownTenants);
        // Канали polling відомі з конфігу: їхній tenant береться з БД через ChannelDirectory (довірене джерело).
        foreach (var channelId in polling.ChannelIds)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var channel = await scope.ServiceProvider.GetRequiredService<ChannelDirectory>().ResolveAsync(ChannelIds.Telegram, channelId, ct);
                if (channel is not null) tenants.Add(channel.TenantId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("chat_sweep_channel_unresolved channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
            }
        }
        return tenants;
    }

    private async Task<List<Guid>> SweepTenantAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        TenantGuard.EnsureOrSetForTrustedJob(sp.GetRequiredService<TenantContext>(), tenantId);
        var db = sp.GetRequiredService<BeautyDbContext>();
        var now = clock.GetUtcNow();

        // reaper: sending, що завис (падіння між claim і результатом) -> pending
        var stuckBefore = now - opts.SendingStaleAfter;
        await db.Messages.Where(m => m.Direction == MessageDirection.Outbound && m.Status == "sending" && m.UpdatedAt < stuckBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending").SetProperty(m => m.UpdatedAt, now), ct);

        // повтор відповідей ШІ, що лишились pending (за відсутності Node-воркера); claim pending -> sending виключає подвійну відправку
        var retryBefore = now - opts.PendingRetryAfter;
        var due = await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Outbound && m.Status == "pending" && m.IdempotencyKey != null
                && (m.IdempotencyKey.StartsWith("ai-reply:") || m.IdempotencyKey.StartsWith("staff-reply:")) && m.Attempts < OutboxProcessor.MaxAttempts && m.UpdatedAt < retryBefore)
            .OrderBy(m => m.UpdatedAt).Select(m => m.Id).Take(20).ToListAsync(ct);
        foreach (var id in due)
        {
            var claimed = await db.Messages.Where(m => m.Id == id && m.Status == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "sending").SetProperty(m => m.UpdatedAt, now), ct);
            if (claimed != 1) continue;
            try
            {
                await sp.GetRequiredService<OutboundDispatcher>().DispatchAsync(tenantId, id, ct);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
            {
                log.LogWarning("chat_outbox_retry_failed message={MessageId} error={ErrorType}", id, ex.GetType().Name);
                await db.Messages.Where(m => m.Id == id && m.Status == "sending")
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending").SetProperty(m => m.UpdatedAt, clock.GetUtcNow()), CancellationToken.None);
            }
        }

        // M1: підсумки запису, які вже `sent`/`failed` (доставив Node-воркер або повтор вище): prepared -> shown / superseded + шаблон клієнту.
        var tracker = sp.GetRequiredService<ChatSummaryTracker>();
        foreach (var summaryId in await tracker.SettledSummariesAsync(20, ct))
        {
            try
            {
                if (await tracker.OnDispatchedAsync(summaryId, ct) is { } followUp)
                    await sp.GetRequiredService<OutboundDispatcher>().DispatchAsync(tenantId, followUp, ct);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
            {
                log.LogWarning("chat_summary_followup_failed message={MessageId} error={ErrorType}", summaryId, ex.GetType().Name);
            }
        }

        // TASK-753 (§23.4): протермінувати клавіатури й прибрати розмітку (повтор до 3 разів); збій не ламає прохід.
        try
        {
            await sp.GetRequiredService<KeyboardMarkupCleaner>().SweepAsync(50, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
        {
            log.LogWarning("chat_keyboard_sweep_failed tenant={TenantId} error={ErrorType}", tenantId, ex.GetType().Name);
        }

        // TASK-770 (§25.5.7): тимчасові номери контактів (contact_phone) старші за ChatPhone:ContactRetentionHours обнуляються; ідемпотентно, у лог лише кількість.
        try
        {
            await sp.GetRequiredService<ContactPhoneSweeper>().SweepTenantAsync(tenantId, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
        {
            log.LogWarning("chat_contact_phone_sweep_failed tenant={TenantId} error={ErrorType}", tenantId, ex.GetType().Name);
        }

        // Ходи, відкладені через тимчасову причину (M2), повторюються не раніше ніж через RateLimitRetryDelay.
        var cutoff = now - opts.SweepMinAge;
        var deferredCutoff = now - opts.RateLimitRetryDelay;
        return await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Inbound && m.Status == "received" && m.CreatedAt < cutoff
                && (m.LastError != ConversationTurnProcessor.DeferredMarker || m.UpdatedAt < deferredCutoff))
            .Select(m => m.ConversationId).Distinct().Take(100).ToListAsync(ct);
    }
}
