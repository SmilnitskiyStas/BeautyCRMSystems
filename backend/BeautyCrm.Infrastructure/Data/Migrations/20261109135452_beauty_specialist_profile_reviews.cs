using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_specialist_profile_reviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bio",
                table: "beauty_specialists",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "practice_start_year",
                table: "beauty_specialists",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_specialist_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rating = table.Column<short>(type: "smallint", nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_specialist_reviews", x => x.id);
                    table.UniqueConstraint("ak_beauty_specialist_reviews_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_specialist_reviews_author_len", "char_length(author_name) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_beauty_specialist_reviews_rating", "rating BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_beauty_specialist_reviews_text_len", "text IS NULL OR char_length(text) <= 1000");
                    table.ForeignKey(
                        name: "fk_beauty_specialist_reviews_beauty_appointments_tenant_id_app",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_specialist_reviews_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_specialist_reviews_beauty_specialists_tenant_id_spec",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_specialists_bio_len",
                table: "beauty_specialists",
                sql: "bio IS NULL OR char_length(bio) <= 500");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_specialists_practice_start_year",
                table: "beauty_specialists",
                sql: "practice_start_year IS NULL OR practice_start_year BETWEEN 1950 AND 2100");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_reviews_specialist_published",
                table: "beauty_specialist_reviews",
                columns: new[] { "tenant_id", "specialist_id", "is_published", "created_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_reviews_tenant_id_client_id",
                table: "beauty_specialist_reviews",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_specialist_reviews_appointment",
                table: "beauty_specialist_reviews",
                columns: new[] { "tenant_id", "appointment_id" },
                unique: true,
                filter: "appointment_id IS NOT NULL");

            // ---- RLS (як у beauty_location_closures: ENABLE + FORCE, fail-closed без app.tenant_id) ----
            migrationBuilder.Sql("ALTER TABLE beauty_specialist_reviews ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_specialist_reviews FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation ON beauty_specialist_reviews
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");

            // Права ролі застосунку: як для решти таблиць (SELECT/INSERT/UPDATE/DELETE); якщо ролі beautycrm_app немає
            // (інша назва / provisioning ops) - нічого не робимо, grant виконує ops (див. database.md §5).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON beauty_specialist_reviews TO beautycrm_app;
    END IF;
END
$$;");

            // Безпека: FORCE RLS має діяти на новій таблиці, інакше міграція падає, а не лишає таблицю в обхід RLS.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_class c
        WHERE c.oid = to_regclass('public.beauty_specialist_reviews') AND c.relrowsecurity AND c.relforcerowsecurity) THEN
        RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on beauty_specialist_reviews';
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP TABLE прибирає політику, індекси й FK таблиці відгуків.
            migrationBuilder.DropTable(
                name: "beauty_specialist_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_specialists_bio_len",
                table: "beauty_specialists");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_specialists_practice_start_year",
                table: "beauty_specialists");

            migrationBuilder.DropColumn(
                name: "bio",
                table: "beauty_specialists");

            migrationBuilder.DropColumn(
                name: "practice_start_year",
                table: "beauty_specialists");
        }
    }
}
