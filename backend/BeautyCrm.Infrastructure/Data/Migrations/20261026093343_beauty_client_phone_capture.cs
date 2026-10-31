using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_client_phone_capture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt_all_or_none",
                table: "beauty_conversations");

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                table: "beauty_messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_verdict",
                table: "beauty_messages",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reply_keyboard_open_at",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "call_consent_at",
                table: "beauty_clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "duplicate_of_client_id",
                table: "beauty_clients",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_pending",
                table: "beauty_clients",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "phone_pending_at",
                table: "beauty_clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_pending_source",
                table: "beauty_clients",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "phone_requested_at",
                table: "beauty_clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_source",
                table: "beauty_clients",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_messages_contact_phone",
                table: "beauty_messages",
                columns: new[] { "tenant_id", "created_at" },
                filter: "contact_phone IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_contact_fields",
                table: "beauty_messages",
                sql: "kind = 'contact' OR (contact_phone IS NULL AND contact_verdict IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_contact_phone",
                table: "beauty_messages",
                sql: "contact_phone IS NULL OR (contact_verdict IS NOT NULL AND contact_verdict = 'own' AND contact_phone ~ '^\\+[1-9][0-9]{7,14}$')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_contact_verdict",
                table: "beauty_messages",
                sql: "contact_verdict IS NULL OR contact_verdict IN ('own', 'foreign', 'invalid')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages",
                sql: "kind IN ('text', 'command', 'unsupported', 'callback', 'contact')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt",
                table: "beauty_conversations",
                sql: "pending_prompt IS NULL OR pending_prompt IN ('reminder_choice', 'phone_request')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt_all_or_none",
                table: "beauty_conversations",
                sql: "(pending_prompt IS NULL AND pending_prompt_appointment_id IS NULL AND pending_prompt_expires_at IS NULL) OR (pending_prompt IS NOT NULL AND pending_prompt = 'reminder_choice' AND pending_prompt_appointment_id IS NOT NULL AND pending_prompt_expires_at IS NOT NULL) OR (pending_prompt IS NOT NULL AND pending_prompt = 'phone_request' AND pending_prompt_expires_at IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_clients_tenant_id_duplicate_of_client_id",
                table: "beauty_clients",
                columns: new[] { "tenant_id", "duplicate_of_client_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_clients_duplicate_not_self",
                table: "beauty_clients",
                sql: "duplicate_of_client_id IS NULL OR duplicate_of_client_id <> id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_clients_phone_pending_all_or_none",
                table: "beauty_clients",
                sql: "(phone_pending IS NULL) = (phone_pending_source IS NULL) AND (phone_pending IS NULL) = (phone_pending_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_clients_phone_pending_format",
                table: "beauty_clients",
                sql: "phone_pending IS NULL OR phone_pending ~ '^\\+[1-9][0-9]{7,14}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_clients_phone_pending_source",
                table: "beauty_clients",
                sql: "phone_pending_source IS NULL OR phone_pending_source IN ('telegram_contact', 'manual_chat')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_clients_phone_source",
                table: "beauty_clients",
                sql: "phone_source IS NULL OR phone_source IN ('telegram_contact', 'manual_chat', 'web_form', 'staff')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_clients_phone_source_requires_phone",
                table: "beauty_clients",
                sql: "phone_source IS NULL OR phone IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_clients_beauty_clients_tenant_id_duplicate_of_client",
                table: "beauty_clients",
                columns: new[] { "tenant_id", "duplicate_of_client_id" },
                principalTable: "beauty_clients",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Нових таблиць немає: політики tenant_isolation і FORCE лишаються. Без backfill (усі нові колонки NULL).
            // GRANT без змін (API-роль уже має UPDATE; роль воркера не читає contact_phone), див. runbook.

            // Гарантія безпеки: FORCE RLS має діяти на змінених таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_clients', 'beauty_conversations', 'beauty_messages']
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
            migrationBuilder.DropForeignKey(
                name: "fk_beauty_clients_beauty_clients_tenant_id_duplicate_of_client",
                table: "beauty_clients");

            migrationBuilder.DropIndex(
                name: "ix_beauty_messages_contact_phone",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_contact_fields",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_contact_phone",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_contact_verdict",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt_all_or_none",
                table: "beauty_conversations");

            migrationBuilder.DropIndex(
                name: "ix_beauty_clients_tenant_id_duplicate_of_client_id",
                table: "beauty_clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_clients_duplicate_not_self",
                table: "beauty_clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_clients_phone_pending_all_or_none",
                table: "beauty_clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_clients_phone_pending_format",
                table: "beauty_clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_clients_phone_pending_source",
                table: "beauty_clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_clients_phone_source",
                table: "beauty_clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_clients_phone_source_requires_phone",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "contact_verdict",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "reply_keyboard_open_at",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "call_consent_at",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "duplicate_of_client_id",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "phone_pending",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "phone_pending_at",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "phone_pending_source",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "phone_requested_at",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "phone_source",
                table: "beauty_clients");

            // Відкат: рядки kind=contact старий CHECK не знає - переводимо їх в unsupported (рядки не видаляються; contact_phone/verdict
            // втрачаються разом з колонками), питання phone_request знімається (pending_prompt* = NULL). Телефони phone_pending
            // втрачаються з колонками. FORCE знімаємо лише на час UPDATE, як у TASK-749/736.
            migrationBuilder.Sql("ALTER TABLE beauty_messages NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE beauty_messages SET kind = 'unsupported' WHERE kind = 'contact';");
            migrationBuilder.Sql("ALTER TABLE beauty_messages FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_conversations NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE beauty_conversations SET pending_prompt = NULL, pending_prompt_appointment_id = NULL, pending_prompt_expires_at = NULL WHERE pending_prompt = 'phone_request';");
            migrationBuilder.Sql("ALTER TABLE beauty_conversations FORCE ROW LEVEL SECURITY;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_kind",
                table: "beauty_messages",
                sql: "kind IN ('text', 'command', 'unsupported', 'callback')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt",
                table: "beauty_conversations",
                sql: "pending_prompt IS NULL OR pending_prompt IN ('reminder_choice')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt_all_or_none",
                table: "beauty_conversations",
                sql: "(pending_prompt IS NULL) = (pending_prompt_appointment_id IS NULL) AND (pending_prompt IS NULL) = (pending_prompt_expires_at IS NULL)");
        }
    }
}
