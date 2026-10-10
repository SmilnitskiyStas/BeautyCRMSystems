using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_location_closures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "closed_weekdays",
                table: "beauty_locations",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "beauty_location_closures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_from = table.Column<DateOnly>(type: "date", nullable: false),
                    date_to = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_location_closures", x => x.id);
                    table.UniqueConstraint("ak_beauty_location_closures_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_location_closures_dates", "date_to >= date_from");
                    table.CheckConstraint("ck_beauty_location_closures_reason", "reason IS NULL OR char_length(reason) <= 200");
                    table.ForeignKey(
                        name: "fk_beauty_location_closures_beauty_locations_tenant_id_locatio",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_location_closures_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_locations_closed_weekdays",
                table: "beauty_locations",
                sql: "closed_weekdays <@ ARRAY['mon','tue','wed','thu','fri','sat','sun']::text[]");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_location_closures_tenant_id_created_by_user_id",
                table: "beauty_location_closures",
                columns: new[] { "tenant_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_location_closures_tenant_id_location_id_date_from",
                table: "beauty_location_closures",
                columns: new[] { "tenant_id", "location_id", "date_from" });

            // ---- Немає перетину закриттів одного закладу (повні дні, включно) ----
            migrationBuilder.Sql(@"
                ALTER TABLE beauty_location_closures
                    ADD CONSTRAINT ex_beauty_location_closures_no_overlap
                    EXCLUDE USING gist (
                        tenant_id WITH =,
                        location_id WITH =,
                        daterange(date_from, date_to, '[]') WITH &&
                    );");

            // ---- RLS (як у add_beauty_schema: ENABLE + FORCE, fail-closed без app.tenant_id) ----
            migrationBuilder.Sql("ALTER TABLE beauty_location_closures ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_location_closures FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation ON beauty_location_closures
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");

            // Гарантія безпеки: FORCE RLS має діяти на новій таблиці й на beauty_locations (власник схеми не обходить RLS).
            // Якщо FORCE не ввімкнено, міграція падає (RAISE EXCEPTION), а не лишає схему в обхід RLS.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_locations', 'beauty_location_closures']
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
            migrationBuilder.DropTable(
                name: "beauty_location_closures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_locations_closed_weekdays",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "closed_weekdays",
                table: "beauty_locations");
        }
    }
}
