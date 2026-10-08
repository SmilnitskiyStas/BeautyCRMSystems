using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
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
    /// <summary>Заклади tenant-а (id, name, address, timezone, isActive) — для всіх ролей персоналу.</summary>
    [HttpGet("locations")]
    [Authorize(Policy = AuthPolicies.Staff)]
    [ProducesResponseType<IReadOnlyList<LocationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Locations(CancellationToken ct) => Ok(await overview.ListLocationsAsync(ct));

    /// <summary>
    /// Записи дня + KPI (кількість записів, виручка за день, нові клієнти, вільні слоти). Лише owner/admin (містить виручку).
    /// date — локальна дата закладу (за замовчуванням сьогодні); locationId — необов'язково (інакше всі активні заклади).
    /// </summary>
    [HttpGet("overview")]
    [Authorize(Policy = AuthPolicies.Management)]
    [ProducesResponseType<OverviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Overview([FromQuery] DateOnly? date, [FromQuery] Guid? locationId, CancellationToken ct) =>
        this.ToResult(await overview.GetAsync(date, locationId, ct));
}
