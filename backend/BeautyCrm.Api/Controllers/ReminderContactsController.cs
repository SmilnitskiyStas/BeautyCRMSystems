using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Вподобання клієнта (контракт §22.6.2, ADR-012): модуль beauty_clients, owner/admin (specialist -> 403 forbidden_role до читання тіла).
/// Відповіді містять ПД: Cache-Control: no-store. Tenant лише з токена; чужий або видалений клієнт -> 404.
/// </summary>
[ApiController]
[Route("api/beauty/clients/{clientId:guid}/contact-preferences")]
[RequireModule("beauty_clients")]
[Authorize(Policy = AuthPolicies.Staff)] // автентифікація; роль перевіряє ReminderRolesAttribute, щоб віддати {code: forbidden_role}
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ContactPreferencesController(ReminderContactsService service) : ControllerBase
{
    private const int BodyLimit = 4 * 1024;

    [HttpGet]
    [ProducesResponseType<ContactPreferencesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid clientId, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.GetPreferencesAsync(actor, clientId, ct)) : Unauthorized();

    /// <summary>422 invalid_method | invalid_offsets; 409 telegram_unavailable | phone_required | call_reminders_disabled | telegram_reminders_disabled.</summary>
    [HttpPut]
    [RequestSizeLimit(BodyLimit)]
    [ProducesResponseType<ContactPreferencesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Put(Guid clientId, [FromBody] PutContactPreferencesRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.PutPreferencesAsync(actor, clientId, request, ct)) : Unauthorized();
}

/// <summary>
/// Задачі «зателефонувати» (контракт §22.6.3): модуль beauty_clients, owner/admin. Телефон віддається лише тут і лише з клієнта.
/// 409 task_already_closed / task_cancelled несуть поточний стан у полі <c>task</c>.
/// </summary>
[ApiController]
[Route("api/beauty/call-tasks")]
[RequireModule("beauty_clients")]
[Authorize(Policy = AuthPolicies.Staff)]
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class CallTasksController(ReminderContactsService service) : ControllerBase
{
    private const int BodyLimit = 2 * 1024;

    /// <summary>day=today|tomorrow|YYYY-MM-DD (+ необов'язковий to=YYYY-MM-DD, ≤ 31 день); status=open|done|skipped|cancelled|all; take ≤ 200.</summary>
    [HttpGet]
    [ProducesResponseType<CallTaskListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromQuery] string? day, [FromQuery] string? to, [FromQuery] string? status, [FromQuery] Guid? locationId, [FromQuery] int? take,
        CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.ListCallTasksAsync(actor, day, to, status, locationId, take, ct)) : Unauthorized();

    [HttpPatch("{id:guid}")]
    [RequestSizeLimit(BodyLimit)]
    [ProducesResponseType<CallTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Patch(Guid id, [FromBody] PatchCallTaskRequest request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        var r = await service.PatchCallTaskAsync(actor, id, request, ct);
        if (!r.IsOk && r.Error!.Conflicts is CallTaskDto task)
            return Conflict(new { code = r.Error.Code, message = r.Error.Message, task });
        return this.ToResult(r);
    }
}

/// <summary>Налаштування нагадувань tenant (контракт §22.6.4): модуль beauty_booking; читають owner/admin, змінює лише owner.</summary>
[ApiController]
[Route("api/beauty/settings/reminders")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ReminderSettingsController(ReminderContactsService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ReminderSettingsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.GetSettingsAsync(actor, ct)) : Unauthorized();

    /// <summary>Усі 4 поля обов'язкові: 422 settings_incomplete | invalid_quiet_hours.</summary>
    [HttpPut]
    [ReminderRoles(Roles.Owner)]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<ReminderSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Put([FromBody] ReminderSettingsDto request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await service.PutSettingsAsync(actor, request, ct)) : Unauthorized();
}

/// <summary>Дозволені ролі (інакше 403 {code: forbidden_role} до прив'язки тіла). Метод може звузити перелік класу.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class ReminderRolesAttribute(params string[] roles) : Attribute, IActionFilter, IOrderedFilter
{
    public int Order => -3000; // раніше за ModelStateInvalidFilter (-2000): specialist не бачить навіть 422

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var allowed = context.ActionDescriptor.EndpointMetadata.OfType<ReminderRolesAttribute>().LastOrDefault() ?? this;
        var role = context.HttpContext.User.ToActor()?.Role;
        if (role is not null && allowed.Allowed.Contains(role)) return;
        context.Result = new ObjectResult(new ApiError("forbidden_role", "This action is not available for your role."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    internal string[] Allowed { get; } = roles;
}
