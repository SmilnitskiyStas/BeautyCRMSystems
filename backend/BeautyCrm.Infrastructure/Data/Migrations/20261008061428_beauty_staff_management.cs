using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_staff_management : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "position",
                table: "beauty_specialists",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_specialist_absences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    date_from = table.Column<DateOnly>(type: "date", nullable: false),
                    date_to = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_specialist_absences", x => x.id);
                    table.UniqueConstraint("ak_beauty_specialist_absences_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_specialist_absences_dates", "date_to >= date_from");
                    table.CheckConstraint("ck_beauty_specialist_absences_note", "note IS NULL OR char_length(note) <= 500");
                    table.CheckConstraint("ck_beauty_specialist_absences_status", "status IN ('requested', 'approved', 'rejected', 'cancelled')");
                    table.CheckConstraint("ck_beauty_specialist_absences_type", "type IN ('sick', 'vacation', 'day_off', 'other')");
                    table.ForeignKey(
                        name: "fk_beauty_specialist_absences_beauty_specialists_tenant_id_spe",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_specialist_absences_users_tenant_id_decided_by_user_",
                        columns: x => new { x.tenant_id, x.decided_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_specialist_absences_users_tenant_id_requested_by_use",
                        columns: x => new { x.tenant_id, x.requested_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_specialist_services",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_specialist_services", x => new { x.tenant_id, x.specialist_id, x.service_id });
                    table.ForeignKey(
                        name: "fk_beauty_specialist_services_beauty_services_tenant_id_servic",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_specialist_services_beauty_specialists_tenant_id_spe",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_absences_tenant_id_decided_by_user_id",
                table: "beauty_specialist_absences",
                columns: new[] { "tenant_id", "decided_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_absences_tenant_id_requested_by_user_id",
                table: "beauty_specialist_absences",
                columns: new[] { "tenant_id", "requested_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_absences_tenant_id_specialist_id_date_from",
                table: "beauty_specialist_absences",
                columns: new[] { "tenant_id", "specialist_id", "date_from" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_services_tenant_id_service_id",
                table: "beauty_specialist_services",
                columns: new[] { "tenant_id", "service_id" });

            // ---- Backfill: кожному наявному майстру призначити всі наявні послуги tenant-а (щоб не зламати демо/наявні записи) ----
            // Власник таблиць підпадає під FORCE RLS, а app.tenant_id у міграції не задано, тому на час backfill
            // вимикаємо FORCE на джерелах (транзакція міграції; повертаємо одразу після вставки).
            migrationBuilder.Sql("ALTER TABLE beauty_specialists NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_services NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                INSERT INTO beauty_specialist_services (tenant_id, specialist_id, service_id)
                SELECT sp.tenant_id, sp.id, sv.id
                FROM beauty_specialists sp
                JOIN beauty_services sv ON sv.tenant_id = sp.tenant_id
                ON CONFLICT DO NOTHING;");
            migrationBuilder.Sql("ALTER TABLE beauty_specialists FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_services FORCE ROW LEVEL SECURITY;");

            // ---- Немає перетину активних (requested/approved) відсутностей одного майстра (повні дні, включно) ----
            migrationBuilder.Sql(@"
                ALTER TABLE beauty_specialist_absences
                    ADD CONSTRAINT ex_beauty_specialist_absences_no_overlap
                    EXCLUDE USING gist (
                        tenant_id WITH =,
                        specialist_id WITH =,
                        daterange(date_from, date_to, '[]') WITH &&
                    ) WHERE (status IN ('requested', 'approved'));");

            // ---- RLS (як у add_beauty_schema: ENABLE + FORCE, fail-closed без app.tenant_id) ----
            foreach (var table in new[] { "beauty_specialist_services", "beauty_specialist_absences" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "beauty_specialist_absences");

            migrationBuilder.DropTable(
                name: "beauty_specialist_services");

            migrationBuilder.DropColumn(
                name: "position",
                table: "beauty_specialists");
        }
    }
}
