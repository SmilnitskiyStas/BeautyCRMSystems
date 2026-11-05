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
        services.AddScoped<BeautyCrm.Application.Features.BeautyControlPlane.IControlPlaneStore, EfControlPlaneStore>();
        services.AddScoped<BeautyCrm.Application.Features.BeautyControlPlane.IOwnerInviteStore, EfOwnerInviteStore>();
        return services;
    }
}
