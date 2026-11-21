using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Api.Auth;

/// <summary>
/// §31.7.1 (F-3): читає чергу forgot і для кожного запису викликає <c>auth_password_reset_issue</c> рівно один раз. Поза синхронним
/// шляхом запиту, тож час відповіді <c>forgot</c> не залежить від наявності пошти. Токен тут не з'являється: його генерує воркер.
/// </summary>
public sealed class PasswordForgotWorker(PasswordForgotQueue queue, PasswordForgotProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var email in queue.Reader.ReadAllAsync(stoppingToken))
                await processor.ProcessAsync(email, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
