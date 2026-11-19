using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Infrastructure.AI.Secrets;

public static class AiSecretsServiceExtensions
{
    /// <summary>Keyring + протектор + команда перешифрування. Не кидає на старті: недоступність виявляється при використанні.</summary>
    public static IServiceCollection AddBeautyAiSecrets(this IServiceCollection services)
    {
        services.TryAddSingleton<AiSecretKeyring>();
        services.TryAddSingleton<IAiSecretProtector, AesGcmAiSecretProtector>();
        services.TryAddSingleton<IAiSecretRowStore, NpgsqlAiSecretRowStore>();
        services.TryAddTransient<AiSecretReencryptor>();
        return services;
    }
}
