using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Замовлення товарів клієнтами (ADR-026, контракт §33.15, TASK-901; human gate G1 пройдено). Адитивна, без backfill:
    /// beauty_product_orders (RLS ENABLE+FORCE, 6 статусів, CHECK узгодженості статусу з мітками), beauty_product_order_lines (append-only через GRANT),
    /// beauty_payments.appointment_id NULL + product_order_id + CHECK «рівно одне з двох», beauty_stock_operations.product_order_id + UNIQUE (tenant, order, kind),
    /// CHECK типів операцій/рухів += sale_return (рух sale_return &gt; 0). GRANT/REVOKE - у scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql.
    /// Down: ВІДМОВЛЯЄ (RAISE EXCEPTION 'product orders exist'), якщо є замовлення, платіж із product_order_id або операція/рух sale | sale_return; інакше відкат.
    /// </summary>
    public partial class beauty_product_orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_operations_kind",
                table: "beauty_stock_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_movements_quantity_sign",
                table: "beauty_stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_movements_type",
                table: "beauty_stock_movements");

            migrationBuilder.AddColumn<Guid>(
                name: "product_order_id",
                table: "beauty_stock_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "appointment_id",
                table: "beauty_payments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "product_order_id",
                table: "beauty_payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_product_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<int>(type: "integer", nullable: false),
                    public_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "pending_payment"),
                    source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    guest_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    guest_phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    total_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false, defaultValue: "UAH"),
                    reservation_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payment_attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    payment_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refund_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fulfilled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    refund_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_product_orders", x => x.id);
                    table.UniqueConstraint("ak_product_orders_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_product_orders_cancelled", "status <> 'cancelled' OR close_reason IN ('client', 'staff')");
                    table.CheckConstraint("ck_beauty_product_orders_close_reason", "close_reason IN ('client', 'staff', 'expired', 'stock_unavailable')");
                    table.CheckConstraint("ck_beauty_product_orders_closed", "(status IN ('cancelled', 'expired', 'refunded')) = (closed_at IS NOT NULL AND close_reason IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_product_orders_currency", "currency = 'UAH'");
                    table.CheckConstraint("ck_beauty_product_orders_expired", "(status = 'expired') = (close_reason IS NOT DISTINCT FROM 'expired')");
                    table.CheckConstraint("ck_beauty_product_orders_fulfilled", "(status = 'fulfilled') = (fulfilled_at IS NOT NULL AND fulfilled_by_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_product_orders_guest_name", "char_length(btrim(guest_name)) BETWEEN 2 AND 100");
                    table.CheckConstraint("ck_beauty_product_orders_guest_phone", "guest_phone ~ '^\\+[1-9][0-9]{7,14}$'");
                    table.CheckConstraint("ck_beauty_product_orders_number", "order_number > 0");
                    table.CheckConstraint("ck_beauty_product_orders_paid", "status NOT IN ('paid', 'fulfilled') OR (paid_at IS NOT NULL AND payment_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_product_orders_payment_attempts", "payment_attempts BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_beauty_product_orders_payment_started", "payment_started_at IS NULL OR status = 'pending_payment'");
                    table.CheckConstraint("ck_beauty_product_orders_refund_amount", "refund_amount IS NULL OR status = 'refunded'");
                    table.CheckConstraint("ck_beauty_product_orders_refund_started", "refund_started_at IS NULL OR status = 'paid'");
                    table.CheckConstraint("ck_beauty_product_orders_refunded", "status <> 'refunded' OR (close_reason IN ('staff', 'stock_unavailable') AND payment_id IS NOT NULL AND refund_amount = total_amount)");
                    table.CheckConstraint("ck_beauty_product_orders_source", "source IN ('web', 'ios', 'android')");
                    table.CheckConstraint("ck_beauty_product_orders_status", "status IN ('pending_payment', 'paid', 'fulfilled', 'cancelled', 'expired', 'refunded')");
                    table.CheckConstraint("ck_beauty_product_orders_total", "total_amount > 0 AND total_amount <= 1000000");
                    table.ForeignKey(
                        name: "fk_beauty_product_orders_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_product_orders_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_product_orders_beauty_payments_tenant_id_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalTable: "beauty_payments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_product_orders_users_tenant_id_cancelled_by_user_id",
                        columns: x => new { x.tenant_id, x.cancelled_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_product_orders_users_tenant_id_fulfilled_by_user_id",
                        columns: x => new { x.tenant_id, x.fulfilled_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_product_order_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<short>(type: "smallint", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_product_order_lines", x => x.id);
                    table.UniqueConstraint("ak_beauty_product_order_lines_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_product_order_lines_line_no", "line_no BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_beauty_product_order_lines_line_total", "line_total = unit_price * quantity");
                    table.CheckConstraint("ck_beauty_product_order_lines_quantity", "quantity BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_beauty_product_order_lines_unit", "unit IN ('pcs', 'ml', 'g', 'l', 'kg', 'pack')");
                    table.CheckConstraint("ck_beauty_product_order_lines_unit_price", "unit_price > 0 AND unit_price <= 1000000");
                    table.ForeignKey(
                        name: "fk_beauty_product_order_lines_beauty_product_orders_tenant_id_",
                        columns: x => new { x.tenant_id, x.order_id },
                        principalTable: "beauty_product_orders",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_product_order_lines_beauty_products_tenant_id_produc",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_stock_operations_product_order",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "product_order_id", "kind" },
                unique: true,
                filter: "product_order_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_operations_kind",
                table: "beauty_stock_operations",
                sql: "kind IN ('receipt', 'write_off', 'transfer', 'stocktake', 'service_consumption', 'sale', 'sale_return')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_operations_product_order",
                table: "beauty_stock_operations",
                sql: "(kind IN ('sale', 'sale_return')) = (product_order_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_movements_quantity_sign",
                table: "beauty_stock_movements",
                sql: "(type IN ('receipt', 'transfer_in') AND quantity > 0) OR (type IN ('write_off', 'transfer_out', 'service_consumption', 'sale') AND quantity < 0) OR (type = 'sale_return' AND quantity > 0) OR (type = 'stocktake_adjust' AND quantity <> 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_movements_type",
                table: "beauty_stock_movements",
                sql: "type IN ('receipt', 'write_off', 'transfer_out', 'transfer_in', 'stocktake_adjust', 'service_consumption', 'sale', 'sale_return')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_payments_product_order",
                table: "beauty_payments",
                columns: new[] { "tenant_id", "product_order_id" },
                filter: "product_order_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_payments_subject",
                table: "beauty_payments",
                sql: "num_nonnulls(appointment_id, product_order_id) = 1");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_order_lines_product",
                table: "beauty_product_order_lines",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_product_order_lines_line_no",
                table: "beauty_product_order_lines",
                columns: new[] { "tenant_id", "order_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_beauty_product_order_lines_product",
                table: "beauty_product_order_lines",
                columns: new[] { "tenant_id", "order_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_cancelled_by",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "cancelled_by_user_id" },
                filter: "cancelled_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_client",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "client_id" },
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_created",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_expiry",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "reservation_expires_at" },
                filter: "status = 'pending_payment'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_fulfilled_by",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "fulfilled_by_user_id" },
                filter: "fulfilled_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_location_created",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "location_id", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_paid",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "location_id", "created_at" },
                filter: "status = 'paid'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_payment",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "payment_id" },
                filter: "payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_pending",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "location_id" },
                filter: "status = 'pending_payment'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_orders_phone_created",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "guest_phone", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_product_orders_idempotency",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "idempotency_key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_beauty_product_orders_number",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_beauty_product_orders_token",
                table: "beauty_product_orders",
                columns: new[] { "tenant_id", "public_token_hash" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_payments_product_orders_tenant_id_product_order_id",
                table: "beauty_payments",
                columns: new[] { "tenant_id", "product_order_id" },
                principalTable: "beauty_product_orders",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_stock_operations_product_orders_tenant_id_product_or",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "product_order_id" },
                principalTable: "beauty_product_orders",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // RLS (ENABLE + FORCE, fail-closed без app.tenant_id, ADR-002); нових політик, окрім tenant_isolation, немає
            foreach (var table in new[] { "beauty_product_orders", "beauty_product_order_lines" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // GRANT/REVOKE - не в міграції (scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql): app-роль без DELETE замовлень і без UPDATE/DELETE рядків;
            // воркер - лише колонковий SELECT/UPDATE замовлень.

            migrationBuilder.Sql(@"
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_product_orders', 'beauty_product_order_lines'] LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class WHERE oid = to_regclass('public.' || t) AND relrowsecurity AND relforcerowsecurity
        ) THEN
            RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
        END IF;
    END LOOP;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Відкат знищує замовлення й зачіпає журнал складу: за наявних даних відмовляє (вся міграція відкочується разом з RAISE).
            // FORCE RLS приховав би рядки від власника без app.tenant_id: на час перевірки знімається й для змінюваних наявних таблиць повертається.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    ALTER TABLE beauty_product_orders NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_payments NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_stock_operations NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_stock_movements NO FORCE ROW LEVEL SECURITY;
    IF EXISTS (SELECT 1 FROM beauty_product_orders)
       OR EXISTS (SELECT 1 FROM beauty_payments WHERE product_order_id IS NOT NULL)
       OR EXISTS (SELECT 1 FROM beauty_stock_operations WHERE kind IN ('sale', 'sale_return'))
       OR EXISTS (SELECT 1 FROM beauty_stock_movements WHERE type IN ('sale', 'sale_return')) THEN
        RAISE EXCEPTION 'product orders exist';
    END IF;
    ALTER TABLE beauty_payments FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_stock_operations FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_stock_movements FORCE ROW LEVEL SECURITY;
END
$$;");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_payments_product_orders_tenant_id_product_order_id",
                table: "beauty_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_stock_operations_product_orders_tenant_id_product_or",
                table: "beauty_stock_operations");

            migrationBuilder.DropTable(
                name: "beauty_product_order_lines");

            migrationBuilder.DropTable(
                name: "beauty_product_orders");

            migrationBuilder.DropIndex(
                name: "ux_beauty_stock_operations_product_order",
                table: "beauty_stock_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_operations_kind",
                table: "beauty_stock_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_operations_product_order",
                table: "beauty_stock_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_movements_quantity_sign",
                table: "beauty_stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_movements_type",
                table: "beauty_stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_beauty_payments_product_order",
                table: "beauty_payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_payments_subject",
                table: "beauty_payments");

            migrationBuilder.DropColumn(
                name: "product_order_id",
                table: "beauty_stock_operations");

            migrationBuilder.DropColumn(
                name: "product_order_id",
                table: "beauty_payments");

            migrationBuilder.AlterColumn<Guid>(
                name: "appointment_id",
                table: "beauty_payments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_operations_kind",
                table: "beauty_stock_operations",
                sql: "kind IN ('receipt', 'write_off', 'transfer', 'stocktake', 'service_consumption', 'sale')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_movements_quantity_sign",
                table: "beauty_stock_movements",
                sql: "(type IN ('receipt', 'transfer_in') AND quantity > 0) OR (type IN ('write_off', 'transfer_out', 'service_consumption', 'sale') AND quantity < 0) OR (type = 'stocktake_adjust' AND quantity <> 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_movements_type",
                table: "beauty_stock_movements",
                sql: "type IN ('receipt', 'write_off', 'transfer_out', 'transfer_in', 'stocktake_adjust', 'service_consumption', 'sale')");
        }
    }
}
