using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-697: міграція beauty_locations_and_cancellation_history - backfill вже скасованих записів (type = system, дані не
/// втрачаються, нескасовані не чіпаються) під FORCE RLS роллю-власником, FORCE RLS після міграції лишається ввімкненим.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class CancellationHistoryMigrationTests
{
    private const string Previous = "20261008092926_beauty_staff_hardening";

    [SkippableFact]
    public async Task backfill_marks_existing_cancelled_appointments_as_system_and_keeps_force_rls()
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
        var db = $"beauty_hist_{suffix}";
        var owner = $"beauty_hist_owner_{suffix}";
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

            // дані "до міграції": скасований (без автора), активний; власник схеми підпадає під FORCE RLS - app.tenant_id у сесії
            var tenant = Guid.NewGuid();
            Guid cancelled, active;
            await using (var conn = new NpgsqlConnection(ownerCs))
            {
                await conn.OpenAsync();
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
                var location = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenant}', 'L', 'UTC', true) RETURNING id");
                var specialist = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenant}', 'S', true) RETURNING id");
                var service = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenant}', 'Svc', 60, true) RETURNING id");
                var client = await Id(conn, $"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{tenant}', 'C') RETURNING id");
                string Appt(int day, string status, string? cancelledAt) =>
                    $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
                        status, source, price_original, price_final, cancelled_at)
                       VALUES ('{tenant}', '{location}', '{specialist}', '{service}', '{client}', now() + interval '{day} day', 60,
                        '{status}', 'admin', 100, 100, {cancelledAt ?? "NULL"}) RETURNING id";
                cancelled = await Id(conn, Appt(5, "cancelled", "now()"));
                active = await Id(conn, Appt(6, "confirmed", null));
            }

            await migrator.MigrateAsync();

            await using var after = new NpgsqlConnection(ownerCs);
            await after.OpenAsync();
            await Exec(after, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            Assert.Equal("system", await Text(after, $"SELECT cancelled_by_type FROM beauty_appointments WHERE id = '{cancelled}'"));
            Assert.Null(await Text(after, $"SELECT cancelled_by_type FROM beauty_appointments WHERE id = '{active}'"));
            Assert.Equal(2L, await Scalar(after, "SELECT count(*) FROM beauty_appointments")); // нічого не видалено
            Assert.Equal(1L, await Scalar(after, "SELECT count(*) FROM pg_class WHERE oid = 'beauty_appointments'::regclass AND relrowsecurity AND relforcerowsecurity"));
            // без app.tenant_id власник схеми нічого не бачить (FORCE повернуто)
            await Exec(after, "SELECT set_config('app.tenant_id', '', false)");
            Assert.Equal(0L, await Scalar(after, "SELECT count(*) FROM beauty_appointments"));
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

    private static async Task<string?> Text(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
