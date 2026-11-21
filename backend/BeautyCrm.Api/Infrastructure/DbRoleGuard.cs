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
            await CheckIdentityIsolationAsync(cs, role, ct);
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

    // ---------- ADR-024 / контракт §31.16, чекліст F-8 п.3: ізоляція глобальної ідентичності ----------

    public const string IdentityRole = "beautycrm_identity";
    public const string IdentityReleaseRole = "beautycrm_identity_release";
    /// <summary>Роль воркера (grants.sql / setup-db.ps1): перевіряється, лише якщо існує в кластері.</summary>
    public const string WorkerRole = "beautycrm_worker";

    /// <summary>Глобальні deny-all таблиці: runtime-роль не має на них жодних прав (доступ лише через функції auth_*).</summary>
    public static readonly string[] IdentityTables = ["identities", "auth_login_tickets", "auth_password_resets", "auth_login_attempts"];

    /// <summary>Матриця EXECUTE (§31.16): що має runtime-роль (beautycrm_app). Решта auth_* їй недоступні.</summary>
    public static readonly string[] IdentityAppFunctions =
    [
        "auth_identity_find", "auth_login_attempt_is_locked", "auth_login_attempt_failed", "auth_login_attempt_reset",
        "auth_identity_login_succeeded", "auth_identity_memberships", "auth_identity_is_active", "auth_identity_email_in_use",
        "auth_identity_create", "auth_identity_create_pending", "auth_identity_activate_pending",
        "auth_login_ticket_issue", "auth_login_ticket_consume", "auth_password_reset_issue", "auth_password_reset_consume",
    ];

    /// <summary>Лише воркер (доставка листів скидання, очищення): runtime-ролі API EXECUTE на них не потрібен і не дозволений.</summary>
    public static readonly string[] IdentityWorkerFunctions =
    [
        "auth_password_reset_claim_delivery", "auth_password_reset_attach_token", "auth_password_reset_mark_delivery", "auth_identity_purge_expired",
    ];

    /// <summary>Лише ops/мігратор за runbook (human gate): звільнення пошти.</summary>
    public static readonly string[] IdentityOpsFunctions = ["auth_identity_release"];

    private const string IdentityGuardFunction = "users_identity_email_guard";

    /// <summary>
    /// Runtime-роль не має прямих прав на глобальні таблиці ідентичності, не має CREATE на схемі public, має EXECUTE рівно за матрицею,
    /// а функції auth_* належать ролям ідентичності (SECURITY DEFINER, фіксований search_path, не виконуються для PUBLIC). Ролі ідентичності
    /// NOLOGIN/NOINHERIT і не досяжні через SET ROLE. Таблиці під FORCE RLS. Міграція ще не застосована (таблиці identities немає)
    /// - перевірка пропускається. Порушення = помилка старту, окрім Development (там попередження).
    /// </summary>
    private async Task CheckIdentityIsolationAsync(string cs, string appRole, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using (var probe = new NpgsqlCommand("SELECT to_regclass('public.identities') IS NOT NULL", conn))
            if (!(bool)(await probe.ExecuteScalarAsync(ct))!) return; // not migrated yet

        var problems = new List<string>();

        foreach (var table in IdentityTables)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT has_table_privilege(current_user, @t, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'), " +
                "       COALESCE((SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = @t::regclass), false)", conn);
            cmd.Parameters.AddWithValue("t", "public." + table);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            if (r.GetBoolean(0)) problems.Add($"runtime role '{appRole}' must have no privileges on {table} (deny-all; access only via auth_* functions)");
            if (!r.GetBoolean(1)) problems.Add($"table {table} must have ENABLE + FORCE ROW LEVEL SECURITY");
        }

        // R-2: колонкові гранти (has_table_privilege їх не бачить) для runtime-ролі і, якщо існує, ролі воркера
        var workerExists = false;
        await using (var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @w)", conn))
        {
            cmd.Parameters.AddWithValue("w", WorkerRole);
            workerExists = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var grantees = workerExists ? new[] { appRole, WorkerRole } : [appRole];
        foreach (var grantee in grantees)
        foreach (var table in IdentityTables)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT has_any_column_privilege(@g::name, @t::text, 'SELECT,INSERT,UPDATE,REFERENCES')", conn);
            cmd.Parameters.AddWithValue("g", grantee);
            cmd.Parameters.AddWithValue("t", "public." + table);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
                problems.Add($"role '{grantee}' must have no column privileges on {table} (deny-all; access only via auth_* functions)");
        }

        // R-2: політики RLS на глобальних таблицях адресовані лише ролям ідентичності (не PUBLIC / OID 0, не app / воркеру / іншим)
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT c.relname, p.polname
            FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
            WHERE c.relnamespace = 'public'::regnamespace AND c.relname = ANY(@tables)
              AND EXISTS (SELECT 1 FROM unnest(p.polroles) AS r(oid)
                          WHERE r.oid NOT IN (SELECT oid FROM pg_roles WHERE rolname = ANY(@idRoles)))
            """, conn))
        {
            cmd.Parameters.AddWithValue("tables", IdentityTables);
            cmd.Parameters.AddWithValue("idRoles", new[] { IdentityRole, IdentityReleaseRole });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                problems.Add($"policy {r.GetString(1)} on {r.GetString(0)} must apply only to '{IdentityRole}' / '{IdentityReleaseRole}' (not PUBLIC or other roles)");
        }

        await using (var cmd = new NpgsqlCommand("SELECT has_schema_privilege(current_user, 'public', 'CREATE')", conn))
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) problems.Add($"runtime role '{appRole}' must not have CREATE on schema public");

        foreach (var idRole in new[] { IdentityRole, IdentityReleaseRole })
        {
            await using (var cmd = new NpgsqlCommand(
                "SELECT rolcanlogin, rolsuper, rolbypassrls, rolinherit, pg_has_role(current_user, rolname, 'MEMBER') FROM pg_roles WHERE rolname = @r", conn))
            {
                cmd.Parameters.AddWithValue("r", idRole);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) { problems.Add($"role '{idRole}' is missing"); continue; }
                if (r.GetBoolean(0)) problems.Add($"role '{idRole}' must be NOLOGIN");
                if (r.GetBoolean(1)) problems.Add($"role '{idRole}' must not be SUPERUSER");
                if (r.GetBoolean(2)) problems.Add($"role '{idRole}' must not be BYPASSRLS");
                if (r.GetBoolean(3)) problems.Add($"role '{idRole}' must be NOINHERIT");
                if (r.GetBoolean(4)) problems.Add($"runtime role '{appRole}' must not be a member of '{idRole}' (SET ROLE path)");
            }
            // R-2: роль ідентичності сама не член жодної ролі (інакше успадкувала б чужі права / політики)
            await using (var cmd = new NpgsqlCommand(
                "SELECT pg_get_userbyid(roleid) FROM pg_auth_members WHERE member = @r::regrole", conn))
            {
                cmd.Parameters.AddWithValue("r", idRole);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    problems.Add($"role '{idRole}' must not be a member of '{r.GetString(0)}'");
            }
            // політики TO ця роль діють на кожного, хто її успадковує: жоден член не має INHERIT
            await using (var cmd = new NpgsqlCommand(
                "SELECT pg_get_userbyid(member) FROM pg_auth_members WHERE roleid = @r::regrole AND inherit_option", conn))
            {
                cmd.Parameters.AddWithValue("r", idRole);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    problems.Add($"role '{r.GetString(0)}' is a member of '{idRole}' WITH INHERIT (must be INHERIT FALSE)");
            }
        }

        var expected = IdentityAppFunctions.Concat(IdentityWorkerFunctions).Concat(IdentityOpsFunctions).ToHashSet();
        var seen = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.proname, pg_get_userbyid(p.proowner), p.prosecdef,
                   has_function_privilege('public', p.oid, 'EXECUTE'),
                   has_function_privilege(current_user, p.oid, 'EXECUTE'),
                   COALESCE(array_to_string(p.proconfig, ','), ''),
                   pg_get_function_identity_arguments(p.oid)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND (p.proname LIKE 'auth\_%' OR p.proname = 'users_identity_email_guard')
            """, conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var fn = r.GetString(0);
                if (fn == IdentityGuardFunction)
                {
                    if (r.GetBoolean(3) || r.GetBoolean(4)) problems.Add($"function {fn} must not be executable by PUBLIC or the runtime role");
                    continue;
                }
                if (!expected.Contains(fn) || !seen.Add(fn))
                {
                    problems.Add($"unexpected function {fn}({r.GetString(6)}) in the auth_* namespace (new ones need a reviewed change here + ADR-024)");
                    continue;
                }
                var owner = IdentityOpsFunctions.Contains(fn) ? IdentityReleaseRole : IdentityRole;
                if (r.GetString(1) != owner) problems.Add($"function {fn} must be owned by '{owner}'");
                if (!r.GetBoolean(2)) problems.Add($"function {fn} must be SECURITY DEFINER");
                if (r.GetBoolean(3)) problems.Add($"function {fn} must not be executable by PUBLIC");
                if (r.GetString(5) != "search_path=pg_catalog, pg_temp")
                    problems.Add($"function {fn} must have exactly SET search_path = pg_catalog, pg_temp (found '{r.GetString(5)}')");
                var runtimeMayExecute = IdentityAppFunctions.Contains(fn);
                if (runtimeMayExecute && !r.GetBoolean(4)) problems.Add($"runtime role '{appRole}' must be able to EXECUTE {fn}");
                if (!runtimeMayExecute && r.GetBoolean(4)) problems.Add($"runtime role '{appRole}' must not EXECUTE {fn} (worker/ops only)");
            }
        }
        foreach (var missing in expected.Where(f => !seen.Contains(f)))
            problems.Add($"function {missing} is missing");

        if (problems.Count == 0) return;
        var message = "Global identity isolation invariants violated (ADR-024, contract section 31.16 F-8): " + string.Join("; ", problems) +
                      ". Re-apply deploy/staging/db/grants.sql (or scripts/dev/setup-db.ps1) after the migration.";
        if (env.IsDevelopment()) log.LogWarning("{Message}", message);
        else throw new InvalidOperationException(message);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class DbRoleGuardExtensions
{
    public static IServiceCollection AddBeautyDbRoleGuard(this IServiceCollection services) =>
        services.AddHostedService<DbRoleGuard>();
}
