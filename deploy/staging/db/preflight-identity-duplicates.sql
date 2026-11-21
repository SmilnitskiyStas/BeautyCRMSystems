-- ADR-024 фаза 0 (TASK-861): PREFLIGHT дублікатів пошти між бізнесами. ТІЛЬКИ ЧИТАННЯ.
--
-- Що перевіряє: чи є одна пошта (lower(btrim(email))) у кількох рядках users (різні tenant), які міграція
-- auth_global_identities (TASK-862) мала б злити в одну ідентичність; запрошення та власники, які цьому
-- заважають або будуть відхилені за рішенням 3 («зайнята пошта -> відмова»).
--
-- Запуск (від superuser/BYPASSRLS, напр. postgres, у БД beautycrm; users має FORCE RLS, тож власник схеми
-- без BYPASSRLS побачив би 0 рядків: скрипт у такому разі падає з помилкою, а не мовчить):
--   psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 -f preflight-identity-duplicates.sql
-- Показати повні адреси пошти (лише в терміналі ops, не в git/логи/чат):
--   psql ... -v show_email=1 -f preflight-identity-duplicates.sql
--
-- Гарантії:
--  * Транзакція READ ONLY + ROLLBACK: жодних змін схеми чи даних, жодних тимчасових таблиць.
--  * Паролі й будь-які хеші НЕ виводяться ні в якому вигляді: лише булеві ознаки й лічильники.
--    Хеші (bcrypt) солені, тож збіг/розбіжність ПАРОЛІВ за хешами визначити неможливо; див. розділ 2.
--  * За замовчуванням пошта маскується (email_key = перші 8 hex md5 нормалізованої пошти + маска a***@d***.tld).
--    Виводяться slug tenant, ролі, стани, UUID членств: не ПІІ.
--  * email_key з розділів 1-3 - це значення для allowlist міграції: PGOPTIONS="-c beautycrm.identity_merge_allowlist=<key1>,<key2>"
--    (параметр beautycrm.identity_merge_allow - синонім). Без нього міграція auth_global_identities падає при дублікатах (ADR-024 errata E-6).
--  * Нічого не пишеться у файли скриптом; вивід звіту людині (без повних пошт) можна зберегти: psql ... > report.txt
--
-- Розділи: 0 guard і підсумок; 1 групи дублікатів; 2 членства в групах (кандидат на «переможця» злиття);
-- 3 блокери злиття; 4 аномалії нормалізації пошти; 5 незавершені запрошення персоналу;
-- 6 незавершені запрошення власників; 7 розподіл bcrypt cost (F-13).

\set ON_ERROR_STOP on
\if :{?show_email}
\else
  \set show_email 0
\endif
\pset pager off
\pset null '-'

BEGIN READ ONLY;

-- 0. Guard: ми маємо бачити дані попри FORCE RLS
DO $$
BEGIN
  IF NOT (SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user) THEN
    RAISE EXCEPTION 'preflight: роль % не обходить RLS (users має FORCE RLS) - результат був би хибно порожнім. Запустіть від postgres/superuser.', current_user;
  END IF;
  IF to_regclass('public.users') IS NULL OR to_regclass('public.invites') IS NULL
     OR to_regclass('public.platform_owner_invites') IS NULL THEN
    RAISE EXCEPTION 'preflight: немає users/invites/platform_owner_invites - це не БД beautycrm або не застосовані міграції.';
  END IF;
END $$;

\echo
\echo '=== 0. Підсумок ==='
WITH m AS (
  SELECT lower(btrim(email)) AS en, tenant_id, state, is_active, password_hash IS NOT NULL AS has_pw
  FROM users
), g AS (
  SELECT en, count(*) AS n, count(DISTINCT tenant_id) AS nt FROM m GROUP BY en
)
SELECT (SELECT count(*) FROM m)                                   AS users_total,
       (SELECT count(*) FROM g)                                   AS distinct_emails_normalized,
       (SELECT count(*) FROM g WHERE n > 1)                       AS duplicate_email_groups,
       (SELECT coalesce(sum(n), 0) FROM g WHERE n > 1)            AS memberships_in_duplicates,
       (SELECT count(*) FROM g WHERE n > 1 AND nt < n)            AS groups_with_same_tenant_dup_should_be_0,
       (SELECT count(*) FROM tenants)                             AS tenants_total;

