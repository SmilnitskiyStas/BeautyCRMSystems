using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-736 (§22.3): міграція beauty_reminders_contacts_inbox. Backfill спадкових записів (1h/2h/none) і scheduled-нагадувань
/// роллю-власником під FORCE RLS (NOSUPERUSER NOBYPASSRLS), адитивність (нічого не видаляється, не чіпаються чужі стани),
/// FORCE повернуто після міграції, Down відновлює старі CHECK-и. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class RemindersContactsInboxMigrationTests
{
    private const string Previous = "20261024102153_beauty_chat_assistant";

    [SkippableFact]
    public async Task backfill_converts_legacy_reminder_options_and_scheduled_rows_keeps_force_rls_and_down_restores_checks()
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
        var db = $"beauty_rem_{suffix}";
        var owner = $"beauty_rem_owner_{suffix}";
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

            // ---- дані "до міграції": два tenant, щоб перевірити, що backfill не змішує їх ----
            var t1 = Guid.NewGuid();
            var t2 = Guid.NewGuid();
            var ids = new Dictionary<string, Guid>();
            foreach (var (tenant, tag) in new[] { (t1, "a"), (t2, "b") })
            {
                await using var conn = new NpgsqlConnection(ownerCs);
                await conn.OpenAsync();
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
                var location = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenant}', 'L', 'UTC', true) RETURNING id");
                var specialist = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenant}', 'S', true) RETURNING id");
                var service = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenant}', 'Svc', 60, true) RETURNING id");
                var client = await Id(conn, $"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{tenant}', 'C') RETURNING id");
                var day = 3;
                async Task<Guid> Appt(string key, string option)
                {
                    var id = await Id(conn, $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at,
                            duration_minutes, status, source, price_original, price_final, reminder_option)
                        VALUES ('{tenant}', '{location}', '{specialist}', '{service}', '{client}', date_trunc('hour', now()) + interval '{day++} day', 60,
                            'confirmed', 'admin', 100, 100, '{option}') RETURNING id");
                    ids[tag + key] = id;
                    return id;
                }
                async Task<Guid> Rem(string key, Guid appt, string status, int hoursBefore)
                {
                    var id = await Id(conn, $@"INSERT INTO beauty_reminders (tenant_id, appointment_id, scheduled_at, status)
                        VALUES ('{tenant}', '{appt}', now() + interval '{hoursBefore} hour', '{status}') RETURNING id");
                    ids[tag + key] = id;
                    return id;
                }
                var one = await Appt("1h", "1h");
                var two = await Appt("2h", "2h");
                var none = await Appt("none", "none");
                await Rem("r1h", one, "scheduled", 5);
                await Rem("r1h_sent", one, "sent", 1);         // історичний рядок не чіпаємо
                await Rem("r2h_old", two, "scheduled", 2);     // дубль scheduled: отримує offset лише останній (UNIQUE не падає)
                await Rem("r2h_new", two, "scheduled", 9);
                await Rem("rnone", none, "scheduled", 7);      // scheduled без offset у плані (none): лише method/planned_starts_at
                await Rem("rcancel", two, "cancelled", 3);
            }

            await migrator.MigrateAsync(); // власник, NOSUPERUSER NOBYPASSRLS: проходить лише завдяки тимчасовому NO FORCE

            async Task<NpgsqlConnection> Open(Guid tenant)
            {
                var c = new NpgsqlConnection(ownerCs);
                await c.OpenAsync();
                await Exec(c, $"SELECT set_config('app.tenant_id', '{tenant}', false)");
                return c;
            }

            foreach (var (tenant, tag) in new[] { (t1, "a"), (t2, "b") })
            {
                await using var c = await Open(tenant);
                async Task<string> Plan(string key) =>
                    await Text(c, $"SELECT reminder_method || ':' || array_to_string(reminder_offsets, ',') || ':' || reminder_option FROM beauty_appointments WHERE id = '{ids[tag + key]}'") ?? "<null>";
                Assert.Equal("auto:60:1h", await Plan("1h"));
                Assert.Equal("auto:120:2h", await Plan("2h"));
                Assert.Equal("none::none", await Plan("none"));

                async Task<string> Rem(string key) =>
                    await Text(c, $@"SELECT coalesce(offset_minutes::text, '-') || ':' || coalesce(method, '-') || ':' ||
                                            coalesce((planned_starts_at = (SELECT starts_at FROM beauty_appointments a WHERE a.id = r.appointment_id))::text, '-')
                                     FROM beauty_reminders r WHERE id = '{ids[tag + key]}'") ?? "<null>";
                Assert.Equal("60:auto:true", await Rem("r1h"));
                Assert.Equal("120:auto:true", await Rem("r2h_new"));      // найновіший scheduled
                Assert.Equal("-:auto:true", await Rem("r2h_old"));        // спадковий (offset NULL), але method/planned заповнено
                Assert.Equal("-:auto:true", await Rem("rnone"));
                Assert.Equal("-:-:-", await Rem("r1h_sent")); // історичні статуси не чіпаємо
                Assert.Equal("-:-:-", await Rem("rcancel"));

                Assert.Equal(3L, await Scalar(c, "SELECT count(*) FROM beauty_appointments")); // нічого не видалено
                Assert.Equal(6L, await Scalar(c, "SELECT count(*) FROM beauty_reminders"));
            }

            // FORCE повернуто на змінюваних таблицях: без app.tenant_id власник нічого не бачить
            await using (var c = new NpgsqlConnection(ownerCs))
            {
                await c.OpenAsync();
                foreach (var table in new[] { "beauty_appointments", "beauty_reminders", "beauty_conversations", "beauty_messages", "beauty_clients",
                             "beauty_client_contact_preferences", "beauty_call_tasks", "beauty_reminder_settings" })
                    Assert.Equal(1L, await Scalar(c, $"SELECT count(*) FROM pg_class WHERE oid = '{table}'::regclass AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM beauty_appointments"));
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM beauty_reminders"));
            }

            // застосунок після міграції пише custom (дзеркало плану з кількох точок)
            await using (var c = await Open(t1))
            {
                await Exec(c, "ALTER TABLE beauty_appointments NO FORCE ROW LEVEL SECURITY");
                await Exec(c, "UPDATE beauty_appointments SET reminder_option = 'custom', reminder_method = 'telegram', reminder_offsets = '{120,1440}' WHERE id = '" + ids["a2h"] + "'");
                await Exec(c, "ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY");
            }

            // ---- Down: старі CHECK-и повертаються, custom/skipped приводяться до допустимих, дані й FORCE лишаються ----
            await using (var c = await Open(t1))
            {
                await Exec(c, $"INSERT INTO beauty_reminders (tenant_id, appointment_id, scheduled_at, status, offset_minutes) VALUES ('{t1}', '{ids["a2h"]}', now(), 'skipped', 1440)");
            }
            await migrator.MigrateAsync(Previous);
            await using (var c = await Open(t1))
            {
                Assert.Equal("none", await Text(c, $"SELECT reminder_option FROM beauty_appointments WHERE id = '{ids["a2h"]}'"));
                Assert.Equal("1h", await Text(c, $"SELECT reminder_option FROM beauty_appointments WHERE id = '{ids["a1h"]}'"));
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM beauty_reminders WHERE status = 'skipped'"));
                Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM pg_class WHERE oid = 'beauty_appointments'::regclass AND relforcerowsecurity"));
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('beauty_call_tasks', 'beauty_client_contact_preferences', 'beauty_reminder_settings')"));
                await using var bad = new NpgsqlCommand("UPDATE beauty_appointments SET reminder_option = 'custom'", c);
                await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
            }
            // і знову вгору: міграція повторювана
            await migrator.MigrateAsync();
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

    private static async Task<string?> Text(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
