using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-816 (ADR-018): міграція platform_control_plane під роллю-власником NOSUPERUSER NOBYPASSRLS (FORCE RLS діє і на власника).
/// Up адитивна (наявні tenant/користувачі без змін), Down відмовляє, поки є користувачі в стані invited, потім чисто знімає
/// все (FORCE на users/tenants збережено), повторний Up. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class PlatformControlPlaneMigrationTests
{
    private const string Previous = "20261026203521_beauty_marketing";

    private static readonly string[] NewTables =
    {
        "platform_owner_invites", "platform_audit_log", "control_plane_nonces", "beauty_platform_feedback",
    };

    [SkippableFact]
    public async Task up_is_additive_down_refuses_with_invited_owner_then_drops_everything_and_up_again()
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
        var db = $"beauty_cp_{suffix}";
        var owner = $"beauty_cp_owner_{suffix}";
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

            // ---- дані "до міграції": suspended tenant (через старий статус) і користувач з паролем ----
            var t1 = Guid.NewGuid();
            var t2 = Guid.NewGuid();
            var u1 = Guid.NewGuid();
            await using var conn = new NpgsqlConnection(ownerCs);
            await conn.OpenAsync();
            foreach (var t in new[] { t1, t2 })
            {
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{t}', false)");
                await Exec(conn, $"INSERT INTO tenants (id, name, slug, status) VALUES ('{t}', 'T', 'cp-{t:N}', '{(t == t2 ? "suspended" : "active")}')");
            }
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{t1}', false)");
            await Exec(conn, $"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ('{u1}', '{t1}', 'o@x.test', 'Owner', 'bcrypt-hash', 'owner')");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'tenants' AND column_name = 'external_ref'"));
            Assert.Equal("NO", await Text(conn, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'password_hash'"));

            // ---- Up роллю-власником під FORCE RLS ----
            await migrator.MigrateAsync();

            Assert.Equal("YES", await Text(conn, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'password_hash'"));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM users WHERE id = '{u1}' AND password_hash = 'bcrypt-hash' AND state = 'active'")); // дані й пароль не змінені
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM tenants WHERE id = '{t1}' AND external_ref IS NULL AND external_source IS NULL AND suspended_at IS NULL"));
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{t2}', false)");
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM tenants WHERE id = '{t2}' AND status = 'suspended' AND suspended_at IS NULL")); // старе призупинення сумісне з CHECK
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{t1}', false)");

            foreach (var table in NewTables)
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}"));
            foreach (var table in new[] { "beauty_platform_feedback", "users", "tenants" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_policies WHERE tablename = 'tenants' AND policyname = 'tenants_control_lookup'"));

            // ---- invited-власник + запрошення: Down має відмовити (password_hash не повернути в NOT NULL без вигаданого значення) ----
            var invited = Guid.NewGuid();
            await Exec(conn, $"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role, state) VALUES ('{invited}', '{t1}', 'inv@x.test', 'Invited', NULL, 'owner', 'invited')");
            await Exec(conn, $@"INSERT INTO platform_owner_invites (tenant_id, user_id, token_hash, expires_at, created_by_actor)
                VALUES ('{t1}', '{invited}', sha256('t'::bytea), now() + interval '72 hours', 'platform_key')");

            var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("Down refused", Flatten(refused));
            // відкат транзакції: усе на місці, FORCE не знято
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.platform_owner_invites')"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.users') AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM users WHERE state = 'invited' AND password_hash IS NULL"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM platform_owner_invites"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id LIKE '%_platform_control_plane'"));

            // людина вирішила: видалила invited-користувача (запрошення зникає каскадом) -> Down проходить
            await Exec(conn, $"DELETE FROM users WHERE id = '{invited}'");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM platform_owner_invites"));
            await migrator.MigrateAsync(Previous);

            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid IN (" + string.Join(", ", NewTables.Select(t => $"to_regclass('public.{t}')")) + ")"));
            Assert.Equal(0L, await Scalar(conn, @"SELECT count(*) FROM information_schema.columns WHERE
                (table_name = 'tenants' AND column_name IN ('external_ref', 'external_source', 'suspended_at', 'suspended_reason'))
                OR (table_name = 'users' AND column_name = 'state')"));
            Assert.Equal("NO", await Text(conn, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'password_hash'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_policies WHERE policyname = 'tenants_control_lookup'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_proc WHERE proname = 'platform_audit_log_block_mutation'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_users_state', 'ck_users_password_or_invited', 'ck_tenants_suspended', 'ck_tenants_external_pair')"));
            foreach (var table in new[] { "users", "tenants" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM users WHERE id = '{u1}' AND password_hash = 'bcrypt-hash'"));

            await migrator.MigrateAsync(); // повторний Up
            foreach (var table in NewTables)
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}"));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM users WHERE id = '{u1}' AND state = 'active'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_platform_feedback') AND relforcerowsecurity"));
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
        for (var e = ex; e is not null; e = e.InnerException!) { parts.Add(e.Message); if (e.InnerException is null) break; }
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
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> Text(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
