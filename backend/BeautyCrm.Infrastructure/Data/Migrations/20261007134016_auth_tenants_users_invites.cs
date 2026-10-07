using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class auth_tenants_users_invites : Migration
    {
        private static readonly string[] TenantTables = { "users", "invites", "refresh_tokens" };

        private const string TenantValue = "NULLIF(current_setting('app.tenant_id', true), '')::uuid";
        private const string TenantPredicate = "tenant_id = " + TenantValue;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    modules = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_slug", "slug ~ '^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$'");
                    table.CheckConstraint("ck_tenants_status", "status IN ('active', 'suspended')");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lockout_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.UniqueConstraint("ak_users_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_users_role", "role IN ('owner', 'admin', 'specialist')");
                    table.ForeignKey(
                        name: "fk_users_beauty_specialists_tenant_id_specialist_id",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invites", x => x.id);
                    table.UniqueConstraint("ak_invites_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_invites_role", "role IN ('admin', 'specialist')");
                    table.ForeignKey(
                        name: "fk_invites_beauty_specialists_tenant_id_specialist_id",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invites_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invites_users_tenant_id_invited_by_user_id",
                        columns: x => new { x.tenant_id, x.invited_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.UniqueConstraint("ak_refresh_tokens_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_refresh_tokens_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invites_tenant_id_email",
                table: "invites",
                columns: new[] { "tenant_id", "email" });

            migrationBuilder.CreateIndex(
                name: "ix_invites_tenant_id_invited_by_user_id",
                table: "invites",
                columns: new[] { "tenant_id", "invited_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invites_tenant_id_specialist_id",
                table: "invites",
                columns: new[] { "tenant_id", "specialist_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invites_tenant_id_token_hash",
                table: "invites",
                columns: new[] { "tenant_id", "token_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_tenant_id_token_hash",
                table: "refresh_tokens",
                columns: new[] { "tenant_id", "token_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_tenant_id_user_id",
                table: "refresh_tokens",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_email",
                table: "users",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_specialist_id",
                table: "users",
                columns: new[] { "tenant_id", "specialist_id" },
                unique: true,
                filter: "specialist_id IS NOT NULL");

            // ---- RLS (як у add_beauty_schema: ENABLE + FORCE, fail-closed без app.tenant_id) ----
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                    CREATE POLICY tenant_isolation ON {table}
                        USING ({TenantPredicate})
                        WITH CHECK ({TenantPredicate});");
            }

            // tenants не має tenant_id: рядок видно лише власному tenant (id = app.tenant_id).
            migrationBuilder.Sql("ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tenants FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"
                CREATE POLICY tenant_self ON tenants
                    USING (id = {TenantValue})
                    WITH CHECK (id = {TenantValue});");
            // Логін: tenant невідомий, є лише slug. SELECT-only політика відкриває один рядок за транзакційним
            // app.tenant_slug (див. EfAuthStore.FindTenantBySlugAsync); решта таблиць лишається повністю ізольованою.
            migrationBuilder.Sql(@"
                CREATE POLICY tenants_login_lookup ON tenants
                    FOR SELECT
                    USING (slug = NULLIF(current_setting('app.tenant_slug', true), ''));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenants_login_lookup ON tenants;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_self ON tenants;");
            foreach (var table in TenantTables)
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");

            migrationBuilder.DropTable(
                name: "invites");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
