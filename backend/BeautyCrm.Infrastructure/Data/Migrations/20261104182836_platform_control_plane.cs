using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class platform_control_plane : Migration
    {
        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "password_hash",
                table: "users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<string>(
                name: "external_ref",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_source",
                table: "tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "suspended_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suspended_reason",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_platform_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    severity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "normal"),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    page_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    delivery_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "pending"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    external_lead_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_platform_feedback", x => x.id);
                    table.UniqueConstraint("ak_beauty_platform_feedback_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_platform_feedback_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_beauty_platform_feedback_delivered", "(delivery_status = 'delivered') = (delivered_at IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_platform_feedback_delivery_status", "delivery_status IN ('pending', 'delivered', 'failed')");
                    table.CheckConstraint("ck_beauty_platform_feedback_lead", "external_lead_id IS NULL OR delivery_status = 'delivered'");
                    table.CheckConstraint("ck_beauty_platform_feedback_message", "char_length(message) BETWEEN 10 AND 2000");
                    table.CheckConstraint("ck_beauty_platform_feedback_severity", "severity IN ('low', 'normal', 'high', 'critical')");
                    table.CheckConstraint("ck_beauty_platform_feedback_type", "type IN ('cooperation', 'improvement', 'bug')");
                    table.ForeignKey(
                        name: "fk_beauty_platform_feedback_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_platform_feedback_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "control_plane_nonces",
                columns: table => new
                {
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_control_plane_nonces", x => new { x.key_id, x.nonce });
                    table.CheckConstraint("ck_control_plane_nonces_key_id", "key_id ~ '^[a-z0-9-]{3,32}$'");
                    table.CheckConstraint("ck_control_plane_nonces_nonce", "nonce ~ '^[A-Za-z0-9_-]{22,64}$'");
                });

            migrationBuilder.CreateTable(
                name: "platform_audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    actor_display = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_audit_log", x => x.id);
                    table.CheckConstraint("ck_platform_audit_log_action", "action IN ('tenant.created', 'tenant.create_replayed', 'tenant.suspended', 'tenant.resumed', 'tenant.modules_changed', 'owner_invite.issued', 'owner_invite.revoked', 'owner_invite.accepted', 'control_auth.failed')");
                    table.CheckConstraint("ck_platform_audit_log_actor_type", "actor_type IN ('shelfguard_admin', 'platform_key', 'owner', 'system')");
                    table.CheckConstraint("ck_platform_audit_log_details", "jsonb_typeof(details) = 'object'");
                });

            migrationBuilder.CreateTable(
                name: "platform_owner_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by_actor = table.Column<string>(type: "character varying(130)", maxLength: 130, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_owner_invites", x => x.id);
                    table.CheckConstraint("ck_platform_owner_invites_expires", "expires_at > created_at");
                    table.CheckConstraint("ck_platform_owner_invites_token_hash", "octet_length(token_hash) = 32");
                    table.CheckConstraint("ck_platform_owner_invites_used_or_revoked", "used_at IS NULL OR revoked_at IS NULL");
                    table.ForeignKey(
                        name: "fk_platform_owner_invites_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_platform_owner_invites_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_password_or_invited",
                table: "users",
                sql: "password_hash IS NOT NULL OR state = 'invited'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_state",
                table: "users",
                sql: "state IN ('active', 'invited')");

            migrationBuilder.CreateIndex(
                name: "ux_tenants_external_ref",
                table: "tenants",
                columns: new[] { "external_source", "external_ref" },
                unique: true,
                filter: "external_ref IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_external_pair",
                table: "tenants",
                sql: "(external_ref IS NULL) = (external_source IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_external_ref",
                table: "tenants",
                sql: "external_ref IS NULL OR external_ref ~ '^[A-Za-z0-9_-]{1,64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_external_source",
                table: "tenants",
                sql: "external_source IS NULL OR external_source IN ('shelfguard')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_suspended",
                table: "tenants",
                sql: "status = 'suspended' OR (suspended_at IS NULL AND suspended_reason IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_platform_feedback_pending",
                table: "beauty_platform_feedback",
                columns: new[] { "tenant_id", "next_attempt_at" },
                filter: "delivery_status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_platform_feedback_tenant_id_user_id_created_at",
                table: "beauty_platform_feedback",
                columns: new[] { "tenant_id", "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_control_plane_nonces_seen_at",
                table: "control_plane_nonces",
                column: "seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_log_occurred_at",
                table: "platform_audit_log",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_log_tenant_id_occurred_at",
                table: "platform_audit_log",
                columns: new[] { "tenant_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "tenant_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_platform_owner_invites_tenant_id_user_id",
                table: "platform_owner_invites",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_owner_invites_token_hash",
                table: "platform_owner_invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_platform_owner_invites_active_user",
                table: "platform_owner_invites",
                column: "user_id",
                unique: true,
                filter: "used_at IS NULL AND revoked_at IS NULL");

            // ---- RLS (ADR-018 §4/§6) ----
            // beauty_platform_feedback - звичайна tenant-таблиця: ENABLE + FORCE, fail-closed без app.tenant_id.
            migrationBuilder.Sql("ALTER TABLE beauty_platform_feedback ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE beauty_platform_feedback FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON beauty_platform_feedback
                    USING ({TenantPredicate})
                    WITH CHECK ({TenantPredicate});");

            // Q5 (звірка з кодом): tenants НЕ платформна таблиця без RLS, а FORCE RLS (tenant_self + tenants_login_lookup).
            // Control plane знає лише external_ref, не tenant_id, тож потрібен такий самий вузький SELECT-only lookup,
            // як для логіну: рядок видно лише за транзакційним app.tenant_external_ref (set_config(..., true)) і лише для 'shelfguard'.
            // Запис, призупинення, зміна модулів - як і раніше, після визначення tenant_id через app.tenant_id (tenant_self).
            migrationBuilder.Sql(@"
                CREATE POLICY tenants_control_lookup ON tenants
                    FOR SELECT
                    USING (external_source = 'shelfguard'
                           AND external_ref = NULLIF(current_setting('app.tenant_external_ref', true), ''));");

            // ---- platform_audit_log: append-only (defense in depth) ----
            // Основний захист - REVOKE UPDATE, DELETE для beautycrm_app (grants.sql / setup-db.ps1). Тригер додатково блокує
            // UPDATE/DELETE/TRUNCATE для будь-якої ролі без BYPASS (зокрема коли GRANT ... ON ALL TABLES видано знову).
            migrationBuilder.Sql(@"
                CREATE FUNCTION platform_audit_log_block_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION 'platform_audit_log is append-only' USING ERRCODE = '42501';
                END
                $fn$;");
            migrationBuilder.Sql(@"
                CREATE TRIGGER platform_audit_log_append_only
                    BEFORE UPDATE OR DELETE ON platform_audit_log
                    FOR EACH ROW EXECUTE FUNCTION platform_audit_log_block_mutation();");
            migrationBuilder.Sql(@"
                CREATE TRIGGER platform_audit_log_no_truncate
                    BEFORE TRUNCATE ON platform_audit_log
                    FOR EACH STATEMENT EXECUTE FUNCTION platform_audit_log_block_mutation();");

            // GRANT (не в міграції: ролі залежать від середовища; deploy/staging/db/grants.sql і scripts/dev/setup-db.ps1):
            //   app: платформні таблиці - SELECT/INSERT/UPDATE/DELETE (за ON ALL TABLES), platform_audit_log - REVOKE UPDATE, DELETE;
            //   worker: beauty_platform_feedback SELECT/UPDATE; control_plane_nonces SELECT/DELETE (очищення); users - лише SELECT
            //           колонок (id, tenant_id, email, full_name, role) для тіла заявки (без password_hash).

            // Гарантія безпеки: FORCE RLS на feedback і не ослаблений на users/tenants; політики й тригери на місці.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_platform_feedback', 'users', 'tenants']
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
            WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
            RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'beauty_platform_feedback' AND policyname = 'tenant_isolation') THEN
        RAISE EXCEPTION 'tenant_isolation policy is missing on beauty_platform_feedback';
    END IF;
    IF (SELECT count(*) FROM pg_trigger WHERE tgrelid = to_regclass('public.platform_audit_log') AND NOT tgisinternal) <> 2 THEN
        RAISE EXCEPTION 'append-only triggers are missing on platform_audit_log';
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // HUMAN GATE (ADR-018 §4): Down втрачає журнал platform_audit_log, запрошення, nonce, заявки та зв'язок external_ref.
            // Відмовляє, якщо є користувачі в стані invited або без пароля: password_hash не можна повернути в NOT NULL без
            // вигаданого значення (порожній хеш = небезпечний). Спершу запросіть/активуйте або видаліть цих користувачів.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- FORCE RLS без app.tenant_id приховує всі рядки (перевірка пройшла б хибно): знімаємо FORCE лише на час перевірки;
    -- при RAISE вся міграція відкочується разом зі зміною.
    ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
    IF EXISTS (SELECT 1 FROM users WHERE password_hash IS NULL OR state = 'invited') THEN
        RAISE EXCEPTION 'Down refused: users in state invited (password_hash IS NULL) exist; resolve them first (human gate)';
    END IF;
    ALTER TABLE users FORCE ROW LEVEL SECURITY;
END
$$;");

            migrationBuilder.Sql("DROP POLICY IF EXISTS tenants_control_lookup ON tenants;");

            migrationBuilder.DropTable(
                name: "beauty_platform_feedback");

            migrationBuilder.DropTable(
                name: "control_plane_nonces");

            migrationBuilder.DropTable(
                name: "platform_audit_log");

            migrationBuilder.DropTable(
                name: "platform_owner_invites");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_password_or_invited",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_state",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ux_tenants_external_ref",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_external_pair",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_external_ref",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_external_source",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_suspended",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "state",
                table: "users");

            migrationBuilder.DropColumn(
                name: "external_ref",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "external_source",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "suspended_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "suspended_reason",
                table: "tenants");

            migrationBuilder.AlterColumn<string>(
                name: "password_hash",
                table: "users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform_audit_log_block_mutation();");
        }
    }
}
