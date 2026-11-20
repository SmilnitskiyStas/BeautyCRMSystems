using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Міграція beauty_product_orders (ADR-026, контракт §33.15, TASK-901): дві нові таблиці (RLS ENABLE+FORCE, композитні FK, CHECK-и, UNIQUE токена/ключа/номера),
/// beauty_payments.appointment_id NULL + product_order_id + «рівно одне з двох», beauty_stock_operations.product_order_id + UNIQUE (tenant, order, kind),
/// типи sale_return у CHECK операцій і рухів; GRANT/REVOKE із deploy/staging/db/grants.sql; Down відмовляє, якщо є замовлення/рухи sale; Up -> Down -> Up.
/// Без PostgreSQL пропускається (BEAUTY_TEST_REQUIRE_DB=1 - помилка).
/// </summary>
public sealed class ProductOrdersMigrationTests
{
    private const string Previous = "20261120085617_beauty_inventory_product_categories";
    private static readonly string[] NewTables = ["beauty_product_orders", "beauty_product_order_lines"];

    [SkippableFact]
    public async Task up_creates_orders_with_rls_constraints_grants_and_down_refuses_when_data_exists()
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
        var db = $"beauty_ord_{suffix}";
        var owner = $"beauty_ord_owner_{suffix}";
        var app = $"beauty_ord_app_{suffix}";
        var worker = $"beauty_ord_wrk_{suffix}";
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

