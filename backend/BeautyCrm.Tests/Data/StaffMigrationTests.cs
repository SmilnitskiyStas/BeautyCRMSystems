using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-691: міграція beauty_staff_management на БД з наявними даними. Власник схеми — звичайна роль
/// (NOSUPERUSER, без BYPASSRLS), тож FORCE RLS діє і на неї: перевіряється, що backfill усіх послуг для наявних
/// майстрів справді вставляє рядки лише в межах свого tenant, а FORCE на джерелах повертається.
/// Без PostgreSQL тест пропускається (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class StaffMigrationTests
{
    private const string PreviousMigration = "20261007153917_beauty_public_booking";

    [SkippableFact]
    public async Task migration_backfills_all_services_per_tenant_and_restores_force_rls()
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
        var db = $"beauty_mig_{suffix}";
        var owner = $"beauty_mig_owner_{suffix}";
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
            await migrator.MigrateAsync(PreviousMigration);

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            Guid specA1 = Guid.NewGuid(), specA2 = Guid.NewGuid(), specB = Guid.NewGuid();
            Guid svcA1 = Guid.NewGuid(), svcA2 = Guid.NewGuid(), svcA3 = Guid.NewGuid(), svcB = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(ownerCs))
            {
                await conn.OpenAsync();
                // дані кожного tenant вставляються з його app.tenant_id (RLS WITH CHECK)
                async Task Seed(Guid tenant, string sql)
                {
                    await using var tx = await conn.BeginTransactionAsync();
                    await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', true)", tx);
                    await Exec(conn, sql, tx);
                    await tx.CommitAsync();
                }
                await Seed(tenantA, $@"
                    INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES
                        ('{specA1}', '{tenantA}', 'A1', true), ('{specA2}', '{tenantA}', 'A2', false);
                    INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES
                        ('{svcA1}', '{tenantA}', 'S1', 30, true), ('{svcA2}', '{tenantA}', 'S2', 60, true), ('{svcA3}', '{tenantA}', 'S3', 45, false);");
                await Seed(tenantB, $@"
                    INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ('{specB}', '{tenantB}', 'B1', true);
                    INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ('{svcB}', '{tenantB}', 'SB', 30, true);");
            }

            await migrator.MigrateAsync(); // beauty_staff_management

            await using var check = new NpgsqlConnection(ownerCs);
            await check.OpenAsync();
            async Task<List<(Guid Spec, Guid Svc)>> Links(Guid tenant)
            {
                await using var tx = await check.BeginTransactionAsync();
                await Exec(check, $"SELECT set_config('app.tenant_id', '{tenant}', true)", tx);
                await using var cmd = new NpgsqlCommand("SELECT specialist_id, service_id FROM beauty_specialist_services", check, tx);
                await using var rd = await cmd.ExecuteReaderAsync();
                var rows = new List<(Guid, Guid)>();
                while (await rd.ReadAsync()) rows.Add((rd.GetGuid(0), rd.GetGuid(1)));
                return rows;
            }

            var a = await Links(tenantA);
            Assert.Equal(6, a.Count); // 2 майстри x 3 послуги (включно з неактивною)
            Assert.All(a, l => Assert.Contains(l.Spec, new[] { specA1, specA2 }));
            Assert.Equal(new[] { svcA1, svcA2, svcA3 }.Order(), a.Where(l => l.Spec == specA1).Select(l => l.Svc).Order());

            var b = await Links(tenantB);
            Assert.Equal([(specB, svcB)], b); // жодного перехресного призначення між tenant

            // без tenant-контексту нових таблиць не видно (fail-closed, FORCE діє і на власника)
            await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM beauty_specialist_services", check))
                Assert.Equal(0L, await cmd.ExecuteScalarAsync());

            // FORCE RLS повернуто на джерелах backfill і ввімкнено на нових таблицях
            await using var flags = new NpgsqlCommand(@"
                SELECT count(*) FROM pg_class
                WHERE relname IN ('beauty_specialists', 'beauty_services', 'beauty_specialist_services', 'beauty_specialist_absences')
                  AND relrowsecurity AND relforcerowsecurity", check);
            Assert.Equal(4L, await flags.ExecuteScalarAsync());
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

    private static async Task Exec(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
