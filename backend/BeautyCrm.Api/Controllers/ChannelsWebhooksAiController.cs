using System.Text.Json;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>Налаштування каналів: секрети лише записуються, у відповіді — маска (останні 4 символи).</summary>
[ApiController]
[Route("api/beauty/channels")]
[RequireModule("beauty_channels")]
[Authorize(Policy = AuthPolicies.Management)]
public sealed class ChannelsController(
    ChannelSettingsService channels, IChannelSettingsStore store, TelegramCommandsService commands) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await channels.ListAsync(ct));

    /// <summary>Створює або оновлює канал за id (type обов'язковий при створенні).</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Upsert(Guid id, [FromBody] UpsertChannelRequest request, CancellationToken ct)
    {
        var wasActive = (await store.GetAsync(id, ct))?.IsActive ?? false;
        var result = await channels.UpsertAsync(id, request, ct);
        // §23.4: меню команд Telegram після коміту, якщо змінено токен або isActive false -> true. Best-effort, тайм-аут 5 с,
        // відповідь PUT від результату не залежить.
        if (result.IsOk && result.Value!.Type == ChannelIds.Telegram && result.Value.IsActive && (request.Token is not null || !wasActive))
            await commands.TrySyncAfterSaveAsync(id, ct);
        return this.ToResult(result);
    }

    /// <summary>Оновлює меню команд бота (setMyCommands: default і uk). 204; помилки Telegram без тексту.</summary>
    [HttpPost("{id:guid}/telegram/sync-commands")]
    public async Task<IActionResult> SyncCommands(Guid id, CancellationToken ct) =>
        await commands.SyncAsync(id, ct) switch
        {
            ChannelActionResult.Ok => NoContent(),
            ChannelActionResult.ChannelNotFound => NotFound(new ApiError("channel_not_found", "Channel not found.")),
            ChannelActionResult.Unsupported => Conflict(new ApiError("channel_unsupported", "Channel is not a Telegram channel.")),
            ChannelActionResult.Inactive => Conflict(new ApiError("channel_inactive", "Channel is inactive.")),
            ChannelActionResult.TokenMissing => Conflict(new ApiError("token_missing", "Channel token is not configured.")),
            _ => StatusCode(StatusCodes.Status502BadGateway, new ApiError("telegram_unavailable", "Telegram is unavailable.")),
        };
}

/// <summary>
/// Вхідні webhook-и каналів. ПУБЛІЧНИЙ за дизайном (виняток із auth-за-замовчуванням): tenant визначається за
/// channelId, автентичність — обов'язковий підпис/секрет каналу (без нього 401, нічого не зберігається).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/beauty/webhooks/{channel}/{channelId:guid}")]
public sealed class WebhooksController(WebhookIngestService ingest) : ControllerBase
{
    private const int MaxBodyBytes = 1_048_576;

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Receive(string channel, Guid channelId, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var r = await ingest.HandleAsync(channel, channelId, ToRequest(Request), body, ct);
        return r.Outcome switch
        {
            WebhookOutcome.Accepted => Ok(new { accepted = r.NewMessages }),
            WebhookOutcome.InvalidSignature => Unauthorized(new ApiError("invalid_signature", "Signature verification failed.")),
            WebhookOutcome.BadPayload => BadRequest(new ApiError("bad_payload", "Payload could not be parsed.")),
            _ => NotFound(new ApiError("channel_not_found", "Unknown channel.")),
        };
    }

    /// <summary>Meta handshake (hub.challenge) для Instagram/Messenger.</summary>
    [HttpGet]
    public async Task<IActionResult> Handshake(string channel, Guid channelId, CancellationToken ct)
    {
        var challenge = await ingest.VerifyChallengeAsync(channel, channelId, ToRequest(Request), ct);
        return challenge is null ? Forbid() : Content(challenge, "text/plain");
    }

    private static WebhookRequest ToRequest(HttpRequest req) => new(
        req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
        req.Query.ToDictionary(q => q.Key, q => q.Value.ToString()));
}

public sealed record RunAiToolRequest(string Tool, JsonElement Input, Guid? ClientId);
public sealed record AiActionResponse(string Content, bool IsError, string? Status);

/// <summary>Журнал дій AI і підтвердження людиною (режим confirm).</summary>
[ApiController]
[Route("api/beauty/ai/actions")]
[RequireModule("beauty_ai")]
[Authorize(Policy = AuthPolicies.Management)]
public sealed class AiActionsController(IAiActionReader reader, AiToolExecutor executor, AiTenantRuntime runtime) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 100, [FromQuery] string? status = null, CancellationToken ct = default) =>
        Ok((await reader.ListAsync(take, status, ct))
            .Select(a => new { a.Id, a.Action, a.Target, a.CreatedAt, a.Status, a.Revertible, mode = a.Mode.ToString().ToLowerInvariant() }));

    /// <summary>Запуск tool менеджером; режим автономності — з налаштувань tenant (немає рядка = confirm; auto лише за збереженою згодою).</summary>
    [HttpPost]
    public async Task<IActionResult> Run([FromBody] RunAiToolRequest request, CancellationToken ct)
    {
        var tenantId = this.TenantId();
        var mode = await runtime.GetModeAsync(tenantId, ct);
        return Map(await executor.ExecuteAsync(new AiToolContext(tenantId, request.ClientId, mode, AiScope.Manager),
            request.Tool, request.Input, ct), StatusCodes.Status422UnprocessableEntity);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) =>
        Map(await executor.ConfirmAsync(this.TenantId(), id, ct), StatusCodes.Status409Conflict);

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct) =>
        Map(await executor.RejectPendingAsync(this.TenantId(), id, ct), StatusCodes.Status409Conflict);

    [HttpPost("{id:guid}/revert")]
    public async Task<IActionResult> Revert(Guid id, CancellationToken ct) =>
        Map(await executor.RevertAsync(this.TenantId(), id, ct), StatusCodes.Status409Conflict);

    private IActionResult Map(AiToolResult r, int errorStatus)
    {
        var body = new AiActionResponse(r.Content, r.IsError, r.ActionStatus);
        return r.IsError ? StatusCode(errorStatus, body) : Ok(body);
    }
}
