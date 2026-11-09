using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_public_location_catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            UpPrelude(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "beauty_locations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_discoverable",
                table: "beauty_locations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                table: "beauty_locations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                table: "beauty_locations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_code",
                table: "beauty_locations",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValueSql: "public.beauty_generate_public_code()");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_locations_discoverable_coords",
                table: "beauty_locations",
                columns: new[] { "latitude", "longitude" },
                filter: "is_discoverable AND is_active AND latitude IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_locations_public_code",
                table: "beauty_locations",
                column: "public_code",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_locations_coords_pair",
                table: "beauty_locations",
                sql: "(latitude IS NULL) = (longitude IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_locations_latitude",
                table: "beauty_locations",
                sql: "latitude IS NULL OR latitude BETWEEN -90 AND 90");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_locations_longitude",
                table: "beauty_locations",
                sql: "longitude IS NULL OR longitude BETWEEN -180 AND 180");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_locations_public_code",
                table: "beauty_locations",
                sql: "public_code ~ '^[0-9A-HJKMNP-TV-Z]{8}$'");

            UpCatalog(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DownCatalog(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "ix_beauty_locations_discoverable_coords",
                table: "beauty_locations");

            migrationBuilder.DropIndex(
                name: "ux_beauty_locations_public_code",
                table: "beauty_locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_locations_coords_pair",
                table: "beauty_locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_locations_latitude",
                table: "beauty_locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_locations_longitude",
                table: "beauty_locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_locations_public_code",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "city",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "is_discoverable",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "public_code",
                table: "beauty_locations");

            DownPrelude(migrationBuilder);
        }
    }
}
