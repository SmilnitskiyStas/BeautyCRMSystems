using Npgsql;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Проби для оркестратора (TASK-800). Без автентифікації, без tenant-контексту, без деталей у відповіді
/// (ні версій, ні рядків підключення, ні тексту винятків).
/// <c>/healthz</c> - liveness, не торкається залежностей. <c>/readyz</c> - readiness: один <c>SELECT 1</c> до БД
/// з рядком <c>ConnectionStrings:Default</c> (runtime-роль) і жорстким таймаутом.
/// </summary>
public static class HealthEndpoints
{
    public const string LivePath = "/healthz";
    public const string ReadyPath = "/readyz";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);

    public static IEndpointRouteBuilder MapBeautyHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet(LivePath, () => Results.Text("ok", "text/plain")).AllowAnonymous();
        app.MapGet(ReadyPath, CheckReadyAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> CheckReadyAsync(IConfiguration config, ILoggerFactory logs, CancellationToken ct)
    {
        var cs = config.GetConnectionString(BeautyCrm.Infrastructure.Data.DataServiceExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(cs)) return Unavailable();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReadyTimeout);
            var builder = new NpgsqlConnectionStringBuilder(cs) { Timeout = 3, CommandTimeout = 3 };
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(timeout.Token);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(timeout.Token);
            return Results.Text("ok", "text/plain");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Лише тип винятку: текст може містити хост/ім'я БД.
            logs.CreateLogger("Readiness").LogWarning("readyz_failed reason={Reason}", ex.GetType().Name);
            return Unavailable();
        }
    }

    private static IResult Unavailable() => Results.Text("unavailable", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
}
