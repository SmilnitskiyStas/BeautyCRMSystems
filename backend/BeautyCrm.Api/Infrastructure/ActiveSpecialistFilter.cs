using BeautyCrm.Api.Auth;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// TASK-696 / ADR-023 M7 (контракт §30.5 п.11): користувач, чий прив'язаний профіль майстра деактивовано (або не видно), не діє на
/// жодному tenant-ендпоінті: 403 specialist_inactive. Деактивація вимикає користувача й відкликає refresh-токени, але вже виданий
/// access-токен живе до кінця терміну, а повторне ввімкнення користувача при неактивному профілі теж не має давати доступу.
/// Глобальний action-фільтр (порядок 0, після 422, як раніше). Ознаку бере з <see cref="IAccessContext"/> (її виставляє
/// AccessContextMiddleware), окремого запиту до БД не робить. Винятки: <see cref="AllowInactiveSpecialistAttribute"/>
/// (<c>/auth/me</c>, <c>/access/catalog</c>, <c>/feedback</c>). Без завантаженого контексту (публічні/платформні маршрути) не діє.
/// </summary>
public sealed class ActiveSpecialistFilter(IAccessContext access) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (access is { IsLoaded: true, Access.SpecialistInactive: true }
            && !context.ActionDescriptor.EndpointMetadata.OfType<AllowInactiveSpecialistAttribute>().Any())
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
