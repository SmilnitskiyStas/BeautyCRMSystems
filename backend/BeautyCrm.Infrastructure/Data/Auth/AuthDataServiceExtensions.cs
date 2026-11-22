using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.Data.Auth;

public static class AuthDataServiceExtensions
{
    /// <summary>Порти даних автентифікації. Потребує AddBeautyData (DbContext + TenantContext).</summary>
    public static IServiceCollection AddBeautyAuthData(this IServiceCollection services)
    {
        services.TryAddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<IAuthStore, EfAuthStore>();
        services.AddScoped<IIdentityStore, EfIdentityStore>(); // ADR-024: функції auth_* поза RLS tenant
        services.AddSingleton<IPasswordResetStore, EfPasswordResetStore>(); // §31.7: функції auth_password_reset_issue/consume (без стану, singleton для фонового обробника)
        services.AddScoped<IAccessAdminStore, EfAccessAdminStore>(); // ADR-023 (TASK-851)
        services.AddScoped<IAccessStore, EfAccessStore>(); // ADR-023: доступ користувача для AccessContextMiddleware
        services.AddScoped<BeautyCrm.Application.Features.BeautyControlPlane.IControlPlaneStore, EfControlPlaneStore>();
        services.AddScoped<BeautyCrm.Application.Features.BeautyControlPlane.IOwnerInviteStore, EfOwnerInviteStore>();
        return services;
    }
}
