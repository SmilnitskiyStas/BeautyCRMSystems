using BeautyCrm.Infrastructure.AI.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Перевірка keyring ключів ШІ на старті (ADR-010 §2.5). НІКОЛИ не зупиняє старт: без keyring або з невалідним ШІ з власними
/// ключами просто недоступний (fail closed при записі/читанні ключа), решта застосунку працює. Логує лише стан і причину,
/// без значень ключів.
/// </summary>
public sealed class AiSecretsStartupCheck(AiSecretKeyring keyring, ILogger<AiSecretsStartupCheck> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        switch (keyring.State)
        {
            case AiSecretsState.Ready:
                log.LogInformation("AI secrets keyring ready ({KeyCount} key(s), active kid '{Kid}').", keyring.KeyCount, keyring.ActiveKeyId);
                break;
            case AiSecretsState.NotConfigured:
                log.LogInformation("AI secrets keyring is not configured (Ai:Secrets:*): own AI provider keys are unavailable (secrets_unavailable).");
                break;
            default:
                log.LogWarning("AI secrets keyring is INVALID: {Problem}. Own AI provider keys are unavailable (secrets_unavailable); the rest of the API is unaffected.", keyring.Problem);
                break;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class AiSecretsStartupExtensions
{
    public static IServiceCollection AddBeautyAiSecretsStartupCheck(this IServiceCollection services) =>
        services.AddBeautyAiSecrets().AddHostedService<AiSecretsStartupCheck>();
}

/// <summary>
/// CLI: <c>dotnet BeautyCrm.Api.dll ai-secrets reencrypt [--tenant &lt;guid&gt;] [--drop-unreadable]</c>.
/// Друкує лише кількості. Код виходу: 0 = усе на активному kid, 1 = лишились нечитані/конфлікти/збої, 2 = помилка запуску.
/// </summary>
public static class AiSecretsCli
{
    public const string Command = "ai-secrets";

    public static async Task<int> RunAsync(IServiceProvider sp, string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] != "reencrypt")
        {
            await output.WriteLineAsync("Usage: ai-secrets reencrypt [--tenant <guid>] [--drop-unreadable]");
            return 2;
        }

        Guid? tenant = null;
        var drop = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--drop-unreadable") drop = true;
            else if (args[i] == "--tenant" && i + 1 < args.Length && Guid.TryParse(args[i + 1], out var g) && g != Guid.Empty) { tenant = g; i++; }
            else
            {
                await output.WriteLineAsync("Unknown or invalid argument. Usage: ai-secrets reencrypt [--tenant <guid>] [--drop-unreadable]");
                return 2;
            }
        }

        try
        {
            var report = await sp.GetRequiredService<AiSecretReencryptor>().RunAsync(new AiReencryptOptions(tenant, drop), CancellationToken.None);
            await output.WriteLineAsync(
                $"tenants={report.Tenants} scanned={report.Scanned} reencrypted={report.Reencrypted} already_current={report.AlreadyCurrent} " +
                $"unreadable={report.Unreadable} dropped={report.Dropped} conflicts={report.Conflicts} failed_tenants={report.FailedTenants} " +
                $"remaining_not_on_active_kid={report.Remaining}");
            return report.Complete ? 0 : 1;
        }
        catch (AiSecretException ex)
        {
            await output.WriteLineAsync($"Re-encryption not started: {ex.Error}.");
            return 2;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Re-encryption failed ({ex.GetType().Name}).");
            return 2;
        }
    }
}
