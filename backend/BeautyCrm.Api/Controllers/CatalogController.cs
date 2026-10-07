using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyCatalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

[ApiController]
[Route("api/beauty")]
[RequireModule("beauty_catalog")]
[Authorize(Policy = AuthPolicies.Staff)] // читання; зміни — лише owner/admin
public sealed class CatalogController(CatalogService catalog, PromotionService promotions) : ControllerBase
{
    [HttpGet("services")]
    public async Task<IActionResult> Services([FromQuery] bool includeInactive, CancellationToken ct) =>
        Ok(await catalog.ListServicesAsync(includeInactive, ct));

    [HttpPost("services")]
    [Authorize(Policy = AuthPolicies.Management)]
    public async Task<IActionResult> CreateService([FromBody] UpsertServiceRequest request, CancellationToken ct) =>
        this.ToResult(await catalog.CreateServiceAsync(request, ct), s => StatusCode(StatusCodes.Status201Created, s));

    [HttpPut("services/{id:guid}")]
    [Authorize(Policy = AuthPolicies.Management)]
    public async Task<IActionResult> UpdateService(Guid id, [FromBody] UpsertServiceRequest request, CancellationToken ct) =>
        this.ToResult(await catalog.UpdateServiceAsync(id, request, ct));

    /// <summary>Мережева ціна (locationId = null) і override закладів.</summary>
    [HttpGet("services/{id:guid}/prices")]
    public async Task<IActionResult> Prices(Guid id, CancellationToken ct) => this.ToResult(await catalog.ListPricesAsync(id, ct));

    [HttpPut("services/{id:guid}/prices")]
    [Authorize(Policy = AuthPolicies.Management)]
    public async Task<IActionResult> SetPrice(Guid id, [FromBody] SetPriceRequest request, CancellationToken ct) =>
        this.ToResult(await catalog.SetPriceAsync(id, request, ct));

    [HttpGet("promotions")]
    public async Task<IActionResult> Promotions(CancellationToken ct) => Ok(await promotions.ListAsync(ct));

    [HttpPost("promotions")]
    [Authorize(Policy = AuthPolicies.Management)]
    public async Task<IActionResult> CreatePromotion([FromBody] UpsertPromotionRequest request, CancellationToken ct) =>
        this.ToResult(await promotions.CreateAsync(request, ct), p => StatusCode(StatusCodes.Status201Created, p));

    [HttpPut("promotions/{id:guid}")]
    [Authorize(Policy = AuthPolicies.Management)]
    public async Task<IActionResult> UpdatePromotion(Guid id, [FromBody] UpsertPromotionRequest request, CancellationToken ct) =>
        this.ToResult(await promotions.UpdateAsync(id, request, ct));

    /// <summary>Перерахунок цін закладу з акціями (на момент at або для однієї акції promotionId).</summary>
    [HttpGet("promotions/preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] Guid locationId, [FromQuery] Guid? promotionId, [FromQuery] DateTimeOffset? at, CancellationToken ct) =>
        this.ToResult(await promotions.PreviewAsync(locationId, promotionId, at, ct));
}
