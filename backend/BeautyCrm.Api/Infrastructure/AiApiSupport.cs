using System.Threading.RateLimiting;
using BeautyCrm.Api.Auth;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Ліміт PUT/DELETE/test ключів ШІ на пару (tenant, користувач) (контракт §20.2 <c>Ai:KeyRateLimit</c>, §20.5). Вбудований rate limiter
/// стоїть до автентифікації й не знає користувача, тож перевірка йде в контролері після автентифікації (патерн <see cref="AbsenceThrottle"/>).
/// </summary>
public sealed class AiKeyThrottle
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly TimeSpan _window;

    public AiKeyThrottle(int permit, TimeSpan window)
    {
        _window = window;
        _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 }));
    }

    /// <summary>false = перевищено; <paramref name="retryAfter"/> - скільки чекати.</summary>
    public bool TryAcquire(Guid tenantId, Guid userId, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire($"{tenantId:N}:{userId:N}");
        retryAfter = TimeSpan.Zero;
        if (lease.IsAcquired) return true;
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? ra : _window;
        return false;
    }
}

/// <summary>403 forbidden_role для endpoint-ів, доступних лише owner (admin проходить політику Management, але не цю).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AiOwnerOnlyAttribute : Attribute, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.User.ToActor() is { Role: BeautyCrm.Application.Features.BeautyAuth.Roles.Owner }) return;
        context.Result = new ObjectResult(new ApiError(AiSettingsService.ErrForbiddenRole, "This action is available to the owner only."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>
/// HTTP-мапінг винятків ШІ-шару (TASK-715): <see cref="AiQuotaExceededException"/> -> 429 ai_quota_exceeded + Retry-After;
/// <see cref="AiRateLimitedException"/> -> 429 ai_rate_limited + Retry-After; <see cref="AiUnavailableException"/> -> 409 ai_unavailable + reason;
/// <see cref="AiProviderException"/> -> 502 provider_unavailable. Тіло й повідомлення винятку провайдера в відповідь не потрапляють.
/// </summary>
public sealed class AiExceptionFilter(TimeProvider clock) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var res = context.HttpContext.Response;
        switch (context.Exception)
        {
            case AiQuotaExceededException q:
                res.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((q.RetryAfter - clock.GetUtcNow()).TotalSeconds)).ToString();
                context.Result = new ObjectResult(new ApiError("ai_quota_exceeded", "Monthly AI limit is exhausted."))
                    { StatusCode = StatusCodes.Status429TooManyRequests };
                break;
            case AiRateLimitedException:
                res.Headers.RetryAfter = "60";
                context.Result = new ObjectResult(new ApiError("ai_rate_limited", "Too many AI requests. Try again later."))
                    { StatusCode = StatusCodes.Status429TooManyRequests };
                break;
            case AiUnavailableException u:
                context.Result = new ConflictObjectResult(new { code = "ai_unavailable", message = "AI is unavailable for this company.", reason = u.Reason });
                break;
            case AiProviderException:
                context.Result = new ObjectResult(new ApiError("provider_unavailable", "The AI provider is unavailable."))
                    { StatusCode = StatusCodes.Status502BadGateway };
                break;
            default:
                return;
        }
        context.ExceptionHandled = true;
    }
}

/// <summary>Swagger: поле apiKey - writeOnly з прикладом-заглушкою (ключів у прикладах немає).</summary>
public sealed class AiSecretSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(AiKeySaveRequest) && context.Type != typeof(AiKeyTestRequest)) return;
        if (!schema.Properties.TryGetValue("apiKey", out var p)) return;
        p.WriteOnly = true;
        p.Example = new OpenApiString("<your-api-key>");
    }
}

public static class AiApiSetup
{
    public static IServiceCollection AddBeautyAiApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new AiKeyThrottle(
            Math.Max(1, config.GetValue("Ai:KeyRateLimit:PermitLimit", 5)),
            TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Ai:KeyRateLimit:WindowSeconds", 600)))));
        services.Configure<MvcOptions>(o => o.Filters.Add<AiExceptionFilter>());
        services.ConfigureSwaggerGen(o => o.SchemaFilter<AiSecretSchemaFilter>());
        return services;
    }

    /// <summary>Cache-Control: no-store на /ai/settings*, /ai/models, /ai/usage - навіть для 401/403/429 (контракт §20.5).</summary>
    public static IApplicationBuilder UseBeautyAiNoStore(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var p = ctx.Request.Path;
        if (p.StartsWithSegments("/api/beauty/ai/settings") || p.StartsWithSegments("/api/beauty/ai/models") || p.StartsWithSegments("/api/beauty/ai/usage"))
            ctx.Response.Headers.CacheControl = "no-store";
        await next();
    });
}
