using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// ADR-025 §12 / контракт §32.24.2, TASK-895. Склад, етап 2b (Telegram-сповіщення власнику): beauty_staff_telegram_links (issued -> claimed ->
    /// active -> revoked; CHECK форми кожного стану, часткові UNIQUE), beauty_staff_telegram_link_events (append-only: тригер + REVOKE у GRANT-ах),
    /// beauty_staff_notifications (службова черга), beauty_staff_notification_alerts. Адитивна: лише 4 нові таблиці, колонки beauty_staff_alerts
    /// і users не змінюються (users уже має AK (tenant_id, id) для композитного FK). Згенерована `migrations add`; ручні частини: RLS ENABLE +
    /// FORCE + tenant_isolation (USING + WITH CHECK, ADR-002), перевірочний DO-блок, тригери append-only журналу, Down з відмовою.
    /// chat_id лише зашифрований (формат ISecretProtector) + HMAC-хеш; username/ім'я/телефон не зберігаються.
    /// GRANT-и (у т.ч. роль воркера) - поза міграцією: scripts/dev/setup-db.ps1 і deploy/staging/db/grants.sql.
    /// Down ВІДМОВЛЯЄ (RAISE EXCEPTION 'inventory stage2b history exists'), якщо є прив'язка зі станом, відмінним від issued, або подія журналу.
    /// </summary>
    public partial class beauty_inventory_stage2b : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "beauty_staff_telegram_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "issued"),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    chat_id_encrypted = table.Column<string>(type: "text", nullable: true),
                    chat_id_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    consent_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    consent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    bot_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    last_delivery_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_staff_telegram_links", x => x.id);
                    table.UniqueConstraint("ak_beauty_staff_telegram_links_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_staff_telegram_links_chat", "(chat_id_encrypted IS NULL) = (chat_id_hash IS NULL)");
                    table.CheckConstraint("ck_beauty_staff_telegram_links_chat_hash", "chat_id_hash IS NULL OR octet_length(chat_id_hash) = 32");
                    table.CheckConstraint("ck_beauty_staff_telegram_links_consent", "consent_version ~ '^inv-tg-v[0-9]+$'");
                    table.CheckConstraint("ck_beauty_staff_telegram_links_revoke_reason", "(revoke_reason IS NULL) = (status <> 'revoked') AND (revoke_reason IS NULL OR revoke_reason IN ('user_unlink', 'bot_stop', 'declined', 'replaced', 'expired', 'bot_blocked', 'chat_not_found', 'user_deactivated', 'role_changed', 'tenant_inactive'))");
                    table.CheckConstraint("ck_beauty_staff_telegram_links_shape", "(status = 'issued' AND token_hash IS NOT NULL AND token_expires_at IS NOT NULL AND chat_id_encrypted IS NULL AND chat_id_hash IS NULL AND claimed_at IS NULL AND bot_confirmed_at IS NULL AND activated_at IS NULL AND revoked_at IS NULL AND last_delivery_at IS NULL) OR (status = 'claimed' AND token_hash IS NOT NULL AND token_expires_at IS NOT NULL AND chat_id_encrypted IS NOT NULL AND chat_id_hash IS NOT NULL AND claimed_at IS NOT NULL AND bot_confirmed_at IS NULL AND activated_at IS NULL AND revoked_at IS NULL AND last_delivery_at IS NULL) OR (status = 'active' AND token_hash IS NULL AND chat_id_encrypted IS NOT NULL AND chat_id_hash IS NOT NULL AND bot_confirmed_at IS NOT NULL AND activated_at IS NOT NULL AND revoked_at IS NULL) OR (status = 'revoked' AND token_hash IS NULL AND chat_id_encrypted IS NULL AND chat_id_hash IS NULL AND revoked_at IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_staff_telegram_links_status", "status IN ('issued', 'claimed', 'active', 'revoked')");
                    table.CheckConstraint("ck_beauty_staff_telegram_links_token_hash", "token_hash IS NULL OR octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_beauty_staff_telegram_links_beauty_channels_tenant_id_chann",
                        columns: x => new { x.tenant_id, x.channel_id },
                        principalTable: "beauty_channels",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_telegram_links_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_staff_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "pending"),
                    attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_error = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    external_message_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_staff_notifications", x => x.id);
                    table.UniqueConstraint("ak_beauty_staff_notifications_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_staff_notifications_attempts", "attempts BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_beauty_staff_notifications_body", "char_length(body) BETWEEN 1 AND 4096");
                    table.CheckConstraint("ck_beauty_staff_notifications_kind", "kind IN ('alert', 'digest')");
                    table.CheckConstraint("ck_beauty_staff_notifications_sent", "(status = 'sent') = (sent_at IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_staff_notifications_status", "status IN ('pending', 'sending', 'sent', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_beauty_staff_notifications_beauty_staff_telegram_links_tena",
                        columns: x => new { x.tenant_id, x.link_id },
                        principalTable: "beauty_staff_telegram_links",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_staff_telegram_link_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    actor = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_staff_telegram_link_events", x => x.id);
                    table.UniqueConstraint("ak_beauty_staff_telegram_link_events_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_staff_telegram_link_events_actor", "actor IN ('user', 'bot', 'system')");
                    table.CheckConstraint("ck_beauty_staff_telegram_link_events_event", "event IN ('issued', 'claimed', 'activated', 'declined', 'revoked', 'rejected')");
                    table.CheckConstraint("ck_beauty_staff_telegram_link_events_reason", "(reason IS NULL OR reason IN ('user_unlink', 'bot_stop', 'declined', 'replaced', 'expired', 'bot_blocked', 'chat_not_found', 'user_deactivated', 'role_changed', 'tenant_inactive', 'expired', 'used', 'channel_mismatch', 'chat_conflict', 'user_inactive', 'disabled')) AND (event NOT IN ('revoked', 'rejected') OR reason IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_beauty_staff_telegram_link_events_beauty_staff_telegram_lin",
                        columns: x => new { x.tenant_id, x.link_id },
                        principalTable: "beauty_staff_telegram_links",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_telegram_link_events_users_tenant_id_actor_use",
                        columns: x => new { x.tenant_id, x.actor_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_telegram_link_events_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_staff_notification_alerts",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_staff_notification_alerts", x => new { x.tenant_id, x.notification_id, x.alert_id });
                    table.ForeignKey(
                        name: "fk_beauty_staff_notification_alerts_beauty_staff_alerts_tenant",
                        columns: x => new { x.tenant_id, x.alert_id },
                        principalTable: "beauty_staff_alerts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_staff_notification_alerts_beauty_staff_notifications",
                        columns: x => new { x.tenant_id, x.notification_id },
                        principalTable: "beauty_staff_notifications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_staff_notification_alerts_beauty_staff_telegram_link",
                        columns: x => new { x.tenant_id, x.link_id },
                        principalTable: "beauty_staff_telegram_links",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_notification_alerts_alert",
                table: "beauty_staff_notification_alerts",
                columns: new[] { "tenant_id", "alert_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_staff_notification_alerts_link_alert",
                table: "beauty_staff_notification_alerts",
                columns: new[] { "tenant_id", "link_id", "alert_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_notifications_due",
                table: "beauty_staff_notifications",
                columns: new[] { "tenant_id", "status", "next_attempt_at" },
                filter: "status IN ('pending', 'sending')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_notifications_link",
                table: "beauty_staff_notifications",
                columns: new[] { "tenant_id", "link_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_staff_notifications_dedupe",
                table: "beauty_staff_notifications",
                columns: new[] { "tenant_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_telegram_link_events_actor",
                table: "beauty_staff_telegram_link_events",
                columns: new[] { "tenant_id", "actor_user_id" },
                filter: "actor_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_telegram_link_events_link",
                table: "beauty_staff_telegram_link_events",
                columns: new[] { "tenant_id", "link_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_telegram_link_events_user",
                table: "beauty_staff_telegram_link_events",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_telegram_links_channel",
                table: "beauty_staff_telegram_links",
                columns: new[] { "tenant_id", "channel_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_telegram_links_expiry",
                table: "beauty_staff_telegram_links",
                columns: new[] { "tenant_id", "status", "token_expires_at" },
                filter: "status IN ('issued', 'claimed')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_staff_telegram_links_user_created",
                table: "beauty_staff_telegram_links",
                columns: new[] { "tenant_id", "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_staff_telegram_links_chat_open",
                table: "beauty_staff_telegram_links",
                columns: new[] { "tenant_id", "channel_id", "chat_id_hash" },
                unique: true,
                filter: "status IN ('claimed', 'active')");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_staff_telegram_links_token",
                table: "beauty_staff_telegram_links",
                columns: new[] { "tenant_id", "token_hash" },
                unique: true,
                filter: "token_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_staff_telegram_links_user_open",
                table: "beauty_staff_telegram_links",
                columns: new[] { "tenant_id", "user_id" },
                unique: true,
                filter: "status IN ('issued', 'claimed', 'active')");

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на 4 нових таблицях ----
            foreach (var table in Stage2bTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // Гарантія безпеки: FORCE RLS має діяти на всіх нових таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_staff_telegram_links', 'beauty_staff_telegram_link_events', 'beauty_staff_notifications', 'beauty_staff_notification_alerts']
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class WHERE oid = to_regclass('public.' || t) AND relrowsecurity AND relforcerowsecurity
        ) THEN
            RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
        END IF;
    END LOOP;
END
$$;");

            // Append-only журнал подій прив'язки на рівні БД (патерн beauty_stock_journal_block_mutation): тригер діє й на власника, не лише REVOKE.
            // INSERT не зачіпається; DROP TABLE (Down) тригери не виконує. Свідоме очищення - human gate: ALTER TABLE ... DISABLE TRIGGER.
            migrationBuilder.Sql(@"
CREATE FUNCTION beauty_staff_telegram_events_block_mutation() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = '42501';
END
$fn$;
CREATE TRIGGER beauty_staff_telegram_link_events_append_only BEFORE UPDATE OR DELETE ON beauty_staff_telegram_link_events
    FOR EACH ROW EXECUTE FUNCTION beauty_staff_telegram_events_block_mutation();
CREATE TRIGGER beauty_staff_telegram_link_events_no_truncate BEFORE TRUNCATE ON beauty_staff_telegram_link_events
    FOR EACH STATEMENT EXECUTE FUNCTION beauty_staff_telegram_events_block_mutation();");
        }

        private static readonly string[] Stage2bTables =
        [
            "beauty_staff_telegram_links", "beauty_staff_telegram_link_events", "beauty_staff_notifications", "beauty_staff_notification_alerts",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Відкат знищує журнал прив'язок: непорожня історія 2b (прив'язка зі станом, відмінним від issued, або подія) блокує відкат (human gate).
            // FORCE RLS приховав би рядки від власника без app.tenant_id: на час перевірки знімаємо; при RAISE вся міграція відкочується,
            // інакше FORCE повертається (таблиці далі видаляються).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    ALTER TABLE beauty_staff_telegram_links NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_staff_telegram_link_events NO FORCE ROW LEVEL SECURITY;
    IF EXISTS (SELECT 1 FROM beauty_staff_telegram_links WHERE status <> 'issued')
       OR EXISTS (SELECT 1 FROM beauty_staff_telegram_link_events) THEN
        RAISE EXCEPTION 'inventory stage2b history exists';
    END IF;
    ALTER TABLE beauty_staff_telegram_links FORCE ROW LEVEL SECURITY;
    ALTER TABLE beauty_staff_telegram_link_events FORCE ROW LEVEL SECURITY;
END
$$;");

            migrationBuilder.DropTable(
                name: "beauty_staff_notification_alerts");

            migrationBuilder.DropTable(
                name: "beauty_staff_telegram_link_events");

            migrationBuilder.DropTable(
                name: "beauty_staff_notifications");

            migrationBuilder.DropTable(
                name: "beauty_staff_telegram_links");

            migrationBuilder.Sql("DROP FUNCTION beauty_staff_telegram_events_block_mutation();");
        }
    }
}
