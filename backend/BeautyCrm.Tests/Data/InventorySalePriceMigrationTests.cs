using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Міграція beauty_inventory_sale_price (ADR-025, контракт §32): ADD COLUMN NULL (наявні товари зберігаються з NULL), CHECK 0..1000000,
/// 2 знаки, Down = DROP COLUMN (дані цін втрачаються - прийнято), Up -> Down -> Up. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class InventorySalePriceMigrationTests
{
    private const string Previous = "20261119142456_beauty_inventory_stage2b";

    [SkippableFact]
    public async Task up_adds_nullable_column_with_check_keeps_existing_rows_and_down_drops_it()
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
        var db = $"beauty_invsp_{suffix}";
        var owner = $"beauty_invsp_owner_{suffix}";
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
            string Cs(string user) => new NpgsqlConnectionStringBuilder(adminCs) { Database = db, Username = user, Password = password }.ConnectionString;

            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(Cs(owner));
            await using var ctx = new BeautyDbContext(options.Options, new TenantContext());
            var migrator = ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
                           ?? throw new InvalidOperationException("IMigrator not available");
            await migrator.MigrateAsync(Previous);
            await using var conn = new NpgsqlConnection(Cs(owner));
            await conn.OpenAsync();

            // товар до міграції (стара схема, без sale_price)
            var tenant = Guid.NewGuid();
            var user = Guid.NewGuid();
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenant}', 'T', 't-{suffix}')");
            await Exec(conn, $"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ('{user}', '{tenant}', 'u-{suffix}@x.test', 'U', 'h', 'owner')");
            await Exec(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{tenant}', 'Old', 'pcs', 'sale', '{user}')");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_products' AND column_name = 'sale_price'"));

            await migrator.MigrateAsync();

            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_products' AND column_name = 'sale_price' AND is_nullable = 'YES' AND numeric_precision = 12 AND numeric_scale = 2"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_products WHERE name = 'Old' AND sale_price IS NULL"));

            string Prod(string price) =>
                $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, sale_price, updated_by_user_id) VALUES ('{tenant}', 'P', 'pcs', 'sale', {price}, '{user}')";
            await Rejects(conn, Prod("-0.01"), "ck_beauty_products_sale_price");
            await Rejects(conn, Prod("1000001"), "ck_beauty_products_sale_price");
            await Exec(conn, Prod("0"));
            await Exec(conn, Prod("1000000").Replace("'P'", "'Q'"));
            await Exec(conn, "UPDATE beauty_products SET sale_price = 12.345 WHERE name = 'P'");
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_products WHERE name = 'P' AND sale_price = 12.35"));

            // Down = DROP COLUMN (ціни втрачаються, товари лишаються); Up -> Down -> Up
            await migrator.MigrateAsync(Previous);
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_products' AND column_name = 'sale_price'"));
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_products"));
            await migrator.MigrateAsync();
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_products' AND column_name = 'sale_price'"));
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_products WHERE sale_price IS NULL"));
        }
        finally
        {
            try
            {
                await using var admin = new NpgsqlConnection(adminCs);
                await admin.OpenAsync();
                await Exec(admin, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
                await Exec(admin, $"DROP ROLE IF EXISTS {owner}");
            }
            catch { /* прибирання best-effort */ }
            if (container is not null) await container.DisposeAsync();
        }
    }

    private static async Task Rejects(NpgsqlConnection conn, string sql, string constraint)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, sql));
        Assert.Equal(constraint, ex.ConstraintName);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
