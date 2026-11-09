using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Міграція beauty_specialist_profile_reviews: FORCE RLS і ізоляція tenant-ів у beauty_specialist_reviews, один відгук на запис
/// (унікальний appointment_id), CHECK-обмеження профілю майстра, дані майстрів переживають Down/Up, Down чисто відкочує.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку).
/// </summary>
public sealed class SpecialistReviewsMigrationTests
{
    private const string Previous = "20261109075625_beauty_public_location_catalog";

    [SkippableFact]
    public async Task up_creates_rls_protected_reviews_table_and_down_rolls_back_cleanly()
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
        var db = $"beauty_rev_{suffix}";
        var owner = $"beauty_rev_owner_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = new NpgsqlConnection(adminCs))
            {
                await admin.OpenAsync();
                await Exec(admin, $"CREATE ROLE {owner} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
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

            // id міграції береться за назвою (часова мітка зміниться, якщо міграцію перегенерують)
            var target = ctx.Database.GetMigrations().Single(m => m.EndsWith("_beauty_specialist_profile_reviews", StringComparison.Ordinal));
            await migrator.MigrateAsync(Previous);
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            Guid specA, specB, apptA, clientA;
            await using (var conn = new NpgsqlConnection(ownerCs))
            {
                await conn.OpenAsync();
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");
                specA = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenantA}', 'A', true) RETURNING id");
                var loc = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'L', 'UTC', true) RETURNING id");
                var svc = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenantA}', 'S', 60, true) RETURNING id");
                clientA = await Id(conn, $"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{tenantA}', 'C') RETURNING id");
                apptA = await Id(conn, $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at,
                        duration_minutes, status, source, price_original, price_final)
                    VALUES ('{tenantA}', '{loc}', '{specA}', '{svc}', '{clientA}', now() + interval '3 day', 60, 'confirmed', 'admin', 100, 100) RETURNING id");
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantB}', false)");
                specB = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenantB}', 'B', true) RETURNING id");
            }

            await migrator.MigrateAsync(target);

            await using (var c = new NpgsqlConnection(ownerCs))
            {
                await c.OpenAsync();
                Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM pg_class WHERE oid = 'beauty_specialist_reviews'::regclass AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM pg_policies WHERE tablename = 'beauty_specialist_reviews' AND policyname = 'tenant_isolation'"));

                await Exec(c, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");
                string Review(Guid sp, Guid t, int rating, string appt = "NULL", string client = "NULL") =>
                    $"INSERT INTO beauty_specialist_reviews (tenant_id, specialist_id, appointment_id, client_id, author_name, rating) VALUES ('{t}', '{sp}', {appt}, {client}, 'Olena K.', {rating})";
                await Exec(c, Review(specA, tenantA, 5, $"'{apptA}'", $"'{clientA}'"));

                // один відгук на запис
                await Assert.ThrowsAsync<PostgresException>(() => Exec(c, Review(specA, tenantA, 4, $"'{apptA}'")));
                // кілька відгуків без запису дозволені
                await Exec(c, Review(specA, tenantA, 3));
                await Exec(c, Review(specA, tenantA, 3));
                // рейтинг 1..5
                await Assert.ThrowsAsync<PostgresException>(() => Exec(c, Review(specA, tenantA, 0)));
                await Assert.ThrowsAsync<PostgresException>(() => Exec(c, Review(specA, tenantA, 6)));
                // WITH CHECK: чужий tenant_id і складений FK на чужого майстра
                await Assert.ThrowsAsync<PostgresException>(() => Exec(c, Review(specA, tenantB, 5)));
                await Assert.ThrowsAsync<PostgresException>(() => Exec(c, Review(specB, tenantA, 5)));
                // CHECK профілю майстра
                await Assert.ThrowsAsync<PostgresException>(() => Exec(c, $"UPDATE beauty_specialists SET practice_start_year = 1949 WHERE id = '{specA}'"));
                await Assert.ThrowsAsync<PostgresException>(() => Exec(c, $"UPDATE beauty_specialists SET bio = repeat('x', 501) WHERE id = '{specA}'"));
                await Exec(c, $"UPDATE beauty_specialists SET practice_start_year = 2010, bio = 'ok' WHERE id = '{specA}'");

                // ізоляція: tenant B нічого не бачить, без app.tenant_id (fail-closed) теж
                Assert.Equal(3L, await Scalar(c, "SELECT count(*) FROM beauty_specialist_reviews"));
                await Exec(c, $"SELECT set_config('app.tenant_id', '{tenantB}', false)");
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM beauty_specialist_reviews"));
                await Exec(c, "SELECT set_config('app.tenant_id', '', false)");
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM beauty_specialist_reviews"));
            }

            // Down: таблиця й колонки зникають, майстри лишаються; повторний Up працює
            await migrator.MigrateAsync(Previous);
            await using (var c = new NpgsqlConnection(ownerCs))
            {
                await c.OpenAsync();
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'beauty_specialist_reviews'"));
                Assert.Equal(0L, await Scalar(c, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_specialists' AND column_name IN ('bio', 'practice_start_year')"));
                await Exec(c, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");
                Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM beauty_specialists"));
            }
            await migrator.MigrateAsync(target);
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
