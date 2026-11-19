using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Міграція beauty_cancellation_terms_visibility: існуючі рядки отримують show_terms_to_client = true / custom_terms_text = NULL,
/// CHECK довжини ≤ 500; Down прибирає колонки, повторний Up працює. Без PostgreSQL пропускається
/// (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class CancellationTermsMigrationTests
{
    private const string Previous = "20261109150000_beauty_reminder_24h_email_channel";

    [SkippableFact]
    public async Task up_adds_columns_and_down_is_reversible()
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
                await Exec(admin, "DO $$ BEGIN CREATE ROLE beautycrm_audit_purge NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; EXCEPTION WHEN duplicate_object THEN NULL; END $$");
                await Exec(admin, $"GRANT beautycrm_audit_purge TO {owner} WITH INHERIT FALSE, SET TRUE");
                await Exec(admin, $"CREATE DATABASE {db} OWNER {owner}");
            }
            var ownerCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = db, Username = owner, Password = password }.ConnectionString;

            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(ownerCs);
            await using var ctx = new BeautyDbContext(options.Options, new TenantContext());
            var migrator = ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
                           ?? throw new InvalidOperationException("IMigrator not available");
            var target = ctx.Database.GetMigrations().Single(m => m.EndsWith("_beauty_cancellation_terms_visibility", StringComparison.Ordinal));

            await migrator.MigrateAsync(Previous);
            var tenant = Guid.NewGuid();
            await using var c = new NpgsqlConnection(ownerCs);
            await c.OpenAsync();
            await Exec(c, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            await Exec(c, $"INSERT INTO beauty_cancellation_settings (tenant_id, window_hours, refund_percent_in_window) VALUES ('{tenant}', 6, 40)");

            await migrator.MigrateAsync(target);
            // старий рядок: значення за замовчуванням, числа не змінились
            Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM beauty_cancellation_settings WHERE show_terms_to_client AND custom_terms_text IS NULL AND window_hours = 6 AND refund_percent_in_window = 40"));
            await Exec(c, "UPDATE beauty_cancellation_settings SET custom_terms_text = repeat('x', 500), show_terms_to_client = false");
            await CheckViolation(() => Exec(c, "UPDATE beauty_cancellation_settings SET custom_terms_text = repeat('x', 501)"));

            await migrator.MigrateAsync(Previous);
            Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_cancellation_settings' AND column_name IN ('show_terms_to_client', 'custom_terms_text')"));
            Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM beauty_cancellation_settings WHERE window_hours = 6")); // рядок пережив Down

            // повторний Up
            await migrator.MigrateAsync(target);
            Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM beauty_cancellation_settings WHERE show_terms_to_client AND custom_terms_text IS NULL"));
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
