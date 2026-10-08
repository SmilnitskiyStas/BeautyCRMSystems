using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCatalog;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Application.Features.BeautyClients;
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
        services.AddScoped<ClientService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<ChannelSettingsService>();
        services.AddScoped<StaffService>();
        services.AddScoped<AbsenceService>();
        services.AddScoped<OverviewService>();
        return services;
    }
}
