using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyBooking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>Політика скасування/повернення коштів tenant-а: читають усі ролі staff, змінює лише owner.</summary>
[ApiController]
[Route("api/beauty/settings/cancellation")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class CancellationSettingsController(CancellationSettingsService settings) : ControllerBase
{
    /// <summary>Поточні налаштування або значення за замовчуванням (12 год, 50%, 100%, без комісії).</summary>
    [HttpGet]
    [ProducesResponseType<CancellationSettings>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await settings.GetAsync(ct));

    /// <summary>Повна заміна; 422 при невалідних значеннях (відсотки 0..100, windowHours 0..720).</summary>
    [HttpPut]
    [Authorize(Policy = AuthPolicies.Owner)]
    [ProducesResponseType<CancellationSettings>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Put([FromBody] UpdateCancellationSettingsRequest request, CancellationToken ct) =>
        this.ToResult(await settings.UpdateAsync(request, ct));
}