\echo
\echo '=== 1. Групи дублікатів (одна пошта, >1 членства) ==='
-- hash_variants: скільки різних значень хеша (не паролів!) серед членств з паролем; усі солені, тож
-- >1 НЕ означає різні паролі. same_hash_in_all = true лише якщо рядки містять буквально той самий хеш.
WITH m AS (
  SELECT u.id, u.tenant_id, lower(btrim(u.email)) AS en, u.email, u.role, u.state, u.is_active,
         u.password_hash, u.last_login_at, t.slug
  FROM users u JOIN tenants t ON t.id = u.tenant_id
), g AS (
  SELECT en,
         count(*)                                                  AS memberships,
         count(DISTINCT tenant_id)                                 AS tenants,
         count(DISTINCT email)                                     AS raw_case_variants,
         count(*) FILTER (WHERE is_active AND state = 'active')    AS active_memberships,
         count(*) FILTER (WHERE password_hash IS NOT NULL)         AS with_password,
         count(*) FILTER (WHERE state = 'invited')                 AS invited,
         count(*) FILTER (WHERE role = 'owner')                    AS owner_roles,
         count(DISTINCT password_hash)                             AS hash_variants,
         max(last_login_at)                                        AS last_login_max,
         string_agg(slug || ':' || role || ':' || state || ':' || CASE WHEN is_active THEN 'on' ELSE 'off' END,
                    ', ' ORDER BY last_login_at DESC NULLS LAST, slug) AS members
  FROM m GROUP BY en HAVING count(*) > 1
)
SELECT left(md5(en), 8) AS email_key,
       CASE WHEN :show_email = 1 THEN en
            ELSE left(en, 1) || '***@' || left(split_part(en, '@', 2), 1) || '***.' || regexp_replace(split_part(en, '@', 2), '^.*\.', '') END AS email,
       memberships, tenants, raw_case_variants, active_memberships, with_password, invited, owner_roles,
       hash_variants, (hash_variants = 1 AND with_password = memberships) AS same_hash_in_all,
       last_login_max, members
FROM g ORDER BY memberships DESC, email_key;

\echo
\echo '=== 2. Членства в групах дублікатів (merge_winner = чий пароль взяла б міграція за правилом latest_login) ==='
-- Правило ADR-024 §2.10: серед активних членств з паролем найбільший last_login_at (NULLS LAST), далі updated_at.
-- ADR каже «активного членства» без уточнення: тут «active» = state='active' AND is_active. Якщо таких немає,
-- merge_winner порожній, і група потребує ручного розв'язання.
WITH m AS (
  SELECT u.id, u.tenant_id, lower(btrim(u.email)) AS en, u.role, u.state, u.is_active, u.specialist_id,
         u.password_hash IS NOT NULL AS has_pw, u.last_login_at, u.updated_at, u.failed_login_count,
         (u.lockout_until IS NOT NULL AND u.lockout_until > now()) AS locked_now,
         t.slug, t.status AS tenant_status
  FROM users u JOIN tenants t ON t.id = u.tenant_id
), d AS (
  SELECT *, count(*) OVER (PARTITION BY en) AS n FROM m
), r AS (
  SELECT d.*, row_number() OVER (PARTITION BY en
         ORDER BY (is_active AND state = 'active' AND has_pw) DESC,
                  last_login_at DESC NULLS LAST, updated_at DESC, id) AS rn
  FROM d WHERE n > 1
)
SELECT left(md5(en), 8) AS email_key, slug AS tenant_slug, tenant_status, id AS user_id, role, state, is_active,
       has_pw AS has_password, last_login_at, failed_login_count, locked_now,
       specialist_id IS NOT NULL AS is_specialist_profile,
       (rn = 1 AND is_active AND state = 'active' AND has_pw) AS merge_winner
FROM r ORDER BY email_key, rn;

\echo
\echo '=== 3. Блокери злиття (міграція має відмовити / потрібне ручне рішення людини, Q4) ==='
-- B1: у групі є власник у стані invited (пароля ще не задавав), а ідентичність стала б активною
--     (ADR: така людина ніколи не прийме запрошення власника) -> завжди відмова.
-- B2: у групі немає жодного активного членства з паролем -> немає чийого пароля брати.
-- B3: група охоплює tenant зі status='suspended' (інформативно: членство лишиться, але бізнес недоступний).
WITH m AS (
  SELECT u.id, lower(btrim(u.email)) AS en, u.role, u.state, u.is_active, u.password_hash IS NOT NULL AS has_pw,
         t.slug, t.status AS tstatus
  FROM users u JOIN tenants t ON t.id = u.tenant_id
), g AS (
  SELECT en, count(*) AS n,
         count(*) FILTER (WHERE role = 'owner' AND state = 'invited')                 AS invited_owners,
         count(*) FILTER (WHERE is_active AND state = 'active' AND has_pw)            AS usable_pw,
         count(*) FILTER (WHERE tstatus = 'suspended')                                AS suspended_tenants
  FROM m GROUP BY en HAVING count(*) > 1
)
SELECT left(md5(en), 8) AS email_key, n AS memberships,
       invited_owners > 0 AND usable_pw > 0 AS "B1_invited_owner_vs_active_identity",
       usable_pw = 0                       AS "B2_no_active_password_to_keep",
       suspended_tenants > 0               AS "B3_suspended_tenant_involved",
       CASE WHEN invited_owners > 0 AND usable_pw > 0 THEN 'BLOCKER: розвести вручну до міграції'
            WHEN usable_pw = 0             THEN 'BLOCKER: немає пароля для ідентичності'
            WHEN usable_pw = 1             THEN 'єдиний активний пароль; решта членств отримають його ж'
            ELSE 'потрібне рішення людини: чий пароль лишається (за замовчуванням latest_login)' END AS verdict
FROM g ORDER BY email_key;

