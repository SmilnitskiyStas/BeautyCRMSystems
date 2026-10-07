using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Real PostgreSQL for RLS tests. Source, in order:
///  1. env BEAUTY_TEST_PG_ADMIN — superuser connection string to an existing server
///     (e.g. "Host=localhost;Username=postgres;Password=..."); a throwaway database is created;
///  2. Testcontainers (postgres:16-alpine) — needs a running Docker daemon.
/// If neither is available the tests are SKIPPED with a reason, unless BEAUTY_TEST_REQUIRE_DB=1
/// (set it in CI) — then the fixture fails.
///
/// Migrations run as the admin (table owner); tests run as a separate LOGIN role that is
/// NOSUPERUSER NOBYPASSRLS — superusers bypass RLS, so testing as postgres would prove nothing.
/// </summary>
public sealed class PostgresRlsFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _adminServerCs;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];

    private string DatabaseName => $"beauty_rls_test_{_suffix}";
    private string AppRole => $"beauty_app_test_{_suffix}";

    public string? SkipReason { get; private set; }
    public string AppConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var requireDb = Environment.GetEnvironmentVariable("BEAUTY_TEST_REQUIRE_DB") == "1";
        try
        {
            _adminServerCs = Environment.GetEnvironmentVariable("BEAUTY_TEST_PG_ADMIN");
            if (string.IsNullOrWhiteSpace(_adminServerCs))
            {
                _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await _container.StartAsync();
                _adminServerCs = _container.GetConnectionString();
            }
        }
        catch (Exception ex) when (!requireDb)
        {
            SkipReason = "No PostgreSQL for RLS tests: set BEAUTY_TEST_PG_ADMIN or start Docker. " +
                         $"({ex.GetType().Name}: {ex.Message})";
            return;
        }

        await using (var admin = new NpgsqlConnection(_adminServerCs))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE DATABASE {DatabaseName}");
        }

        var adminDbCs = new NpgsqlConnectionStringBuilder(_adminServerCs) { Database = DatabaseName }.ConnectionString;

        var migrateOptions = new DbContextOptionsBuilder<BeautyDbContext>();
        migrateOptions.UseBeautyNpgsql(adminDbCs);
        await using (var ctx = new BeautyDbContext(migrateOptions.Options, new TenantContext()))
        {
            await ctx.Database.MigrateAsync();
        }

        var password = Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(adminDbCs))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE ROLE {AppRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
            await Exec(admin, $"GRANT CONNECT ON DATABASE {DatabaseName} TO {AppRole}");
            await Exec(admin, $"GRANT USAGE ON SCHEMA public TO {AppRole}");
            await Exec(admin, $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole}");
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(adminDbCs)
        {
            Username = AppRole,
            Password = password,
        }.ConnectionString;
    }

    /// <summary>Context as the runtime app would get it: app role + tenant interceptor.</summary>
    public BeautyDbContext CreateContext(Guid? tenantId)
    {
        var tenant = tenantId is { } id ? new TenantContext(id) : new TenantContext();
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(AppConnectionString).AddInterceptors(new TenantConnectionInterceptor(tenant));
        return new BeautyDbContext(options.Options, tenant);
    }

    public async Task DisposeAsync()
    {
        if (SkipReason is null && _adminServerCs is not null && _container is null)
        {
            // external server: clean up what we created
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_adminServerCs);
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
            await Exec(admin, $"DROP ROLE IF EXISTS {AppRole}");
        }

        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
