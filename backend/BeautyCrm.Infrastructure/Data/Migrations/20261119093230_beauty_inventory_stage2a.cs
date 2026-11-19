using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// ADR-025 §11 / контракт §32.16-§32.19, TASK-883. Склад, етап 2a: норми матеріалів (beauty_service_material_sets + beauty_service_materials),
    /// дефіцит (beauty_stock_levels.shortage_quantity, beauty_stock_movements.is_shortage, beauty_stocktake_lines.expected_shortage),
    /// автосписання (beauty_stock_operations.appointment_id + UNIQUE (tenant_id, appointment_id)), сповіщення (beauty_staff_alerts).
    /// Згенерована `migrations add`; ручні частини: RLS ENABLE + FORCE + tenant_isolation (USING + WITH CHECK, ADR-002) на 3 нових таблицях,
    /// перевірочний DO-блок, Down з відмовою. Адитивна для даних: ADD COLUMN ... DEFAULT і заміна CHECK ck_beauty_stock_movements_batch
    /// на (batch_id IS NULL) = is_shortage не оновлюють рядків, тож append-only тригери етапу 1 (hardening) не зачіпаються; наявні рухи
    /// мають batch_id, отже задовольняють новий CHECK. GRANT-и (у т.ч. роль воркера) - поза міграцією: scripts/dev/setup-db.ps1 і
    /// deploy/staging/db/grants.sql.
    /// Down ВІДМОВЛЯЄ (RAISE EXCEPTION 'inventory stage2a history exists'), якщо є рух is_shortage або операція з appointment_id
    /// (втрата обліку нестач/автосписань лише свідомо: human gate, runbook). Норми й сповіщення - конфігурація/похідні дані, їх Down видаляє.
    /// </summary>
    public partial class beauty_inventory_stage2a : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_movements_batch",
                table: "beauty_stock_movements");

            migrationBuilder.AddColumn<decimal>(
                name: "expected_shortage",
                table: "beauty_stocktake_lines",
                type: "numeric(14,3)",
                precision: 14,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "appointment_id",
                table: "beauty_stock_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_shortage",
                table: "beauty_stock_movements",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "shortage_quantity",
                table: "beauty_stock_levels",
                type: "numeric(14,3)",
                precision: 14,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "beauty_service_material_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_service_material_sets", x => x.id);
                    table.UniqueConstraint("ak_beauty_service_material_sets_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_beauty_service_material_sets_beauty_locations_tenant_id_loc",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_service_material_sets_beauty_services_tenant_id_serv",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_service_material_sets_users_tenant_id_updated_by_use",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_staff_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    threshold = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    days_bucket = table.Column<short>(type: "smallint", nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "open"),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_staff_alerts", x => x.id);
                    table.UniqueConstraint("ak_beauty_staff_alerts_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_staff_alerts_batch", "(kind = 'expiring') = (batch_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_staff_alerts_days_bucket", "days_bucket IS NULL OR (kind = 'expiring' AND days_bucket BETWEEN 0 AND 365)");
                    table.CheckConstraint("ck_beauty_staff_alerts_kind", "kind IN ('low_stock', 'expiring', 'shortage')");
                    table.CheckConstraint("ck_beauty_staff_alerts_operation", "(kind = 'shortage') = (operation_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_staff_alerts_read", "status <> 'read' OR read_at IS NOT NULL");
                    table.CheckConstraint("ck_beauty_staff_alerts_resolved", "(status = 'resolved') = (resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_staff_alerts_status", "status IN ('open', 'read', 'resolved')");
                    table.CheckConstraint("ck_beauty_staff_alerts_threshold", "threshold IS NULL OR (kind = 'low_stock' AND threshold >= 0)");
                    table.ForeignKey(
                        name: "fk_beauty_staff_alerts_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_alerts_beauty_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_alerts_beauty_stock_batches_tenant_id_batch_id",
                        columns: x => new { x.tenant_id, x.batch_id },
                        principalTable: "beauty_stock_batches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_alerts_beauty_stock_operations_tenant_id_opera",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalTable: "beauty_stock_operations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_alerts_users_tenant_id_read_by_user_id",
                        columns: x => new { x.tenant_id, x.read_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_service_materials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_service_materials", x => x.id);
                    table.UniqueConstraint("ak_beauty_service_materials_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_service_materials_quantity", "quantity > 0 AND quantity <= 100000");
                    table.ForeignKey(
                        name: "fk_beauty_service_materials_beauty_products_tenant_id_product_",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_service_materials_beauty_service_material_sets_tenan",
                        columns: x => new { x.tenant_id, x.set_id },
                        principalTable: "beauty_service_material_sets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stocktake_lines_expected_shortage",
                table: "beauty_stocktake_lines",
                sql: "expected_shortage IS NULL OR expected_shortage >= 0");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_stock_operations_appointment",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "appointment_id" },
                unique: true,
                filter: "appointment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_operations_appointment",
                table: "beauty_stock_operations",
                sql: "(kind = 'service_consumption') = (appointment_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_movements_batch",
                table: "beauty_stock_movements",
                sql: "(batch_id IS NULL) = is_shortage");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_movements_shortage",
                table: "beauty_stock_movements",
                sql: "NOT is_shortage OR type = 'service_consumption' OR (type IN ('receipt', 'transfer_in', 'stocktake_adjust') AND quantity > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_levels_shortage",
                table: "beauty_stock_levels",
                sql: "shortage_quantity >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_material_sets_tenant_id_location_id",
                table: "beauty_service_material_sets",
                columns: new[] { "tenant_id", "location_id" },
                filter: "location_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_material_sets_tenant_id_updated_by_user_id",
                table: "beauty_service_material_sets",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_service_material_sets_scope",
                table: "beauty_service_material_sets",
                columns: new[] { "tenant_id", "service_id", "location_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_materials_tenant_id_product_id",
                table: "beauty_service_materials",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_service_materials_pair",
                table: "beauty_service_materials",
                columns: new[] { "tenant_id", "set_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_alerts_location_status",
                table: "beauty_staff_alerts",
                columns: new[] { "tenant_id", "location_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_alerts_status_created",
                table: "beauty_staff_alerts",
                columns: new[] { "tenant_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_alerts_tenant_id_batch_id",
                table: "beauty_staff_alerts",
                columns: new[] { "tenant_id", "batch_id" },
                filter: "batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_alerts_tenant_id_operation_id",
                table: "beauty_staff_alerts",
                columns: new[] { "tenant_id", "operation_id" },
                filter: "operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_alerts_tenant_id_product_id",
                table: "beauty_staff_alerts",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_alerts_tenant_id_read_by_user_id",
                table: "beauty_staff_alerts",
                columns: new[] { "tenant_id", "read_by_user_id" },
                filter: "read_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_staff_alerts_dedupe",
                table: "beauty_staff_alerts",
                columns: new[] { "tenant_id", "dedupe_key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_stock_operations_beauty_appointments_tenant_id_appoi",
                table: "beauty_stock_operations",
                columns: new[] { "tenant_id", "appointment_id" },
                principalTable: "beauty_appointments",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на 3 нових таблицях ----
            foreach (var table in Stage2aTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // Гарантія безпеки: FORCE RLS має діяти на всіх нових таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_service_material_sets', 'beauty_service_materials', 'beauty_staff_alerts']
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

        private static readonly string[] Stage2aTables =
        [
            "beauty_service_material_sets", "beauty_service_materials", "beauty_staff_alerts",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Відкат знищує облік нестач і зв'язок списань із записами: непорожній стан блокує відкат (human gate, runbook).
            // FORCE RLS приховав би рядки від власника без app.tenant_id: на час перевірки знімаємо, при RAISE вся міграція відкочується,
            // інакше FORCE повертається (таблиці лишаються). Тригери append-only не зачіпаються (лише SELECT/ALTER).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    ALTER TABLE beauty_stock_movements NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_stock_operations NO FORCE ROW LEVEL SECURITY;
    IF EXISTS (SELECT 1 FROM beauty_stock_movements WHERE is_shortage)
       OR EXISTS (SELECT 1 FROM beauty_stock_operations WHERE appointment_id IS NOT NULL) THEN
        RAISE EXCEPTION 'inventory stage2a history exists';
    END IF;
    ALTER TABLE beauty_stock_movements FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_stock_operations FORCE ROW LEVEL SECURITY;
END
$$;");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_stock_operations_beauty_appointments_tenant_id_appoi",
                table: "beauty_stock_operations");

            migrationBuilder.DropTable(
                name: "beauty_service_materials");

            migrationBuilder.DropTable(
                name: "beauty_staff_alerts");

            migrationBuilder.DropTable(
                name: "beauty_service_material_sets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stocktake_lines_expected_shortage",
                table: "beauty_stocktake_lines");

            migrationBuilder.DropIndex(
                name: "ux_beauty_stock_operations_appointment",
                table: "beauty_stock_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_operations_appointment",
                table: "beauty_stock_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_movements_batch",
                table: "beauty_stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_movements_shortage",
                table: "beauty_stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stock_levels_shortage",
                table: "beauty_stock_levels");

            migrationBuilder.DropColumn(
                name: "expected_shortage",
                table: "beauty_stocktake_lines");

            migrationBuilder.DropColumn(
                name: "appointment_id",
                table: "beauty_stock_operations");

            migrationBuilder.DropColumn(
                name: "is_shortage",
                table: "beauty_stock_movements");

            migrationBuilder.DropColumn(
                name: "shortage_quantity",
                table: "beauty_stock_levels");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stock_movements_batch",
                table: "beauty_stock_movements",
                sql: "batch_id IS NOT NULL OR type = 'service_consumption'");
        }
    }
}
