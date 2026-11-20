using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Міграція beauty_inventory_product_categories (ADR-025, контракт §32.27): нова таблиця дерева (RLS ENABLE+FORCE, композитні tenant-FK, унікальність
/// імені в межах батька без регістру, у т.ч. для кореня), beauty_products.category_id NULL (наявні товари зберігаються), Down відмовляє,
/// якщо в таблиці є рядки (RAISE EXCEPTION), інакше DROP; Up -> Down -> Up. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class InventoryProductCategoriesMigrationTests
{
    private const string Previous = "20261120084056_beauty_inventory_sale_price";

    [SkippableFact]
    public async Task up_creates_tree_table_with_rls_constraints_and_down_refuses_when_rows_exist()
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
        var db = $"beauty_invpc_{suffix}";
        var owner = $"beauty_invpc_owner_{suffix}";
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

            // товар до міграції (стара схема, без category_id)
            var tenant = Guid.NewGuid();
            var tenant2 = Guid.NewGuid();
            var user = Guid.NewGuid();
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenant}', 'T', 't-{suffix}')");
            await Exec(conn, $"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ('{user}', '{tenant}', 'u-{suffix}@x.test', 'U', 'h', 'owner')");
            await Exec(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, category, updated_by_user_id) VALUES ('{tenant}', 'Old', 'pcs', 'sale', 'Hair', '{user}')");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'beauty_product_categories'"));

            await migrator.MigrateAsync();

            // дерево порожнє; наявний товар зберігся, category_id NULL, стара текстова category без змін
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_product_categories"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_products WHERE name = 'Old' AND category_id IS NULL AND category = 'Hair'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_products' AND column_name = 'category_id' AND is_nullable = 'YES'"));
            // RLS ENABLE + FORCE + політика
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE relname = 'beauty_product_categories' AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_policies WHERE tablename = 'beauty_product_categories' AND policyname = 'tenant_isolation'"));

            static string Cat(Guid t, string name, string? parent = null, string? id = null) =>
                $"INSERT INTO beauty_product_categories ({(id is null ? "" : "id, ")}tenant_id, parent_id, name) VALUES ({(id is null ? "" : $"'{id}', ")}'{t}', {(parent is null ? "NULL" : $"'{parent}'")}, '{name}')";

            var root = Guid.NewGuid().ToString();
            var child = Guid.NewGuid().ToString();
            await Exec(conn, Cat(tenant, "Hair", id: root));
            await Exec(conn, Cat(tenant, "Dye", root, child));
            // унікальність імені в межах батька без регістру (і для кореня з parent_id NULL)
            await Rejects(conn, Cat(tenant, "HAIR"), "ux_beauty_product_categories_name");
            await Rejects(conn, Cat(tenant, "dye", root), "ux_beauty_product_categories_name");
            await Exec(conn, Cat(tenant, "Dye")); // те саме ім'я під іншим батьком (корінь) - можна
            // CHECK: порожнє/пробільне ім'я, довге ім'я, сам собі батько
            await Rejects(conn, Cat(tenant, "   ", root), "ck_beauty_product_categories_name");
            var tooLong = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Cat(tenant, new string('x', 101), root))); // varchar(100)
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, tooLong.SqlState);
            await Rejects(conn, $"UPDATE beauty_product_categories SET parent_id = id WHERE id = '{child}'", "ck_beauty_product_categories_no_self_parent");
            // композитний FK: батько з іншого tenant неможливий; RLS ховає чужі рядки
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant2}', false)");
            await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenant2}', 'T2', 't2-{suffix}')");
            var foreign = Guid.NewGuid().ToString();
            await Exec(conn, Cat(tenant2, "Foreign", id: foreign));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_product_categories"));
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_product_categories"));
            var fkCat = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Cat(tenant, "Cross", foreign)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fkCat.SqlState);
            var fkProd = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_products SET category_id = '{foreign}' WHERE name = 'Old'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fkProd.SqlState);
            // товар у своїй категорії; ON DELETE RESTRICT (і для товару, і для підкатегорії)
            await Exec(conn, $"UPDATE beauty_products SET category_id = '{child}' WHERE name = 'Old'");
            var delUsed = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_product_categories WHERE id = '{child}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delUsed.SqlState);
            var delParent = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_product_categories WHERE id = '{root}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delParent.SqlState);

            // Down відмовляє, поки в таблиці є рядки (транзакція міграції відкочується: таблиця, RLS і дані на місці)
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("product categories exist", ex.ToString());
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE relname = 'beauty_product_categories' AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_product_categories"));

            // після очищення Down = DROP (товари лишаються), Up -> Down -> Up
            await Exec(conn, "UPDATE beauty_products SET category_id = NULL");
            await Exec(conn, "DELETE FROM beauty_product_categories WHERE parent_id IS NOT NULL");
            await Exec(conn, "DELETE FROM beauty_product_categories");
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant2}', false)");
            await Exec(conn, "DELETE FROM beauty_product_categories");
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            await migrator.MigrateAsync(Previous);
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'beauty_product_categories'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_products' AND column_name = 'category_id'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_products"));
            await migrator.MigrateAsync();
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'beauty_product_categories'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_products WHERE category_id IS NULL AND category = 'Hair'"));
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
