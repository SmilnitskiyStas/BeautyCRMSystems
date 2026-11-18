using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-876 (ADR-025, §32.3): міграція beauty_inventory. Перевіряє адитивність (до/після), FORCE RLS і політики на 9 таблицях,
/// CHECK-обмеження, композитні FK, унікальності (штрихкод, ідемпотентність, одна відкрита інвентаризація), append-only рухів
/// і операцій для app-ролі (REVOKE UPDATE/DELETE, як у setup-db.ps1/grants.sql), Down із відмовою при непорожньому журналі й
/// повторний Up. Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка). Без реального годинника.
/// </summary>
public sealed class InventoryMigrationTests
{
    private const string Previous = "20261115120000_auth_user_permissions";

    private static readonly string[] Tables =
    [
        "beauty_suppliers", "beauty_products", "beauty_product_location_settings", "beauty_stock_levels", "beauty_stock_operations",
        "beauty_stock_batches", "beauty_stock_movements", "beauty_stocktakes", "beauty_stocktake_lines",
    ];

    [SkippableFact]
    public async Task up_creates_rls_forced_tables_constraints_hold_journal_is_append_only_and_down_refuses_with_history()
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
        var db = $"beauty_inv_{suffix}";
        var owner = $"beauty_inv_owner_{suffix}";
        var app = $"beauty_inv_app_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = new NpgsqlConnection(adminCs))
            {
                await admin.OpenAsync();
                await Exec(admin, $"CREATE ROLE {owner} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
                await Exec(admin, $"CREATE ROLE {app} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
                // ADR-022 / ADR-023: ops заздалегідь створює ролі каталогу й purge (CREATEROLE у мігратора немає)
                await Exec(admin, "DO $$ BEGIN CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; EXCEPTION WHEN duplicate_object THEN NULL; END $$");
                await Exec(admin, $"GRANT beautycrm_catalog TO {owner} WITH INHERIT FALSE, SET TRUE");
                await Exec(admin, "DO $$ BEGIN CREATE ROLE beautycrm_audit_purge NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; EXCEPTION WHEN duplicate_object THEN NULL; END $$");
                await Exec(admin, $"GRANT beautycrm_audit_purge TO {owner} WITH INHERIT FALSE, SET TRUE");
                await Exec(admin, $"CREATE DATABASE {db} OWNER {owner}");
            }
            var ownerCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = db, Username = owner, Password = password }.ConnectionString;
            var appCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = db, Username = app, Password = password }.ConnectionString;

            var options = new DbContextOptionsBuilder<BeautyDbContext>();
            options.UseBeautyNpgsql(ownerCs);
            await using var ctx = new BeautyDbContext(options.Options, new TenantContext());
            var migrator = ((IInfrastructure<IServiceProvider>)ctx.Database).Instance.GetService(typeof(IMigrator)) as IMigrator
                           ?? throw new InvalidOperationException("IMigrator not available");
            await migrator.MigrateAsync(Previous);
            await using var conn = new NpgsqlConnection(ownerCs);
            await conn.OpenAsync();

