using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

public static class ChannelsServiceExtensions
{
    /// <param name="useMocks">true (default during development): Mock adapters for Telegram/Instagram; false: real ones.</param>
    /// <remarks>
    /// Ports (Wave B): IChannelCredentialsProvider (ambient, per webhook/outbox call), IChannelMessageRepository (EF),
    /// IChannelQueue (deferred: DB is the source of truth, no Redis producer). Registered with TryAdd so they can be replaced.
    /// </remarks>
    public static IServiceCollection AddBeautyChannels(this IServiceCollection services, bool useMocks = true)
    {
        if (useMocks)
        {
            services.AddSingleton<IChannelAdapter>(sp => new MockTelegramAdapter(sp.GetService<IChannelCredentialsProvider>()));
            services.AddSingleton<IChannelAdapter>(sp => new MockInstagramAdapter(sp.GetService<IChannelCredentialsProvider>()));
        }
        else
        {
            services.AddSingleton<IChannelAdapter>(sp => new TelegramAdapter(new HttpClient(), sp.GetRequiredService<IChannelCredentialsProvider>()));
            services.AddSingleton<IChannelAdapter>(sp => new InstagramAdapter(new HttpClient(), sp.GetRequiredService<IChannelCredentialsProvider>()));
        }
        services.AddSingleton<IChannelAdapter, ViberAdapter>();
        services.AddSingleton<IChannelAdapter, WhatsAppAdapter>();
        services.AddSingleton<IChannelAdapter, MessengerAdapter>();
        services.AddSingleton<IChannelAdapter, WidgetAdapter>();
        services.AddSingleton<ChannelRegistry>();
        services.TryAddSingleton<ChannelRequestContext>();
        services.TryAddSingleton<IChannelCredentialsProvider, AmbientChannelCredentialsProvider>();
        services.TryAddScoped<IChannelMessageRepository, EfChannelMessageRepository>();
        services.TryAddScoped<IChannelQueue, DeferredChannelQueue>();
        services.TryAddScoped<ChannelDirectory>();
        services.TryAddScoped<WebhookIngestService>();
        services.TryAddScoped<OutboundDispatcher>();
        services.AddScoped<InboundWebhookService>();
        services.AddScoped<OutboxProcessor>();
        return services;
    }
}
