using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// ADR-024 (фаза 1, expand) + errata 2026-11-17 / контракт §31.16, TASK-862. Написана вручну на основі виводу `dotnet ef migrations add`
    /// (знімок моделі оновлено ним же; згенерований Designer прибрано, як у auth_user_permissions). Зміст:
    ///   ролі beautycrm_identity (NOLOGIN) і beautycrm_identity_release (NOLOGIN; лише auth_identity_release, щоб не розширювати права першої);
    ///   identities, auth_login_tickets, auth_password_resets, auth_login_attempts: без tenant_id, ENABLE + FORCE RLS, політики лише TO ролі
    ///     (для beautycrm_app, воркера й PUBLIC політик немає: deny-all; REVOKE ALL на таблиці - у grants.sql / setup-db.ps1 ПІСЛЯ масових GRANT);
    ///   users.identity_id NOT NULL (FK RESTRICT, UNIQUE (tenant_id, identity_id)), users.sessions_valid_after, тригер узгодженості пошти;
    ///   розширення CHECK platform_audit_log (identity.*, actor_type identity);
    ///   SECURITY DEFINER-функції auth_* (власник - роль, search_path = pg_catalog, pg_temp, RETURNS TABLE з явними колонками,
    ///     нормалізація пошти всередині, consume одним UPDATE ... RETURNING, REVOKE FROM PUBLIC); матриця EXECUTE - у grants.sql;
    ///   backfill: одна ідентичність на lower(btrim(email)). Дублікати між tenant зливаються ЛИШЕ за allowlist email_key (8 hex з preflight)
    ///     у параметрі сесії beautycrm.identity_merge_allowlist (синонім beautycrm.identity_merge_allow; Npgsql `Options=-c ...` або PGOPTIONS).
    ///     Без нього дублікати = RAISE EXCEPTION. Група не в allowlist, ключ без групи або з кількома групами, група з invited-власником (B1),
    ///     без активного членства з паролем (B2) чи з дублем усередині одного tenant = RAISE. Для злитої групи в тій самій транзакції
    ///     відкликаються refresh-токени ВСІХ членств і users.sessions_valid_after = now(). RAISE NOTICE лише з кількостями.
    ///   users.password_hash, failed_login_count, lockout_until НЕ чіпаються (фаза 3; dual-write у коді фази 2).
    /// Down: відмовляє (RAISE EXCEPTION), якщо є активне членство без users.password_hash (dual-write порушено); інакше прибирає колонки, таблиці,
    ///   функції, політики й гранти ролей (самі ролі лишає); CHECK platform_audit_log звужує лише коли немає рядків identity.* (журнал append-only).
    ///   Втрачаються блокування, квитки й запити скидання. Human gate на staging/prod.
    /// </summary>
    [DbContext(typeof(BeautyDbContext))]
    [Migration("20261117140201_auth_global_identities")]
    public class auth_global_identities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RolesSql);
            migrationBuilder.Sql(TablesSql);
            migrationBuilder.Sql(BackfillSql);
            migrationBuilder.Sql(ConstraintsAndRlsSql);
            migrationBuilder.Sql(FunctionsSql);
            migrationBuilder.Sql(OwnershipSql);
        }

        // ------------------------------------------------------------------------------------------------------------
        // 1. Ролі (патерн beautycrm_catalog / beautycrm_audit_purge): створює міграція або ops заздалегідь (init-catalog-role.sql)
        // ------------------------------------------------------------------------------------------------------------
        private const string RolesSql = """
            DO $do$
            DECLARE
                r text;
            BEGIN
                FOREACH r IN ARRAY ARRAY['beautycrm_identity', 'beautycrm_identity_release'] LOOP
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                        BEGIN
                            EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT', r);
                        EXCEPTION
                            WHEN duplicate_object THEN NULL;
                            WHEN insufficient_privilege THEN
                                RAISE EXCEPTION 'Role % does not exist and % cannot create it (needs CREATEROLE). Create it as ops (deploy/staging/db/init-catalog-role.sql): CREATE ROLE % NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; GRANT % TO % WITH INHERIT FALSE, SET TRUE;', r, current_user, r, r, current_user;
                        END;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r AND (rolcanlogin OR rolsuper OR rolbypassrls OR rolinherit)) THEN
                        RAISE EXCEPTION 'Role % must be NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT (ADR-024)', r;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_auth_members m
                                   JOIN pg_roles ro ON ro.oid = m.roleid AND ro.rolname = r
                                   JOIN pg_roles u ON u.oid = m.member AND u.rolname = current_user
                                   WHERE m.set_option AND NOT m.inherit_option) THEN
                        BEGIN
                            EXECUTE format('GRANT %I TO %I WITH INHERIT FALSE, SET TRUE', r, current_user);
                        EXCEPTION WHEN insufficient_privilege THEN
                            RAISE EXCEPTION 'The migrator % must be a member of % WITH INHERIT FALSE, SET TRUE. Ops: GRANT % TO % WITH INHERIT FALSE, SET TRUE;', current_user, r, r, current_user;
                        END;
                    END IF;
                END LOOP;
            END
            $do$;
            """;

        // ------------------------------------------------------------------------------------------------------------
        // 2. Таблиці й колонки (імена збігаються з EF-моделлю / знімком), users.identity_id поки NULL
        // ------------------------------------------------------------------------------------------------------------
        private const string TablesSql = """
            ALTER TABLE users ADD COLUMN identity_id uuid NULL;                       -- стане NOT NULL після backfill
            ALTER TABLE users ADD COLUMN sessions_valid_after timestamp with time zone NULL;

            CREATE TABLE identities (
                id uuid NOT NULL DEFAULT gen_random_uuid(),
                email character varying(320) NOT NULL,
                password_hash character varying(255) NULL,
                state character varying(16) NOT NULL,
                last_login_at timestamp with time zone NULL,
                password_changed_at timestamp with time zone NULL,
                created_at timestamp with time zone NOT NULL DEFAULT now(),
                updated_at timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT pk_identities PRIMARY KEY (id),
                CONSTRAINT ck_identities_email_normalized CHECK (email = lower(btrim(email)) AND email <> ''),
                CONSTRAINT ck_identities_state CHECK (state IN ('pending', 'active')),
                CONSTRAINT ck_identities_password_or_pending CHECK ((state = 'active') = (password_hash IS NOT NULL))
            );
            CREATE UNIQUE INDEX ux_identities_email ON identities (email);

            CREATE TABLE auth_login_tickets (
                id uuid NOT NULL DEFAULT gen_random_uuid(),
                identity_id uuid NOT NULL,
                token_hash bytea NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                consumed_at timestamp with time zone NULL,
                created_at timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT pk_auth_login_tickets PRIMARY KEY (id),
                CONSTRAINT ck_auth_login_tickets_token_hash CHECK (octet_length(token_hash) = 32),
                CONSTRAINT fk_auth_login_tickets_identities_identity_id FOREIGN KEY (identity_id) REFERENCES identities (id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX ix_auth_login_tickets_token_hash ON auth_login_tickets (token_hash);
            CREATE INDEX ix_auth_login_tickets_identity_id ON auth_login_tickets (identity_id);
            CREATE INDEX ix_auth_login_tickets_expires_at ON auth_login_tickets (expires_at);

            -- Запит скидання й черга доставки листів (ADR-003, патерн черги-таблиці). Сирого токена немає ніколи (F-9): лише sha256.
            CREATE TABLE auth_password_resets (
                id uuid NOT NULL DEFAULT gen_random_uuid(),
                identity_id uuid NOT NULL,
                login_key bytea NOT NULL,
                token_hash bytea NULL,
                expires_at timestamp with time zone NOT NULL,
                consumed_at timestamp with time zone NULL,
                superseded_at timestamp with time zone NULL,
                delivery_status character varying(16) NOT NULL DEFAULT 'pending',
                attempts integer NOT NULL DEFAULT 0,
                next_attempt_at timestamp with time zone NOT NULL DEFAULT now(),
                last_error character varying(64) NULL,
                notice_status character varying(16) NOT NULL DEFAULT 'none',
                notice_attempts integer NOT NULL DEFAULT 0,
                notice_next_attempt_at timestamp with time zone NULL,
                created_at timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT pk_auth_password_resets PRIMARY KEY (id),
                CONSTRAINT ck_auth_password_resets_login_key CHECK (octet_length(login_key) = 32),
                CONSTRAINT ck_auth_password_resets_token_hash CHECK (token_hash IS NULL OR octet_length(token_hash) = 32),
                CONSTRAINT ck_auth_password_resets_delivery_status CHECK (delivery_status IN ('pending', 'sent', 'failed')),
                CONSTRAINT ck_auth_password_resets_notice_status CHECK (notice_status IN ('none', 'pending', 'sent', 'failed')),
                CONSTRAINT ck_auth_password_resets_attempts CHECK (attempts >= 0 AND notice_attempts >= 0),
                CONSTRAINT ck_auth_password_resets_consumed_or_superseded CHECK (consumed_at IS NULL OR superseded_at IS NULL),
                CONSTRAINT fk_auth_password_resets_identities_identity_id FOREIGN KEY (identity_id) REFERENCES identities (id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX ux_auth_password_resets_token_hash ON auth_password_resets (token_hash) WHERE token_hash IS NOT NULL;
            CREATE UNIQUE INDEX ux_auth_password_resets_active_identity ON auth_password_resets (identity_id)
                WHERE consumed_at IS NULL AND superseded_at IS NULL;
            CREATE INDEX ix_auth_password_resets_identity_id ON auth_password_resets (identity_id);
            CREATE INDEX ix_auth_password_resets_delivery_queue ON auth_password_resets (next_attempt_at)
                WHERE delivery_status = 'pending' AND consumed_at IS NULL AND superseded_at IS NULL;
            CREATE INDEX ix_auth_password_resets_notice_queue ON auth_password_resets (notice_next_attempt_at) WHERE notice_status = 'pending';
            CREATE INDEX ix_auth_password_resets_expires_at ON auth_password_resets (expires_at);

            -- Єдиний глобальний лічильник невдалих входів (F-2): без пошти й identity_id, ключ = HMAC-SHA256(pepper, email_norm)
            CREATE TABLE auth_login_attempts (
                login_key bytea NOT NULL,
                failed_count integer NOT NULL,
                window_started_at timestamp with time zone NOT NULL,
                locked_until timestamp with time zone NULL,
                updated_at timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT pk_auth_login_attempts PRIMARY KEY (login_key),
                CONSTRAINT ck_auth_login_attempts_login_key CHECK (octet_length(login_key) = 32),
                CONSTRAINT ck_auth_login_attempts_failed_count CHECK (failed_count >= 0)
            );
            CREATE INDEX ix_auth_login_attempts_updated_at ON auth_login_attempts (updated_at);

            -- журнал platform-дій: дії рівня ідентичності (§31.12); журнал append-only, наявні рядки задовольняють розширений CHECK
            ALTER TABLE platform_audit_log DROP CONSTRAINT ck_platform_audit_log_action;
            ALTER TABLE platform_audit_log DROP CONSTRAINT ck_platform_audit_log_actor_type;
            ALTER TABLE platform_audit_log ADD CONSTRAINT ck_platform_audit_log_action CHECK (action IN ('tenant.created', 'tenant.create_replayed', 'tenant.suspended', 'tenant.resumed', 'tenant.modules_changed', 'owner_invite.issued', 'owner_invite.revoked', 'owner_invite.accepted', 'control_auth.failed', 'identity.password_reset_completed', 'identity.locked', 'identity.created', 'identity.released'));
            ALTER TABLE platform_audit_log ADD CONSTRAINT ck_platform_audit_log_actor_type CHECK (actor_type IN ('shelfguard_admin', 'platform_key', 'owner', 'system', 'identity'));
            """;

        // ------------------------------------------------------------------------------------------------------------
        // 3. Backfill + злиття дублікатів за allowlist (F-5, ADR-024 §2.10). users.password_hash НЕ змінюється.
        //    FORCE RLS на users/refresh_tokens без app.tenant_id приховує всі рядки: знімаємо лише на час цього блоку (прецедент 20261115120000).
        // ------------------------------------------------------------------------------------------------------------
        public const string BackfillSql = """
            DO $do$
            DECLARE
                v_raw text;
                v_alias text;
                v_allow text[];
                v_bad text;
                v_n bigint;
                v_users bigint;
                v_new bigint;
                v_groups bigint;
                v_members bigint;
                v_revoked bigint;
            BEGIN
                ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE refresh_tokens NO FORCE ROW LEVEL SECURITY;

                -- allowlist email_key: 8 hex (left(md5(lower(btrim(email))), 8), як у звіті preflight-identity-duplicates.sql)
                v_raw := NULLIF(btrim(current_setting('beautycrm.identity_merge_allowlist', true)), '');
                v_alias := NULLIF(btrim(current_setting('beautycrm.identity_merge_allow', true)), '');
                IF v_raw IS NOT NULL AND v_alias IS NOT NULL AND v_raw <> v_alias THEN
                    RAISE EXCEPTION 'beautycrm.identity_merge_allowlist and beautycrm.identity_merge_allow are both set and differ; set only one';
                END IF;
                v_raw := COALESCE(v_raw, v_alias);
                IF v_raw IS NULL THEN
                    v_allow := ARRAY[]::text[];
                ELSE
                    SELECT COALESCE(array_agg(DISTINCT lower(btrim(k))), ARRAY[]::text[]) INTO v_allow FROM unnest(string_to_array(v_raw, ',')) AS k;
                    SELECT string_agg(k, ', ') INTO v_bad FROM unnest(v_allow) AS k WHERE k !~ '^[0-9a-f]{8}$';
                    IF v_bad IS NOT NULL THEN
                        RAISE EXCEPTION 'beautycrm.identity_merge_allowlist must be a comma-separated list of 8-hex email_key values from the preflight report (invalid entries: %)', v_bad;
                    END IF;
                END IF;

                -- кандидати: членства без ідентичності (повторний запуск нічого не знаходить)
                CREATE TEMP TABLE _identity_backfill ON COMMIT DROP AS
                SELECT u.id AS user_id, u.tenant_id, lower(btrim(u.email)) AS en, left(md5(lower(btrim(u.email))), 8) AS email_key,
                       u.role, u.state, u.is_active, u.password_hash, u.last_login_at, u.updated_at, u.created_at,
                       count(*) OVER (PARTITION BY lower(btrim(u.email))) AS n,
                       row_number() OVER (PARTITION BY lower(btrim(u.email))
                                          ORDER BY (u.is_active AND u.state = 'active' AND u.password_hash IS NOT NULL) DESC,
                                                   u.last_login_at DESC NULLS LAST, u.updated_at DESC, u.id) AS rn
                FROM users u WHERE u.identity_id IS NULL;
                SELECT count(*) INTO v_users FROM _identity_backfill;

                -- дублікат усередині одного tenant (case-варіанти) злити не можна: UNIQUE (tenant_id, identity_id)
                SELECT string_agg(DISTINCT email_key, ', ') INTO v_bad FROM
                    (SELECT email_key FROM _identity_backfill GROUP BY email_key, en HAVING count(*) <> count(DISTINCT tenant_id)) s;
                IF v_bad IS NOT NULL THEN
                    RAISE EXCEPTION 'Same-tenant duplicate emails (email_key: %): resolve manually before migrating (ADR-024, preflight section 1)', v_bad;
                END IF;

                -- групи дублікатів (між tenant) мають бути в allowlist
                SELECT string_agg(DISTINCT email_key, ', ') INTO v_bad FROM _identity_backfill WHERE n > 1 AND email_key <> ALL (v_allow);
                IF v_bad IS NOT NULL THEN
                    RAISE EXCEPTION 'Duplicate email groups found that are not in beautycrm.identity_merge_allowlist (email_key: %). Run deploy/staging/db/preflight-identity-duplicates.sql, resolve or approve them (human gate, ADR-024 Q4) and set the allowlist.', v_bad;
                END IF;
                -- ключ з allowlist без групи або з кількома групами (колізія префікса md5)
                SELECT string_agg(k, ', ') INTO v_bad FROM unnest(v_allow) AS k
                 WHERE (SELECT count(DISTINCT en) FROM _identity_backfill WHERE n > 1 AND email_key = k) <> 1;
                IF v_bad IS NOT NULL THEN
                    RAISE EXCEPTION 'beautycrm.identity_merge_allowlist entries matching no duplicate group or more than one group (email_key: %)', v_bad;
                END IF;
                -- B1: invited-власник у групі: людина ніколи не прийме запрошення власника, коли ідентичність активна. Завжди відмова
                SELECT string_agg(DISTINCT email_key, ', ') INTO v_bad FROM _identity_backfill
                 WHERE n > 1 AND email_key IN (SELECT email_key FROM _identity_backfill WHERE role = 'owner' AND state = 'invited');
                IF v_bad IS NOT NULL THEN
                    RAISE EXCEPTION 'Duplicate group contains an invited owner (B1; email_key: %): resolve manually before migrating (ADR-024 section 2.10)', v_bad;
                END IF;
                -- B2: у групі немає активного членства з паролем: нічий пароль брати
                SELECT string_agg(DISTINCT email_key, ', ') INTO v_bad FROM _identity_backfill
                 WHERE n > 1 AND email_key NOT IN (SELECT email_key FROM _identity_backfill WHERE is_active AND state = 'active' AND password_hash IS NOT NULL);
                IF v_bad IS NOT NULL THEN
                    RAISE EXCEPTION 'Duplicate group has no active membership with a password (B2; email_key: %): resolve manually before migrating', v_bad;
                END IF;

                -- одна ідентичність на нормалізовану пошту; пароль: «найсвіжіший вхід» серед активних членств з паролем (rn = 1)
                INSERT INTO identities (email, password_hash, state, last_login_at, created_at, updated_at)
                SELECT w.en, w.password_hash, CASE WHEN w.password_hash IS NULL THEN 'pending' ELSE 'active' END,
                       (SELECT max(b.last_login_at) FROM _identity_backfill b WHERE b.en = w.en),
                       (SELECT min(b.created_at) FROM _identity_backfill b WHERE b.en = w.en), now()
                FROM _identity_backfill w WHERE w.rn = 1
                ON CONFLICT (email) DO NOTHING;
                GET DIAGNOSTICS v_new = ROW_COUNT;

                UPDATE users u SET identity_id = i.id
                FROM identities i
                WHERE u.identity_id IS NULL AND i.email = lower(btrim(u.email));

                -- злиті групи: сесії всіх членств недійсні (інакше сесія «програвшого» членства через switch увійшла б у бізнес «переможця» без пароля)
                SELECT count(DISTINCT en), count(*) INTO v_groups, v_members FROM _identity_backfill WHERE n > 1;
                IF v_members > 0 THEN
                    UPDATE refresh_tokens rt SET revoked_at = now()
                    FROM _identity_backfill m
                    WHERE m.n > 1 AND rt.tenant_id = m.tenant_id AND rt.user_id = m.user_id AND rt.revoked_at IS NULL;
                    GET DIAGNOSTICS v_revoked = ROW_COUNT;
                    UPDATE users u SET sessions_valid_after = now()
                    FROM _identity_backfill m WHERE m.n > 1 AND u.id = m.user_id;
                ELSE
                    v_revoked := 0;
                END IF;

                SELECT count(*) INTO v_n FROM users WHERE identity_id IS NULL;
                IF v_n > 0 THEN
                    RAISE EXCEPTION 'identity backfill incomplete: % users without identity', v_n;
                END IF;
                RAISE NOTICE 'auth_global_identities backfill: memberships processed=%, identities created=%, merged groups=%, merged memberships=%, refresh tokens revoked=%',
                    v_users, v_new, v_groups, v_members, v_revoked;

                ALTER TABLE users FORCE ROW LEVEL SECURITY;
                ALTER TABLE refresh_tokens FORCE ROW LEVEL SECURITY;
            END
            $do$;
            """;

        // ------------------------------------------------------------------------------------------------------------
        // 4. NOT NULL / FK / UNIQUE, RLS deny-all, політики й колонкові гранти ролей
        // ------------------------------------------------------------------------------------------------------------
        private const string ConstraintsAndRlsSql = """
            ALTER TABLE users ALTER COLUMN identity_id SET NOT NULL;
            ALTER TABLE users ADD CONSTRAINT fk_users_auth_identity_identity_id FOREIGN KEY (identity_id) REFERENCES identities (id) ON DELETE RESTRICT;
            CREATE INDEX ix_users_identity_id ON users (identity_id);
            CREATE UNIQUE INDEX ix_users_tenant_id_identity_id ON users (tenant_id, identity_id);

            -- Глобальні таблиці: ENABLE + FORCE RLS; політики лише TO beautycrm_identity (+ release-роль там, де їй треба). Для app/воркера/PUBLIC політик НЕМАЄ.
            ALTER TABLE identities ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identities FORCE ROW LEVEL SECURITY;
            ALTER TABLE auth_login_tickets ENABLE ROW LEVEL SECURITY;
            ALTER TABLE auth_login_tickets FORCE ROW LEVEL SECURITY;
            ALTER TABLE auth_password_resets ENABLE ROW LEVEL SECURITY;
            ALTER TABLE auth_password_resets FORCE ROW LEVEL SECURITY;
            ALTER TABLE auth_login_attempts ENABLE ROW LEVEL SECURITY;
            ALTER TABLE auth_login_attempts FORCE ROW LEVEL SECURITY;
            CREATE POLICY identity_access ON identities TO beautycrm_identity USING (true) WITH CHECK (true);
            CREATE POLICY identity_access ON auth_login_tickets TO beautycrm_identity USING (true) WITH CHECK (true);
            CREATE POLICY identity_access ON auth_password_resets TO beautycrm_identity USING (true) WITH CHECK (true);
            CREATE POLICY identity_access ON auth_login_attempts TO beautycrm_identity USING (true) WITH CHECK (true);

            -- Міжтенантні політики лише для цих ролей і лише потрібні колонки (колонкові GRANT). tenant_isolation для решти ролей не змінюється.
            CREATE POLICY identity_select ON users FOR SELECT TO beautycrm_identity USING (true);
            CREATE POLICY identity_update ON users FOR UPDATE TO beautycrm_identity USING (true) WITH CHECK (true);
            CREATE POLICY identity_select ON tenants FOR SELECT TO beautycrm_identity USING (true);
            CREATE POLICY identity_select ON refresh_tokens FOR SELECT TO beautycrm_identity USING (true);
            CREATE POLICY identity_update ON refresh_tokens FOR UPDATE TO beautycrm_identity USING (true) WITH CHECK (true);

            CREATE POLICY identity_release_select ON identities FOR SELECT TO beautycrm_identity_release USING (true);
            CREATE POLICY identity_release_update ON identities FOR UPDATE TO beautycrm_identity_release USING (true) WITH CHECK (true);
            CREATE POLICY identity_release_select ON users FOR SELECT TO beautycrm_identity_release USING (true);
            CREATE POLICY identity_release_update ON users FOR UPDATE TO beautycrm_identity_release USING (true) WITH CHECK (true);
            CREATE POLICY identity_release_select ON refresh_tokens FOR SELECT TO beautycrm_identity_release USING (true);
            CREATE POLICY identity_release_update ON refresh_tokens FOR UPDATE TO beautycrm_identity_release USING (true) WITH CHECK (true);
            CREATE POLICY identity_release_select ON auth_login_tickets FOR SELECT TO beautycrm_identity_release USING (true);
            CREATE POLICY identity_release_update ON auth_login_tickets FOR UPDATE TO beautycrm_identity_release USING (true) WITH CHECK (true);
            CREATE POLICY identity_release_select ON auth_password_resets FOR SELECT TO beautycrm_identity_release USING (true);
            CREATE POLICY identity_release_update ON auth_password_resets FOR UPDATE TO beautycrm_identity_release USING (true) WITH CHECK (true);

            GRANT USAGE ON SCHEMA public TO beautycrm_identity, beautycrm_identity_release;
            GRANT SELECT, INSERT, UPDATE ON identities TO beautycrm_identity;
            GRANT SELECT, INSERT, UPDATE, DELETE ON auth_login_tickets, auth_password_resets, auth_login_attempts TO beautycrm_identity;
            GRANT SELECT (id, tenant_id, identity_id, role, is_active, state), UPDATE (password_hash, sessions_valid_after) ON users TO beautycrm_identity;
            GRANT SELECT (id, name, slug, status) ON tenants TO beautycrm_identity;
            GRANT SELECT (tenant_id, user_id, revoked_at), UPDATE (revoked_at) ON refresh_tokens TO beautycrm_identity;
            -- журнал: запис identity.* і підрахунок квоти (identity.created за 24 год); UPDATE/DELETE заблоковані тригером append-only
            GRANT INSERT, SELECT (action, tenant_id, occurred_at, details) ON platform_audit_log TO beautycrm_identity;

            -- auth_identity_release (F-4): окрема роль, мінімум прав; змінює пошту, активність, сесії й гасить квитки/запити скидання
            GRANT SELECT (id, email), UPDATE (email, updated_at) ON identities TO beautycrm_identity_release;
            GRANT SELECT (id, tenant_id, identity_id, access_version), UPDATE (email, is_active, sessions_valid_after, access_version) ON users TO beautycrm_identity_release;
            GRANT SELECT (tenant_id, user_id, revoked_at), UPDATE (revoked_at) ON refresh_tokens TO beautycrm_identity_release;
            GRANT SELECT (identity_id, consumed_at), UPDATE (consumed_at) ON auth_login_tickets TO beautycrm_identity_release;
            GRANT SELECT (identity_id, consumed_at, superseded_at), UPDATE (superseded_at) ON auth_password_resets TO beautycrm_identity_release;
            GRANT INSERT ON platform_audit_log TO beautycrm_identity_release;
            """;

        // ------------------------------------------------------------------------------------------------------------
        // 5. Функції SECURITY DEFINER (контракт §31.16). Пошта нормалізується всередині (lower(btrim())).
        //    Жодна функція не приймає identity_id від клієнта: identity_id береться з результату інших функцій.
        // ------------------------------------------------------------------------------------------------------------
        private const string FunctionsSql = """
            -- ---- пошук і стан ----
            CREATE FUNCTION public.auth_identity_find(p_email text)
            RETURNS TABLE (identity_id uuid, password_hash text, state text)
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                RETURN QUERY
                SELECT i.id, i.password_hash::text, i.state::text
                FROM public.identities i
                WHERE i.email = lower(btrim(p_email));
            END
            $fn$;

            CREATE FUNCTION public.auth_identity_memberships(p_identity uuid)
            RETURNS TABLE (tenant_id uuid, user_id uuid, tenant_name text, tenant_slug text, role text)
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                RETURN QUERY
                SELECT u.tenant_id, u.id, t.name::text, t.slug::text, u.role::text
                FROM public.users u
                JOIN public.tenants t ON t.id = u.tenant_id
                JOIN public.identities i ON i.id = u.identity_id
                WHERE u.identity_id = p_identity AND u.is_active AND u.state = 'active'
                  AND t.status = 'active' AND i.state = 'active';
            END
            $fn$;

            CREATE FUNCTION public.auth_identity_is_active(p_identity uuid) RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                RETURN EXISTS (SELECT 1 FROM public.identities i WHERE i.id = p_identity AND i.state = 'active');
            END
            $fn$;

            -- зайнята пошта: активна АБО pending ідентичність
            CREATE FUNCTION public.auth_identity_email_in_use(p_email text) RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                RETURN EXISTS (SELECT 1 FROM public.identities i WHERE i.email = lower(btrim(p_email)));
            END
            $fn$;

            CREATE FUNCTION public.auth_identity_login_succeeded(p_identity uuid, p_now timestamptz) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                UPDATE public.identities i SET last_login_at = p_now, updated_at = p_now WHERE i.id = p_identity AND i.state = 'active';
            END
            $fn$;

            -- ---- створення ----
            -- квота (лише via = staff_invite): identity.created за 24 год для origin tenant у platform_audit_log; advisory lock
            -- серіалізує паралельні прийняття одного tenant. Квота перевіряється ДО вставки (без оракула email_in_use для tenant над квотою).
            -- unique_violation (23505, ux_identities_email) віддається викликачу як є (409 email_in_use).
            CREATE FUNCTION public.auth_identity_create(p_email text, p_password_hash text, p_origin_tenant_id uuid, p_via text, p_max_per_day integer)
            RETURNS uuid
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_email text := lower(btrim(p_email));
                v_id uuid;
            BEGIN
                IF v_email IS NULL OR v_email = '' OR p_password_hash IS NULL OR p_password_hash = ''
                   OR p_via IS NULL OR p_via NOT IN ('staff_invite', 'owner_provisioning', 'platform') THEN
                    RAISE EXCEPTION 'auth_identity_create: invalid argument' USING ERRCODE = '22023';
                END IF;
                IF p_via = 'staff_invite' THEN
                    IF p_origin_tenant_id IS NULL OR p_max_per_day IS NULL OR p_max_per_day < 1 THEN
                        RAISE EXCEPTION 'auth_identity_create: origin tenant and a positive quota are required for staff_invite' USING ERRCODE = '22023';
                    END IF;
                    PERFORM pg_advisory_xact_lock(hashtextextended('auth_identity_quota:' || p_origin_tenant_id::text, 0));
                    IF (SELECT count(*) FROM public.platform_audit_log a
                        WHERE a.action = 'identity.created' AND a.tenant_id = p_origin_tenant_id
                          AND a.occurred_at > now() - interval '24 hours' AND a.details ->> 'via' = 'staff_invite') >= p_max_per_day THEN
                        RAISE EXCEPTION 'identity_quota' USING ERRCODE = 'P0001';
                    END IF;
                END IF;
                INSERT INTO public.identities (email, password_hash, state, password_changed_at)
                VALUES (v_email, p_password_hash, 'active', now())
                RETURNING id INTO v_id;
                INSERT INTO public.platform_audit_log (action, tenant_id, actor_type, actor_id, details)
                VALUES ('identity.created', p_origin_tenant_id, 'identity', v_id::text, jsonb_build_object('identityId', v_id, 'via', p_via));
                RETURN v_id;
            END
            $fn$;

            CREATE FUNCTION public.auth_identity_create_pending(p_email text, p_origin_tenant_id uuid, p_via text) RETURNS uuid
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_email text := lower(btrim(p_email));
                v_id uuid;
            BEGIN
                IF v_email IS NULL OR v_email = '' OR p_via IS NULL OR p_via NOT IN ('owner_provisioning', 'platform') THEN
                    RAISE EXCEPTION 'auth_identity_create_pending: invalid argument' USING ERRCODE = '22023';
                END IF;
                INSERT INTO public.identities (email, password_hash, state)
                VALUES (v_email, NULL, 'pending')
                RETURNING id INTO v_id;
                INSERT INTO public.platform_audit_log (action, tenant_id, actor_type, actor_id, details)
                VALUES ('identity.created', p_origin_tenant_id, 'identity', v_id::text, jsonb_build_object('identityId', v_id, 'via', p_via));
                RETURN v_id;
            END
            $fn$;

            -- прийняття запрошення власника: лише pending і рівно одне членство
            CREATE FUNCTION public.auth_identity_activate_pending(p_identity uuid, p_password_hash text) RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                IF p_password_hash IS NULL OR p_password_hash = '' THEN
                    RAISE EXCEPTION 'auth_identity_activate_pending: password hash is required' USING ERRCODE = '22023';
                END IF;
                UPDATE public.identities i
                   SET state = 'active', password_hash = p_password_hash, password_changed_at = now(), updated_at = now()
                 WHERE i.id = p_identity AND i.state = 'pending'
                   AND (SELECT count(*) FROM public.users u WHERE u.identity_id = i.id) = 1;
                RETURN FOUND;
            END
            $fn$;

            -- ---- лічильник невдалих входів (F-2) ----
            CREATE FUNCTION public.auth_login_attempt_is_locked(p_key bytea, p_now timestamptz) RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                RETURN EXISTS (SELECT 1 FROM public.auth_login_attempts a WHERE a.login_key = p_key AND a.locked_until > p_now);
            END
            $fn$;

            -- Атомарний upsert одного рядка. Вікно спливло -> лічильник з 1. Досягнення max -> locked_until = now + lockout, лічильник 0.
            -- Рядок із діючим блокуванням не змінюється. identity_id (з auth_identity_find, не від клієнта) лише для запису identity.locked.
            CREATE FUNCTION public.auth_login_attempt_failed(p_key bytea, p_identity uuid, p_max integer, p_window_seconds integer, p_lockout_seconds integer, p_now timestamptz)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_window interval;
                v_lockout interval;
                v_until timestamptz;
            BEGIN
                IF p_key IS NULL OR octet_length(p_key) <> 32 OR p_max IS NULL OR p_max < 1
                   OR p_window_seconds IS NULL OR p_window_seconds < 1 OR p_lockout_seconds IS NULL OR p_lockout_seconds < 1 THEN
                    RAISE EXCEPTION 'auth_login_attempt_failed: invalid argument' USING ERRCODE = '22023';
                END IF;
                v_window := make_interval(secs => p_window_seconds);
                v_lockout := make_interval(secs => p_lockout_seconds);
                INSERT INTO public.auth_login_attempts AS a (login_key, failed_count, window_started_at, locked_until, updated_at)
                VALUES (p_key,
                        CASE WHEN 1 >= p_max THEN 0 ELSE 1 END,
                        p_now,
                        CASE WHEN 1 >= p_max THEN p_now + v_lockout ELSE NULL END,
                        p_now)
                ON CONFLICT (login_key) DO UPDATE SET
                    failed_count = CASE
                        WHEN a.locked_until > p_now THEN a.failed_count
                        WHEN (CASE WHEN p_now - a.window_started_at >= v_window THEN 1 ELSE a.failed_count + 1 END) >= p_max THEN 0
                        WHEN p_now - a.window_started_at >= v_window THEN 1
                        ELSE a.failed_count + 1 END,
                    window_started_at = CASE
                        WHEN a.locked_until > p_now THEN a.window_started_at
                        WHEN p_now - a.window_started_at >= v_window THEN p_now
                        ELSE a.window_started_at END,
                    locked_until = CASE
                        WHEN a.locked_until > p_now THEN a.locked_until
                        WHEN (CASE WHEN p_now - a.window_started_at >= v_window THEN 1 ELSE a.failed_count + 1 END) >= p_max THEN p_now + v_lockout
                        ELSE NULL END,
                    updated_at = p_now
                RETURNING a.locked_until INTO v_until;
                IF v_until IS NOT NULL AND v_until > p_now THEN
                    IF v_until = p_now + v_lockout AND p_identity IS NOT NULL THEN
                        INSERT INTO public.platform_audit_log (action, tenant_id, actor_type, actor_id, details)
                        VALUES ('identity.locked', NULL, 'identity', p_identity::text, jsonb_build_object('identityId', p_identity));
                    END IF;
                    RETURN true;
                END IF;
                RETURN false;
            END
            $fn$;

            CREATE FUNCTION public.auth_login_attempt_reset(p_key bytea) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                DELETE FROM public.auth_login_attempts a WHERE a.login_key = p_key;
            END
            $fn$;

            -- ---- квиток вибору бізнесу ----
            CREATE FUNCTION public.auth_login_ticket_issue(p_identity uuid, p_token_hash bytea, p_expires_at timestamptz) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                IF p_token_hash IS NULL OR octet_length(p_token_hash) <> 32 OR p_expires_at IS NULL
                   OR p_expires_at <= now() OR p_expires_at > now() + interval '1 hour' THEN
                    RAISE EXCEPTION 'auth_login_ticket_issue: invalid argument' USING ERRCODE = '22023';
                END IF;
                -- нова видача гасить попередні непогашені квитки ідентичності
                UPDATE public.auth_login_tickets t SET consumed_at = now() WHERE t.identity_id = p_identity AND t.consumed_at IS NULL;
                INSERT INTO public.auth_login_tickets (identity_id, token_hash, expires_at)
                SELECT i.id, p_token_hash, p_expires_at FROM public.identities i WHERE i.id = p_identity AND i.state = 'active';
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'auth_login_ticket_issue: identity is not active' USING ERRCODE = 'P0002';
                END IF;
            END
            $fn$;

            -- ОДИН оператор: квиток гаситься завжди (Q-C5: навіть при чужому tenant), членство перевіряється тим самим запитом.
            -- Повертає users.id членства або NULL (квиток невідомий/прострочений/погашений, членство не належить ідентичності/неактивне).
            CREATE FUNCTION public.auth_login_ticket_consume(p_token_hash bytea, p_tenant_id uuid, p_now timestamptz) RETURNS uuid
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_user uuid;
            BEGIN
                WITH c AS (
                    UPDATE public.auth_login_tickets t SET consumed_at = p_now
                    WHERE t.token_hash = p_token_hash AND t.consumed_at IS NULL AND t.expires_at > p_now
                    RETURNING t.identity_id
                )
                SELECT (SELECT u.id FROM public.users u
                        JOIN public.tenants tn ON tn.id = u.tenant_id
                        JOIN public.identities i ON i.id = u.identity_id
                        WHERE u.identity_id = c.identity_id AND u.tenant_id = p_tenant_id
                          AND u.is_active AND u.state = 'active' AND tn.status = 'active' AND i.state = 'active')
                INTO v_user
                FROM c;
                RETURN v_user;
            END
            $fn$;

            -- ---- скидання пароля ----
            -- Для активної ідентичності створює запит і гасить попередні непогашені; для невідомої/pending пошти нічого не робить (void).
            CREATE FUNCTION public.auth_password_reset_issue(p_email text, p_login_key bytea, p_expires_at timestamptz) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_identity uuid;
            BEGIN
                IF p_login_key IS NULL OR octet_length(p_login_key) <> 32 OR p_expires_at IS NULL
                   OR p_expires_at <= now() OR p_expires_at > now() + interval '24 hours' THEN
                    RAISE EXCEPTION 'auth_password_reset_issue: invalid argument' USING ERRCODE = '22023';
                END IF;
                -- блокування рядка ідентичності серіалізує паралельні видачі (partial unique: один живий запит)
                SELECT i.id INTO v_identity FROM public.identities i
                 WHERE i.email = lower(btrim(p_email)) AND i.state = 'active' FOR UPDATE;
                IF v_identity IS NULL THEN
                    RETURN;
                END IF;
                UPDATE public.auth_password_resets r SET superseded_at = now()
                 WHERE r.identity_id = v_identity AND r.consumed_at IS NULL AND r.superseded_at IS NULL;
                INSERT INTO public.auth_password_resets (identity_id, login_key, expires_at, next_attempt_at)
                VALUES (v_identity, p_login_key, p_expires_at, now());
            END
            $fn$;

            -- ОДИН UPDATE ... RETURNING по token_hash (без SELECT перед ним). Далі в тій самій транзакції: новий пароль, зняття блокування за
            -- login_key, відкликання refresh-токенів і sessions_valid_after усіх членств, погашення квитків, dual-write users.password_hash
            -- (до фази 3), лист «Пароль змінено» у чергу, запис identity.password_reset_completed. Повертає identity_id або NULL.
            CREATE FUNCTION public.auth_password_reset_consume(p_token_hash bytea, p_new_hash text, p_now timestamptz) RETURNS uuid
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_identity uuid;
                v_key bytea;
            BEGIN
                IF p_new_hash IS NULL OR p_new_hash = '' THEN
                    RAISE EXCEPTION 'auth_password_reset_consume: password hash is required' USING ERRCODE = '22023';
                END IF;
                UPDATE public.auth_password_resets r
                   SET consumed_at = p_now, notice_status = 'pending', notice_attempts = 0, notice_next_attempt_at = p_now
                  FROM public.identities i
                 WHERE r.token_hash = p_token_hash AND r.identity_id = i.id AND i.state = 'active'
                   AND r.consumed_at IS NULL AND r.superseded_at IS NULL AND r.expires_at > p_now
                RETURNING r.identity_id, r.login_key INTO v_identity, v_key;
                IF NOT FOUND THEN
                    RETURN NULL;
                END IF;
                UPDATE public.identities i SET password_hash = p_new_hash, password_changed_at = p_now, updated_at = p_now WHERE i.id = v_identity;
                DELETE FROM public.auth_login_attempts a WHERE a.login_key = v_key;
                UPDATE public.refresh_tokens rt SET revoked_at = p_now
                 WHERE rt.revoked_at IS NULL
                   AND EXISTS (SELECT 1 FROM public.users u WHERE u.id = rt.user_id AND u.tenant_id = rt.tenant_id AND u.identity_id = v_identity);
                UPDATE public.users u SET sessions_valid_after = p_now, password_hash = p_new_hash WHERE u.identity_id = v_identity;
                UPDATE public.auth_login_tickets t SET consumed_at = p_now WHERE t.identity_id = v_identity AND t.consumed_at IS NULL;
                INSERT INTO public.platform_audit_log (action, tenant_id, actor_type, actor_id, details)
                VALUES ('identity.password_reset_completed', NULL, 'identity', v_identity::text, jsonb_build_object('identityId', v_identity));
                RETURN v_identity;
            END
            $fn$;

            -- Воркер: лізинг через next_attempt_at, FOR UPDATE SKIP LOCKED. Лише неспливлі й непогашені запити (reset_link) і лист «Пароль змінено»
            -- (changed_notice). Ліміт batch діє на кожен вид окремо. Повертає пошту лише для відправки (без токена й token_hash).
            CREATE FUNCTION public.auth_password_reset_claim_delivery(p_now timestamptz, p_lease_seconds integer, p_batch integer)
            RETURNS TABLE (reset_id uuid, email text, kind text)
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                IF p_now IS NULL OR p_lease_seconds IS NULL OR p_lease_seconds < 1 OR p_lease_seconds > 3600
                   OR p_batch IS NULL OR p_batch < 1 OR p_batch > 1000 THEN
                    RAISE EXCEPTION 'auth_password_reset_claim_delivery: invalid argument' USING ERRCODE = '22023';
                END IF;
                RETURN QUERY
                WITH due AS (
                    SELECT r.id FROM public.auth_password_resets r
                    JOIN public.identities i ON i.id = r.identity_id AND i.state = 'active'
                    WHERE r.delivery_status = 'pending' AND r.consumed_at IS NULL AND r.superseded_at IS NULL
                      AND r.expires_at > p_now AND r.next_attempt_at <= p_now
                    ORDER BY r.next_attempt_at, r.id
                    LIMIT p_batch
                    FOR UPDATE OF r SKIP LOCKED
                ), upd AS (
                    UPDATE public.auth_password_resets r SET next_attempt_at = p_now + make_interval(secs => p_lease_seconds)
                    FROM due WHERE r.id = due.id
                    RETURNING r.id, r.identity_id
                )
                SELECT upd.id, i.email::text, 'reset_link'::text FROM upd JOIN public.identities i ON i.id = upd.identity_id;
                RETURN QUERY
                WITH due AS (
                    SELECT r.id FROM public.auth_password_resets r
                    JOIN public.identities i ON i.id = r.identity_id AND i.state = 'active'
                    WHERE r.notice_status = 'pending' AND r.notice_next_attempt_at <= p_now
                    ORDER BY r.notice_next_attempt_at, r.id
                    LIMIT p_batch
                    FOR UPDATE OF r SKIP LOCKED
                ), upd AS (
                    UPDATE public.auth_password_resets r SET notice_next_attempt_at = p_now + make_interval(secs => p_lease_seconds)
                    FROM due WHERE r.id = due.id
                    RETURNING r.id, r.identity_id
                )
                SELECT upd.id, i.email::text, 'changed_notice'::text FROM upd JOIN public.identities i ON i.id = upd.identity_id;
            END
            $fn$;

            -- Воркер генерує токен і зберігає лише sha256; false = запит погашено/спливає/вже не pending: лист не надсилається
            CREATE FUNCTION public.auth_password_reset_attach_token(p_reset_id uuid, p_token_hash bytea, p_now timestamptz) RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                IF p_token_hash IS NULL OR octet_length(p_token_hash) <> 32 OR p_now IS NULL THEN
                    RAISE EXCEPTION 'auth_password_reset_attach_token: invalid argument' USING ERRCODE = '22023';
                END IF;
                UPDATE public.auth_password_resets r SET token_hash = p_token_hash
                 WHERE r.id = p_reset_id AND r.delivery_status = 'pending' AND r.consumed_at IS NULL
                   AND r.superseded_at IS NULL AND r.expires_at > p_now;
                RETURN FOUND;
            END
            $fn$;

            -- kind: reset_link | changed_notice; status: pending (повтор, потрібен next_at) | sent | failed. Кожен виклик = одна спроба.
            -- error_code: лише код [a-z0-9_]{1,64} (інакше 'unknown'), щоб не писати в БД пошту чи токен.
            CREATE FUNCTION public.auth_password_reset_mark_delivery(p_reset_id uuid, p_kind text, p_status text, p_error_code text, p_next_at timestamptz) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_err text;
            BEGIN
                IF p_kind IS NULL OR p_kind NOT IN ('reset_link', 'changed_notice') OR p_status IS NULL OR p_status NOT IN ('pending', 'sent', 'failed') THEN
                    RAISE EXCEPTION 'auth_password_reset_mark_delivery: invalid argument' USING ERRCODE = '22023';
                END IF;
                v_err := CASE WHEN p_error_code IS NULL THEN NULL WHEN p_error_code ~ '^[a-z0-9_]{1,64}$' THEN p_error_code ELSE 'unknown' END;
                IF p_kind = 'reset_link' THEN
                    UPDATE public.auth_password_resets r
                       SET delivery_status = p_status, attempts = r.attempts + 1, last_error = v_err,
                           next_attempt_at = CASE WHEN p_status = 'pending' THEN COALESCE(p_next_at, now() + interval '1 minute') ELSE r.next_attempt_at END
                     WHERE r.id = p_reset_id AND r.delivery_status = 'pending';
                ELSE
                    UPDATE public.auth_password_resets r
                       SET notice_status = p_status, notice_attempts = r.notice_attempts + 1, last_error = v_err,
                           notice_next_attempt_at = CASE WHEN p_status = 'pending' THEN COALESCE(p_next_at, now() + interval '1 minute') ELSE r.notice_next_attempt_at END
                     WHERE r.id = p_reset_id AND r.notice_status = 'pending';
                END IF;
            END
            $fn$;

            -- Воркер: прострочені квитки, прострочені запити скидання (крім тих, де лист «Пароль змінено» ще в черзі) і неактивні рядки
            -- лічильника (немає діючого блокування; рядок не оновлювався добу, тож Auth:LoginAttemptWindowSeconds має бути <= 86400).
            CREATE FUNCTION public.auth_identity_purge_expired(p_now timestamptz) RETURNS bigint
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_total bigint := 0;
                v_n bigint;
            BEGIN
                DELETE FROM public.auth_login_tickets t WHERE t.expires_at < p_now;
                GET DIAGNOSTICS v_n = ROW_COUNT;
                v_total := v_total + v_n;
                DELETE FROM public.auth_password_resets r WHERE r.expires_at < p_now AND r.notice_status <> 'pending';
                GET DIAGNOSTICS v_n = ROW_COUNT;
                v_total := v_total + v_n;
                DELETE FROM public.auth_login_attempts a
                 WHERE (a.locked_until IS NULL OR a.locked_until < p_now) AND a.updated_at < p_now - interval '1 day';
                GET DIAGNOSTICS v_n = ROW_COUNT;
                v_total := v_total + v_n;
                RETURN v_total;
            END
            $fn$;

            -- ---- звільнення пошти (F-4, Q11): human gate, лише ops/мігратор (роль beautycrm_identity_release) ----
            CREATE FUNCTION public.auth_identity_release(p_identity uuid, p_reason_code text) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                v_id uuid;
                v_email text;
            BEGIN
                IF p_reason_code IS NULL OR p_reason_code !~ '^[a-z0-9_]{1,64}$' THEN
                    RAISE EXCEPTION 'auth_identity_release: reason code must match [a-z0-9_]{1,64}' USING ERRCODE = '22023';
                END IF;
                SELECT i.id INTO v_id FROM public.identities i WHERE i.id = p_identity FOR UPDATE;
                IF v_id IS NULL THEN
                    RAISE EXCEPTION 'auth_identity_release: identity not found' USING ERRCODE = 'P0002';
                END IF;
                v_email := 'released+' || v_id::text || '@released.invalid';
                -- спершу ідентичність, потім членства (тригер узгодженості пошти бачить уже нову пошту)
                UPDATE public.identities i SET email = v_email, updated_at = now() WHERE i.id = v_id;
                UPDATE public.refresh_tokens rt SET revoked_at = now()
                 WHERE rt.revoked_at IS NULL
                   AND EXISTS (SELECT 1 FROM public.users u WHERE u.id = rt.user_id AND u.tenant_id = rt.tenant_id AND u.identity_id = v_id);
                UPDATE public.users u
                   SET email = v_email, is_active = false, sessions_valid_after = now(), access_version = u.access_version + 1
                 WHERE u.identity_id = v_id;
                UPDATE public.auth_login_tickets t SET consumed_at = now() WHERE t.identity_id = v_id AND t.consumed_at IS NULL;
                UPDATE public.auth_password_resets r SET superseded_at = now() WHERE r.identity_id = v_id AND r.consumed_at IS NULL AND r.superseded_at IS NULL;
                INSERT INTO public.platform_audit_log (action, tenant_id, actor_type, actor_id, details)
                VALUES ('identity.released', NULL, 'identity', v_id::text, jsonb_build_object('identityId', v_id, 'reasonCode', p_reason_code));
            END
            $fn$;

            -- ---- тригер узгодженості users.email з identities.email (ADR-024 §2.1) ----
            -- users.email - денормалізована копія. Порівняння толерантне до регістру/пробілів старих рядків (preflight розділ 4).
            -- Зміна пошти: спершу ідентичність, потім членства в одній транзакції.
            CREATE FUNCTION public.users_identity_email_guard() RETURNS trigger
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM public.identities i WHERE i.id = NEW.identity_id AND i.email = lower(btrim(NEW.email))) THEN
                    RAISE EXCEPTION 'users.email must match the email of its identity (ADR-024)' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END
            $fn$;
            -- тригер створюється ДО передачі власності функції: CREATE TRIGGER потребує EXECUTE, якого мігратор потім не матиме (після backfill і NOT NULL)
            CREATE TRIGGER users_identity_email_consistency
                BEFORE INSERT OR UPDATE OF email, identity_id ON users
                FOR EACH ROW EXECUTE FUNCTION public.users_identity_email_guard();
            """;

        // ------------------------------------------------------------------------------------------------------------
        // 6. Власність і EXECUTE: REVOKE FROM PUBLIC до передачі власності; передача через тимчасовий CREATE на схемі (патерн ADR-022/023)
        //    GRANT EXECUTE ролям середовища - у deploy/staging/db/grants.sql і scripts/dev/setup-db.ps1 (явно для кожної функції).
        // ------------------------------------------------------------------------------------------------------------
        private const string OwnershipSql = """
            DO $do$
            DECLARE
                f record;
            BEGIN
                FOR f IN SELECT p.oid::regprocedure AS sig, p.proname FROM pg_proc p
                         WHERE p.pronamespace = 'public'::regnamespace AND (p.proname LIKE 'auth\_%' OR p.proname = 'users_identity_email_guard')
                           AND p.proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                LOOP
                    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f.sig);
                END LOOP;
            END
            $do$;

            GRANT CREATE ON SCHEMA public TO beautycrm_identity, beautycrm_identity_release;
            DO $do$
            DECLARE
                f record;
            BEGIN
                FOR f IN SELECT p.oid::regprocedure AS sig, p.proname FROM pg_proc p
                         WHERE p.pronamespace = 'public'::regnamespace AND (p.proname LIKE 'auth\_%' OR p.proname = 'users_identity_email_guard')
                           AND p.proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                LOOP
                    EXECUTE format('ALTER FUNCTION %s OWNER TO %I', f.sig,
                                   CASE WHEN f.proname = 'auth_identity_release' THEN 'beautycrm_identity_release' ELSE 'beautycrm_identity' END);
                END LOOP;
            END
            $do$;
            REVOKE CREATE ON SCHEMA public FROM beautycrm_identity, beautycrm_identity_release;
            """;

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // HUMAN GATE (ADR-024 §2.10): Down втрачає блокування входу, квитки й запити скидання. Відмовляє, якщо dual-write
            // users.password_hash порушено (тоді відкат образу API лишив би членство без пароля). FORCE знімаємо лише на час перевірки.
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                    ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
                    IF EXISTS (SELECT 1 FROM users WHERE state = 'active' AND password_hash IS NULL) THEN
                        RAISE EXCEPTION 'Down refused: active memberships with users.password_hash IS NULL exist (ADR-024 dual-write broken); fix them first (human gate)';
                    END IF;
                    ALTER TABLE users FORCE ROW LEVEL SECURITY;
                END
                $do$;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER users_identity_email_consistency ON users;

                DROP POLICY identity_select ON users;
                DROP POLICY identity_update ON users;
                DROP POLICY identity_release_select ON users;
                DROP POLICY identity_release_update ON users;
                DROP POLICY identity_select ON tenants;
                DROP POLICY identity_select ON refresh_tokens;
                DROP POLICY identity_update ON refresh_tokens;
                DROP POLICY identity_release_select ON refresh_tokens;
                DROP POLICY identity_release_update ON refresh_tokens;
                REVOKE ALL ON users, tenants, refresh_tokens, platform_audit_log FROM beautycrm_identity, beautycrm_identity_release;

                ALTER TABLE users DROP CONSTRAINT fk_users_auth_identity_identity_id;
                DROP INDEX ix_users_identity_id;
                DROP INDEX ix_users_tenant_id_identity_id;
                ALTER TABLE users DROP COLUMN identity_id;
                ALTER TABLE users DROP COLUMN sessions_valid_after;

                DROP TABLE auth_login_tickets;
                DROP TABLE auth_password_resets;
                DROP TABLE auth_login_attempts;
                DROP TABLE identities;
                """);

            // функції належать ролям: мігратор (INHERIT FALSE, SET TRUE) стає ними лише на час DROP (прецедент auth_user_permissions)
            migrationBuilder.Sql("""
                DO $do$
                DECLARE
                    r text;
                    f record;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['beautycrm_identity', 'beautycrm_identity_release'] LOOP
                        EXECUTE format('SET LOCAL ROLE %I', r);
                        FOR f IN SELECT p.oid::regprocedure AS sig FROM pg_proc p
                                 WHERE p.pronamespace = 'public'::regnamespace AND p.proowner = (SELECT oid FROM pg_roles WHERE rolname = r)
                        LOOP
                            EXECUTE format('DROP FUNCTION %s', f.sig);
                        END LOOP;
                        RESET ROLE;
                    END LOOP;
                END
                $do$;
                REVOKE USAGE ON SCHEMA public FROM beautycrm_identity, beautycrm_identity_release;

                -- журнал append-only: звужуємо CHECK лише тоді, коли рядків identity.* немає (інакше лишаємо розширений: це безпечніше за втрату журналу)
                DO $do$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM platform_audit_log WHERE action LIKE 'identity.%' OR actor_type = 'identity') THEN
                        ALTER TABLE platform_audit_log DROP CONSTRAINT ck_platform_audit_log_action;
                        ALTER TABLE platform_audit_log DROP CONSTRAINT ck_platform_audit_log_actor_type;
                        ALTER TABLE platform_audit_log ADD CONSTRAINT ck_platform_audit_log_action CHECK (action IN ('tenant.created', 'tenant.create_replayed', 'tenant.suspended', 'tenant.resumed', 'tenant.modules_changed', 'owner_invite.issued', 'owner_invite.revoked', 'owner_invite.accepted', 'control_auth.failed'));
                        ALTER TABLE platform_audit_log ADD CONSTRAINT ck_platform_audit_log_actor_type CHECK (actor_type IN ('shelfguard_admin', 'platform_key', 'owner', 'system'));
                    ELSE
                        RAISE NOTICE 'platform_audit_log has identity.* rows (append-only): the widened CHECK constraints are kept';
                    END IF;
                END
                $do$;
                """);
        }
    }
}
