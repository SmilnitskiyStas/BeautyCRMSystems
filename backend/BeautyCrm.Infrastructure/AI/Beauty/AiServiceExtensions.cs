using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.AI.Beauty;

// Зона ai-agent-developer: AI-клієнт, tools, режими автономності.
// Порти (IFindFreeSlotsPort тощо) і IAiActionJournal реєструє Application/Data; тут — лише AI-шар.
public static class AiServiceExtensions
{
    public static IServiceCollection AddBeautyAi(this IServiceCollection services)
    {
        services.TryAddSingleton(new AiSettings()); // DefaultMode = Confirm
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.TryAddSingleton<IAiClient>(sp =>
            new AnthropicAiClient(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<AiSettings>()));
        // Порти й журнал (Хвиля B): над сервісами Application / БД.
        services.TryAddScoped<IFindFreeSlotsPort, FindFreeSlotsAdapter>();
        services.TryAddScoped<ICreateAppointmentPort, CreateAppointmentAdapter>();
        services.TryAddScoped<IGetPricesPort, GetPricesAdapter>();
        services.TryAddScoped<IGetActivePromotionsPort, GetActivePromotionsAdapter>();
        services.TryAddScoped<IGetClientContextPort, GetClientContextAdapter>();
        services.TryAddScoped<IDraftReplyPort, DraftReplyAdapter>();
        services.TryAddScoped<ISuggestPromotionPort, SuggestPromotionAdapter>();
        services.TryAddScoped<IBuildAudiencePort, BuildAudienceAdapter>();
        services.TryAddScoped<EfAiActionJournal>();
        services.TryAddScoped<IAiActionJournal>(sp => sp.GetRequiredService<EfAiActionJournal>());
        services.TryAddScoped<IAiActionReader>(sp => sp.GetRequiredService<EfAiActionJournal>());
        services.TryAddScoped<AiToolExecutor>();
        services.TryAddScoped<BeautyAssistant>();
        return services;
    }
}
