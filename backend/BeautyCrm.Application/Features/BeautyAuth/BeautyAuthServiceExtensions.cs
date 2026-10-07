using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Application.Features.BeautyAuth;

public static class BeautyAuthServiceExtensions
{
    /// <summary>Сервіси автентифікації. Порти (IAuthStore, IPasswordHasher, ITokenIssuer) реєструють Infrastructure/Api.</summary>
    public static IServiceCollection AddBeautyAuthApplication(this IServiceCollection services, AuthOptions? options = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new AuthOptions());
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<PlatformTenantService>();
        services.AddScoped<AppointmentAccessService>();
        return services;
    }
}
