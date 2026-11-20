using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>Роздрібна ціна товару складу (ADR-025, контракт §32). ADD COLUMN NULL без перезаписування таблиці. Down: DROP COLUMN - дані роздрібних цін втрачаються (прийнято).</summary>
    public partial class beauty_inventory_sale_price : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "sale_price",
                table: "beauty_products",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_products_sale_price",
                table: "beauty_products",
                sql: "sale_price IS NULL OR sale_price BETWEEN 0 AND 1000000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_products_sale_price",
                table: "beauty_products");

            migrationBuilder.DropColumn(
                name: "sale_price",
                table: "beauty_products");
        }
    }
}
