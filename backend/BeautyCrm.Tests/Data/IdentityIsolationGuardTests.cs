using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Tests.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>TASK-862 (F-8 п.3): DbRoleGuard відхиляє відхилення від матриці ізоляції глобальної ідентичності (ADR-024).</summary>
public sealed class IdentityIsolationGuardTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public IdentityIsolationGuardTests(PostgresRlsFixture db) => _db = db;

    private static async Task Exec(string cs, string sql)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task StartGuard(string env = "Production") =>
        new DbRoleGuard(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = _db.AppConnectionString }).Build(),
            new TestEnv(env), NullLogger<DbRoleGuard>.Instance).StartAsync(default);

    private async Task Rejects(string setup, string teardown, string fragment)
    {
        Skip.If(_db.SkipReason is not null, _db.SkipReason);
        await StartGuard(); // baseline
        await Exec(_db.AdminConnectionString, setup);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard());
            Assert.Contains(fragment, ex.Message);
            await StartGuard("Development"); // у Development лише попередження
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Exec(_db.AdminConnectionString, teardown);
        }
        await StartGuard();
    }

    private string App => new NpgsqlConnectionStringBuilder(_db.AppConnectionString).Username!;

    [SkippableFact] public Task Rejects_direct_table_privilege() =>
        Rejects($"GRANT SELECT ON identities TO {App}", $"REVOKE ALL ON identities FROM {App}", "no privileges on identities");

    [SkippableFact] public Task Rejects_create_on_public() =>
        Rejects($"GRANT CREATE ON SCHEMA public TO {App}", $"REVOKE CREATE ON SCHEMA public FROM {App}", "CREATE on schema public");

    [SkippableFact] public Task Rejects_execute_of_ops_function() =>
        Rejects($"GRANT EXECUTE ON FUNCTION public.auth_identity_release TO {App}", $"REVOKE ALL ON FUNCTION public.auth_identity_release FROM {App}", "must not EXECUTE auth_identity_release");

    [SkippableFact] public Task Rejects_public_execute() =>
        Rejects("GRANT EXECUTE ON FUNCTION public.auth_identity_find TO PUBLIC", "REVOKE ALL ON FUNCTION public.auth_identity_find FROM PUBLIC", "executable by PUBLIC");

    [SkippableFact] public Task Rejects_missing_execute() =>
        Rejects($"REVOKE ALL ON FUNCTION public.auth_identity_find FROM {App}", $"GRANT EXECUTE ON FUNCTION public.auth_identity_find TO {App}", "must be able to EXECUTE auth_identity_find");

    [SkippableFact] public Task Rejects_search_path() =>
        Rejects("ALTER FUNCTION public.auth_identity_find(text) RESET search_path", "ALTER FUNCTION public.auth_identity_find(text) SET search_path = pg_catalog, pg_temp", "search_path");

    [SkippableFact] public Task Rejects_unexpected_function() =>
        Rejects("CREATE FUNCTION public.auth_rogue() RETURNS int LANGUAGE sql AS 'SELECT 1'", "DROP FUNCTION public.auth_rogue()", "unexpected function auth_rogue");

    [SkippableFact] public Task Rejects_role_membership() =>
        Rejects($"GRANT beautycrm_identity TO {App}", $"REVOKE beautycrm_identity FROM {App}", "SET ROLE path");

    // ---- R-2: колонкові гранти, членство ролей ідентичності, політики не для ролей ідентичності ----

    [SkippableFact] public Task Rejects_column_privilege_of_runtime_role() =>
        Rejects($"GRANT SELECT (email) ON identities TO {App}", $"REVOKE ALL (email) ON identities FROM {App}", "no column privileges on identities");

    [SkippableFact] public Task Rejects_column_update_privilege_on_another_global_table() =>
        Rejects($"GRANT UPDATE (consumed_at) ON auth_login_tickets TO {App}", $"REVOKE ALL (consumed_at) ON auth_login_tickets FROM {App}", "no column privileges on auth_login_tickets");

    [SkippableFact]
    public async Task Rejects_column_privilege_of_worker_role_when_it_exists()
    {
        Skip.If(_db.SkipReason is not null, _db.SkipReason);
        await Exec(_db.AdminConnectionString,
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_worker') THEN CREATE ROLE beautycrm_worker NOLOGIN; END IF; END $$");
        await Rejects("GRANT SELECT (email) ON identities TO beautycrm_worker", "REVOKE ALL (email) ON identities FROM beautycrm_worker",
            "role 'beautycrm_worker' must have no column privileges on identities");
    }

    [SkippableTheory]
    [InlineData("beautycrm_identity")]
    [InlineData("beautycrm_identity_release")]
    public Task Rejects_identity_role_that_is_a_member_of_another_role(string idRole) =>
        Rejects($"CREATE ROLE guard_probe_parent NOLOGIN; GRANT guard_probe_parent TO {idRole}",
                $"REVOKE guard_probe_parent FROM {idRole}; DROP ROLE guard_probe_parent",
                $"role '{idRole}' must not be a member of 'guard_probe_parent'");

    [SkippableFact] public Task Rejects_policy_addressed_to_public() =>
        Rejects("CREATE POLICY guard_probe ON identities FOR SELECT USING (true)", "DROP POLICY guard_probe ON identities",
            "policy guard_probe on identities must apply only to");

    [SkippableFact] public Task Rejects_policy_addressed_to_the_runtime_role() =>
        Rejects($"CREATE POLICY guard_probe_app ON auth_login_attempts FOR ALL TO {App} USING (true)", "DROP POLICY guard_probe_app ON auth_login_attempts",
            "policy guard_probe_app on auth_login_attempts must apply only to");

    [SkippableFact] public Task Rejects_policy_mixing_an_identity_role_with_another_role() =>
        Rejects($"CREATE POLICY guard_probe_mix ON auth_password_resets FOR SELECT TO beautycrm_identity, {App} USING (true)", "DROP POLICY guard_probe_mix ON auth_password_resets",
            "policy guard_probe_mix on auth_password_resets must apply only to");

    [SkippableFact] public Task Rejects_no_force_rls() =>
        Rejects("ALTER TABLE identities NO FORCE ROW LEVEL SECURITY", "ALTER TABLE identities FORCE ROW LEVEL SECURITY", "FORCE ROW LEVEL SECURITY");
}
