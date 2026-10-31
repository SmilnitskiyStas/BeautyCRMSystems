using BeautyCrm.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Tenancy;

/// <summary>
/// TASK-777 (§26.2): staff API маркетингу (кампанії, купони) вимкнено, доки <c>Marketing:Enabled</c> не true (за замовчуванням false)
/// -> 403 <c>marketing_disabled</c>. Для відкликання/зниження згоди НЕ застосовується (воно працює завжди).
/// Ставити після <see cref="RequireModuleAttribute"/> (модуль beauty_marketing): порядок фільтрів один і той самий, Order = 10.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireMarketingEnabledAttribute : Attribute, IActionFilter, IOrderedFilter
{
    public const string Code = "marketing_disabled";
    public int Order => 10;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (config.GetValue<bool>("Marketing:Enabled")) return;
        context.Result = new ObjectResult(new ApiError(Code, "Marketing is not enabled."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
