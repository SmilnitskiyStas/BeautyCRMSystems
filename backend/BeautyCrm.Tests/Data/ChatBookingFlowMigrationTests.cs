using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-836 (ADR-020 §2.4, §2.8): міграція beauty_chat_booking_flow. Перевіряє FORCE RLS і ізоляцію tenant на нових таблицях,
/// CHECK координат закладу, частковий UNIQUE «одна активна flow на розмову», CAS-версію, композитні FK (крос-tenant),
/// CASCADE підказки з розмовою та Down (таблиці й колонки зникають, повторний Up проходить).
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class ChatBookingFlowMigrationTests
{
    private const string Previous = "20261104182836_platform_control_plane";

    [SkippableFact]
    public async Task up_creates_rls_forced_tables_constraints_hold_and_down_reverts()
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
        var db = $"beauty_flow_{suffix}";
        var owner = $"beauty_flow_owner_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = new NpgsqlConnection(adminCs))
            {
                await admin.OpenAsync();
                await Exec(admin, $"CREATE ROLE {owner} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
                // ADR-011: ops заздалегідь створює роль каталогу (CREATEROLE у мігратора немає)
                await Exec(admin, "DO $$ BEGIN CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; EXCEPTION WHEN duplicate_object THEN NULL; END $$");
                await Exec(admin, $"GRANT beautycrm_catalog TO {owner} WITH INHERIT FALSE, SET TRUE");
                await Exec(admin, $"CREATE DATABASE {db} OWNER {owner}");
            }
            var ownerCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = db, Username = owner, Password = password }.ConnectionString;

            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(ownerCs);
            await using var ctx = new BeautyDbContext(options.Options, new TenantContext());
            var migrator = ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
                           ?? throw new InvalidOperationException("IMigrator not available");
            await migrator.MigrateAsync(Previous);
            await using var conn = new NpgsqlConnection(ownerCs);
            await conn.OpenAsync();

            // ---- дані "до міграції": заклад без координат ----
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{t}', false)");
                await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'f-{t:N}')");
            }
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");
            var locA = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'Центр', 'Europe/Kyiv', true) RETURNING id");

            // дані "до міграції": повідомлення, які мають пережити Up/Down
            var channel = await Id(conn, $"INSERT INTO beauty_channels (tenant_id, type, name, is_active) VALUES ('{tenantA}', 'telegram', 'bot', true) RETURNING id");
            var conv = await Id(conn, $"INSERT INTO beauty_conversations (tenant_id, channel_id, external_chat_id) VALUES ('{tenantA}', '{channel}', '1') RETURNING id");
            var svc = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenantA}', 'Стрижка', 60, true) RETURNING id");

            await migrator.MigrateAsync();

            // ---- адитивність і FORCE RLS ----
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_locations WHERE latitude IS NULL AND longitude IS NULL"));
            foreach (var table in new[] { "beauty_chat_booking_flows", "beauty_chat_keyboards", "beauty_messages", "beauty_locations", "beauty_chat_booking_proposals" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_policies WHERE policyname = 'tenant_isolation' AND tablename = 'beauty_chat_booking_flows'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_chat_location_hints')")); // локацію клієнта не зберігаємо
            // FK на потік: ON DELETE SET NULL (flow_id) - confdeltype 'n' зі списком колонок
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE confrelid = to_regclass('public.beauty_chat_booking_flows') AND contype = 'f' AND confdeltype = 'n' AND confdelsetcols IS NOT NULL AND cardinality(confdelsetcols) = 1 AND conrelid IN (to_regclass('public.beauty_chat_keyboards'), to_regclass('public.beauty_chat_booking_proposals'))"));

            // ---- CHECK координат: обидва або жодного, діапазони ----
            await Exec(conn, $"UPDATE beauty_locations SET latitude = 50.4501, longitude = 30.5234 WHERE id = '{locA}'");
            foreach (var bad in new[] { "latitude = 50.1, longitude = NULL", "latitude = NULL, longitude = 30.1", "latitude = 90.5, longitude = 30", "latitude = 50, longitude = -180.1" })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_locations SET {bad} WHERE id = '{locA}'"));
                Assert.Equal("ck_beauty_locations_coordinates", ex.ConstraintName);
            }

            // ---- flow: дефолти, одна активна на розмову, CAS, CHECK кроків і залежностей ----
            var spec = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenantA}', 'S', true) RETURNING id");
            // TASK-838: перший крок - послуга (без жодного вибору); вибір зростає зі кроком (§28.10)
            var flow = await Id(conn, $"INSERT INTO beauty_chat_booking_flows (tenant_id, conversation_id, step, expires_at) VALUES ('{tenantA}', '{conv}', 'service', now() + interval '30 minutes') RETURNING id");
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_chat_booking_flows WHERE id = '{flow}' AND status = 'active' AND version = 0 AND origin = 'button' AND skipped_steps = '{{}}' AND suggested_last_location_id IS NULL AND preferred_time IS NULL"));
            var dup = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"INSERT INTO beauty_chat_booking_flows (tenant_id, conversation_id, step, expires_at) VALUES ('{tenantA}', '{conv}', 'service', now() + interval '30 minutes')"));
            Assert.Equal("ux_beauty_chat_booking_flows_active", dup.ConstraintName);
            foreach (var step in new[] { "last_location", "location", "service", "specialist", "time", "day", "confirm" })
                Assert.Contains(step, await Text(conn, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_beauty_chat_booking_flows_step'"));
            var badStep = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET step = 'pay' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_step", badStep.ConstraintName);
            // залежності кроків від вибору
            var needSvc = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET step = 'specialist' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_step_service", needSvc.ConstraintName);
            await Exec(conn, $"UPDATE beauty_chat_booking_flows SET service_id = '{svc}' WHERE id = '{flow}'");
            var needSpec = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET step = 'location' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_step_specialist", needSpec.ConstraintName);
            await Exec(conn, $"UPDATE beauty_chat_booking_flows SET specialist_id = '{spec}' WHERE id = '{flow}'");
            var needLoc = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET step = 'time' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_step_location", needLoc.ConstraintName);
            var needLast = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET step = 'last_location' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_step_last_location", needLast.ConstraintName);
            var badSkip = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET skipped_steps = '{{service}}' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_skipped_steps", badSkip.ConstraintName);
            // CAS: перший виграє, другий з тією ж версією - 0 рядків
            Assert.Equal(1, await ExecCount(conn, $"UPDATE beauty_chat_booking_flows SET step = 'service', version = version + 1 WHERE id = '{flow}' AND version = 0 AND status = 'active'"));
            Assert.Equal(0, await ExecCount(conn, $"UPDATE beauty_chat_booking_flows SET step = 'service', version = version + 1 WHERE id = '{flow}' AND version = 0 AND status = 'active'"));
            await Exec(conn, $"UPDATE beauty_chat_booking_flows SET service_id = '{svc}', preferred_time = '10:30', suggested_last_location_id = '{locA}' WHERE id = '{flow}'");
            // термінальні статуси
            var noFinish = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET status = 'completed' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_finished", noFinish.ConstraintName);
            var noReason = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET status = 'cancelled', finished_at = now() WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_cancel", noReason.ConstraintName);
            var badReason = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"UPDATE beauty_chat_booking_flows SET status = 'cancelled', finished_at = now(), cancel_reason = 'x' WHERE id = '{flow}'"));
            Assert.Equal("ck_beauty_chat_booking_flows_cancel_reason", badReason.ConstraintName);

            // ---- keyboards: flow_* kind, зв'язок з потоком, послаблений CHECK для не-active ----
            var msg = await Id(conn, $"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind) VALUES ('{tenantA}', '{conv}', 'outbound', 'ai', 'b', 'text') RETURNING id");
            string Kb(string kind, string flowId, string flowVersion, string status, string token) =>
                $"INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, status, expires_at, flow_id, flow_version) VALUES ('{tenantA}', '{conv}', '{msg}', '{kind}', '{token}', '[]', '{status}', now() + interval '30 minutes', {flowId}, {flowVersion})";
            const string T1 = "AAAAAAAAAAAAAAAAAAAAAA";
            var noVer = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Kb("flow_service", $"'{flow}'", "NULL", "active", T1)));
            Assert.Equal("ck_beauty_chat_keyboards_flow_link", noVer.ConstraintName);
            var activeNoFlow = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Kb("flow_service", "NULL", "1", "active", T1)));
            Assert.Equal("ck_beauty_chat_keyboards_flow_link", activeNoFlow.ConstraintName);
            var foreignKind = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Kb("slot_pick", $"'{flow}'", "1", "active", T1)));
            Assert.Equal("ck_beauty_chat_keyboards_flow_none", foreignKind.ConstraintName);
            var badKind = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Kb("flow_pay", $"'{flow}'", "1", "active", T1)));
            Assert.Equal("ck_beauty_chat_keyboards_kind", badKind.ConstraintName);
            await Exec(conn, Kb("flow_last_location", $"'{flow}'", "1", "active", T1));
            // видалення потоку обнуляє лише flow_id (tenant_id цілий); active-клавіатура з NULL flow_id заборонена CHECK, тому спершу завершена
            await Assert.ThrowsAnyAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_chat_booking_flows WHERE id = '{flow}'"));
            await Exec(conn, "UPDATE beauty_chat_keyboards SET status = 'expired'");
            await Exec(conn, $"UPDATE beauty_chat_booking_flows SET status = 'completed', finished_at = now() WHERE id = '{flow}'");
            // завершена flow не блокує нову активну
            var flow2 = await Id(conn, $"INSERT INTO beauty_chat_booking_flows (tenant_id, conversation_id, step, expires_at) VALUES ('{tenantA}', '{conv}', 'service', now() + interval '30 minutes') RETURNING id");
            await Exec(conn, $"DELETE FROM beauty_chat_booking_flows WHERE id = '{flow}'");
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_chat_keyboards WHERE flow_id IS NULL AND flow_version = 1 AND status = 'expired'"));

            // ---- messages.kind = location (без координат) ----
            await Exec(conn, $"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind) VALUES ('{tenantA}', '{conv}', 'inbound', 'client', '[локація]', 'location')");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_messages' AND column_name ~ '(lat|lng|lon)'"));

            // ---- RLS: tenant B не бачить і не пише чужого; без app.tenant_id - fail-closed ----
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantB}', false)");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_chat_booking_flows"));
            await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"INSERT INTO beauty_chat_booking_flows (tenant_id, conversation_id, step, expires_at) VALUES ('{tenantA}', '{conv}', 'service', now())"));
            // композитний FK: tenant B не може послатися на розмову tenant A
            await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"INSERT INTO beauty_chat_booking_flows (tenant_id, conversation_id, step, expires_at) VALUES ('{tenantB}', '{conv}', 'service', now())"));
            await Exec(conn, "SELECT set_config('app.tenant_id', '', false)");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_chat_booking_flows"));
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");

            // ---- Down: flow_* клавіатури видаляються, location -> unsupported, таблиця й колонки зникають, старі CHECK повернено ----
            await Exec(conn, "UPDATE beauty_chat_booking_flows SET status = 'cancelled', cancel_reason = 'client', finished_at = now()");
            await migrator.MigrateAsync(Previous);
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_chat_booking_flows')"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE (table_name = 'beauty_locations' AND column_name IN ('latitude', 'longitude')) OR (table_name IN ('beauty_chat_keyboards', 'beauty_chat_booking_proposals') AND column_name IN ('flow_id', 'flow_version'))"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_locations"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_chat_keyboards"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE kind = 'location'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE kind = 'unsupported'"));
            foreach (var table in new[] { "beauty_chat_keyboards", "beauty_messages" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relforcerowsecurity"));
            var oldKind = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind) VALUES ('{tenantA}', '{conv}', 'inbound', 'client', 'b', 'location')"));
            Assert.Equal("ck_beauty_messages_kind", oldKind.ConstraintName);
            await migrator.MigrateAsync(); // повторний Up
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_chat_booking_flows') AND relforcerowsecurity"));
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

    private static async Task<string> Text(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
