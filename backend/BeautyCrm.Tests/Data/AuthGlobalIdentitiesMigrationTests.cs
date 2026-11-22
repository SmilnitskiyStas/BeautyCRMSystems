using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Migrations;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-862 (ADR-024 + errata, контракт §31.16): міграція auth_global_identities. Міграція йде під роллю-власником
/// (NOSUPERUSER NOBYPASSRLS, без CREATEROLE: як на staging), перевірки й посів даних - під superuser, ролі застосунку/воркера
/// імітуються через SET ROLE (з реальними іменами beautycrm_app / beautycrm_worker і справжнім deploy/staging/db/grants.sql).
///  1. clean_database: застосування на чистій БД, deny-all таблиці, власність/search_path/EXECUTE функцій, матриця EXECUTE (grants.sql,
///     повторний прогін), RLS (навіть власник таблиці бачить 0 рядків), поведінка кожної функції.
///  2. duplicates: синтетичні дублікати, allowlist (позитив/негатив, B1/B2, колізії), відкликання сесій, ідемпотентність backfill, Down.
///  3. missing_roles: без ролей і без CREATEROLE міграція падає з інструкцією для ops.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class AuthGlobalIdentitiesMigrationTests
{
    private const string Previous = "20261115120000_auth_user_permissions";
    private const string Target = "20261117140201_auth_global_identities";
    private const string Latest = "20261118080034_auth_users_identity_id_immutable";

    // матриця EXECUTE (§31.16): ім'я -> (результат, ролі з EXECUTE)
    private static readonly (string Name, string Result, string[] Roles)[] Matrix =
    [
        ("auth_identity_find", "TABLE(identity_id uuid, password_hash text, state text)", ["app"]),
        ("auth_login_attempt_is_locked", "boolean", ["app"]),
        ("auth_login_attempt_failed", "boolean", ["app"]),
        ("auth_login_attempt_reset", "void", ["app"]),
        ("auth_identity_login_succeeded", "void", ["app"]),
        ("auth_identity_memberships", "TABLE(tenant_id uuid, user_id uuid, tenant_name text, tenant_slug text, role text)", ["app"]),
        ("auth_identity_is_active", "boolean", ["app"]),
        ("auth_identity_email_in_use", "boolean", ["app"]),
        ("auth_identity_create", "uuid", ["app"]),
        ("auth_identity_create_pending", "uuid", ["app"]),
        ("auth_identity_activate_pending", "boolean", ["app"]),
        ("auth_login_ticket_issue", "void", ["app"]),
        ("auth_login_ticket_consume", "uuid", ["app"]),
        ("auth_password_reset_issue", "void", ["app"]),
        ("auth_password_reset_consume", "uuid", ["app"]),
        ("auth_password_reset_claim_delivery", "TABLE(reset_id uuid, email text, kind text)", ["worker"]),
        ("auth_password_reset_attach_token", "boolean", ["worker"]),
        ("auth_password_reset_mark_delivery", "void", ["worker"]),
        ("auth_identity_purge_expired", "bigint", ["worker"]),
        ("auth_identity_release", "void", ["owner"]),
    ];

    private static readonly string[] GlobalTables = ["identities", "auth_login_tickets", "auth_password_resets", "auth_login_attempts"];

    // ================================================================================================================
    // 1. чиста БД
    // ================================================================================================================
    [SkippableFact]
    public async Task clean_database_schema_privileges_rls_and_function_behavior()
    {
        await using var env = await Env.CreateAsync(identityRoles: true);
        var sc = env.Super;
        var ctx = env.NewContext();
        var migrator = env.Migrator(ctx);
        Assert.Contains(Latest, ctx.Database.GetMigrations()); // пізніші міграції (склад) цей тест не зачіпає
        var all = ctx.Database.GetMigrations().ToList();
        Assert.Equal(all.IndexOf(Target) + 1, all.IndexOf(Latest)); // R-3 (users_identity_id_immutable) іде одразу після неї
        await migrator.MigrateAsync(); // усі міграції, під роллю-власником без CREATEROLE

        // ---- схема ----
        foreach (var r in new[] { "beautycrm_identity", "beautycrm_identity_release" })
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_roles WHERE rolname = '{r}' AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolinherit"));
        Assert.Equal("NO", await Text(sc, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'identity_id'"));
        Assert.Equal("YES", await Text(sc, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'sessions_valid_after'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_users_tenant_id_identity_id' AND indexdef LIKE 'CREATE UNIQUE%(tenant_id, identity_id)'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_users_auth_identity_identity_id' AND confdeltype = 'r'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_trigger WHERE tgname = 'users_identity_email_consistency' AND NOT tgisinternal"));
        // F-2: без лічильника на ідентичності; auth_login_attempts без пошти й identity_id
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'identities' AND column_name IN ('failed_login_count', 'lockout_until')"));
        Assert.Equal("login_key,failed_count,window_started_at,locked_until,updated_at",
            await Text(sc, "SELECT string_agg(column_name, ',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_name = 'auth_login_attempts'"));
        // F-9: у черзі листів немає сирого токена
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'auth_password_resets' AND column_name ~ 'token' AND column_name <> 'token_hash'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_auth_password_resets_token_hash' AND indexdef LIKE 'CREATE UNIQUE%WHERE (token_hash IS NOT NULL)'"));
        // колонки черги листа «Пароль змінено» (§31.7.3)
        Assert.Equal(3L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'auth_password_resets' AND column_name IN ('notice_status', 'notice_attempts', 'notice_next_attempt_at')"));
        // CHECK журналу
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_platform_audit_log_actor_type' AND pg_get_constraintdef(oid) LIKE '%''identity''%'"));
        foreach (var a in new[] { "identity.password_reset_completed", "identity.locked", "identity.created", "identity.released" })
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_constraint WHERE conname = 'ck_platform_audit_log_action' AND pg_get_constraintdef(oid) LIKE '%''{a}''%'"));

        // ---- RLS: ENABLE + FORCE, політики лише TO ролей ідентичності (deny-all для app, воркера, PUBLIC) ----
        foreach (var t in GlobalTables)
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{t}') AND relrowsecurity AND relforcerowsecurity"));
        Assert.Equal(0L, await Scalar(sc, @"SELECT count(*) FROM pg_policies
            WHERE tablename IN ('identities', 'auth_login_tickets', 'auth_password_resets', 'auth_login_attempts')
              AND NOT (roles <@ ARRAY['beautycrm_identity', 'beautycrm_identity_release']::name[])"));
        Assert.Equal(0L, await Scalar(sc, @"SELECT count(*) FROM pg_policies WHERE policyname LIKE 'identity%'
            AND tablename IN ('users', 'tenants', 'refresh_tokens') AND NOT (roles <@ ARRAY['beautycrm_identity', 'beautycrm_identity_release']::name[])"));
        foreach (var t in new[] { "users", "refresh_tokens" })
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{t}') AND relrowsecurity AND relforcerowsecurity"));
        // колонкові гранти ролі (ADR-024 §2.3 + sessions_valid_after); без шляху до users.email/password_hash інакше ніж UPDATE password_hash
        Assert.True(await Bool(sc, "SELECT has_column_privilege('beautycrm_identity', 'public.users', 'sessions_valid_after', 'UPDATE')"));
        Assert.True(await Bool(sc, "SELECT has_column_privilege('beautycrm_identity', 'public.users', 'password_hash', 'UPDATE')"));
        Assert.False(await Bool(sc, "SELECT has_column_privilege('beautycrm_identity', 'public.users', 'email', 'UPDATE')"));
        Assert.False(await Bool(sc, "SELECT has_column_privilege('beautycrm_identity', 'public.users', 'password_hash', 'SELECT')"));
        Assert.False(await Bool(sc, "SELECT has_table_privilege('beautycrm_identity', 'public.users', 'INSERT')"));
        Assert.False(await Bool(sc, "SELECT has_table_privilege('beautycrm_identity_release', 'public.identities', 'INSERT')"));

        // ---- функції: власник, SECURITY DEFINER, search_path, без EXECUTE для PUBLIC, вихід RETURNS TABLE з явними колонками ----
        var names = Matrix.Select(m => m.Name).ToHashSet();
        Assert.Equal(names.OrderBy(x => x), (await Column(sc, "SELECT proname FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname LIKE 'auth\\_%' ORDER BY 1")));
        foreach (var (name, result, _) in Matrix)
        {
            var owner = name == "auth_identity_release" ? "beautycrm_identity_release" : "beautycrm_identity";
            Assert.Equal(1L, await Scalar(sc, $@"SELECT count(*) FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
                WHERE p.proname = '{name}' AND p.prosecdef AND r.rolname = '{owner}' AND p.proconfig::text = '{{""search_path=pg_catalog, pg_temp""}}'
                  AND NOT has_function_privilege('public', p.oid, 'EXECUTE') AND pg_get_function_result(p.oid) = '{result}'"));
        }
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner WHERE p.proname = 'users_identity_email_guard' AND p.prosecdef AND r.rolname = 'beautycrm_identity' AND NOT has_function_privilege('public', p.oid, 'EXECUTE')"));

        // ---- гранти середовища: deploy/staging/db/grants.sql (F-8 п.1, 2, 7), повторний прогін не повертає доступ ----
        await env.ApplyGrantsAsync();
        await AssertPrivilegeMatrix(env);
        await env.ApplyGrantsAsync();
        await AssertPrivilegeMatrix(env);
        // регресія: масовий GRANT після REVOKE (імітація «нова таблиця отримала ALL TABLES»); повторний grants.sql знову закриває
        await Exec(sc, "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO beautycrm_app");
        await Exec(sc, "GRANT EXECUTE ON FUNCTION public.auth_identity_find(text) TO PUBLIC");
        await env.ApplyGrantsAsync();
        await AssertPrivilegeMatrix(env);

        // ---- дані для функціональних перевірок ----
        var (tA, tB, tC, tSusp) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var (t, slug, status) in new[] { (tA, "alpha", "active"), (tB, "beta", "active"), (tC, "gamma", "active"), (tSusp, "delta", "suspended") })
            await Exec(sc, $"INSERT INTO tenants (id, name, slug, status, suspended_at, suspended_reason) VALUES ('{t}', 'Salon {slug}', '{slug}-{t:N}', '{status}', {(status == "suspended" ? "now()" : "NULL")}, {(status == "suspended" ? "'t'" : "NULL")})");

        // === create / find / email_in_use / квота / аудит ===
        var id1 = await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('  Ann@Salon.TEST ', 'bcrypt-hash-1', '{tA}', 'staff_invite', 2)"));
        Assert.Equal("ann@salon.test", await Text(sc, $"SELECT email FROM identities WHERE id = '{id1}'")); // нормалізація всередині
        Assert.Equal("active", await Text(sc, $"SELECT state FROM identities WHERE id = '{id1}'"));
        Assert.Equal(1L, await Scalar(sc, $@"SELECT count(*) FROM platform_audit_log WHERE action = 'identity.created' AND tenant_id = '{tA}' AND actor_type = 'identity'
            AND details = jsonb_build_object('identityId', '{id1}'::uuid, 'via', 'staff_invite')"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM platform_audit_log WHERE details::text ILIKE '%salon.test%' OR actor_id ILIKE '%salon.test%'")); // без ПІІ
        Assert.True(await env.AsApp(c => Bool(c, "SELECT public.auth_identity_email_in_use('ANN@salon.test')")));
        Assert.False(await env.AsApp(c => Bool(c, "SELECT public.auth_identity_email_in_use('nobody@salon.test')")));
        var dup = await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('ann@salon.test', 'h', '{tB}', 'staff_invite', 20)")));
        Assert.Equal("23505", dup.SqlState);
        Assert.Equal("ux_identities_email", dup.ConstraintName);
        var id2 = await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('bob@salon.test', 'bcrypt-hash-2', '{tA}', 'staff_invite', 2)"));
        var quota = await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('carl@salon.test', 'h', '{tA}', 'staff_invite', 2)")));
        Assert.Equal("P0001", quota.SqlState);
        Assert.Equal("identity_quota", quota.MessageText);
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM identities WHERE email = 'carl@salon.test'"));
        await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('dan@salon.test', 'h', '{tB}', 'staff_invite', 2)")); // квота рахується на tenant
        await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('eve@salon.test', 'h', '{tA}', 'platform', 2)")); // не staff_invite: квоти немає
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('x@salon.test', 'h', '{tA}', 'bogus', 2)")))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('x@salon.test', '', '{tA}', 'platform', 2)")))).SqlState);
        // ковзне вікно: рядки старші за 24 год у квоту не входять (журнал append-only: тригер вимикаємо лише в тесті)
        await Exec(sc, "ALTER TABLE platform_audit_log DISABLE TRIGGER platform_audit_log_append_only");
        await Exec(sc, $"UPDATE platform_audit_log SET occurred_at = now() - interval '25 hours' WHERE action = 'identity.created' AND tenant_id = '{tA}' AND details ->> 'via' = 'staff_invite'");
        await Exec(sc, "ALTER TABLE platform_audit_log ENABLE TRIGGER platform_audit_log_append_only");
        await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('carl@salon.test', 'h', '{tA}', 'staff_invite', 2)"));

        // find: явні колонки, нормалізація, 0/1 рядок
        await using (var c = await env.OpenAsApp())
        {
            Assert.Equal(["identity_id", "password_hash", "state"], await ColumnNames(c, "SELECT * FROM public.auth_identity_find('Ann@SALON.test')"));
            var rows = await Rows(c, "SELECT identity_id, password_hash, state FROM public.auth_identity_find(' ANN@salon.test')");
            Assert.Single(rows);
            Assert.Equal((id1, "bcrypt-hash-1", "active"), ((Guid)rows[0][0]!, (string)rows[0][1]!, (string)rows[0][2]!));
            Assert.Empty(await Rows(c, "SELECT * FROM public.auth_identity_find('nobody@salon.test')"));
        }

        // create_pending + activate_pending (рівно одне членство)
        var pend = await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create_pending('Owner@Salon.TEST', '{tC}', 'owner_provisioning')"));
        Assert.Equal("pending", await Text(sc, $"SELECT state FROM identities WHERE id = '{pend}'"));
        Assert.True(await env.AsApp(c => Bool(c, "SELECT public.auth_identity_email_in_use('owner@salon.test')"))); // pending теж займає пошту
        Assert.False(await env.AsApp(c => Bool(c, $"SELECT public.auth_identity_activate_pending('{pend}', 'new-hash')"))); // 0 членств
        await SeedUser(sc, tC, pend, "owner@salon.test", "owner", state: "invited", hash: null);
        Assert.True(await env.AsApp(c => Bool(c, $"SELECT public.auth_identity_activate_pending('{pend}', 'new-hash')")));
        Assert.Equal("active", await Text(sc, $"SELECT state FROM identities WHERE id = '{pend}'"));
        Assert.False(await env.AsApp(c => Bool(c, $"SELECT public.auth_identity_activate_pending('{pend}', 'again')"))); // вже active
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM platform_audit_log WHERE action = 'identity.created' AND details ->> 'via' = 'owner_provisioning' AND tenant_id = '{tC}'"));
        var pend2 = await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create_pending('multi@salon.test', '{tC}', 'platform')"));
        await SeedUser(sc, tA, pend2, "multi@salon.test", "owner", state: "invited", hash: null);
        await SeedUser(sc, tB, pend2, "multi@salon.test", "owner", state: "invited", hash: null);
        Assert.False(await env.AsApp(c => Bool(c, $"SELECT public.auth_identity_activate_pending('{pend2}', 'h')"))); // кілька членств
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create_pending('z@salon.test', '{tC}', 'staff_invite')")))).SqlState);

        // === memberships (лише активні членства активних tenant), ізоляція між ідентичностями ===
        var mA = await SeedUser(sc, tA, id1, "ann@salon.test", "admin");
        var mB = await SeedUser(sc, tB, id1, "ann@salon.test", "specialist");
        await SeedUser(sc, tC, id1, "ann@salon.test", "admin", active: false); // вимкнене
        await SeedUser(sc, tSusp, id1, "ann@salon.test", "admin"); // tenant призупинено
        await SeedUser(sc, tA, id2, "bob@salon.test", "admin");
        await using (var c = await env.OpenAsApp())
        {
            Assert.Equal(["tenant_id", "user_id", "tenant_name", "tenant_slug", "role"], await ColumnNames(c, $"SELECT * FROM public.auth_identity_memberships('{id1}')"));
            var m = await Rows(c, $"SELECT tenant_id, user_id, tenant_name, role FROM public.auth_identity_memberships('{id1}') ORDER BY tenant_name");
            Assert.Equal(2, m.Count);
            Assert.Equal([(tA, mA, "Salon alpha", "admin"), (tB, mB, "Salon beta", "specialist")], m.Select(r => ((Guid)r[0]!, (Guid)r[1]!, (string)r[2]!, (string)r[3]!)).ToArray());
            Assert.Single(await Rows(c, $"SELECT * FROM public.auth_identity_memberships('{id2}')"));
            Assert.Empty(await Rows(c, $"SELECT * FROM public.auth_identity_memberships('{Guid.NewGuid()}')"));
            Assert.True(await Bool(c, $"SELECT public.auth_identity_is_active('{id1}')"));
            Assert.False(await Bool(c, $"SELECT public.auth_identity_is_active('{pend2}')")); // pending
            Assert.False(await Bool(c, $"SELECT public.auth_identity_is_active('{Guid.NewGuid()}')"));
        }
        await env.AsAppDo(c => Exec(c, $"SELECT public.auth_identity_login_succeeded('{id1}', timestamptz '2030-01-01 10:00:00+00')"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM identities WHERE id = '{id1}' AND last_login_at = timestamptz '2030-01-01 10:00:00+00'"));

        // === лічильник невдалих входів (F-2) ===
        var k1 = "decode(repeat('a1', 32), 'hex')";
        var t0 = "timestamptz '2030-01-01 12:00:00+00'";
        string Failed(string key, string ident, string at, int max = 5, int window = 900, int lockout = 900) =>
            $"SELECT public.auth_login_attempt_failed({key}, {ident}, {max}, {window}, {lockout}, {at})";
        for (var i = 0; i < 4; i++)
            Assert.False(await env.AsApp(c => Bool(c, Failed(k1, $"'{id1}'", $"{t0} + interval '{i} seconds'"))));
        Assert.Equal(4L, await Scalar(sc, "SELECT failed_count FROM auth_login_attempts WHERE login_key = decode(repeat('a1', 32), 'hex')"));
        Assert.False(await env.AsApp(c => Bool(c, $"SELECT public.auth_login_attempt_is_locked({k1}, {t0} + interval '10 seconds')")));
        Assert.True(await env.AsApp(c => Bool(c, Failed(k1, $"'{id1}'", $"{t0} + interval '5 seconds'")))); // 5-та: блокування
        Assert.Equal(0L, await Scalar(sc, "SELECT failed_count FROM auth_login_attempts WHERE login_key = decode(repeat('a1', 32), 'hex')"));
        Assert.True(await env.AsApp(c => Bool(c, $"SELECT public.auth_login_attempt_is_locked({k1}, {t0} + interval '6 seconds')")));
        Assert.True(await env.AsApp(c => Bool(c, Failed(k1, $"'{id1}'", $"{t0} + interval '7 seconds'")))); // у блокуванні рядок не змінюється
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM platform_audit_log WHERE action = 'identity.locked' AND details = jsonb_build_object('identityId', '{id1}'::uuid)")); // один запис на блокування
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM auth_login_attempts WHERE login_key = decode(repeat('a1', 32), 'hex') AND locked_until = timestamptz '2030-01-01 12:00:05+00' + interval '900 seconds'"));
        Assert.False(await env.AsApp(c => Bool(c, $"SELECT public.auth_login_attempt_is_locked({k1}, {t0} + interval '906 seconds')"))); // блокування сплило
        Assert.False(await env.AsApp(c => Bool(c, Failed(k1, "NULL", $"{t0} + interval '1000 seconds'"))));
        Assert.Equal(1L, await Scalar(sc, "SELECT failed_count FROM auth_login_attempts WHERE login_key = decode(repeat('a1', 32), 'hex')")); // після спливу лічильник з нуля
        // вікно: 3 невдалі, пауза > window -> знову з 1; невідома пошта (identity NULL) не пише identity.locked
        var k2 = "decode(repeat('b2', 32), 'hex')";
        for (var i = 0; i < 3; i++) await env.AsApp(c => Bool(c, Failed(k2, "NULL", $"{t0} + interval '{i} seconds'")));
        Assert.False(await env.AsApp(c => Bool(c, Failed(k2, "NULL", $"{t0} + interval '901 seconds'"))));
        Assert.Equal(1L, await Scalar(sc, "SELECT failed_count FROM auth_login_attempts WHERE login_key = decode(repeat('b2', 32), 'hex')"));
        var lockedBefore = await Scalar(sc, "SELECT count(*) FROM platform_audit_log WHERE action = 'identity.locked'");
        var k3 = "decode(repeat('c3', 32), 'hex')";
        Assert.True(await env.AsApp(c => Bool(c, Failed(k3, "NULL", t0, max: 1)))); // max = 1: блокування одразу
        Assert.Equal(lockedBefore, await Scalar(sc, "SELECT count(*) FROM platform_audit_log WHERE action = 'identity.locked'"));
        await env.AsAppDo(c => Exec(c, $"SELECT public.auth_login_attempt_reset({k3})"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM auth_login_attempts WHERE login_key = decode(repeat('c3', 32), 'hex')"));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => Bool(c, Failed("decode('abcd', 'hex')", "NULL", t0))))).SqlState); // ключ не 32 байти
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc, "INSERT INTO auth_login_attempts (login_key, failed_count, window_started_at) VALUES (decode('ab', 'hex'), 0, now())"))).SqlState);

        // === квитки вибору бізнесу (кожна видача - новий токен; у БД лише sha256) ===
        static string H(int n) => $"decode(repeat('{n:x2}', 32), 'hex')";
        string Issue(int n, object ident) => $"SELECT public.auth_login_ticket_issue('{ident}', {H(n)}, now() + interval '3 minutes')";
        string Consume(int n, Guid tenant, string at = "now()") => $"SELECT public.auth_login_ticket_consume({H(n)}, '{tenant}', {at})";
        await env.AsAppDo(c => Exec(c, Issue(1, id1)));
        Assert.Equal(mB, await env.AsApp(c => NullableGuid(c, Consume(1, tB)))); // повертає users.id членства
        Assert.Null(await env.AsApp(c => NullableGuid(c, Consume(1, tB)))); // одноразовий
        // Q-C5: будь-який запит із чинним квитком гасить його, навіть якщо tenant не підійшов; повтор уже не працює
        await env.AsAppDo(c => Exec(c, Issue(2, id1)));
        Assert.Null(await env.AsApp(c => NullableGuid(c, Consume(2, tSusp)))); // членство в призупиненому tenant
        Assert.Null(await env.AsApp(c => NullableGuid(c, Consume(2, tA))));
        await env.AsAppDo(c => Exec(c, Issue(3, id1)));
        await env.AsAppDo(c => Exec(c, Issue(4, id1))); // нова видача гасить попередній
        Assert.Null(await env.AsApp(c => NullableGuid(c, Consume(3, tA))));
        Assert.Equal(mA, await env.AsApp(c => NullableGuid(c, Consume(4, tA))));
        await env.AsAppDo(c => Exec(c, Issue(5, id1)));
        Assert.Null(await env.AsApp(c => NullableGuid(c, Consume(5, tA, "now() + interval '4 minutes'")))); // прострочений
        await env.AsAppDo(c => Exec(c, Issue(6, id2)));
        Assert.Null(await env.AsApp(c => NullableGuid(c, Consume(6, tB)))); // tenant не з ідентичності квитка
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsAppDo(c => Exec(c, $"SELECT public.auth_login_ticket_issue('{id1}', {H(7)}, now() + interval '2 hours')")))).SqlState);
        Assert.Equal("P0002", (await Assert.ThrowsAsync<PostgresException>(() => env.AsAppDo(c => Exec(c, Issue(8, pend2))))).SqlState); // pending не отримує квиток
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'auth_login_tickets' AND column_name ILIKE '%token%' AND column_name <> 'token_hash'"));

        // === скидання пароля (F-3, F-6, F-8, F-9, F-10) ===
        var lk = "decode(repeat('e1', 32), 'hex')";
        await env.AsApp(c => Bool(c, Failed(lk, $"'{id1}'", "now()")));
        await SeedRefresh(sc, tA, mA); await SeedRefresh(sc, tB, mB);
        await env.AsAppDo(c => Exec(c, Issue(9, id1))); // квиток, який скидання має погасити
        Assert.Empty(await Column(sc, "SELECT 1 FROM auth_password_resets"));
        await env.AsAppDo(c => Exec(c, $"SELECT public.auth_password_reset_issue('nobody@salon.test', {lk}, now() + interval '30 minutes')"));
        await env.AsAppDo(c => Exec(c, $"SELECT public.auth_password_reset_issue('multi@salon.test', {lk}, now() + interval '30 minutes')")); // pending
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM auth_password_resets")); // невідома/pending пошта: нічого
        await env.AsAppDo(c => Exec(c, $"SELECT public.auth_password_reset_issue(' ANN@Salon.test', {lk}, now() + interval '30 minutes')"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE identity_id = '{id1}' AND token_hash IS NULL AND delivery_status = 'pending' AND notice_status = 'none'"));
        var firstReset = await GuidOf(sc, "SELECT id FROM auth_password_resets");
        await env.AsAppDo(c => Exec(c, $"SELECT public.auth_password_reset_issue('ann@salon.test', {lk}, now() + interval '30 minutes')")); // гасить попередній
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE superseded_at IS NOT NULL AND id = '{firstReset}'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM auth_password_resets WHERE superseded_at IS NULL AND consumed_at IS NULL"));
        var reset = await GuidOf(sc, "SELECT id FROM auth_password_resets WHERE superseded_at IS NULL");
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsAppDo(c => Exec(c, $"SELECT public.auth_password_reset_issue('ann@salon.test', {lk}, now() + interval '2 days')")))).SqlState);

        // воркер: claim (лізинг) -> attach_token -> mark_delivery; app таких функцій не бачить (матриця)
        await using (var w = await env.OpenAsWorker())
        {
            Assert.Equal(["reset_id", "email", "kind"], await ColumnNames(w, "SELECT * FROM public.auth_password_reset_claim_delivery(now(), 60, 10)"));
        }
        await Exec(sc, $"UPDATE auth_password_resets SET next_attempt_at = now() WHERE id = '{reset}'"); // повернути з лізингу
        var claimed = await env.AsWorker(c => Rows(c, "SELECT reset_id, email, kind FROM public.auth_password_reset_claim_delivery(now(), 60, 10)"));
        Assert.Single(claimed);
        Assert.Equal((reset, "ann@salon.test", "reset_link"), ((Guid)claimed[0][0]!, (string)claimed[0][1]!, (string)claimed[0][2]!));
        Assert.Empty(await env.AsWorker(c => Rows(c, "SELECT * FROM public.auth_password_reset_claim_delivery(now(), 60, 10)"))); // лізинг тримає
        var tokenHash = "decode(repeat('f7', 32), 'hex')";
        Assert.False(await env.AsWorker(c => Bool(c, $"SELECT public.auth_password_reset_attach_token('{firstReset}', {tokenHash}, now())"))); // погашений новою видачею
        Assert.True(await env.AsWorker(c => Bool(c, $"SELECT public.auth_password_reset_attach_token('{reset}', {tokenHash}, now())")));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsWorker(c => Bool(c, $"SELECT public.auth_password_reset_attach_token('{reset}', decode('ab', 'hex'), now())")))).SqlState);
        await env.AsWorkerDo(c => Exec(c, $"SELECT public.auth_password_reset_mark_delivery('{reset}', 'reset_link', 'pending', 'smtp_timeout', now() + interval '1 minute')"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE id = '{reset}' AND delivery_status = 'pending' AND attempts = 1 AND last_error = 'smtp_timeout' AND next_attempt_at > now()"));
        await env.AsWorkerDo(c => Exec(c, $"SELECT public.auth_password_reset_mark_delivery('{reset}', 'reset_link', 'pending', 'a@b.c: bad token', now() + interval '1 minute')"));
        Assert.Equal("unknown", await Text(sc, $"SELECT last_error FROM auth_password_resets WHERE id = '{reset}'")); // лише код: пошта/токен у БД не потрапляють
        await env.AsWorkerDo(c => Exec(c, $"SELECT public.auth_password_reset_mark_delivery('{reset}', 'reset_link', 'sent', NULL, NULL)"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE id = '{reset}' AND delivery_status = 'sent' AND attempts = 3 AND last_error IS NULL"));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsWorkerDo(c => Exec(c, $"SELECT public.auth_password_reset_mark_delivery('{reset}', 'x', 'sent', NULL, NULL)")))).SqlState);

        // consume: ОДИН UPDATE по token_hash; усі побічні ефекти в одній транзакції
        Assert.Null(await env.AsApp(c => NullableGuid(c, "SELECT public.auth_password_reset_consume(decode(repeat('00', 32), 'hex'), 'new', now())"))); // невідомий токен
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => NullableGuid(c, $"SELECT public.auth_password_reset_consume({tokenHash}, '', now())")))).SqlState);
        Assert.Null(await env.AsApp(c => NullableGuid(c, $"SELECT public.auth_password_reset_consume({tokenHash}, 'new', now() + interval '31 minutes')"))); // прострочений: токен лишається чинним
        Assert.Equal(id1, await env.AsApp(c => NullableGuid(c, $"SELECT public.auth_password_reset_consume({tokenHash}, 'brand-new-hash', now())")));
        Assert.Null(await env.AsApp(c => NullableGuid(c, $"SELECT public.auth_password_reset_consume({tokenHash}, 'again', now())"))); // одноразовий
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM identities WHERE id = '{id1}' AND password_hash = 'brand-new-hash' AND password_changed_at IS NOT NULL"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM auth_login_attempts WHERE login_key = decode(repeat('e1', 32), 'hex')")); // блокування знято за login_key
        Assert.Equal(4L, await Scalar(sc, $"SELECT count(*) FROM users WHERE identity_id = '{id1}' AND password_hash = 'brand-new-hash' AND sessions_valid_after IS NOT NULL")); // dual-write + sessions_valid_after усіх членств
        Assert.Equal(2L, await Scalar(sc, $"SELECT count(*) FROM refresh_tokens rt JOIN users u ON u.id = rt.user_id AND u.tenant_id = rt.tenant_id WHERE u.identity_id = '{id1}' AND rt.revoked_at IS NOT NULL"));
        Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM auth_login_tickets WHERE identity_id = '{id1}' AND consumed_at IS NULL"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE id = '{reset}' AND consumed_at IS NOT NULL AND notice_status = 'pending' AND notice_attempts = 0"));
        Assert.Equal(1L, await Scalar(sc, $@"SELECT count(*) FROM platform_audit_log WHERE action = 'identity.password_reset_completed' AND tenant_id IS NULL AND actor_type = 'identity'
            AND details = jsonb_build_object('identityId', '{id1}'::uuid)"));
        // лист «Пароль змінено» (kind = changed_notice), воркер (TASK-872) надсилає й позначає sent/failed
        var notice = await env.AsWorker(c => Rows(c, "SELECT reset_id, email, kind FROM public.auth_password_reset_claim_delivery(now() + interval '1 second', 60, 10)"));
        Assert.Single(notice);
        Assert.Equal((reset, "ann@salon.test", "changed_notice"), ((Guid)notice[0][0]!, (string)notice[0][1]!, (string)notice[0][2]!));
        await env.AsWorkerDo(c => Exec(c, $"SELECT public.auth_password_reset_mark_delivery('{reset}', 'changed_notice', 'failed', 'not_implemented', NULL)"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE id = '{reset}' AND notice_status = 'failed' AND notice_attempts = 1 AND last_error = 'not_implemented' AND delivery_status = 'sent'"));
        Assert.Empty(await env.AsWorker(c => Rows(c, "SELECT * FROM public.auth_password_reset_claim_delivery(now() + interval '1 day', 60, 10)"))); // нічого не лишилось

        // purge (воркер): прострочене видаляється; рядок з листом «Пароль змінено» в черзі, діюче блокування й чинні записи лишаються
        await env.AsAppDo(c => Exec(c, $"SELECT public.auth_password_reset_issue('bob@salon.test', {lk}, now() + interval '30 minutes')"));
        var keepNotice = await GuidOf(sc, $"SELECT id FROM auth_password_resets WHERE identity_id = '{id2}'");
        await Exec(sc, $"UPDATE auth_password_resets SET consumed_at = now(), notice_status = 'pending', notice_next_attempt_at = now() WHERE id = '{keepNotice}'");
        await env.AsApp(c => Bool(c, Failed("decode(repeat('9a', 32), 'hex')", "NULL", "timestamptz '2033-06-01 00:00:00+00'", max: 1))); // діюче блокування (до 2033)
        var purged = await env.AsWorker(c => Long(c, "SELECT public.auth_identity_purge_expired(timestamptz '2032-01-01 00:00:00+00')"));
        Assert.True(purged >= 3, $"purged {purged}");
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM auth_login_tickets"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE id = '{keepNotice}'"));
        Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM auth_password_resets WHERE id = '{reset}'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM auth_login_attempts WHERE login_key = decode(repeat('9a', 32), 'hex')"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM auth_login_attempts WHERE login_key = decode(repeat('a1', 32), 'hex')"));

        // === тригер узгодженості пошти ===
        var tTrg = Guid.NewGuid();
        await Exec(sc, $"INSERT INTO tenants (id, name, slug) VALUES ('{tTrg}', 'T', 'trg-{tTrg:N}')");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc,
            $"INSERT INTO users (tenant_id, email, full_name, password_hash, role, identity_id) VALUES ('{tTrg}', 'other@salon.test', 'X', 'x', 'admin', '{id2}')"))).SqlState);
        await Exec(sc, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role, identity_id) VALUES ('{tTrg}', 'Bob@Salon.TEST ', 'X', 'x', 'admin', '{id2}')"); // регістр/пробіли толерантні
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc, $"UPDATE users SET email = 'moved@salon.test' WHERE tenant_id = '{tTrg}'"))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc,
            $"INSERT INTO users (tenant_id, email, full_name, password_hash, role, identity_id) VALUES ('{tTrg}', 'n@salon.test', 'X', 'x', 'admin', NULL)"))).SqlState); // вставка без ідентичності неможлива (тригер раніше за NOT NULL)
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc,
            $"INSERT INTO users (tenant_id, email, full_name, password_hash, role, identity_id) VALUES ('{tTrg}', 'bob@salon.test', 'X', 'x', 'admin', '{id2}')"))).SqlState); // UNIQUE (tenant_id, identity_id)

        // === R-3: users.identity_id незмінний (UPDATE іншого значення відхиляється, навіть для власника/superuser; те саме значення - ні) ===
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_trigger WHERE tgname = 'users_identity_id_immutable' AND NOT tgisinternal"));
        // пошта змінюється разом з identity_id, щоб тригер узгодженості пошти пропустив зміну і спрацював саме тригер незмінності
        var repoint = $"UPDATE users SET identity_id = '{id1}', email = 'ann@salon.test' WHERE tenant_id = '{tTrg}'";
        var ex1 = await Assert.ThrowsAsync<PostgresException>(() => Exec(sc, repoint));
        Assert.Equal("23514", ex1.SqlState);
        Assert.Contains("immutable", ex1.MessageText);
        var ex2 = await Assert.ThrowsAsync<PostgresException>(() => env.AsAppDo(c => Exec(c, $"SELECT set_config('app.tenant_id', '{tTrg}', true); {repoint}"))); // роль app (UPDATE на users є), під RLS свого tenant
        Assert.Equal("23514", ex2.SqlState);
        Assert.Contains("immutable", ex2.MessageText);
        Assert.Equal(id2, await GuidOf(sc, $"SELECT identity_id FROM users WHERE tenant_id = '{tTrg}'")); // нічого не змінено
        await Exec(sc, $"UPDATE users SET identity_id = '{id2}', full_name = 'Same' WHERE tenant_id = '{tTrg}'"); // те саме значення - дозволено
        Assert.Equal(id2, await GuidOf(sc, $"SELECT identity_id FROM users WHERE tenant_id = '{tTrg}'"));

        // === auth_identity_release (F-4): лише ops-роль; app і воркер не мають EXECUTE ===
        await SeedRefresh(sc, tA, mA);
        await env.AsAppDo(c => Exec(c, Issue(10, id1)));
        var accessBefore = await Scalar(sc, $"SELECT access_version FROM users WHERE id = '{mA}'");
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => env.AsAppDo(c => Exec(c, $"SELECT public.auth_identity_release('{id1}', 'owner_request')")))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => env.AsWorkerDo(c => Exec(c, $"SELECT public.auth_identity_release('{id1}', 'owner_request')")))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => env.AsOpsDo(c => Exec(c, $"SELECT public.auth_identity_release('{id1}', 'has space')")))).SqlState);
        Assert.Equal("P0002", (await Assert.ThrowsAsync<PostgresException>(() => env.AsOpsDo(c => Exec(c, $"SELECT public.auth_identity_release('{Guid.NewGuid()}', 'owner_request')")))).SqlState);
        await env.AsOpsDo(c => Exec(c, $"SELECT public.auth_identity_release('{id1}', 'owner_request')"));
        var released = $"released+{id1}@released.invalid";
        Assert.Equal(released, await Text(sc, $"SELECT email FROM identities WHERE id = '{id1}'"));
        Assert.Equal(4L, await Scalar(sc, $"SELECT count(*) FROM users WHERE identity_id = '{id1}' AND email = '{released}' AND NOT is_active AND sessions_valid_after IS NOT NULL"));
        Assert.Equal(accessBefore + 1, await Scalar(sc, $"SELECT access_version FROM users WHERE id = '{mA}'"));
        Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM refresh_tokens rt JOIN users u ON u.id = rt.user_id AND u.tenant_id = rt.tenant_id WHERE u.identity_id = '{id1}' AND rt.revoked_at IS NULL"));
        Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM auth_login_tickets WHERE identity_id = '{id1}' AND consumed_at IS NULL"));
        Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM platform_audit_log WHERE action = 'identity.released' AND tenant_id IS NULL AND details = jsonb_build_object('identityId', '{id1}'::uuid, 'reasonCode', 'owner_request')"));
        Assert.False(await env.AsApp(c => Bool(c, "SELECT public.auth_identity_email_in_use('ann@salon.test')"))); // пошту звільнено
        await env.AsApp(c => GuidOf(c, $"SELECT public.auth_identity_create('ann@salon.test', 'h', '{tA}', 'platform', 20)"));
        Assert.Empty(await env.AsApp(c => Rows(c, $"SELECT * FROM public.auth_identity_memberships('{id1}')"))); // звільнена ідентичність без активних членств

        // === deny-all: пряме читання/запис таблиць і RLS навіть при випадковому GRANT; власник таблиці теж бачить 0 рядків ===
        foreach (var t in GlobalTables)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => env.AsApp(c => Scalar(c, $"SELECT count(*) FROM {t}")));
            Assert.Equal("42501", ex.SqlState);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => env.AsWorker(c => Scalar(c, $"SELECT count(*) FROM {t}")))).SqlState);
        }
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => env.AsAppDo(c => Exec(c, "INSERT INTO identities (email, password_hash, state) VALUES ('hack@x.test', 'h', 'active')")))).SqlState);
        Assert.True(await Scalar(sc, "SELECT count(*) FROM identities") > 0);
        await using (var ownerConn = await Open(env.OwnerCs)) // власник таблиць (мігратор): FORCE RLS і INHERIT FALSE
        {
            foreach (var t in GlobalTables) Assert.Equal(0L, await Scalar(ownerConn, $"SELECT count(*) FROM {t}"));
        }
        await Exec(sc, "GRANT SELECT, INSERT, UPDATE, DELETE ON identities, auth_login_tickets, auth_password_resets, auth_login_attempts TO beautycrm_app, beautycrm_worker");
        foreach (var t in GlobalTables)
        {
            Assert.Equal(0L, await env.AsApp(c => Scalar(c, $"SELECT count(*) FROM {t}"))); // права є, політик немає: RLS приховує все
            Assert.Equal(0L, await env.AsWorker(c => Scalar(c, $"SELECT count(*) FROM {t}")));
        }
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => env.AsAppDo(c => Exec(c, "INSERT INTO identities (email, password_hash, state) VALUES ('hack@x.test', 'h', 'active')")))).SqlState); // WITH CHECK (RLS)
        await env.ApplyGrantsAsync();
        await AssertPrivilegeMatrix(env);
    }

    private static async Task AssertPrivilegeMatrix(Env env)
    {
        var sc = env.Super;
        foreach (var role in new[] { "beautycrm_app", "beautycrm_worker", env.PublicLikeRole })
        {
            foreach (var t in GlobalTables)
                Assert.False(await Bool(sc, $"SELECT has_table_privilege('{role}', 'public.{t}', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')"), $"{role} has privileges on {t}");
            Assert.False(await Bool(sc, $"SELECT has_schema_privilege('{role}', 'public', 'CREATE')"), $"{role} has CREATE on schema public");
        }
        Assert.False(await Bool(sc, "SELECT has_schema_privilege('public', 'public', 'CREATE')"));
        var roleKeys = new Dictionary<string, string> { ["app"] = "beautycrm_app", ["worker"] = "beautycrm_worker", ["owner"] = "beautycrm_owner", ["none"] = env.PublicLikeRole };
        foreach (var (name, _, allowed) in Matrix)
        {
            var oid = $"(SELECT oid FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = '{name}')";
            Assert.False(await Bool(sc, $"SELECT has_function_privilege('public', {oid}, 'EXECUTE')"), $"PUBLIC can execute {name}");
            foreach (var (key, role) in roleKeys)
                Assert.Equal(allowed.Contains(key), await Bool(sc, $"SELECT has_function_privilege('{role}', {oid}, 'EXECUTE')"));
        }
        foreach (var role in roleKeys.Values)
            Assert.False(await Bool(sc, $"SELECT has_function_privilege('{role}', 'public.users_identity_email_guard()'::regprocedure, 'EXECUTE')"));
    }

    // ================================================================================================================
    // 2. синтетичні дублікати: allowlist, відкликання сесій, ідемпотентність, Down
    // ================================================================================================================
    [SkippableFact]
    public async Task duplicates_require_explicit_allowlist_and_merge_revokes_sessions()
    {
        await using var env = await Env.CreateAsync(identityRoles: true);
        var sc = env.Super;
        var plain = env.NewContext();
        var migrator = env.Migrator(plain);
        await migrator.MigrateAsync(Previous);

        // ---- дані до міграції ----
        var (t1, t2, t3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var (t, n) in new[] { (t1, "a"), (t2, "b"), (t3, "c") })
            await Exec(sc, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T{n}', 'dup-{n}-{t:N}')");
        async Task<Guid> Old(Guid tenant, string email, string role, string? hash, string? lastLogin, bool active = true, string state = "active", string updated = "2026-01-01")
        {
            var id = Guid.NewGuid();
            await Exec(sc, $@"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role, state, is_active, last_login_at, updated_at)
                VALUES ('{id}', '{tenant}', '{email}', 'N', {(hash is null ? "NULL" : $"'{hash}'")}, '{role}', '{state}', {active.ToString().ToLowerInvariant()}, {(lastLogin is null ? "NULL" : $"'{lastLogin}'")}, '{updated}')");
            return id;
        }
        var single = await Old(t1, "single@x.test", "admin", "h-single", "2026-03-01");
        var pendingOwner = await Old(t1, "owner@x.test", "owner", null, null, state: "invited");
        // G1: три членства (регістр пошти різний), переможець - T2 (найсвіжіший вхід серед активних з паролем); T3 вимкнене, хоч і свіжіше
        var g1a = await Old(t1, "dup@x.test", "admin", "h-old", "2026-01-01");
        var g1b = await Old(t2, "Dup@X.TEST", "specialist", "h-new", "2026-06-01");
        var g1c = await Old(t3, " dup@x.test ", "admin", "h-disabled", "2026-11-01", active: false);
        // G2: без входів - вирішує updated_at
        var g2a = await Old(t1, "g2@x.test", "admin", "h-g2-old", null, updated: "2026-02-01");
        var g2b = await Old(t2, "g2@x.test", "admin", "h-g2-new", null, updated: "2026-05-01");
        var members = new[] { single, pendingOwner, g1a, g1b, g1c, g2a, g2b };
        foreach (var u in members)
        {
            var (tenant, _) = await Row(sc, $"SELECT tenant_id, 1 FROM users WHERE id = '{u}'");
            await Exec(sc, $"INSERT INTO refresh_tokens (tenant_id, user_id, token_hash, expires_at) VALUES ('{tenant}', '{u}', '{Guid.NewGuid():N}', now() + interval '14 days')");
        }
        var before = await Rows(sc, "SELECT id, password_hash FROM users ORDER BY id");
        var key1 = await Text(sc, "SELECT left(md5('dup@x.test'), 8)");
        var key2 = await Text(sc, "SELECT left(md5('g2@x.test'), 8)");

        BeautyDbContext Ctx(string? allowlist, string name = "beautycrm.identity_merge_allowlist") =>
            env.NewContext(allowlist is null ? null : $"-c {name}={allowlist}");
        async Task<string> Fails(string? allowlist, string name = "beautycrm.identity_merge_allowlist")
        {
            await using var c = Ctx(allowlist, name);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => env.Migrator(c).MigrateAsync(Target));
            // міграція відкотилась повністю
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.identities')"));
            Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'identity_id'"));
            Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.users') AND relforcerowsecurity"));
            Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id = '{Target}'"));
            var text = Flatten(ex);
            Assert.DoesNotContain("@x.test", text); // у повідомленнях лише email_key, без пошт
            return text;
        }

        // 1) без параметра дублікати = падіння, повідомлення з email_key обох груп
        var m1 = await Fails(null);
        Assert.Contains("not in beautycrm.identity_merge_allowlist", m1);
        Assert.Contains(key1, m1); Assert.Contains(key2, m1);
        // 2) неповний allowlist
        var m2 = await Fails(key1);
        Assert.Contains("not in beautycrm.identity_merge_allowlist", m2);
        Assert.Contains(key2, m2); Assert.DoesNotContain(key1, m2);
        // 3) ключ без групи
        var m3 = await Fails($"{key1},{key2},deadbeef");
        Assert.Contains("matching no duplicate group", m3); Assert.Contains("deadbeef", m3);
        // 4) невалідний формат
        Assert.Contains("8-hex", await Fails("not-a-key"));
        Assert.Contains("8-hex", await Fails($"{key1},{key2},ABCDEF1")); // 7 hex
        // 5) два параметри з різним значенням
        await using (var both = env.NewContext($"-c beautycrm.identity_merge_allowlist={key1},{key2} -c beautycrm.identity_merge_allow={key1}"))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => env.Migrator(both).MigrateAsync(Target));
            Assert.Contains("both set and differ", Flatten(ex));
        }
        // 6) B1: invited-власник у групі завжди відмова, навіть з allowlist
        var g3a = await Old(t1, "g3@x.test", "admin", "h-g3", "2026-02-02");
        var g3b = await Old(t2, "g3@x.test", "owner", null, null, state: "invited");
        var key3 = await Text(sc, "SELECT left(md5('g3@x.test'), 8)");
        var b1 = await Fails($"{key1},{key2},{key3}");
        Assert.Contains("invited owner", b1); Assert.Contains(key3, b1);
        await Exec(sc, $"DELETE FROM refresh_tokens WHERE user_id IN ('{g3a}', '{g3b}'); DELETE FROM users WHERE id IN ('{g3a}', '{g3b}')");
        // 7) B2: у групі немає активного членства з паролем
        var g4a = await Old(t1, "g4@x.test", "admin", "h-4a", null, active: false);
        var g4b = await Old(t2, "g4@x.test", "admin", "h-4b", null, active: false);
        var key4 = await Text(sc, "SELECT left(md5('g4@x.test'), 8)");
        var b2 = await Fails($"{key1},{key2},{key4}");
        Assert.Contains("no active membership with a password", b2); Assert.Contains(key4, b2);
        await Exec(sc, $"DELETE FROM users WHERE id IN ('{g4a}', '{g4b}')");
        // 8) дублікат усередині одного tenant (case-варіанти) злити не можна
        var s1 = await Old(t3, "Same@x.test", "admin", "h-s1", null);
        var s2 = await Old(t3, "same@x.test", "admin", "h-s2", null);
        var keyS = await Text(sc, "SELECT left(md5('same@x.test'), 8)");
        var b3 = await Fails($"{key1},{key2},{keyS}");
        Assert.Contains("Same-tenant duplicate", b3); Assert.Contains(keyS, b3);
        await Exec(sc, $"DELETE FROM users WHERE id IN ('{s1}', '{s2}')");
        // дані повернулись у вихідний стан (нічого не змінено невдалими спробами)
        Assert.Equal(before.Count, (await Rows(sc, "SELECT id, password_hash FROM users ORDER BY id")).Count);

        // ---- позитив: allowlist обох груп ----
        await using (var ok = Ctx($"{key1},{key2}"))
            await env.Migrator(ok).MigrateAsync(Target);
        await AssertMergedState(sc, key1, single, pendingOwner, g1a, g1b, g1c, g2a, g2b, before);

        // ---- ідемпотентність: повторний запуск backfill нічого не змінює (нових членств без ідентичності немає) ----
        var identitiesBefore = await Scalar(sc, "SELECT count(*) FROM identities");
        var revokedBefore = await Scalar(sc, "SELECT count(*) FROM refresh_tokens WHERE revoked_at IS NOT NULL");
        await ExecAsOwner(env.OwnerCs, auth_global_identities.BackfillSql);
        Assert.Equal(identitiesBefore, await Scalar(sc, "SELECT count(*) FROM identities"));
        Assert.Equal(revokedBefore, await Scalar(sc, "SELECT count(*) FROM refresh_tokens WHERE revoked_at IS NOT NULL"));
        foreach (var t in new[] { "users", "refresh_tokens" })
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{t}') AND relrowsecurity AND relforcerowsecurity"));

        // ---- Down відмовляє, коли dual-write порушено (активне членство без users.password_hash) ----
        await Exec(sc, "ALTER TABLE users DROP CONSTRAINT ck_users_password_or_invited");
        await Exec(sc, $"UPDATE users SET password_hash = NULL WHERE id = '{single}'");
        var down = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
        Assert.Contains("Down refused", Flatten(down));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.identities')")); // відкотилось
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.users') AND relforcerowsecurity"));
        await Exec(sc, $"UPDATE users SET password_hash = 'h-single' WHERE id = '{single}'");
        await Exec(sc, "ALTER TABLE users ADD CONSTRAINT ck_users_password_or_invited CHECK (password_hash IS NOT NULL OR state = 'invited')");

        // ---- Down без перешкод: чисто відкочує, дані користувачів цілі, ролі лишаються ----
        await Exec(sc, "INSERT INTO platform_audit_log (action, actor_type) VALUES ('identity.created', 'identity')"); // журнал append-only: CHECK тоді лишається розширеним
        await migrator.MigrateAsync(Previous);
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid IN (to_regclass('public.identities'), to_regclass('public.auth_login_tickets'), to_regclass('public.auth_password_resets'), to_regclass('public.auth_login_attempts'))"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name IN ('identity_id', 'sessions_valid_after')"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND (proname LIKE 'auth\\_%' OR proname = 'users_identity_email_guard')"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_policies WHERE policyname LIKE 'identity%'"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_trigger WHERE tgname = 'users_identity_email_consistency'"));
        Assert.Equal(2L, await Scalar(sc, "SELECT count(*) FROM pg_roles WHERE rolname IN ('beautycrm_identity', 'beautycrm_identity_release')")); // кластерні ролі не видаляємо
        Assert.False(await Bool(sc, "SELECT has_table_privilege('beautycrm_identity', 'public.users', 'SELECT') OR has_column_privilege('beautycrm_identity', 'public.users', 'password_hash', 'UPDATE')"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_platform_audit_log_actor_type' AND pg_get_constraintdef(oid) LIKE '%''identity''%'")); // рядки identity.* є
        Assert.Equal((long)before.Count, await Scalar(sc, "SELECT count(*) FROM users"));
        // users.password_hash міграція не чіпала: після Down значення ті самі, що були до неї
        Assert.Equal(before.Select(r => (r[0], r[1])), (await Rows(sc, "SELECT id, password_hash FROM users ORDER BY id")).Select(r => (r[0], r[1])));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.users') AND relforcerowsecurity"));
    }

    [SkippableFact]
    public async Task alias_parameter_name_is_accepted_and_up_down_up_is_repeatable()
    {
        await using var env = await Env.CreateAsync(identityRoles: true);
        var sc = env.Super;
        var migrator = env.Migrator(env.NewContext());
        await migrator.MigrateAsync(Previous);
        var (t1, t2) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var t in new[] { t1, t2 })
            await Exec(sc, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'al-{t:N}')");
        await Exec(sc, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role, last_login_at) VALUES ('{t1}', 'alias@x.test', 'A', 'h1', 'admin', '2026-01-01'), ('{t2}', 'alias@x.test', 'A', 'h2', 'admin', '2026-02-01')");
        var key = await Text(sc, "SELECT left(md5('alias@x.test'), 8)");

        await using (var c = env.NewContext($"-c beautycrm.identity_merge_allow={key}"))
            await env.Migrator(c).MigrateAsync(Target);
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM identities WHERE email = 'alias@x.test' AND password_hash = 'h2' AND state = 'active'"));
        Assert.Equal(2L, await Scalar(sc, "SELECT count(*) FROM users WHERE sessions_valid_after IS NOT NULL"));

        await migrator.MigrateAsync(Previous);
        // без дублікатів параметр не потрібен: Up на чистих даних; а непотрібний allowlist (ключ без групи) = падіння
        await Exec(sc, $"DELETE FROM users WHERE tenant_id = '{t2}'");
        await using (var c = env.NewContext($"-c beautycrm.identity_merge_allowlist={key}"))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => env.Migrator(c).MigrateAsync(Target));
            Assert.Contains("matching no duplicate group", Flatten(ex));
        }
        await migrator.MigrateAsync(Target);
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM identities"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM users WHERE sessions_valid_after IS NOT NULL")); // не злито: сесії не чіпаємо
    }

    /// <summary>R-3: міграція users_identity_id_immutable окремо від auth_global_identities; Down прибирає лише свій тригер, Up повторюваний.</summary>
    [SkippableFact]
    public async Task identity_id_immutable_migration_applies_on_top_of_target_and_down_removes_only_its_trigger()
    {
        await using var env = await Env.CreateAsync(identityRoles: true);
        var sc = env.Super;
        var migrator = env.Migrator(env.NewContext());
        await migrator.MigrateAsync(Target);
        const string trg = "SELECT count(*) FROM pg_trigger WHERE tgname = 'users_identity_id_immutable' AND NOT tgisinternal";
        const string fn = "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'users_identity_id_immutable_guard'";
        Assert.Equal(0L, await Scalar(sc, trg));
        Assert.Equal(0L, await Scalar(sc, fn));

        // дані на момент Target: без тригера identity_id ще змінюється (саме цю діру закриває міграція)
        var (t, id1, id2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Exec(sc, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'imm-{t:N}')");
        await Exec(sc, $"INSERT INTO identities (id, email, password_hash, state) VALUES ('{id1}', 'imm1@x.test', 'h', 'active'), ('{id2}', 'imm2@x.test', 'h', 'active')");
        await SeedUser(sc, t, id1, "imm1@x.test", "admin");
        await Exec(sc, $"UPDATE users SET identity_id = '{id2}', email = 'imm2@x.test' WHERE tenant_id = '{t}'"); // дозволено до міграції
        await Exec(sc, $"UPDATE users SET identity_id = '{id1}', email = 'imm1@x.test' WHERE tenant_id = '{t}'");

        await migrator.MigrateAsync(Latest);
        Assert.Equal(1L, await Scalar(sc, trg));
        Assert.Equal(1L, await Scalar(sc, fn));
        Assert.False(await Bool(sc, "SELECT has_function_privilege('public', 'public.users_identity_id_immutable_guard()'::regprocedure, 'EXECUTE')"));
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc,
            $"UPDATE users SET identity_id = '{id2}', email = 'imm2@x.test' WHERE tenant_id = '{t}'"))).SqlState);

        await migrator.MigrateAsync(Target);
        Assert.Equal(0L, await Scalar(sc, trg));
        Assert.Equal(0L, await Scalar(sc, fn));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_trigger WHERE tgname = 'users_identity_email_consistency' AND NOT tgisinternal")); // тригер пошти лишається
        Assert.Equal(id1, await GuidOf(sc, $"SELECT identity_id FROM users WHERE tenant_id = '{t}'")); // дані цілі

        await migrator.MigrateAsync(Latest); // Up після Down
        Assert.Equal(1L, await Scalar(sc, trg));
        await migrator.MigrateAsync(Previous); // повний відкат (Down обох міграцій)
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_trigger WHERE tgname IN ('users_identity_id_immutable', 'users_identity_email_consistency')"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname LIKE 'users\\_identity\\_%'"));
    }

    private static async Task AssertMergedState(NpgsqlConnection sc, string key1, Guid single, Guid pendingOwner, Guid g1a, Guid g1b, Guid g1c, Guid g2a, Guid g2b, List<object?[]> before)
    {
        _ = key1;
        Assert.Equal(4L, await Scalar(sc, "SELECT count(*) FROM identities"));
        Assert.Equal(0L, await Scalar(sc, "SELECT count(*) FROM users WHERE identity_id IS NULL"));
        // G1: пароль - членство з найсвіжішим входом серед активних з паролем (не вимкнене T3)
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM identities WHERE email = 'dup@x.test' AND password_hash = 'h-new' AND state = 'active'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM identities WHERE email = 'g2@x.test' AND password_hash = 'h-g2-new' AND state = 'active'")); // за updated_at
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM identities WHERE email = 'owner@x.test' AND password_hash IS NULL AND state = 'pending'")); // запрошений власник
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM identities WHERE email = 'single@x.test' AND password_hash = 'h-single' AND state = 'active'"));
        Assert.Equal(3L, await Scalar(sc, $"SELECT count(*) FROM users WHERE id IN ('{g1a}', '{g1b}', '{g1c}') AND identity_id = (SELECT id FROM identities WHERE email = 'dup@x.test')"));
        Assert.Equal(2L, await Scalar(sc, $"SELECT count(*) FROM users WHERE id IN ('{g2a}', '{g2b}') AND identity_id = (SELECT id FROM identities WHERE email = 'g2@x.test')"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(DISTINCT identity_id) FROM users WHERE email ILIKE '%dup@x.test%'"));
        // users.password_hash НЕ змінено (фаза 3)
        var after = await Rows(sc, "SELECT id, password_hash FROM users ORDER BY id");
        Assert.Equal(before.Select(r => (r[0], r[1])), after.Select(r => (r[0], r[1])));
        // злиті членства: сесії недійсні (refresh відкликано всім, sessions_valid_after); незлиті не чіпаємо
        var merged = new[] { g1a, g1b, g1c, g2a, g2b };
        foreach (var u in merged)
        {
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM users WHERE id = '{u}' AND sessions_valid_after IS NOT NULL"));
            Assert.Equal(0L, await Scalar(sc, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{u}' AND revoked_at IS NULL"));
        }
        foreach (var u in new[] { single, pendingOwner })
        {
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM users WHERE id = '{u}' AND sessions_valid_after IS NULL"));
            Assert.Equal(1L, await Scalar(sc, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{u}' AND revoked_at IS NULL"));
        }
        // обмеження після backfill
        Assert.Equal("NO", await Text(sc, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'identity_id'"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.users') AND relrowsecurity AND relforcerowsecurity"));
        Assert.Equal(1L, await Scalar(sc, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.refresh_tokens') AND relrowsecurity AND relforcerowsecurity"));
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => Exec(sc,
            $"INSERT INTO users (tenant_id, email, full_name, password_hash, role, identity_id) SELECT tenant_id, email, 'X', 'x', 'admin', identity_id FROM users WHERE id = '{single}'"))).SqlState);
    }

    // ================================================================================================================
    // 3. ролі відсутні й мігратор без CREATEROLE: зрозуміла інструкція для ops
    // ================================================================================================================
    [SkippableFact]
    public async Task missing_roles_fail_with_ops_instruction()
    {
        await using var env = await Env.CreateAsync(identityRoles: false);
        var migrator = env.Migrator(env.NewContext());
        await migrator.MigrateAsync(Previous);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Target));
        var text = Flatten(ex);
        Assert.Contains("beautycrm_identity", text);
        Assert.Contains("cannot create it", text);
        Assert.Contains("GRANT beautycrm_identity TO", text);
        Assert.Equal(0L, await Scalar(env.Super, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.identities')"));
    }

    // ================================================================================================================
    // helpers
    // ================================================================================================================
    private static async Task<Guid> SeedUser(NpgsqlConnection sc, Guid tenant, Guid identity, string email, string role, bool active = true, string state = "active", string? hash = "x")
    {
        var id = Guid.NewGuid();
        await Exec(sc, $@"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role, state, is_active, identity_id)
            VALUES ('{id}', '{tenant}', '{email}', 'N', {(hash is null ? "NULL" : $"'{hash}'")}, '{role}', '{state}', {active.ToString().ToLowerInvariant()}, '{identity}')");
        return id;
    }

    private static Task SeedRefresh(NpgsqlConnection sc, Guid tenant, Guid user) =>
        Exec(sc, $"INSERT INTO refresh_tokens (tenant_id, user_id, token_hash, expires_at) VALUES ('{tenant}', '{user}', '{Guid.NewGuid():N}', now() + interval '14 days')");

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    private static async Task ExecAsOwner(string ownerCs, string sql)
    {
        await using var conn = await Open(ownerCs);
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    private static async Task<NpgsqlConnection> Open(string cs)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        return conn;
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

    private static Task<long> Long(NpgsqlConnection conn, string sql) => Scalar(conn, sql);

    private static async Task<string> Text(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<bool> Bool(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> GuidOf(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Guid?> NullableGuid(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : (Guid)v;
    }

    private static async Task<List<string>> Column(NpgsqlConnection conn, string sql)
    {
        var res = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) res.Add(Convert.ToString(r.GetValue(0))!);
        return res;
    }

    private static async Task<List<object?[]>> Rows(NpgsqlConnection conn, string sql)
    {
        var res = new List<object?[]>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            res.Add(row);
        }
        return res;
    }

    private static async Task<(object? A, object? B)> Row(NpgsqlConnection conn, string sql)
    {
        var rows = await Rows(conn, sql);
        return (rows[0][0], rows[0][1]);
    }

    private static async Task<string[]> ColumnNames(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        return Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
    }

    /// <summary>Контейнер/сервер, БД, роль-власник (як на staging: без CREATEROLE), ролі середовища для перевірки grants.sql.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private PostgreSqlContainer? _container;
        private string _adminCs = null!;
        private string _db = null!, _owner = null!;
        private readonly List<string> _createdRoles = [];

        public NpgsqlConnection Super { get; private set; } = null!;
        public string OwnerCs { get; private set; } = null!;
        public string SuperCs { get; private set; } = null!;
        /// <summary>Роль без жодних прав: заміна PUBLIC для перевірок EXECUTE/CREATE.</summary>
        public string PublicLikeRole { get; private set; } = null!;

        public static async Task<Env> CreateAsync(bool identityRoles)
        {
            var requireDb = Environment.GetEnvironmentVariable("BEAUTY_TEST_REQUIRE_DB") == "1";
            var env = new Env();
            try
            {
                var external = Environment.GetEnvironmentVariable("BEAUTY_TEST_PG_ADMIN");
                if (string.IsNullOrWhiteSpace(external))
                {
                    env._container = new PostgreSqlBuilder("postgres:16-alpine").Build();
                    await env._container.StartAsync();
                    env._adminCs = env._container.GetConnectionString();
                }
                else env._adminCs = external;
            }
            catch (Exception ex) when (!requireDb)
            {
                Skip.If(true, $"No PostgreSQL: {ex.GetType().Name}");
                return null!;
            }

            var suffix = System.Guid.NewGuid().ToString("N")[..10];
            env._db = $"beauty_idn_{suffix}";
            env._owner = $"beauty_idn_owner_{suffix}";
            env.PublicLikeRole = $"beauty_idn_nobody_{suffix}";
            var password = System.Guid.NewGuid().ToString("N");
            await using (var admin = await Open(env._adminCs))
            {
                await Exec(admin, $"CREATE ROLE {env._owner} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
                async Task EnsureRole(string role, string attrs, bool member)
                {
                    if (await Scalar(admin, $"SELECT count(*) FROM pg_roles WHERE rolname = '{role}'") == 0)
                    {
                        await Exec(admin, $"CREATE ROLE {role} {attrs}");
                        env._createdRoles.Add(role);
                    }
                    if (member) await Exec(admin, $"GRANT {role} TO {env._owner} WITH INHERIT FALSE, SET TRUE");
                }
                await EnsureRole("beautycrm_catalog", "NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT", true);
                await EnsureRole("beautycrm_audit_purge", "NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT", true);
                if (identityRoles)
                {
                    await EnsureRole("beautycrm_identity", "NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT", true);
                    await EnsureRole("beautycrm_identity_release", "NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT", true);
                }
                // ролі середовища (реальні імена з grants.sql); у контейнері створюються без права входу, SET ROLE їм достатньо
                await EnsureRole("beautycrm_app", "NOLOGIN NOSUPERUSER NOBYPASSRLS", false);
                await EnsureRole("beautycrm_worker", "NOLOGIN NOSUPERUSER NOBYPASSRLS", false);
                await EnsureRole("beautycrm_owner", "NOLOGIN NOSUPERUSER NOBYPASSRLS", false);
                await Exec(admin, $"CREATE ROLE {env.PublicLikeRole} NOLOGIN NOSUPERUSER NOBYPASSRLS");
                await Exec(admin, $"CREATE DATABASE {env._db} OWNER {env._owner}");
            }
            env.OwnerCs = new NpgsqlConnectionStringBuilder(env._adminCs) { Database = env._db, Username = env._owner, Password = password }.ConnectionString;
            env.SuperCs = new NpgsqlConnectionStringBuilder(env._adminCs) { Database = env._db }.ConnectionString;
            env.Super = await Open(env.SuperCs);
            return env;
        }

        public BeautyDbContext NewContext(string? pgOptions = null)
        {
            var cs = new NpgsqlConnectionStringBuilder(OwnerCs) { Options = pgOptions }.ConnectionString;
            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(cs);
            return new BeautyDbContext(options.Options, new TenantContext());
        }

        public IMigrator Migrator(BeautyDbContext ctx) =>
            ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
            ?? throw new InvalidOperationException("IMigrator not available");

        public async Task ApplyGrantsAsync()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "staging", "db", "grants.sql"))) dir = dir.Parent;
            Assert.NotNull(dir);
            await Exec(Super, await File.ReadAllTextAsync(Path.Combine(dir!.FullName, "deploy", "staging", "db", "grants.sql")));
        }

        private async Task<NpgsqlConnection> OpenAs(string role)
        {
            var conn = await Open(SuperCs);
            await Exec(conn, $"SET ROLE {role}");
            return conn;
        }

        public Task<NpgsqlConnection> OpenAsApp() => OpenAs("beautycrm_app");
        public Task<NpgsqlConnection> OpenAsWorker() => OpenAs("beautycrm_worker");

        private async Task<T> Run<T>(string role, Func<NpgsqlConnection, Task<T>> f)
        {
            await using var conn = await OpenAs(role);
            return await f(conn);
        }

        public Task AsAppDo(Func<NpgsqlConnection, Task> f) => Run("beautycrm_app", async c => { await f(c); return 0; });
        public Task AsWorkerDo(Func<NpgsqlConnection, Task> f) => Run("beautycrm_worker", async c => { await f(c); return 0; });
        public Task AsOpsDo(Func<NpgsqlConnection, Task> f) => Run("beautycrm_owner", async c => { await f(c); return 0; });
        public Task<T> AsApp<T>(Func<NpgsqlConnection, Task<T>> f) => Run("beautycrm_app", f);
        public Task<T> AsWorker<T>(Func<NpgsqlConnection, Task<T>> f) => Run("beautycrm_worker", f);
        public Task<T> AsOps<T>(Func<NpgsqlConnection, Task<T>> f) => Run("beautycrm_owner", f);

        public async ValueTask DisposeAsync()
        {
            try { if (Super is not null) await Super.DisposeAsync(); } catch { /* ignore */ }
            NpgsqlConnection.ClearAllPools();
            if (_container is not null) { await _container.DisposeAsync(); return; }
            if (_adminCs is null) return;
            await using var admin = await Open(_adminCs);
            await Exec(admin, $"DROP DATABASE IF EXISTS {_db} WITH (FORCE)");
            await Exec(admin, $"DROP ROLE IF EXISTS {_owner}");
            await Exec(admin, $"DROP ROLE IF EXISTS {PublicLikeRole}");
        }
    }
}
