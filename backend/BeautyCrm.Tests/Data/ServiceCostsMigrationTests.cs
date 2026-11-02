using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-762 (§24.3): міграція beauty_service_costs. Адитивність без backfill (завершений запис лишається з NULL-знімком),
/// FORCE RLS роллю-власником (NOSUPERUSER NOBYPASSRLS), Down видаляє таблиці й колонки знімка, повторний Up проходить.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class ServiceCostsMigrationTests
{
    private const string Previous = "20261025080340_beauty_chat_buttons";

    [SkippableFact]
    public async Task up_is_additive_without_backfill_and_down_drops_tables_and_snapshot_columns()
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
        var db = $"beauty_cost_{suffix}";
        var owner = $"beauty_cost_owner_{suffix}";
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
            await migrator.MigrateAsync(Previous);

            // ---- дані "до міграції": завершений і майбутній записи ----
            var tenant = Guid.NewGuid();
            await using var conn = new NpgsqlConnection(ownerCs);
            await conn.OpenAsync();
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenant}', 'T', 'c-{tenant:N}')");
            var loc = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenant}', 'L', 'Europe/Kyiv', true) RETURNING id");
            var spec = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenant}', 'S', true) RETURNING id");
            var svc = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenant}', 'Svc', 60, true) RETURNING id");
            var cl = await Id(conn, $"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{tenant}', 'C') RETURNING id");
            foreach (var (status, day) in new[] { ("completed", 1), ("pending", 2) })
                await Exec(conn, $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final)
                    VALUES ('{tenant}', '{loc}', '{spec}', '{svc}', '{cl}', now() + interval '{day} day', 60, '{status}', 'admin', 200, 150)");

            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_service_costs')"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_appointments' AND column_name = 'cost_snapshot'"));

            // ---- Up роллю-власником під FORCE RLS ----
            await migrator.MigrateAsync();
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_appointments")); // нічого не втрачено
            Assert.Equal(2L, await Scalar(conn, @"SELECT count(*) FROM beauty_appointments WHERE cost_snapshot IS NULL AND master_percent_snapshot IS NULL
                AND master_payout_snapshot IS NULL AND cost_captured_at IS NULL AND price_final = 150")); // без backfill
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_appointments WHERE status = 'completed'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_service_costs"));
            foreach (var table in new[] { "beauty_service_costs", "beauty_service_cost_audit", "beauty_appointments", "beauty_services", "beauty_service_prices" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));

            // ---- Down: таблиці й колонки знімка зникають, записи лишаються ----
            await migrator.MigrateAsync(Previous);
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_appointments"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid IN (to_regclass('public.beauty_service_costs'), to_regclass('public.beauty_service_cost_audit'))"));
            Assert.Equal(0L, await Scalar(conn, @"SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_appointments'
                AND column_name IN ('cost_snapshot', 'master_percent_snapshot', 'master_payout_snapshot', 'cost_captured_at')"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE conname LIKE 'ck_beauty_appointments_cost_%'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_appointments') AND relforcerowsecurity"));

            await migrator.MigrateAsync(); // повторний Up
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_service_costs') AND relforcerowsecurity"));
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_appointments WHERE cost_snapshot IS NULL"));
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

    private static async Task<Guid> Id(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
