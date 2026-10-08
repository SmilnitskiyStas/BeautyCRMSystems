using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_locations_and_cancellation_history : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancel_reason",
                table: "beauty_appointments",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancelled_by_type",
                table: "beauty_appointments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cancelled_by_user_id",
                table: "beauty_appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_cancelled_by_user_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "cancelled_by_user_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_cancel_meta_status",
                table: "beauty_appointments",
                sql: "(cancelled_by_type IS NULL AND cancel_reason IS NULL) OR status = 'cancelled'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_cancel_reason_len",
                table: "beauty_appointments",
                sql: "cancel_reason IS NULL OR char_length(cancel_reason) <= 300");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_cancelled_by_type",
                table: "beauty_appointments",
                sql: "cancelled_by_type IS NULL OR cancelled_by_type IN ('client', 'staff', 'system')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_cancelled_by_user",
                table: "beauty_appointments",
                sql: "cancelled_by_user_id IS NULL OR cancelled_by_type = 'staff'");

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_appointments_users_tenant_id_cancelled_by_user_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "cancelled_by_user_id" },
                principalTable: "users",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // ---- Backfill (TASK-697): уже скасовані записи без даних про автора -> system (ніщо не видаляється) ----
            // Власник таблиці підпадає під FORCE RLS, а app.tenant_id у міграції не задано, тому на час backfill вимикаємо FORCE
            // (усе в транзакції міграції) і повертаємо одразу після UPDATE; нижче DO-блок перевіряє, що FORCE повернуто.
            migrationBuilder.Sql("ALTER TABLE beauty_appointments NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                UPDATE beauty_appointments
                SET cancelled_by_type = 'system'
                WHERE status = 'cancelled' AND cancelled_by_type IS NULL;");
            migrationBuilder.Sql("ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_appointments', 'beauty_locations']
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
            migrationBuilder.DropForeignKey(
                name: "fk_beauty_appointments_users_tenant_id_cancelled_by_user_id",
                table: "beauty_appointments");

            migrationBuilder.DropIndex(
                name: "ix_beauty_appointments_tenant_id_cancelled_by_user_id",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_cancel_meta_status",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_cancel_reason_len",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_cancelled_by_type",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_cancelled_by_user",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "cancel_reason",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "cancelled_by_type",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "cancelled_by_user_id",
                table: "beauty_appointments");
        }
    }
}
