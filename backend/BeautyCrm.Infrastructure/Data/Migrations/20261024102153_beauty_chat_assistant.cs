using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_chat_assistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "beauty_messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "ai_lease_owner",
                table: "beauty_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ai_lease_until",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ai_paused_until",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_state",
                table: "beauty_conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<string>(
                name: "handoff_reason",
                table: "beauty_conversations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_channel_poll_state",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_update_id = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_channel_poll_state", x => new { x.tenant_id, x.channel_id });
                    table.CheckConstraint("ck_beauty_channel_poll_state_last_update_id", "last_update_id >= 0");
                    table.ForeignKey(
                        name: "fk_beauty_channel_poll_state_beauty_channels_tenant_id_channel",
                        columns: x => new { x.tenant_id, x.channel_id },
                        principalTable: "beauty_channels",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "beauty_chat_booking_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    price_original = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    price_final = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "prepared"),
                    confirmation_token_hash = table.Column<string>(type: "character(64)", nullable: false),
                    summary_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_chat_booking_proposals", x => x.id);
                    table.UniqueConstraint("ak_beauty_chat_booking_proposals_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_chat_booking_proposals_duration", "duration_minutes > 0");
                    table.CheckConstraint("ck_beauty_chat_booking_proposals_failure_code", "failure_code IS NULL OR char_length(failure_code) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_beauty_chat_booking_proposals_prices", "price_original >= 0 AND price_final >= 0");
                    table.CheckConstraint("ck_beauty_chat_booking_proposals_status", "status IN ('prepared', 'shown', 'confirmed', 'rejected', 'expired', 'superseded', 'failed')");
                    table.CheckConstraint("ck_beauty_chat_booking_proposals_token_hash", "confirmation_token_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_appointments_tenant_id",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_clients_tenant_id_clie",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_conversations_tenant_i",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_locations_tenant_id_lo",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_messages_tenant_id_sum",
                        columns: x => new { x.tenant_id, x.summary_message_id },
                        principalTable: "beauty_messages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_promotions_tenant_id_p",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalTable: "beauty_promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_services_tenant_id_ser",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_booking_proposals_beauty_specialists_tenant_id_",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_messages_inbound_received",
                table: "beauty_messages",
                columns: new[] { "tenant_id", "conversation_id", "sent_at" },
                filter: "direction = 'inbound' AND status = 'received'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages",
                sql: "kind IN ('text', 'command', 'unsupported')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_tenant_id_ai_state",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "ai_state" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_ai_state",
                table: "beauty_conversations",
                sql: "ai_state IN ('active', 'handoff')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_appointment_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "appointment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_client_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_conversation_id_cre",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_location_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_promotion_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_service_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_specialist_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "specialist_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_summary_message_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "summary_message_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_chat_booking_proposals_active",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "conversation_id" },
                unique: true,
                filter: "status IN ('prepared', 'shown')");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_chat_booking_proposals_token",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "confirmation_token_hash" },
                unique: true);

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на нових таблицях ----
            foreach (var table in new[] { "beauty_chat_booking_proposals", "beauty_channel_poll_state" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // GRANT runtime-ролі (не в міграції: роль залежить від середовища, див. runbook):
            //   SELECT/INSERT/UPDATE/DELETE на beauty_chat_booking_proposals і beauty_channel_poll_state.
            // beauty_list_tenants() (SECURITY DEFINER) свідомо НЕ створюється: рішення, у V1 її немає.

            // Гарантія безпеки: FORCE RLS має діяти на нових таблицях і на розмовах/повідомленнях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_chat_booking_proposals', 'beauty_channel_poll_state', 'beauty_conversations', 'beauty_messages']
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
                name: "beauty_channel_poll_state");

            migrationBuilder.DropTable(
                name: "beauty_chat_booking_proposals");

            migrationBuilder.DropIndex(
                name: "ix_beauty_messages_inbound_received",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages");

            migrationBuilder.DropIndex(
                name: "ix_beauty_conversations_tenant_id_ai_state",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_ai_state",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "ai_lease_owner",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "ai_lease_until",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "ai_paused_until",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "ai_state",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "handoff_reason",
                table: "beauty_conversations");
        }
    }
}
