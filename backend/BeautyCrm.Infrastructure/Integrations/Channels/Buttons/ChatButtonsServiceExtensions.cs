using BeautyCrm.Application.Features.BeautyChatFlow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

/// <summary>Читає <c>ChatFlow:*</c> (§28.2). Значення поза межами -> типові (як <c>ChatButtons:*</c>), а не помилка старту.</summary>
public static class ChatFlowOptionsReader
{
    public static ChatFlowOptions Read(IConfiguration c, ChatButtonsOptions buttons)
    {
        static int I(IConfiguration c, string key, int def, int min, int max) =>
            int.TryParse(c[key], out var v) && v >= min && v <= max ? v : def;
        static bool B(IConfiguration c, string key, bool def) => bool.TryParse(c[key], out var v) ? v : def;
        return new ChatFlowOptions
        {
            Enabled = B(c, "ChatFlow:Enabled", false),
            OfferLastLocation = B(c, "ChatFlow:OfferLastLocation", true),
            IdleTimeoutMinutes = I(c, "ChatFlow:IdleTimeoutMinutes", 30, 5, 240),
            PageSize = I(c, "ChatFlow:PageSize", 5, 2, 6),
            DayPageSize = I(c, "ChatFlow:DayPageSize", 6, 2, 8),
            MaxScanDays = I(c, "ChatFlow:MaxScanDays", 14, 1, 60),
            ResendStepAfterLlm = B(c, "ChatFlow:ResendStepAfterLlm", true),
            LlmPrepareWhenFlow = B(c, "ChatFlow:LlmPrepareWhenFlow", false),
            GeoEnabled = B(c, "ChatFlow:GeoEnabled", false),
            TransientLocationTtlSeconds = I(c, "ChatFlow:TransientLocationTtlSeconds", 300, 30, 900),
            FinishedRetentionDays = I(c, "ChatFlow:FinishedRetentionDays", 30, 1, 365),
            SlotButtons = buttons.SlotButtons,
        };
    }
}

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
        services.TryAddSingleton(sp => ChatFlowOptionsReader.Read(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ChatButtonsOptions>()));
        services.TryAddSingleton<ChatCallbackRateLimiter>();
        services.TryAddScoped<ChatKeyboardService>();
        services.TryAddScoped<ClientChatAppointments>();
        services.TryAddScoped<ChatButtonActions>();
        return services;
    }
}
