using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-883 (ADR-025 §11, §32.16-§32.19): міграція beauty_inventory_stage2a. Адитивність (дані етапу 1 цілі, нові колонки з
/// дефолтами), FORCE RLS на 3 нових таблицях, CHECK-и (shortage, is_shortage ↔ batch_id, kind ↔ appointment_id, алерти), UNIQUE
/// (appointment, scope наборів NULLS NOT DISTINCT, dedupe_key), RESTRICT/CASCADE, append-only журнал із новими колонками (тригери
/// hardening не зачеплені), GRANT воркера, Down із відмовою при is_shortage/appointment_id і Up -> Down -> Up. Без реального годинника.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class InventoryStage2aMigrationTests
{
    private const string Previous = "20261118201522_beauty_inventory_hardening";

    private static readonly string[] NewTables = ["beauty_service_material_sets", "beauty_service_materials", "beauty_staff_alerts"];

    [SkippableFact]
    public async Task up_adds_shortage_norms_and_alerts_constraints_hold_journal_stays_append_only_and_down_refuses_with_history()
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
        var db = $"beauty_inv2a_{suffix}";
        var owner = $"beauty_inv2a_owner_{suffix}";
        var app = $"beauty_inv2a_app_{suffix}";
        var worker = $"beauty_inv2a_wrk_{suffix}";
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

            // ---- дані етапу 1 "до міграції": два tenant, заклади, послуга, запис, товар, партія, рівень, рух, інвентаризація ----
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Ctx(conn, t);
                await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'i2-{t:N}')");
            }
            async Task<Guid> User(Guid t)
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{t}', 'u@{t:N}.test', 'O', 'x', 'owner') RETURNING id");
            }
            var userA = await User(tenantA);
            var userB = await User(tenantB);
            await Ctx(conn, tenantB);
            var locOfB = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantB}', 'B', 'Europe/Kyiv', true) RETURNING id");
            var svcOfB = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenantB}', 'SvcB', 60, true) RETURNING id");
            await Ctx(conn, tenantA);
            var locA = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'Центр', 'Europe/Kyiv', true) RETURNING id");
            var locB = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'Схід', 'Europe/Kyiv', true) RETURNING id");
            var spec = await Id(conn, $"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{tenantA}', 'S', true) RETURNING id");
            var svc = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenantA}', 'Svc', 60, true) RETURNING id");
            var svc2 = await Id(conn, $"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{tenantA}', 'Svc2', 30, true) RETURNING id");
            var client = await Id(conn, $"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{tenantA}', 'C') RETURNING id");
            async Task<Guid> Appt(int day) => await Id(conn, $@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final)
                VALUES ('{tenantA}', '{locA}', '{spec}', '{svc}', '{client}', TIMESTAMPTZ '2026-11-{20 + day} 09:00+00', 60, 'pending', 'admin', 200, 150) RETURNING id");
            var appt1 = await Appt(0);
            var appt2 = await Appt(1);
            var product = await Id(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{tenantA}', 'P', 'ml', 'salon_use', '{userA}') RETURNING id");
            var product2 = await Id(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{tenantA}', 'P2', 'pcs', 'both', '{userA}') RETURNING id");
            var receipt = await Id(conn, $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, created_by_user_id) VALUES ('{tenantA}', 'receipt', '{locA}', '{userA}') RETURNING id");
            var batch = await Id(conn, $@"INSERT INTO beauty_stock_batches (tenant_id, location_id, product_id, operation_id, received_at, expires_on, initial_quantity, quantity)
                VALUES ('{tenantA}', '{locA}', '{product}', '{receipt}', TIMESTAMPTZ '2026-11-18 09:00+00', DATE '2027-01-31', 10, 10) RETURNING id");
            await Exec(conn, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id, quantity) VALUES ('{tenantA}', '{locA}', '{product}', 10)");
            await Exec(conn, $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, type, quantity) VALUES ('{tenantA}', '{receipt}', '{locA}', '{product}', '{batch}', 'receipt', 10)");
            var stocktake = await Id(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, created_by_user_id) VALUES ('{tenantA}', '{locA}', '{userA}') RETURNING id");
            await Exec(conn, $"INSERT INTO beauty_stocktake_lines (tenant_id, stocktake_id, product_id, counted_quantity, expected_quantity) VALUES ('{tenantA}', '{stocktake}', '{product}', 9, 10)");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_staff_alerts')"));

            await migrator.MigrateAsync();

            // ---- адитивність: дані етапу 1 цілі, нові колонки мають безпечні значення за замовчуванням ----
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_stock_levels WHERE quantity = 10 AND shortage_quantity = 0"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_stock_movements WHERE NOT is_shortage AND batch_id IS NOT NULL"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_stock_operations WHERE appointment_id IS NULL"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_stocktake_lines WHERE expected_shortage IS NULL AND expected_quantity = 10"));
            foreach (var table in NewTables)
            {
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation' AND qual IS NOT NULL AND with_check IS NOT NULL"));
            }
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_stock_movements') AND relforcerowsecurity"));
            // тригери append-only етапу 1 на місці (DDL міграції їх не зачепив)
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal"));

            // ---- рівень: дефіцит >= 0, quantity >= 0 лишається; обидва можуть бути > 0 ----
            await Rejects(conn, $"UPDATE beauty_stock_levels SET shortage_quantity = -0.001", "ck_beauty_stock_levels_shortage");
            await Rejects(conn, $"UPDATE beauty_stock_levels SET quantity = -0.001", "ck_beauty_stock_levels_quantity");
            Assert.Equal(1, await ExecCount(conn, "UPDATE beauty_stock_levels SET shortage_quantity = 2.5"));

            // ---- рухи: (batch_id IS NULL) = is_shortage; знак/тип рядків нестачі ----
            string Mv(string type, string qty, string? batchId, bool shortage, string? op = null) =>
                $@"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, is_shortage, type, quantity)
                   VALUES ('{tenantA}', '{op ?? receipt.ToString()}', '{locA}', '{product}', {(batchId is null ? "NULL" : $"'{batchId}'")}, {(shortage ? "true" : "false")}, '{type}', {qty})";
            await Rejects(conn, Mv("service_consumption", "-1", null, false), "ck_beauty_stock_movements_batch"); // етап 1 дозволяв; тепер без партії лише is_shortage
            await Rejects(conn, Mv("write_off", "-1", null, false), "ck_beauty_stock_movements_batch");
            await Rejects(conn, Mv("receipt", "1", batch.ToString(), true), "ck_beauty_stock_movements_batch"); // is_shortage з партією
            await Rejects(conn, Mv("write_off", "-1", null, true), "ck_beauty_stock_movements_shortage");
            await Rejects(conn, Mv("transfer_out", "-1", null, true), "ck_beauty_stock_movements_shortage");
            await Rejects(conn, Mv("sale", "-1", null, true), "ck_beauty_stock_movements_shortage");
            await Rejects(conn, Mv("stocktake_adjust", "-1", null, true), "ck_beauty_stock_movements_shortage"); // погашення лише +
            await Rejects(conn, Mv("service_consumption", "1", null, true), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("receipt", "0", null, true), "ck_beauty_stock_movements_quantity_sign");
            await Exec(conn, Mv("receipt", "1", batch.ToString(), false)); // партійні рухи без змін
            await Exec(conn, Mv("stocktake_adjust", "-1", batch.ToString(), false));

            // ---- операції автосписання: kind ↔ appointment_id, унікальність за записом, RESTRICT ----
            string Op(string kind, string? appointment) =>
                $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, appointment_id) VALUES ('{tenantA}', '{kind}', '{locA}', {(appointment is null ? "NULL" : $"'{appointment}'")})";
            await Rejects(conn, Op("service_consumption", null), "ck_beauty_stock_operations_appointment");
            await Rejects(conn, Op("receipt", appt1.ToString()), "ck_beauty_stock_operations_appointment");
            await Rejects(conn, Op("sale", appt1.ToString()), "ck_beauty_stock_operations_appointment");
            var consumption = await Id(conn, Op("service_consumption", appt1.ToString()) + " RETURNING id"); // created_by_user_id NULL = система
            await Rejects(conn, Op("service_consumption", appt1.ToString()), "ux_beauty_stock_operations_appointment");
            Assert.Equal(1, await ExecCount(conn, Op("service_consumption", appt2.ToString())));
            await Exec(conn, Op("receipt", null)); // NULL appointment_id не конфліктують
            await Exec(conn, Op("receipt", null));
            var fk = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_appointments WHERE id = '{appt1}'"));
            Assert.Equal("23503", fk.SqlState); // RESTRICT: запис зі списанням не видаляється

            // рухи нестачі автосписання й погашення: без партії, з is_shortage
            await Exec(conn, Mv("service_consumption", "-2.5", null, true, consumption.ToString()));
            await Exec(conn, Mv("receipt", "1", null, true));
            await Exec(conn, Mv("transfer_in", "1", null, true));
            await Exec(conn, Mv("stocktake_adjust", "2.5", null, true));
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM beauty_stock_movements WHERE is_shortage AND batch_id IS NULL"));

            // ---- рядки інвентаризації ----
            await Rejects(conn, $"UPDATE beauty_stocktake_lines SET expected_shortage = -1", "ck_beauty_stocktake_lines_expected_shortage");
            Assert.Equal(1, await ExecCount(conn, "UPDATE beauty_stocktake_lines SET expected_shortage = 2.5"));

            // ---- набори норм: NULLS NOT DISTINCT, RESTRICT/CASCADE ----
            string Set(Guid service, string? location, Guid? by = null) =>
                $"INSERT INTO beauty_service_material_sets (tenant_id, service_id, location_id, updated_by_user_id) VALUES ('{tenantA}', '{service}', {(location is null ? "NULL" : $"'{location}'")}, '{by ?? userA}')";
            var network = await Id(conn, Set(svc, null) + " RETURNING id");
            await Rejects(conn, Set(svc, null), "ux_beauty_service_material_sets_scope"); // друга мережева: NULL не різні
            var overrideA = await Id(conn, Set(svc, locA.ToString()) + " RETURNING id");
            await Rejects(conn, Set(svc, locA.ToString()), "ux_beauty_service_material_sets_scope");
            await Exec(conn, Set(svc, locB.ToString()));
            await Exec(conn, Set(svc2, null)); // інша послуга
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Set(svcOfB, null))); // послуга іншого tenant (композитний FK)
            Assert.Equal("23503", ex.SqlState);

            string Line(Guid set, Guid prod, string qty) =>
                $"INSERT INTO beauty_service_materials (tenant_id, set_id, product_id, quantity) VALUES ('{tenantA}', '{set}', '{prod}', {qty})";
            await Rejects(conn, Line(network, product, "0"), "ck_beauty_service_materials_quantity");
            await Rejects(conn, Line(network, product, "-1"), "ck_beauty_service_materials_quantity");
            await Rejects(conn, Line(network, product, "100000.001"), "ck_beauty_service_materials_quantity");
            await Exec(conn, Line(network, product, "100000"));
            await Rejects(conn, Line(network, product, "5"), "ux_beauty_service_materials_pair");
            await Exec(conn, Line(network, product2, "0.001"));
            await Exec(conn, Line(overrideA, product, "12.5"));
            var restrictProduct = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_products WHERE id = '{product2}'"));
            Assert.Equal("23503", restrictProduct.SqlState);
            var restrictService = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_services WHERE id = '{svc}'"));
            Assert.Equal("23503", restrictService.SqlState);
            Assert.Equal(3L, await Scalar(conn, "SELECT count(*) FROM beauty_service_materials"));
            // CASCADE: набір видаляє рядки; override закладу видаляється разом із закладом
            Assert.Equal(1, await ExecCount(conn, $"DELETE FROM beauty_service_material_sets WHERE id = '{network}'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_service_materials"));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_service_material_sets WHERE location_id = '{locB}'"));
            var locC = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'Тимчасовий', 'Europe/Kyiv', true) RETURNING id");
            await Exec(conn, Set(svc2, locC.ToString()));
            Assert.Equal(1, await ExecCount(conn, $"DELETE FROM beauty_locations WHERE id = '{locC}'"));
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM beauty_service_material_sets WHERE location_id = '{locC}'"));

            // ---- сповіщення ----
            string Alert(string kind, string? batchId = null, string? operationId = null, string key = "k", string status = "open", string extra = "",
                string extraValue = "") =>
                $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, batch_id, operation_id, quantity, dedupe_key, status{extra})
                   VALUES ('{tenantA}', '{kind}', '{locA}', '{product}', {(batchId is null ? "NULL" : $"'{batchId}'")}, {(operationId is null ? "NULL" : $"'{operationId}'")}, 3, '{key}', '{status}'{extraValue})";
            await Rejects(conn, Alert("stock_low", key: "a1"), "ck_beauty_staff_alerts_kind");
            await Rejects(conn, Alert("low_stock", key: "a2", status: "closed"), "ck_beauty_staff_alerts_status");
            await Rejects(conn, Alert("expiring", key: "a3"), "ck_beauty_staff_alerts_batch");
            await Rejects(conn, Alert("low_stock", batchId: batch.ToString(), key: "a4"), "ck_beauty_staff_alerts_batch");
            await Rejects(conn, Alert("shortage", key: "a5"), "ck_beauty_staff_alerts_operation");
            await Rejects(conn, Alert("low_stock", operationId: consumption.ToString(), key: "a6"), "ck_beauty_staff_alerts_operation");
            await Rejects(conn, Alert("expiring", batchId: batch.ToString(), key: "a7", extra: ", days_bucket", extraValue: ", 366"), "ck_beauty_staff_alerts_days_bucket");
            await Rejects(conn, Alert("expiring", batchId: batch.ToString(), key: "a8", extra: ", days_bucket", extraValue: ", -1"), "ck_beauty_staff_alerts_days_bucket");
            await Rejects(conn, Alert("low_stock", key: "a9", extra: ", days_bucket", extraValue: ", 7"), "ck_beauty_staff_alerts_days_bucket");
            await Rejects(conn, Alert("expiring", batchId: batch.ToString(), key: "a10", extra: ", threshold", extraValue: ", 5"), "ck_beauty_staff_alerts_threshold");
            await Rejects(conn, Alert("low_stock", key: "a11", extra: ", threshold", extraValue: ", -1"), "ck_beauty_staff_alerts_threshold");
            await Rejects(conn, Alert("low_stock", key: "a12", status: "resolved"), "ck_beauty_staff_alerts_resolved");
            await Rejects(conn, Alert("low_stock", key: "a13", extra: ", resolved_at", extraValue: ", now()"), "ck_beauty_staff_alerts_resolved");
            await Rejects(conn, Alert("low_stock", key: "a14", status: "read"), "ck_beauty_staff_alerts_read");
            await Exec(conn, Alert("low_stock", key: "low_stock:l:p:2026-11-19", extra: ", threshold", extraValue: ", 5"));
            await Exec(conn, Alert("expiring", batchId: batch.ToString(), key: $"expiring:{batch}:7", extra: ", days_bucket, expires_on", extraValue: ", 7, DATE '2027-01-31'"));
            await Exec(conn, Alert("expiring", batchId: batch.ToString(), key: $"expiring:{batch}:0", extra: ", days_bucket", extraValue: ", 0"));
            await Exec(conn, Alert("shortage", operationId: consumption.ToString(), key: $"shortage:{consumption}:{product}"));
            await Exec(conn, Alert("low_stock", key: "r1", status: "read", extra: ", read_at, read_by_user_id", extraValue: $", now(), '{userA}'"));
            await Exec(conn, Alert("low_stock", key: "r2", status: "resolved", extra: ", resolved_at", extraValue: ", now()"));
            await Rejects(conn, Alert("low_stock", key: "low_stock:l:p:2026-11-19"), "ux_beauty_staff_alerts_dedupe");
            Assert.Equal(1, await ExecCount(conn, "UPDATE beauty_staff_alerts SET status = 'read', read_at = now() WHERE dedupe_key = 'low_stock:l:p:2026-11-19'"));
            var restrictBatch = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_stock_batches WHERE id = '{batch}'"));
            Assert.Equal("23503", restrictBatch.SqlState);

            // ---- крос-tenant: B не бачить норм/сповіщень A (RLS) і не може посилатися на A (композитні FK) ----
            await Ctx(conn, tenantB);
            foreach (var table in NewTables) Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}"));
            var crossOp = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn,
                $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, appointment_id) VALUES ('{tenantB}', 'service_consumption', '{locOfB}', '{appt1}')"));
            Assert.Equal("23503", crossOp.SqlState);
            var crossAlert = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn,
                $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, batch_id, quantity, dedupe_key, days_bucket)
                   VALUES ('{tenantB}', 'expiring', '{locOfB}', '{product}', '{batch}', 1, 'x', 7)"));
            Assert.Equal("23503", crossAlert.SqlState);
            await Exec(conn, $@"INSERT INTO beauty_service_material_sets (tenant_id, service_id, updated_by_user_id) VALUES ('{tenantB}', '{svcOfB}', '{userB}')");
            await Ctx(conn, tenantA);
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM beauty_service_material_sets WHERE service_id = '{svcOfB}'"));
            await Exec(conn, "SELECT set_config('app.tenant_id', '', false)");
            foreach (var table in NewTables) Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}")); // fail-closed без контексту
            await Ctx(conn, tenantA);

            // ---- append-only з новими колонками: тригери діють і на власника ----
            foreach (var sql in new[]
                     {
                         "UPDATE beauty_stock_movements SET is_shortage = false", "UPDATE beauty_stock_operations SET appointment_id = NULL",
                         "DELETE FROM beauty_stock_movements WHERE is_shortage", "DELETE FROM beauty_stock_operations WHERE appointment_id IS NOT NULL",
                     })
            {
                var blocked = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, sql));
                Assert.Equal("42501", blocked.SqlState);
                Assert.Contains("append-only", blocked.MessageText);
            }

            // ---- GRANT app-ролі (як у setup-db.ps1/grants.sql) і воркера (§32.20) ----
            await using (var admin = new NpgsqlConnection(Cs(owner)))
            {
                await admin.OpenAsync();
                await Exec(admin, $"GRANT USAGE ON SCHEMA public TO {app}, {worker}");
                await Exec(admin, $"GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO {app}");
                await Exec(admin, $"REVOKE UPDATE, DELETE ON beauty_stock_movements FROM {app}");
                await Exec(admin, $"REVOKE UPDATE, DELETE ON beauty_stock_operations FROM {app}");
                await Exec(admin, $"GRANT SELECT ON beauty_stock_levels, beauty_stock_batches, beauty_products, beauty_product_location_settings, beauty_locations TO {worker}");
                await Exec(admin, $"GRANT SELECT, INSERT, UPDATE ON beauty_staff_alerts TO {worker}");
            }
            await using (var appConn = new NpgsqlConnection(Cs(app)))
            {
                await appConn.OpenAsync();
                await Ctx(appConn, tenantA);
                Assert.Equal(1, await ExecCount(appConn, Mv("service_consumption", "-1", null, true, consumption.ToString()))); // INSERT дозволено
                Assert.Equal(1, await ExecCount(appConn, Op("receipt", null)));
                foreach (var table in new[] { "beauty_stock_movements", "beauty_stock_operations" })
                {
                    var upd = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, $"UPDATE {table} SET " + (table == "beauty_stock_movements" ? "quantity = quantity" : "note = note")));
                    Assert.Equal("42501", upd.SqlState);
                    var del = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, $"DELETE FROM {table}"));
                    Assert.Equal("42501", del.SqlState);
                }
                Assert.True(await ExecCount(appConn, "UPDATE beauty_staff_alerts SET updated_at = now()") > 0); // алерти змінювані
                Assert.True(await ExecCount(appConn, "UPDATE beauty_service_material_sets SET updated_at = now()") > 0);
            }
            await using (var wrkConn = new NpgsqlConnection(Cs(worker)))
            {
                await wrkConn.OpenAsync();
                await Ctx(wrkConn, tenantA);
                Assert.True(await Scalar(wrkConn, "SELECT count(*) FROM beauty_stock_levels") > 0);
                Assert.True(await Scalar(wrkConn, "SELECT count(*) FROM beauty_stock_batches") > 0);
                Assert.True(await Scalar(wrkConn, "SELECT count(*) FROM beauty_staff_alerts") > 0);
                await Exec(wrkConn, Alert("low_stock", key: "worker-low"));
                Assert.Equal(1, await ExecCount(wrkConn, "UPDATE beauty_staff_alerts SET status = 'resolved', resolved_at = now() WHERE dedupe_key = 'worker-low'"));
                foreach (var sql in new[]
                         {
                             "DELETE FROM beauty_staff_alerts", "SELECT count(*) FROM beauty_service_materials", "SELECT count(*) FROM beauty_service_material_sets",
                             "SELECT count(*) FROM beauty_stock_movements", "UPDATE beauty_stock_levels SET quantity = 0",
                         })
                {
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(wrkConn, sql));
                    Assert.Equal("42501", denied.SqlState);
                }
            }

            // ---- Down з історією (is_shortage / appointment_id) відмовляє; нічого не втрачається ----
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("inventory stage2a history exists", FullMessage(refused));
            foreach (var table in NewTables)
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relforcerowsecurity"));
            foreach (var table in new[] { "beauty_stock_movements", "beauty_stock_operations" }) // відкат Down повернув FORCE
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_attribute WHERE attrelid = 'beauty_stock_movements'::regclass AND attname = 'is_shortage' AND NOT attisdropped"));

            // свідоме очищення (runbook): власник вимикає тригери, видаляє лише історію 2a (спершу сповіщення), вмикає тригери
            await Ctx(conn, tenantA);
            await Exec(conn, "DELETE FROM beauty_staff_alerts");
            await Exec(conn, "ALTER TABLE beauty_stock_movements DISABLE TRIGGER USER");
            await Exec(conn, "ALTER TABLE beauty_stock_operations DISABLE TRIGGER USER");
            await Exec(conn, "DELETE FROM beauty_stock_movements WHERE is_shortage");
            // лише операція з appointment_id (без рухів нестачі) теж блокує Down
            var stillRefused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("inventory stage2a history exists", FullMessage(stillRefused));
            await Exec(conn, "DELETE FROM beauty_stock_operations WHERE appointment_id IS NOT NULL");
            await Exec(conn, "ALTER TABLE beauty_stock_movements ENABLE TRIGGER USER");
            await Exec(conn, "ALTER TABLE beauty_stock_operations ENABLE TRIGGER USER");

            await migrator.MigrateAsync(Previous);
            foreach (var table in NewTables)
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}')"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_attribute WHERE attrelid = 'beauty_stock_movements'::regclass AND attname = 'is_shortage' AND NOT attisdropped"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_attribute WHERE attrelid = 'beauty_stock_levels'::regclass AND attname = 'shortage_quantity' AND NOT attisdropped"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_stock_levels WHERE quantity = 10"));
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal"));
            // старий CHECK повернено: рух service_consumption без партії знову дозволений, write_off без партії - ні
            await Exec(conn, $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, type, quantity) VALUES ('{tenantA}', '{receipt}', '{locA}', '{product}', 'service_consumption', -1)");
            await Rejects(conn, $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, type, quantity) VALUES ('{tenantA}', '{receipt}', '{locA}', '{product}', 'write_off', -1)", "ck_beauty_stock_movements_batch");

            // ---- повторний Up: старий рух service_consumption без партії блокує новий CHECK, поки не очищений (свідомо) ----
            await Exec(conn, "ALTER TABLE beauty_stock_movements DISABLE TRIGGER USER");
            await Exec(conn, "DELETE FROM beauty_stock_movements WHERE batch_id IS NULL");
            await Exec(conn, "ALTER TABLE beauty_stock_movements ENABLE TRIGGER USER");
            await migrator.MigrateAsync(); // Up -> Down -> Up
            foreach (var table in NewTables)
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM beauty_stock_levels WHERE quantity = 10 AND shortage_quantity = 0"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_staff_alerts"));
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal"));
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
