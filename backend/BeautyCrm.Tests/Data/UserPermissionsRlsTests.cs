using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-849 (ADR-023 §2.4, §2.10; контракт §30.9.6): FORCE RLS, композитні tenant-FK, CHECK-формати, append-only журнал
/// (тригер і GRANT), розширені ролі users/invites, access_version. Тести йдуть під NOSUPERUSER NOBYPASSRLS роллю застосунку.
/// </summary>
public sealed class UserPermissionsRlsTests : IClassFixture<PostgresRlsFixture>
{
    private const string Denied = "42501";
    private const string ForeignKeyViolation = "23503";
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";

    private readonly PostgresRlsFixture _db;

    public UserPermissionsRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid OwnerId, Guid AdminId, Guid InviteId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var (ownerId, adminId, inviteId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var slug = "up-" + tenantId.ToString("N")[..20];
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await Exec(admin, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', '{slug}')");
        await Exec(admin, IdentitySeed.UserSql(tenantId, $"o@{slug}.test", "Owner", "x", "owner", ownerId));
        await Exec(admin, IdentitySeed.UserSql(tenantId, $"a@{slug}.test", "Admin", "x", "admin", adminId));
        await Exec(admin, $@"INSERT INTO invites (id, tenant_id, email, role, token_hash, expires_at)
                             VALUES ('{inviteId}', '{tenantId}', 'i@{slug}.test', 'deputy', '{Guid.NewGuid():N}', now() + interval '1 day')");
        return new Seed(tenantId, ownerId, adminId, inviteId);
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

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;
    private static string? Constraint(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

    private static async Task Rejects(Func<Task> act, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        Assert.Equal(state, SqlState(ex));
        if (constraint is not null) Assert.Equal(constraint, Constraint(ex));
    }

    private static UserPermission Perm(Guid tenantId, Guid userId, string permission = "calendar.view", string level = "all", Guid? by = null) =>
        new() { TenantId = tenantId, UserId = userId, Permission = permission, Level = level, UpdatedByUserId = by };

    private static UserAccessAuditRow Audit(Guid tenantId, Guid? target, Guid? invite = null, Guid? actor = null,
        string action = "permissions_changed", string changes = "[{\"permission\":\"calendar.view\",\"from\":\"own\",\"to\":\"all\"}]") =>
        new() { TenantId = tenantId, TargetUserId = target, InviteId = invite, ActorUserId = actor, Action = action, Changes = changes };

    private async Task Save(Guid tenantId, params object[] rows)
    {
        await using var ctx = _db.CreateContext(tenantId);
        ctx.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task new_tables_have_force_rls_and_a_tenant_isolation_policy()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[] { "user_permissions", "user_access_audit" })
        {
            Assert.Equal(1L, await Scalar(admin, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(admin, $"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation'"));
        }
        // нові колонки/таблиці не ослабили RLS на users/invites
        foreach (var table in new[] { "users", "invites" })
            Assert.Equal(1L, await Scalar(admin, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
    }

    [SkippableFact]
    public async Task rows_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Save(a.TenantId, Perm(a.TenantId, a.AdminId), Audit(a.TenantId, a.AdminId, actor: a.OwnerId));

        await using (var other = _db.CreateContext(b.TenantId))
        {
            Assert.Empty(await other.UserPermissions.ToListAsync());
            Assert.Empty(await other.UserAccessAudit.ToListAsync());
            Assert.Equal(0, await other.Database.ExecuteSqlRawAsync("UPDATE user_permissions SET level = 'none'"));
            Assert.Equal(0, await other.Database.ExecuteSqlRawAsync("DELETE FROM user_permissions"));
        }
        await using (var own = _db.CreateContext(a.TenantId))
        {
            Assert.Equal("all", (await own.UserPermissions.AsNoTracking().SingleAsync()).Level);
            Assert.Single(await own.UserAccessAudit.AsNoTracking().ToListAsync());
        }
        await using var none = _db.CreateContext(null);
        Assert.Empty(await none.UserPermissions.ToListAsync());
        Assert.Empty(await none.UserAccessAudit.ToListAsync());
    }

    [SkippableFact]
    public async Task writing_a_row_with_a_foreign_tenant_id_is_blocked_by_with_check()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        foreach (var row in new object[] { Perm(a.TenantId, a.AdminId), Audit(a.TenantId, a.AdminId) })
            await Rejects(() => Save(b.TenantId, row), "42501"); // new row violates row-level security policy
    }

    [SkippableFact]
    public async Task composite_fks_reject_users_and_invites_of_another_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        // права: користувач і автор зміни з іншого tenant
        await Rejects(() => Save(a.TenantId, Perm(a.TenantId, b.AdminId)), ForeignKeyViolation, "fk_user_permissions_users_tenant_id_user_id");
        await Rejects(() => Save(a.TenantId, Perm(a.TenantId, a.AdminId, by: b.OwnerId)), ForeignKeyViolation, "fk_user_permissions_users_tenant_id_updated_by_user_id");
        // журнал: ціль, автор і запрошення з іншого tenant
        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, b.AdminId)), ForeignKeyViolation, "fk_user_access_audit_users_tenant_id_target_user_id");
        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, a.AdminId, actor: b.OwnerId)), ForeignKeyViolation, "fk_user_access_audit_users_tenant_id_actor_user_id");
        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, null, invite: b.InviteId, action: "invited")), ForeignKeyViolation, "fk_user_access_audit_invites_tenant_id_invite_id");
        // власні — приймаються
        await Save(a.TenantId, Perm(a.TenantId, a.AdminId, by: a.OwnerId), Audit(a.TenantId, a.AdminId, actor: a.OwnerId));
    }

    [SkippableFact]
    public async Task permission_and_level_formats_are_checked_and_the_key_is_unique_per_user()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Save(a.TenantId, Perm(a.TenantId, a.AdminId, "calendar.view"), Perm(a.TenantId, a.AdminId, "calendar.manage", "own"),
            Perm(a.TenantId, a.AdminId, "marketing.campaigns", "edit"));
        foreach (var bad in new[] { "calendar", "Calendar.view", "calendar.view.x", "calendar.", ".view", "cal-endar.view", "calendar.view1", new string('a', 33) + ".view" })
            await Rejects(() => Save(a.TenantId, Perm(a.TenantId, a.OwnerId, bad)), CheckViolation, "ck_user_permissions_permission");
        foreach (var bad in new[] { "ALL", "all-levels", "", "a1", new string('a', 17) })
            await Rejects(() => Save(a.TenantId, Perm(a.TenantId, a.OwnerId, level: bad)), CheckViolation, "ck_user_permissions_level");
        await Rejects(() => Save(a.TenantId, Perm(a.TenantId, a.AdminId, "calendar.view", "none")), UniqueViolation, "pk_user_permissions");
        // невідомий ключ/рівень каталогу БД не відхиляє (лише формат): новий ключ не вимагає міграції CHECK
        await Save(a.TenantId, Perm(a.TenantId, a.AdminId, "future_block.new_key", "new_level"));
    }

    [SkippableFact]
    public async Task deleting_a_user_cascades_permissions_but_the_audit_trail_blocks_the_delete()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var extra = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await Exec(admin, IdentitySeed.UserSql(a.TenantId, $"e{extra:N}@x.test", "E", "x", "specialist", extra));
            await Save(a.TenantId, Perm(a.TenantId, extra), Perm(a.TenantId, extra, "calendar.manage", "own"));
            await Exec(admin, $"DELETE FROM users WHERE id = '{extra}'"); // CASCADE
            Assert.Equal(0L, await Scalar(admin, $"SELECT count(*) FROM user_permissions WHERE user_id = '{extra}'"));

            await Save(a.TenantId, Perm(a.TenantId, a.AdminId), Audit(a.TenantId, a.AdminId));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(admin, $"DELETE FROM users WHERE id = '{a.AdminId}'"));
            Assert.Equal(ForeignKeyViolation, ex.SqlState); // журнал історії не губиться разом із користувачем
        }
    }

    [SkippableFact]
    public async Task audit_requires_a_target_or_an_invite_and_validates_action_and_changes()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        // кожна допустима дія
        foreach (var action in new[] { "permissions_changed", "role_changed", "preset_reset", "activated", "deactivated", "migrated" })
            await Save(a.TenantId, Audit(a.TenantId, a.AdminId, action: action));
        // Q-C2: рядки запрошення без цільового користувача
        foreach (var action in new[] { "invited", "invite_revoked", "invite_accepted" })
            await Save(a.TenantId, Audit(a.TenantId, null, invite: a.InviteId, action: action));
        await Save(a.TenantId, Audit(a.TenantId, a.AdminId, invite: a.InviteId, actor: a.OwnerId, action: "invite_accepted",
            changes: "{\"role\":{\"from\":null,\"to\":\"deputy\"}}"));

        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, null, invite: null)), CheckViolation, "ck_user_access_audit_target_or_invite");
        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, null, invite: null, actor: a.OwnerId)), CheckViolation, "ck_user_access_audit_target_or_invite");
        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, a.AdminId, action: "role_deleted")), CheckViolation, "ck_user_access_audit_action");
        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, a.AdminId, changes: "\"text\"")), CheckViolation, "ck_user_access_audit_changes");
        await Rejects(() => Save(a.TenantId, Audit(a.TenantId, a.AdminId, changes: "1")), CheckViolation, "ck_user_access_audit_changes");

        await using var ctx = _db.CreateContext(a.TenantId);
        var row = await ctx.UserAccessAudit.AsNoTracking().FirstAsync(x => x.Action == "migrated");
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.True(row.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5)); // DEFAULT now()
        Assert.Equal(10, await ctx.UserAccessAudit.CountAsync());
    }

    [SkippableFact]
    public async Task audit_is_append_only_even_for_a_role_with_full_dml_grants()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Save(a.TenantId, Audit(a.TenantId, a.AdminId));
        // тестова app-роль має GRANT ... ON ALL TABLES (як без REVOKE зі setup-db/grants.sql): блокує тригер
        foreach (var sql in new[] { "UPDATE user_access_audit SET action = 'migrated'", "DELETE FROM user_access_audit" })
        {
            await using var ctx = _db.CreateContext(a.TenantId);
            var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => ctx.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(Denied, ex.SqlState);
            Assert.Contains("append-only", ex.MessageText);
        }
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString)) // і власник/superuser: тригер, не лише GRANT
        {
            await admin.OpenAsync();
            foreach (var sql in new[] { "UPDATE user_access_audit SET action = 'migrated'", "DELETE FROM user_access_audit", "TRUNCATE user_access_audit" })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(admin, sql));
                Assert.Equal(Denied, ex.SqlState);
                Assert.Contains("append-only", ex.MessageText);
            }
        }
        await using var check = _db.CreateContext(a.TenantId);
        Assert.Single(await check.UserAccessAudit.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task audit_is_append_only_for_a_role_granted_select_and_insert_as_in_grants_sql()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var role = $"beauty_uaa_t_{Guid.NewGuid():N}"[..28];
        await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
        await conn.OpenAsync();
        await Exec(conn, $"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        try
        {
            await Exec(conn, $"GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT, INSERT ON user_access_audit TO {role}");
            await Exec(conn, $"SET ROLE {role}");
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{a.TenantId}', false)");
            await Exec(conn, $"INSERT INTO user_access_audit (tenant_id, target_user_id, action, changes) VALUES ('{a.TenantId}', '{a.AdminId}', 'activated', '[]')");
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM user_access_audit"));
            foreach (var sql in new[] { "UPDATE user_access_audit SET action = 'migrated'", "DELETE FROM user_access_audit" })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, sql));
                Assert.Equal(Denied, ex.SqlState);
                Assert.Contains("permission denied", ex.MessageText); // саме GRANT, а не тригер
            }
            await Exec(conn, "RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new NpgsqlConnection(_db.AdminConnectionString);
            await cleanup.OpenAsync();
            await Exec(cleanup, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }

    [SkippableFact]
    public async Task users_and_invites_accept_deputy_and_access_version_starts_at_one()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using var ctx = _db.CreateContext(a.TenantId);
        Assert.Equal(new[] { 1, 1 }, await ctx.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => u.AccessVersion).ToArrayAsync());

        var email = $"d{Guid.NewGuid():N}@x.test";
        await ctx.Database.ExecuteSqlRawAsync(IdentitySeed.UserSql(a.TenantId, email, "D", "x", "deputy", asApp: true));
        Assert.Equal(1, await ctx.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.AccessVersion).SingleAsync());
        await Rejects(() => ctx.Database.ExecuteSqlRawAsync(IdentitySeed.UserSql(a.TenantId, "m" + email, "M", "x", "manager", asApp: true)),
            CheckViolation, "ck_users_role");
        await Rejects(() => ctx.Database.ExecuteSqlRawAsync($"INSERT INTO invites (tenant_id, email, role, token_hash, expires_at) VALUES ('{a.TenantId}', 'o{email}', 'owner', '{Guid.NewGuid():N}', now() + interval '1 day')"),
            CheckViolation, "ck_invites_role");

        // invites.permissions: NOT NULL, лише JSON-об'єкт (повний знімок); за замовчуванням '{}' (fail-closed)
        Assert.Equal("{}", (await ctx.Invites.AsNoTracking().SingleAsync(i => i.Id == a.InviteId)).Permissions);
        await ctx.Database.ExecuteSqlRawAsync($"UPDATE invites SET permissions = jsonb_build_object('revenue.view', 'own') WHERE id = '{a.InviteId}'");
        foreach (var bad in new[] { "[]", "\"x\"", "1", "null" })
            await Rejects(() => ctx.Database.ExecuteSqlRawAsync($"UPDATE invites SET permissions = '{bad}'::jsonb WHERE id = '{a.InviteId}'"), CheckViolation, "ck_invites_permissions");
        await Rejects(() => ctx.Database.ExecuteSqlRawAsync($"UPDATE invites SET permissions = NULL WHERE id = '{a.InviteId}'"), "23502");
    }

    [SkippableFact]
    public async Task appointment_author_fk_is_tenant_safe_nullable_and_restricts_user_delete()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        async Task<Guid> Id(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, admin);
            return (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        var t = a.TenantId;
        var spec = await Id($"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{t}', 'S', true) RETURNING id");
        var loc = await Id($"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{t}', 'L', 'UTC', true) RETURNING id");
        var svc = await Id($"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{t}', 'S', 60, true) RETURNING id");
        var client = await Id($"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{t}', 'C') RETURNING id");
        var day = 3;
        Task Appt(string createdBy) => Exec(admin, $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at,
                duration_minutes, status, source, price_original, price_final, created_by_user_id)
            VALUES ('{t}', '{loc}', '{spec}', '{svc}', '{client}', now() + interval '{day++} day', 60, 'confirmed', 'online', 100, 100, {createdBy})");

        await Appt("NULL"); // публічні/чат/старі записи
        await Appt($"'{a.AdminId}'");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Appt($"'{b.AdminId}'")); // користувач іншого tenant
        Assert.Equal(ForeignKeyViolation, ex.SqlState);
        Assert.Equal("fk_beauty_appointments_users_tenant_id_created_by_user_id", ex.ConstraintName);
        var del = await Assert.ThrowsAsync<PostgresException>(() => Exec(admin, $"DELETE FROM users WHERE id = '{a.AdminId}'"));
        Assert.Equal(ForeignKeyViolation, del.SqlState); // RESTRICT
    }

    private async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var conn = new NpgsqlConnection(_db.AdminConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static Task InsertAudit(NpgsqlConnection conn, Seed s, string age, string action = "migrated") => Exec(conn,
        $"INSERT INTO user_access_audit (tenant_id, target_user_id, action, changes, created_at) VALUES ('{s.TenantId}', '{s.AdminId}', '{action}', '[]', now() - interval '{age}')");

    [SkippableFact]
    public async Task purge_function_is_the_only_delete_path_and_refuses_anything_younger_than_three_years()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using var admin = await OpenAdminAsync();
        foreach (var s in new[] { a, b })
        {
            await InsertAudit(admin, s, "5 years");
            await InsertAudit(admin, s, "37 months");
            await InsertAudit(admin, s, "2 years");
            await InsertAudit(admin, s, "1 day");
        }
        // властивості: SECURITY DEFINER, власник - NOLOGIN-роль, жодних EXECUTE для PUBLIC і для тестової app-ролі
        Assert.Equal(1L, await Scalar(admin, @"SELECT count(*) FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
            WHERE p.proname = 'purge_user_access_audit' AND p.prosecdef AND r.rolname = 'beautycrm_audit_purge' AND NOT r.rolcanlogin
              AND NOT has_function_privilege('public', p.oid, 'EXECUTE')"));
        await using (var app = _db.CreateContext(a.TenantId))
        {
            var denied = await Assert.ThrowsAnyAsync<PostgresException>(() => app.Database.ExecuteSqlRawAsync("SELECT purge_user_access_audit(now() - interval '4 years')"));
            Assert.Equal(Denied, denied.SqlState);
            // app-роль не може стати власником функції (немає членства): тригер не обійти
            var setRole = await Assert.ThrowsAnyAsync<PostgresException>(() => app.Database.ExecuteSqlRawAsync("SET ROLE beautycrm_audit_purge"));
            Assert.Equal(Denied, setRole.SqlState);
        }

        var role = $"beauty_purge_t_{Guid.NewGuid():N}"[..28];
        await Exec(admin, $"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        try
        {
            await Exec(admin, $"GRANT USAGE ON SCHEMA public TO {role}; GRANT EXECUTE ON FUNCTION public.purge_user_access_audit(timestamptz) TO {role}");
            await using var rc = await OpenAdminAsync(); await Exec(rc, $"SET ROLE {role}");
            foreach (var young in new[] { "now()", "now() - interval '2 years'", "now() - interval '2 years 364 days'", "NULL::timestamptz", "now() + interval '1 day'" })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(rc, $"SELECT purge_user_access_audit({young})"));
                Assert.Equal("22023", ex.SqlState);
            }
            Assert.Equal(8L, await Scalar(admin, $"SELECT count(*) FROM user_access_audit WHERE target_user_id IN ('{a.AdminId}', '{b.AdminId}')")); // ніщо не видалено
            // функція працює між tenant-ами без app.tenant_id: видаляє лише старше 3 років
            var removed = await Scalar(rc, "SELECT purge_user_access_audit(now() - interval '3 years')");
            Assert.True(removed >= 4, $"removed {removed}");
            Assert.Equal(0L, await Scalar(admin, "SELECT count(*) FROM user_access_audit WHERE created_at < now() - interval '3 years'"));
            Assert.Equal(4L, await Scalar(admin, $"SELECT count(*) FROM user_access_audit WHERE target_user_id IN ('{a.AdminId}', '{b.AdminId}')")); // 2 роки й 1 день лишились
            // сама роль-виконавець не отримує зайвого: прямого UPDATE/TRUNCATE немає, DELETE лише через функцію
            var direct = await Assert.ThrowsAsync<PostgresException>(() => Exec(rc, "DELETE FROM user_access_audit"));
            Assert.Equal(Denied, direct.SqlState);
            await Exec(rc, "RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var cleanup = await OpenAdminAsync();
            await Exec(cleanup, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
        // власник функції (роль beautycrm_audit_purge): TRUNCATE й UPDATE недоступні навіть йому
        await Exec(admin, "SET ROLE beautycrm_audit_purge");
        foreach (var sql in new[] { "UPDATE user_access_audit SET action = 'migrated'", "TRUNCATE user_access_audit" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(admin, sql));
            Assert.Equal(Denied, ex.SqlState);
        }
        await Exec(admin, "RESET ROLE");
    }
}
