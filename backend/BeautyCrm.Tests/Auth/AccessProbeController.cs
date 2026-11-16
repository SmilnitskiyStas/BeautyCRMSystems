using System.ComponentModel.DataAnnotations;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Tests.Auth;

public sealed record ProbeBody([Required, StringLength(5)] string Name);

/// <summary>
/// Тестові ендпоінти ядра доступу (TASK-850): у production їх немає. Підключаються лише в <c>AccessApiTests</c> через ApplicationPart.
/// Дають інтеграційну перевірку [RequirePermission], порядку фільтрів і ToActor() у реальному конвеєрі.
/// </summary>
[ApiController]
[Route("api/__probe")]
[Authorize(Policy = AuthPolicies.Staff)]
public sealed class AccessProbeController : ControllerBase
{
    [HttpGet("clients")]
    [RequireModule("beauty_clients")]
    [RequirePermission(PermissionKeys.ClientsView, PermissionLevels.All)]
    public IActionResult Clients() => Ok(new { ok = true });

    /// <summary>Без [RequireModule]: модуль вимкнено -> відмову дає сам [RequirePermission] (module_disabled).</summary>
    [HttpGet("marketing")]
    [RequirePermission(PermissionKeys.MarketingCampaigns, PermissionLevels.Edit)]
    public IActionResult Marketing() => Ok(new { ok = true });

    /// <summary>AND-семантика двох атрибутів.</summary>
    [HttpGet("both")]
    [RequirePermission(PermissionKeys.CalendarView, PermissionLevels.All)]
    [RequirePermission(PermissionKeys.RevenueView, PermissionLevels.All)]
    public IActionResult Both() => Ok(new { ok = true });

    /// <summary>Відмова приходить до прив'язки тіла: 403, а не 422.</summary>
    [HttpPost("body")]
    [RequirePermission(PermissionKeys.ClientsEdit, PermissionLevels.All)]
    public IActionResult Body([FromBody] ProbeBody body) => Ok(new { body.Name });

    [HttpGet("actor")]
    public IActionResult Actor()
    {
        var actor = User.ToActor();
        return Ok(new
        {
            role = actor?.Role,
            specialistId = actor?.SpecialistId,
            roleClaims = User.FindAll("role").Select(c => c.Value).ToArray(),
            msRoleClaims = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToArray(),
            specialistClaims = User.FindAll("specialist_id").Select(c => c.Value).ToArray(),
            clientsView = actor?.Access.Level(PermissionKeys.ClientsView),
            calendarView = actor?.Access.Level(PermissionKeys.CalendarView),
            specialistInactive = actor?.Access.SpecialistInactive,
        });
    }
}
