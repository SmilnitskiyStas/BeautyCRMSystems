using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>beauty_channels.type ("facebook") -> id адаптера ("messenger").</summary>
public static class ChannelTypeMap
{
    public static string ToAdapterId(string type) =>
        string.Equals(type, "facebook", StringComparison.OrdinalIgnoreCase) ? ChannelIds.Messenger : type.ToLowerInvariant();
}

/// <summary>Канал, визначений за beauty_channels.id (tenant береться з каналу, не з запиту).</summary>
public sealed record ResolvedChannel(Guid TenantId, Guid ChannelId, string Type, ChannelCredentials Credentials);

/// <summary>
/// Ambient (AsyncLocal) контекст поточного каналу: адаптери — singleton і запитують секрети через
/// IChannelCredentialsProvider.Get(channelType), тож секрети поточного запиту передаються так, а не через DI scope.
/// </summary>
public sealed class ChannelRequestContext
{
    private static readonly AsyncLocal<ResolvedChannel?> Current = new();
    public ResolvedChannel? Value => Current.Value;

    public IDisposable Begin(ResolvedChannel channel)
    {
        var previous = Current.Value;
        Current.Value = channel;
        return new Scope(previous);
    }

    private sealed class Scope(ResolvedChannel? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public sealed class AmbientChannelCredentialsProvider(ChannelRequestContext context) : IChannelCredentialsProvider
{
    public ChannelCredentials? Get(string channel) =>
        context.Value is { } c && string.Equals(c.Type, channel, StringComparison.OrdinalIgnoreCase) ? c.Credentials : null;
}

/// <summary>
/// Визначає tenant вхідного webhook за id каналу. Окреме з'єднання + транзакційний app.channel_id,
/// який відкриває лише політику channel_webhook_lookup (SELECT одного рядка beauty_channels); решта таблиць
/// лишається під RLS. Секрети розшифровуються тут.
/// </summary>
public sealed class ChannelDirectory(IConfiguration config, ISecretProtector secrets)
{
    public async Task<ResolvedChannel?> ResolveAsync(string channelType, Guid channelId, CancellationToken ct)
    {
        var cs = config.GetConnectionString(DataServiceExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var set = new NpgsqlCommand("SELECT set_config('app.channel_id', @id, true)", conn, tx))
        {
            set.Parameters.AddWithValue("id", channelId.ToString());
            await set.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT tenant_id, type, credentials_encrypted, is_active FROM beauty_channels WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", channelId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var tenantId = reader.GetGuid(0);
        var type = ChannelTypeMap.ToAdapterId(reader.GetString(1));
        var encrypted = reader.IsDBNull(2) ? null : reader.GetString(2);
        var active = reader.GetBoolean(3);
        if (!active || !string.Equals(type, ChannelTypeMap.ToAdapterId(channelType), StringComparison.OrdinalIgnoreCase)) return null;

        var (token, secret) = ChannelSecrets.Read(secrets, encrypted);
        return new ResolvedChannel(tenantId, channelId, type, new ChannelCredentials(token, secret));
    }
}

public sealed record WebhookIngestResult(WebhookOutcome Outcome, int NewMessages = 0, bool ChannelNotFound = false, string? Challenge = null);

/// <summary>Повний вхідний потік: channelId -> tenant (SetTenant) -> секрети -> перевірка підпису -> збереження -> черга.</summary>
public sealed class WebhookIngestService(
    ChannelDirectory directory, TenantContext tenant, ChannelRequestContext context, InboundWebhookService inbound, ChannelRegistry registry)
{
    public async Task<WebhookIngestResult> HandleAsync(string channelType, Guid channelId, WebhookRequest req, string body, CancellationToken ct)
    {
        var channel = await directory.ResolveAsync(channelType, channelId, ct);
        if (channel is null) return new(WebhookOutcome.UnknownChannel, ChannelNotFound: true);

        tenant.SetTenant(channel.TenantId); // до першого запиту до БД через EF у цьому scope
        using var _ = context.Begin(channel);
        var result = await inbound.HandleAsync(channel.Type, req, body, ct);
        return new(result.Outcome, result.NewMessages);
    }

    /// <summary>Meta GET handshake (hub.challenge); null = не пройдено/не підтримується.</summary>
    public async Task<string?> VerifyChallengeAsync(string channelType, Guid channelId, WebhookRequest req, CancellationToken ct)
    {
        var channel = await directory.ResolveAsync(channelType, channelId, ct);
        if (channel is null) return null;
        using var _ = context.Begin(channel);
        return registry.Find(channel.Type) is InstagramAdapter ig ? ig.VerifyChallenge(req) : null;
    }
}

/// <summary>Надсилання вихідного повідомлення (outbox): tenant + секрети каналу -> OutboxProcessor.</summary>
public sealed class OutboundDispatcher(
    BeautyDbContext db, TenantContext tenant, ChannelRequestContext context, ISecretProtector secrets, OutboxProcessor processor)
{
    public async Task<OutboxResult> DispatchAsync(Guid tenantId, Guid messageId, CancellationToken ct)
    {
        tenant.SetTenant(tenantId);
        var ch = await db.Messages.AsNoTracking().Where(m => m.Id == messageId)
            .Select(m => m.Conversation!.Channel!).FirstOrDefaultAsync(ct);
        if (ch is null) return OutboxResult.NotFound;
        var (token, secret) = ChannelSecrets.Read(secrets, ch.CredentialsEncrypted);
        using var _ = context.Begin(new ResolvedChannel(tenantId, ch.Id, ChannelTypeMap.ToAdapterId(EnumText<ChannelType>.ToDb(ch.Type)), new ChannelCredentials(token, secret)));
        return await processor.ProcessAsync(messageId, ct);
    }
}

/// <summary>Реалізація IChannelMessageRepository над beauty_conversations / beauty_messages.</summary>
public sealed class EfChannelMessageRepository(BeautyDbContext db, ChannelRequestContext context) : IChannelMessageRepository
{
    private const string UniqueViolation = "23505";

    public async Task<IReadOnlyList<InboundMessage>> SaveInboundAsync(IReadOnlyList<InboundMessage> messages, CancellationToken ct)
    {
        var channel = context.Value ?? throw new InvalidOperationException("No channel context for inbound messages.");
        var fresh = new List<InboundMessage>();
        foreach (var m in messages)
        {
            if (await db.Messages.AnyAsync(x => x.IdempotencyKey == m.IdempotencyKey, ct)) continue;

            var conv = await db.Conversations.FirstOrDefaultAsync(c => c.ChannelId == channel.ChannelId && c.ExternalChatId == m.ExternalConversationId, ct);
            if (conv is null)
                db.Conversations.Add(conv = new Conversation { Id = Guid.NewGuid(), ChannelId = channel.ChannelId, ExternalChatId = m.ExternalConversationId });
            conv.LastMessageAt = m.ReceivedAt;
            db.Messages.Add(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client,
                Body = m.Text, ExternalMessageId = m.ExternalMessageId, SentAt = m.ReceivedAt, Status = "received",
                IdempotencyKey = m.IdempotencyKey,
            });
            try
            {
                await db.SaveChangesAsync(ct);
                fresh.Add(m);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear(); // конкурентна повторна доставка того ж webhook
            }
        }
        return fresh;
    }

    public async Task<OutboxItem?> GetOutboxItemAsync(Guid messageId, CancellationToken ct)
    {
        var row = await db.Messages.AsNoTracking()
            .Where(m => m.Id == messageId && m.Direction == MessageDirection.Outbound)
            .Select(m => new
            {
                m.Body, m.Status, m.Attempts, m.ConversationId, Type = m.Conversation!.Channel!.Type, m.Conversation.ExternalChatId,
                LastInbound = db.Messages.Where(x => x.ConversationId == m.ConversationId && x.Direction == MessageDirection.Inbound)
                    .Max(x => (DateTimeOffset?)x.SentAt),
            }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var status = row.Status switch { "pending" => OutboxStatus.Pending, "failed" => OutboxStatus.Failed, _ => OutboxStatus.Sent };
        return new OutboxItem(
            new OutboundMessage(messageId, ChannelTypeMap.ToAdapterId(EnumText<ChannelType>.ToDb(row.Type)), row.ExternalChatId, row.Body, row.LastInbound),
            status, row.Attempts);
    }

    public async Task MarkSentAsync(Guid messageId, CancellationToken ct)
    {
        var m = await db.Messages.FirstAsync(x => x.Id == messageId, ct);
        m.Status = "sent"; m.LastError = null; m.SentAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAttemptFailedAsync(Guid messageId, int attempts, string error, bool final, CancellationToken ct)
    {
        var m = await db.Messages.FirstAsync(x => x.Id == messageId, ct);
        m.Attempts = attempts; m.LastError = error.Length > 1000 ? error[..1000] : error; m.Status = final ? "failed" : "pending";
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// IChannelQueue без Redis: вхідні/вихідні повідомлення вже збережені в БД (status pending/received), тому
/// черга лише логує. Продюсер BullMQ (Redis) — окреме рішення (відкрите питання), підміняється реєстрацією.
/// </summary>
public sealed class DeferredChannelQueue(ILogger<DeferredChannelQueue> log) : IChannelQueue
{
    public Task EnqueueInboundAsync(IReadOnlyList<InboundMessage> messages, CancellationToken ct)
    {
        log.LogInformation("Inbound stored, {Count} message(s) await consumers (no queue producer configured)", messages.Count);
        return Task.CompletedTask;
    }

    public Task ScheduleOutboxRetryAsync(Guid messageId, TimeSpan delay, CancellationToken ct)
    {
        log.LogInformation("Outbox retry for {MessageId} due in {Delay}; message stays 'pending' in DB", messageId, delay);
        return Task.CompletedTask;
    }
}
