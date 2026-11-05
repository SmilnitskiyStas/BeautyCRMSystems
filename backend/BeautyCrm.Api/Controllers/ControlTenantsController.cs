using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Auth.ControlPlane;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyControlPlane;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Control plane ShelfGuard (ADR-018, beauty-contracts.md §27.3-§27.7): керування бізнесами. Автентифікація лише
/// <c>ControlPlaneHmac</c> (підпис, вікно, nonce, rate limit - у <see cref="ControlPlaneHmacHandler"/> / middleware); JWT і
/// X-Platform-Key цю групу не відкривають. Контролер лише маршрутизує: уся логіка - у <see cref="ControlPlaneService"/>
/// (спільні доменні операції з /api/platform/tenants). Відповіді не кешуються (no-store від middleware).
/// </summary>
[ApiController]
[Route("api/control/v1/tenants")]
[Authorize(Policy = AuthPolicies.ControlPlane)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class ControlTenantsController(ControlPlaneService service) : ControllerBase
{
    /// <summary>201 + одноразове посилання-запрошення; 200 + Idempotent-Replayed (повтор); 409 external_ref_conflict | tenant_slug_taken; 422.</summary>
    [HttpPost]
    [ProducesResponseType<CpCreateResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<CpCreateResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CpCreateRequest? request, CancellationToken ct)
    {
        var result = await service.CreateAsync(request ?? new CpCreateRequest(null, null, null, null, null, null), Caller(), ct);
        if (!result.IsOk) return this.ToAuthError(result.Error!);
        if (!result.Value!.Replayed) return StatusCode(StatusCodes.Status201Created, result.Value.Body);
        Response.Headers["Idempotent-Replayed"] = "true";
        return Ok(result.Value.Body);
    }

    [HttpGet("{externalRef}")]
    public async Task<IActionResult> Get(string externalRef, CancellationToken ct) =>
        this.ToResult(await service.GetAsync(externalRef, ct));

    [HttpPost("{externalRef}/suspend")]
    public async Task<IActionResult> Suspend(string externalRef, [FromBody] CpSuspendRequest? request, CancellationToken ct) =>
        this.ToResult(await service.SuspendAsync(externalRef, request, Caller(), ct));

    [HttpPost("{externalRef}/resume")]
    public async Task<IActionResult> Resume(string externalRef, [FromBody] CpActorRequest? request, CancellationToken ct) =>
        this.ToResult(await service.ResumeAsync(externalRef, request, Caller(), ct));

    /// <summary>Повний набір модулів (TASK-831, §27.6). Дозволено для призупиненого tenant; той самий набір = 200 noop.</summary>
    [HttpPut("{externalRef}/modules")]
    public async Task<IActionResult> SetModules(string externalRef, [FromBody] CpModulesRequest? request, CancellationToken ct) =>
        this.ToResult(await service.SetModulesAsync(externalRef, request, Caller(), ct));

    /// <summary>Перевипуск посилання власника; попереднє невикористане запрошення анулюється.</summary>
    [HttpPost("{externalRef}/owner-invite")]
    [ProducesResponseType<CpOwnerInviteResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> ReissueOwnerInvite(string externalRef, [FromBody] CpActorRequest? request, CancellationToken ct) =>
        this.ToResult(await service.ReissueOwnerInviteAsync(externalRef, request, Caller(), ct),
            r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>kid / X-Request-Id кладе ControlPlaneHmacHandler після валідного підпису; IP - після UseForwardedHeaders.</summary>
    private CpCaller Caller() => new(
        HttpContext.Items[ControlPlaneHmacHandler.KidItem] as string ?? "unknown",
        HttpContext.Items[ControlPlaneHmacHandler.RequestIdItem] as string,
        HttpContext.Connection.RemoteIpAddress?.ToString());
}
