using BeautyCrm.Api.Auth;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyStaff;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// TASK-696: користувач з роллю specialist, чий профіль майстра деактивовано (або видалено), не діє в записах і відсутностях:
/// 403 specialist_inactive. Деактивація вимикає користувача й відкликає refresh-токени, але вже виданий access-токен живе до
/// кінця терміну, а повторне ввімкнення користувача при неактивному профілі теж не має давати доступу до роботи.
/// Застосовується як [ServiceFilter(typeof(ActiveSpecialistFilter))] на контролерах booking/absences; owner/admin не зачіпає.
/// </summary>
public sealed class ActiveSpecialistFilter(IStaffStore staff) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User.ToActor() is { Role: Roles.Specialist, SpecialistId: { } specialistId }
            && await staff.GetSpecialistLinkAsync(specialistId, context.HttpContext.RequestAborted) is not { IsActive: true })
        {
            context.Result = new ObjectResult(new ApiError("specialist_inactive", "The specialist profile is deactivated."))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }
        await next();
    }
}
