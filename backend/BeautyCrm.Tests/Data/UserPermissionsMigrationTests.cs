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
/// TASK-849 (ADR-023 §2.11): міграція auth_user_permissions на БД з наявними користувачами: backfill пресетів admin/specialist
/// (owner без рядків, ідемпотентно, `migrated` у журнал), CHECK ролей, Down відмовляє за наявності заступників/кастомних прав
/// і чисто відкочує інакше. Міграція йде під роллю-власником (NOSUPERUSER NOBYPASSRLS, FORCE RLS), перевірки та посів даних — під superuser.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class UserPermissionsMigrationTests
{
    private const string Previous = "20261110090000_beauty_cancellation_terms_visibility";

    // §30.3: admin / specialist
    private static readonly (string Key, string Admin, string Specialist)[] Presets =
    [
        ("calendar.view", "all", "own"), ("calendar.manage", "all", "own"),
        ("clients.view", "all", "none"), ("clients.edit", "all", "none"), ("clients.contacts", "full", "masked"), ("clients.calls", "all", "none"),
        ("revenue.view", "all", "none"), ("revenue.costs", "edit", "none"),
        ("channels.inbox", "reply", "none"), ("channels.settings", "edit", "none"),
        ("ai.use", "use", "none"), ("ai.settings", "edit", "none"),
        ("services.catalog", "edit", "view"), ("marketing.campaigns", "edit", "none"),
        ("staff.access", "edit", "own"), ("locations.access", "edit", "own"),
        ("settings.general", "view", "none"), ("team.manage", "manage", "none"),
    ];

    [SkippableFact]
    public async Task backfill_presets_check_roles_and_guarded_down()
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
        var db = $"beauty_uperm_{suffix}";
        var owner = $"beauty_uperm_owner_{suffix}";
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
            var target = ctx.Database.GetMigrations().Single(m => m.EndsWith("_auth_user_permissions", StringComparison.Ordinal));
            Assert.Equal(target, ctx.Database.GetMigrations().Last()); // нова міграція остання

            // ---- дані "до міграції": 2 tenant-и, owner/admin/specialist, вимкнений admin, pending-запрошення ----
            await migrator.MigrateAsync(Previous);
            await using var sc = new NpgsqlConnection(superCs);
            await sc.OpenAsync();
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'access_version'"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.user_permissions')"));

            var (tA, tB) = (Guid.NewGuid(), Guid.NewGuid());
            var (ownerA, adminA, adminOffA, specA, adminB, specB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            foreach (var t in new[] { tA, tB })
                await Exec(sc, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'up-{t:N}')");
            Task User(Guid id, Guid t, string role, bool active = true) => Exec(sc,
                $"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role, is_active) VALUES ('{id}', '{t}', '{id:N}@x.test', '{role}', 'x', '{role}', {active.ToString().ToLowerInvariant()})");
            await User(ownerA, tA, "owner");
            await User(adminA, tA, "admin");
            await User(adminOffA, tA, "admin", active: false);
            await User(specA, tA, "specialist");
            await User(adminB, tB, "admin");
            await User(specB, tB, "specialist");
            var pendingInvite = Guid.NewGuid();
            await Exec(sc, $"INSERT INTO invites (id, tenant_id, email, role, token_hash, expires_at) VALUES ('{pendingInvite}', '{tA}', 'p@x.test', 'admin', '{Guid.NewGuid():N}', now() + interval '1 day')");
            var revokedInvite = Guid.NewGuid(); // не лише pending: усі наявні запрошення отримують знімок (колонка стає NOT NULL)
            await Exec(sc, $"INSERT INTO invites (id, tenant_id, email, role, token_hash, expires_at, revoked_at) VALUES ('{revokedInvite}', '{tB}', 'r@x.test', 'specialist', '{Guid.NewGuid():N}', now() + interval '1 day', now())");
            await Exec(sc, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{tA}', 'dep@x.test', 'D', 'x', 'specialist')"); // ще один майстер
            await ExpectCheck(sc, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{tA}', 'd2@x.test', 'D', 'x', 'deputy')"); // deputy ще не існує

            // ---- Up (роль-власник під FORCE RLS) ----
            await migrator.MigrateAsync(target);

            Assert.Equal(7L, await Scalar(sc, "SELECT count(*) FROM users"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM users WHERE access_version <> 1")); // існуючі користувачі: 1
            // invites.permissions: NOT NULL, повний знімок пресету ролі для кожного наявного запрошення
            Assert.Equal("NO", await Text(sc, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'invites' AND column_name = 'permissions'"));
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM invites WHERE id = '{pendingInvite}' AND role = 'admin'"));
            foreach (var (inv, isAdmin) in new[] { (pendingInvite, true), (revokedInvite, false) })
            {
                Assert.Equal(Presets.Length, (int)await Scalar(sc, $"SELECT count(*) FROM invites i, jsonb_object_keys(i.permissions) k WHERE i.id = '{inv}'"));
                foreach (var p in Presets)
                    Assert.Equal(isAdmin ? p.Admin : p.Specialist, await Text(sc, $"SELECT permissions ->> '{p.Key}' FROM invites WHERE id = '{inv}'"));
            }
            // нові запрошення без знімка отримують '{}' (fail-closed) до TASK-851; NULL і не-об'єкт відхиляються
            await Exec(sc, $"INSERT INTO invites (tenant_id, email, role, token_hash, expires_at) VALUES ('{tA}', 'plain@x.test', 'admin', '{Guid.NewGuid():N}', now() + interval '1 day')");
            Assert.Equal("{}", await Text(sc, "SELECT permissions::text FROM invites WHERE email = 'plain@x.test'"));
            Assert.Equal("23502", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc, $"INSERT INTO invites (tenant_id, email, role, token_hash, expires_at, permissions) VALUES ('{tA}', 'n@x.test', 'admin', '{Guid.NewGuid():N}', now() + interval '1 day', NULL)"))).SqlState);
            await ExpectCheck(sc, $"INSERT INTO invites (tenant_id, email, role, token_hash, expires_at, permissions) VALUES ('{tA}', 'arr@x.test', 'admin', '{Guid.NewGuid():N}', now() + interval '1 day', '[]'::jsonb)");
            await Exec(sc, "DELETE FROM invites WHERE email = 'plain@x.test'");

            // beauty_appointments.created_by_user_id: nullable, композитний FK RESTRICT, частковий індекс
            Assert.Equal("YES", await Text(sc, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'beauty_appointments' AND column_name = 'created_by_user_id'"));
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_beauty_appointments_users_tenant_id_created_by_user_id' AND contype = 'f' AND confdeltype = 'r'"));
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_beauty_appointments_tenant_id_created_by_user_id' AND indexdef LIKE '%created_by_user_id IS NOT NULL%'"));

            // purge-роль і функція: NOLOGIN-роль, SECURITY DEFINER, фіксований search_path, без EXECUTE для PUBLIC
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_roles WHERE rolname = 'beautycrm_audit_purge' AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolinherit"));
            Assert.Equal(1L, await Scalar(sc, @"SELECT count(*) FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
                WHERE p.proname = 'purge_user_access_audit' AND p.prosecdef AND r.rolname = 'beautycrm_audit_purge'
                  AND p.proconfig::text LIKE '%search_path=pg_catalog, pg_temp%' AND NOT has_function_privilege('public', p.oid, 'EXECUTE')"));
            foreach (var t in new[] { "user_permissions", "user_access_audit" })
                Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{t}') AND relrowsecurity AND relforcerowsecurity"));
            foreach (var t in new[] { "users", "invites" })
                Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{t}') AND relrowsecurity AND relforcerowsecurity")); // FORCE повернуто

            // backfill: owner без рядків; кожен admin/specialist (зокрема вимкнений) має повний пресет
            Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM user_permissions WHERE user_id = '{ownerA}'"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM user_permissions up JOIN users u ON u.id = up.user_id AND u.tenant_id = up.tenant_id WHERE u.role NOT IN ('admin', 'specialist')"));
            Assert.Equal(3L, await Scalar(sc, "SELECT count(*) FROM users WHERE role = 'specialist'"));
            foreach (var (u, t, isAdmin) in new[] { (adminA, tA, true), (adminOffA, tA, true), (specA, tA, false), (adminB, tB, true), (specB, tB, false) })
            {
                Assert.Equal(Presets.Length, (int)await Scalar(sc, $"SELECT count(*) FROM user_permissions WHERE tenant_id = '{t}' AND user_id = '{u}'"));
                foreach (var p in Presets)
                    Assert.Equal(isAdmin ? p.Admin : p.Specialist,
                        await Text(sc, $"SELECT level FROM user_permissions WHERE tenant_id = '{t}' AND user_id = '{u}' AND permission = '{p.Key}'"));
            }
            Assert.Equal(6L * Presets.Length, await Scalar(sc, "SELECT count(*) FROM user_permissions")); // 5 перелічених + додатковий майстер
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM user_permissions WHERE updated_by_user_id IS NOT NULL"));
            // журнал: один 'migrated' на користувача, без актора, зі снімком усіх ключів, без ПІІ (лише ключі й рівні)
            Assert.Equal(6L, await Scalar(sc, "SELECT count(*) FROM user_access_audit"));
            Assert.Equal(6L, await Scalar(sc, "SELECT count(*) FROM user_access_audit WHERE action = 'migrated' AND actor_user_id IS NULL AND invite_id IS NULL AND jsonb_array_length(changes) = " + Presets.Length));
            Assert.Equal(1L, await Scalar(sc, $@"SELECT count(*) FROM user_access_audit WHERE target_user_id = '{adminA}'
                AND changes @> '[{{""permission"": ""revenue.view"", ""from"": null, ""to"": ""all""}}]'::jsonb"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM user_access_audit WHERE changes::text ~* '(@|password|token|x\\.test)'"));

            // ---- CHECK ролей розширено ----
            await Exec(sc, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{tA}', 'dep1@x.test', 'D', 'x', 'deputy')");
            await Exec(sc, $"INSERT INTO invites (tenant_id, email, role, token_hash, expires_at, permissions) VALUES ('{tA}', 'dep2@x.test', 'deputy', '{Guid.NewGuid():N}', now() + interval '1 day', jsonb_build_object('revenue.view', 'own'))");
            await ExpectCheck(sc, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{tA}', 'bad@x.test', 'D', 'x', 'manager')");
            await ExpectCheck(sc, $"INSERT INTO invites (tenant_id, email, role, token_hash, expires_at) VALUES ('{tA}', 'bad@x.test', 'owner', '{Guid.NewGuid():N}', now() + interval '1 day')");

            // ---- ідемпотентний backfill: повторний запуск тієї самої SQL нічого не змінює (кастомні значення не перезаписуються) ----
            await Exec(sc, $"UPDATE user_permissions SET level = 'own', updated_by_user_id = '{ownerA}' WHERE tenant_id = '{tA}' AND user_id = '{adminA}' AND permission = 'revenue.view'");
            var (permBefore, auditBefore) = (await Scalar(sc, "SELECT count(*) FROM user_permissions"), await Scalar(sc, "SELECT count(*) FROM user_access_audit"));
            await ExecAsOwner(ownerCs, BackfillSql());
            Assert.Equal(permBefore, await Scalar(sc, "SELECT count(*) FROM user_permissions"));
            Assert.Equal(auditBefore, await Scalar(sc, "SELECT count(*) FROM user_access_audit"));
            Assert.Equal("own", await Text(sc, $"SELECT level FROM user_permissions WHERE user_id = '{adminA}' AND permission = 'revenue.view'"));
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM user_permissions WHERE user_id = '{adminA}' AND updated_by_user_id = '{ownerA}'"));
            // ...а загублений рядок пресету backfill відновлює (один новий `migrated` на користувача, лише за вставлене)
            await Exec(sc, $"DELETE FROM user_permissions WHERE user_id = '{specB}' AND permission = 'team.manage'");
            await ExecAsOwner(ownerCs, BackfillSql());
            Assert.Equal("none", await Text(sc, $"SELECT level FROM user_permissions WHERE user_id = '{specB}' AND permission = 'team.manage'"));
            Assert.Equal(auditBefore + 1, await Scalar(sc, "SELECT count(*) FROM user_access_audit"));
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM user_access_audit WHERE target_user_id = '{specB}' AND jsonb_array_length(changes) = 1"));
            await Exec(sc, $"UPDATE user_permissions SET level = 'all' WHERE user_id = '{adminA}' AND permission = 'revenue.view'");

            // ---- Down відмовляє, поки є заступник / запрошення deputy / кастомні права ----
            await AssertDownRefused(migrator, sc, "role deputy"); // є користувач і запрошення deputy; спершу користувач
            await Exec(sc, "DELETE FROM users WHERE role = 'deputy'");
            await AssertDownRefused(migrator, sc, "invites with role deputy"); // запрошення deputy + permissions
            await Exec(sc, "DELETE FROM invites WHERE role = 'deputy'");

            await Exec(sc, $"UPDATE invites SET permissions = jsonb_build_object('revenue.view', 'own') WHERE id = '{pendingInvite}'");
            await AssertDownRefused(migrator, sc, "custom permissions");
            await Exec(sc, $"UPDATE invites SET permissions = '{{}}'::jsonb WHERE id = '{pendingInvite}'"); // порожній знімок - не кастомізація

            await Exec(sc, $"UPDATE user_permissions SET level = 'own' WHERE user_id = '{adminB}' AND permission = 'revenue.view'");
            await AssertDownRefused(migrator, sc, "differ from role presets");
            await Exec(sc, $"UPDATE user_permissions SET level = 'all' WHERE user_id = '{adminB}' AND permission = 'revenue.view'");

            await Exec(sc, $"DELETE FROM user_permissions WHERE user_id = '{specA}' AND permission = 'calendar.view'"); // відсутній рядок теж відрізняється
            await AssertDownRefused(migrator, sc, "differ from role presets");
            await Exec(sc, $"INSERT INTO user_permissions (tenant_id, user_id, permission, level) VALUES ('{tA}', '{specA}', 'calendar.view', 'own')");

            await Exec(sc, $"INSERT INTO user_permissions (tenant_id, user_id, permission, level) VALUES ('{tA}', '{specA}', 'future.key', 'x')"); // зайвий ключ
            await AssertDownRefused(migrator, sc, "differ from role presets");
            await Exec(sc, "DELETE FROM user_permissions WHERE permission = 'future.key'");

            await Exec(sc, $"INSERT INTO user_permissions (tenant_id, user_id, permission, level) VALUES ('{tA}', '{ownerA}', 'revenue.view', 'all')"); // права owner не зберігаються
            await AssertDownRefused(migrator, sc, "differ from role presets");
            await Exec(sc, $"DELETE FROM user_permissions WHERE user_id = '{ownerA}'");

            // ---- Down без кастомізацій: чисто відкочує, дані користувачів цілі ----
            await migrator.MigrateAsync(Previous);
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid IN (to_regclass('public.user_permissions'), to_regclass('public.user_access_audit'))"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_proc WHERE proname = 'user_access_audit_block_mutation'"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE (table_name = 'users' AND column_name = 'access_version') OR (table_name = 'invites' AND column_name = 'permissions')"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_invites_permissions'"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_proc WHERE proname = 'purge_user_access_audit'"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_appointments' AND column_name = 'created_by_user_id'"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_beauty_appointments_users_tenant_id_created_by_user_id'"));
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_roles WHERE rolname = 'beautycrm_audit_purge'")); // кластерну роль не видаляємо
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_users_role' AND pg_get_constraintdef(oid) NOT LIKE '%deputy%'"));
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_invites_role' AND pg_get_constraintdef(oid) NOT LIKE '%deputy%'"));
            foreach (var t in new[] { "users", "invites" })
                Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{t}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(7L, await Scalar(sc, "SELECT count(*) FROM users"));
            await ExpectCheck(sc, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{tA}', 'dep3@x.test', 'D', 'x', 'deputy')");

            // ---- повторний Up: backfill відтворює пресети ----
            await migrator.MigrateAsync(target);
            Assert.Equal(6L * Presets.Length, await Scalar(sc, "SELECT count(*) FROM user_permissions"));
            Assert.Equal(6L, await Scalar(sc, "SELECT count(*) FROM user_access_audit WHERE action = 'migrated'"));
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

    /// <summary>Фрагмент backfill із SQL самої міграції (від зняття FORCE на users до його повернення): перевіряє справжній код, а не копію.</summary>
    private static string BackfillSql()
    {
        var migration = (Migration)Activator.CreateInstance(typeof(auth_user_permissions))!;
        var up = migration.UpOperations.OfType<SqlOperation>().Single().Sql;
        const string start = "ALTER TABLE users NO FORCE ROW LEVEL SECURITY;";
        const string end = "ALTER TABLE invites FORCE ROW LEVEL SECURITY;";
        var from = up.IndexOf(start, StringComparison.Ordinal);
        var to = up.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "backfill markers not found in the migration SQL");
        return up[from..(to + end.Length)];
    }

    private static async Task AssertDownRefused(IMigrator migrator, NpgsqlConnection sc, string reason)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
        var text = Flatten(ex);
        Assert.Contains("Down refused", text);
        Assert.Contains(reason, text);
        // відкат транзакції: усе на місці, FORCE не знято, запис історії міграцій не видалено
        Assert.Equal(2L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid IN (to_regclass('public.user_permissions'), to_regclass('public.user_access_audit'))"));
        Assert.Equal(3L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid IN (to_regclass('public.users'), to_regclass('public.invites'), to_regclass('public.user_permissions')) AND relforcerowsecurity"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id LIKE '%_auth_user_permissions'"));
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    private static async Task ExecAsOwner(string ownerCs, string sql)
    {
        await using var conn = new NpgsqlConnection(ownerCs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // у міграції нові таблиці ще без RLS на час backfill; тут вони вже під FORCE, тож знімаємо його лише в цій транзакції
        var wrapped = "ALTER TABLE user_permissions NO FORCE ROW LEVEL SECURITY; ALTER TABLE user_access_audit NO FORCE ROW LEVEL SECURITY;\n" + sql +
                      "\nALTER TABLE user_permissions FORCE ROW LEVEL SECURITY; ALTER TABLE user_access_audit FORCE ROW LEVEL SECURITY;";
        await using (var cmd = new NpgsqlCommand(wrapped, conn, tx)) await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    private static async Task ExpectCheck(NpgsqlConnection conn, string sql) =>
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, sql))).SqlState);

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
