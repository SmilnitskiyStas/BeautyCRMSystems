using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Дерево категорій товарів складу (ADR-025, контракт §32): таблиця beauty_product_categories + beauty_products.category_id (NULL, без перезапису).
    /// Стара текстова category лишається; дерево стартує порожнім. Ручні частини: унікальність імені в межах батька
    /// (tenant_id, COALESCE(parent_id, nil-uuid), lower(name)), RLS ENABLE + FORCE + tenant_isolation, перевірочний DO-блок.
    /// Down: DROP (дані дерева втрачаються - прийнято), але ВІДМОВЛЯЄ (RAISE EXCEPTION 'product categories exist'), якщо в таблиці є рядки.
    /// </summary>
    public partial class beauty_inventory_product_categories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                table: "beauty_products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_product_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_product_categories", x => x.id);
                    table.UniqueConstraint("ak_beauty_product_categories_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_product_categories_name", "char_length(btrim(name)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_beauty_product_categories_no_self_parent", "parent_id IS NULL OR parent_id <> id");
                    table.ForeignKey(
                        name: "fk_beauty_product_categories_beauty_product_categories_tenant_",
                        columns: x => new { x.tenant_id, x.parent_id },
                        principalTable: "beauty_product_categories",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_products_category",
                table: "beauty_products",
                columns: new[] { "tenant_id", "category_id" },
                filter: "category_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_product_categories_parent",
                table: "beauty_product_categories",
                columns: new[] { "tenant_id", "parent_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_products_beauty_product_categories_tenant_id_categor",
                table: "beauty_products",
                columns: new[] { "tenant_id", "category_id" },
                principalTable: "beauty_product_categories",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Унікальне ім'я в межах батька (корінь: parent_id IS NULL -> nil-uuid), без урахування регістру; не виражається в моделі EF
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_beauty_product_categories_name ON beauty_product_categories (tenant_id, COALESCE(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));");

            // RLS (ENABLE + FORCE, fail-closed без app.tenant_id, ADR-002)
            migrationBuilder.Sql("ALTER TABLE beauty_product_categories ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_product_categories FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation ON beauty_product_categories
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");

            // GRANT (не в міграції): app-роль покрита ALL TABLES у scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql; воркеру доступ не потрібен.

            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_class WHERE oid = to_regclass('public.beauty_product_categories') AND relrowsecurity AND relforcerowsecurity
    ) THEN
        RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on beauty_product_categories';
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Відкат знищує дерево категорій: непорожня таблиця блокує відкат (вся міграція відкочується разом з RAISE).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- FORCE RLS приховав би рядки від власника без app.tenant_id
    ALTER TABLE beauty_product_categories NO FORCE ROW LEVEL SECURITY;
    IF EXISTS (SELECT 1 FROM beauty_product_categories) THEN
        RAISE EXCEPTION 'product categories exist';
    END IF;
END
$$;");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_products_beauty_product_categories_tenant_id_categor",
                table: "beauty_products");

            migrationBuilder.DropTable(
                name: "beauty_product_categories");

            migrationBuilder.DropIndex(
                name: "ix_beauty_products_category",
                table: "beauty_products");

            migrationBuilder.DropColumn(
                name: "category_id",
                table: "beauty_products");
        }
    }
}
