using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>Сигнал «у розмові є нове вхідне» (ADR-011 §2.2). tenant/канал беруться з довіреного ambient-контексту каналу, не з payload.</summary>
public sealed record ConversationSignal(Guid TenantId, Guid ChannelId, Guid ConversationId, string? SenderName);

/// <summary>
/// In-process канал сигналів + реєстр tenants/каналів, відомих цьому процесу з поточного ingest/polling (для recovery-sweeper
/// без SECURITY DEFINER-функції: ADR-011 §2.9, TASK-724). Сигнал лише прискорює обробку; джерело істини - рядки `received` у БД.
/// Канал обмежений: при переповненні відкидається найстаріший сигнал (його підбере sweeper).
/// </summary>
public sealed class ConversationSignals
{
    public const int Capacity = 1000;
    private const int MaxNames = 5000;

    private readonly Channel<ConversationSignal> _channel = Channel.CreateBounded<ConversationSignal>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _tenants = new();
    private readonly ConcurrentDictionary<Guid, string> _names = new();

    public ChannelReader<ConversationSignal> Reader => _channel.Reader;

    public void Publish(ConversationSignal signal)
    {
        _tenants[signal.TenantId] = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(signal.SenderName))
        {
            if (_names.Count >= MaxNames) _names.Clear(); // груба межа пам'яті: ім'я лише для підписи нового клієнта
            _names[signal.ConversationId] = signal.SenderName!;
        }
        _channel.Writer.TryWrite(signal);
    }

    /// <summary>tenants, чиї канали проходили через ingest цього процесу.</summary>
    public IReadOnlyCollection<Guid> KnownTenants => _tenants.Keys.ToList();

    /// <summary>
    /// Відповідь менеджера (TASK-739): tenant потрапляє в обхід sweeper, щоб `staff-reply:` у стані `pending` було дошлано,
    /// навіть якщо після рестарту від клієнтів цього tenant ще не було вхідних.
    /// </summary>
    public void NoteTenant(Guid tenantId) => _tenants[tenantId] = DateTimeOffset.UtcNow;

    /// <summary>Ім'я з профілю Telegram (лише в пам'яті; у БД його немає). Null після рестарту -> «Клієнт Telegram».</summary>
    public string? SenderName(Guid conversationId) => _names.TryGetValue(conversationId, out var n) ? n : null;
}

/// <summary>
/// <see cref="IChannelQueue"/> без Redis (замінює лише-логуючий <see cref="DeferredChannelQueue"/>): вхідні повідомлення вже в БД,
/// тут публікується сигнал для ConversationWorker. Повтор outbox лишається за воркером/ConversationWorker (повідомлення `pending` у БД).
/// </summary>
public sealed class ChannelSignalQueue(ConversationSignals signals, ChannelRequestContext context, ILogger<ChannelSignalQueue> log) : IChannelQueue
{
    public Task EnqueueInboundAsync(IReadOnlyList<InboundMessage> messages, CancellationToken ct)
    {
        // Без ambient-каналу (виклик поза ingest) tenant невідомий: сигналу немає, повідомлення підбере sweeper.
        if (context.Value is not { } channel)
        {
            log.LogWarning("conversation_signal_skipped reason=no_channel_context count={Count}", messages.Count);
            return Task.CompletedTask;
        }
        foreach (var group in messages.Where(m => m.ConversationId is not null).GroupBy(m => m.ConversationId!.Value))
            signals.Publish(new ConversationSignal(channel.TenantId, channel.ChannelId, group.Key,
                group.Select(m => m.SenderName).LastOrDefault(n => !string.IsNullOrWhiteSpace(n))));
        return Task.CompletedTask;
    }

    public Task ScheduleOutboxRetryAsync(Guid messageId, TimeSpan delay, CancellationToken ct)
    {
        log.LogInformation("Outbox retry for {MessageId} due in {Delay}; message stays 'pending' in DB", messageId, delay);
        return Task.CompletedTask;
    }
}
