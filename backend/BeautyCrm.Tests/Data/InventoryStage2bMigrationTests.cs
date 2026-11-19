using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-895 (ADR-025 §12, §32.24.2): міграція beauty_inventory_stage2b. Адитивність, FORCE RLS на 4 таблицях, CHECK форми станів прив'язки
/// (кожна недозволена комбінація), часткові UNIQUE (користувач, чат, токен), append-only журнал подій (тригер: UPDATE/DELETE/TRUNCATE
/// відхилено для власника й app-ролі), крос-tenant композитні FK, RESTRICT/CASCADE, GRANT-и app-ролі й воркера (виконується справжній
/// deploy/staging/db/grants.sql з підміною імен ролей), Down із відмовою за історії 2b і Up -> Down -> Up. Без реального годинника.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class InventoryStage2bMigrationTests
{
    private const string Previous = "20261119093230_beauty_inventory_stage2a";

    private static readonly string[] NewTables =
        ["beauty_staff_telegram_links", "beauty_staff_telegram_link_events", "beauty_staff_notifications", "beauty_staff_notification_alerts"];

    [SkippableFact]
    public async Task up_creates_four_tables_state_shape_unique_append_only_grants_hold_and_down_refuses_with_history()
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
        var db = $"beauty_inv2b_{suffix}";
        var owner = $"beauty_inv2b_owner_{suffix}";
        var app = $"beauty_inv2b_app_{suffix}";
        var worker = $"beauty_inv2b_wrk_{suffix}";
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

            // ---- дані "до міграції": два tenant, користувачі, канали, заклад, товар, алерт ----
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Ctx(conn, t);
                await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'i2b-{t:N}')");
            }
            async Task<Guid> User(Guid t, string tag, string role = "owner")
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{t}', '{tag}@{t:N}.test', 'U', 'x', '{role}') RETURNING id");
            }
            async Task<Guid> Channel(Guid t)
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO beauty_channels (tenant_id, type, name, is_active) VALUES ('{t}', 'telegram', 'bot', true) RETURNING id");
            }
            var u1 = await User(tenantA, "u1");
            var u2 = await User(tenantA, "u2");
            var u3 = await User(tenantA, "u3");
            var uB = await User(tenantB, "ub");
            var chA = await Channel(tenantA);
            var chA2 = await Channel(tenantA);
            var chB = await Channel(tenantB);
            await Ctx(conn, tenantA);
            var locA = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'Центр', 'Europe/Kyiv', true) RETURNING id");
            var product = await Id(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{tenantA}', 'P', 'ml', 'salon_use', '{u1}') RETURNING id");
            string AlertSql(string key) => $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, quantity, dedupe_key)
                VALUES ('{tenantA}', 'low_stock', '{locA}', '{product}', 3, '{key}') RETURNING id";
            var alert1 = await Id(conn, AlertSql("k1"));
            var alert2 = await Id(conn, AlertSql("k2"));
            var alert3 = await Id(conn, AlertSql("k3"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_staff_telegram_links')"));
            var alertColsBefore = await Scalar(conn, "SELECT count(*) FROM pg_attribute WHERE attrelid = 'beauty_staff_alerts'::regclass AND attnum > 0 AND NOT attisdropped");

            await migrator.MigrateAsync();

            // ---- адитивність: дані й колонки beauty_staff_alerts цілі; 4 таблиці під FORCE RLS з політикою (USING + WITH CHECK) ----
            Assert.Equal(alertColsBefore, await Scalar(conn, "SELECT count(*) FROM pg_attribute WHERE attrelid = 'beauty_staff_alerts'::regclass AND attnum > 0 AND NOT attisdropped"));
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_alerts"));
            foreach (var table in NewTables)
            {
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation' AND qual IS NOT NULL AND with_check IS NOT NULL"));
            }
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_staff_telegram_link_events_%' AND NOT tgisinternal"));
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal")); // тригери етапу 1 не зачеплені


            // ---- прив'язки: форма кожного стану ----
            string Hex(string hh) => $"decode(repeat('{hh}', 32), 'hex')";
            string Link(Guid user, string status, string cols = "", string vals = "", Guid? channel = null, Guid? tenant = null) =>
                $@"INSERT INTO beauty_staff_telegram_links (tenant_id, user_id, channel_id, status, consent_version, consent_at{cols})
                   VALUES ('{tenant ?? tenantA}', '{user}', '{channel ?? chA}', '{status}', 'inv-tg-v1', now(){vals})";
            string Issued(Guid user, string tok, Guid? channel = null, Guid? tenant = null) =>
                Link(user, "issued", ", token_hash, token_expires_at", $", {Hex(tok)}, now() + interval '15 minutes'", channel, tenant);
            string Claimed(Guid user, string tok, string chat, Guid? channel = null) =>
                Link(user, "claimed", ", token_hash, token_expires_at, chat_id_encrypted, chat_id_hash, claimed_at",
                    $", {Hex(tok)}, now() + interval '15 minutes', 'enc', {Hex(chat)}, now()", channel);
            string Active(Guid user, string chat, string extraCols = "", string extraVals = "") =>
                Link(user, "active", ", chat_id_encrypted, chat_id_hash, claimed_at, bot_confirmed_at, activated_at" + extraCols,
                    $", 'enc', {Hex(chat)}, now(), now(), now()" + extraVals);
            string Revoked(Guid user, string reason = "user_unlink", string extraCols = "", string extraVals = "") =>
                Link(user, "revoked", ", revoked_at, revoke_reason" + extraCols, $", now(), '{reason}'" + extraVals);
            string Revoke(Guid id, string reason) =>
                $"UPDATE beauty_staff_telegram_links SET status = 'revoked', token_hash = NULL, chat_id_encrypted = NULL, chat_id_hash = NULL, revoked_at = now(), revoke_reason = '{reason}' WHERE id = '{id}'";

            await Ctx(conn, tenantA);
            const string Shape = "ck_beauty_staff_telegram_links_shape";
            // issued
            await Rejects(conn, Link(u1, "issued"), Shape); // без токена
            await Rejects(conn, Link(u1, "issued", ", token_hash", $", {Hex("a1")}"), Shape); // без строку дії токена
            await Rejects(conn, Link(u1, "issued", ", token_expires_at", ", now()"), Shape); // без хешу токена
            await Rejects(conn, Link(u1, "issued", ", token_hash, token_expires_at, chat_id_encrypted, chat_id_hash", $", {Hex("a1")}, now(), 'enc', {Hex("c1")}"), Shape);
            foreach (var col in new[] { "claimed_at", "activated_at", "bot_confirmed_at", "revoked_at", "last_delivery_at" })
                await Rejects(conn, Link(u1, "issued", $", token_hash, token_expires_at, {col}", $", {Hex("a1")}, now(), now()"), Shape);
            // claimed
            await Rejects(conn, Link(u1, "claimed", ", token_hash, token_expires_at, claimed_at", $", {Hex("a1")}, now(), now()"), Shape); // без чату
            await Rejects(conn, Link(u1, "claimed", ", token_hash, token_expires_at, chat_id_encrypted, chat_id_hash", $", {Hex("a1")}, now(), 'enc', {Hex("c1")}"), Shape); // без claimed_at
            await Rejects(conn, Link(u1, "claimed", ", token_expires_at, chat_id_encrypted, chat_id_hash, claimed_at", $", now(), 'enc', {Hex("c1")}, now()"), Shape); // без токена
            await Rejects(conn, Link(u1, "claimed", ", token_hash, chat_id_encrypted, chat_id_hash, claimed_at", $", {Hex("a1")}, 'enc', {Hex("c1")}, now()"), Shape); // без строку дії
            foreach (var col in new[] { "activated_at", "bot_confirmed_at", "revoked_at", "last_delivery_at" })
                await Rejects(conn, Link(u1, "claimed", $", token_hash, token_expires_at, chat_id_encrypted, chat_id_hash, claimed_at, {col}",
                    $", {Hex("a1")}, now(), 'enc', {Hex("c1")}, now(), now()"), Shape);
            // active
            await Rejects(conn, Active(u1, "c1", ", token_hash", $", {Hex("a1")}"), Shape); // токен лишився
            await Rejects(conn, Link(u1, "active", ", chat_id_encrypted, chat_id_hash, activated_at, claimed_at", $", 'enc', {Hex("c1")}, now(), now()"), Shape); // без bot_confirmed_at
            await Rejects(conn, Link(u1, "active", ", chat_id_encrypted, chat_id_hash, bot_confirmed_at, claimed_at", $", 'enc', {Hex("c1")}, now(), now()"), Shape); // без activated_at
            await Rejects(conn, Link(u1, "active", ", bot_confirmed_at, activated_at", ", now(), now()"), Shape); // без чату
            await Rejects(conn, Active(u1, "c1", ", revoked_at", ", now()"), Shape);
            // revoked
            await Rejects(conn, Revoked(u1, extraCols: ", token_hash, token_expires_at", extraVals: $", {Hex("a1")}, now()"), Shape); // токен лишився
            await Rejects(conn, Revoked(u1, extraCols: ", chat_id_encrypted, chat_id_hash", extraVals: $", 'enc', {Hex("c1")}"), Shape); // дані чату лишилися
            await Rejects(conn, Link(u1, "revoked", ", revoke_reason", ", 'user_unlink'"), Shape); // без revoked_at
            // невідомий статус: форма не збігається з жодним станом (CHECK shape спрацьовує раніше за ck_..._status за алфавітом)
            await Rejects(conn, Link(u1, "pending"), Shape);
            // інші CHECK
            await Rejects(conn, Revoked(u1, "oops"), "ck_beauty_staff_telegram_links_revoke_reason");
            await Rejects(conn, Link(u1, "revoked", ", revoked_at", ", now()"), "ck_beauty_staff_telegram_links_revoke_reason"); // revoked без причини
            await Rejects(conn, Link(u1, "issued", ", token_hash, token_expires_at, revoke_reason", $", {Hex("a1")}, now(), 'replaced'"), "ck_beauty_staff_telegram_links_revoke_reason");
            await Rejects(conn, Link(u1, "claimed", ", token_hash, token_expires_at, chat_id_encrypted, claimed_at", $", {Hex("a1")}, now(), 'enc', now()"), "ck_beauty_staff_telegram_links_chat");
            await Rejects(conn, Link(u1, "issued", ", token_hash, token_expires_at", ", decode(repeat('aa', 31), 'hex'), now()"), "ck_beauty_staff_telegram_links_token_hash");
            await Rejects(conn, Link(u1, "claimed", ", token_hash, token_expires_at, chat_id_encrypted, chat_id_hash, claimed_at",
                $", {Hex("a1")}, now(), 'enc', decode(repeat('cc', 33), 'hex'), now()"), "ck_beauty_staff_telegram_links_chat_hash");
            foreach (var bad in new[] { "v1", "inv-tg-v", "inv-tg-vX", "INV-TG-V1", "inv-tg-v1 " })
                await Rejects(conn, Issued(u1, "a1").Replace("'inv-tg-v1'", $"'{bad}'"), "ck_beauty_staff_telegram_links_consent");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_telegram_links"));

            // ---- допустимі форми й життєвий цикл через UPDATE (як у §32.24.3) ----
            var link1 = await Id(conn, Issued(u1, "a1") + " RETURNING id");
            Assert.Equal(1, await ExecCount(conn, $@"UPDATE beauty_staff_telegram_links SET status = 'claimed', chat_id_encrypted = 'enc', chat_id_hash = {Hex("c1")}, claimed_at = now() WHERE id = '{link1}'"));
            await Rejects(conn, $"UPDATE beauty_staff_telegram_links SET status = 'active' WHERE id = '{link1}'", Shape); // активація без очищення токена й підтвердження
            Assert.Equal(1, await ExecCount(conn, $"UPDATE beauty_staff_telegram_links SET status = 'active', token_hash = NULL, bot_confirmed_at = now(), activated_at = now() WHERE id = '{link1}'"));
            await Rejects(conn, $"UPDATE beauty_staff_telegram_links SET status = 'revoked', revoked_at = now(), revoke_reason = 'bot_stop' WHERE id = '{link1}'", Shape); // дані чату не стерто
            Assert.Equal(1, await ExecCount(conn, Revoke(link1, "bot_stop")));
            foreach (var reason in new[] { "user_unlink", "bot_stop", "declined", "replaced", "expired", "bot_blocked", "chat_not_found", "user_deactivated", "role_changed", "tenant_inactive" })
                await Exec(conn, Revoked(u2, reason)); // усі 10 причин допустимі; revoked-рядки не обмежені UNIQUE
            Assert.Equal(10L, await Scalar(conn, $"SELECT count(*) FROM beauty_staff_telegram_links WHERE user_id = '{u2}' AND status = 'revoked'"));

            // ---- часткові UNIQUE ----
            var link2 = await Id(conn, Issued(u1, "a2") + " RETURNING id"); // після revoked користувач може мати новий рядок
            await Rejects(conn, Issued(u1, "a3"), "ux_beauty_staff_telegram_links_user_open"); // другий незакритий рядок користувача
            await Rejects(conn, Claimed(u1, "a3", "c9"), "ux_beauty_staff_telegram_links_user_open");
            await Rejects(conn, Active(u1, "c9"), "ux_beauty_staff_telegram_links_user_open");
            await Rejects(conn, Issued(u3, "a2"), "ux_beauty_staff_telegram_links_token"); // той самий токен
            var link3 = await Id(conn, Claimed(u2, "a4", "c2") + " RETURNING id");
            await Rejects(conn, Claimed(u3, "a5", "c2"), "ux_beauty_staff_telegram_links_chat_open"); // чат уже в u2
            await Rejects(conn, Active(u3, "c2"), "ux_beauty_staff_telegram_links_chat_open");
            var link4 = await Id(conn, Claimed(u3, "a5", "c2", chA2) + " RETURNING id"); // інший бот - той самий хеш допустимий
            Assert.Equal(1, await ExecCount(conn, Revoke(link4, "declined")));
            await Rejects(conn, Active(u3, "c2"), "ux_beauty_staff_telegram_links_chat_open"); // чат на chA усе ще в u2
            Assert.Equal(1, await ExecCount(conn, Revoke(link3, "declined")));
            var link5 = await Id(conn, Active(u3, "c2") + " RETURNING id"); // чат вільний після відв'язки
            await Ctx(conn, tenantB);
            var linkB = await Id(conn, Issued(uB, "a2", chB, tenantB) + " RETURNING id"); // той самий токен в іншому tenant допустимий
            await Ctx(conn, tenantA);

            // ---- журнал подій: CHECK-и, FK, append-only ----
            string Ev(Guid link, Guid user, string ev, string actor, string? reason = null, Guid? actorUser = null, Guid? tenant = null) =>
                $@"INSERT INTO beauty_staff_telegram_link_events (tenant_id, link_id, user_id, event, reason, actor, actor_user_id)
                   VALUES ('{tenant ?? tenantA}', '{link}', '{user}', '{ev}', {(reason is null ? "NULL" : $"'{reason}'")}, '{actor}', {(actorUser is null ? "NULL" : $"'{actorUser}'")})";
            await Rejects(conn, Ev(link5, u3, "sent", "bot"), "ck_beauty_staff_telegram_link_events_event");
            await Rejects(conn, Ev(link5, u3, "issued", "robot"), "ck_beauty_staff_telegram_link_events_actor");
            await Rejects(conn, Ev(link5, u3, "revoked", "bot", "oops"), "ck_beauty_staff_telegram_link_events_reason");
            await Rejects(conn, Ev(link5, u3, "revoked", "bot"), "ck_beauty_staff_telegram_link_events_reason"); // revoked без причини
            await Rejects(conn, Ev(link5, u3, "rejected", "bot"), "ck_beauty_staff_telegram_link_events_reason"); // rejected без причини
            await Exec(conn, Ev(link5, u3, "issued", "user", actorUser: u3));
            await Exec(conn, Ev(link5, u3, "claimed", "bot"));
            await Exec(conn, Ev(link5, u3, "activated", "bot"));
            await Exec(conn, Ev(link5, u3, "declined", "bot"));
            foreach (var reason in new[] { "expired", "used", "channel_mismatch", "chat_conflict", "user_inactive", "disabled" })
                await Exec(conn, Ev(link5, u3, "rejected", "bot", reason));
            await Exec(conn, Ev(link5, u3, "revoked", "system", "bot_blocked"));
            Assert.Equal(11L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_telegram_link_events"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_staff_telegram_link_events' AND column_name ~ 'chat|hash|token|username|phone'")); // журнал без даних чату
            foreach (var sql in new[]
                     {
                         "UPDATE beauty_staff_telegram_link_events SET reason = NULL", "DELETE FROM beauty_staff_telegram_link_events",
                         "TRUNCATE beauty_staff_telegram_link_events",
                     })
            {
                var blocked = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, sql)); // тригер діє й на власника
                Assert.Equal("42501", blocked.SqlState);
                Assert.Contains("append-only", blocked.MessageText);
            }
            Assert.Equal(11L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_telegram_link_events"));
            var restrictLink = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_staff_telegram_links WHERE id = '{link5}'"));
            Assert.Equal("23503", restrictLink.SqlState); // RESTRICT: прив'язка з подіями не видаляється
            await Ctx(conn, tenantB);
            var crossLinkEv = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Ev(link5, uB, "issued", "user", tenant: tenantB))); // прив'язка іншого tenant
            Assert.Equal("23503", crossLinkEv.SqlState);
            var crossUserEv = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Ev(linkB, u1, "issued", "user", tenant: tenantB)));
            Assert.Equal("23503", crossUserEv.SqlState);
            var crossActorEv = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Ev(linkB, uB, "issued", "user", actorUser: u1, tenant: tenantB)));
            Assert.Equal("23503", crossActorEv.SqlState);
            var crossLink = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Link(u1, "issued", ", token_hash, token_expires_at", $", {Hex("e1")}, now()", chB, tenantB)));
            Assert.Equal("23503", crossLink.SqlState); // користувач tenant A у прив'язці tenant B
            var crossChannel = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Link(uB, "revoked", ", revoked_at, revoke_reason", ", now(), 'expired'", chA, tenantB)));
            Assert.Equal("23503", crossChannel.SqlState); // канал tenant A у прив'язці tenant B (revoked-рядок: unique user_open спрацював би раніше за FK)
            await Ctx(conn, tenantA);

            // ---- черга відправки ----
            string Nf(Guid link, string key, string kind = "alert", string body = "'Склад · Центр'", string extraCols = "", string extraVals = "", Guid? tenant = null) =>
                $@"INSERT INTO beauty_staff_notifications (tenant_id, link_id, kind, body, dedupe_key{extraCols})
                   VALUES ('{tenant ?? tenantA}', '{link}', '{kind}', {body}, '{key}'{extraVals})";
            await Rejects(conn, Nf(link5, "n-x", kind: "sms"), "ck_beauty_staff_notifications_kind");
            await Rejects(conn, Nf(link5, "n-x", body: "''"), "ck_beauty_staff_notifications_body");
            await Rejects(conn, Nf(link5, "n-x", body: "repeat('x', 4097)"), "ck_beauty_staff_notifications_body");
            await Rejects(conn, Nf(link5, "n-x", extraCols: ", status", extraVals: ", 'queued'"), "ck_beauty_staff_notifications_status");
            await Rejects(conn, Nf(link5, "n-x", extraCols: ", attempts", extraVals: ", 11"), "ck_beauty_staff_notifications_attempts");
            await Rejects(conn, Nf(link5, "n-x", extraCols: ", attempts", extraVals: ", -1"), "ck_beauty_staff_notifications_attempts");
            await Rejects(conn, Nf(link5, "n-x", extraCols: ", status", extraVals: ", 'sent'"), "ck_beauty_staff_notifications_sent"); // sent без sent_at
            await Rejects(conn, Nf(link5, "n-x", extraCols: ", sent_at", extraVals: ", now()"), "ck_beauty_staff_notifications_sent"); // sent_at без sent
            var n1 = await Id(conn, Nf(link5, "alert:l5:a1") + " RETURNING id");
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_staff_notifications WHERE id = '{n1}' AND status = 'pending' AND attempts = 0 AND next_attempt_at IS NOT NULL AND sent_at IS NULL"));
            await Rejects(conn, Nf(link5, "alert:l5:a1"), "ux_beauty_staff_notifications_dedupe");
            var n2 = await Id(conn, Nf(link5, "digest:l5:1", kind: "digest", body: "repeat('x', 4096)", extraCols: ", attempts", extraVals: ", 10") + " RETURNING id");
            await Exec(conn, Nf(link5, "alert:l5:a3", extraCols: ", status, sent_at, external_message_id, last_error", extraVals: ", 'sent', now(), '12345', NULL"));
            await Exec(conn, Nf(link5, "alert:l5:a4", extraCols: ", status, last_error", extraVals: ", 'failed', 'bot_blocked'"));

            // ---- зв'язок алертів ----
            string Na(Guid notification, Guid alert, Guid link, Guid? tenant = null) =>
                $"INSERT INTO beauty_staff_notification_alerts (tenant_id, notification_id, alert_id, link_id) VALUES ('{tenant ?? tenantA}', '{notification}', '{alert}', '{link}')";
            await Exec(conn, Na(n1, alert1, link5));
            await Rejects(conn, Na(n1, alert1, link5), "pk_beauty_staff_notification_alerts");
            await Rejects(conn, Na(n2, alert1, link5), "ux_beauty_staff_notification_alerts_link_alert"); // той самий алерт тому ж адресату вдруге
            await Exec(conn, Na(n2, alert2, link5)); // дайджест охоплює інші алерти
            var n3 = await Id(conn, Nf(link2, "alert:l2:a1") + " RETURNING id");
            await Exec(conn, Na(n3, alert1, link2)); // той самий алерт іншому адресату допустимий
            var restrictAlert = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_staff_alerts WHERE id = '{alert1}'"));
            Assert.Equal("23503", restrictAlert.SqlState);
            Assert.Equal(1, await ExecCount(conn, $"DELETE FROM beauty_staff_notifications WHERE id = '{n3}'")); // CASCADE на (d)
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM beauty_staff_notification_alerts WHERE notification_id = '{n3}'"));
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_notification_alerts"));
            // RESTRICT: канал і користувач із прив'язками
            var restrictChannel = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_channels WHERE id = '{chA}'"));
            Assert.Equal("23503", restrictChannel.SqlState);
            var restrictUser = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM users WHERE id = '{u3}'"));
            Assert.Equal("23503", restrictUser.SqlState);

            // ---- крос-tenant: ізоляція RLS, WITH CHECK, FK між tenant, fail-closed ----
            await Ctx(conn, tenantB);
            var nB = await Id(conn, Nf(linkB, "alert:lb:a1", tenant: tenantB) + " RETURNING id");
            var crossNotification = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Nf(link5, "x", tenant: tenantB)));
            Assert.Equal("23503", crossNotification.SqlState);
            var crossAlert = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Na(nB, alert1, linkB, tenantB)));
            Assert.Equal("23503", crossAlert.SqlState);
            var crossNa = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Na(n2, alert1, linkB, tenantB)));
            Assert.Equal("23503", crossNa.SqlState);
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_telegram_links"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_notifications"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_telegram_link_events"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_notification_alerts"));
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM beauty_staff_telegram_links WHERE tenant_id = '{tenantA}'"));
            Assert.Equal(0, await ExecCount(conn, $"UPDATE beauty_staff_telegram_links SET updated_at = now() WHERE id = '{link5}'")); // чужий рядок невидимий
            var withCheck = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Issued(uB, "b1", chB, tenantA)));
            Assert.Equal("42501", withCheck.SqlState); // WITH CHECK: запис у чужий tenant
            await Exec(conn, "SELECT set_config('app.tenant_id', '', false)");
            foreach (var table in NewTables) Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}"));
            await Ctx(conn, tenantA);

            // ---- GRANT-и: справжній deploy/staging/db/grants.sql із підміною імен ролей (GRANT/REVOKE на FUNCTION належать beautycrm_catalog - поза тестом) ----
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
                Assert.Equal(1, await ExecCount(appConn, Ev(link5, u3, "issued", "user"))); // INSERT журналу
                Assert.True(await Scalar(appConn, "SELECT count(*) FROM beauty_staff_telegram_link_events") > 0); // SELECT журналу
                foreach (var sql in new[] { "UPDATE beauty_staff_telegram_link_events SET reason = NULL", "DELETE FROM beauty_staff_telegram_link_events", "TRUNCATE beauty_staff_telegram_link_events" })
                {
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, sql));
                    Assert.Equal("42501", denied.SqlState);
                }
                Assert.True(await ExecCount(appConn, "UPDATE beauty_staff_telegram_links SET updated_at = now()") > 0); // DML прив'язок і черги
                Assert.True(await ExecCount(appConn, "UPDATE beauty_staff_notifications SET updated_at = now()") > 0);
                Assert.Equal(1, await ExecCount(appConn, Nf(link5, "alert:l5:app")));
                Assert.Equal(1, await ExecCount(appConn, "DELETE FROM beauty_staff_notifications WHERE dedupe_key = 'alert:l5:app'"));
            }
            await using (var wrkConn = new NpgsqlConnection(Cs(worker)))
            {
                await wrkConn.OpenAsync();
                await Ctx(wrkConn, tenantA);
                // прив'язки: колонковий SELECT/UPDATE, без INSERT/DELETE, без токена/хешу/згоди
                await Exec(wrkConn, "SELECT id, tenant_id, user_id, channel_id, status, chat_id_encrypted, token_expires_at, activated_at FROM beauty_staff_telegram_links");
                foreach (var sql in new[]
                         {
                             "SELECT * FROM beauty_staff_telegram_links", "SELECT token_hash FROM beauty_staff_telegram_links",
                             "SELECT chat_id_hash FROM beauty_staff_telegram_links", "SELECT consent_at FROM beauty_staff_telegram_links",
                             "UPDATE beauty_staff_telegram_links SET consent_version = 'inv-tg-v2'", "UPDATE beauty_staff_telegram_links SET user_id = user_id",
                             "DELETE FROM beauty_staff_telegram_links", Issued(u2, "b2"),
                             // журнал: лише INSERT
                             "SELECT count(*) FROM beauty_staff_telegram_link_events", "UPDATE beauty_staff_telegram_link_events SET reason = NULL",
                             "DELETE FROM beauty_staff_telegram_link_events", "TRUNCATE beauty_staff_telegram_link_events",
                             // зв'язок алертів: без UPDATE/DELETE
                             "UPDATE beauty_staff_notification_alerts SET link_id = link_id", "DELETE FROM beauty_staff_notification_alerts",
                             // нові колонкові SELECT не відкривають зайвого; норми матеріалів воркеру не потрібні
                             "SELECT password_hash FROM users", "SELECT purchase_price FROM beauty_products", "SELECT count(*) FROM beauty_service_materials",
                         })
                {
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(wrkConn, sql));
                    Assert.Equal("42501", denied.SqlState);
                }
                Assert.Equal(1, await ExecCount(wrkConn, $"UPDATE beauty_staff_telegram_links SET last_delivery_at = now(), updated_at = now() WHERE id = '{link5}'"));
                Assert.Equal(1, await ExecCount(wrkConn, Ev(link5, u3, "revoked", "system", "user_deactivated"))); // INSERT журналу
                Assert.True(await Scalar(wrkConn, "SELECT count(*) FROM beauty_staff_notifications") > 0); // черга: повний DML
                Assert.Equal(1, await ExecCount(wrkConn, Nf(link5, "alert:l5:w1")));
                Assert.Equal(1, await ExecCount(wrkConn, "UPDATE beauty_staff_notifications SET status = 'sending', updated_at = now() WHERE dedupe_key = 'alert:l5:w1'"));
                var wn = await Id(wrkConn, Nf(link5, "alert:l5:w2") + " RETURNING id");
                Assert.Equal(1, await ExecCount(wrkConn, Na(wn, alert3, link5)));
                Assert.True(await Scalar(wrkConn, "SELECT count(*) FROM beauty_staff_notification_alerts") > 0);
                Assert.Equal(1, await ExecCount(wrkConn, $"DELETE FROM beauty_staff_notifications WHERE id = '{wn}'")); // прибирання; (d) - каскадом
                Assert.Equal(0L, await Scalar(wrkConn, $"SELECT count(*) FROM beauty_staff_notification_alerts WHERE alert_id = '{alert3}'"));
                await Exec(wrkConn, "SELECT is_active, state FROM users");
                await Exec(wrkConn, "SELECT name, unit FROM beauty_products");
                await Exec(wrkConn, "SELECT name, timezone FROM beauty_locations");
                await Exec(wrkConn, "SELECT lot_number FROM beauty_stock_batches");
                // відв'язка воркером: стирання даних чату й токена (прибирання прострочених)
                Assert.Equal(1, await ExecCount(wrkConn, $"UPDATE beauty_staff_telegram_links SET status = 'revoked', token_hash = NULL, chat_id_encrypted = NULL, chat_id_hash = NULL, revoked_at = now(), revoke_reason = 'expired', updated_at = now() WHERE id = '{link2}'"));
            }

            // ---- Down із історією 2b відмовляє; нічого не втрачається ----
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("inventory stage2b history exists", FullMessage(refused));
            foreach (var table in NewTables)
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.True(await Scalar(conn, "SELECT count(*) FROM beauty_staff_telegram_link_events") > 0);

            // свідоме очищення (runbook): лише події недостатньо - лишаються прив'язки зі станом, відмінним від issued
            await Exec(conn, "ALTER TABLE beauty_staff_telegram_link_events DISABLE TRIGGER USER");
            await Exec(conn, "DELETE FROM beauty_staff_telegram_link_events");
            await Exec(conn, "ALTER TABLE beauty_staff_telegram_link_events ENABLE TRIGGER USER");
            var stillRefused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("inventory stage2b history exists", FullMessage(stillRefused));
            // і навпаки: прив'язка лише issued, але є подія - теж відмова
            await Exec(conn, "DELETE FROM beauty_staff_notification_alerts");
            await Exec(conn, "DELETE FROM beauty_staff_notifications");
            await Exec(conn, "DELETE FROM beauty_staff_telegram_links");
            var onlyIssued = await Id(conn, Issued(u1, "d1") + " RETURNING id");
            await Exec(conn, Ev(onlyIssued, u1, "issued", "user", actorUser: u1));
            var refusedByEvent = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("inventory stage2b history exists", FullMessage(refusedByEvent));
            await Exec(conn, "ALTER TABLE beauty_staff_telegram_link_events DISABLE TRIGGER USER");
            await Exec(conn, "DELETE FROM beauty_staff_telegram_link_events");
            await Exec(conn, "ALTER TABLE beauty_staff_telegram_link_events ENABLE TRIGGER USER");

            // лише issued без подій (і рядки issued іншого tenant): Down дозволений
            await migrator.MigrateAsync(Previous);
            foreach (var table in NewTables)
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}')"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_proc WHERE proname = 'beauty_staff_telegram_events_block_mutation'"));
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_alerts")); // етап 2a цілий
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal"));

            await migrator.MigrateAsync(); // Up -> Down -> Up
            foreach (var table in NewTables)
            {
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}"));
            }
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_staff_telegram_link_events_%' AND NOT tgisinternal"));
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_alerts"));
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

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
