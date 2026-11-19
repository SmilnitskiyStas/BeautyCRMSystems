using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// ADR-025 / контракт §32.3, TASK-876. Склад, етап 1: 9 таблиць (постачальники, товари, налаштування товару в закладі, рівні,
    /// заголовки операцій, партії, рухи, інвентаризації та їх рядки). Адитивна, без backfill даних (права inventory.* - TASK-877).
    /// Згенерована `migrations add`; ручні частини: вираз-індекс ux_beauty_suppliers_name (tenant_id, lower(name)), RLS ENABLE + FORCE +
    /// tenant_isolation (USING + WITH CHECK, ADR-002) на всіх 9 таблицях, перевірочний DO-блок, Down з відмовою при непорожньому журналі.
    /// Журнал рухів і заголовки операцій append-only для app-ролі лише через GRANT/REVOKE (не в міграції: ролі залежать від середовища):
    /// scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql. Роль воркера: нічого в етапі 1.
    /// Down ВІДМОВЛЯЄ (RAISE EXCEPTION 'inventory history exists'), якщо beauty_stock_movements непорожня: втрата журналу лише
    /// свідомо (human gate, runbook відкату); інакше видаляє таблиці у зворотному порядку залежностей.
    /// </summary>
    public partial class beauty_inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "beauty_stocktakes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "draft"),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_stocktakes", x => x.id);
                    table.UniqueConstraint("ak_stocktakes_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_stocktakes_cancelled", "(status = 'cancelled') = (cancelled_at IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_stocktakes_posted", "(status = 'posted') = (posted_at IS NOT NULL AND posted_by_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_stocktakes_status", "status IN ('draft', 'posted', 'cancelled')");
                    table.CheckConstraint("ck_beauty_stocktakes_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_beauty_stocktakes_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stocktakes_users_tenant_id_cancelled_by_user_id",
                        columns: x => new { x.tenant_id, x.cancelled_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stocktakes_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stocktakes_users_tenant_id_posted_by_user_id",
                        columns: x => new { x.tenant_id, x.posted_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_suppliers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_suppliers", x => x.id);
                    table.UniqueConstraint("ak_beauty_suppliers_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_suppliers_name", "char_length(btrim(name)) BETWEEN 1 AND 200");
                });

            migrationBuilder.CreateTable(
                name: "beauty_products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purchase_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    min_stock = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: true),
                    track_expiry = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_products", x => x.id);
                    table.UniqueConstraint("ak_beauty_products_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_products_barcode", "barcode IS NULL OR barcode ~ '^[0-9A-Za-z._-]{1,64}$'");
                    table.CheckConstraint("ck_beauty_products_min_stock", "min_stock IS NULL OR min_stock >= 0");
                    table.CheckConstraint("ck_beauty_products_purchase_price", "purchase_price IS NULL OR purchase_price BETWEEN 0 AND 1000000");
                    table.CheckConstraint("ck_beauty_products_purpose", "purpose IN ('sale', 'salon_use', 'both')");
                    table.CheckConstraint("ck_beauty_products_unit", "unit IN ('pcs', 'ml', 'g', 'l', 'kg', 'pack')");
                    table.ForeignKey(
                        name: "fk_beauty_products_beauty_suppliers_tenant_id_supplier_id",
                        columns: x => new { x.tenant_id, x.supplier_id },
                        principalTable: "beauty_suppliers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_products_users_tenant_id_updated_by_user_id",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_stock_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    received_on = table.Column<DateOnly>(type: "date", nullable: true),
                    reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    stocktake_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    idempotency_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    idempotency_request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_stock_operations", x => x.id);
                    table.UniqueConstraint("ak_beauty_stock_operations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_stock_operations_idempotency", "(idempotency_key_hash IS NULL) = (idempotency_request_hash IS NULL)");
                    table.CheckConstraint("ck_beauty_stock_operations_kind", "kind IN ('receipt', 'write_off', 'transfer', 'stocktake', 'service_consumption', 'sale')");
                    table.CheckConstraint("ck_beauty_stock_operations_reason", "reason IS NULL OR reason IN ('expired', 'damaged', 'lost', 'internal_use', 'correction', 'other')");
                    table.CheckConstraint("ck_beauty_stock_operations_reason_kind", "(kind = 'write_off') = (reason IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_stock_operations_receipt_fields", "kind = 'receipt' OR (supplier_id IS NULL AND document_number IS NULL AND received_on IS NULL)");
                    table.CheckConstraint("ck_beauty_stock_operations_stocktake", "(kind = 'stocktake') = (stocktake_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_stock_operations_transfer_distinct", "to_location_id IS NULL OR to_location_id <> location_id");
                    table.CheckConstraint("ck_beauty_stock_operations_transfer_target", "(kind = 'transfer') = (to_location_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_beauty_stock_operations_beauty_locations_tenant_id_location",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_operations_beauty_locations_tenant_id_to_locat",
                        columns: x => new { x.tenant_id, x.to_location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_operations_beauty_suppliers_tenant_id_supplier",
                        columns: x => new { x.tenant_id, x.supplier_id },
                        principalTable: "beauty_suppliers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_operations_stocktakes_tenant_id_stocktake_id",
                        columns: x => new { x.tenant_id, x.stocktake_id },
                        principalTable: "beauty_stocktakes",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_operations_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_product_location_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    min_stock = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_product_location_settings", x => x.id);
                    table.UniqueConstraint("ak_beauty_product_location_settings_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_product_location_settings_min_stock", "min_stock IS NULL OR min_stock >= 0");
                    table.ForeignKey(
                        name: "fk_beauty_product_location_settings_beauty_locations_tenant_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_product_location_settings_beauty_products_tenant_id_",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_product_location_settings_users_tenant_id_updated_by",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_stock_levels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false, defaultValue: 0m),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_stock_levels", x => x.id);
                    table.UniqueConstraint("ak_beauty_stock_levels_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_stock_levels_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "fk_beauty_stock_levels_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_levels_beauty_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_stocktake_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    stocktake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counted_quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: true),
                    expected_quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: true),
                    surplus_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_stocktake_lines", x => x.id);
                    table.UniqueConstraint("ak_beauty_stocktake_lines_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_stocktake_lines_counted", "counted_quantity IS NULL OR counted_quantity >= 0");
                    table.CheckConstraint("ck_beauty_stocktake_lines_surplus_expires_on", "surplus_expires_on IS NULL OR surplus_expires_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31'");
                    table.ForeignKey(
                        name: "fk_beauty_stocktake_lines_beauty_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stocktake_lines_beauty_stocktakes_tenant_id_stocktak",
                        columns: x => new { x.tenant_id, x.stocktake_id },
                        principalTable: "beauty_stocktakes",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_stocktake_lines_users_tenant_id_updated_by_user_id",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_stock_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    lot_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    initial_quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_stock_batches", x => x.id);
                    table.UniqueConstraint("ak_beauty_stock_batches_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_stock_batches_expires_on", "expires_on IS NULL OR expires_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31'");
                    table.CheckConstraint("ck_beauty_stock_batches_initial_quantity", "initial_quantity > 0");
                    table.CheckConstraint("ck_beauty_stock_batches_quantity", "quantity >= 0 AND quantity <= initial_quantity");
                    table.CheckConstraint("ck_beauty_stock_batches_unit_cost", "unit_cost IS NULL OR unit_cost BETWEEN 0 AND 1000000");
                    table.ForeignKey(
                        name: "fk_beauty_stock_batches_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_batches_beauty_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_batches_beauty_stock_batches_tenant_id_source_",
                        columns: x => new { x.tenant_id, x.source_batch_id },
                        principalTable: "beauty_stock_batches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_batches_beauty_stock_operations_tenant_id_oper",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalTable: "beauty_stock_operations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_batches_beauty_suppliers_tenant_id_supplier_id",
                        columns: x => new { x.tenant_id, x.supplier_id },
                        principalTable: "beauty_suppliers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_stock_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_stock_movements", x => x.id);
                    table.UniqueConstraint("ak_beauty_stock_movements_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_stock_movements_batch", "batch_id IS NOT NULL OR type = 'service_consumption'");
                    table.CheckConstraint("ck_beauty_stock_movements_quantity_sign", "(type IN ('receipt', 'transfer_in') AND quantity > 0) OR (type IN ('write_off', 'transfer_out', 'service_consumption', 'sale') AND quantity < 0) OR (type = 'stocktake_adjust' AND quantity <> 0)");
                    table.CheckConstraint("ck_beauty_stock_movements_type", "type IN ('receipt', 'write_off', 'transfer_out', 'transfer_in', 'stocktake_adjust', 'service_consumption', 'sale')");
                    table.ForeignKey(
                        name: "fk_beauty_stock_movements_beauty_locations_tenant_id_location_",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_movements_beauty_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_movements_beauty_stock_batches_tenant_id_batch",
                        columns: x => new { x.tenant_id, x.batch_id },
                        principalTable: "beauty_stock_batches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_movements_beauty_stock_operations_tenant_id_op",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalTable: "beauty_stock_operations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_stock_movements_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_location_settings_tenant_id_location_id",
                table: "beauty_product_location_settings",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_location_settings_tenant_id_updated_by_user_",
                table: "beauty_product_location_settings",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_product_location_settings_pair",
                table: "beauty_product_location_settings",
                columns: new[] { "tenant_id", "product_id", "location_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_products_active_name",
                table: "beauty_products",
                columns: new[] { "tenant_id", "is_active", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_products_supplier",
                table: "beauty_products",
                columns: new[] { "tenant_id", "supplier_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_products_tenant_id_updated_by_user_id",
                table: "beauty_products",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_products_barcode",
                table: "beauty_products",
                columns: new[] { "tenant_id", "barcode" },
                unique: true,
                filter: "barcode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_batches_expiry",
                table: "beauty_stock_batches",
                columns: new[] { "tenant_id", "location_id", "expires_on" },
                filter: "quantity > 0 AND expires_on IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_batches_fefo",
                table: "beauty_stock_batches",
                columns: new[] { "tenant_id", "location_id", "product_id", "expires_on", "received_at", "id" },
                filter: "quantity > 0");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_batches_tenant_id_operation_id",
                table: "beauty_stock_batches",
                columns: new[] { "tenant_id", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_batches_tenant_id_product_id",
                table: "beauty_stock_batches",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_batches_tenant_id_source_batch_id",
                table: "beauty_stock_batches",
                columns: new[] { "tenant_id", "source_batch_id" },
                filter: "source_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_batches_tenant_id_supplier_id",
                table: "beauty_stock_batches",
                columns: new[] { "tenant_id", "supplier_id" },
                filter: "supplier_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_levels_tenant_id_product_id",
                table: "beauty_stock_levels",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_stock_levels_pair",
                table: "beauty_stock_levels",
                columns: new[] { "tenant_id", "location_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_movements_batch",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_movements_location_occurred",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "location_id", "occurred_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_movements_operation",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_movements_product_occurred",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "product_id", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_movements_tenant_id_created_by_user_id",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "created_by_user_id" },
                filter: "created_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_operations_location_created",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "location_id", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_operations_tenant_id_created_by_user_id",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "created_by_user_id" },
                filter: "created_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_operations_tenant_id_supplier_id",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "supplier_id" },
                filter: "supplier_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_operations_to_location_created",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "to_location_id", "created_at" },
                descending: new[] { false, false, true },
                filter: "to_location_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_stock_operations_idempotency",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "idempotency_key_hash" },
                unique: true,
                filter: "idempotency_key_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_stock_operations_stocktake",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "stocktake_id" },
                unique: true,
                filter: "stocktake_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stocktake_lines_tenant_id_product_id",
                table: "beauty_stocktake_lines",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stocktake_lines_tenant_id_updated_by_user_id",
                table: "beauty_stocktake_lines",
                columns: new[] { "tenant_id", "updated_by_user_id" },
                filter: "updated_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_stocktake_lines_pair",
                table: "beauty_stocktake_lines",
                columns: new[] { "tenant_id", "stocktake_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stocktakes_location_created",
                table: "beauty_stocktakes",
                columns: new[] { "tenant_id", "location_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stocktakes_tenant_id_cancelled_by_user_id",
                table: "beauty_stocktakes",
                columns: new[] { "tenant_id", "cancelled_by_user_id" },
                filter: "cancelled_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stocktakes_tenant_id_created_by_user_id",
                table: "beauty_stocktakes",
                columns: new[] { "tenant_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stocktakes_tenant_id_posted_by_user_id",
                table: "beauty_stocktakes",
                columns: new[] { "tenant_id", "posted_by_user_id" },
                filter: "posted_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_stocktakes_open",
                table: "beauty_stocktakes",
                columns: new[] { "tenant_id", "location_id" },
                unique: true,
                filter: "status = 'draft'");

            // Унікальна назва постачальника в tenant без урахування регістру (вираз-індекс, не виражається в моделі EF)
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_beauty_suppliers_name ON beauty_suppliers (tenant_id, lower(name));");

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на всіх 9 таблицях ----
            foreach (var table in InventoryTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // GRANT (не в міграції): app-роль - SELECT/INSERT/UPDATE/DELETE (ALL TABLES), але beauty_stock_movements і
            // beauty_stock_operations append-only (REVOKE UPDATE, DELETE) у scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql.

            // Гарантія безпеки: FORCE RLS має діяти на всіх нових таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_suppliers', 'beauty_products', 'beauty_product_location_settings', 'beauty_stock_levels',
                             'beauty_stock_operations', 'beauty_stock_batches', 'beauty_stock_movements', 'beauty_stocktakes',
                             'beauty_stocktake_lines']
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class WHERE oid = to_regclass('public.' || t) AND relrowsecurity AND relforcerowsecurity
        ) THEN
            RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
        END IF;
    END LOOP;
END
$$;");
        }

        private static readonly string[] InventoryTables =
        [
            "beauty_suppliers", "beauty_products", "beauty_product_location_settings", "beauty_stock_levels",
            "beauty_stock_operations", "beauty_stock_batches", "beauty_stock_movements", "beauty_stocktakes", "beauty_stocktake_lines",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Відкат знищує склад. Журнал рухів - первинний документ обліку: непорожній журнал блокує відкат (human gate, runbook).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- FORCE RLS приховав би рядки від власника без app.tenant_id; при RAISE вся міграція (разом з цією зміною) відкочується
    ALTER TABLE beauty_stock_movements NO FORCE ROW LEVEL SECURITY;
    IF EXISTS (SELECT 1 FROM beauty_stock_movements) THEN
        RAISE EXCEPTION 'inventory history exists';
    END IF;
END
$$;");

            migrationBuilder.DropTable(
                name: "beauty_product_location_settings");

            migrationBuilder.DropTable(
                name: "beauty_stock_levels");

            migrationBuilder.DropTable(
                name: "beauty_stock_movements");

            migrationBuilder.DropTable(
                name: "beauty_stocktake_lines");

            migrationBuilder.DropTable(
                name: "beauty_stock_batches");

            migrationBuilder.DropTable(
                name: "beauty_products");

            migrationBuilder.DropTable(
                name: "beauty_stock_operations");

            migrationBuilder.DropTable(
                name: "beauty_suppliers");

            migrationBuilder.DropTable(
                name: "beauty_stocktakes");
        }
    }
}
