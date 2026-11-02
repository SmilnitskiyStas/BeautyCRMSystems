using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-776 (§26.3, ADR-015): міграція beauty_marketing. Up роллю-власником під FORCE RLS (NOSUPERUSER NOBYPASSRLS):
/// адитивність, скидання marketing_consent у false БЕЗ втрати решти полів і БЕЗ запису в нові таблиці, FORCE повернено;
/// Down видаляє нові таблиці/колонки й клавіатури нових видів, клієнти лишаються, прапорець не відновлюється; повторний Up.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class MarketingMigrationTests
{
    private const string Previous = "20261026093343_beauty_client_phone_capture";

    private static readonly string[] NewTables =
    {
        "beauty_marketing_consents", "beauty_marketing_consent_events", "beauty_campaigns",
        "beauty_campaign_recipients", "beauty_coupons", "beauty_coupon_redemptions",
    };

    [SkippableFact]
    public async Task up_resets_marketing_consent_without_losing_data_and_down_drops_everything_new()
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
        var db = $"beauty_mkt_{suffix}";
        var owner = $"beauty_mkt_owner_{suffix}";
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

            // ---- дані "до міграції": два tenant, клієнти з різними прапорцями, відкладений запис, клавіатура старого виду ----
            var t1 = Guid.NewGuid();
            var t2 = Guid.NewGuid();
            await using var conn = new NpgsqlConnection(ownerCs);
            await conn.OpenAsync();
            foreach (var t in new[] { t1, t2 })
            {
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{t}', false)");
                await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'm-{t:N}')");
            }

            async Task<Guid> Client(Guid tenant, string name, bool consent, bool unsub)
            {
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
                return await Id(conn, $@"INSERT INTO beauty_clients (tenant_id, full_name, phone, marketing_consent, unsubscribed)
                    VALUES ('{tenant}', '{name}', '+38050{Random.Shared.Next(1000000, 9999999)}', {(consent ? "true" : "false")}, {(unsub ? "true" : "false")}) RETURNING id");
            }
            var c1 = await Client(t1, "Згода", true, false);
            var c2 = await Client(t1, "Згода й відписка", true, true);
            var c3 = await Client(t1, "Без згоди", false, false);
            var c4 = await Client(t2, "Інший tenant", true, false);

            await Exec(conn, $"SELECT set_config('app.tenant_id', '{t1}', false)");
            var channel = await Id(conn, $"INSERT INTO beauty_channels (tenant_id, type, name, is_active) VALUES ('{t1}', 'telegram', 'bot', true) RETURNING id");
            var conv = await Id(conn, $"INSERT INTO beauty_conversations (tenant_id, channel_id, client_id, external_chat_id) VALUES ('{t1}', '{channel}', '{c1}', '1') RETURNING id");
            var msg = await Id(conn, $"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body) VALUES ('{t1}', '{conv}', 'outbound', 'system', 'b') RETURNING id");
            await Exec(conn, $@"INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, expires_at)
                VALUES ('{t1}', '{conv}', '{msg}', 'slot_pick', '{new string('k', 22)}', '[]'::jsonb, now() + interval '1 day')");

            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_campaigns')"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_promotions' AND column_name = 'access'"));

            // ---- Up роллю-власником під FORCE RLS ----
            await migrator.MigrateAsync();

            // дані не втрачено, прапорець скинуто лише там, де був true; відписка, ім'я, телефон, updated_at не змінились
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{t1}', false)");
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_clients"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_clients WHERE marketing_consent"));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_clients WHERE id = '{c2}' AND unsubscribed AND full_name = 'Згода й відписка' AND phone IS NOT NULL"));
            Assert.Equal(2L, await Scalar(conn, $"SELECT count(*) FROM beauty_clients WHERE id IN ('{c1}', '{c3}') AND NOT unsubscribed AND marketing_offered_at IS NULL"));
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{t2}', false)");
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_clients WHERE id = '{c4}' AND NOT marketing_consent")); // скидання охопило всіх tenant
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{t1}', false)");

            // згоди НЕ переносяться і історії немає (§26.3): усі нові таблиці порожні
            foreach (var table in NewTables)
            {
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}"));
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            }
            foreach (var table in new[] { "beauty_clients", "beauty_promotions", "beauty_appointments", "beauty_conversations", "beauty_chat_keyboards", "beauty_chat_booking_proposals" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_chat_keyboards WHERE kind = 'slot_pick'")); // старі клавіатури лишились

            // нові клавіатури вже можливі; готуємо їх для Down
            var msg2 = await Id(conn, $"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body) VALUES ('{t1}', '{conv}', 'outbound', 'system', 'c') RETURNING id");
            await Exec(conn, $@"INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, expires_at)
                VALUES ('{t1}', '{conv}', '{msg2}', 'marketing_consent', '{new string('m', 22)}', '[]'::jsonb, now() + interval '1 day')");
            await Exec(conn, $"INSERT INTO beauty_marketing_consents (tenant_id, client_id, channel, level, source, text_version) VALUES ('{t1}', '{c1}', 'telegram', 'full', 'chat', 'mc-v1')");

            // ---- Down: нові таблиці й колонки зникають, клієнти й старі клавіатури лишаються, прапорець не відновлюється ----
            await migrator.MigrateAsync(Previous);
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_clients"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_clients WHERE marketing_consent"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid IN (" + string.Join(", ", NewTables.Select(t => $"to_regclass('public.{t}')")) + ")"));
            Assert.Equal(0L, await Scalar(conn, @"SELECT count(*) FROM information_schema.columns WHERE
                (table_name = 'beauty_promotions' AND column_name = 'access') OR (table_name = 'beauty_clients' AND column_name = 'marketing_offered_at')
                OR (table_name IN ('beauty_appointments', 'beauty_chat_booking_proposals') AND column_name IN ('coupon_id', 'campaign_id'))
                OR (table_name = 'beauty_conversations' AND column_name IN ('offer_campaign_id', 'offer_coupon_id', 'offer_until'))
                OR (table_name = 'beauty_chat_keyboards' AND column_name = 'campaign_recipient_id')"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_chat_keyboards"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_chat_keyboards WHERE kind = 'slot_pick'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_beauty_chat_keyboards_kind' AND pg_get_constraintdef(oid) LIKE '%campaign_offer%'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE conname LIKE 'ck_beauty_promotions_access' OR conname LIKE 'ck_beauty_conversations_offer%'"));
            foreach (var table in new[] { "beauty_clients", "beauty_chat_keyboards", "beauty_appointments" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relforcerowsecurity"));

            await migrator.MigrateAsync(); // повторний Up (idempotent відносно даних: нічого не скидати, таблиці порожні)
            foreach (var table in NewTables)
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relforcerowsecurity"));
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_clients"));
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

    private static async Task<object?> ScalarObj(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql) => (long)(await ScalarObj(conn, sql))!;
}
