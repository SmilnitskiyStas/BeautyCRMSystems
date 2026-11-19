using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// ADR-023 / контракт §30, TASK-849. Написана вручну (як beauty_reminder_24h_email_channel; знімок моделі оновлено через
    /// `migrations add`, згенерований Designer прибрано). Зміст:
    ///   users.access_version (integer NOT NULL DEFAULT 1) і розширення ck_users_role: + 'deputy';
    ///   invites.permissions jsonb NOT NULL (повний знімок прав, B1; наявні запрошення отримують пресет своєї ролі; DEFAULT '{}' до TASK-851;
    ///     ck_invites_permissions: об'єкт) і ck_invites_role: + 'deputy';
    ///   beauty_appointments.created_by_user_id uuid NULL (композитний FK на users RESTRICT, частковий індекс): для виключення
    ///     власноруч створених і скасованих записів зі scope own (§30.2, M3); наявні рядки NULL;
    ///   purge_user_access_audit(timestamptz) SECURITY DEFINER (власник - NOLOGIN-роль beautycrm_audit_purge, потребує створення роллю з
    ///     CREATEROLE або ops заздалегідь, див. init-catalog-role.sql): єдиний шлях DELETE з журналу, відмовляє для p_before молодших за
    ///     3 роки; тригер append-only пропускає лише цей DELETE; EXECUTE лише ролі воркера (grants), TRUNCATE ніхто;
    ///   user_permissions: PK (tenant_id, user_id, permission), композитний FK на users (CASCADE) і на автора зміни (RESTRICT),
    ///     CHECK лише формату ключа/рівня (валідність за каталогом перевіряє Application), RLS ENABLE + FORCE + tenant_isolation;
    ///   user_access_audit (append-only: тригер на UPDATE/DELETE/TRUNCATE + REVOKE у grants): target_user_id NULL-able + invite_id
    ///     (Q-C2, CHECK «цільовий користувач або запрошення»), дії + invite_accepted, композитні FK, RLS FORCE;
    ///   backfill пресетів admin/specialist (§30.3): ON CONFLICT DO NOTHING; `migrated` в журнал лише за фактично вставлені рядки,
    ///     тож повторне виконання нічого не змінює й не дублює журнал. Для owner рядків немає (права неявні).
    /// UNIQUE (tenant_id, id) на users/invites уже є (ak_users_tenant_id_id, ak_invites_tenant_id_id).
    /// Down ВІДМОВЛЯЄ (RAISE EXCEPTION), якщо є користувачі deputy, запрошення deputy/з перевизначеннями прав або права, що
    /// відрізняються від пресетів (admin/specialist), чи права для owner/невідомих ключів: втрата налаштувань має бути свідомою
    /// (human gate на staging/prod, runbook відкату). GRANT-и не в міграції (ролі залежать від середовища): deploy/staging/db/grants.sql
    /// і scripts/dev/setup-db.ps1.
    /// </summary>
    [DbContext(typeof(BeautyDbContext))]
    [Migration("20261115120000_auth_user_permissions")]
    public class auth_user_permissions : Migration
    {
        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        // Пресети admin/specialist з §30.3 (owner неявний, deputy - лише для нових користувачів, не для backfill).
        // Рівні, що відповідають найнижчому рівню ключа (none/masked/view/own), теж зберігаються явно: що в БД, те й діє.
        private const string Presets = """
            presets(permission, admin_level, specialist_level) AS (VALUES
                ('calendar.view', 'all', 'own'),
                ('calendar.manage', 'all', 'own'),
                ('clients.view', 'all', 'none'),
                ('clients.edit', 'all', 'none'),
                ('clients.contacts', 'full', 'masked'),
                ('clients.calls', 'all', 'none'),
                ('revenue.view', 'all', 'none'),
                ('revenue.costs', 'edit', 'none'),
                ('channels.inbox', 'reply', 'none'),
                ('channels.settings', 'edit', 'none'),
                ('ai.use', 'use', 'none'),
                ('ai.settings', 'edit', 'none'),
                ('services.catalog', 'edit', 'view'),
                ('marketing.campaigns', 'edit', 'none'),
                ('staff.access', 'edit', 'own'),
                ('locations.access', 'edit', 'own'),
                ('settings.general', 'view', 'none'),
                ('team.manage', 'manage', 'none'))
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                -- users / invites
                ALTER TABLE users ADD COLUMN access_version integer NOT NULL DEFAULT 1;
                ALTER TABLE users DROP CONSTRAINT IF EXISTS ck_users_role;
                ALTER TABLE users ADD CONSTRAINT ck_users_role CHECK (role IN ('owner', 'admin', 'deputy', 'specialist'));
                ALTER TABLE invites ADD COLUMN permissions jsonb NULL; -- стане NOT NULL після backfill нижче
                ALTER TABLE invites DROP CONSTRAINT IF EXISTS ck_invites_role;
                ALTER TABLE invites ADD CONSTRAINT ck_invites_role CHECK (role IN ('admin', 'deputy', 'specialist'));

                -- хто створив запис (ADR-023 §30.2: виключає власноруч створені й скасовані записи зі scope own); наявні рядки NULL
                ALTER TABLE beauty_appointments ADD COLUMN created_by_user_id uuid NULL;
                ALTER TABLE beauty_appointments ADD CONSTRAINT fk_beauty_appointments_users_tenant_id_created_by_user_id
                    FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT;
                CREATE INDEX ix_beauty_appointments_tenant_id_created_by_user_id ON beauty_appointments (tenant_id, created_by_user_id)
                    WHERE created_by_user_id IS NOT NULL;

                -- user_permissions
                CREATE TABLE user_permissions (
                    tenant_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    permission text NOT NULL,
                    level text NOT NULL,
                    updated_at timestamp with time zone NOT NULL DEFAULT now(),
                    updated_by_user_id uuid NULL,
                    CONSTRAINT pk_user_permissions PRIMARY KEY (tenant_id, user_id, permission),
                    CONSTRAINT ck_user_permissions_permission CHECK (permission ~ '^[a-z_]{1,32}\.[a-z_]{1,32}$'),
                    CONSTRAINT ck_user_permissions_level CHECK (level ~ '^[a-z_]{1,16}$'),
                    CONSTRAINT fk_user_permissions_users_tenant_id_user_id FOREIGN KEY (tenant_id, user_id)
                        REFERENCES users (tenant_id, id) ON DELETE CASCADE,
                    CONSTRAINT fk_user_permissions_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id)
                        REFERENCES users (tenant_id, id) ON DELETE RESTRICT
                );
                CREATE INDEX ix_user_permissions_tenant_id_updated_by_user_id ON user_permissions (tenant_id, updated_by_user_id);

                -- user_access_audit (append-only)
                CREATE TABLE user_access_audit (
                    id uuid NOT NULL DEFAULT gen_random_uuid(),
                    tenant_id uuid NOT NULL,
                    actor_user_id uuid NULL,
                    target_user_id uuid NULL,
                    invite_id uuid NULL,
                    action character varying(32) NOT NULL,
                    changes jsonb NOT NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT pk_user_access_audit PRIMARY KEY (id),
                    CONSTRAINT ck_user_access_audit_action CHECK (action IN ('permissions_changed', 'role_changed', 'preset_reset', 'invited', 'invite_revoked', 'invite_accepted', 'activated', 'deactivated', 'migrated')),
                    CONSTRAINT ck_user_access_audit_changes CHECK (jsonb_typeof(changes) IN ('array', 'object')),
                    CONSTRAINT ck_user_access_audit_target_or_invite CHECK (target_user_id IS NOT NULL OR invite_id IS NOT NULL),
                    CONSTRAINT fk_user_access_audit_users_tenant_id_actor_user_id FOREIGN KEY (tenant_id, actor_user_id)
                        REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
                    CONSTRAINT fk_user_access_audit_users_tenant_id_target_user_id FOREIGN KEY (tenant_id, target_user_id)
                        REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
                    CONSTRAINT fk_user_access_audit_invites_tenant_id_invite_id FOREIGN KEY (tenant_id, invite_id)
                        REFERENCES invites (tenant_id, id) ON DELETE RESTRICT
                );
                CREATE INDEX ix_user_access_audit_tenant_id_actor_user_id ON user_access_audit (tenant_id, actor_user_id);
                CREATE INDEX ix_user_access_audit_tenant_id_created_at_id ON user_access_audit (tenant_id, created_at DESC, id DESC);
                CREATE INDEX ix_user_access_audit_tenant_id_invite_id_created_at ON user_access_audit (tenant_id, invite_id, created_at DESC)
                    WHERE invite_id IS NOT NULL;
                CREATE INDEX ix_user_access_audit_tenant_id_target_user_id_created_at ON user_access_audit (tenant_id, target_user_id, created_at DESC);

                -- append-only (defense in depth): основний захист - REVOKE UPDATE, DELETE для beautycrm_app (grants.sql / setup-db.ps1)
                -- Єдиний виняток: рядковий DELETE, що виконує власник purge_user_access_audit (current_user всередині SECURITY DEFINER-функції).
                -- Роль застосунку не може стати цим користувачем (немає членства), тож обійти тригер неможливо.
                CREATE FUNCTION user_access_audit_block_mutation() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
                BEGIN
                    IF TG_OP = 'DELETE' AND TG_LEVEL = 'ROW' AND current_user = 'beautycrm_audit_purge' THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'user_access_audit is append-only' USING ERRCODE = '42501';
                END
                $fn$;
                CREATE TRIGGER user_access_audit_append_only
                    BEFORE UPDATE OR DELETE ON user_access_audit
                    FOR EACH ROW EXECUTE FUNCTION user_access_audit_block_mutation();
                CREATE TRIGGER user_access_audit_no_truncate
                    BEFORE TRUNCATE ON user_access_audit
                    FOR EACH STATEMENT EXECUTE FUNCTION user_access_audit_block_mutation();

                -- backfill (до ENABLE RLS на нових таблицях). FORCE RLS на users без app.tenant_id приховує всі рядки: знімаємо
                -- FORCE лише на час читання в цій транзакції (прецедент: platform_control_plane Down).
                ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE invites NO FORCE ROW LEVEL SECURITY;
                WITH {{Presets}},
                ins AS (
                    INSERT INTO user_permissions (tenant_id, user_id, permission, level, updated_by_user_id)
                    SELECT u.tenant_id, u.id, p.permission,
                           CASE u.role WHEN 'admin' THEN p.admin_level ELSE p.specialist_level END, NULL
                    FROM users u CROSS JOIN presets p
                    WHERE u.role IN ('admin', 'specialist')
                    ON CONFLICT (tenant_id, user_id, permission) DO NOTHING
                    RETURNING tenant_id, user_id, permission, level
                )
                INSERT INTO user_access_audit (tenant_id, actor_user_id, target_user_id, action, changes)
                SELECT tenant_id, NULL, user_id, 'migrated',
                       jsonb_agg(jsonb_build_object('permission', permission, 'from', NULL::text, 'to', level) ORDER BY permission)
                FROM ins
                GROUP BY tenant_id, user_id;
                -- наявні запрошення (pending, прийняті, відкликані): повний знімок = пресет ролі (§30.3); ідемпотентно (лише NULL)
                WITH {{Presets}}
                UPDATE invites i
                   SET permissions = (SELECT jsonb_object_agg(p.permission,
                                             CASE i.role WHEN 'admin' THEN p.admin_level ELSE p.specialist_level END)
                                      FROM presets p)
                 WHERE i.permissions IS NULL AND i.role IN ('admin', 'specialist');
                ALTER TABLE users FORCE ROW LEVEL SECURITY;
                ALTER TABLE invites FORCE ROW LEVEL SECURITY;

                -- invites.permissions: повний матеріалізований знімок (B1), NOT NULL. DEFAULT '{}' (порожній знімок = найнижчі рівні,
                -- fail-closed) лише щоб наявний код створення запрошень (до TASK-851, який завжди передає повний знімок) не падав.
                ALTER TABLE invites ALTER COLUMN permissions SET DEFAULT '{}'::jsonb;
                ALTER TABLE invites ALTER COLUMN permissions SET NOT NULL;
                ALTER TABLE invites ADD CONSTRAINT ck_invites_permissions CHECK (jsonb_typeof(permissions) = 'object');

                -- Retention (Q7, 3 роки): ЄДИНИЙ шлях видалення з журналу - SECURITY DEFINER purge_user_access_audit, власник - окрема
                -- NOLOGIN-роль beautycrm_audit_purge (за патерном beautycrm_catalog, ADR-022: створюється тут або ops заздалегідь;
                -- членство мігратора INHERIT FALSE, SET TRUE; політики TO цієї ролі діють на всіх, хто її успадковує).
                DO $do$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_audit_purge') THEN
                        BEGIN
                            CREATE ROLE beautycrm_audit_purge NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
                        EXCEPTION
                            WHEN duplicate_object THEN NULL;
                            WHEN insufficient_privilege THEN
                            RAISE EXCEPTION 'Role beautycrm_audit_purge does not exist and % cannot create it (needs CREATEROLE). Create it as ops: CREATE ROLE beautycrm_audit_purge NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; GRANT beautycrm_audit_purge TO % WITH INHERIT FALSE, SET TRUE;', current_user, current_user;
                        END;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_audit_purge' AND (rolcanlogin OR rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'Role beautycrm_audit_purge must be NOLOGIN NOSUPERUSER NOBYPASSRLS (ADR-023)';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_auth_members m
                                   JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_audit_purge'
                                   JOIN pg_roles u ON u.oid = m.member AND u.rolname = current_user
                                   WHERE m.set_option AND NOT m.inherit_option) THEN
                        BEGIN
                            EXECUTE format('GRANT beautycrm_audit_purge TO %I WITH INHERIT FALSE, SET TRUE', current_user);
                        EXCEPTION WHEN insufficient_privilege THEN
                            RAISE EXCEPTION 'The migrator % must be a member of beautycrm_audit_purge WITH INHERIT FALSE, SET TRUE. Ops: GRANT beautycrm_audit_purge TO % WITH INHERIT FALSE, SET TRUE;', current_user, current_user;
                        END;
                    END IF;
                END
                $do$;
                GRANT USAGE ON SCHEMA public TO beautycrm_audit_purge;
                GRANT SELECT, DELETE ON user_access_audit TO beautycrm_audit_purge;

                CREATE FUNCTION public.purge_user_access_audit(p_before timestamptz) RETURNS bigint
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                DECLARE
                    n bigint;
                BEGIN
                    IF p_before IS NULL OR p_before > now() - interval '3 years' THEN
                        RAISE EXCEPTION 'purge_user_access_audit: p_before must be at least 3 years in the past' USING ERRCODE = '22023';
                    END IF;
                    DELETE FROM public.user_access_audit WHERE created_at < p_before;
                    GET DIAGNOSTICS n = ROW_COUNT;
                    RETURN n;
                END
                $fn$;
                -- EXECUTE: ніхто, крім ролі воркера (GRANT у grants.sql / setup-db.ps1; ролі середовища). REVOKE ДО передачі власності
                REVOKE ALL ON FUNCTION public.purge_user_access_audit(timestamptz) FROM PUBLIC;
                GRANT CREATE ON SCHEMA public TO beautycrm_audit_purge;
                ALTER FUNCTION public.purge_user_access_audit(timestamptz) OWNER TO beautycrm_audit_purge;
                REVOKE CREATE ON SCHEMA public FROM beautycrm_audit_purge;

                -- RLS (ADR-002): ENABLE + FORCE, fail-closed без app.tenant_id
                ALTER TABLE user_permissions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE user_permissions FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON user_permissions
                    USING ({{TenantPredicate}}) WITH CHECK ({{TenantPredicate}});
                ALTER TABLE user_access_audit ENABLE ROW LEVEL SECURITY;
                ALTER TABLE user_access_audit FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON user_access_audit
                    USING ({{TenantPredicate}}) WITH CHECK ({{TenantPredicate}});
                -- purge діє між tenant-ами (без app.tenant_id): окремі політики лише для ролі-власника функції, лише SELECT/DELETE
                CREATE POLICY audit_purge_select ON user_access_audit FOR SELECT TO beautycrm_audit_purge USING (true);
                CREATE POLICY audit_purge_delete ON user_access_audit FOR DELETE TO beautycrm_audit_purge USING (true);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // HUMAN GATE (ADR-023 §2.11): Down втрачає права користувачів і журнал доступу. Відмовляє, якщо втрата була б
            // несвідомою. FORCE RLS знімаємо лише на час перевірки (інакше без app.tenant_id усе здається порожнім);
            // при RAISE вся міграція відкочується разом зі зміною.
            migrationBuilder.Sql($$"""
                DO $do$
                BEGIN
                    ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE invites NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE user_permissions NO FORCE ROW LEVEL SECURITY;
                    IF EXISTS (SELECT 1 FROM users WHERE role = 'deputy') THEN
                        RAISE EXCEPTION 'Down refused: users with role deputy exist (ADR-023); change or remove them first (human gate)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM invites WHERE role = 'deputy') THEN
                        RAISE EXCEPTION 'Down refused: invites with role deputy exist (ADR-023); remove them first (human gate)';
                    END IF;
                    -- знімок, що не дорівнює пресету ролі (порожній '{}' = значення за замовчуванням, не кастомізація)
                    IF EXISTS (
                        WITH {{Presets}}
                        SELECT 1 FROM invites i
                        WHERE i.permissions <> '{}'::jsonb
                          AND i.permissions IS DISTINCT FROM (SELECT jsonb_object_agg(p.permission,
                                  CASE i.role WHEN 'admin' THEN p.admin_level ELSE p.specialist_level END) FROM presets p)
                    ) THEN
                        RAISE EXCEPTION 'Down refused: invites with custom permissions exist (ADR-023); remove or reset them first (human gate)';
                    END IF;
                    IF EXISTS (
                        WITH {{Presets}}
                        SELECT 1 FROM users u CROSS JOIN presets p
                        LEFT JOIN user_permissions up
                               ON up.tenant_id = u.tenant_id AND up.user_id = u.id AND up.permission = p.permission
                        WHERE u.role IN ('admin', 'specialist')
                          AND up.level IS DISTINCT FROM CASE u.role WHEN 'admin' THEN p.admin_level ELSE p.specialist_level END
                        UNION ALL
                        SELECT 1 FROM user_permissions up
                        JOIN users u ON u.tenant_id = up.tenant_id AND u.id = up.user_id
                        WHERE u.role = 'owner' OR NOT EXISTS (SELECT 1 FROM presets p WHERE p.permission = up.permission)
                    ) THEN
                        RAISE EXCEPTION 'Down refused: user permissions differ from role presets (custom access settings exist; ADR-023); export or reset them first (human gate)';
                    END IF;
                    ALTER TABLE users FORCE ROW LEVEL SECURITY;
                    ALTER TABLE invites FORCE ROW LEVEL SECURITY;
                END
                $do$;
                """);

            migrationBuilder.Sql("""
                DROP TABLE user_access_audit;
                DROP FUNCTION user_access_audit_block_mutation();
                -- власник функції - beautycrm_audit_purge: мігратор (INHERIT FALSE) стає нею лише на час DROP. Саму роль не видаляємо
                -- (кластерна, може бути спільною); знімаємо лише її привілеї зі схеми.
                SET LOCAL ROLE beautycrm_audit_purge;
                DROP FUNCTION public.purge_user_access_audit(timestamptz);
                RESET ROLE;
                REVOKE USAGE ON SCHEMA public FROM beautycrm_audit_purge;
                DROP TABLE user_permissions;
                DROP INDEX ix_beauty_appointments_tenant_id_created_by_user_id;
                ALTER TABLE beauty_appointments DROP CONSTRAINT fk_beauty_appointments_users_tenant_id_created_by_user_id;
                ALTER TABLE beauty_appointments DROP COLUMN created_by_user_id;
                ALTER TABLE invites DROP CONSTRAINT IF EXISTS ck_invites_permissions;
                ALTER TABLE invites DROP CONSTRAINT IF EXISTS ck_invites_role;
                ALTER TABLE invites DROP COLUMN permissions;
                ALTER TABLE invites ADD CONSTRAINT ck_invites_role CHECK (role IN ('admin', 'specialist'));
                ALTER TABLE users DROP CONSTRAINT IF EXISTS ck_users_role;
                ALTER TABLE users DROP COLUMN access_version;
                ALTER TABLE users ADD CONSTRAINT ck_users_role CHECK (role IN ('owner', 'admin', 'specialist'));
                """);
        }
    }
}
