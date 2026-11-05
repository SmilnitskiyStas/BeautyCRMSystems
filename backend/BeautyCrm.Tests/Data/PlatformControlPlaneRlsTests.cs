using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-816 (ADR-018 §4/§6): міграція platform_control_plane. external_ref + унікальний індекс, suspended_*, стан користувача
/// invited (password_hash NULL), platform_owner_invites, platform_audit_log (append-only), control_plane_nonces,
/// beauty_platform_feedback (FORCE RLS), вузька lookup-політика tenants за external_ref, права ролі воркера.
/// </summary>
public sealed class PlatformControlPlaneRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public PlatformControlPlaneRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid UserId, string Slug);

    private async Task<Seed> SeedTenantAsync(string? externalRef = null)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var slug = $"cp-{tenantId:N}";
        var ext = externalRef is null ? "NULL, NULL" : $"'{externalRef}', 'shelfguard'";
        await Admin($@"INSERT INTO tenants (id, name, slug, external_ref, external_source) VALUES ('{tenantId}', 'T', '{slug}', {ext});
                       INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                       VALUES ('{userId}', '{tenantId}', 'u@{tenantId:N}.test', 'Owner', 'x', 'owner');");
        return new Seed(tenantId, userId, slug);
    }

    private static string Feedback(Seed s, Dictionary<string, string>? over = null)
    {
        var all = new Dictionary<string, string>
        {
            ["tenant_id"] = $"'{s.TenantId}'", ["user_id"] = $"'{s.UserId}'", ["type"] = "'bug'",
            ["message"] = "'Не працює календар у Chrome'",
        };
        if (over is not null) foreach (var (k, v) in over) all[k] = v;
        return $"INSERT INTO beauty_platform_feedback ({string.Join(", ", all.Keys)}) VALUES ({string.Join(", ", all.Values)})";
    }

    private static Dictionary<string, string> O(params (string k, string v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);

    private static string Invite(Seed s, string hashExpr = "sha256(gen_random_uuid()::text::bytea)") =>
        $@"INSERT INTO platform_owner_invites (tenant_id, user_id, token_hash, expires_at, created_by_actor)
           VALUES ('{s.TenantId}', '{s.UserId}', {hashExpr}, now() + interval '72 hours', 'shelfguard:sg-admin-7')";

    private static string Audit(Dictionary<string, string>? over = null)
    {
        var all = new Dictionary<string, string> { ["action"] = "'tenant.created'", ["actor_type"] = "'shelfguard_admin'", ["actor_id"] = "'sg-admin-7'" };
        if (over is not null) foreach (var (k, v) in over) all[k] = v;
        return $"INSERT INTO platform_audit_log ({string.Join(", ", all.Keys)}) VALUES ({string.Join(", ", all.Values)})";
    }

    // ---------- helpers ----------

    private async Task Rejects(Guid? tenant, string sql, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Run(tenant, sql));
        Assert.Equal(state, ex.SqlState);
        if (constraint is not null) Assert.Equal(constraint, ex.ConstraintName);
    }

    private async Task<int> Run(Guid? tenant, string sql)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> Count(Guid? tenant, string sql)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM ({sql}) q";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task Admin(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string Check = PostgresErrorCodes.CheckViolation;
    private const string Unique = PostgresErrorCodes.UniqueViolation;
    private const string Fk = PostgresErrorCodes.ForeignKeyViolation;
    private const string Denied = PostgresErrorCodes.InsufficientPrivilege;

    // ---------- FORCE / політики ----------

    [SkippableFact]
    public async Task feedback_has_force_rls_and_platform_tables_stay_outside_tenant_rls()
    {
        SkipIfNoDb();
        foreach (var table in new[] { "beauty_platform_feedback", "users", "tenants" })
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
        Assert.Equal(1L, await Scalar(@"SELECT count(*) FROM pg_policies WHERE tablename = 'beauty_platform_feedback' AND policyname = 'tenant_isolation'
            AND qual IS NOT NULL AND with_check IS NOT NULL"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM pg_policies WHERE tablename = 'tenants' AND policyname = 'tenants_control_lookup' AND cmd = 'SELECT'"));
        // ADR-018 §6: платформні таблиці без tenant RLS
        foreach (var table in new[] { "platform_owner_invites", "platform_audit_log", "control_plane_nonces" })
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND NOT relrowsecurity"));
    }

    // ---------- tenants: external_ref, suspended_* ----------

    [SkippableFact]
    public async Task external_ref_is_unique_per_source_and_nulls_are_free()
    {
        SkipIfNoDb();
        var extRef = $"sg-{Guid.NewGuid():N}"[..20];
        await SeedTenantAsync(extRef);
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => SeedTenantAsync(extRef));
        Assert.Equal(Unique, ex.SqlState);
        Assert.Equal("ux_tenants_external_ref", ex.ConstraintName);

        await SeedTenantAsync();   // external_ref NULL дозволений будь-яку кількість разів
        await SeedTenantAsync();
        await SeedTenantAsync($"{extRef}-2"); // інший ref - ок
    }

    [SkippableFact]
    public async Task tenants_external_and_suspension_checks()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        async Task Bad(string set, string constraint)
        {
            var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Admin($"UPDATE tenants SET {set} WHERE id = '{s.TenantId}'"));
            Assert.Equal(Check, ex.SqlState);
            Assert.Equal(constraint, ex.ConstraintName);
        }
        await Bad("external_ref = 'a b', external_source = 'shelfguard'", "ck_tenants_external_ref");
        await Bad("external_ref = '', external_source = 'shelfguard'", "ck_tenants_external_ref");
        await Bad("external_ref = 'sg-1', external_source = 'other'", "ck_tenants_external_source");
        await Bad("external_ref = 'sg-1'", "ck_tenants_external_pair");
        await Bad("external_source = 'shelfguard'", "ck_tenants_external_pair");
        await Bad("suspended_at = now()", "ck_tenants_suspended");                                   // active з suspended_at
        await Bad("suspended_reason = 'x'", "ck_tenants_suspended");

        await Admin($"UPDATE tenants SET status = 'suspended', suspended_at = now(), suspended_reason = 'неоплата' WHERE id = '{s.TenantId}'");
        Assert.Equal(1L, await Scalar($"SELECT count(*) FROM tenants WHERE id = '{s.TenantId}' AND suspended_at IS NOT NULL AND suspended_reason = 'неоплата'"));
        var tooLong = await Assert.ThrowsAnyAsync<PostgresException>(() => Admin($"UPDATE tenants SET suspended_reason = repeat('x', 501) WHERE id = '{s.TenantId}'"));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, tooLong.SqlState);
        await Admin($"UPDATE tenants SET status = 'active', suspended_at = NULL, suspended_reason = NULL WHERE id = '{s.TenantId}'"); // resume
    }

    [SkippableFact]
    public async Task control_lookup_policy_exposes_exactly_one_tenant_by_external_ref_and_select_only()
    {
        SkipIfNoDb();
        var extRef = $"sg-{Guid.NewGuid():N}"[..20];
        var a = await SeedTenantAsync(extRef);
        var b = await SeedTenantAsync($"{extRef}-b");
        await SeedTenantAsync();

        await using var conn = new NpgsqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        async Task<long> N(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
        async Task Set(string value)
        {
            await using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_external_ref', @v, false)", conn);
            cmd.Parameters.AddWithValue("v", value);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(0L, await N("SELECT count(*) FROM tenants"));            // без контексту fail-closed
        await Set(extRef);
        Assert.Equal(1L, await N("SELECT count(*) FROM tenants"));
        Assert.Equal(1L, await N($"SELECT count(*) FROM tenants WHERE id = '{a.TenantId}'"));
        Assert.Equal(0L, await N($"SELECT count(*) FROM tenants WHERE id = '{b.TenantId}'"));
        await using (var upd = new NpgsqlCommand($"UPDATE tenants SET name = 'hijack' WHERE id = '{a.TenantId}'", conn))
            Assert.Equal(0, await upd.ExecuteNonQueryAsync());                // політика лише SELECT: запис не відкрито
        await Set("");
        Assert.Equal(0L, await N("SELECT count(*) FROM tenants"));            // порожнє значення не відкриває NULL-рядки
        await Set("no-such-ref");
        Assert.Equal(0L, await N("SELECT count(*) FROM tenants"));
        Assert.Equal("T", (string)(await Scalar($"SELECT name FROM tenants WHERE id = '{a.TenantId}'"))!);
    }

    // ---------- users: state invited ----------

    [SkippableFact]
    public async Task invited_user_may_lack_password_hash_but_active_may_not()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        Assert.Equal("active", (string)(await Scalar($"SELECT state FROM users WHERE id = '{s.UserId}'"))!); // дефолт для наявних/нових рядків

        string User(string state, string? hash) =>
            $@"INSERT INTO users (tenant_id, email, full_name, password_hash, role, state)
               VALUES ('{s.TenantId}', '{Guid.NewGuid():N}@x.test', 'N', {(hash is null ? "NULL" : $"'{hash}'")}, 'owner', '{state}')";

        await Run(s.TenantId, User("invited", null));
        await Run(s.TenantId, User("invited", "bcrypt"));
        await Run(s.TenantId, User("active", "bcrypt"));
        await Rejects(s.TenantId, User("active", null), Check, "ck_users_password_or_invited");
        await Rejects(s.TenantId, User("pending", "bcrypt"), Check, "ck_users_state");
        // активація: пароль задано -> active
        await Run(s.TenantId, $"UPDATE users SET password_hash = 'h', state = 'active' WHERE tenant_id = '{s.TenantId}' AND state = 'invited' AND password_hash IS NULL");
        Assert.Equal(0L, await Count(s.TenantId, "SELECT 1 FROM users WHERE password_hash IS NULL"));
    }

    // ---------- platform_owner_invites ----------

    [SkippableFact]
    public async Task owner_invites_hash_unique_one_active_per_user_and_tenant_safe_fk()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await Run(null, Invite(a, "decode(repeat('ab', 32), 'hex')"));
        await Rejects(null, Invite(b, "decode(repeat('ab', 32), 'hex')"), Unique, "ix_platform_owner_invites_token_hash");
        await Rejects(null, Invite(a), Unique, "ux_platform_owner_invites_active_user"); // друге активне на того самого користувача
        Assert.Equal(1L, await Count(null, $"SELECT 1 FROM platform_owner_invites WHERE user_id = '{a.UserId}'")); // платформна таблиця видима без tenant-контексту

        // перевипуск: старе відкликано -> нове дозволене
        await Run(null, $"UPDATE platform_owner_invites SET revoked_at = now() WHERE user_id = '{a.UserId}' AND revoked_at IS NULL");
        await Run(null, Invite(a));
        Assert.Equal(2L, await Count(null, $"SELECT 1 FROM platform_owner_invites WHERE user_id = '{a.UserId}'"));
        // погашення: used_at звільняє «активний» слот
        await Run(null, $"UPDATE platform_owner_invites SET used_at = now() WHERE user_id = '{a.UserId}' AND revoked_at IS NULL AND used_at IS NULL");
        await Run(null, Invite(a));

        await Rejects(null, Invite(b, "decode('ab', 'hex')"), Check, "ck_platform_owner_invites_token_hash"); // не 32 байти
        await Rejects(null, $@"UPDATE platform_owner_invites SET used_at = now(), revoked_at = now() WHERE user_id = '{a.UserId}' AND used_at IS NULL AND revoked_at IS NULL",
            Check, "ck_platform_owner_invites_used_or_revoked");
        await Rejects(null, $@"INSERT INTO platform_owner_invites (tenant_id, user_id, token_hash, expires_at, created_by_actor)
            VALUES ('{b.TenantId}', '{b.UserId}', sha256('e'::bytea), now() - interval '1 hour', 'x')", Check, "ck_platform_owner_invites_expires");
        // user з іншого tenant не підставити
        await Rejects(null, $@"INSERT INTO platform_owner_invites (tenant_id, user_id, token_hash, expires_at, created_by_actor)
            VALUES ('{a.TenantId}', '{b.UserId}', sha256('f'::bytea), now() + interval '1 hour', 'x')", Fk);
    }

    // ---------- platform_audit_log ----------

    [SkippableFact]
    public async Task audit_accepts_valid_rows_and_rejects_invalid_ones()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Run(null, Audit(O(("tenant_id", $"'{s.TenantId}'"), ("key_id", "'sg-key-1'"), ("request_id", "'req-1'"), ("ip", "'203.0.113.7'"),
            ("details", "'{\"before\":[\"a\"],\"after\":[\"a\",\"b\"]}'::jsonb"), ("actor_display", "'Admin'"))));
        await Run(null, Audit(O(("action", "'control_auth.failed'"), ("actor_type", "'system'"), ("actor_id", "NULL"))));
        Assert.Equal(1L, await Count(null, $"SELECT 1 FROM platform_audit_log WHERE tenant_id = '{s.TenantId}' AND id > 0 AND occurred_at IS NOT NULL AND details ? 'before'"));
        await Rejects(null, Audit(O(("action", "'tenant.deleted'"))), Check, "ck_platform_audit_log_action");
        await Rejects(null, Audit(O(("actor_type", "'root'"))), Check, "ck_platform_audit_log_actor_type");
        await Rejects(null, Audit(O(("details", "'[1]'::jsonb"))), Check, "ck_platform_audit_log_details");
        // журнал не блокує дії над tenant: tenant_id без FK
        await Run(null, Audit(O(("tenant_id", $"'{Guid.NewGuid()}'"))));
    }

    [SkippableFact]
    public async Task audit_is_append_only_even_for_a_role_with_full_dml_grants()
    {
        SkipIfNoDb();
        await Run(null, Audit());
        // тестова app-роль має GRANT ... ON ALL TABLES (як без REVOKE зі setup-db/grants.sql): блокує тригер
        foreach (var sql in new[] { "UPDATE platform_audit_log SET actor_id = 'x'", "DELETE FROM platform_audit_log", "TRUNCATE platform_audit_log" })
        {
            var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Run(null, sql));
            Assert.Equal(Denied, ex.SqlState);
        }
        Assert.True(await Count(null, "SELECT 1 FROM platform_audit_log") >= 1);
    }

    [SkippableFact]
    public async Task audit_is_append_only_for_a_role_granted_select_and_insert()
    {
        SkipIfNoDb();
        var role = $"beauty_pal_t_{Guid.NewGuid():N}"[..28];
        await Admin($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        try
        {
            await Admin($"GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT, INSERT ON platform_audit_log TO {role}");
            await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
            await conn.OpenAsync();
            async Task Exec(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await Exec($"SET ROLE {role}");
            await Exec(Audit());
            var upd = await Assert.ThrowsAsync<PostgresException>(() => Exec("UPDATE platform_audit_log SET actor_id = 'x'"));
            Assert.Equal(Denied, upd.SqlState);
            Assert.Contains("permission denied", upd.MessageText); // саме GRANT, а не тригер
            var del = await Assert.ThrowsAsync<PostgresException>(() => Exec("DELETE FROM platform_audit_log"));
            Assert.Equal(Denied, del.SqlState);
            Assert.Contains("permission denied", del.MessageText);
            await Exec("RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Admin($"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }

    // ---------- control_plane_nonces ----------

    [SkippableFact]
    public async Task nonce_replay_is_detected_by_primary_key_and_on_conflict_do_nothing()
    {
        SkipIfNoDb();
        var nonce = Guid.NewGuid().ToString("N") + "ab"; // 34 символи
        string Ins(string kid, string n) => $"INSERT INTO control_plane_nonces (key_id, nonce) VALUES ('{kid}', '{n}') ON CONFLICT DO NOTHING";
        Assert.Equal(1, await Run(null, Ins("sg-key-1", nonce)));
        Assert.Equal(0, await Run(null, Ins("sg-key-1", nonce)));  // replay: 0 рядків
        Assert.Equal(1, await Run(null, Ins("sg-key-2", nonce)));  // той самий nonce під іншим kid - не replay
        await Rejects(null, $"INSERT INTO control_plane_nonces (key_id, nonce) VALUES ('sg-key-1', '{nonce}')", Unique, "pk_control_plane_nonces");
        await Rejects(null, Ins("SG", nonce), Check, "ck_control_plane_nonces_key_id");
        await Rejects(null, Ins("sg-key-1", "short"), Check, "ck_control_plane_nonces_nonce");
        await Rejects(null, Ins("sg-key-1", new string('a', 21) + "+"), Check, "ck_control_plane_nonces_nonce");
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM pg_indexes WHERE tablename = 'control_plane_nonces' AND indexname = 'ix_control_plane_nonces_seen_at'"));
    }

    // ---------- beauty_platform_feedback ----------

    [SkippableFact]
    public async Task feedback_is_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Run(a.TenantId, Feedback(a));
        await Run(b.TenantId, Feedback(b));

        Assert.Equal(1L, await Count(a.TenantId, "SELECT 1 FROM beauty_platform_feedback"));
        Assert.Equal(1L, await Count(b.TenantId, "SELECT 1 FROM beauty_platform_feedback"));
        Assert.Equal(0L, await Count(null, "SELECT 1 FROM beauty_platform_feedback"));
        Assert.Equal(0L, await Count(b.TenantId, $"SELECT 1 FROM beauty_platform_feedback WHERE tenant_id = '{a.TenantId}'"));

        await Rejects(b.TenantId, Feedback(a), Denied);                // WITH CHECK
        await Rejects(null, Feedback(a), Denied);
        await Rejects(a.TenantId, "UPDATE beauty_platform_feedback SET tenant_id = '" + b.TenantId + "'", Denied);
        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_platform_feedback SET attempts = 9 WHERE tenant_id = '{a.TenantId}'"));
        Assert.Equal(0, await Run(b.TenantId, $"DELETE FROM beauty_platform_feedback WHERE tenant_id = '{a.TenantId}'"));
    }

    [SkippableFact]
    public async Task feedback_constraints_defaults_and_cross_tenant_fk()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await Run(a.TenantId, Feedback(a));
        Assert.Equal(1L, await Count(a.TenantId,
            @"SELECT 1 FROM beauty_platform_feedback WHERE severity = 'normal' AND delivery_status = 'pending' AND attempts = 0
              AND next_attempt_at IS NOT NULL AND delivered_at IS NULL AND external_lead_id IS NULL AND page_url IS NULL AND last_error IS NULL
              AND id IS NOT NULL AND created_at IS NOT NULL AND updated_at IS NOT NULL"));
        foreach (var t in new[] { "cooperation", "improvement", "bug" })
            await Run(a.TenantId, Feedback(a, O(("type", $"'{t}'"))));
        foreach (var sev in new[] { "low", "normal", "high", "critical" })
            await Run(a.TenantId, Feedback(a, O(("severity", $"'{sev}'"), ("page_url", "'/admin/calendar'"))));

        await Rejects(a.TenantId, Feedback(a, O(("type", "'complaint'"))), Check, "ck_beauty_platform_feedback_type");
        await Rejects(a.TenantId, Feedback(a, O(("severity", "'urgent'"))), Check, "ck_beauty_platform_feedback_severity");
        await Rejects(a.TenantId, Feedback(a, O(("message", "'коротко'"))), Check, "ck_beauty_platform_feedback_message");           // < 10
        await Rejects(a.TenantId, Feedback(a, O(("message", "repeat('я', 2001)"))), PostgresErrorCodes.StringDataRightTruncation);  // > 2000
        await Run(a.TenantId, Feedback(a, O(("message", "repeat('я', 2000)"))));
        await Rejects(a.TenantId, Feedback(a, O(("delivery_status", "'sent'"))), Check, "ck_beauty_platform_feedback_delivery_status");
        await Rejects(a.TenantId, Feedback(a, O(("attempts", "-1"))), Check, "ck_beauty_platform_feedback_attempts");
        await Rejects(a.TenantId, Feedback(a, O(("page_url", "repeat('u', 501)"))), PostgresErrorCodes.StringDataRightTruncation);
        // delivered <=> delivered_at; external_lead_id лише для delivered
        await Rejects(a.TenantId, Feedback(a, O(("delivery_status", "'delivered'"))), Check, "ck_beauty_platform_feedback_delivered");
        await Rejects(a.TenantId, Feedback(a, O(("delivered_at", "now()"))), Check, "ck_beauty_platform_feedback_delivered");
        await Rejects(a.TenantId, Feedback(a, O(("external_lead_id", "'lead-1'"))), Check, "ck_beauty_platform_feedback_lead");
        await Run(a.TenantId, Feedback(a, O(("delivery_status", "'delivered'"), ("delivered_at", "now()"), ("external_lead_id", "'lead-1'"))));
        await Run(a.TenantId, Feedback(a, O(("delivery_status", "'failed'"), ("attempts", "12"), ("last_error", "'http_503'"))));

        // користувач іншого tenant у заявці не підставляється (композитний FK)
        await Rejects(a.TenantId, Feedback(a, O(("user_id", $"'{b.UserId}'"))), Fk);
    }

    [SkippableFact]
    public async Task feedback_ef_round_trip_and_fk_indexes_exist()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        Guid id;
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var row = new PlatformFeedback { UserId = a.UserId, Type = "improvement", Message = "Додайте експорт клієнтів у CSV" };
            ctx.Add(row);
            await ctx.SaveChangesAsync();
            id = row.Id;
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var row = await ctx.PlatformFeedbacks.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(a.TenantId, row.TenantId);
            Assert.Equal("normal", row.Severity);
            Assert.Equal("pending", row.DeliveryStatus);
            Assert.Equal(0, row.Attempts);
            Assert.Null(row.DeliveredAt);
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            await ctx.PlatformAuditLog.AddAsync(new PlatformAuditRow { Action = "owner_invite.issued", ActorType = "shelfguard_admin", ActorId = "sg-admin-7", TenantId = a.TenantId, Details = "{\"userId\":\"x\"}" });
            ctx.Add(new ControlPlaneNonce { KeyId = "sg-key-1", Nonce = Guid.NewGuid().ToString("N") });
            await ctx.SaveChangesAsync();
        }
        // індекс на кожен FK: (tenant_id, user_id, ...) для feedback; (tenant_id, user_id) для запрошень
        Assert.Equal(1L, await Scalar(@"SELECT count(*) FROM pg_indexes WHERE tablename = 'beauty_platform_feedback'
            AND indexname = 'ix_beauty_platform_feedback_tenant_id_user_id_created_at'"));
        Assert.Equal(1L, await Scalar(@"SELECT count(*) FROM pg_indexes WHERE tablename = 'platform_owner_invites'
            AND indexname = 'ix_platform_owner_invites_tenant_id_user_id'"));
    }

    // ---------- права ролі воркера (grants.sql) ----------

    [SkippableFact]
    public async Task worker_role_grants_cover_feedback_delivery_nonce_cleanup_but_not_password_hash()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Run(s.TenantId, Feedback(s));
        await Run(null, $"INSERT INTO control_plane_nonces (key_id, nonce) VALUES ('sg-key-9', '{Guid.NewGuid():N}ab')");
        var role = $"beauty_wk_t_{Guid.NewGuid():N}"[..28];
        await Admin($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        try
        {
            // дзеркало блоку воркера з deploy/staging/db/grants.sql
            await Admin($@"GRANT USAGE ON SCHEMA public TO {role};
                GRANT SELECT ON tenants TO {role};
                GRANT SELECT, UPDATE ON beauty_platform_feedback TO {role};
                GRANT SELECT, DELETE ON control_plane_nonces TO {role};
                GRANT SELECT (id, tenant_id, email, full_name, role) ON users TO {role};");
            await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
            await conn.OpenAsync();
            async Task<int> Exec(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                return await cmd.ExecuteNonQueryAsync();
            }
            async Task<long> N(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                return (long)(await cmd.ExecuteScalarAsync())!;
            }
            async Task Denies(string sql) =>
                Assert.Equal(Denied, (await Assert.ThrowsAsync<PostgresException>(() => Exec(sql))).SqlState);

            await Exec($"SET ROLE {role}");
            await Exec($"SELECT set_config('app.tenant_id', '{s.TenantId}', false)");
            Assert.Equal(1L, await N("SELECT count(*) FROM beauty_platform_feedback"));
            Assert.Equal(1, await Exec("UPDATE beauty_platform_feedback SET attempts = attempts + 1, last_error = 'http_503', next_attempt_at = now() + interval '1 minute'"));
            Assert.Equal(1L, await N("SELECT count(*) FROM users WHERE id IS NOT NULL AND email IS NOT NULL AND full_name IS NOT NULL AND role IS NOT NULL"));
            await Denies("SELECT password_hash FROM users");
            await Denies("SELECT * FROM users");
            await Denies(Feedback(s));
            await Denies("DELETE FROM beauty_platform_feedback");
            await Denies(Audit());
            await Denies("SELECT count(*) FROM platform_audit_log");
            await Denies("SELECT count(*) FROM platform_owner_invites");
            Assert.True(await Exec("DELETE FROM control_plane_nonces WHERE seen_at < now() + interval '1 minute'") >= 1);
            await Exec("RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Admin($"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }
}
