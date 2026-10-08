using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Fail-fast на старті (аудит TASK-682 H1): runtime-роль БД не має бути superuser / BYPASSRLS — інакше RLS не діє
/// і будь-яка помилка в tenant-контексті стає крос-tenant витоком. Перевіряє <c>ConnectionStrings:Default</c>
/// (runtime); міграції виконуються окремою ролею з <c>ConnectionStrings:Migrator</c>.
/// Виняток: Development із явним <c>Beauty:AllowPrivilegedDbRole=true</c> (лише попередження).
/// Також відхиляє Npgsql Multiplexing: app.tenant_id тримається на рівні з'єднання (див. beauty-contracts §14, M4).
/// </summary>
public sealed class DbRoleGuard(IConfiguration config, IHostEnvironment env, ILogger<DbRoleGuard> log) : IHostedService
{
    public const string AllowPrivilegedKey = "Beauty:AllowPrivilegedDbRole";

    public async Task StartAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString(BeautyCrm.Infrastructure.Data.DataServiceExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(cs))
        {
            log.LogWarning("ConnectionStrings:Default is not configured; DB role check skipped (database access will fail).");
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(cs);
        if (builder.Multiplexing)
            throw new InvalidOperationException(
                "Npgsql Multiplexing is not supported: the RLS tenant context (app.tenant_id) is held per connection.");

        var allowPrivileged = env.IsDevelopment() && config.GetValue(AllowPrivilegedKey, false);
        bool superuser, bypassRls;
        string role;
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT rolsuper, rolbypassrls, current_user::text FROM pg_roles WHERE rolname = current_user", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Could not read the current database role from pg_roles.");
            superuser = r.GetBoolean(0);
            bypassRls = r.GetBoolean(1);
            role = r.GetString(2);
        }
        catch (NpgsqlException ex) when (env.IsDevelopment())
        {
            log.LogWarning("Database is not reachable at startup ({Reason}); DB role check skipped in Development.", ex.GetType().Name);
            return;
        }

        if (!superuser && !bypassRls) return;

        var message = $"Database role '{role}' used by the API is {(superuser ? "SUPERUSER" : "BYPASSRLS")}: row-level security would not apply. " +
                      "Use a dedicated NOSUPERUSER NOBYPASSRLS runtime role in ConnectionStrings:Default and a separate owner role in ConnectionStrings:Migrator.";
        if (!allowPrivileged) throw new InvalidOperationException(message);
        log.LogWarning("{Message} Allowed because {Key}=true in Development.", message, AllowPrivilegedKey);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class DbRoleGuardExtensions
{
    public static IServiceCollection AddBeautyDbRoleGuard(this IServiceCollection services) =>
        services.AddHostedService<DbRoleGuard>();
}
