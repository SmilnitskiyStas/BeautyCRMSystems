using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.AI.Beauty.Settings;
using BeautyCrm.Infrastructure.AI.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.AI.Beauty;

// Зона ai-agent-developer: AI-клієнт, tools, режими автономності.
// Порти (IFindFreeSlotsPort тощо) і IAiActionJournal реєструє Application/Data; тут — лише AI-шар.
public static class AiServiceExtensions
{
    public static IServiceCollection AddBeautyAi(this IServiceCollection services)
    {
        // Глобальні параметри виконання (ліміт знижки, години розсилок, ітерації tools) з Ai:* ; режим/модель/ключ — per tenant.
        services.TryAddSingleton(sp => ReadRuntimeOptions(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

        // Keyring майстер-ключів ШІ (TASK-714); ключі tenant читаються через протектор, не з env.
        services.TryAddSingleton<AiSecretKeyring>();
        services.TryAddSingleton<IAiSecretProtector, AesGcmAiSecretProtector>();

        // Провайдери: доступність = адаптер зареєстровано. mock лише за Ai:UseMockProvider (поза Development старт падає у Program.cs).
        services.TryAddSingleton(sp => new AiPlatformOptions(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(sp => AiModelCatalog.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAiProvider, AnthropicAiProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAiProvider, MockAiProvider>());
        services.TryAddSingleton(sp =>
        {
            var useMock = sp.GetRequiredService<AiPlatformOptions>().UseMockProvider;
            return new AiProviderRegistry(sp.GetServices<IAiProvider>().Where(p => useMock || p.Id != AiProviderRegistry.MockId));
        });
        services.TryAddSingleton<AiRateLimiter>();

        // Налаштування tenant, облік, клієнт.
        services.TryAddScoped<IAiTenantSettingsStore, EfAiTenantSettingsStore>();
        services.TryAddScoped<IAiProviderKeyStore, EfAiProviderKeyStore>();
        services.TryAddScoped<IAiUsageStore, EfAiUsageStore>();
        services.TryAddScoped<AiTenantRuntime>();
        services.TryAddScoped<AiSettingsService>();
        services.TryAddScoped<IAiClient, TenantAiClient>();

        // Порти й журнал (Хвиля B): над сервісами Application / БД.
        services.TryAddScoped<IFindFreeSlotsPort, FindFreeSlotsAdapter>();
        services.TryAddScoped<ICreateAppointmentPort, CreateAppointmentAdapter>();
        services.TryAddScoped<IGetPricesPort, GetPricesAdapter>();
        services.TryAddScoped<IGetActivePromotionsPort, GetActivePromotionsAdapter>();
        services.TryAddScoped<IGetClientContextPort, GetClientContextAdapter>();
        services.TryAddScoped<IDraftReplyPort, DraftReplyAdapter>();
        services.TryAddScoped<ISuggestPromotionPort, SuggestPromotionAdapter>();
        services.TryAddScoped<IBuildAudiencePort, BuildAudienceAdapter>();
        // Чат клієнта (TASK-727): порт поверх публічних сервісів (ліниво), вікно історії, tools scope ClientChat.
        services.TryAddSingleton(sp => ReadChatOptions(sp.GetRequiredService<IConfiguration>()));
        services.TryAddScoped<IClientChatPort, ClientChatPortAdapter>();
        services.TryAddScoped<ClientChatToolbox>();
        services.TryAddScoped<IChatPolicyFactsPort, ChatPolicyFactsAdapter>(); // TASK-741: ФАКТИ ЗАКЛАДУ та PolicyClaimGuard
        services.TryAddScoped<ChatContextBuilder>(); // TASK-735: дата/час і каталог закладу в системному промпті ходу
        services.TryAddScoped<EfAiActionJournal>();
        services.TryAddScoped<IAiActionJournal>(sp => sp.GetRequiredService<EfAiActionJournal>());
        services.TryAddScoped<IAiActionReader>(sp => sp.GetRequiredService<EfAiActionJournal>());
        services.TryAddScoped<AiToolExecutor>();
        services.TryAddScoped<BeautyAssistant>();

        // Конвеєр розмов клієнта (TASK-728, §21.5): воркер + sweeper. ChatAssistant:Enabled=false (default) - воркер нічого не робить.
        services.TryAddSingleton(sp => ChatAssistantOptions.Read(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<ChatRateLimiter>();
        services.TryAddSingleton<ConversationSignals>();
        services.TryAddSingleton(sp => TelegramPollingOptions.Read(sp.GetRequiredService<IConfiguration>()));
        services.TryAddScoped<ChatSummaryTracker>();
        services.TryAddScoped<ReminderChoiceApplier>();
        services.TryAddScoped<ReminderStopHandler>();
        services.AddBeautyChatButtons(); // TASK-752 (§23): клавіатури, перевірка callback, дії клієнта
        services.TryAddSingleton(sp => new ChatKeyboardBuilder(sp.GetRequiredService<ChatButtonsOptions>())); // TASK-753: розкладки формує код
        services.TryAddScoped<KeyboardMarkupCleaner>(); // TASK-753: прибирання кнопок (inline + sweeper)
        services.TryAddScoped<ConversationTurnProcessor>();
        services.TryAddSingleton<ConversationWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationWorker>());
        return services;
    }

    private static ChatPromptOptions ReadChatOptions(IConfiguration cfg)
    {
        var o = new ChatPromptOptions();
        if (int.TryParse(cfg["ChatAssistant:HistoryMessages"], out var m)) o.HistoryMessages = m;
        if (int.TryParse(cfg["ChatAssistant:HistoryMaxChars"], out var c)) o.HistoryMaxChars = c;
        if (int.TryParse(cfg["ChatAssistant:MaxToolIterations"], out var it)) o.MaxToolIterations = it;
        if (int.TryParse(cfg["ChatAssistant:ContextMaxServices"], out var cs)) o.ContextMaxServices = cs;
        if (int.TryParse(cfg["ChatAssistant:ContextMaxSpecialists"], out var cp)) o.ContextMaxSpecialists = cp;
        if (!string.IsNullOrWhiteSpace(cfg["ChatAssistant:CurrencyLabel"])) o.CurrencyLabel = cfg["ChatAssistant:CurrencyLabel"]!.Trim();
        return o;
    }

    private static AiSettings ReadRuntimeOptions(IConfiguration cfg)
    {
        var s = new AiSettings();
        if (int.TryParse(cfg["Ai:MaxToolIterations"], out var it) && it is > 0 and <= 20) s.MaxToolIterations = it;
        if (decimal.TryParse(cfg["Ai:MaxDiscountPercent"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 100) s.MaxDiscountPercent = d;
        if (int.TryParse(cfg["Ai:BroadcastFromHour"], out var f) && f is >= 0 and <= 23) s.BroadcastFromHour = f;
        if (int.TryParse(cfg["Ai:BroadcastToHour"], out var t) && t is >= 1 and <= 24) s.BroadcastToHour = t;
        return s;
    }
}
