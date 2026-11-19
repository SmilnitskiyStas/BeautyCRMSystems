using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_ai_settings_and_usage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "beauty_ai_provider_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key_encrypted = table.Column<string>(type: "text", nullable: false),
                    key_kid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_ai_provider_keys", x => x.id);
                    table.UniqueConstraint("ak_beauty_ai_provider_keys_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_ai_provider_keys_key_encrypted", "key_encrypted LIKE 'v2:%'");
                    table.CheckConstraint("ck_beauty_ai_provider_keys_key_kid", "key_kid ~ '^[a-z0-9-]{1,32}$'");
                    table.CheckConstraint("ck_beauty_ai_provider_keys_provider", "provider ~ '^[a-z][a-z0-9_]{1,31}$'");
                    table.ForeignKey(
                        name: "fk_beauty_ai_provider_keys_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_ai_provider_keys_users_tenant_id_updated_by_user_id",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_ai_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    key_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "platform"),
                    chat_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "anthropic"),
                    chat_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    analytics_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "anthropic"),
                    analytics_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "confirm"),
                    prompt_addendum = table.Column<string>(type: "text", nullable: true),
                    monthly_token_limit = table.Column<long>(type: "bigint", nullable: true),
                    monthly_request_limit = table.Column<int>(type: "integer", nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 1024),
                    platform_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    platform_monthly_token_quota = table.Column<long>(type: "bigint", nullable: true),
                    platform_monthly_request_quota = table.Column<int>(type: "integer", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_ai_settings", x => x.id);
                    table.UniqueConstraint("ak_beauty_ai_settings_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_ai_settings_analytics_model", "char_length(analytics_model) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_beauty_ai_settings_analytics_provider", "analytics_provider ~ '^[a-z][a-z0-9_]{1,31}$'");
                    table.CheckConstraint("ck_beauty_ai_settings_chat_model", "char_length(chat_model) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_beauty_ai_settings_chat_provider", "chat_provider ~ '^[a-z][a-z0-9_]{1,31}$'");
                    table.CheckConstraint("ck_beauty_ai_settings_key_source", "key_source IN ('own', 'platform')");
                    table.CheckConstraint("ck_beauty_ai_settings_max_output_tokens", "max_output_tokens BETWEEN 64 AND 8192");
                    table.CheckConstraint("ck_beauty_ai_settings_mode", "mode IN ('suggest', 'confirm', 'auto')");
                    table.CheckConstraint("ck_beauty_ai_settings_monthly_request_limit", "monthly_request_limit IS NULL OR monthly_request_limit BETWEEN 1 AND 10000000");
                    table.CheckConstraint("ck_beauty_ai_settings_monthly_token_limit", "monthly_token_limit IS NULL OR monthly_token_limit BETWEEN 1 AND 100000000000");
                    table.CheckConstraint("ck_beauty_ai_settings_platform_quotas", "(platform_monthly_token_quota IS NULL OR platform_monthly_token_quota >= 0) AND (platform_monthly_request_quota IS NULL OR platform_monthly_request_quota >= 0)");
                    table.CheckConstraint("ck_beauty_ai_settings_prompt_addendum", "prompt_addendum IS NULL OR char_length(prompt_addendum) <= 4000");
                    table.ForeignKey(
                        name: "fk_beauty_ai_settings_users_tenant_id_updated_by_user_id",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_ai_settings_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    changed_fields = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_ai_settings_audit", x => x.id);
                    table.UniqueConstraint("ak_beauty_ai_settings_audit_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_ai_settings_audit_action", "action IN ('settings_updated', 'key_source_changed', 'key_set', 'key_replaced', 'key_deleted', 'key_tested', 'prompt_reset', 'platform_quota_changed')");
                    table.CheckConstraint("ck_beauty_ai_settings_audit_provider", "provider IS NULL OR provider ~ '^[a-z][a-z0-9_]{1,31}$'");
                    table.ForeignKey(
                        name: "fk_beauty_ai_settings_audit_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_ai_usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    error_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_ai_usage", x => x.id);
                    table.UniqueConstraint("ak_beauty_ai_usage_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_ai_usage_counters", "request_count >= 0 AND error_count >= 0 AND input_tokens >= 0 AND output_tokens >= 0");
                    table.CheckConstraint("ck_beauty_ai_usage_key_source", "key_source IN ('own', 'platform')");
                    table.CheckConstraint("ck_beauty_ai_usage_period_start", "extract(day from period_start) = 1");
                    table.CheckConstraint("ck_beauty_ai_usage_provider", "provider ~ '^[a-z][a-z0-9_]{1,31}$'");
                    table.CheckConstraint("ck_beauty_ai_usage_role", "role IN ('chat', 'analytics')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_provider_keys_key_kid",
                table: "beauty_ai_provider_keys",
                column: "key_kid");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_provider_keys_tenant_id_created_by_user_id",
                table: "beauty_ai_provider_keys",
                columns: new[] { "tenant_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_provider_keys_tenant_id_provider",
                table: "beauty_ai_provider_keys",
                columns: new[] { "tenant_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_provider_keys_tenant_id_updated_by_user_id",
                table: "beauty_ai_provider_keys",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_settings_tenant_id",
                table: "beauty_ai_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_settings_tenant_id_updated_by_user_id",
                table: "beauty_ai_settings",
                columns: new[] { "tenant_id", "updated_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_settings_audit_tenant_id_created_at",
                table: "beauty_ai_settings_audit",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_settings_audit_tenant_id_user_id",
                table: "beauty_ai_settings_audit",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_usage_tenant_id_period_start_role_provider_model_",
                table: "beauty_ai_usage",
                columns: new[] { "tenant_id", "period_start", "role", "provider", "model", "key_source" },
                unique: true);

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на всіх чотирьох таблицях ----
            foreach (var table in new[] { "beauty_ai_settings", "beauty_ai_provider_keys", "beauty_ai_usage", "beauty_ai_settings_audit" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // GRANT runtime-ролі (не в міграції: роль залежить від середовища, див. runbook):
            //   SELECT/INSERT/UPDATE/DELETE на beauty_ai_settings, beauty_ai_provider_keys, beauty_ai_usage;
            //   лише SELECT/INSERT на beauty_ai_settings_audit (append-only).

            // Гарантія безпеки: FORCE RLS має діяти на всіх нових таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beauty_ai_settings', 'beauty_ai_provider_keys', 'beauty_ai_usage', 'beauty_ai_settings_audit']
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
                name: "beauty_ai_provider_keys");

            migrationBuilder.DropTable(
                name: "beauty_ai_settings");

            migrationBuilder.DropTable(
                name: "beauty_ai_settings_audit");

            migrationBuilder.DropTable(
                name: "beauty_ai_usage");
        }
    }
}
