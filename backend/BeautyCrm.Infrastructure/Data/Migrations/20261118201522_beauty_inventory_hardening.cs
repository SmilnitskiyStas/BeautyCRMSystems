using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_inventory_hardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_beauty_stock_movements_beauty_stock_batches_tenant_id_batch",
                table: "beauty_stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_beauty_stock_movements_batch",
                table: "beauty_stock_movements");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_beauty_stock_batches_tenant_id_id_product_id_location_id",
                table: "beauty_stock_batches",
                columns: new[] { "tenant_id", "id", "product_id", "location_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_stocktake_lines_expected",
                table: "beauty_stocktake_lines",
                sql: "expected_quantity >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_movements_batch",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "batch_id", "product_id", "location_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_products_name",
                table: "beauty_products",
                sql: "char_length(btrim(name)) BETWEEN 1 AND 200");

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_stock_movements_beauty_stock_batches_tenant_id_batch",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "batch_id", "product_id", "location_id" },
                principalTable: "beauty_stock_batches",
                principalColumns: new[] { "tenant_id", "id", "product_id", "location_id" },
                onDelete: ReferentialAction.Restrict);

            // Append-only на рівні БД (defense in depth, патерн user_access_audit): тригер діє й на власника, не лише REVOKE для app-ролі.
            // INSERT не зачіпається; DROP TABLE (Down початкової міграції) тригери не виконує. Виняток для свідомого очищення (human gate,
            // runbook відкату): власник робить ALTER TABLE ... DISABLE TRIGGER на час операції.
            migrationBuilder.Sql(@"
CREATE FUNCTION beauty_stock_journal_block_mutation() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = '42501';
END
$fn$;
CREATE TRIGGER beauty_stock_movements_append_only BEFORE UPDATE OR DELETE ON beauty_stock_movements
    FOR EACH ROW EXECUTE FUNCTION beauty_stock_journal_block_mutation();
CREATE TRIGGER beauty_stock_movements_no_truncate BEFORE TRUNCATE ON beauty_stock_movements
    FOR EACH STATEMENT EXECUTE FUNCTION beauty_stock_journal_block_mutation();
CREATE TRIGGER beauty_stock_operations_append_only BEFORE UPDATE OR DELETE ON beauty_stock_operations
    FOR EACH ROW EXECUTE FUNCTION beauty_stock_journal_block_mutation();
CREATE TRIGGER beauty_stock_operations_no_truncate BEFORE TRUNCATE ON beauty_stock_operations
    FOR EACH STATEMENT EXECUTE FUNCTION beauty_stock_journal_block_mutation();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER beauty_stock_movements_append_only ON beauty_stock_movements;
DROP TRIGGER beauty_stock_movements_no_truncate ON beauty_stock_movements;
DROP TRIGGER beauty_stock_operations_append_only ON beauty_stock_operations;
DROP TRIGGER beauty_stock_operations_no_truncate ON beauty_stock_operations;
DROP FUNCTION beauty_stock_journal_block_mutation();");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_stock_movements_beauty_stock_batches_tenant_id_batch",
                table: "beauty_stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_stocktake_lines_expected",
                table: "beauty_stocktake_lines");

            migrationBuilder.DropIndex(
                name: "ix_beauty_stock_movements_batch",
                table: "beauty_stock_movements");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_beauty_stock_batches_tenant_id_id_product_id_location_id",
                table: "beauty_stock_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_products_name",
                table: "beauty_products");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_stock_movements_batch",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "batch_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_stock_movements_beauty_stock_batches_tenant_id_batch",
                table: "beauty_stock_movements",
                columns: new[] { "tenant_id", "batch_id" },
                principalTable: "beauty_stock_batches",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
