using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyLocations;
using BeautyCrm.Application.Features.BeautyProductImages;
using Microsoft.AspNetCore.Http.Features;
using Err = BeautyCrm.Application.Features.BeautyCommon.Error;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyOverview;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Заклади (§16, TASK-697): читають усі ролі персоналу, змінюють owner/admin. Видалення немає — лише деактивація.
/// Контролер лише маршрутизує: правила (унікальність імені, timezone, has_future_appointments) — у Application/BeautyLocations.
/// </summary>
[ApiController]
[Route("api/beauty/locations")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class LocationsController(LocationService locations, LocationLogoService logos, MediaOptions media, UploadThrottle uploads) : ControllerBase
{
    /// <summary>За замовчуванням лише активні; `includeInactive=true` враховується тільки для owner/admin.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<LocationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        User.ToActor() is { } actor ? Ok(await locations.ListAsync(actor, includeInactive, ct)) : Unauthorized();

    [HttpPost]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] LocationRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await locations.CreateAsync(actor, request, ct), l => StatusCode(StatusCodes.Status201Created, l))
            : Unauthorized();

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(Guid id, [FromBody] LocationRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await locations.UpdateAsync(actor, id, request, ct)) : Unauthorized();

    // ---------- логотип закладу (ADR-027 медіасховище, контракт .ai/artifacts/location-logo) ----------

    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
    }

    /// <summary>
    /// 200 LocationDto. multipart/form-data, поле <c>file</c> (JPEG/PNG/WebP); ті самі ліміти, rate limit, конкурентність і конвеєр обробки, що й фото товарів.
    /// Попередній логотип видаляється після commit (best effort).
    /// </summary>
    [HttpPut("{id:guid}/logo")]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [DisableFormValueModelBinding]
    [ProducesResponseType<LocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UploadLogo(Guid id, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!uploads.TryAcquire(actor.TenantId, actor.UserId)) return RateLimited();
        // ліміт тіла для цього маршруту: файл + службові частини (тіло читається потоково нижче)
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = media.MaxUploadBytes + MultipartUpload.Overhead;
        // глобальний ліміт одночасних upload на процес - ДО читання тіла в пам'ять
        if (!uploads.TryEnter())
        {
            Response.Headers.RetryAfter = "5";
            Result<LocationDto> busy = Err.Unavailable("media_busy", "Image processing is busy. Try again shortly.");
            return this.ToResult(busy);
        }
        Result<LocationDto> result;
        try { result = await logos.UploadAsync(actor, id, c => MultipartUpload.ReadFilePartAsync(Request, media, c), ct); }
        finally { uploads.Exit(); }
        if (!result.IsOk && result.Error!.Code == "media_busy") Response.Headers.RetryAfter = "5";
        return this.ToResult(result);
    }

    /// <summary>200 LocationDto (logoUrl = null). Ідемпотентно.</summary>
    [HttpDelete("{id:guid}/logo")]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> DeleteLogo(Guid id, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!uploads.TryAcquire(actor.TenantId, actor.UserId)) return RateLimited();
        return this.ToResult(await logos.DeleteAsync(actor, id, ct));
    }

    // ---------- вихідні дні закладу (§17, TASK-701) ----------

    /// <summary>
    /// Щотижневі вихідні (повна заміна). 409 has_appointments_on_closed_days + conflicts[] (без даних клієнта), якщо на нові
    /// вихідні є майбутні активні записи й не передано confirm=true; зміни тоді немає.
    /// </summary>
    [HttpPut("{id:guid}/closed-weekdays")]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetClosedWeekdays(Guid id, [FromBody] ClosedWeekdaysRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await locations.SetClosedWeekdaysAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>
    /// Години роботи закладу (§18.1, TASK-703): `{workingHours: {...}|null, confirm?}`. 409 has_appointments_outside_hours +
    /// conflicts[] (без даних клієнта), якщо майбутні активні записи опиняються поза новими годинами й не передано confirm=true.
    /// </summary>
    [HttpPut("{id:guid}/working-hours")]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetWorkingHours(Guid id, [FromBody] WorkingHoursRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await locations.SetWorkingHoursAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>Закриття закладу (`reason` лише керівникам). Діапазон до 366 днів; за замовчуванням від сьогодні на 366 днів.</summary>
    [HttpGet("{id:guid}/closures")]
    [ProducesResponseType<IReadOnlyList<ClosureDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ListClosures(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await locations.ListClosuresAsync(actor, id, from, to, ct)) : Unauthorized();

    [HttpPost("{id:guid}/closures")]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<ClosureDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddClosure(Guid id, [FromBody] ClosureRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await locations.AddClosureAsync(actor, id, request, ct), c => StatusCode(StatusCodes.Status201Created, c))
            : Unauthorized();

    [HttpDelete("{id:guid}/closures/{closureId:guid}")]
    [RequirePermission(PermissionKeys.LocationsAccess, PermissionLevels.Edit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteClosure(Guid id, Guid closureId, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await locations.DeleteClosureAsync(actor, id, closureId, ct), _ => NoContent())
            : Unauthorized();
}
