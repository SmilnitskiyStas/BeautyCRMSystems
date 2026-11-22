using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Логотип закладу (контракт .ai/artifacts/location-logo/API_CONTRACT.md): beauty_locations.logo_storage_key character(32) NULL
    /// + CHECK + partial UNIQUE; публічні функції каталогу (ADR-022) віддають ключ ще однією колонкою (див. .Sql.cs). Адитивна, без backfill.
    /// Down повертає попередні визначення функцій і права, потім видаляє колонку (ключі логотипів губляться; файли на томі прибере очищувач).
    /// </summary>
    public partial class beauty_location_logo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "logo_storage_key",
                table: "beauty_locations",
                type: "character(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_locations_logo_storage_key",
                table: "beauty_locations",
                sql: "logo_storage_key IS NULL OR logo_storage_key ~ '^[0-9a-f]{32}$'");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_locations_logo_storage_key",
                table: "beauty_locations",
                column: "logo_storage_key",
                unique: true,
                filter: "logo_storage_key IS NOT NULL");

            UpCatalogLogo(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DownCatalogLogo(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "ux_beauty_locations_logo_storage_key",
                table: "beauty_locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_locations_logo_storage_key",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "logo_storage_key",
                table: "beauty_locations");
        }
    }
}
