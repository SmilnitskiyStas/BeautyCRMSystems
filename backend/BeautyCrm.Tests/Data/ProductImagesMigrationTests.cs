using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Міграція beauty_product_images (ADR-027, контракт §33.22.6): RLS ENABLE+FORCE+tenant_isolation, CHECK-и, глобальний UNIQUE storage_key,
/// DEFERRABLE UNIQUE порядку, композитний FK ON DELETE CASCADE, GRANT-и з deploy/staging/db/grants.sql (app: SELECT/INSERT/DELETE + UPDATE (sort_order); воркер - нічого),
/// Down відмовляє, якщо є рядки; Up -> Down -> Up. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class ProductImagesMigrationTests
{
    private const string Previous = "20261120122321_beauty_product_orders";
    private const string Table = "beauty_product_images";

    [SkippableFact]
    public async Task up_creates_images_with_rls_constraints_grants_and_down_refuses_when_rows_exist()
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
        var db = $"beauty_img_{suffix}";
        var owner = $"beauty_img_owner_{suffix}";
        var app = $"beauty_img_app_{suffix}";
        var worker = $"beauty_img_wrk_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = new NpgsqlConnection(adminCs))
            {
                await admin.OpenAsync();
                foreach (var r in new[] { owner, app, worker })
                    await Exec(admin, $"CREATE ROLE {r} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
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

            // ---- дані "до міграції": два tenant, користувачі, товари ----
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Ctx(conn, t);
                await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'img-{t:N}')");
            }
            async Task<Guid> User(Guid t)
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{t}', 'u@{t:N}.test', 'U', 'x', 'owner') RETURNING id");
            }
            async Task<Guid> Prod(Guid t, Guid u, string name)
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, sale_price, updated_by_user_id) VALUES ('{t}', '{name}', 'pcs', 'sale', 50, '{u}') RETURNING id");
            }
            var userA = await User(tenantA);
            var userB = await User(tenantB);
            var prodA1 = await Prod(tenantA, userA, "P1");
            var prodA2 = await Prod(tenantA, userA, "P2");
            var prodB = await Prod(tenantB, userB, "PB");
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM information_schema.tables WHERE table_name = '{Table}'"));

            await migrator.MigrateAsync();

            // ---- структура ----
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{Table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_policies WHERE tablename = '{Table}' AND policyname = 'tenant_isolation' AND qual IS NOT NULL AND with_check IS NOT NULL"));
            foreach (var ck in new[] { "storage_key", "mime", "size", "width", "height", "sort_order" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_constraint WHERE conname = 'ck_beauty_product_images_{ck}' AND contype = 'c'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE conname = 'ux_beauty_product_images_order' AND contype = 'u' AND condeferrable AND condeferred"));
            foreach (var ix in new[] { "ux_beauty_product_images_storage_key", "ix_beauty_product_images_product" })
                Assert.True(1L == await Scalar(conn, $"SELECT count(*) FROM pg_indexes WHERE indexname = '{ix}'"), ix);
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_constraint WHERE conrelid = to_regclass('public.{Table}') AND contype = 'f' AND confdeltype = 'c'")); // лише FK на товар - CASCADE
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM information_schema.columns WHERE table_name = '{Table}' AND column_name = 'storage_key' AND data_type = 'character' AND character_maximum_length = 32"));

            // ---- INSERT-хелпер і CHECK-и ----
            var seq = 0;
            string Key() => (++seq).ToString("x").PadLeft(32, '0');
            string Img(Guid prod, int order, Guid? t = null, Guid? user = null, string? key = null, string mime = "image/webp", string size = "1000", string w = "400", string h = "300") =>
                $"INSERT INTO {Table} (tenant_id, product_id, storage_key, mime, size_bytes, width, height, sort_order, created_by_user_id) " +
                $"VALUES ('{t ?? tenantA}', '{prod}', '{key ?? Key()}', '{mime}', {size}, {w}, {h}, {order}, '{user ?? userA}')";
            await Ctx(conn, tenantA);
            await Rejects(conn, Img(prodA1, 0, key: new string('a', 31)), "ck_beauty_product_images_storage_key");
            await Rejects(conn, Img(prodA1, 0, key: new string('A', 32)), "ck_beauty_product_images_storage_key");
            await Rejects(conn, Img(prodA1, 0, key: new string('g', 32)), "ck_beauty_product_images_storage_key");
            await Rejects(conn, Img(prodA1, 0, mime: "image/png"), "ck_beauty_product_images_mime");
            await Rejects(conn, Img(prodA1, 0, size: "0"), "ck_beauty_product_images_size");
            await Rejects(conn, Img(prodA1, 0, size: "10485761"), "ck_beauty_product_images_size");
            await Rejects(conn, Img(prodA1, 0, w: "0"), "ck_beauty_product_images_width");
            await Rejects(conn, Img(prodA1, 0, w: "1601"), "ck_beauty_product_images_width");
            await Rejects(conn, Img(prodA1, 0, h: "0"), "ck_beauty_product_images_height");
            await Rejects(conn, Img(prodA1, 0, h: "1601"), "ck_beauty_product_images_height");
            await Rejects(conn, Img(prodA1, -1), "ck_beauty_product_images_sort_order");
            await Rejects(conn, Img(prodA1, 100), "ck_beauty_product_images_sort_order");
            var img0 = await Id(conn, Img(prodA1, 0, size: "10485760", w: "1600", h: "1600") + " RETURNING id"); // межі дозволені
            var img1 = await Id(conn, Img(prodA1, 1) + " RETURNING id");
            await Exec(conn, Img(prodA1, 99));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM {Table} WHERE id = '{img0}' AND created_at IS NOT NULL"));
            // UNIQUE: порядок у межах товару; storage_key глобально
            await Rejects(conn, Img(prodA1, 1), "ux_beauty_product_images_order"); // DEFERRABLE INITIALLY DEFERRED - спрацьовує на commit; в autocommit це теж межа оператора
            await Exec(conn, Img(prodA2, 0)); // той самий порядок в іншому товарі
            var existingKey = await StringScalar(conn, $"SELECT storage_key FROM {Table} WHERE id = '{img0}'");
            await Rejects(conn, Img(prodA2, 5, key: existingKey), "ux_beauty_product_images_storage_key");
            // переставлення одним UPDATE у транзакції: проміжний дубль допустимий (DEFERRABLE)
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await Exec(conn, $"UPDATE {Table} SET sort_order = CASE id WHEN '{img0}' THEN 1 WHEN '{img1}' THEN 0 END WHERE id IN ('{img0}', '{img1}')");
                await tx.CommitAsync();
            }
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM {Table} WHERE id = '{img0}' AND sort_order = 1"));
            // дубль, що лишився на commit, відхиляється саме на commit
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await Exec(conn, $"UPDATE {Table} SET sort_order = 1 WHERE id = '{img1}'");
                var onCommit = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
                Assert.Equal("ux_beauty_product_images_order", onCommit.ConstraintName);
            }

            // ---- FK ----
            var badProduct = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Img(Guid.NewGuid(), 3)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, badProduct.SqlState);
            var badUser = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Img(prodA1, 3, user: Guid.NewGuid())));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, badUser.SqlState);
            var delUser = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM users WHERE id = '{userA}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delUser.SqlState); // RESTRICT

            // ---- RLS і крос-tenant ----
            await Ctx(conn, tenantB);
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {Table}")); // tenant B не бачить фото A
            Assert.Equal(0, await ExecCount(conn, $"UPDATE {Table} SET sort_order = 50 WHERE id = '{img0}'")); // чужий рядок невидимий
            Assert.Equal(0, await ExecCount(conn, $"DELETE FROM {Table} WHERE id = '{img0}'"));
            var withCheck = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Img(prodA1, 7, t: tenantA)));
            Assert.Equal("42501", withCheck.SqlState); // WITH CHECK: запис у чужий tenant
            var crossProd = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Img(prodA1, 7, t: tenantB, user: userB)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossProd.SqlState); // товар іншого tenant
            var crossUser = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Img(prodB, 7, t: tenantB, user: userA)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossUser.SqlState);
            await Exec(conn, Img(prodB, 0, t: tenantB, user: userB));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM {Table}"));
            await Exec(conn, "SELECT set_config('app.tenant_id', '', false)");
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {Table}")); // fail-closed без tenant
            await Ctx(conn, tenantA);
            Assert.Equal(4L, await Scalar(conn, $"SELECT count(*) FROM {Table}"));

            // ---- GRANT-и: справжній deploy/staging/db/grants.sql із підміною імен ролей ----
            await using (var admin = new NpgsqlConnection(Cs(owner)))
            {
                await admin.OpenAsync();
                foreach (var stmt in GrantStatements())
                    await Exec(admin, stmt.Replace("beautycrm_app", app).Replace("beautycrm_worker", worker));
            }
            await using (var appConn = new NpgsqlConnection(Cs(app)))
            {
                await appConn.OpenAsync();
                await Ctx(appConn, tenantA);
                Assert.Equal(4L, await Scalar(appConn, $"SELECT count(*) FROM {Table}"));
                var appImg = await Id(appConn, Img(prodA2, 9) + " RETURNING id");
                Assert.Equal(1, await ExecCount(appConn, $"UPDATE {Table} SET sort_order = 8 WHERE id = '{appImg}'")); // колонковий UPDATE (sort_order)
                foreach (var sql in new[]
                         {
                             $"UPDATE {Table} SET storage_key = '{Key()}'", $"UPDATE {Table} SET size_bytes = 1", $"UPDATE {Table} SET product_id = '{prodA2}'",
                             $"UPDATE {Table} SET created_by_user_id = '{userA}'", $"UPDATE {Table} SET sort_order = 8, width = 1", $"TRUNCATE {Table}",
                         })
                {
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, sql));
                    Assert.Equal("42501", denied.SqlState);
                }
                Assert.Equal(1, await ExecCount(appConn, $"DELETE FROM {Table} WHERE id = '{appImg}'"));
                // app під чужим tenant-контекстом не зачіпає рядки A
                await Ctx(appConn, tenantB);
                Assert.Equal(0, await ExecCount(appConn, $"DELETE FROM {Table} WHERE id = '{img0}'"));
                Assert.Equal(1L, await Scalar(appConn, $"SELECT count(*) FROM {Table}"));
            }
            await using (var wrkConn = new NpgsqlConnection(Cs(worker)))
            {
                await wrkConn.OpenAsync();
                await Ctx(wrkConn, tenantA);
                foreach (var sql in new[]
                         {
                             $"SELECT count(*) FROM {Table}", $"SELECT id FROM {Table}", $"UPDATE {Table} SET sort_order = 0", $"DELETE FROM {Table}", Img(prodA2, 20),
                         })
                {
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(wrkConn, sql));
                    Assert.Equal("42501", denied.SqlState);
                }
            }

            // ---- CASCADE: видалення товару прибирає його фото ----
            await Ctx(conn, tenantA);
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM {Table} WHERE product_id = '{prodA2}'"));
            await Exec(conn, $"DELETE FROM beauty_products WHERE id = '{prodA2}'");
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {Table} WHERE product_id = '{prodA2}'"));
            Assert.Equal(3L, await Scalar(conn, $"SELECT count(*) FROM {Table}"));

            // ---- Down із даними відмовляє; нічого не втрачається ----
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("product images exist", FullMessage(refused));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{Table}') AND relrowsecurity AND relforcerowsecurity")); // відкат повернув FORCE
            Assert.Equal(3L, await Scalar(conn, $"SELECT count(*) FROM {Table}"));
            // очищення обох tenant-ів: Down перевіряє всі рядки (на час перевірки FORCE знімається)
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Ctx(conn, t);
                await Exec(conn, $"DELETE FROM {Table}");
            }
            await Ctx(conn, tenantA);

            await migrator.MigrateAsync(Previous); // порожньо: Down дозволений
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{Table}')"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_products WHERE name = 'P1'")); // товари цілі

            await migrator.MigrateAsync(); // Up -> Down -> Up
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{Table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {Table}"));
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
                foreach (var r in new[] { app, worker, owner }) await Exec(admin, $"DROP ROLE IF EXISTS {r}");
            }
        }
    }

    /// <summary>Оператори deploy/staging/db/grants.sql без коментарів і без GRANT/REVOKE на функції (ними володіють beautycrm_catalog/beautycrm_audit_purge).</summary>
    private static IEnumerable<string> GrantStatements()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "staging", "db", "grants.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var text = string.Join(' ', File.ReadAllLines(Path.Combine(dir!.FullName, "deploy", "staging", "db", "grants.sql"))
            .Where(l => !l.TrimStart().StartsWith("--") && l.Trim().Length > 0));
        return text.Split(';').Select(s => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim())
            .Where(s => s.Length > 0 && !s.Contains("ON FUNCTION"));
    }

    private static string FullMessage(Exception e)
    {
        var parts = new List<string>();
        for (var x = e; x is not null; x = x.InnerException!) { parts.Add(x.Message); if (x.InnerException is null) break; }
        return string.Join(" | ", parts);
    }

    private static Task Ctx(NpgsqlConnection conn, Guid tenant) => Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");

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

    private static async Task<int> ExecCount(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> Id(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> StringScalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
