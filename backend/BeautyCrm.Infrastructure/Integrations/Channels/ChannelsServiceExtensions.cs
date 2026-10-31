using BeautyCrm.Infrastructure.Integrations.Channels.Inbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

public static class ChannelsServiceExtensions
{
    /// <param name="useMocks">false (default): real Telegram/Instagram adapters. true: mock adapters; the caller (Program) allows it
    /// ONLY in the Development environment — mocks accept any request that carries the configured mock secret.</param>
    /// <remarks>
    /// Ports (Wave B): IChannelCredentialsProvider (ambient, per webhook/outbox call), IChannelMessageRepository (EF),
    /// IChannelQueue (deferred: DB is the source of truth, no Redis producer). Registered with TryAdd so they can be replaced.
    /// </remarks>
    public static IServiceCollection AddBeautyChannels(this IServiceCollection services, bool useMocks = false)
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
        services.TryAddSingleton<ConversationSignals>(); // сигнал для ConversationWorker (TASK-728); замінює лише-логуючу DeferredChannelQueue
        services.TryAddScoped<IChannelQueue, ChannelSignalQueue>();
        services.TryAddScoped<ChannelDirectory>();
        services.TryAddScoped<WebhookIngestService>();
        services.TryAddScoped<OutboundDispatcher>();
        services.AddScoped<InboundWebhookService>();
        services.TryAddScoped<InboundIngestCore>();
        // Telegram long polling (ADR-011): вимкнений за замовчуванням; старт падає, якщо ввімкнений поза Development або з UseMocks.
        services.TryAddSingleton(sp => TelegramPollingOptions.Read(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(sp => new TelegramPollingHttp(new HttpClient(new SocketsHttpHandler())
        {
            Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<TelegramPollingOptions>().TimeoutSeconds + 10), // без IHttpClientFactory: URL з токеном не логується
        }));
        services.TryAddSingleton<TelegramPoller>();
        services.AddHostedService<TelegramPollingService>();
        services.AddScoped<OutboxProcessor>();
        // Контакт/номер Telegram (TASK-768, §25.4): ChatPhone:*, порт читання з обнулінням для конвеєра, sweeper тимчасових номерів.
        services.TryAddSingleton(sp => ChatPhoneOptions.Read(sp.GetRequiredService<IConfiguration>()));
        services.TryAddScoped<IContactPhoneStore, EfContactPhoneStore>();
        services.TryAddScoped<ContactPhoneSweeper>();
        services.AddHostedService<ContactPhoneSweeperService>();
        // Транспорт інлайн-кнопок Telegram (TASK-750, §23.4): порт для TASK-752/753 і сервіс меню команд для API.
        services.TryAddScoped<IChannelActionsPort, ChannelActionsPort>();
        services.TryAddScoped<TelegramCommandsService>();
        // Inbox розмов (TASK-739, §22.6.5): ліміти Inbox:* ; сервіс використовує IAiActionJournal з AddBeautyAi.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => InboxOptions.Read(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<InboxThrottle>();
        services.TryAddScoped<InboxService>();
        return services;
    }
}
