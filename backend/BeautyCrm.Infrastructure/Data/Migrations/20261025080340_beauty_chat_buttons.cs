using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_chat_buttons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages");

            migrationBuilder.AddColumn<string>(
                name: "actions",
                table: "beauty_messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "callback_data",
                table: "beauty_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "callback_message_ref",
                table: "beauty_messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "client_confirmed_at",
                table: "beauty_appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_chat_keyboards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    token = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "active"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_option = table.Column<short>(type: "smallint", nullable: true),
                    used_by_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    markup_cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clear_attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_chat_keyboards", x => x.id);
                    table.UniqueConstraint("ak_beauty_chat_keyboards_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_chat_keyboards_appointment", "kind NOT IN ('appointment_actions', 'cancel_confirm', 'reminder_choice') OR appointment_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_chat_keyboards_clear_attempts", "clear_attempts >= 0");
                    table.CheckConstraint("ck_beauty_chat_keyboards_kind", "kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm')");
                    table.CheckConstraint("ck_beauty_chat_keyboards_options", "jsonb_typeof(options) = 'array' AND jsonb_array_length(options) <= 10");
                    table.CheckConstraint("ck_beauty_chat_keyboards_proposal", "kind <> 'booking_confirm' OR proposal_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_chat_keyboards_status", "status IN ('active', 'used', 'expired', 'revoked')");
                    table.CheckConstraint("ck_beauty_chat_keyboards_token", "token ~ '^[A-Za-z0-9_-]{22}$'");
                    table.CheckConstraint("ck_beauty_chat_keyboards_used", "(status = 'used' AND used_at IS NOT NULL AND used_by_message_id IS NOT NULL) OR (status <> 'used' AND used_at IS NULL AND used_by_message_id IS NULL)");
                    table.CheckConstraint("ck_beauty_chat_keyboards_used_option", "used_option IS NULL OR (status = 'used' AND used_option BETWEEN 0 AND 9)");
                    table.ForeignKey(
                        name: "fk_beauty_chat_keyboards_beauty_appointments_tenant_id_appoint",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_keyboards_beauty_chat_booking_proposals_tenant_",
                        columns: x => new { x.tenant_id, x.proposal_id },
                        principalTable: "beauty_chat_booking_proposals",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_keyboards_beauty_conversations_tenant_id_conver",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_keyboards_beauty_messages_tenant_id_message_id",
                        columns: x => new { x.tenant_id, x.message_id },
                        principalTable: "beauty_messages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_chat_keyboards_beauty_messages_tenant_id_used_by_mes",
                        columns: x => new { x.tenant_id, x.used_by_message_id },
                        principalTable: "beauty_messages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_actions",
                table: "beauty_messages",
                sql: "actions IS NULL OR (direction = 'outbound' AND jsonb_typeof(actions) = 'object' AND octet_length(actions::text) <= 4096)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_callback_fields",
                table: "beauty_messages",
                sql: "kind = 'callback' OR (callback_data IS NULL AND callback_message_ref IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages",
                sql: "kind IN ('text', 'command', 'unsupported', 'callback')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_active",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "conversation_id" },
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_sweeper",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "updated_at" },
                filter: "status <> 'active' AND markup_cleared_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_appointment_id",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "appointment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_conversation_id",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_message_id",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_proposal_id",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "proposal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_token",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_used_by_message_id",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "used_by_message_id" });

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на новій таблиці ----
            migrationBuilder.Sql("ALTER TABLE beauty_chat_keyboards ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_chat_keyboards FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation ON beauty_chat_keyboards
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");

            // GRANT (не в міграції: ролі залежать від середовища, див. runbook):
            //   API-роль: SELECT/INSERT/UPDATE на beauty_chat_keyboards;
            //   роль воркера: SELECT/INSERT на beauty_chat_keyboards, UPDATE beauty_messages.external_message_id.

            // Гарантія безпеки: FORCE RLS має діяти на новій і змінюваних таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_chat_keyboards', 'beauty_messages', 'beauty_appointments']
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
                name: "beauty_chat_keyboards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_actions",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_callback_fields",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "actions",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "callback_data",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "callback_message_ref",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "client_confirmed_at",
                table: "beauty_appointments");

            // Відкат: рядки kind=callback старий CHECK не знає - переводимо їх в unsupported (рядки не видаляються;
            // callback_data / callback_message_ref / actions втрачаються разом з колонками). FORCE знімаємо лише на час UPDATE, як у TASK-736.
            migrationBuilder.Sql("ALTER TABLE beauty_messages NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE beauty_messages SET kind = 'unsupported' WHERE kind = 'callback';");
            migrationBuilder.Sql("ALTER TABLE beauty_messages FORCE ROW LEVEL SECURITY;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages",
                sql: "kind IN ('text', 'command', 'unsupported')");
        }
    }
}
