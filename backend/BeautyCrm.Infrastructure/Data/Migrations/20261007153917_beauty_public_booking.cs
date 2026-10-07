using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_public_booking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key_hash",
                table: "beauty_appointments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_request_hash",
                table: "beauty_appointments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_token_hash",
                table: "beauty_appointments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_beauty_appointments_idempotency",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "idempotency_key_hash" },
                unique: true,
                filter: "idempotency_key_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_appointments_public_token",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "public_token_hash" },
                unique: true,
                filter: "public_token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_beauty_appointments_idempotency",
                table: "beauty_appointments");

            migrationBuilder.DropIndex(
                name: "ux_beauty_appointments_public_token",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "idempotency_key_hash",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "idempotency_request_hash",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "public_token_hash",
                table: "beauty_appointments");
        }
    }
}
