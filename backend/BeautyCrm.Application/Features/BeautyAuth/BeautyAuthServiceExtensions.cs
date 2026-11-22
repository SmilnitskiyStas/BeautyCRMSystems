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
        services.TryAddSingleton<LoginDummyHash>(); // F-13: фіктивний bcrypt-хеш того самого cost; Program.cs створює його на старті
        services.TryAddSingleton(sp => new InviteEmailCheckThrottle(
            sp.GetRequiredService<AuthOptions>().InviteEmailCheck.PerTenantPerHour, TimeSpan.FromHours(1), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new PasswordForgotQueue(sp.GetRequiredService<AuthOptions>().PasswordForgot.QueueCapacity));
        services.TryAddSingleton(sp =>
        {
            var f = sp.GetRequiredService<AuthOptions>().PasswordForgot;
            return new PasswordForgotEmailThrottle(f.PerEmailPermit, TimeSpan.FromSeconds(Math.Max(1, f.PerEmailWindowSeconds)), sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<PasswordForgotProcessor>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<AccessAdminService>(); // ADR-023 (TASK-851)
        services.AddScoped<PlatformTenantService>();
        services.AddScoped<BeautyCrm.Application.Features.BeautyControlPlane.ControlPlaneService>();
        services.AddScoped<BeautyCrm.Application.Features.BeautyControlPlane.OwnerInviteService>();
        services.AddScoped<AppointmentAccessService>();
        services.AddScoped<BeautyCrm.Application.Features.BeautyStaff.StaffInviteService>();
        return services;
    }
}