            // ---- дані "до міграції": два tenant, користувачі, заклади, товари, клієнт, партія ----
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Ctx(conn, t);
                await Exec(conn, $"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'ord-{t:N}')");
            }
            async Task<Guid> User(Guid t, string tag)
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO users (tenant_id, email, full_name, password_hash, role) VALUES ('{t}', '{tag}@{t:N}.test', 'U', 'x', 'owner') RETURNING id");
            }
            async Task<Guid> Loc(Guid t)
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{t}', 'L', 'Europe/Kyiv', true) RETURNING id");
            }
            async Task<Guid> Prod(Guid t, Guid u, string name)
            {
                await Ctx(conn, t);
                return await Id(conn, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, sale_price, updated_by_user_id) VALUES ('{t}', '{name}', 'pcs', 'sale', 50, '{u}') RETURNING id");
            }
            var userA = await User(tenantA, "a1");
            var userA2 = await User(tenantA, "a2");
            var userB = await User(tenantB, "b1");
            var locA = await Loc(tenantA);
            var locB = await Loc(tenantB);
            var prodA1 = await Prod(tenantA, userA, "P1");
            var prodA2 = await Prod(tenantA, userA, "P2");
            var prodB = await Prod(tenantB, userB, "PB");
            await Ctx(conn, tenantA);
            var clientA = await Id(conn, $"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{tenantA}', 'C') RETURNING id");
            var receipt = await Id(conn, $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, created_by_user_id) VALUES ('{tenantA}', 'receipt', '{locA}', '{userA}') RETURNING id");
            var batch = await Id(conn, $"INSERT INTO beauty_stock_batches (tenant_id, location_id, product_id, operation_id, received_at, initial_quantity, quantity, unit_cost) VALUES ('{tenantA}', '{locA}', '{prodA1}', '{receipt}', now(), 10, 10, 20) RETURNING id");
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('beauty_product_orders', 'beauty_product_order_lines')"));
            // до міграції: sale_return і product_order_id у журналі складу неможливі
            await Rejects(conn, $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id) VALUES ('{tenantA}', 'sale_return', '{locA}')", "ck_beauty_stock_operations_kind");

            await migrator.MigrateAsync();

            // ---- структура: RLS ENABLE+FORCE+політика, nullable appointment_id, нові колонки ----
            foreach (var table in NewTables)
            {
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation' AND qual IS NOT NULL AND with_check IS NOT NULL"));
            }
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_payments' AND column_name = 'appointment_id' AND is_nullable = 'YES'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_payments' AND column_name = 'product_order_id' AND is_nullable = 'YES'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_stock_operations' AND column_name = 'product_order_id' AND is_nullable = 'YES'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_product_orders' AND column_name = 'currency' AND data_type = 'character' AND character_maximum_length = 3"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_product_orders' AND column_name = 'status' AND column_default LIKE '%pending_payment%'"));
            foreach (var ck in new[]
                     {
                         "ck_beauty_product_orders_status", "ck_beauty_product_orders_source", "ck_beauty_product_orders_close_reason", "ck_beauty_product_orders_number",
                         "ck_beauty_product_orders_guest_name", "ck_beauty_product_orders_guest_phone", "ck_beauty_product_orders_total", "ck_beauty_product_orders_currency",
                         "ck_beauty_product_orders_payment_attempts", "ck_beauty_product_orders_closed", "ck_beauty_product_orders_expired", "ck_beauty_product_orders_cancelled",
                         "ck_beauty_product_orders_refunded", "ck_beauty_product_orders_refund_amount", "ck_beauty_product_orders_paid", "ck_beauty_product_orders_fulfilled",
                         "ck_beauty_product_orders_payment_started", "ck_beauty_product_orders_refund_started",
                         "ck_beauty_product_order_lines_unit", "ck_beauty_product_order_lines_line_no", "ck_beauty_product_order_lines_quantity",
                         "ck_beauty_product_order_lines_unit_price", "ck_beauty_product_order_lines_line_total",
                         "ck_beauty_payments_subject", "ck_beauty_stock_operations_product_order",
                     })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_constraint WHERE conname = '{ck}' AND contype = 'c'"));
            foreach (var ix in new[]
                     {
                         "ux_beauty_product_orders_number", "ux_beauty_product_orders_token", "ux_beauty_product_orders_idempotency", "ix_beauty_product_orders_created",
                         "ix_beauty_product_orders_location_created", "ix_beauty_product_orders_pending", "ix_beauty_product_orders_expiry", "ix_beauty_product_orders_paid",
                         "ix_beauty_product_orders_phone_created", "ix_beauty_product_orders_client", "ux_beauty_product_order_lines_product", "ux_beauty_product_order_lines_line_no",
                         "ix_beauty_product_order_lines_product", "ix_beauty_payments_product_order", "ux_beauty_stock_operations_product_order",
                     })
                Assert.True(1L == await Scalar(conn, $"SELECT count(*) FROM pg_indexes WHERE indexname = '{ix}'"), ix);
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_beauty_product_orders_pending' AND indexdef LIKE '%pending_payment%'"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_beauty_stock_operations_product_order' AND indexdef LIKE 'CREATE UNIQUE%' AND indexdef LIKE '%product_order_id IS NOT NULL%'"));
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal")); // тригери append-only не зачеплені
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_stock_batches WHERE id = '{batch}' AND quantity = 10")); // дані до міграції на місці
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_product_orders"));
            await Ctx(conn, tenantA);

            // ---- замовлення: INSERT-хелпер і CHECK-и ----
            var seq = 0;
            string Ord(Guid? t = null, Guid? loc = null, int? number = null, string? token = null, string? idem = null, string source = "web", string name = "Ірина",
                string phone = "+380501234567", string total = "100.00", string currency = "UAH", string extraCols = "", string extraVals = "")
            {
                seq++;
                var tid = t ?? tenantA;
                return $"INSERT INTO beauty_product_orders (tenant_id, location_id, order_number, public_token_hash, idempotency_key_hash, idempotency_request_hash, source, guest_name, guest_phone, total_amount, currency, reservation_expires_at{extraCols}) " +
                       $"VALUES ('{tid}', '{loc ?? locA}', {number ?? seq}, '{(token ?? $"t{seq}").PadLeft(64, '0')}', '{(idem ?? $"i{seq}").PadLeft(64, '0')}', '{new string('r', 64)}', '{source}', '{name}', '{phone}', {total}, '{currency}', now() + interval '15 minutes'{extraVals})";
            }
            Task RejectsOrder(string sql, string ck) => Rejects(conn, sql, ck);
            await RejectsOrder(Ord(number: 0), "ck_beauty_product_orders_number");
            await RejectsOrder(Ord(total: "0"), "ck_beauty_product_orders_total");
            await RejectsOrder(Ord(total: "-1"), "ck_beauty_product_orders_total");
            await RejectsOrder(Ord(total: "1000000.01"), "ck_beauty_product_orders_total");
            await RejectsOrder(Ord(currency: "USD"), "ck_beauty_product_orders_currency");
            await RejectsOrder(Ord(source: "sms"), "ck_beauty_product_orders_source");
            await RejectsOrder(Ord(name: " a "), "ck_beauty_product_orders_guest_name");
            foreach (var bad in new[] { "0501234567", "+0501234567", "+38050", "+3805012", "380501234567", "+38050123456a" })
                await RejectsOrder(Ord(phone: bad), "ck_beauty_product_orders_guest_phone");
            await RejectsOrder(Ord(extraCols: ", status", extraVals: ", 'draft'"), "ck_beauty_product_orders_status");
            await RejectsOrder(Ord(extraCols: ", payment_attempts", extraVals: ", 11"), "ck_beauty_product_orders_payment_attempts");
            await RejectsOrder(Ord(extraCols: ", payment_attempts", extraVals: ", -1"), "ck_beauty_product_orders_payment_attempts");
            await RejectsOrder(Ord(extraCols: ", close_reason", extraVals: ", 'other'"), "ck_beauty_product_orders_close_reason");
            var longNote = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Ord(extraCols: ", cancel_note", extraVals: $", '{new string('x', 301)}'")));
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, longNote.SqlState);

            var o1 = await Id(conn, Ord(number: 1, token: "tok1", idem: "key1", total: "1000000.00") + " RETURNING id"); // межа total дозволена
            await Exec(conn, "UPDATE beauty_product_orders SET total_amount = 100.00");
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_product_orders WHERE id = '{o1}' AND status = 'pending_payment' AND currency = 'UAH' AND payment_attempts = 0 AND client_id IS NULL AND payment_id IS NULL"));
            // UNIQUE: номер, токен, ключ ідемпотентності - у межах tenant
            await RejectsOrder(Ord(number: 1, token: "tok2", idem: "key2"), "ux_beauty_product_orders_number");
            await RejectsOrder(Ord(number: 2, token: "tok1", idem: "key2"), "ux_beauty_product_orders_token");
            await RejectsOrder(Ord(number: 2, token: "tok2", idem: "key1"), "ux_beauty_product_orders_idempotency");
            // стан статусу: кожен CHECK порушується окремо
            string Set(string set, Guid? id = null) => $"UPDATE beauty_product_orders SET {set} WHERE id = '{id ?? o1}'";
            await Rejects(conn, Set("status = 'cancelled', close_reason = 'client'"), "ck_beauty_product_orders_closed"); // немає closed_at
            await Rejects(conn, Set("closed_at = now(), close_reason = 'client'"), "ck_beauty_product_orders_closed"); // pending із причиною закриття
            await Rejects(conn, Set("status = 'expired', close_reason = 'client', closed_at = now()"), "ck_beauty_product_orders_expired");
            await Rejects(conn, Set("close_reason = 'expired'"), "ck_beauty_product_orders_expired");
            await Rejects(conn, Set("status = 'cancelled', close_reason = 'stock_unavailable', closed_at = now()"), "ck_beauty_product_orders_cancelled");
            await Rejects(conn, Set("status = 'refunded', close_reason = 'client', closed_at = now(), refund_amount = 100.00"), "ck_beauty_product_orders_refunded");
            await Rejects(conn, Set("status = 'refunded', close_reason = 'staff', closed_at = now(), refund_amount = 100.00"), "ck_beauty_product_orders_refunded"); // немає payment_id
            await Rejects(conn, Set("refund_amount = 1.00"), "ck_beauty_product_orders_refund_amount");
            await Rejects(conn, Set("status = 'paid'"), "ck_beauty_product_orders_paid");
            await Rejects(conn, Set("refund_started_at = now()"), "ck_beauty_product_orders_refund_started");

            // платіж замовлення (спроба): рівно одне з appointment_id / product_order_id
            string Pay(string? orderId, string? apptId = null, string amount = "100.00", Guid? t = null) =>
                $"INSERT INTO beauty_payments (tenant_id, appointment_id, product_order_id, amount, method, status) VALUES ('{t ?? tenantA}', {(apptId is null ? "NULL" : $"'{apptId}'")}, {(orderId is null ? "NULL" : $"'{orderId}'")}, {amount}, 'card', 'pending')";
            await Rejects(conn, Pay(null), "ck_beauty_payments_subject");
            await Rejects(conn, Pay(o1.ToString(), Guid.NewGuid().ToString()), "ck_beauty_payments_subject"); // обидва
            await Rejects(conn, Pay(o1.ToString(), amount: "0"), "ck_beauty_payments_amount_positive");
            var apptOnly = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Pay(null, Guid.NewGuid().ToString()))); // CHECK пройдено - далі лише FK на запис
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, apptOnly.SqlState);
            var pay1 = await Id(conn, Pay(o1.ToString()) + " RETURNING id");
            var pay1b = await Id(conn, Pay(o1.ToString()) + " RETURNING id"); // кілька спроб на замовлення
            Assert.Equal(2L, await Scalar(conn, $"SELECT count(*) FROM beauty_payments WHERE product_order_id = '{o1}' AND appointment_id IS NULL"));
            // життєвий цикл: pending (спроба) -> paid -> fulfilled
            await Exec(conn, Set("payment_attempts = 1, payment_started_at = now()"));
            await Exec(conn, Set($"status = 'paid', payment_started_at = NULL, paid_at = now(), payment_id = '{pay1}', client_id = '{clientA}'"));
            await Rejects(conn, Set("payment_started_at = now()"), "ck_beauty_product_orders_payment_started");
            await Exec(conn, Set("refund_started_at = now()"));
            await Exec(conn, Set("refund_started_at = NULL"));
            await Rejects(conn, Set("status = 'fulfilled', paid_at = now()"), "ck_beauty_product_orders_fulfilled");
            await Exec(conn, Set($"status = 'fulfilled', fulfilled_at = now(), fulfilled_by_user_id = '{userA}'"));
            await Rejects(conn, Set("refund_started_at = now()"), "ck_beauty_product_orders_refund_started");
            // pending -> expired / cancelled / refunded (stock_unavailable і staff)
            var o2 = await Id(conn, Ord(number: 2) + " RETURNING id");
            await Exec(conn, Set("status = 'expired', close_reason = 'expired', closed_at = now()", o2));
            var o3 = await Id(conn, Ord(number: 3) + " RETURNING id");
            await Exec(conn, Set($"status = 'cancelled', close_reason = 'staff', closed_at = now(), cancelled_by_user_id = '{userA2}', cancel_note = 'нотатка'", o3));
            var o4 = await Id(conn, Ord(number: 4) + " RETURNING id");
            var pay4 = await Id(conn, Pay(o4.ToString()) + " RETURNING id");
            await Exec(conn, Set($"status = 'refunded', close_reason = 'stock_unavailable', closed_at = now(), payment_id = '{pay4}', refund_amount = 100.00", o4));
            await Rejects(conn, Set("refund_amount = 99.99", o4), "ck_beauty_product_orders_refunded"); // повернення рівно на суму замовлення
            // FK: платіж, клієнт, користувачі, заклад - у межах tenant; RESTRICT
            var delPay = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_payments WHERE id = '{pay1}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delPay.SqlState);
            var delLoc = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_locations WHERE id = '{locA}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delLoc.SqlState);
            var delClient = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_clients WHERE id = '{clientA}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delClient.SqlState);
            var delUser = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM users WHERE id = '{userA2}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delUser.SqlState);
            var delOrder = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_product_orders WHERE id = '{o1}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delOrder.SqlState); // платіж посилається на замовлення

            // ---- рядки замовлення ----
            string Line(Guid order, Guid product, int lineNo = 1, string qty = "2", string price = "50.00", string? total = null, string unit = "pcs", Guid? t = null) =>
                $"INSERT INTO beauty_product_order_lines (tenant_id, order_id, line_no, product_id, product_name, unit, quantity, unit_price, line_total) VALUES ('{t ?? tenantA}', '{order}', {lineNo}, '{product}', 'Шампунь', '{unit}', {qty}, {price}, {total ?? (decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) * int.Parse(qty)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})";
            var o5 = await Id(conn, Ord(number: 5, total: "150.00") + " RETURNING id");
            await Rejects(conn, Line(o5, prodA1, qty: "0", total: "0"), "ck_beauty_product_order_lines_quantity");
            await Rejects(conn, Line(o5, prodA1, qty: "11"), "ck_beauty_product_order_lines_quantity");
            await Rejects(conn, Line(o5, prodA1, qty: "-1", total: "-50.00"), "ck_beauty_product_order_lines_quantity");
            await Rejects(conn, Line(o5, prodA1, price: "0", total: "0"), "ck_beauty_product_order_lines_unit_price");
            await Rejects(conn, Line(o5, prodA1, price: "1000000.01", qty: "1"), "ck_beauty_product_order_lines_unit_price");
            await Rejects(conn, Line(o5, prodA1, total: "99.99"), "ck_beauty_product_order_lines_line_total");
            await Rejects(conn, Line(o5, prodA1, lineNo: 0), "ck_beauty_product_order_lines_line_no");
            await Rejects(conn, Line(o5, prodA1, lineNo: 21), "ck_beauty_product_order_lines_line_no");
            await Rejects(conn, Line(o5, prodA1, unit: "box"), "ck_beauty_product_order_lines_unit");
            var fractional = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Line(o5, prodA1, qty: "'1.5'", total: "75.00")));
            Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, fractional.SqlState); // quantity - ціле
            await Exec(conn, Line(o5, prodA1, qty: "10", price: "50.00", total: "500.00")); // межа
            await Exec(conn, $"DELETE FROM beauty_product_order_lines WHERE order_id = '{o5}'"); // власник може; app-роль - ні (нижче)
            await Exec(conn, Line(o5, prodA1, lineNo: 1, qty: "1", total: "50.00"));
            await Rejects(conn, Line(o5, prodA1, lineNo: 2, qty: "1", total: "50.00"), "ux_beauty_product_order_lines_product");
            await Rejects(conn, Line(o5, prodA2, lineNo: 1, qty: "1", total: "50.00"), "ux_beauty_product_order_lines_line_no");
            await Exec(conn, Line(o5, prodA2, lineNo: 2, qty: "2", total: "100.00"));
            Assert.Equal(2L, await Scalar(conn, $"SELECT count(*) FROM beauty_product_order_lines WHERE order_id = '{o5}'"));
            var delOrderWithLines = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_product_orders WHERE id = '{o5}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delOrderWithLines.SqlState);
            var delProd = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"DELETE FROM beauty_products WHERE id = '{prodA1}'"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, delProd.SqlState);

            // ---- склад: sale / sale_return ----
            string Op(string kind, string? order, Guid? t = null) =>
                $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, product_order_id, created_by_user_id) VALUES ('{t ?? tenantA}', '{kind}', '{locA}', {(order is null ? "NULL" : $"'{order}'")}, NULL)";
            await Rejects(conn, Op("sale", null), "ck_beauty_stock_operations_product_order");
            await Rejects(conn, Op("sale_return", null), "ck_beauty_stock_operations_product_order");
            await Rejects(conn, Op("receipt", o1.ToString()), "ck_beauty_stock_operations_product_order");
            var saleOp = await Id(conn, Op("sale", o1.ToString()) + " RETURNING id");
            await Rejects(conn, Op("sale", o1.ToString()), "ux_beauty_stock_operations_product_order"); // одна операція продажу на замовлення
            var returnOp = await Id(conn, Op("sale_return", o1.ToString()) + " RETURNING id");
            await Rejects(conn, Op("sale_return", o1.ToString()), "ux_beauty_stock_operations_product_order");
            await Exec(conn, Op("sale", o5.ToString())); // інше замовлення - окрема операція
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{tenantA}' AND product_order_id IS NULL AND kind = 'receipt'"));
            await Exec(conn, Op("receipt", null)); // ужиті раніше види без замовлення працюють
            string Mv(string op, string type, string qty, bool shortage = false, string? batchId = null) =>
                $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, is_shortage, type, quantity) VALUES ('{tenantA}', '{op}', '{locA}', '{prodA1}', {(shortage ? "NULL" : $"'{batchId ?? batch.ToString()}'")}, {(shortage ? "true" : "false")}, '{type}', {qty})";
            await Exec(conn, Mv(saleOp.ToString(), "sale", "-2"));
            await Rejects(conn, Mv(saleOp.ToString(), "sale", "2"), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv(returnOp.ToString(), "sale_return", "-2"), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv(returnOp.ToString(), "sale_return", "0"), "ck_beauty_stock_movements_quantity_sign");
            await Rejects(conn, Mv(returnOp.ToString(), "sale_return", "2", shortage: true), "ck_beauty_stock_movements_shortage"); // sale_return не погашає дефіцит
            await Rejects(conn, Mv(saleOp.ToString(), "sale", "-2", shortage: true), "ck_beauty_stock_movements_shortage");
            await Rejects(conn, $"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, is_shortage, type, quantity) VALUES ('{tenantA}', '{returnOp}', '{locA}', '{prodA1}', NULL, false, 'sale_return', 2)", "ck_beauty_stock_movements_batch"); // batch NULL <=> is_shortage
            var badType = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Mv(returnOp.ToString(), "refund", "2")));
            Assert.Contains(badType.ConstraintName, new[] { "ck_beauty_stock_movements_type", "ck_beauty_stock_movements_quantity_sign" }); // невідомий тип не проходить жодним CHECK
            await Exec(conn, Mv(returnOp.ToString(), "sale_return", "2")); // у ту саму партію
            await Exec(conn, Mv(receipt.ToString(), "receipt", "1")); // наявні типи без змін
            // журнал лишається append-only (тригер)
            var mutate = await Assert.ThrowsAnyAsync<PostgresException>(() => Exec(conn, "UPDATE beauty_stock_movements SET quantity = quantity WHERE type = 'sale'"));
            Assert.NotEqual(PostgresErrorCodes.CheckViolation, mutate.SqlState);

            // ---- RLS і крос-tenant ----
            await Ctx(conn, tenantB);
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_product_orders"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM beauty_product_order_lines"));
            var oB = await Id(conn, Ord(t: tenantB, loc: locB, number: 1, token: "tok1", idem: "key1") + " RETURNING id"); // той самий номер/токен/ключ в іншому tenant - можна
            var withCheck = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Ord(t: tenantA, number: 90)));
            Assert.Equal("42501", withCheck.SqlState); // WITH CHECK: запис у чужий tenant
            var crossLoc = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Ord(t: tenantB, loc: locA, number: 2)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossLoc.SqlState);
            var crossLine = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Line(oB, prodA1, t: tenantB)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossLine.SqlState); // товар іншого tenant
            await Exec(conn, Line(oB, prodB, t: tenantB));
            var crossPayment = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Pay(o1.ToString(), t: tenantB)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossPayment.SqlState);
            var crossOp = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, Op("sale", o2.ToString(), tenantB)));
            Assert.True(crossOp.SqlState is PostgresErrorCodes.ForeignKeyViolation or "42501", crossOp.SqlState);
            Assert.Equal(0, await ExecCount(conn, $"UPDATE beauty_product_orders SET cancel_note = 'x' WHERE id = '{o1}'")); // чужий рядок невидимий
            await Exec(conn, "SELECT set_config('app.tenant_id', '', false)");
            foreach (var table in NewTables) Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}")); // fail-closed без tenant
            await Ctx(conn, tenantA);
            Assert.Equal(5L, await Scalar(conn, "SELECT count(*) FROM beauty_product_orders"));

            // ---- GRANT-и: справжній deploy/staging/db/grants.sql із підміною імен ролей ----
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
                var appOrder = await Id(appConn, Ord(number: 70, token: "app70", idem: "app70") + " RETURNING id");
                Assert.True(await ExecCount(appConn, $"UPDATE beauty_product_orders SET updated_at = now() WHERE id = '{appOrder}'") == 1);
                await Exec(appConn, Line(appOrder, prodA2, qty: "1", total: "50.00"));
                Assert.True(await Scalar(appConn, "SELECT count(*) FROM beauty_product_order_lines") > 0);
                // замовлення не видаляються, рядки append-only
                foreach (var sql in new[]
                         {
                             $"DELETE FROM beauty_product_orders WHERE id = '{appOrder}'", "UPDATE beauty_product_order_lines SET product_name = 'x'",
                             "DELETE FROM beauty_product_order_lines", "TRUNCATE beauty_product_orders",
                         })
                {
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, sql));
                    Assert.Equal("42501", denied.SqlState);
                }
                // платіж замовлення - звичайний DML app-ролі; журнал складу лишається без UPDATE/DELETE
                Assert.Equal(1, await ExecCount(appConn, Pay(appOrder.ToString())));
                Assert.Equal(1, await ExecCount(appConn, Op("sale", appOrder.ToString())));
                var deniedOp = await Assert.ThrowsAsync<PostgresException>(() => Exec(appConn, "DELETE FROM beauty_stock_operations"));
                Assert.Equal("42501", deniedOp.SqlState);
            }
            await using (var wrkConn = new NpgsqlConnection(Cs(worker)))
            {
                await wrkConn.OpenAsync();
                await Ctx(wrkConn, tenantA);
                // колонковий SELECT: лише поля для TTL і підрахунку зависих оплат
                await Exec(wrkConn, "SELECT id, tenant_id, status, reservation_expires_at, payment_started_at FROM beauty_product_orders");
                foreach (var sql in new[]
                         {
                             "SELECT * FROM beauty_product_orders", "SELECT guest_phone FROM beauty_product_orders", "SELECT guest_name FROM beauty_product_orders",
                             "SELECT total_amount FROM beauty_product_orders", "SELECT public_token_hash FROM beauty_product_orders", "SELECT closed_at FROM beauty_product_orders",
                             "SELECT count(*) FROM beauty_product_order_lines", "SELECT count(*) FROM beauty_payments",
                             "UPDATE beauty_product_orders SET total_amount = 1", "UPDATE beauty_product_orders SET payment_started_at = NULL",
                             "UPDATE beauty_product_orders SET guest_phone = '+380501111111'", "UPDATE beauty_product_orders SET payment_id = NULL",
                             "DELETE FROM beauty_product_orders", Ord(number: 91, token: "w91", idem: "w91"),
                             "UPDATE beauty_product_order_lines SET quantity = 1", "DELETE FROM beauty_product_order_lines",
                         })
                {
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(wrkConn, sql));
                    Assert.Equal("42501", denied.SqlState);
                }
                // косметичний TTL: pending_payment -> expired (status, closed_at, close_reason, updated_at; WHERE лише по granted-колонках)
                var ttl = await Id(conn, Ord(number: 80, token: "ttl80", idem: "ttl80") + " RETURNING id");
                await Exec(conn, $"UPDATE beauty_product_orders SET reservation_expires_at = now() - interval '1 hour' WHERE id = '{ttl}'");
                Assert.Equal(1, await ExecCount(wrkConn,
                    "UPDATE beauty_product_orders SET status = 'expired', closed_at = now(), close_reason = 'expired', updated_at = now() " +
                    $"WHERE id = '{ttl}' AND status = 'pending_payment' AND payment_started_at IS NULL AND reservation_expires_at <= now()"));
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_product_orders WHERE id = '{ttl}' AND status = 'expired' AND close_reason = 'expired' AND closed_at IS NOT NULL"));
            }

            // ---- Down із даними відмовляє; нічого не втрачається ----
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("product orders exist", FullMessage(refused));
            foreach (var table in NewTables)
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            foreach (var table in new[] { "beauty_payments", "beauty_stock_operations", "beauty_stock_movements" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity")); // відкат повернув FORCE
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_payments' AND column_name = 'product_order_id'"));
            Assert.True(await Scalar(conn, "SELECT count(*) FROM beauty_product_orders") > 0);

            // свідоме очищення (runbook): журнал sale/sale_return (append-only тригери вимикаються), платежі, рядки й замовлення - по tenant
            await Exec(conn, "ALTER TABLE beauty_stock_movements DISABLE TRIGGER USER");
            await Exec(conn, "ALTER TABLE beauty_stock_operations DISABLE TRIGGER USER");
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Ctx(conn, t);
                await Exec(conn, "DELETE FROM beauty_stock_movements WHERE type IN ('sale', 'sale_return')");
                await Exec(conn, "DELETE FROM beauty_stock_operations WHERE kind IN ('sale', 'sale_return')");
            }
            await Ctx(conn, tenantA);
            // лише платежі/замовлення лишаються: Down усе одно відмовляє
            var refusedByOrders = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("product orders exist", FullMessage(refusedByOrders));
            foreach (var t in new[] { tenantA, tenantB })
            {
                await Ctx(conn, t);
                await Exec(conn, "UPDATE beauty_product_orders SET status = 'cancelled', close_reason = 'staff', closed_at = now(), payment_id = NULL, paid_at = NULL, fulfilled_at = NULL, fulfilled_by_user_id = NULL, refund_amount = NULL, payment_started_at = NULL, refund_started_at = NULL WHERE status IN ('paid', 'fulfilled', 'refunded')");
                await Exec(conn, "DELETE FROM beauty_payments WHERE product_order_id IS NOT NULL");
                await Exec(conn, "DELETE FROM beauty_product_order_lines");
                await Exec(conn, "DELETE FROM beauty_product_orders");
            }
            await Exec(conn, "ALTER TABLE beauty_stock_movements ENABLE TRIGGER USER");
            await Exec(conn, "ALTER TABLE beauty_stock_operations ENABLE TRIGGER USER");
            await Ctx(conn, tenantA);

            await migrator.MigrateAsync(Previous); // порожньо: Down дозволений
            foreach (var table in NewTables) Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}')"));
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_payments' AND column_name = 'appointment_id' AND is_nullable = 'NO'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_payments' AND column_name = 'product_order_id'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_stock_operations' AND column_name = 'product_order_id'"));
            Assert.Equal(0L, await Scalar(conn, "SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_beauty_payments_subject', 'ck_beauty_stock_operations_product_order')"));
            Assert.Equal(4L, await Scalar(conn, "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'beauty_stock_%' AND NOT tgisinternal"));
            foreach (var table in new[] { "beauty_payments", "beauty_stock_operations", "beauty_stock_movements" })
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            await Rejects(conn, $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id) VALUES ('{tenantA}', 'sale_return', '{locA}')", "ck_beauty_stock_operations_kind"); // CHECK повернувся
            Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM beauty_stock_batches WHERE id = '{batch}' AND quantity = 10")); // склад цілий

            await migrator.MigrateAsync(); // Up -> Down -> Up
            foreach (var table in NewTables)
            {
                Assert.Equal(1L, await Scalar(conn, $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM {table}"));
            }
            Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_payments' AND column_name = 'appointment_id' AND is_nullable = 'YES'"));
            await Exec(conn, Op("sale_return", (await Id(conn, Ord(number: 1, token: "again", idem: "again") + " RETURNING id")).ToString()));
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
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
