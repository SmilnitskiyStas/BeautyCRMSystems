using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCatalog;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Application.Features.BeautyClients;
using BeautyCrm.Application.Features.BeautyLocations;
using BeautyCrm.Application.Features.BeautyOverview;
using BeautyCrm.Application.Features.BeautyStaff;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Application.Features;

// Зона backend-developer (Хвиля B): реєстрація сервісів Application.
// Порти даних (IBookingStore, ICatalogStore, ...) реєструє AddBeautyData (Infrastructure).
public static class BeautyServiceExtensions
{
    public static IServiceCollection AddBeautyApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPaymentService, MockPaymentService>(); // реальний провайдер підміняє реєстрацію
        services.AddScoped<CancellationSettingsService>();
        services.AddScoped<BookingService>();
        services.AddScoped<CancellationService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<PromotionService>();
        services.TryAddSingleton(new BeautyCrm.Application.Features.BeautyCoupons.CouponOptions());
        services.AddSingleton<BeautyCrm.Application.Features.BeautyCoupons.CouponAttemptLimiter>(); // TASK-778: лічильники спроб (in-memory)
        services.AddScoped<BeautyCrm.Application.Features.BeautyCoupons.CouponService>();
        services.AddScoped<ClientService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<MyRevenueService>(); // ADR-023 (TASK-852): GET /me/revenue
        services.AddScoped<AnalyticsDetailsService>();
        services.AddScoped<ChannelSettingsService>();
        services.AddScoped<StaffService>();
        services.AddScoped<AbsenceService>();
        services.AddScoped<OverviewService>();
        services.AddScoped<LocationService>();
        services.TryAddSingleton(new BeautyCrm.Application.Features.BeautyInventory.InventoryOptions()); // ADR-025: склад
        services.AddScoped<BeautyCrm.Application.Features.BeautyInventory.InventoryService>();
        services.TryAddSingleton(new BeautyCrm.Application.Features.BeautyProductImages.MediaOptions()); // ADR-027: без RootPath функцію вимкнено; Api підміняє з конфігу
        services.AddScoped<BeautyCrm.Application.Features.BeautyInventory.InventoryNormsService>(); // етап 2a: норми матеріалів (§32.16)
        services.AddScoped<BeautyCrm.Application.Features.BeautyInventory.InventoryAlertsService>(); // етап 2a: сповіщення персоналу (§32.19)
        return services;
    }
}
