using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_chat_booking_flow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards");

            migrationBuilder.AddColumn<decimal>(
                name: "latitude",
                table: "beauty_locations",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "longitude",
                table: "beauty_locations",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "flow_id",
                table: "beauty_chat_keyboards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "flow_version",
                table: "beauty_chat_keyboards",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "flow_id",
                table: "beauty_chat_booking_proposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_chat_booking_flows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "active"),
                    step = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    suggested_last_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    day = table.Column<DateOnly>(type: "date", nullable: true),
                    preferred_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    cursor_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    list_cursor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    skipped_steps = table.Column<string[]>(type: "character varying(16)[]", nullable: false, defaultValueSql: "'{}'"),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "button"),
                    cancel_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_chat_booking_flows", x => x.id);
                    table.UniqueConstraint("ak_chat_booking_flows_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_chat_booking_flows_cancel", "(status = 'cancelled') = (cancel_reason IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_cancel_reason", "cancel_reason IS NULL OR cancel_reason IN ('client', 'declined', 'handoff', 'no_services')");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_finished", "(status = 'active') = (finished_at IS NULL)");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_origin", "origin IN ('button', 'command', 'llm', 'text')");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_skipped_steps", "skipped_steps <@ ARRAY['last_location','location','specialist']::varchar[]");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_status", "status IN ('active', 'completed', 'cancelled', 'expired', 'superseded')");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_step", "step IN ('last_location', 'location', 'service', 'specialist', 'time', 'day', 'confirm')");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_step_confirm", "step <> 'confirm' OR proposal_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_step_last_location", "step <> 'last_location' OR suggested_last_location_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_step_location", "step NOT IN ('service', 'specialist', 'time', 'day', 'confirm') OR location_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_step_service", "step NOT IN ('specialist', 'time', 'day', 'confirm') OR service_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_step_specialist", "step NOT IN ('time', 'day', 'confirm') OR specialist_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_chat_booking_flows_version", "version >= 0");
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_flows_beauty_chat_booking_proposals_ten",
                        columns: x => new { x.tenant_id, x.proposal_id },
                        principalTable: "beauty_chat_booking_proposals",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_flows_beauty_conversations_tenant_id_co",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_flows_beauty_locations_tenant_id_locati",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_flows_beauty_locations_tenant_id_sugges",
                        columns: x => new { x.tenant_id, x.suggested_last_location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_flows_beauty_services_tenant_id_service",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_flows_beauty_specialists_tenant_id_spec",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages",
                sql: "kind IN ('text', 'command', 'unsupported', 'callback', 'contact', 'location')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_locations_coordinates",
                table: "beauty_locations",
                sql: "(latitude IS NULL) = (longitude IS NULL) AND latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_flow_id",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "flow_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_keyboards_flow_link",
                table: "beauty_chat_keyboards",
                sql: "kind NOT LIKE 'flow\\_%' OR (flow_version IS NOT NULL AND (flow_id IS NOT NULL OR status <> 'active'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_keyboards_flow_none",
                table: "beauty_chat_keyboards",
                sql: "kind LIKE 'flow\\_%' OR (flow_id IS NULL AND flow_version IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards",
                sql: "kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm', 'marketing_consent', 'campaign_offer', 'flow_last_location', 'flow_location', 'flow_service', 'flow_specialist', 'flow_time', 'flow_day')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_flow_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "flow_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_retention",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "finished_at" },
                filter: "status <> 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_sweeper",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "expires_at" },
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_tenant_id_conversation_id_created",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_tenant_id_location_id",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_tenant_id_proposal_id",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "proposal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_tenant_id_service_id",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_tenant_id_specialist_id",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "specialist_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_flows_tenant_id_suggested_last_location",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "suggested_last_location_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_chat_booking_flows_active",
                table: "beauty_chat_booking_flows",
                columns: new[] { "tenant_id", "conversation_id" },
                unique: true,
                filter: "status = 'active'");

            // Композитні FK на потік: ON DELETE SET NULL (flow_id) (PG15+; EF не вміє column list, тому SQL).
            // Retention потоків (30 днів) не блокується пропозиціями/клавіатурами; tenant_id не обнуляється.
            // Клавіатура з NULL flow_id допустима лише не-active (CHECK ck_beauty_chat_keyboards_flow_link).
            // Назви збігаються з тими, що очікує Down (DropForeignKey).
            migrationBuilder.Sql(@"
ALTER TABLE beauty_chat_booking_proposals
    ADD CONSTRAINT fk_beauty_chat_booking_proposals_chat_booking_flows_tenant_id_
    FOREIGN KEY (tenant_id, flow_id) REFERENCES beauty_chat_booking_flows (tenant_id, id) ON DELETE SET NULL (flow_id);");
            migrationBuilder.Sql(@"
ALTER TABLE beauty_chat_keyboards
    ADD CONSTRAINT fk_beauty_chat_keyboards_chat_booking_flows_tenant_id_flow_id
    FOREIGN KEY (tenant_id, flow_id) REFERENCES beauty_chat_booking_flows (tenant_id, id) ON DELETE SET NULL (flow_id);");

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на новій таблиці ----
            migrationBuilder.Sql("ALTER TABLE beauty_chat_booking_flows ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_chat_booking_flows FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation ON beauty_chat_booking_flows
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");

            // GRANT (не в міграції: ролі залежать від середовища; deploy/staging/db/grants.sql і scripts/dev/setup-db.ps1):
            //   app-роль отримує права через ALL TABLES; воркер потоків не читає.

            // Гарантія безпеки: FORCE RLS має діяти на новій і змінюваних таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_chat_booking_flows', 'beauty_chat_keyboards', 'beauty_messages', 'beauty_locations', 'beauty_chat_booking_proposals']
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
            // УВАГА, втрата даних: таблиця beauty_chat_booking_flows (стан і історія потоків), клавіатури kind=flow_*
            // (видаляються), колонки flow_id/flow_version/flow_id у keyboards/proposals та координати закладів
            // beauty_locations.latitude/longitude (введені персоналом) зникають безповоротно. Повідомлення kind=location
            // переводяться в unsupported (рядки лишаються). На staging/prod - human gate і експорт координат закладів
            // перед відкатом (runbook). FORCE RLS знімаємо лише на час DELETE/UPDATE, як у TASK-736.
            migrationBuilder.Sql("ALTER TABLE beauty_chat_keyboards NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("DELETE FROM beauty_chat_keyboards WHERE kind LIKE 'flow\\_%';");
            migrationBuilder.Sql("ALTER TABLE beauty_chat_keyboards FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_messages NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE beauty_messages SET kind = 'unsupported' WHERE kind = 'location';");
            migrationBuilder.Sql("ALTER TABLE beauty_messages FORCE ROW LEVEL SECURITY;");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_chat_booking_proposals_chat_booking_flows_tenant_id_",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_chat_keyboards_chat_booking_flows_tenant_id_flow_id",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropTable(
                name: "beauty_chat_booking_flows");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_locations_coordinates",
                table: "beauty_locations");

            migrationBuilder.DropIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_flow_id",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_keyboards_flow_link",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_keyboards_flow_none",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_flow_id",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "beauty_locations");

            migrationBuilder.DropColumn(
                name: "flow_id",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropColumn(
                name: "flow_version",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropColumn(
                name: "flow_id",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages",
                sql: "kind IN ('text', 'command', 'unsupported', 'callback', 'contact')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards",
                sql: "kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm', 'marketing_consent', 'campaign_offer')");
        }
    }
}
