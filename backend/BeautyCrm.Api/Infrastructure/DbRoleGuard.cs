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

        if (!superuser && !bypassRls)
        {
            await CheckCatalogRoleAsync(cs, role, ct);
            return;
        }

        var message = $"Database role '{role}' used by the API is {(superuser ? "SUPERUSER" : "BYPASSRLS")}: row-level security would not apply. " +
                      "Use a dedicated NOSUPERUSER NOBYPASSRLS runtime role in ConnectionStrings:Default and a separate owner role in ConnectionStrings:Migrator.";
        if (!allowPrivileged) throw new InvalidOperationException(message);
        log.LogWarning("{Message} Allowed because {Key}=true in Development.", message, AllowPrivilegedKey);
    }

    public const string CatalogRole = "beautycrm_catalog";

    /// <summary>Exact set of public catalog definer functions (a new one needs a reviewed change here + ADR-022).</summary>
    private static readonly HashSet<string> ExpectedFunctions =
    [
        "beauty_public_resolve_location",
        "beauty_public_nearby_locations",
        "beauty_public_search_locations_by_city",
    ];

    /// <summary>
    /// ADR-022: інваріанти публічного каталогу. Якщо роль <c>beautycrm_catalog</c> існує, вона має бути NOLOGIN,
    /// не superuser, не BYPASSRLS, NOINHERIT, без власних членств; runtime-роль не є її членом (немає шляху <c>SET ROLE</c>);
    /// жоден член не має INHERIT (політики <c>TO beautycrm_catalog</c> діють на всіх, хто успадковує її права); точний набір
    /// функцій <c>beauty_public_*</c> належить їй, SECURITY DEFINER, фіксований search_path, не виконуються для PUBLIC. Роль відсутня (міграція ще не застосована)
    /// — перевірка пропускається. Порушення = помилка старту (будь-яке середовище).
    /// </summary>
    private async Task CheckCatalogRoleAsync(string cs, string appRole, CancellationToken ct)
    {
        var problems = new List<string>();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT rolcanlogin, rolsuper, rolbypassrls, rolinherit, pg_has_role(current_user, rolname, 'MEMBER') " +
            "FROM pg_roles WHERE rolname = @r", conn))
        {
            cmd.Parameters.AddWithValue("r", CatalogRole);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return; // not provisioned yet
            if (r.GetBoolean(0)) problems.Add($"role '{CatalogRole}' must be NOLOGIN");
            if (r.GetBoolean(1)) problems.Add($"role '{CatalogRole}' must not be SUPERUSER");
            if (r.GetBoolean(2)) problems.Add($"role '{CatalogRole}' must not be BYPASSRLS");
            if (r.GetBoolean(3)) problems.Add($"role '{CatalogRole}' must be NOINHERIT");
            if (r.GetBoolean(4)) problems.Add($"runtime role '{appRole}' must not be a member of '{CatalogRole}' (SET ROLE path)");
        }

        // Members with INHERIT: the TO beautycrm_catalog policies apply to every role that inherits its privileges,
        // so an inheriting member (e.g. the migrator) would read the catalog rows directly (M1).
        await using (var cmd = new NpgsqlCommand(
            "SELECT pg_get_userbyid(member) FROM pg_auth_members WHERE roleid = 'beautycrm_catalog'::regrole AND inherit_option", conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                problems.Add($"role '{r.GetString(0)}' is a member of '{CatalogRole}' WITH INHERIT (must be INHERIT FALSE)");
        }

        // The catalog role itself must not be a member of any other role (privilege escalation).
        await using (var cmd = new NpgsqlCommand(
            "SELECT pg_get_userbyid(roleid) FROM pg_auth_members WHERE member = 'beautycrm_catalog'::regrole", conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                problems.Add($"role '{CatalogRole}' must not be a member of '{r.GetString(0)}'");
        }

        var seen = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.proname, pg_get_userbyid(p.proowner), p.prosecdef,
                   has_function_privilege('public', p.oid, 'EXECUTE'),
                   COALESCE(array_to_string(p.proconfig, ','), ''),
                   pg_get_function_identity_arguments(p.oid)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.proname LIKE 'beauty\_public\_%'
            """, conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var fn = r.GetString(0);
                if (!ExpectedFunctions.Contains(fn) || !seen.Add(fn))
                {
                    problems.Add($"unexpected function {fn}({r.GetString(5)}) in the beauty_public_* namespace");
                    continue;
                }
                if (r.GetString(1) != CatalogRole) problems.Add($"function {fn} must be owned by '{CatalogRole}'");
                if (!r.GetBoolean(2)) problems.Add($"function {fn} must be SECURITY DEFINER");
                if (r.GetBoolean(3)) problems.Add($"function {fn} must not be executable by PUBLIC");
                if (r.GetString(4) != "search_path=pg_catalog, pg_temp")
                    problems.Add($"function {fn} must have exactly SET search_path = pg_catalog, pg_temp (found '{r.GetString(4)}')");
            }
        }
        foreach (var missing in ExpectedFunctions.Where(f => !seen.Contains(f)))
            problems.Add($"function {missing} is missing");

        if (problems.Count > 0)
            throw new InvalidOperationException("Public catalog role invariants violated (ADR-022): " + string.Join("; ", problems) + ".");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class DbRoleGuardExtensions
{
    public static IServiceCollection AddBeautyDbRoleGuard(this IServiceCollection services) =>
        services.AddHostedService<DbRoleGuard>();
}
