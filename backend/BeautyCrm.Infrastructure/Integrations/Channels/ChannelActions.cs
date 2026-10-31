using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>Результат транспортної операції каналу (§23.4). Тексту помилок провайдера немає навмисно (ПД/токени).</summary>
public enum ChannelActionResult
{
    Ok,
    ChannelNotFound,
    /// <summary>Канал не Telegram або адаптер не має такої можливості (mock).</summary>
    Unsupported,
    Inactive,
    TokenMissing,
    /// <summary>Telegram недоступний/відхилив запит (тимчасово чи ні). Для прибирання кнопок - привід для повтору sweeper (до 3 разів).</summary>
    Failed,
}

/// <summary>
/// Порт транспорту кнопок для TASK-752/753 (без доменної логіки): викликач передає tenant і канал, порт сам бере секрети каналу
/// під RLS tenant. Усі методи best-effort: виняток провайдера не виходить назовні, лог - лише <c>channel</c> і код/тип помилки.
/// </summary>
public interface IChannelActionsPort
{
    /// <summary>answerCallbackQuery без тексту; тайм-аут 3 с, без повторів. Основний ack робить <see cref="InboundIngestCore"/> одразу після збереження.</summary>
    Task<ChannelActionResult> AnswerCallbackAsync(Guid tenantId, Guid channelId, string callbackQueryId, CancellationToken ct);

    /// <summary>
    /// editMessageReplyMarkup з порожньою клавіатурою для повідомлення <paramref name="externalMessageId"/> (Telegram message_id).
    /// <see cref="ChannelActionResult.Ok"/> також для "message is not modified" / "message to edit not found". Без externalMessageId не викликати.
    /// </summary>
    Task<ChannelActionResult> ClearKeyboardAsync(Guid tenantId, Guid channelId, string externalConversationId, string externalMessageId, CancellationToken ct);

    /// <summary>setMyCommands: default і <c>uk</c>; тайм-аут 5 с.</summary>
    Task<ChannelActionResult> SyncCommandsAsync(Guid tenantId, Guid channelId, CancellationToken ct);
}

public sealed class ChannelActionsPort(
    BeautyDbContext db, TenantContext tenant, ISecretProtector secrets, ChannelRegistry registry, ChannelRequestContext context,
    ILogger<ChannelActionsPort> log) : IChannelActionsPort
{
    public Task<ChannelActionResult> AnswerCallbackAsync(Guid tenantId, Guid channelId, string callbackQueryId, CancellationToken ct) =>
        RunAsync<IChannelCallbackAcknowledger>(tenantId, channelId, "telegram_ack_failed", InboundIngestCore.AckTimeout, null, ct,
            (a, t) => a.AcknowledgeCallbackAsync(callbackQueryId, t));

    public Task<ChannelActionResult> ClearKeyboardAsync(
        Guid tenantId, Guid channelId, string externalConversationId, string externalMessageId, CancellationToken ct) =>
        RunAsync<IChannelMessageEditor>(tenantId, channelId, "telegram_clear_failed", TimeSpan.FromSeconds(5), null, ct,
            (a, t) => a.ClearActionsAsync(externalConversationId, externalMessageId, t));

    public Task<ChannelActionResult> SyncCommandsAsync(Guid tenantId, Guid channelId, CancellationToken ct) =>
        RunAsync<IChannelCommandsSync>(tenantId, channelId, "telegram_commands_failed", TelegramAdapter.CommandsTimeout + TimeSpan.FromSeconds(1),
            "telegram_commands_synced", ct, (a, t) => a.SyncCommandsAsync(t));

    private async Task<ChannelActionResult> RunAsync<TCapability>(
        Guid tenantId, Guid channelId, string failLog, TimeSpan timeout, string? okLog, CancellationToken ct,
        Func<TCapability, CancellationToken, Task> call) where TCapability : class
    {
        try
        {
            TenantGuard.EnsureOrSetForTrustedJob(tenant, tenantId); // викликач - довірений конвеєр/API; чужий tenant -> TenantMismatchException
            var ch = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, ct);
            if (ch is null) return ChannelActionResult.ChannelNotFound;
            var type = ChannelTypeMap.ToAdapterId(EnumText<ChannelType>.ToDb(ch.Type));
            if (type != ChannelIds.Telegram || registry.Find(type) is not TCapability capability) return ChannelActionResult.Unsupported;
            if (!ch.IsActive) return ChannelActionResult.Inactive;
            var c = ChannelSecrets.Read(secrets, ch.CredentialsEncrypted);
            if (string.IsNullOrEmpty(c.Token)) return ChannelActionResult.TokenMissing;

            using var scope = context.Begin(new ResolvedChannel(tenantId, ch.Id, type, new ChannelCredentials(c.Token, c.WebhookSecret, c.AppSecret, c.VerifyToken)));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await call(capability, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(); }
            if (okLog is not null) log.LogInformation(okLog + " channel={ChannelId}", channelId);
            return ChannelActionResult.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TenantMismatchException) { throw; }
        catch (Exception ex)
        {
            // Лише код/тип: повідомлення винятків мережі може містити URL з токеном бота.
            log.LogWarning(failLog + " channel={ChannelId} error={ErrorType}", channelId, (ex as PermanentChannelException)?.Code ?? ex.GetType().Name);
            return ChannelActionResult.Failed;
        }
    }
}

/// <summary>
/// Меню команд Telegram для API (§23.4): ручний <c>POST /channels/{id}/telegram/sync-commands</c> і best-effort виклик після збереження
/// каналу (<c>PUT /channels/{id}</c>, якщо змінено токен або isActive false -> true). tenant береться з автентифікованого контексту.
/// </summary>
public sealed class TelegramCommandsService(IChannelActionsPort port, TenantContext tenant)
{
    /// <summary>Тайм-аут best-effort синхронізації після збереження каналу (відповідь PUT від результату не залежить).</summary>
    public static readonly TimeSpan AfterSaveTimeout = TimeSpan.FromSeconds(5);

    public Task<ChannelActionResult> SyncAsync(Guid channelId, CancellationToken ct) =>
        tenant.TenantId is { } t ? port.SyncCommandsAsync(t, channelId, ct) : Task.FromResult(ChannelActionResult.ChannelNotFound);

    /// <summary>Ніколи не кидає (крім скасування запиту): збій лише в лозі порту.</summary>
    public async Task TrySyncAfterSaveAsync(Guid channelId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AfterSaveTimeout);
        try { await SyncAsync(channelId, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }
}
