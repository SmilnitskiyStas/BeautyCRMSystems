using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Integrations.Channels.Inbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Controllers;

public sealed record InboxReplyRequest(string? Text);
public sealed record InboxReadRequest(Guid? UpToMessageId);

/// <summary>
/// Inbox розмов (контракт §22.6.5, ADR-012 §2.6). Модуль beauty_channels, лише owner/admin (specialist -> 403 forbidden_role
/// до будь-якого читання). Відповіді містять тексти розмов: Cache-Control: no-store. tenant лише з токена.
/// </summary>
[ApiController]
[Route("api/beauty/conversations")]
[RequireModule("beauty_channels")]
[Authorize(Policy = AuthPolicies.Staff)] // автентифікація; роль перевіряє InboxManagementOnly, щоб віддати {code: forbidden_role}
[InboxManagementOnly]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class InboxController(InboxService inbox) : ControllerBase
{
    private const int ReplyBody = 32 * 1024; // 3500 символів із запасом на JSON-екранування
    private const int SmallBody = 1024;

    private IActionResult Map<T>(Result<T> r, Func<T, IActionResult>? ok = null)
    {
        if (r.IsOk) return ok is null ? Ok(r.Value) : ok(r.Value!);
        var e = r.Error!;
        if (e.Code == InboxErrors.RateLimited)
        {
            Response.Headers.RetryAfter = (e.Conflicts is int s ? s : 60).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError(e.Code, e.Message));
        }
        return this.ToError(e);
    }

    /// <summary>filter=needs_reply|all (за замовчуванням needs_reply); cursor непрозорий; take ≤ 100.</summary>
    [HttpGet]
    [ProducesResponseType<InboxListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> List(
        [FromQuery] string? filter, [FromQuery] Guid? locationId, [FromQuery] string? channelType, [FromQuery] string? cursor,
        [FromQuery] int? take, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await inbox.ListAsync(actor, filter, locationId, channelType, cursor, take, ct)) : Unauthorized();

    [HttpGet("{id:guid}")]
    [ProducesResponseType<InboxDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Get(Guid id, [FromQuery] Guid? before, [FromQuery] int? take, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await inbox.GetAsync(actor, id, before, take, ct)) : Unauthorized();

    /// <summary>
    /// 202 {message: {id, status}}; 400 idempotency_key_required; 404 conversation_not_found; 409 channel_inactive |
    /// channel_unsupported | idempotency_conflict; 422 invalid_text; 429 rate_limited.
    /// </summary>
    [HttpPost("{id:guid}/reply")]
    [RequestSizeLimit(ReplyBody)]
    [ProducesResponseType<InboxReplyAcceptedDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Reply(
        Guid id, [FromBody] InboxReplyRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!ValidKey(idempotencyKey))
            return BadRequest(new ApiError("idempotency_key_required", "Idempotency-Key header (8..128 visible ASCII characters) is required."));
        return Map(await inbox.ReplyAsync(actor, id, idempotencyKey!, request.Text, ct), r => Accepted(r));
    }

    /// <summary>Ідемпотентно: 204 також, коли ШІ вже активний.</summary>
    [HttpPost("{id:guid}/resume-ai")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResumeAi(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await inbox.ResumeAiAsync(actor, id, ct), _ => NoContent()) : Unauthorized();

    /// <summary>«Опрацьовано» (TASK-793): закриває розмову без відповіді клієнту. Ідемпотентно: 204 також, коли вже закрита.</summary>
    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await inbox.ResolveAsync(actor, id, ct), _ => NoContent()) : Unauthorized();

    [HttpPost("{id:guid}/read")]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Read(Guid id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] InboxReadRequest? request, CancellationToken ct) =>
        User.ToActor() is { } actor ? Map(await inbox.MarkReadAsync(actor, id, request?.UpToMessageId, ct), _ => NoContent()) : Unauthorized();

    private static bool ValidKey(string? key) =>
        key is { Length: >= 8 and <= 128 } && key.All(c => c is >= '!' and <= '~');
}

/// <summary>specialist -> 403 {code: forbidden_role} до виконання дії (політика Staff лише автентифікує).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InboxManagementOnlyAttribute : Attribute, IActionFilter, IOrderedFilter
{
    public int Order => -3000; // раніше за ModelStateInvalidFilter (-2000) і [RequireModule]: specialist не бачить навіть 422

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.User.ToActor() is { Role: BeautyCrm.Application.Features.BeautyAuth.Roles.Owner or BeautyCrm.Application.Features.BeautyAuth.Roles.Admin })
            return;
        context.Result = new ObjectResult(new ApiError(InboxErrors.ForbiddenRole, "Inbox is available to the owner and admin only."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>Cache-Control: no-store на всіх відповідях inbox (тексти розмов, §22.6/§22.10).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InboxNoStoreAttribute : Attribute, IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context) => context.HttpContext.Response.Headers.CacheControl = "no-store";

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
