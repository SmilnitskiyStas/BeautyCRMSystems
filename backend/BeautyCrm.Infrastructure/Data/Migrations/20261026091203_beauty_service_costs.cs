using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_service_costs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cost_captured_at",
                table: "beauty_appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cost_snapshot",
                table: "beauty_appointments",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "master_payout_snapshot",
                table: "beauty_appointments",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "master_percent_snapshot",
                table: "beauty_appointments",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_service_cost_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    changed_fields = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_service_cost_audit", x => x.id);
                    table.UniqueConstraint("ak_beauty_service_cost_audit_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_service_cost_audit_action", "action IN ('set', 'delete')");
                    table.CheckConstraint("ck_beauty_service_cost_audit_changed_fields", "changed_fields <@ ARRAY['cost_amount', 'master_percent']::text[]");
                    table.ForeignKey(
                        name: "fk_beauty_service_cost_audit_beauty_locations_tenant_id_locati",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_service_cost_audit_beauty_services_tenant_id_service",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_service_cost_audit_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_service_costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    master_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_service_costs", x => x.id);
                    table.UniqueConstraint("ak_beauty_service_costs_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_service_costs_cost", "cost_amount >= 0 AND cost_amount <= 1000000");
                    table.CheckConstraint("ck_beauty_service_costs_master_percent", "master_percent IS NULL OR master_percent BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_beauty_service_costs_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_service_costs_beauty_services_tenant_id_service_id",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_service_costs_users_tenant_id_updated_by_user_id",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_cost_consistency",
                table: "beauty_appointments",
                sql: "(cost_snapshot IS NULL OR cost_captured_at IS NOT NULL) AND (master_payout_snapshot IS NULL OR cost_snapshot IS NOT NULL) AND (master_percent_snapshot IS NULL OR master_payout_snapshot IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_cost_status",
                table: "beauty_appointments",
                sql: "(cost_snapshot IS NULL AND master_percent_snapshot IS NULL AND master_payout_snapshot IS NULL AND cost_captured_at IS NULL) OR status = 'completed'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_cost_values",
                table: "beauty_appointments",
                sql: "(cost_snapshot IS NULL OR cost_snapshot >= 0) AND (master_payout_snapshot IS NULL OR master_payout_snapshot >= 0) AND (master_percent_snapshot IS NULL OR master_percent_snapshot BETWEEN 0 AND 100)");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_cost_audit_tenant_id_location_id",
                table: "beauty_service_cost_audit",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_cost_audit_tenant_id_service_id_created_at",
                table: "beauty_service_cost_audit",
                columns: new[] { "tenant_id", "service_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_cost_audit_tenant_id_user_id",
                table: "beauty_service_cost_audit",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_costs_tenant_id_location_id",
                table: "beauty_service_costs",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_costs_tenant_id_updated_by_user_id",
                table: "beauty_service_costs",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_service_costs_scope",
                table: "beauty_service_costs",
                columns: new[] { "tenant_id", "service_id", "location_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на двох нових таблицях ----
            foreach (var table in new[] { "beauty_service_costs", "beauty_service_cost_audit" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // GRANT (не в міграції: ролі залежать від середовища, див. runbook):
            //   API-роль: SELECT/INSERT/UPDATE/DELETE на beauty_service_costs, лише SELECT/INSERT на beauty_service_cost_audit (append-only);
            //   ролі воркера й публічного шляху: нічого. Записи, завершені до розгортання, лишаються з NULL (без backfill, ADR-014 §2.2).

            // Гарантія безпеки: FORCE RLS має діяти на нових і змінених/пов'язаних таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_service_costs', 'beauty_service_cost_audit', 'beauty_appointments', 'beauty_services', 'beauty_service_prices']
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
            WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
            RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
        END IF;
    END LOOP;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Відкат видаляє таблиці й колонки знімка: собівартості, аудит і знімки завершених записів втрачаються (runbook).
            migrationBuilder.DropTable(
                name: "beauty_service_cost_audit");

            migrationBuilder.DropTable(
                name: "beauty_service_costs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_cost_consistency",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_cost_status",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_cost_values",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "cost_captured_at",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "cost_snapshot",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "master_payout_snapshot",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "master_percent_snapshot",
                table: "beauty_appointments");
        }
    }
}
