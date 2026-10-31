using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-749 (§23.3): міграція beauty_chat_buttons. Адитивність: наявні повідомлення (text/command/unsupported) і записи не змінюються,
/// FORCE RLS діє роллю-власником (NOSUPERUSER NOBYPASSRLS), Down переводить callback у unsupported (рядки лишаються) і повертає старий CHECK,
/// повторний Up проходить. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class ChatButtonsMigrationTests
{
    private const string Previous = "20261024193048_beauty_reminders_contacts_inbox";

    [SkippableFact]
    public async Task up_is_additive_down_maps_callback_to_unsupported_and_restores_check()
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
        var db = $"beauty_btn_{suffix}";
        var owner = $"beauty_btn_owner_{suffix}";
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
            await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenant}', 'T', 'b-{tenant:N}')");
            var channel = await Id(conn, $"INSERT INTO beauty_channels (tenant_id, type, name, is_active) VALUES ('{tenant}', 'telegram', 'bot', true) RETURNING id");
            var conv = await Id(conn, $"INSERT INTO beauty_conversations (tenant_id, channel_id, external_chat_id) VALUES ('{tenant}', '{channel}', '1') RETURNING id");
            foreach (var kind in new[] { "text", "command", "unsupported" })
                await Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind)
                    VALUES ('{tenant}', '{conv}', 'inbound', 'client', 'b', '{kind}')");
            // до міграції 'callback' відхиляється
            var before = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind)
                VALUES ('{tenant}', '{conv}', 'inbound', 'client', 'b', 'callback')"));
            Assert.Equal("ck_beauty_messages_kind", before.ConstraintName);

            // ---- Up роллю-власником під FORCE RLS ----
            await migrator.MigrateAsync();
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_messages")); // нічого не втрачено й не змінено
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE actions IS NULL AND callback_data IS NULL AND callback_message_ref IS NULL"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE kind = 'unsupported'"));
            foreach (var table in new[] { "beauty_chat_keyboards", "beauty_messages", "beauty_appointments" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));

            await Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind, callback_data, callback_message_ref)
                VALUES ('{tenant}', '{conv}', 'inbound', 'client', '[кнопка]', 'callback', 'i:help', '7')");

            // ---- Down: callback -> unsupported, рядок лишається, старий CHECK відновлено, FORCE на місці ----
            await migrator.MigrateAsync(Previous);
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM beauty_messages"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE kind = 'callback'"));
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_messages WHERE kind = 'unsupported'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_chat_keyboards')"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_messages') AND relforcerowsecurity"));
            var after = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind)
                VALUES ('{tenant}', '{conv}', 'inbound', 'client', 'b', 'callback')"));
            Assert.Equal("ck_beauty_messages_kind", after.ConstraintName);

            await migrator.MigrateAsync(); // повторний Up
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_chat_keyboards') AND relforcerowsecurity"));
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
