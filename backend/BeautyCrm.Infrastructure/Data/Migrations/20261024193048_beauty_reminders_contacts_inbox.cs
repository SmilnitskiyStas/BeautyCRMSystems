using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_reminders_contacts_inbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_reminders_status",
                table: "beauty_reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_reminder_option",
                table: "beauty_appointments");

            migrationBuilder.AddColumn<Guid>(
                name: "message_id",
                table: "beauty_reminders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "method",
                table: "beauty_reminders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "offset_minutes",
                table: "beauty_reminders",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "planned_starts_at",
                table: "beauty_reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "skip_reason",
                table: "beauty_reminders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sender_user_id",
                table: "beauty_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "awaiting_staff_since",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "handoff_at",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_prompt",
                table: "beauty_conversations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pending_prompt_appointment_id",
                table: "beauty_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_prompt_expires_at",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reminders_stop_at",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "staff_last_read_at",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "staff_replied_at",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reminder_method",
                table: "beauty_appointments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<short[]>(
                name: "reminder_offsets",
                table: "beauty_appointments",
                type: "smallint[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "beauty_call_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reminder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    offset_minutes = table.Column<short>(type: "smallint", nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "open"),
                    closed_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    completed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_call_tasks", x => x.id);
                    table.UniqueConstraint("ak_beauty_call_tasks_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_call_tasks_completed", "(status IN ('done', 'skipped') AND completed_by_user_id IS NOT NULL AND completed_at IS NOT NULL) OR (status NOT IN ('done', 'skipped') AND completed_by_user_id IS NULL AND completed_at IS NULL)");
                    table.CheckConstraint("ck_beauty_call_tasks_reason", "reason IN ('reminder', 'delivery_failed')");
                    table.CheckConstraint("ck_beauty_call_tasks_status", "status IN ('open', 'done', 'skipped', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_beauty_call_tasks_beauty_appointments_tenant_id_appointment",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_call_tasks_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_call_tasks_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_call_tasks_beauty_reminders_tenant_id_reminder_id",
                        columns: x => new { x.tenant_id, x.reminder_id },
                        principalTable: "beauty_reminders",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_call_tasks_users_tenant_id_completed_by_user_id",
                        columns: x => new { x.tenant_id, x.completed_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_client_contact_preferences",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    offsets_minutes = table.Column<short[]>(type: "smallint[]", nullable: false, defaultValueSql: "'{}'"),
                    telegram_conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_client_contact_preferences", x => new { x.tenant_id, x.client_id });
                    table.CheckConstraint("ck_beauty_client_contact_preferences_call_single", "method <> 'call' OR cardinality(offsets_minutes) = 1");
                    table.CheckConstraint("ck_beauty_client_contact_preferences_method", "method IN ('none', 'telegram', 'call')");
                    table.CheckConstraint("ck_beauty_client_contact_preferences_none_empty", "(method = 'none') = (cardinality(offsets_minutes) = 0)");
                    table.CheckConstraint("ck_beauty_client_contact_preferences_offsets", "offsets_minutes <@ ARRAY[60, 120, 1440]::smallint[] AND cardinality(offsets_minutes) <= 3");
                    table.CheckConstraint("ck_beauty_client_contact_preferences_source", "source IN ('chat', 'admin', 'stop', 'web')");
                    table.CheckConstraint("ck_beauty_client_contact_preferences_updated_by", "updated_by_user_id IS NULL OR source = 'admin'");
                    table.ForeignKey(
                        name: "fk_beauty_client_contact_preferences_beauty_clients_tenant_id_",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_client_contact_preferences_beauty_conversations_tena",
                        columns: x => new { x.tenant_id, x.telegram_conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_client_contact_preferences_users_tenant_id_updated_b",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_reminder_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    telegram_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    call_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    quiet_start = table.Column<TimeOnly>(type: "time without time zone", nullable: false, defaultValueSql: "'22:00'"),
                    quiet_end = table.Column<TimeOnly>(type: "time without time zone", nullable: false, defaultValueSql: "'08:00'"),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_reminder_settings", x => x.tenant_id);
                    table.CheckConstraint("ck_beauty_reminder_settings_quiet", "quiet_start <> quiet_end");
                    table.ForeignKey(
                        name: "fk_beauty_reminder_settings_users_tenant_id_updated_by_user_id",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_reminders_tenant_id_message_id",
                table: "beauty_reminders",
                columns: new[] { "tenant_id", "message_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_reminders_point",
                table: "beauty_reminders",
                columns: new[] { "tenant_id", "appointment_id", "offset_minutes" },
                unique: true,
                filter: "status = 'scheduled' AND offset_minutes IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_reminders_method",
                table: "beauty_reminders",
                sql: "method IS NULL OR method IN ('telegram', 'auto')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_reminders_status",
                table: "beauty_reminders",
                sql: "status IN ('scheduled', 'sent', 'failed', 'cancelled', 'skipped')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_messages_tenant_id_sender_user_id",
                table: "beauty_messages",
                columns: new[] { "tenant_id", "sender_user_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_messages_sender_user",
                table: "beauty_messages",
                sql: "sender_user_id IS NULL OR sender_type = 'staff'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_awaiting_staff",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "awaiting_staff_since" },
                filter: "awaiting_staff_since IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_last_message_desc",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "last_message_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_tenant_id_pending_prompt_appointment_id",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "pending_prompt_appointment_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt",
                table: "beauty_conversations",
                sql: "pending_prompt IS NULL OR pending_prompt IN ('reminder_choice')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt_all_or_none",
                table: "beauty_conversations",
                sql: "(pending_prompt IS NULL) = (pending_prompt_appointment_id IS NULL) AND (pending_prompt IS NULL) = (pending_prompt_expires_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_reminder_call_single",
                table: "beauty_appointments",
                sql: "reminder_method <> 'call' OR cardinality(reminder_offsets) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_reminder_method",
                table: "beauty_appointments",
                sql: "reminder_method IN ('none', 'auto', 'telegram', 'call')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_reminder_none_empty",
                table: "beauty_appointments",
                sql: "(reminder_method = 'none') = (cardinality(reminder_offsets) = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_reminder_offsets",
                table: "beauty_appointments",
                sql: "reminder_offsets <@ ARRAY[60, 120, 1440]::smallint[] AND cardinality(reminder_offsets) <= 3");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_reminder_option",
                table: "beauty_appointments",
                sql: "reminder_option IN ('none', '1h', '2h', 'custom')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_call_tasks_tenant_id_client_id",
                table: "beauty_call_tasks",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_call_tasks_tenant_id_completed_by_user_id",
                table: "beauty_call_tasks",
                columns: new[] { "tenant_id", "completed_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_call_tasks_tenant_id_location_id_due_at",
                table: "beauty_call_tasks",
                columns: new[] { "tenant_id", "location_id", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_call_tasks_tenant_id_status_due_at",
                table: "beauty_call_tasks",
                columns: new[] { "tenant_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_call_tasks_open_reminder",
                table: "beauty_call_tasks",
                columns: new[] { "tenant_id", "appointment_id" },
                unique: true,
                filter: "status = 'open' AND reason = 'reminder'");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_call_tasks_reminder",
                table: "beauty_call_tasks",
                columns: new[] { "tenant_id", "reminder_id" },
                unique: true,
                filter: "reminder_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_client_contact_preferences_tenant_id_telegram_conver",
                table: "beauty_client_contact_preferences",
                columns: new[] { "tenant_id", "telegram_conversation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_client_contact_preferences_tenant_id_updated_by_user",
                table: "beauty_client_contact_preferences",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_reminder_settings_tenant_id_updated_by_user_id",
                table: "beauty_reminder_settings",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_conversations_beauty_appointments_tenant_id_pending_",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "pending_prompt_appointment_id" },
                principalTable: "beauty_appointments",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_messages_users_tenant_id_sender_user_id",
                table: "beauty_messages",
                columns: new[] { "tenant_id", "sender_user_id" },
                principalTable: "users",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_reminders_beauty_messages_tenant_id_message_id",
                table: "beauty_reminders",
                columns: new[] { "tenant_id", "message_id" },
                principalTable: "beauty_messages",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // ---- Backfill (§22.3): спадкові записи й нагадування. Адитивно, нічого не видаляється; ідемпотентно. ----
            // Власник таблиць підпадає під FORCE RLS, а app.tenant_id у міграції не задано (UPDATE нічого б не побачив),
            // тому на час backfill вимикаємо FORCE на двох змінюваних таблицях (усе в транзакції міграції, як у TASK-697)
            // і повертаємо одразу після; DO-блок нижче падає, якщо FORCE не повернуто.
            migrationBuilder.Sql("ALTER TABLE beauty_appointments NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_reminders NO FORCE ROW LEVEL SECURITY;");

            // none -> (none, {}) вже дають DEFAULT-и колонок; 1h/2h -> (auto, {60}/{120}). Обидві колонки в одному UPDATE (CHECK-и).
            migrationBuilder.Sql(@"
                UPDATE beauty_appointments
                SET reminder_method = 'auto',
                    reminder_offsets = CASE reminder_option WHEN '1h' THEN ARRAY[60]::smallint[] ELSE ARRAY[120]::smallint[] END
                WHERE reminder_option IN ('1h', '2h') AND reminder_method = 'none';");

            // scheduled-рядки: method='auto', planned_starts_at = starts_at запису.
            migrationBuilder.Sql(@"
                UPDATE beauty_reminders r
                SET method = 'auto', planned_starts_at = a.starts_at
                FROM beauty_appointments a
                WHERE r.status = 'scheduled' AND r.method IS NULL
                  AND a.tenant_id = r.tenant_id AND a.id = r.appointment_id;");

            // offset_minutes береться з reminder_option запису; лише для одного (останнього) scheduled-рядка на запис,
            // щоб частковий UNIQUE ux_beauty_reminders_point не міг впасти на спадкових дублікатах (решта лишається спадковими).
            migrationBuilder.Sql(@"
                WITH pick AS (
                    SELECT DISTINCT ON (r.tenant_id, r.appointment_id)
                           r.tenant_id, r.id,
                           (CASE a.reminder_option WHEN '1h' THEN 60 ELSE 120 END)::smallint AS off
                    FROM beauty_reminders r
                    JOIN beauty_appointments a ON a.tenant_id = r.tenant_id AND a.id = r.appointment_id
                    WHERE r.status = 'scheduled' AND r.offset_minutes IS NULL AND a.reminder_option IN ('1h', '2h')
                    ORDER BY r.tenant_id, r.appointment_id, r.scheduled_at DESC, r.id)
                UPDATE beauty_reminders r
                SET offset_minutes = pick.off
                FROM pick
                WHERE r.tenant_id = pick.tenant_id AND r.id = pick.id;");

            migrationBuilder.Sql("ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_reminders FORCE ROW LEVEL SECURITY;");

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на нових таблицях ----
            foreach (var table in new[] { "beauty_client_contact_preferences", "beauty_call_tasks", "beauty_reminder_settings" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // GRANT (не в міграції: ролі залежать від середовища, див. runbook):
            //   API-роль: SELECT/INSERT/UPDATE на 3 нові таблиці;
            //   роль воркера: SELECT на beauty_client_contact_preferences, beauty_reminder_settings, beauty_locations, beauty_services;
            //                 SELECT/INSERT на beauty_call_tasks; UPDATE на beauty_reminders.

            // Гарантія безпеки: FORCE RLS має діяти на нових і змінюваних таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_client_contact_preferences', 'beauty_call_tasks', 'beauty_reminder_settings',
                             'beauty_appointments', 'beauty_reminders', 'beauty_conversations', 'beauty_messages', 'beauty_clients']
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
                name: "fk_beauty_conversations_beauty_appointments_tenant_id_pending_",
                table: "beauty_conversations");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_messages_users_tenant_id_sender_user_id",
                table: "beauty_messages");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_reminders_beauty_messages_tenant_id_message_id",
                table: "beauty_reminders");

            migrationBuilder.DropTable(
                name: "beauty_call_tasks");

            migrationBuilder.DropTable(
                name: "beauty_client_contact_preferences");

            migrationBuilder.DropTable(
                name: "beauty_reminder_settings");

            migrationBuilder.DropIndex(
                name: "ix_beauty_reminders_tenant_id_message_id",
                table: "beauty_reminders");

            migrationBuilder.DropIndex(
                name: "ux_beauty_reminders_point",
                table: "beauty_reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_reminders_method",
                table: "beauty_reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_reminders_status",
                table: "beauty_reminders");

            migrationBuilder.DropIndex(
                name: "ix_beauty_messages_tenant_id_sender_user_id",
                table: "beauty_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_messages_sender_user",
                table: "beauty_messages");

            migrationBuilder.DropIndex(
                name: "ix_beauty_conversations_awaiting_staff",
                table: "beauty_conversations");

            migrationBuilder.DropIndex(
                name: "ix_beauty_conversations_last_message_desc",
                table: "beauty_conversations");

            migrationBuilder.DropIndex(
                name: "ix_beauty_conversations_tenant_id_pending_prompt_appointment_id",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_pending_prompt_all_or_none",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_reminder_call_single",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_reminder_method",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_reminder_none_empty",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_reminder_offsets",
                table: "beauty_appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_appointments_reminder_option",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "beauty_reminders");

            migrationBuilder.DropColumn(
                name: "method",
                table: "beauty_reminders");

            migrationBuilder.DropColumn(
                name: "offset_minutes",
                table: "beauty_reminders");

            migrationBuilder.DropColumn(
                name: "planned_starts_at",
                table: "beauty_reminders");

            migrationBuilder.DropColumn(
                name: "skip_reason",
                table: "beauty_reminders");

            migrationBuilder.DropColumn(
                name: "sender_user_id",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "awaiting_staff_since",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "handoff_at",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "pending_prompt",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "pending_prompt_appointment_id",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "pending_prompt_expires_at",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "reminders_stop_at",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "staff_last_read_at",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "staff_replied_at",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "reminder_method",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "reminder_offsets",
                table: "beauty_appointments");

            // Відкат: значення, яких старі CHECK-и не знають, приводимо до допустимих (дані плану нагадувань при цьому втрачаються).
            // FORCE знімаємо лише на час UPDATE, як в Up.
            migrationBuilder.Sql("ALTER TABLE beauty_appointments NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_reminders NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE beauty_appointments SET reminder_option = 'none' WHERE reminder_option = 'custom';");
            migrationBuilder.Sql("UPDATE beauty_reminders SET status = 'cancelled' WHERE status = 'skipped';");
            migrationBuilder.Sql("ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_reminders FORCE ROW LEVEL SECURITY;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_reminders_status",
                table: "beauty_reminders",
                sql: "status IN ('scheduled', 'sent', 'failed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_appointments_reminder_option",
                table: "beauty_appointments",
                sql: "reminder_option IN ('none', '1h', '2h')");
        }
    }
}
