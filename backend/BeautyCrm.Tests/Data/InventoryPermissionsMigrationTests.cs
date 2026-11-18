using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Migrations;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-877 (ADR-025 §32.2, §32.11 п.13): міграція beauty_inventory_permissions на БД з наявними користувачами: owner без рядків, admin з
/// revenue.view = none отримує inventory.costs = none, deputy/specialist за пресетом, знімки запрошень доповнено (наявні значення не
/// перезаписано), access_version + 1, аудит 'migrated', ідемпотентність, Down відмовляє для ручно змінених значень і чисто відкочує інакше.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class InventoryPermissionsMigrationTests
{
    private const string Previous = "20261118155039_beauty_inventory";
    private const string Target = "20261118170000_beauty_inventory_permissions";

    [SkippableFact]
    public async Task backfill_by_preset_costs_follow_revenue_view_idempotent_and_guarded_down()
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
        var db = $"beauty_invperm_{suffix}";
        var owner = $"beauty_invperm_owner_{suffix}";
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
            var superCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = db }.ConnectionString;

            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(ownerCs);
            await using var ctx = new BeautyDbContext(options.Options, new TenantContext());
            var migrator = ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
                           ?? throw new InvalidOperationException("IMigrator not available");
            Assert.Contains(Target, ctx.Database.GetMigrations());

            await migrator.MigrateAsync(Previous);
            await using var sc = new NpgsqlConnection(superCs);
            await sc.OpenAsync();

            var tA = Guid.NewGuid();
            await Exec(sc, $"INSERT INTO tenants (id, name, slug) VALUES ('{tA}', 'T', 'ip-{tA:N}')");
            var (ownerU, adminAll, adminNone, deputy, spec) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Task User(Guid id, string role) => Exec(sc,
                $"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ('{id}', '{tA}', '{id:N}@x.test', '{role}', 'x', '{role}')");
            await User(ownerU, "owner"); await User(adminAll, "admin"); await User(adminNone, "admin"); await User(deputy, "deputy"); await User(spec, "specialist");
            Task Perm(Guid u, string key, string level) => Exec(sc,
                $"INSERT INTO user_permissions (tenant_id, user_id, permission, level) VALUES ('{tA}', '{u}', '{key}', '{level}')");
            await Perm(adminAll, "revenue.view", "all");
            await Perm(adminNone, "revenue.view", "none"); // кастомне значення
            await Perm(deputy, "revenue.view", "all");     // навіть із revenue.view = all пресет deputy дає costs = none
            await Perm(spec, "inventory.view", "all");     // уже наявний рядок (ON CONFLICT DO NOTHING) - значення не змінюється
            var inv = Guid.NewGuid();
            await Exec(sc, $"INSERT INTO invites (id, tenant_id, email, role, token_hash, expires_at, permissions) VALUES ('{inv}', '{tA}', 'i@x.test', 'admin', '{Guid.NewGuid():N}', now() + interval '1 day', " +
                           "'{\"revenue.view\":\"none\",\"inventory.view\":\"own\"}'::jsonb)");

            await migrator.MigrateAsync(Target);

            Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM user_permissions WHERE user_id = '{ownerU}' AND permission LIKE 'inventory.%'"));
            Assert.Equal(1L, await Scalar(sc, $"SELECT access_version FROM users WHERE id = '{ownerU}'"));
            async Task Level(Guid u, string key, string expected) =>
                Assert.Equal(expected, await Text(sc, $"SELECT level FROM user_permissions WHERE user_id = '{u}' AND permission = '{key}'"));
            await Level(adminAll, "inventory.view", "all"); await Level(adminAll, "inventory.manage", "all"); await Level(adminAll, "inventory.costs", "edit");
            await Level(adminNone, "inventory.costs", "none"); await Level(adminNone, "inventory.manage", "all");
            await Level(deputy, "inventory.view", "all"); await Level(deputy, "inventory.manage", "all"); await Level(deputy, "inventory.costs", "none");
            await Level(spec, "inventory.view", "all"); // наявне значення збережено
            await Level(spec, "inventory.manage", "none"); await Level(spec, "inventory.costs", "none");
            Assert.Equal(2L, await Scalar(sc, $"SELECT access_version FROM users WHERE id = '{adminAll}'"));
            Assert.Equal(2L, await Scalar(sc, $"SELECT access_version FROM users WHERE id = '{spec}'")); // 2 нові рядки з 3: усе одно + 1
            Assert.Equal(4L, await Scalar(sc, "SELECT count(*) FROM user_access_audit WHERE action = 'migrated' AND actor_user_id IS NULL"));
            Assert.Equal(2L, await Scalar(sc, $"SELECT jsonb_array_length(changes) FROM user_access_audit WHERE action = 'migrated' AND target_user_id = '{spec}'"));
            // знімок запрошення: відсутні ключі додано (costs = none, бо revenue.view = none), наявне inventory.view = own збережено
            Assert.Equal("own", await Text(sc, $"SELECT permissions ->> 'inventory.view' FROM invites WHERE id = '{inv}'"));
            Assert.Equal("all", await Text(sc, $"SELECT permissions ->> 'inventory.manage' FROM invites WHERE id = '{inv}'"));
            Assert.Equal("none", await Text(sc, $"SELECT permissions ->> 'inventory.costs' FROM invites WHERE id = '{inv}'"));
            Assert.Equal("none", await Text(sc, $"SELECT permissions ->> 'revenue.view' FROM invites WHERE id = '{inv}'"));

            // ---- ідемпотентність: повторне виконання SQL міграції нічого не змінює ----
            var up = ((Migration)Activator.CreateInstance(typeof(beauty_inventory_permissions))!).UpOperations.OfType<SqlOperation>().Single().Sql;
            var rowsBefore = await Scalar(sc, "SELECT count(*) FROM user_permissions");
            var auditBefore = await Scalar(sc, "SELECT count(*) FROM user_access_audit");
            await using (var conn = new NpgsqlConnection(ownerCs))
            {
                await conn.OpenAsync();
                await Exec(conn, up);
            }
            Assert.Equal(rowsBefore, await Scalar(sc, "SELECT count(*) FROM user_permissions"));
            Assert.Equal(auditBefore, await Scalar(sc, "SELECT count(*) FROM user_access_audit"));
            Assert.Equal(2L, await Scalar(sc, $"SELECT access_version FROM users WHERE id = '{adminAll}'"));
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.user_permissions') AND relforcerowsecurity"));

            // ---- Down: ручна зміна значення блокує відкат ----
            await Exec(sc, $"UPDATE user_permissions SET level = 'own', updated_by_user_id = '{ownerU}' WHERE user_id = '{deputy}' AND permission = 'inventory.manage'");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("Down refused", Flatten(ex));
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id = '{Target}'"));
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.user_permissions') AND relforcerowsecurity"));

            // без ручних змін відкат чистий; решта прав і знімки цілі
            await Exec(sc, $"UPDATE user_permissions SET updated_by_user_id = NULL WHERE user_id = '{deputy}'");
            await migrator.MigrateAsync(Previous);
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM user_permissions WHERE permission LIKE 'inventory.%'"));
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM user_permissions WHERE user_id = '{adminAll}' AND permission = 'revenue.view'"));
            Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM invites WHERE id = '{inv}' AND permissions ?| ARRAY['inventory.view','inventory.manage','inventory.costs']"));
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM invites WHERE id = '{inv}' AND permissions ? 'revenue.view'"));
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

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
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

    private static async Task<string> Text(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
