using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCatalog;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Application.Features.BeautyClients;
using BeautyCrm.Application.Features.BeautyOverview;
using BeautyCrm.Application.Features.BeautyStaff;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Infrastructure.Data;

// Зона database-engineer: реєстрація DbContext, RLS-інтерцептора тощо.
public static class DataServiceExtensions
{
    /// <summary>Connection string name: ConnectionStrings:Default (env ConnectionStrings__Default).</summary>
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Registers <see cref="BeautyDbContext"/> (scoped) with the RLS tenant interceptor and a scoped
    /// <see cref="TenantContext"/>. Request/job code must call <c>TenantContext.SetTenant</c> before
    /// the first DB access in the scope. The connection string is resolved lazily (first context
    /// resolution), so the app can start without a DB configured. The DB role used at runtime must
    /// NOT be superuser / BYPASSRLS.
    /// </summary>
    public static IServiceCollection AddBeautyData(this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantConnectionInterceptor>();

        services.AddDbContext<BeautyDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings__{ConnectionStringName}).");

            options.UseBeautyNpgsql(connectionString)
                .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>());
        });

        // Порти даних Application (Хвиля B): один EfBeautyStore на scope реалізує всі.
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddScoped<EfBeautyStore>();
        services.AddScoped<IBookingStore>(sp => sp.GetRequiredService<EfBeautyStore>());
        services.AddScoped<ICatalogStore>(sp => sp.GetRequiredService<EfBeautyStore>());
        services.AddScoped<ICancellationSettingsStore>(sp => sp.GetRequiredService<EfBeautyStore>());
        services.AddScoped<IClientStore>(sp => sp.GetRequiredService<EfBeautyStore>());
        services.AddScoped<IAnalyticsStore>(sp => sp.GetRequiredService<EfBeautyStore>());
        services.AddScoped<IChannelSettingsStore>(sp => sp.GetRequiredService<EfBeautyStore>());
        services.AddScoped<IStaffStore>(sp => sp.GetRequiredService<EfBeautyStore>());
        services.AddScoped<IOverviewStore>(sp => sp.GetRequiredService<EfBeautyStore>());

        return services;
    }

    /// <summary>Shared provider configuration (runtime, design-time, tests).</summary>
    public static DbContextOptionsBuilder UseBeautyNpgsql(this DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(BeautyDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention();
}