\echo
\echo '=== 4. Аномалії нормалізації пошти (серед усіх users) ==='
-- Міграція нормалізує як lower(btrim()); код входу робить Trim().ToLowerInvariant().
-- Не-ASCII та інші пробіли (tab, NBSP) можуть нормалізуватися по-різному в SQL і C#: перевірити вручну.
SELECT left(md5(lower(btrim(email))), 8) AS email_key, u.id AS user_id, t.slug AS tenant_slug,
       email <> lower(btrim(email))          AS differs_from_normalized,
       email ~ '\s' AND email = btrim(email) AS has_inner_or_unicode_whitespace,
       email ~ '[^\x01-\x7f]'                AS has_non_ascii,
       email !~ '^[^@\s]+@[^@\s]+$'          AS malformed
FROM users u JOIN tenants t ON t.id = u.tenant_id
WHERE email <> lower(btrim(email)) OR email ~ '\s' OR email ~ '[^\x01-\x7f]' OR email !~ '^[^@\s]+@[^@\s]+$'
ORDER BY email_key;

\echo
\echo '=== 5. Незавершені запрошення персоналу (invites: не прийняте, не відкликане) ==='
-- Рішення 3: після впровадження запрошення на пошту, зайняту в іншому бізнесі, дасть 409 email_in_use.
-- conflict_other_tenant: пошта вже є в users іншого tenant (прийняття буде відхилене);
-- already_member_same_tenant: вже є в цьому ж tenant (наявний user_exists);
-- pending_in_other_tenant: інше живе запрошення на ту саму пошту в іншому tenant (друге прийняття програє гонку).
SELECT left(md5(lower(btrim(i.email))), 8) AS email_key, t.slug AS tenant_slug, i.role,
       i.created_at, i.expires_at, (i.expires_at <= now()) AS expired,
       EXISTS (SELECT 1 FROM users u WHERE lower(btrim(u.email)) = lower(btrim(i.email))
                 AND u.tenant_id <> i.tenant_id)                      AS conflict_other_tenant,
       EXISTS (SELECT 1 FROM users u WHERE lower(btrim(u.email)) = lower(btrim(i.email))
                 AND u.tenant_id = i.tenant_id)                       AS already_member_same_tenant,
       EXISTS (SELECT 1 FROM invites i2 WHERE lower(btrim(i2.email)) = lower(btrim(i.email))
                 AND i2.tenant_id <> i.tenant_id AND i2.accepted_at IS NULL AND i2.revoked_at IS NULL
                 AND i2.expires_at > now())                           AS pending_in_other_tenant
FROM invites i JOIN tenants t ON t.id = i.tenant_id
WHERE i.accepted_at IS NULL AND i.revoked_at IS NULL
ORDER BY conflict_other_tenant DESC, pending_in_other_tenant DESC, i.expires_at;

\echo
\echo '=== 6. Незавершені запрошення власників (users.state=invited; platform_owner_invites) ==='
-- Власник invited має password_hash NULL -> після міграції ідентичність 'pending'. Конфлікт, якщо така
-- пошта вже є в іншому tenant (див. B1 у розділі 3).
-- has_live_token: є непогашений, невідкликаний, непрострочений токен запрошення власника.
SELECT left(md5(lower(btrim(u.email))), 8) AS email_key, t.slug AS tenant_slug, t.status AS tenant_status,
       u.id AS user_id, u.is_active, u.created_at,
       EXISTS (SELECT 1 FROM platform_owner_invites p
                WHERE p.tenant_id = u.tenant_id AND p.user_id = u.id
                  AND p.used_at IS NULL AND p.revoked_at IS NULL AND p.expires_at > now()) AS has_live_token,
       (SELECT count(*) FROM platform_owner_invites p WHERE p.tenant_id = u.tenant_id AND p.user_id = u.id) AS invite_rows,
       EXISTS (SELECT 1 FROM users o WHERE lower(btrim(o.email)) = lower(btrim(u.email))
                 AND o.tenant_id <> u.tenant_id)                      AS email_also_in_other_tenant
FROM users u JOIN tenants t ON t.id = u.tenant_id
WHERE u.role = 'owner' AND u.state = 'invited'
ORDER BY email_also_in_other_tenant DESC, u.created_at;

\echo
\echo '=== 7. bcrypt cost у users.password_hash (F-13): ЛИШЕ кількості за префіксом $2a$NN$, без хешів ==='
-- Фіктивний хеш логіну генерується тим самим IPasswordHasher з поточним cost; тут видно, який cost мають наявні хеші
-- (різниця cost між фіктивним і реальними хешами дала б часовий оракул існування пошти).
SELECT coalesce(substring(password_hash from '^\$2[abxy]\$([0-9]{2})\$'),
                CASE WHEN password_hash IS NULL THEN '(немає хеша)' ELSE '(не bcrypt)' END) AS bcrypt_cost,
       count(*)                                                AS memberships,
       count(*) FILTER (WHERE is_active AND state = 'active')  AS active_memberships
FROM users GROUP BY 1 ORDER BY 1;

ROLLBACK;
\echo
\echo 'Preflight завершено (READ ONLY, ROLLBACK). Паролі/хеші не виводились.'
