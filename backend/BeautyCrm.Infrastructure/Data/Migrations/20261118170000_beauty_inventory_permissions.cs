using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// ADR-025 / контракт §32.2, TASK-877. Лише дані (схеми не змінює, знімок моделі без змін), PermissionCatalog.Version 1 -> 2.
    /// Backfill ключів inventory.view / inventory.manage / inventory.costs за пресетом ролі:
    ///   user_permissions для admin, deputy, specialist (owner - неявно, рядків немає), ON CONFLICT DO NOTHING;
    ///   inventory.costs = пресет лише коли поточний revenue.view користувача = 'all', інакше 'none' (Q9);
    ///   invites.permissions: додаються лише відсутні ключі inventory.* (jsonb ||, наявні значення не перезаписуються);
    ///   users.access_version + 1 для користувачів, яким вставлено >= 1 рядок; user_access_audit action 'migrated' (actor NULL).
    ///   Ідемпотентна: повторне виконання нічого не вставляє, не змінює версій і не дублює журнал.
    /// Down видаляє рядки inventory.* і ключі зі знімків запрошень; ВІДМОВЛЯЄ (RAISE EXCEPTION), якщо будь-який рядок inventory.*
    /// змінено вручну (updated_by_user_id IS NOT NULL): втрата налаштувань має бути свідомою (human gate). Журнал user_access_audit
    /// append-only, тож записи 'migrated' залишаються.
    /// </summary>
    [DbContext(typeof(BeautyDbContext))]
    [Migration("20261118170000_beauty_inventory_permissions")]
    public class beauty_inventory_permissions : Migration
    {
        private const string Presets = """
            presets(permission, admin_level, deputy_level, specialist_level) AS (VALUES
                ('inventory.view', 'all', 'all', 'own'),
                ('inventory.manage', 'all', 'all', 'none'),
                ('inventory.costs', 'edit', 'none', 'none'))
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FORCE RLS без app.tenant_id приховує/блокує рядки: знімаємо лише на час цієї транзакції (прецедент auth_user_permissions).
            migrationBuilder.Sql($$"""
                ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE invites NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE user_permissions NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE user_access_audit NO FORCE ROW LEVEL SECURITY;

                WITH {{Presets}},
                ins AS (
                    INSERT INTO user_permissions (tenant_id, user_id, permission, level, updated_by_user_id)
                    SELECT u.tenant_id, u.id, p.permission,
                           CASE WHEN p.permission = 'inventory.costs' AND COALESCE(rv.level, 'none') <> 'all' THEN 'none'
                                ELSE CASE u.role WHEN 'admin' THEN p.admin_level WHEN 'deputy' THEN p.deputy_level ELSE p.specialist_level END
                           END,
                           NULL
                    FROM users u
                    CROSS JOIN presets p
                    LEFT JOIN user_permissions rv
                           ON rv.tenant_id = u.tenant_id AND rv.user_id = u.id AND rv.permission = 'revenue.view'
                    WHERE u.role IN ('admin', 'deputy', 'specialist')
                    ON CONFLICT (tenant_id, user_id, permission) DO NOTHING
                    RETURNING tenant_id, user_id, permission, level
                ),
                aud AS (
                    INSERT INTO user_access_audit (tenant_id, actor_user_id, target_user_id, action, changes)
                    SELECT tenant_id, NULL, user_id, 'migrated',
                           jsonb_agg(jsonb_build_object('permission', permission, 'from', NULL::text, 'to', level) ORDER BY permission)
                    FROM ins
                    GROUP BY tenant_id, user_id
                    RETURNING tenant_id, target_user_id
                )
                UPDATE users u SET access_version = u.access_version + 1
                FROM aud WHERE u.tenant_id = aud.tenant_id AND u.id = aud.target_user_id;

                -- знімки запрошень: лише відсутні ключі inventory.* (наявні значення виграють у ||)
                WITH {{Presets}}
                UPDATE invites i
                   SET permissions = (
                        SELECT jsonb_object_agg(p.permission,
                                   CASE WHEN p.permission = 'inventory.costs' AND COALESCE(i.permissions ->> 'revenue.view', 'none') <> 'all' THEN 'none'
                                        ELSE CASE i.role WHEN 'admin' THEN p.admin_level WHEN 'deputy' THEN p.deputy_level ELSE p.specialist_level END
                                   END)
                        FROM presets p WHERE NOT (i.permissions ? p.permission)) || i.permissions
                 WHERE i.role IN ('admin', 'deputy', 'specialist')
                   AND EXISTS (SELECT 1 FROM presets p WHERE NOT (i.permissions ? p.permission));

                ALTER TABLE users FORCE ROW LEVEL SECURITY;
                ALTER TABLE invites FORCE ROW LEVEL SECURITY;
                ALTER TABLE user_permissions FORCE ROW LEVEL SECURITY;
                ALTER TABLE user_access_audit FORCE ROW LEVEL SECURITY;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // HUMAN GATE: ручно змінені права складу не губимо мовчки.
            migrationBuilder.Sql("""
                ALTER TABLE user_permissions NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE invites NO FORCE ROW LEVEL SECURITY;
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM user_permissions WHERE permission IN ('inventory.view', 'inventory.manage', 'inventory.costs')
                                                              AND updated_by_user_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Down refused: inventory.* permissions were edited manually (ADR-025); export or reset them first (human gate)';
                    END IF;
                END
                $do$;
                DELETE FROM user_permissions WHERE permission IN ('inventory.view', 'inventory.manage', 'inventory.costs');
                UPDATE invites SET permissions = permissions - 'inventory.view' - 'inventory.manage' - 'inventory.costs'
                 WHERE permissions ?| ARRAY['inventory.view', 'inventory.manage', 'inventory.costs'];
                ALTER TABLE user_permissions FORCE ROW LEVEL SECURITY;
                ALTER TABLE invites FORCE ROW LEVEL SECURITY;
                """);
        }
    }
}
