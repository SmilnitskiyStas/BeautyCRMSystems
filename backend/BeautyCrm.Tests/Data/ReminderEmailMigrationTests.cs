using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Міграція beauty_reminder_24h_email_channel: CHECK reminder_option приймає '24h', CHECK channels.type приймає 'email';
/// старі значення й дані переживають Up; Down відмовляє, поки є рядки з новими значеннями, і чисто відкочує після їх видалення.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class ReminderEmailMigrationTests
{
    private const string Previous = "20261109135452_beauty_specialist_profile_reviews";

    [SkippableFact]
    public async Task up_extends_checks_and_down_is_guarded_and_reversible()
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
        var db = $"beauty_rem_{suffix}";
        var owner = $"beauty_rem_owner_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = new NpgsqlConnection(adminCs))
            {
                await admin.OpenAsync();
                await Exec(admin, $"CREATE ROLE {owner} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
                await Exec(admin, "DO $$ BEGIN CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; EXCEPTION WHEN duplicate_object THEN NULL; END $$");
                await Exec(admin, $"GRANT beautycrm_catalog TO {owner} WITH INHERIT FALSE, SET TRUE");
                await Exec(admin, $"CREATE DATABASE {db} OWNER {owner}");
            }
            var ownerCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = db, Username = owner, Password = password }.ConnectionString;

            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(ownerCs);
            await using var ctx = new BeautyDbContext(options.Options, new TenantContext());
            var migrator = ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
                           ?? throw new InvalidOperationException("IMigrator not available");
            var target = ctx.Database.GetMigrations().Single(m => m.EndsWith("_beauty_reminder_24h_email_channel", StringComparison.Ordinal));

            await migrator.MigrateAsync(Previous);
            var tenant = Guid.NewGuid();
            await using var c = new NpgsqlConnection(ownerCs);
            await c.OpenAsync();
            await Exec(c, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            var spec = await Id(c, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenant}', 'A', true) RETURNING id");
            var loc = await Id(c, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenant}', 'L', 'UTC', true) RETURNING id");
            var svc = await Id(c, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenant}', 'S', 60, true) RETURNING id");
            var client = await Id(c, $"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{tenant}', 'C') RETURNING id");
            var day = 3;
            Task<Guid> Appt(string reminder) => Id(c, $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at,
                    duration_minutes, status, source, reminder_option, price_original, price_final)
                VALUES ('{tenant}', '{loc}', '{spec}', '{svc}', '{client}', now() + interval '{day++} day', 60, 'confirmed', 'online', '{reminder}', 100, 100) RETURNING id");
            Task Chan(string type) => Exec(c, $"INSERT INTO beauty_channels (tenant_id, type, name, is_active) VALUES ('{tenant}', '{type}', 'x', true)");

            // до міграції: нових значень немає
            await Appt("2h");
            await CheckViolation(() => Appt("24h"));
            await CheckViolation(() => Chan("email"));

            await migrator.MigrateAsync(target);
            Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM beauty_appointments WHERE reminder_option = '2h'")); // старі дані на місці
            foreach (var old in new[] { "none", "1h", "2h" }) await Appt(old);
            var id24 = await Appt("24h");
            await Chan("email");
            await Chan("telegram");
            await CheckViolation(() => Appt("48h"));
            await CheckViolation(() => Appt("24H"));
            await CheckViolation(() => Chan("sms"));

            // Down відмовляє, поки є 24h / email (дані не губляться мовчки)
            await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM beauty_appointments WHERE reminder_option = '24h'"));

            await Exec(c, $"UPDATE beauty_appointments SET reminder_option = '2h' WHERE id = '{id24}'");
            await Exec(c, "DELETE FROM beauty_channels WHERE type = 'email'");
            await migrator.MigrateAsync(Previous);
            await CheckViolation(() => Appt("24h"));
            await CheckViolation(() => Chan("email"));
            await Exec(c, "SELECT 1");

            // повторний Up
            await migrator.MigrateAsync(target);
            await Appt("24h");
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

    private static async Task CheckViolation(Func<Task> act) =>
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(act)).SqlState);

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
