using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-696: міграція beauty_staff_hardening додає cancelled_by_user_id/cancelled_at і ПЕРЕВІРЯЄ, що FORCE RLS
/// ввімкнено на таблицях керування працівниками (RAISE EXCEPTION, якщо ні). Без PostgreSQL тест пропускається
/// (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class StaffHardeningMigrationTests
{
    private const string StaffManagement = "20261008061428_beauty_staff_management";

    [SkippableFact]
    public async Task migration_fails_when_force_rls_is_missing_and_succeeds_when_it_is_enabled()
    {
        var requireDb = Environment.GetEnvironmentVariable("BEAUTY_TEST_REQUIRE_DB") == "1";
        PostgreSqlContainer? container = null;
        string adminCs;
        try
        {
            var external = Environment.GetEnvironmentVariable("BEAUTY_TEST_PG_ADMIN");
            if (string.IsNullOrWhiteSpace(external))
            {
                container = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await container.StartAsync();
                adminCs = container.GetConnectionString();
            }
            else adminCs = external;
        }
        catch (Exception ex) when (!requireDb)
        {
            Skip.If(true, $"No PostgreSQL: {ex.GetType().Name}");
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var db = $"beauty_hard_{suffix}";
        var owner = $"beauty_hard_owner_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = new NpgsqlConnection(adminCs))
            {
                await admin.OpenAsync();
                await Exec(admin, $"CREATE ROLE {owner} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
                await Exec(admin, $"CREATE DATABASE {db} OWNER {owner}");
            }
            var ownerCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = db, Username = owner, Password = password }.ConnectionString;

            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(ownerCs);
            await using var ctx = new BeautyDbContext(options.Options, new TenantContext());
            var migrator = ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
                           ?? throw new InvalidOperationException("IMigrator not available");
            await migrator.MigrateAsync(StaffManagement);

            await using var conn = new NpgsqlConnection(ownerCs);
            await conn.OpenAsync();
            await Exec(conn, "ALTER TABLE beauty_specialist_absences NO FORCE ROW LEVEL SECURITY");

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync());
            Assert.Contains("FORCE ROW LEVEL SECURITY is not enabled on beauty_specialist_absences", ex.ToString());
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_specialist_absences' AND column_name = 'cancelled_at'"));

            await Exec(conn, "ALTER TABLE beauty_specialist_absences FORCE ROW LEVEL SECURITY");
            await migrator.MigrateAsync();
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_specialist_absences' AND column_name IN ('cancelled_at', 'cancelled_by_user_id')"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (container is not null) await container.DisposeAsync();
            else
            {
                await using var admin = new NpgsqlConnection(adminCs);
                await admin.OpenAsync();
                await Exec(admin, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
                await Exec(admin, $"DROP ROLE IF EXISTS {owner}");
            }
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
