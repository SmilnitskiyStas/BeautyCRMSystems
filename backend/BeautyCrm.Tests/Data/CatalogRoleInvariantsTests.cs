using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Tests.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// ADR-011 / security review M1, L1, L2: membership of the migrator in beautycrm_catalog is INHERIT FALSE, SET TRUE;
/// DbRoleGuard rejects every deviation of the catalog-role invariants.
/// </summary>
public sealed class CatalogRoleInvariantsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public CatalogRoleInvariantsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private static async Task Exec(string cs, string sql)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(string cs, string sql)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync();
    }

    private Task StartGuard(string cs) =>
        new DbRoleGuard(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = cs }).Build(),
            new TestEnv("Production"), NullLogger<DbRoleGuard>.Instance).StartAsync(default);

    private async Task AssertGuardRejects(string setup, string teardown, string expectedFragment)
    {
        await Exec(_db.AdminConnectionString, setup);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(_db.AppConnectionString));
            Assert.Contains(expectedFragment, ex.Message);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Exec(_db.AdminConnectionString, teardown);
        }
        await StartGuard(_db.AppConnectionString); // restored state is accepted again
    }

    [SkippableFact]
    public async Task Migrator_membership_is_inherit_false_set_true_and_guard_accepts_the_baseline()
    {
        SkipIfNoDb();
        await StartGuard(_db.AppConnectionString);
        var rows = (long)(await Scalar(_db.AdminConnectionString,
            "SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_catalog' " +
            "WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = current_user) AND m.set_option AND NOT m.inherit_option"))!;
        Assert.Equal(1L, rows);
    }

    [SkippableFact]
    public async Task Guard_rejects_any_member_of_catalog_role_with_inherit()
    {
        SkipIfNoDb();
        var role = "beauty_inh_" + Guid.NewGuid().ToString("N")[..10];
        await AssertGuardRejects(
            $"CREATE ROLE {role} NOLOGIN; GRANT beautycrm_catalog TO {role} WITH INHERIT TRUE",
            $"REVOKE beautycrm_catalog FROM {role}; DROP ROLE {role}",
            "WITH INHERIT");
        // INHERIT FALSE members are fine
        await Exec(_db.AdminConnectionString, $"CREATE ROLE {role} NOLOGIN; GRANT beautycrm_catalog TO {role} WITH INHERIT FALSE, SET TRUE");
        try { await StartGuard(_db.AppConnectionString); }
        finally { await Exec(_db.AdminConnectionString, $"REVOKE beautycrm_catalog FROM {role}; DROP ROLE {role}"); }
    }

    [SkippableFact]
    public async Task Guard_rejects_inheriting_catalog_role()
    {
        SkipIfNoDb();
        await AssertGuardRejects("ALTER ROLE beautycrm_catalog INHERIT", "ALTER ROLE beautycrm_catalog NOINHERIT", "NOINHERIT");
    }

    [SkippableFact]
    public async Task Guard_rejects_catalog_role_that_is_a_member_of_another_role()
    {
        SkipIfNoDb();
        var role = "beauty_parent_" + Guid.NewGuid().ToString("N")[..10];
        await AssertGuardRejects(
            $"CREATE ROLE {role} NOLOGIN; GRANT {role} TO beautycrm_catalog",
            $"REVOKE {role} FROM beautycrm_catalog; DROP ROLE {role}",
            "must not be a member of");
    }

    [SkippableFact]
    public async Task Guard_rejects_unexpected_function_and_missing_search_path()
    {
        SkipIfNoDb();
        await AssertGuardRejects(
            "CREATE FUNCTION public.beauty_public_extra() RETURNS int LANGUAGE sql AS 'SELECT 1'",
            "DROP FUNCTION public.beauty_public_extra()",
            "unexpected function beauty_public_extra");
        await AssertGuardRejects(
            "ALTER FUNCTION public.beauty_public_resolve_location(text) RESET search_path",
            "ALTER FUNCTION public.beauty_public_resolve_location(text) SET search_path = pg_catalog, pg_temp",
            "search_path");
        await AssertGuardRejects(
            "ALTER FUNCTION public.beauty_public_resolve_location(text) SET search_path = public",
            "ALTER FUNCTION public.beauty_public_resolve_location(text) SET search_path = pg_catalog, pg_temp",
            "search_path");
        await AssertGuardRejects(
            "ALTER FUNCTION public.beauty_public_resolve_location(text) RENAME TO beauty_public_renamed",
            "ALTER FUNCTION public.beauty_public_renamed(text) RENAME TO beauty_public_resolve_location",
            "beauty_public_resolve_location is missing");
    }

    /// <summary>
    /// L1: a real NOSUPERUSER migrator with CREATEROLE (the "role does not exist yet" path) in a fresh cluster.
    /// The migration creates beautycrm_catalog, the creator gets ADMIN, the migration re-grants itself INHERIT FALSE, SET TRUE,
    /// ownership is handed over (needs the SET option) and DbRoleGuard accepts the result.
    /// </summary>
    [SkippableFact]
    public async Task Non_superuser_createrole_migrator_provisions_catalog_role_with_inherit_false()
    {
        var requireDb = Environment.GetEnvironmentVariable("BEAUTY_TEST_REQUIRE_DB") == "1";
        PostgreSqlContainer container;
        try
        {
            container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) when (!requireDb)
        {
            Skip.If(true, $"No Docker for a fresh PostgreSQL cluster: {ex.GetType().Name}");
            return;
        }

        await using var _ = container;
        var adminCs = container.GetConnectionString();
        const string migrator = "beauty_ci_migrator";
        const string pw = "Pw-migrator-1";
        await Exec(adminCs, $"CREATE ROLE {migrator} LOGIN PASSWORD '{pw}' NOSUPERUSER CREATEROLE NOBYPASSRLS");
        await Exec(adminCs, $"CREATE DATABASE beauty_createrole OWNER {migrator}");
        Assert.Equal(0L, await Scalar(adminCs, "SELECT count(*) FROM pg_roles WHERE rolname = 'beautycrm_catalog'"));

        var migratorCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = "beauty_createrole", Username = migrator, Password = pw }.ConnectionString;
        var opts = new DbContextOptionsBuilder<BeautyDbContext>();
        opts.UseBeautyNpgsql(migratorCs);
        await using (var ctx = new BeautyDbContext(opts.Options, new TenantContext()))
            await ctx.Database.MigrateAsync();

        var checkCs = new NpgsqlConnectionStringBuilder(migratorCs).ConnectionString;
        // the membership of the migrator: SET without INHERIT
        Assert.Equal(1L, await Scalar(checkCs,
            "SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_catalog' " +
            "JOIN pg_roles u ON u.oid = m.member AND u.rolname = current_user WHERE m.set_option AND NOT m.inherit_option"));
        Assert.Equal(0L, await Scalar(checkCs, "SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_catalog' WHERE m.inherit_option"));
        Assert.Equal(3L, await Scalar(checkCs,
            "SELECT count(*) FROM pg_proc p WHERE p.proname LIKE 'beauty\\_public\\_%' AND pg_get_userbyid(p.proowner) = 'beautycrm_catalog'"));
        // USAGE = "inherits the privileges of the role" (what makes the TO beautycrm_catalog policies apply); SET = can SET ROLE
        Assert.False((bool)(await Scalar(checkCs, "SELECT pg_has_role(current_user, 'beautycrm_catalog', 'USAGE')"))!);
        Assert.True((bool)(await Scalar(checkCs, "SELECT pg_has_role(current_user, 'beautycrm_catalog', 'SET')"))!);
    }
}
