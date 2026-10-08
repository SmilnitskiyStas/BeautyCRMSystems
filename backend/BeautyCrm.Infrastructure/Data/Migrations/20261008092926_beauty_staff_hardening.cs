using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_staff_hardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "beauty_specialist_absences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cancelled_by_user_id",
                table: "beauty_specialist_absences",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_absences_tenant_id_cancelled_by_user_id",
                table: "beauty_specialist_absences",
                columns: new[] { "tenant_id", "cancelled_by_user_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_specialist_absences_users_tenant_id_cancelled_by_use",
                table: "beauty_specialist_absences",
                columns: new[] { "tenant_id", "cancelled_by_user_id" },
                principalTable: "users",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Гарантія безпеки (TASK-696): FORCE RLS має діяти для таблиць керування працівниками. Міграція staff_management
            // на час backfill вимикає FORCE на джерелах і повертає його; тут це перевіряється явно, а не припускається.
            // Якщо FORCE десь не ввімкнено, міграція падає (RAISE EXCEPTION), а не лишає власника схеми в обхід RLS.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_specialists', 'beauty_services', 'beauty_specialist_services', 'beauty_specialist_absences']
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
                name: "fk_beauty_specialist_absences_users_tenant_id_cancelled_by_use",
                table: "beauty_specialist_absences");

            migrationBuilder.DropIndex(
                name: "ix_beauty_specialist_absences_tenant_id_cancelled_by_user_id",
                table: "beauty_specialist_absences");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "beauty_specialist_absences");

            migrationBuilder.DropColumn(
                name: "cancelled_by_user_id",
                table: "beauty_specialist_absences");
        }
    }
}
