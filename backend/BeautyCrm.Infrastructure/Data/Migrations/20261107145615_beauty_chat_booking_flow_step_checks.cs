using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_chat_booking_flow_step_checks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_location",
                table: "beauty_chat_booking_flows");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_service",
                table: "beauty_chat_booking_flows");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_specialist",
                table: "beauty_chat_booking_flows");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_location",
                table: "beauty_chat_booking_flows",
                sql: "step NOT IN ('time', 'day', 'confirm') OR location_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_service",
                table: "beauty_chat_booking_flows",
                sql: "step NOT IN ('specialist', 'last_location', 'location', 'time', 'day', 'confirm') OR service_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_specialist",
                table: "beauty_chat_booking_flows",
                sql: "step NOT IN ('last_location', 'location', 'time', 'day', 'confirm') OR specialist_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Старі CHECK повертаються NOT VALID: таблиця під FORCE RLS, міграція не бачить рядків, а наявні короткоживучі потоки (30 хв) можуть їх порушувати
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_location",
                table: "beauty_chat_booking_flows");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_service",
                table: "beauty_chat_booking_flows");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_booking_flows_step_specialist",
                table: "beauty_chat_booking_flows");

            migrationBuilder.Sql("ALTER TABLE beauty_chat_booking_flows ADD CONSTRAINT ck_beauty_chat_booking_flows_step_location CHECK (step NOT IN ('service', 'specialist', 'time', 'day', 'confirm') OR location_id IS NOT NULL) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE beauty_chat_booking_flows ADD CONSTRAINT ck_beauty_chat_booking_flows_step_service CHECK (step NOT IN ('specialist', 'time', 'day', 'confirm') OR service_id IS NOT NULL) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE beauty_chat_booking_flows ADD CONSTRAINT ck_beauty_chat_booking_flows_step_specialist CHECK (step NOT IN ('time', 'day', 'confirm') OR specialist_id IS NOT NULL) NOT VALID;");
        }
    }
}