            // ---- дані "до міграції": tenant, власник, два заклади ----
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{t}', false)");
                await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'i-{t:N}')");
            }
            async Task<Guid> User(Guid t)
            {
                await Exec(conn, $"SELECT set_config('app.tenant_id', '{t}', false)");
                return await Id(conn, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{t}', 'u@{t:N}.test', 'O', 'x', 'owner') RETURNING id");
            }
            var userA = await User(tenantA);
            var userB = await User(tenantB);
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");
            var locA = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'Центр', 'Europe/Kyiv', true) RETURNING id");
            var locB = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantA}', 'Схід', 'Europe/Kyiv', true) RETURNING id");
            Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.beauty_products')"));

            await migrator.MigrateAsync();

            // ---- адитивність: дані до міграції на місці; FORCE RLS і політики на всіх 9 таблицях ----
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_locations"));
            foreach (var table in Tables)
            {
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation' AND qual IS NOT NULL AND with_check IS NOT NULL"));
            }

            // ---- suppliers ----
            await Rejects(conn, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{tenantA}', '   ')", "ck_beauty_suppliers_name");
            var supplier = await Id(conn, $"INSERT INTO beauty_suppliers (tenant_id, name, is_active) VALUES ('{tenantA}', 'Acme', true) RETURNING id");
            await Rejects(conn, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{tenantA}', 'aCME')", "ux_beauty_suppliers_name");

            // ---- products: enum-CHECK, штрихкод, ціна, мінімум, унікальність ----
            string Prod(string unit = "pcs", string purpose = "sale", string barcode = "NULL", string price = "NULL", string min = "NULL") =>
                $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, barcode, purchase_price, min_stock, updated_by_user_id) VALUES ('{tenantA}', 'P', '{unit}', '{purpose}', {barcode}, {price}, {min}, '{userA}')";
            await Rejects(conn, Prod(unit: "box"), "ck_beauty_products_unit");
            await Rejects(conn, Prod(purpose: "gift"), "ck_beauty_products_purpose");
            await Rejects(conn, Prod(barcode: "'bad code'"), "ck_beauty_products_barcode");
            await Rejects(conn, Prod(price: "1000001"), "ck_beauty_products_purchase_price");
            await Rejects(conn, Prod(price: "-0.01"), "ck_beauty_products_purchase_price");
            await Rejects(conn, Prod(min: "-1"), "ck_beauty_products_min_stock");
            var product = await Id(conn, Prod(unit: "ml", purpose: "both", barcode: "'A-1.b_2'", price: "99.99", min: "10.5") + " RETURNING id");
            await Rejects(conn, Prod(barcode: "'A-1.b_2'"), "ux_beauty_products_barcode");
            await Exec(conn, Prod()); // NULL штрихкоди не конфліктують
            await Exec(conn, Prod());
            foreach (var u in new[] { "pcs", "ml", "g", "l", "kg", "pack" }) await Exec(conn, Prod(unit: u));
            var productB = await Id(conn, Prod() + " RETURNING id");

            // ---- налаштування товару в закладі ----
            await Rejects(conn, $"INSERT INTO beauty_product_location_settings (tenant_id, product_id, location_id, min_stock, updated_by_user_id) VALUES ('{tenantA}', '{product}', '{locA}', -1, '{userA}')", "ck_beauty_product_location_settings_min_stock");
            await Exec(conn, $"INSERT INTO beauty_product_location_settings (tenant_id, product_id, location_id, min_stock, updated_by_user_id) VALUES ('{tenantA}', '{product}', '{locA}', 0, '{userA}')");
            await Rejects(conn, $"INSERT INTO beauty_product_location_settings (tenant_id, product_id, location_id, updated_by_user_id) VALUES ('{tenantA}', '{product}', '{locA}', '{userA}')", "ux_beauty_product_location_settings_pair");

            // ---- stock_levels ----
            await Rejects(conn, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id, quantity) VALUES ('{tenantA}', '{locA}', '{product}', -0.001)", "ck_beauty_stock_levels_quantity");
            await Exec(conn, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id) VALUES ('{tenantA}', '{locA}', '{product}')");
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_stock_levels WHERE quantity = 0"));
            await Rejects(conn, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id) VALUES ('{tenantA}', '{locA}', '{product}')", "ux_beauty_stock_levels_pair");

            // ---- operations ----
            string Op(string kind = "receipt", string to = "NULL", string supplierId = "NULL", string reason = "NULL", string stocktake = "NULL", string keyHash = "NULL", string reqHash = "NULL", string loc = "") =>
                $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, to_location_id, supplier_id, reason, stocktake_id, idempotency_key_hash, idempotency_request_hash, created_by_user_id) VALUES ('{tenantA}', '{kind}', '{(loc.Length > 0 ? loc : locA.ToString())}', {to}, {supplierId}, {reason}, {stocktake}, {keyHash}, {reqHash}, '{userA}')";
            await Rejects(conn, Op(kind: "refund"), "ck_beauty_stock_operations_kind");
            await Rejects(conn, Op(kind: "transfer"), "ck_beauty_stock_operations_transfer_target");
            await Rejects(conn, Op(to: $"'{locB}'"), "ck_beauty_stock_operations_transfer_target");
            await Rejects(conn, Op(kind: "transfer", to: $"'{locA}'"), "ck_beauty_stock_operations_transfer_distinct");
            await Rejects(conn, Op(kind: "write_off", reason: "'lost'", supplierId: $"'{supplier}'"), "ck_beauty_stock_operations_receipt_fields");
            await Rejects(conn, Op(kind: "write_off"), "ck_beauty_stock_operations_reason_kind");
            await Rejects(conn, Op(reason: "'lost'"), "ck_beauty_stock_operations_reason_kind");
            await Rejects(conn, Op(kind: "write_off", reason: "'theft'"), "ck_beauty_stock_operations_reason");
            await Rejects(conn, Op(kind: "stocktake"), "ck_beauty_stock_operations_stocktake");
            await Rejects(conn, Op(keyHash: "'" + new string('a', 64) + "'"), "ck_beauty_stock_operations_idempotency");
            foreach (var reason in new[] { "expired", "damaged", "lost", "internal_use", "correction", "other" })
                await Exec(conn, Op(kind: "write_off", reason: $"'{reason}'"));
            foreach (var kind in new[] { "service_consumption", "sale" }) // етапи 2-3: CHECK надмножина
                await Exec(conn, Op(kind: kind));
            var receipt = await Id(conn, Op(supplierId: $"'{supplier}'", keyHash: $"'{new string('a', 64)}'", reqHash: $"'{new string('b', 64)}'") + " RETURNING id");
            var transfer = await Id(conn, Op(kind: "transfer", to: $"'{locB}'") + " RETURNING id");
            await Rejects(conn, Op(keyHash: $"'{new string('a', 64)}'", reqHash: $"'{new string('c', 64)}'"), "ux_beauty_stock_operations_idempotency");

            // ---- stocktakes ----
            await Rejects(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, status, created_by_user_id) VALUES ('{tenantA}', '{locA}', 'closed', '{userA}')", "ck_beauty_stocktakes_status");
            await Rejects(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, status, created_by_user_id) VALUES ('{tenantA}', '{locA}', 'posted', '{userA}')", "ck_beauty_stocktakes_posted");
            await Rejects(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, status, created_by_user_id) VALUES ('{tenantA}', '{locA}', 'cancelled', '{userA}')", "ck_beauty_stocktakes_cancelled");
            await Rejects(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, version, created_by_user_id) VALUES ('{tenantA}', '{locA}', 0, '{userA}')", "ck_beauty_stocktakes_version");
            var stocktake = await Id(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, created_by_user_id) VALUES ('{tenantA}', '{locA}', '{userA}') RETURNING id");
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_stocktakes WHERE id = '{stocktake}' AND status = 'draft' AND version = 1"));
            await Rejects(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, created_by_user_id) VALUES ('{tenantA}', '{locA}', '{userA}')", "ux_beauty_stocktakes_open");
            await Exec(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, created_by_user_id) VALUES ('{tenantA}', '{locB}', '{userA}')"); // інший заклад
            await Exec(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, status, cancelled_at, created_by_user_id) VALUES ('{tenantA}', '{locA}', 'cancelled', now(), '{userA}')"); // закриті не блокують
            await Exec(conn, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, status, posted_at, posted_by_user_id, created_by_user_id) VALUES ('{tenantA}', '{locA}', 'posted', now(), '{userA}', '{userA}')");
            var stOp = await Id(conn, Op(kind: "stocktake", stocktake: $"'{stocktake}'") + " RETURNING id");
            await Rejects(conn, Op(kind: "stocktake", stocktake: $"'{stocktake}'"), "ux_beauty_stock_operations_stocktake");

            // ---- stocktake_lines ----
            await Rejects(conn, $"INSERT INTO beauty_stocktake_lines (tenant_id, stocktake_id, product_id, counted_quantity) VALUES ('{tenantA}', '{stocktake}', '{product}', -1)", "ck_beauty_stocktake_lines_counted");
            await Rejects(conn, $"INSERT INTO beauty_stocktake_lines (tenant_id, stocktake_id, product_id, surplus_expires_on) VALUES ('{tenantA}', '{stocktake}', '{product}', DATE '1999-12-31')", "ck_beauty_stocktake_lines_surplus_expires_on");
            await Exec(conn, $"INSERT INTO beauty_stocktake_lines (tenant_id, stocktake_id, product_id, counted_quantity) VALUES ('{tenantA}', '{stocktake}', '{product}', NULL)");
            await Rejects(conn, $"INSERT INTO beauty_stocktake_lines (tenant_id, stocktake_id, product_id) VALUES ('{tenantA}', '{stocktake}', '{product}')", "ux_beauty_stocktake_lines_pair");

            // ---- batches ----
            string Batch(string initial = "10", string qty = "10", string expires = "NULL", string cost = "NULL", string source = "NULL") =>
                $"INSERT INTO beauty_stock_batches (tenant_id, location_id, product_id, operation_id, source_batch_id, received_at, expires_on, initial_quantity, quantity, unit_cost) VALUES ('{tenantA}', '{locA}', '{product}', '{receipt}', {source}, TIMESTAMPTZ '2026-11-18 09:00+00', {expires}, {initial}, {qty}, {cost})";
            await Rejects(conn, Batch(initial: "0", qty: "0"), "ck_beauty_stock_batches_initial_quantity");
            await Rejects(conn, Batch(qty: "10.001"), "ck_beauty_stock_batches_quantity");
            await Rejects(conn, Batch(qty: "-0.001"), "ck_beauty_stock_batches_quantity");
            await Rejects(conn, Batch(expires: "DATE '1999-12-31'"), "ck_beauty_stock_batches_expires_on");
            await Rejects(conn, Batch(expires: "DATE '2101-01-01'"), "ck_beauty_stock_batches_expires_on");
            await Rejects(conn, Batch(cost: "1000000.01"), "ck_beauty_stock_batches_unit_cost");
            var batch = await Id(conn, Batch(initial: "10.125", qty: "10.125", expires: "DATE '2027-01-31'", cost: "5.5") + " RETURNING id");
            await Exec(conn, Batch(qty: "0")); // вичерпана партія допустима
            await Exec(conn, Batch(source: $"'{batch}'"));

            // ---- movements ----
            string Mv(string type, string qty, string? batchId = null, string? operation = null) =>
                $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, type, quantity, created_by_user_id) VALUES ('{tenantA}', '{operation ?? receipt.ToString()}', '{locA}', '{product}', {(batchId is null ? "NULL" : $"'{batchId}'")}, '{type}', {qty}, '{userA}')";
            await Rejects(conn, Mv("refund", "1", batch.ToString()), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("receipt", "-1", batch.ToString()), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("transfer_in", "0", batch.ToString()), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("write_off", "1", batch.ToString()), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("transfer_out", "1", batch.ToString()), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("sale", "1", batch.ToString()), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("stocktake_adjust", "0", batch.ToString()), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv("write_off", "-1"), "ck_beauty_stock_movements_batch");
            await Exec(conn, Mv("service_consumption", "-1")); // непокрита нестача (етап 2): без партії
            await Exec(conn, Mv("receipt", "10.125", batch.ToString()));
            await Exec(conn, Mv("stocktake_adjust", "-0.5", batch.ToString(), stOp.ToString()));
            await Exec(conn, Mv("stocktake_adjust", "0.5", batch.ToString(), stOp.ToString()));
            await Exec(conn, Mv("transfer_out", "-2", batch.ToString(), transfer.ToString()));
            await Exec(conn, Mv("transfer_in", "2", batch.ToString(), transfer.ToString()));
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_stock_movements WHERE type = 'receipt' AND occurred_at IS NOT NULL AND quantity = 10.125"));

            // ---- композитні FK (крос-tenant) ----
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantB}', false)");
            var locOfB = await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{tenantB}', 'B', 'Europe/Kyiv', true) RETURNING id");
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id) VALUES ('{tenantB}', '{locOfB}', '{product}')"));
            Assert.Equal("23503", ex.SqlState);
            var ex2 = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, supplier_id, updated_by_user_id) VALUES ('{tenantB}', 'X', 'pcs', 'sale', '{supplier}', '{userB}')"));
            Assert.Equal("23503", ex2.SqlState);
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_products")); // RLS: B не бачить товарів A
            await Exec(conn, "SELECT set_config('app.tenant_id', '', false)");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_stock_movements")); // fail-closed без контексту
            await Exec(conn, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");

            // ---- hardening: CHECK, узгодженість рух↔партія, append-only тригер (діє й на власника) ----
            await Rejects(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{tenantA}', '   ', 'pcs', 'sale', '{userA}')", "ck_beauty_products_name");
            await Rejects(conn, $"INSERT INTO beauty_stocktake_lines (tenant_id, stocktake_id, product_id, expected_quantity) VALUES ('{tenantA}', '{stocktake}', '{productB}', -1)", "ck_beauty_stocktake_lines_expected");
            const string mvFk = "fk_beauty_stock_movements_beauty_stock_batches_tenant_id_batch";
            await Rejects(conn, $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, type, quantity) VALUES ('{tenantA}', '{receipt}', '{locA}', '{productB}', '{batch}', 'receipt', 1)", mvFk); // інший товар
            await Rejects(conn, $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, type, quantity) VALUES ('{tenantA}', '{receipt}', '{locB}', '{product}', '{batch}', 'receipt', 1)", mvFk); // інший заклад
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname = 'beauty_stock_movements_no_truncate' AND NOT tgisinternal"));
            foreach (var sql in new[]
                     {
                         "UPDATE beauty_stock_movements SET created_by_user_id = NULL", "DELETE FROM beauty_stock_movements", "TRUNCATE beauty_stock_movements",
                         "UPDATE beauty_stock_operations SET created_by_user_id = NULL", "DELETE FROM beauty_stock_operations", "TRUNCATE beauty_stock_operations",
                     })
            {
                var blocked = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, sql));
                if (sql == "TRUNCATE beauty_stock_operations") // таблиця під FK рухів/партій: TRUNCATE відхиляє сам PostgreSQL ще до тригера
                {
                    Assert.Equal("0A000", blocked.SqlState);
                    continue;
                }
                Assert.Equal("42501", blocked.SqlState);
                Assert.Contains("append-only", blocked.MessageText);
            }

            // ---- append-only для app-ролі: ті самі GRANT/REVOKE, що в scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql ----
            await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminCs) { Database = db }.ConnectionString))
            {
                await admin.OpenAsync();
                await Exec(admin, $"GRANT USAGE ON SCHEMA public TO {app}");
                await Exec(admin, $"GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO {app}");
                await Exec(admin, $"REVOKE UPDATE, DELETE ON beauty_stock_movements FROM {app}");
                await Exec(admin, $"REVOKE UPDATE, DELETE ON beauty_stock_operations FROM {app}");
            }
            await using (var appConn = new NpgsqlConnection(appCs))
            {
                await appConn.OpenAsync();
                await Exec(appConn, $"SELECT set_config('app.tenant_id', '{tenantA}', false)");
                Assert.True(await Scalar(appConn, "SELECT count(*) FROM beauty_stock_movements") > 0);
                await Exec(appConn, Mv("receipt", "1", batch.ToString())); // INSERT дозволено
                await Exec(appConn, Op(kind: "write_off", reason: "'other'"));
                foreach (var table in new[] { "beauty_stock_movements", "beauty_stock_operations" })
                {
                    var upd = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, $"UPDATE {table} SET created_by_user_id = NULL"));
                    Assert.Equal("42501", upd.SqlState);
                    var del = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, $"DELETE FROM {table}"));
                    Assert.Equal("42501", del.SqlState);
                }
                // решта таблиць (рівні, партії, чернетки) змінюються як зазвичай
                Assert.Equal(1, await ExecCount(appConn, $"UPDATE beauty_stock_levels SET quantity = 5 WHERE product_id = '{product}'"));
                Assert.Equal(1, await ExecCount(appConn, $"UPDATE beauty_stocktakes SET version = version + 1 WHERE id = '{stocktake}' AND status = 'draft' AND version = 1"));
            }
            // ідемпотентність/CAS: другий UPDATE зі старою версією не зачіпає рядків
            Assert.Equal(0, await ExecCount(conn, $"UPDATE beauty_stocktakes SET version = version + 1 WHERE id = '{stocktake}' AND version = 1"));

            // ---- Down з непорожнім журналом відмовляє, нічого не втрачається ----
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("inventory history exists", FullMessage(refused));
            foreach (var table in Tables)
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relforcerowsecurity"));
            Assert.True(await Scalar(conn, "SELECT count(*) FROM beauty_stock_movements") > 0);

            // ---- журнал порожній (свідомо): Down проходить, таблиці зникають, дані до міграції цілі; повторний Up ----
            // Down hardening виконується першим і знімає тригери (окрема міграція, окремий коміт) - навіть коли Down початкової відмовила.
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal"));
            await Exec(conn, "ALTER TABLE beauty_stock_movements NO FORCE ROW LEVEL SECURITY");
            await Exec(conn, "DELETE FROM beauty_stock_movements");
            await Exec(conn, "ALTER TABLE beauty_stock_movements FORCE ROW LEVEL SECURITY");
            await migrator.MigrateAsync(Previous);
            foreach (var table in Tables)
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}')"));
            Assert.Equal(2L, await Scalar(conn, "SELECT count(*) FROM beauty_locations"));
            await migrator.MigrateAsync(); // Up -> Down -> Up
            foreach (var table in Tables)
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_products"));
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal")); // Up відновив тригери
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
                await Exec(admin, $"DROP ROLE IF EXISTS {app}");
                await Exec(admin, $"DROP ROLE IF EXISTS {owner}");
            }
        }
    }

    private static string FullMessage(Exception e)
    {
        var parts = new List<string>();
        for (var x = e; x is not null; x = x.InnerException!) { parts.Add(x.Message); if (x.InnerException is null) break; }
        return string.Join(" | ", parts);
    }

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
