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
public sealed class ConversationSignals(TimeProvider? clock = null)
{
    public const int Capacity = 1000;
    private const int MaxNames = 5000;
    private const int MaxTransient = 2000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid Conversation, Guid Message), (double Lat, double Lon, DateTimeOffset Expires)> _locations = new();
    private readonly ConcurrentDictionary<Guid, (ChatActionLayout Layout, DateTimeOffset Expires)> _actions = new();

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

    // ------------------------------------------------------------ транзієнтна геолокація (ADR-020, §28.9)

    /// <summary>
    /// Координати клієнта між ingest і ходом розмови: ЛИШЕ пам'ять цього процесу (не БД, не логи, не API, не LLM). Ключ - (розмова, вхідне
    /// повідомлення), життя - <paramref name="ttl"/>; читання (<see cref="TakeLocation"/>) видаляє запис. Рестарт/інший інстанс = координат немає.
    /// </summary>
    public void StoreLocation(Guid conversationId, Guid inboundMessageId, double latitude, double longitude, TimeSpan ttl)
    {
        var now = _clock.GetUtcNow();
        if (_locations.Count >= MaxTransient) PruneLocations(now);
        if (_locations.Count >= MaxTransient) return; // межа пам'яті: координат бракуватиме -> T-location-retry
        _locations[(conversationId, inboundMessageId)] = (latitude, longitude, now + ttl);
    }

    /// <summary>Забирає й видаляє координати; null - немає (не було, TTL минув, уже прочитані, інший процес).</summary>
    public TransientPoint? TakeLocation(Guid conversationId, Guid inboundMessageId)
    {
        if (!_locations.TryRemove((conversationId, inboundMessageId), out var v)) return null;
        return v.Expires > _clock.GetUtcNow() ? new TransientPoint(v.Lat, v.Lon) : null;
    }

    /// <summary>Лише для тестів і діагностики: скільки записів у пам'яті (без значень).</summary>
    public int TransientLocationCount => _locations.Count;

    private void PruneLocations(DateTimeOffset now)
    {
        foreach (var kv in _locations)
            if (kv.Value.Expires <= now) _locations.TryRemove(kv.Key, out _);
    }

    // ------------------------------------------------------------ розкладка лише для inline-відправки (§28.9)

    private static readonly TimeSpan ActionsOverrideTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Розкладка, яку inline-відправка бере замість збереженої в <c>beauty_messages.actions</c> (відстані до закладів). Не зберігається;
    /// одноразова (читання видаляє). Повтор відправки воркером бере збережену версію (без відстаней).
    /// </summary>
    public void SetActionsOverride(Guid messageId, ChatActionLayout layout)
    {
        var now = _clock.GetUtcNow();
        if (_actions.Count >= MaxTransient)
            foreach (var kv in _actions)
                if (kv.Value.Expires <= now) _actions.TryRemove(kv.Key, out _);
        if (_actions.Count >= MaxTransient) return;
        _actions[messageId] = (layout, now + ActionsOverrideTtl);
    }

    public ChatActionLayout? TakeActionsOverride(Guid messageId) =>
        _actions.TryRemove(messageId, out var v) && v.Expires > _clock.GetUtcNow() ? v.Layout : null;
}

/// <summary>Координати клієнта в пам'яті; <see cref="ToString"/> їх не друкує (захист від випадкового логування).</summary>
public readonly record struct TransientPoint(double Latitude, double Longitude)
{
    public override string ToString() => nameof(TransientPoint);
}

/// <summary>
/// <see cref="IChannelQueue"/> без Redis (замінює лише-логуючий <see cref="DeferredChannelQueue"/>): вхідні повідомлення вже в БД,
/// тут публікується сигнал для ConversationWorker. Повтор outbox лишається за воркером/ConversationWorker (повідомлення `pending` у БД).
/// </summary>
public sealed class ChannelSignalQueue(
    ConversationSignals signals, ChannelRequestContext context, ILogger<ChannelSignalQueue> log,
    BeautyCrm.Application.Features.BeautyChatFlow.ChatFlowOptions? flow = null) : IChannelQueue
{
    public Task EnqueueInboundAsync(IReadOnlyList<InboundMessage> messages, CancellationToken ct)
    {
        // Координати - до публікації сигналу, щоб хід їх уже бачив. Лише за ChatFlow:GeoEnabled (інакше не зберігаємо навіть у пам'яті).
        if (flow is { GeoEnabled: true })
            foreach (var m in messages)
                if (m is { Kind: InboundKinds.Location, ConversationId: { } conv, MessageId: { } id, TransientLatitude: { } lat, TransientLongitude: { } lon })
                    signals.StoreLocation(conv, id, lat, lon, TimeSpan.FromSeconds(flow.TransientLocationTtlSeconds));
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
