using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Фото товарів (ADR-027, контракт §33.22.6; human gate G1 пройдено). Адитивна, без backfill: beauty_product_images (RLS ENABLE+FORCE, композитний FK на товар ON DELETE CASCADE,
    /// глобально унікальний storage_key, DEFERRABLE UNIQUE порядку - сирим SQL, бо EF такого не створює). GRANT - у scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql.
    /// Down: ВІДМОВЛЯЄ (RAISE EXCEPTION 'product images exist'), якщо є хоч один рядок; інакше DROP TABLE (файли на томі не чіпає).
    /// </summary>
    public partial class beauty_product_images : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "beauty_product_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character(32)", fixedLength: true, maxLength: 32, nullable: false),
                    mime = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<short>(type: "smallint", nullable: false),
                    height = table.Column<short>(type: "smallint", nullable: false),
                    sort_order = table.Column<short>(type: "smallint", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_product_images", x => x.id);
                    table.UniqueConstraint("ak_beauty_product_images_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_product_images_height", "height BETWEEN 1 AND 1600");
                    table.CheckConstraint("ck_beauty_product_images_mime", "mime = 'image/webp'");
                    table.CheckConstraint("ck_beauty_product_images_size", "size_bytes > 0 AND size_bytes <= 10485760");
                    table.CheckConstraint("ck_beauty_product_images_sort_order", "sort_order BETWEEN 0 AND 99");
                    table.CheckConstraint("ck_beauty_product_images_storage_key", "storage_key ~ '^[0-9a-f]{32}$'");
                    table.CheckConstraint("ck_beauty_product_images_width", "width BETWEEN 1 AND 1600");
                    table.ForeignKey(
                        name: "fk_beauty_product_images_beauty_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "beauty_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_product_images_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_product_images_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_images_created_by",
                table: "beauty_product_images",
                columns: new[] { "tenant_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_images_product",
                table: "beauty_product_images",
                columns: new[] { "tenant_id", "product_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_product_images_storage_key",
                table: "beauty_product_images",
                column: "storage_key",
                unique: true);

            migrationBuilder.Sql(@"
ALTER TABLE beauty_product_images ADD CONSTRAINT ux_beauty_product_images_order
    UNIQUE (tenant_id, product_id, sort_order) DEFERRABLE INITIALLY DEFERRED;");

            // RLS (ENABLE + FORCE, fail-closed без app.tenant_id, ADR-002); нових політик, окрім tenant_isolation, немає
            migrationBuilder.Sql("ALTER TABLE beauty_product_images ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_product_images FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation ON beauty_product_images
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");

            // GRANT - не в міграції (scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql): app - SELECT, INSERT, DELETE, UPDATE (sort_order); воркер - нічого.

            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_class WHERE oid = to_regclass('public.beauty_product_images') AND relrowsecurity AND relforcerowsecurity
    ) THEN
        RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on beauty_product_images';
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Відкат знищує метадані фото (файли на томі стали б сиротами): за наявних рядків відмовляє (вся міграція відкочується разом з RAISE).
            // FORCE RLS приховав би рядки від власника без app.tenant_id, тому на час перевірки знімається (таблицю потім видаляємо).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    ALTER TABLE beauty_product_images NO FORCE ROW LEVEL SECURITY;
    IF EXISTS (SELECT 1 FROM beauty_product_images) THEN
        RAISE EXCEPTION 'product images exist';
    END IF;
END
$$;");

            migrationBuilder.DropTable(
                name: "beauty_product_images");
        }
    }
}
