using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyOverview;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>Довідник закладів і огляд дня для адмінки (TASK-690). Контролер лише маршрутизує: логіка в Application/BeautyOverview.</summary>
[ApiController]
[Route("api/beauty")]
[RequireModule("beauty_booking")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class OverviewController(OverviewService overview) : ControllerBase
{
    /// <summary>
    /// Записи дня + KPI (кількість записів, виручка за день, нові клієнти, вільні слоти). Потрібен calendar.view = all; kpi.revenue лише за revenue.view = all (M4).
    /// date — локальна дата закладу (за замовчуванням сьогодні); locationId — необов'язково (інакше всі активні заклади).
    /// </summary>
    [HttpGet("overview")]
    [Authorize(Policy = AuthPolicies.Staff)]
    [RequirePermission(PermissionKeys.CalendarView, PermissionLevels.All)]
    [ProducesResponseType<OverviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Overview([FromQuery] DateOnly? date, [FromQuery] Guid? locationId, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await overview.GetAsync(actor, date, locationId, ct)) : Unauthorized();
}
