using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-767 (§25.3): міграція beauty_client_phone_capture. Адитивність без backfill (наявні клієнти/повідомлення/розмови не змінюються),
/// FORCE RLS роллю-власником (NOSUPERUSER NOBYPASSRLS), Down: contact -> unsupported (рядок лишається), phone_request -> pending_prompt NULL,
/// колонки знімаються, старі CHECK повертаються; повторний Up проходить. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class PhoneCaptureMigrationTests
{
    private const string Previous = "20261026091203_beauty_service_costs";

    [SkippableFact]
    public async Task up_is_additive_down_maps_contact_to_unsupported_clears_phone_request_and_drops_columns()
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
        var db = $"beauty_phone_{suffix}";
        var owner = $"beauty_phone_owner_{suffix}";
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

            // ---- дані "до міграції" ----
            var tenant = Guid.NewGuid();
            await using var conn = new NpgsqlConnection(ownerCs);
            await conn.OpenAsync();
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
            await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenant}', 'T', 'p-{tenant:N}')");
            var loc = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenant}', 'L', 'Europe/Kyiv', true) RETURNING id");
            var spec = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenant}', 'S', true) RETURNING id");
            var svc = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenant}', 'Svc', 60, true) RETURNING id");
            var cl = await Id(conn, $"INSERT INTO beauty_clients (tenant_id, full_name, phone) VALUES ('{tenant}', 'C', '050 111 22 33') RETURNING id");
            var appt = await Id(conn, $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final)
                VALUES ('{tenant}', '{loc}', '{spec}', '{svc}', '{cl}', now() + interval '2 day', 60, 'pending', 'admin', 200, 150) RETURNING id");
            var channel = await Id(conn, $"INSERT INTO beauty_channels (tenant_id, type, name, is_active) VALUES ('{tenant}', 'telegram', 'bot', true) RETURNING id");
            var conv = await Id(conn, $@"INSERT INTO beauty_conversations (tenant_id, channel_id, client_id, external_chat_id, pending_prompt, pending_prompt_appointment_id, pending_prompt_expires_at)
                VALUES ('{tenant}', '{channel}', '{cl}', '1', 'reminder_choice', '{appt}', now() + interval '1 hour') RETURNING id");
            foreach (var kind in new[] { "text", "unsupported", "callback" })
                await Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind)
                    VALUES ('{tenant}', '{conv}', 'inbound', 'client', 'b', '{kind}')");
            // до міграції 'contact' відхиляється
            var before = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind)
                VALUES ('{tenant}', '{conv}', 'inbound', 'client', 'b', 'contact')"));
            Assert.Equal("ck_beauty_messages_kind", before.ConstraintName);

            // ---- Up роллю-власником під FORCE RLS ----
            await migrator.MigrateAsync();
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_clients"));
            Assert.Equal(1L, await Scalar(conn, @"SELECT count(*) FROM beauty_clients WHERE phone = '050 111 22 33' AND phone_source IS NULL AND call_consent_at IS NULL
                AND phone_pending IS NULL AND phone_pending_source IS NULL AND phone_pending_at IS NULL AND phone_requested_at IS NULL AND duplicate_of_client_id IS NULL")); // без backfill
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE contact_phone IS NULL AND contact_verdict IS NULL"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_conversations WHERE pending_prompt = 'reminder_choice' AND reply_keyboard_open_at IS NULL"));
            foreach (var table in new[] { "beauty_clients", "beauty_conversations", "beauty_messages" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));

            await Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind, contact_phone, contact_verdict)
                VALUES ('{tenant}', '{conv}', 'inbound', 'client', '[контакт]', 'contact', '+380501112233', 'own')");
            await Exec(conn, $@"UPDATE beauty_clients SET phone = '+380501112233', phone_source = 'telegram_contact', call_consent_at = now() WHERE id = '{cl}'");
            await Exec(conn, $@"UPDATE beauty_conversations SET pending_prompt = 'phone_request', pending_prompt_appointment_id = NULL,
                pending_prompt_expires_at = now() + interval '1 hour', reply_keyboard_open_at = now() WHERE id = '{conv}'");

            // ---- Down: contact -> unsupported, phone_request знято, колонки знято, старі CHECK відновлено, FORCE на місці ----
            await migrator.MigrateAsync(Previous);
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM beauty_messages"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE kind = 'contact'"));
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE kind = 'unsupported'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_conversations WHERE pending_prompt IS NULL AND pending_prompt_appointment_id IS NULL AND pending_prompt_expires_at IS NULL"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_clients WHERE phone = '+380501112233'")); // phone лишається, новий лише метадані
            Assert.Equal(0L, await Scalar(conn, @"SELECT count(*) FROM information_schema.columns WHERE
                (table_name = 'beauty_clients' AND column_name IN ('phone_source', 'call_consent_at', 'phone_pending', 'phone_pending_source', 'phone_pending_at', 'phone_requested_at', 'duplicate_of_client_id'))
                OR (table_name = 'beauty_conversations' AND column_name = 'reply_keyboard_open_at')
                OR (table_name = 'beauty_messages' AND column_name IN ('contact_phone', 'contact_verdict'))"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE conname LIKE 'ck_beauty_clients_phone_%' OR conname LIKE 'ck_beauty_messages_contact_%' OR conname = 'ck_beauty_clients_duplicate_not_self'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_beauty_messages_contact_phone'"));
            foreach (var table in new[] { "beauty_clients", "beauty_conversations", "beauty_messages" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relforcerowsecurity"));
            var after = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind)
                VALUES ('{tenant}', '{conv}', 'inbound', 'client', 'b', 'contact')"));
            Assert.Equal("ck_beauty_messages_kind", after.ConstraintName);
            var promptAfter = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $@"UPDATE beauty_conversations SET pending_prompt = 'phone_request',
                pending_prompt_expires_at = now() + interval '1 hour' WHERE id = '{conv}'"));
            Assert.Equal("ck_beauty_conversations_pending_prompt", promptAfter.ConstraintName);

            await migrator.MigrateAsync(); // повторний Up
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_clients WHERE phone_source IS NULL AND duplicate_of_client_id IS NULL"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_messages') AND relforcerowsecurity"));
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
