using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_cancellation_settings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "beauty_cancellation_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    window_hours = table.Column<int>(type: "integer", nullable: false, defaultValue: 12),
                    refund_percent_in_window = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    refund_percent_outside = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    deduct_fee = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    fee_percent = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_cancellation_settings", x => x.id);
                    table.UniqueConstraint("ak_beauty_cancellation_settings_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_cancellation_settings_percents", "refund_percent_in_window BETWEEN 0 AND 100 AND refund_percent_outside BETWEEN 0 AND 100 AND fee_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_beauty_cancellation_settings_window", "window_hours >= 0 AND window_hours <= 720");
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_cancellation_settings_tenant_id",
                table: "beauty_cancellation_settings",
                column: "tenant_id",
                unique: true);

            // ---- RLS (як у add_beauty_schema: ENABLE + FORCE, fail-closed без app.tenant_id) ----
            migrationBuilder.Sql("ALTER TABLE beauty_cancellation_settings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_cancellation_settings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation ON beauty_cancellation_settings
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON beauty_cancellation_settings;");
            migrationBuilder.DropTable(
                name: "beauty_cancellation_settings");
        }
    }
}
