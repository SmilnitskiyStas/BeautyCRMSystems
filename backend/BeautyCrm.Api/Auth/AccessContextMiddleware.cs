using System.Security.Claims;
using BeautyCrm.Api.Auth.ControlPlane;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace BeautyCrm.Api.Auth;

/// <summary>Scoped-реалізація <see cref="IAccessContext"/>; заповнюється лише <see cref="AccessContextMiddleware"/>.</summary>
public sealed class AccessContext : IAccessContext
{
    public bool IsLoaded { get; private set; }
    public AccessSet Access { get; private set; } = AccessSet.None;
    public int AccessVersion { get; private set; }
    public string AccessStamp { get; private set; } = string.Empty;
    public IReadOnlyList<string> Modules { get; private set; } = [];

    internal void Load(AccessSet access, int accessVersion, string stamp, IReadOnlyList<string> modules)
    {
        Access = access;
        AccessVersion = accessVersion;
        AccessStamp = stamp;
        Modules = modules;
        IsLoaded = true;
    }
}

/// <summary>
/// Додаткова identity без claims, що несе <see cref="IAccessContext"/> запиту, щоб <c>User.ToActor()</c> брав
/// <see cref="AccessSet"/> з контексту без зміни сигнатури (TASK-851..853 не торкаються сотень викликів). Токен підробити таку
/// identity не може: її додає лише middleware після читання БД.
/// </summary>
public sealed class AccessIdentity : ClaimsIdentity
{
    public IAccessContext Context { get; }

    public AccessIdentity(IAccessContext context) => Context = context;

    private AccessIdentity(AccessIdentity other) : base(other) => Context = other.Context;

    public override ClaimsIdentity Clone() => new AccessIdentity(this);
}

/// <summary>
/// ADR-023 §2.5, контракт §30.5 п.4 (M6). Для JWT користувача tenant одним запитом під RLS читає роль, активність, прив'язку,
/// <c>access_version</c>, <c>specialists.is_active</c> і права; ЗАМІНЮЄ всі claims <c>role</c> і <c>specialist_id</c> значеннями з БД
/// (зміна ролі діє з наступного запиту без перелогіну); додає <c>X-Access-Version</c> до відповідей (крім 401).
/// Fail-closed: JWT без валідних sub/tenant_id або з чужим tenant — 401 invalid_token; користувача немає/вимкнено/не active/не
/// роль tenant — 401 session_revoked; збій БД чи будь-який виняток — 503 access_unavailable (Retry-After: 5), без фолбеку на claims.
/// Пропускає анонімні запити, [AllowAnonymous] і principal схем PlatformKey / ControlPlaneHmac. Стоїть після TenantMiddleware.
/// </summary>
public sealed class AccessContextMiddleware(RequestDelegate next, ILogger<AccessContextMiddleware> logger)
{
    public const string VersionHeader = "X-Access-Version";

    public async Task InvokeAsync(HttpContext http, TenantContext tenant, IAccessStore store, AccessContext context)
    {
        if (!IsTenantJwtRequest(http))
        {
            await next(http);
            return;
        }

        string? failure = null;
        try
        {
            if (!Guid.TryParse(http.User.FindFirst("sub")?.Value, out var userId) || userId == Guid.Empty
                || !Guid.TryParse(http.User.FindFirst(JwtTokenIssuer.TenantClaim)?.Value, out var tokenTenant) || tokenTenant == Guid.Empty
                || tenant.TenantId != tokenTenant)
            {
                failure = "invalid_token";
            }
            else
            {
                var snapshot = await store.LoadAsync(userId, http.RequestAborted);
                if (snapshot is null || !snapshot.IsActive || snapshot.State != UserStates.Active || !Roles.IsTenantRole(snapshot.Role))
                {
                    failure = "session_revoked";
                }
                else
                {
                    var access = AccessSet.Build(snapshot.Role, snapshot.Permissions, snapshot.Modules, snapshot.SpecialistActive == false);
                    var stamp = AccessStamp.Compute(snapshot.AccessVersion, snapshot.Modules);
                    context.Load(access, snapshot.AccessVersion, stamp, [.. snapshot.Modules.OrderBy(m => m, StringComparer.Ordinal)]);
                    ReplaceClaims(http.User, snapshot.Role, snapshot.SpecialistId);
                    http.User.AddIdentity(new AccessIdentity(context));
                    http.Response.OnStarting(() =>
                    {
                        if (http.Response.StatusCode != StatusCodes.Status401Unauthorized)
                            http.Response.Headers[VersionHeader] = stamp;
                        return Task.CompletedTask;
                    });
                }
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return; // клієнт відключився
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "access_unavailable: failed to load access for the request.");
            await Reject(http, StatusCodes.Status503ServiceUnavailable, "access_unavailable", "Access check is temporarily unavailable.");
            return;
        }

        if (failure is not null)
        {
            await Reject(http, StatusCodes.Status401Unauthorized, failure,
                failure == "session_revoked" ? "The session is no longer valid." : "Token is invalid or expired.");
            return;
        }
        await next(http);
    }

    /// <summary>JWT-principal користувача tenant (не PlatformKey / ControlPlaneHmac, не анонімний, не [AllowAnonymous]).</summary>
    private static bool IsTenantJwtRequest(HttpContext http)
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true) return false;
        if (user.Identities.Any(i => i.AuthenticationType is PlatformKeyHandler.Scheme or ControlPlaneHmacHandler.Scheme)) return false;
        if (http.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null) return false;
        return true;
    }

    /// <summary>Видаляє з УСІХ identity всі claims role і specialist_id (зокрема дублікати й ClaimTypes.Role), додає по одному з БД.</summary>
    private static void ReplaceClaims(ClaimsPrincipal user, string role, Guid? specialistId)
    {
        foreach (var identity in user.Identities)
        {
            foreach (var c in identity.Claims.Where(c => c.Type is JwtTokenIssuer.RoleClaim or ClaimTypes.Role or JwtTokenIssuer.SpecialistClaim).ToList())
                identity.RemoveClaim(c);
        }
        var primary = user.Identities.First();
        primary.AddClaim(new Claim(JwtTokenIssuer.RoleClaim, role));
        if (specialistId is { } s) primary.AddClaim(new Claim(JwtTokenIssuer.SpecialistClaim, s.ToString()));
    }

    private static async Task Reject(HttpContext http, int status, string code, string message)
    {
        if (http.Response.HasStarted) return;
        http.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable) http.Response.Headers.RetryAfter = "5";
        await http.Response.WriteAsJsonAsync(new ApiError(code, message));
    }
}
