using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

public static class ChatButtonsServiceExtensions
{
    /// <summary>
    /// Сервіси інлайн-кнопок (TASK-752, §23): клавіатури, перевірка callback, дії клієнта. Потребує <c>AddBeautyChatBookingApplication</c>
    /// і <c>AddBeautyApplication</c> (CancellationService) - їх резолвлять ліниво, тож реєстрація безпечна й без них. Кнопки діють лише за
    /// <c>ChatButtons:Enabled=true</c> (за замовч. false).
    /// </summary>
    public static IServiceCollection AddBeautyChatButtons(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => ChatButtonsOptions.Read(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<ChatCallbackRateLimiter>();
        services.TryAddScoped<ChatKeyboardService>();
        services.TryAddScoped<ClientChatAppointments>();
        services.TryAddScoped<ChatButtonActions>();
        return services;
    }
}
