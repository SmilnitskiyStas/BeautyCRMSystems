# Beauty CRM: локальний запуск (runbook)

Версія 1.1 (2026-11-09; додано роль `beautycrm_catalog`, міграцію ADR-022, ризики збірки через workspaces). Кроки 1–8 виконано вручну на Windows 11 (Git Bash + PowerShell + Docker Desktop) у версії 1.0 (2026-10-08); кроки з ADR-022 (роль каталогу, міграція 11) **не перевірено** на стенді. Redis для локального запуску **не потрібен**: worker читає чергу безпосередньо з PostgreSQL (див. ADR-003).

Потрібно: Docker, .NET SDK 8+ (перевірено на 10.0 з таргетом net8.0), Node.js 22+, `dotnet-ef` (`dotnet tool install -g dotnet-ef`).

## 1. PostgreSQL 16 в Docker
```bash
docker run -d --name beautycrm -e POSTGRES_PASSWORD=<pg-admin-pw> -p 127.0.0.1:55432:5432 postgres:16-alpine
```
Створіть дві ролі: власник схеми (для міграцій) і роль застосунку **без** прав суперкористувача та BYPASSRLS (інакше RLS не діє, API відмовиться стартувати):
```bash
docker exec beautycrm psql -U postgres -v ON_ERROR_STOP=1 \
  -c "CREATE ROLE beautycrm_owner LOGIN PASSWORD '<owner-pw>'" \
  -c "CREATE ROLE beautycrm_app LOGIN PASSWORD '<app-pw>' NOSUPERUSER NOBYPASSRLS" \
  -c "CREATE DATABASE beautycrm OWNER beautycrm_owner"
docker exec beautycrm psql -U postgres -d beautycrm -c "GRANT CONNECT ON DATABASE beautycrm TO beautycrm_app"
```

## 1a. Роль публічного каталогу `beautycrm_catalog` (ADR-022)
Міграція `20261109075625_beauty_public_location_catalog` сама створює роль `NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT`,
якщо її немає, але для цього мігратору потрібен `CREATEROLE`. Локальний стенд з кроку 1 (`beautycrm_owner` без `CREATEROLE`)
тому роль треба створити заздалегідь суперкористувачем `postgres`:
```bash
docker exec beautycrm psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 \
  -c "CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT" \
  -c "GRANT beautycrm_catalog TO beautycrm_owner WITH INHERIT FALSE, SET TRUE"
```
- `GRANT beautycrm_catalog TO beautycrm_owner` потрібен, щоб міграція могла передати власність функцій ролі.
- Право `EXECUTE` для `beautycrm_app` міграція видає автоматично, якщо роль `beautycrm_app` уже існує (крок 1).
  Якщо застосунок працює під іншим ім'ям ролі, виконайте вручну:
  `GRANT EXECUTE ON FUNCTION public.beauty_public_resolve_location(text), public.beauty_public_nearby_locations(double precision, double precision, double precision, integer), public.beauty_public_search_locations_by_city(text, integer) TO <runtime_role>`.
- Після `Down`/`Up` міграції права на функції скидаються: повторіть `GRANT EXECUTE`.
- Перевірка: `DbRoleGuard` при старті API перевіряє інваріанти ролі каталогу (NOLOGIN, не суперкористувач, не BYPASSRLS,
  runtime-роль не є членом, функції належать `beautycrm_catalog`, SECURITY DEFINER, без `EXECUTE` для PUBLIC).
- Не перевірено: чи вистачає власнику `beautycrm_owner` прав на `GRANT CREATE ON SCHEMA public` у PostgreSQL 16 (міграція тимчасово видає це право ролі каталогу).

## 2. Міграції (під власником схеми)
PowerShell:
```powershell
cd backend
$env:ConnectionStrings__Migrator = "Host=localhost;Port=55432;Database=beautycrm;Username=beautycrm_owner;Password=<owner-pw>"
dotnet ef database update --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Infrastructure
```
Очікувано: застосовуються 11 міграцій (від `add_beauty_schema` до `beauty_public_location_catalog`), далі `Done.`, у схемі 25+ таблиць. Міграція 11 вимагає ролі `beautycrm_catalog` (розділ 1a). Потім видайте права ролі застосунку (потрібно повторювати після кожної міграції з новими таблицями):
```bash
docker exec beautycrm psql -U postgres -d beautycrm \
  -c "GRANT USAGE ON SCHEMA public TO beautycrm_app" \
  -c "GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO beautycrm_app" \
  -c "GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app"
```

### 2a. ADR-023: права доступу (міграція `20261115120000_auth_user_permissions`)

**Вимоги до середовищ (обов'язково до міграції):**
- Роль `beautycrm_audit_purge` має існувати до міграції (NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT). Міграція створює її лише якщо мігратор має `CREATEROLE`, інакше падає з інструкцією.
- Мігратор (`beautycrm_owner`) має бути членом `beautycrm_audit_purge` `WITH INHERIT FALSE, SET TRUE`. Міграція видає членство сама, якщо може; інакше — ops вручну. `INHERIT TRUE` небезпечний: політики `TO beautycrm_audit_purge` діяли б на всіх, хто успадковує роль.
- **Локально:** `scripts\dev\setup-db.ps1` створює роль і членство; після міграції повторіть скрипт (GRANT-и ідемпотентні).
- **Staging:** виконати `deploy/staging/db/init-catalog-role.sql` від superuser Postgres **ПЕРЕД** міграцією (роль і членство для `beautycrm_owner`). Далі міграції (`migrations-<SHA>`) під `beautycrm_owner`, потім `deploy/staging/db/grants.sql`.
- Крок «GRANT після міграції» (`grants.sql`) обов'язковий: без нього `beautycrm_app` не має прав на `user_permissions`, а воркер не має `EXECUTE` на `purge_user_access_audit`.

**Backfill:** міграція ідемпотентна (одна транзакція). Для кожного `admin` і `specialist` записує пресет (`ON CONFLICT DO NOTHING`) і `migrated` в журнал. Поведінка не змінюється, бо пресет дорівнює поточній матриці. Застосування на staging/prod — human gate.

**Перевірки після деплою:**
1. `SELECT rolname, rolcanlogin, rolsuper, rolbypassrls, rolinherit FROM pg_roles WHERE rolname='beautycrm_audit_purge'` → `f, f, f, f, f`.
2. `SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname IN ('user_permissions','user_access_audit')` → обидві `t, t`. Виконувати від superuser (FORCE RLS приховує рядки без `app.tenant_id`).
3. `SELECT has_table_privilege('beautycrm_app','user_access_audit','UPDATE')` → `f`; `has_function_privilege('beautycrm_worker','public.purge_user_access_audit(timestamptz)','EXECUTE')` → `t`; `has_function_privilege('public','public.purge_user_access_audit(timestamptz)','EXECUTE')` → `f`.
4. Backfill: кожен `admin`/`specialist` має рядки в `user_permissions` (`SELECT count(*)` з `NOT EXISTS` → `0`), у журналі є `migrated`.
5. API: `GET /api/auth/me` повертає `accessVersion`, `permissions`, `modules`; відповідь має заголовок `X-Access-Version`. `GET /api/access/catalog` → `200`. `GET /api/users/access-audit` від адміністратора → `403 forbidden_role`.
6. Смоук майстра: `GET /api/beauty/me/revenue?from=…&to=…` → `200`; `GET /api/beauty/analytics/network` → `403 forbidden_role`.
7. Воркер: `user_permissions` і `user_access_audit` недоступні (`42501`).

**Відкат міграції `20261115120000` (human gate):**
1. Резервна копія БД (`pg_dump`, див. `deploy/staging/README.md`).
2. Перевірити передумови `Down`: немає користувачів і запрошень `deputy`, немає запрошень з нестандартним знімком, права всіх admin/specialist відповідають пресетам. `Down` **відмовляє** (RAISE EXCEPTION), якщо є відмінності. Не обходити: рішення про втрату налаштувань приймає людина (export або reset через API спочатку).
3. Старий образ API сумісний зі схемою, поки лишається `invites.permissions DEFAULT '{}'`. Спочатку відкотити образи, потім схему.
4. Відкат: `dotnet ef database update 20261110090000_beauty_cancellation_terms_visibility --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Infrastructure` (під `beautycrm_owner`).
5. Роль `beautycrm_audit_purge` не видаляється (кластерна). Після відкату GRANT-и `grants.sql` для видалених таблиць неактуальні.

**Відомі обмеження:** `DbRoleGuard` не перевіряє роль `beautycrm_audit_purge`; retention-job воркера для `purge_user_access_audit` ще не реалізовано (known-issues KI-006).

## 2b. ADR-024: глобальна ідентичність (міграція `20261117140201_auth_global_identities`)

Статус (2026-11-18): код у гілці (TASK-861…866, 871, 872), **на staging не застосовано**. Міграція, `grants.sql` і зміна env — human gate. Модель і контракт: ADR-024 (§ «Реалізація»), `beauty-contracts.md` §31, `database.md` «Global identity».

### 2b.1 Preflight (лише читання, до міграції)
1. Бекап БД (`deploy/staging/README.md` §5) і перевірка файлу.
2. Від `postgres` у БД `beautycrm`:
   ```bash
   psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 -f deploy/staging/db/preflight-identity-duplicates.sql
   ```
   Скрипт працює в `BEGIN READ ONLY … ROLLBACK`. Він падає, якщо роль не обходить RLS (`users` має FORCE RLS, без BYPASSRLS результат був би хибно порожнім). Повні адреси пошти — лише з `-v show_email=1` у терміналі оператора; звіт не комітити й не вставляти в чат.
3. Розділи звіту: 1 групи дублікатів (`email_key` = 8 hex md5 нормалізованої пошти); 2 членства і кандидат-переможець (правило «найсвіжіший вхід»); 3 блокери B1 (invited-власник у групі), B2 (немає активного членства з паролем), B3 (призупинений tenant); 4 аномалії нормалізації (регістр, пробіли, не-ASCII); 5 живі запрошення персоналу, що конфліктують; 6 запрошені власники; 7 розподіл bcrypt cost.
4. Обмеження: хеші солені, тож розбіжність паролів однієї пошти з звіту не видно. «Активний» = `state='active' AND is_active`.
5. Звіт іде людині (Q4). Рішення по групах:
   - одна людина → її `email_key` до allowlist (див. 2b.2); пароль «переможця» (найсвіжіший вхід) лишається, паролі інших членств перестануть працювати;
   - різні люди або тестові акаунти → розвести до міграції (змінити пошту/вимкнути членство), повторити preflight;
   - B1/B2/B3 → лише вручну до міграції. Автоматичного злиття для них немає, міграція відмовить.

### 2b.2 Застосування міграції (human gate)
1. **Ролі.** Якщо мігратор не має `CREATEROLE`, ops створює `beautycrm_identity` і `beautycrm_identity_release` за `deploy/staging/db/init-catalog-role.sql` (NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT) і дає мігратору членство `WITH INHERIT FALSE, SET TRUE`. Інакше міграція зупиниться з готовою інструкцією в тексті помилки.
2. **Allowlist злиття** передається сесійним параметром, а не файлом: `beautycrm.identity_merge_allowlist` = список `email_key` через кому (8 hex). Порожньо = злиття не дозволено; дублікати тоді дають `RAISE EXCEPTION`.
   - Npgsql: `Options=-c beautycrm.identity_merge_allowlist=<key1>,<key2>` у рядку мігратора; або `PGOPTIONS`;
   - psql з SQL-файлом: рядок `SET beautycrm.identity_merge_allowlist = '<key1>,<key2>';` перед тілом міграції, застосування з `-1` (одна транзакція, GUC бачить міграція).
   - Синонім `beautycrm.identity_merge_allow` приймається; різні значення → помилка. Значення не комітити в git, не писати в чат.
3. **Порядок:**
   1. бекап і preflight (2b.1), рішення Q4 записане;
   2. `$DC stop api worker` (`deploy/staging/README.md` §4, крок 3);
   3. міграція від `beautycrm_owner` (README §4, крок 4) з allowlist з п.2;
   4. **`deploy/staging/db/grants.sql` від postgres** (обов'язково після міграції: `REVOKE ALL` на глобальні таблиці йде після `GRANT ON ALL TABLES`, і без нього app може їх читати);
   5. `$DC up -d api`, перевірка `DbRoleGuard` у логах, `/readyz`; `$DC up -d worker`.
4. **Очікувані повідомлення:** `NOTICE` з кількостями backfill. Помилки `identity backfill incomplete`, `Duplicate email groups found…`, `…invited owner (B1…)`, `…no active membership with a password (B2…)` означають: виправте дані або allowlist і повторіть з бекапу, не обходьте.
5. **Відкат.** `Down` — human gate. Відмовляє, якщо є активне членство з `users.password_hash IS NULL` (dual-write порушено). Втрачаються блокування, квитки й токени скидання. Образ API попереднього релізу працює з цією БД, бо `users.password_hash` не змінюється (dual-write до фази 3). Відкат образу без відкату БД — штатний шлях.
6. **Фаза 3 (TASK-870)** видаляє `users.password_hash`, лічильники, `tenants_login_lookup` і поле `tenant`. Не раніше ніж після `Sunset` і стабілізації, окремий human gate.

### 2b.3 Звільнення пошти (`auth_identity_release`, оператор; ADR-024 E-7, Q11)
Лише коли заявник не володіє скринькою або пошту треба звільнити для нового запрошення. Це не звичайне «забув пароль» (для нього є скидання пароля). **Human gate:** спершу поза системою перевірте, що заявник володіє скринькою, і зафіксуйте рішення.

1. Під `beautycrm_owner` знайти ідентичність. Таблиця `identities` має FORCE RLS, тож читання йде через роль, яка має політику:
   ```sql
   SET ROLE beautycrm_identity_release;
   SELECT id FROM identities WHERE email = lower(btrim('<пошта>'));
   RESET ROLE;
   ```
2. Виклик (EXECUTE видано `beautycrm_owner` і `beautycrm_identity_release`):
   ```sql
   SELECT public.auth_identity_release('<identity_id>'::uuid, '<reason_code>');
   ```
   `reason_code` за `^[a-z0-9_]{1,64}$` (напр. `owner_request`, `wrong_email_squat`). Помилки: `22023` (невірний код), `P0002` (ідентичність не знайдено).
3. Ефект в одній транзакції: пошта ідентичності й `users.email` усіх членств → `released+<identity_id>@released.invalid`; членства `is_active = false`; refresh-токени відкликано; `sessions_valid_after = now()`, `access_version + 1`; квитки входу й запити скидання погашено; запис `identity.released` (`tenant_id = NULL`, `{identityId, reasonCode}`) у `platform_audit_log`.
4. Перевірка: `SELECT public.auth_identity_email_in_use('<пошта>');` → `false`. Після цього пошту можна запросити знову. Старі FK на членства лишаються.
5. Запишіть результат (дата, оператор, `reason_code`, identity_id) у журнал змін. Пошту в публічних каналах не писати.

### 2b.4 Нові змінні середовища (ADR-024)
Повний перелік з коментарями — у таблиці §3. Обов'язкові поза Development:
- API: `Auth__LoginAttemptKeyPepper` (≥ 32 байти), `Auth__LegacyTenantLogin__DeprecatedAt`, `Auth__LegacyTenantLogin__SunsetAt`; `Beauty__TrustedProxies` (шлюз + `web`).
- Compose: `BEAUTY_WEB_IP` (статична адреса web).
- Worker (скидання пароля, TASK-865/872): `PUBLIC_APP_URL` — `https`, без query, fragment і userinfo; `http` лише в Development або на loopback. Без нього воркер не бере рядки скидання (записує помилку раз за прохід). Посилання = `{PUBLIC_APP_URL}/reset-password#t=<токен>`. `EMAIL_FROM` — адреса відправника; обов'язковий разом із `SMTP_*`, коли `EMAIL_MODE=smtp`. У mock-режимі листи не надсилаються.

**Увага (KI-008).** `deploy/staging/.env.api.example` і корневий `.env.example` **не містять** `Auth__LoginAttemptKeyPepper` та `Auth__LegacyTenantLogin__*`. Скопійований з прикладу env без них API на staging не стартує. Додайте значення перед першим стартом.

**SMTP-налаштування й перевірка листів.** Відправник і текст листів погоджує людина (Q7). Шаблони в плейсхолдері, потребують погодження до релізу TASK-865 (скидання) і TASK-872 (зміна пароля).

## 3. Секрети й змінні середовища
Згенеруйте (`openssl rand`): `Auth__JwtSigningKey` (`-base64 48`), `Auth__PlatformKey` (`-base64 36`), `Channels__EncryptionKey` (`-base64 32`, base64 від 32 байт; при заміні ключа вже збережені секрети каналів перестануть розшифровуватись). Файл `.env` у `.gitignore`, комітити заборонено. Повний перелік змінних і коментарі: `.env.example`.

| Змінна | Призначення |
|---|---|
| `ConnectionStrings__Default` | runtime-рядок, роль `beautycrm_app` |
| `ConnectionStrings__Migrator` | лише для `dotnet ef` (власник) |
| `Auth__JwtSigningKey`, `Auth__PlatformKey` | без них API не стартує / ендпоінти оператора вимкнені |
| `Auth__LoginAttemptKeyPepper` | ADR-024 (§31.1, E-2): HMAC-ключ `auth_login_attempts`, **≥ 32 байти UTF-8**, обов'язковий поза Development (інакше API не стартує). Зміна скидає лічильники блокувань. Значення задає лише людина |
| `Auth__LegacyTenantLogin__DeprecatedAt`, `Auth__LegacyTenantLogin__SunsetAt` | ADR-024 (§31.2.3, E-12): дати для заголовків `Deprecation` (`@<unix>`) і `Sunset` легкого логіну з полем `tenant`. **Обов'язкові поза Development.** Sunset = дата фази 3 (Q12: 30 днів після релізу фази 2) |
| `Auth__LoginAttemptWindowSeconds` (900), `Auth__LoginTicketSeconds` (180), `Auth__PasswordResetMinutes` (30) | ADR-024 дефолти (§31.9); змінювати лише з рішенням людини |
| `Auth__IdentityCreate__PerTenantPerDay` (20, 1..200), `Auth__InviteEmailCheck__PerTenantPerHour` (30) | квота нових ідентичностей через запрошення (E-7) і перевірки пошти при створенні запрошення |
| `Auth__PasswordForgot__PerIpPermit` (5), `__PerEmailPermit` (3), `__QueueCapacity` (1000) | ліміти й черга `password/forgot` (per-instance, in-memory) |
| `Beauty__TrustedProxies` | (ADR-024 E-14, TASK-871) **шлюз** мережі `beauty_staging` + **статична адреса web** (`BEAUTY_WEB_IP`), окремі IP. Без цього `auth` (10/хв) рахується спільно для всіх клієнтів |
| `BEAUTY_WEB_IP` (`.env.compose`) | статична адреса контейнера `web` у підмережі `BEAUTY_SUBNET` (не шлюз, не `.2`–`.9`); обов'язкова в `docker-compose.staging.yml`. Порт web лише на `127.0.0.1` |
| `Channels__EncryptionKey` | шифрування секретів каналів (AES-256-GCM) |
| `Channels__UseMocks` | `true` лише в Development; поза Development `true` зупиняє старт |
| `Beauty__AllowedOrigins` | точні origin для CORS, без `*` |
| `Beauty__TrustedProxies` | IP/CIDR довірених reverse proxy (інакше `X-Forwarded-For` ігнорується) |
| `Beauty__AllowPrivilegedDbRole` | лише Development: дозволити суперкористувача/BYPASSRLS |
| `PublicBooking:MaxPublicCreatesPerLocationPerHour` | ліміт публічних записів на заклад за годину (за замовч. 60, 0 = вимкнено) |
| `PublicBooking:MinAppVersion:Ios` / `:Android` | мінімальна версія мобільного застосунку (нижче — 426 `upgrade_required`), формат `1.2.3` |
| `PublicBooking:Protection:Web` / `:Ios` / `:Android` | `none` / `monitor` / `enforce` (за замовч. `none`) |
| `PublicBooking:RateLimit:NearbyPermit` / `SearchPermit` | ліміти `public-nearby` (10) і `public-search` (20) на хвилину на IP |
| `EXPO_PUBLIC_API_URL`, `EXPO_PUBLIC_WEB_DOMAIN` | мобільний застосунок, на етапі збірки (`mobile/README.md`) |
| `WORKER_DATABASE_URL`, `WORKER_TENANT_IDS`, `WORKER_MARKETING_DAILY_LIMIT` | worker |
| `NEXT_PUBLIC_API_URL`, `API_URL`, `NEXT_PUBLIC_TENANT_SLUG` | frontend (читаються під час **build**) |
| `APPLE_TEAM_ID`, `IOS_BUNDLE_ID`, `ANDROID_PACKAGE`, `ANDROID_SHA256_CERT_FINGERPRINTS` | frontend runtime: `/.well-known/*` (див. §12a) |
| `API_INTERNAL_URL` | frontend runtime: server-side URL API для `/l/[code]` (fallback `NEXT_PUBLIC_API_URL`) |
| `NEXT_PUBLIC_IOS_STORE_URL`, `NEXT_PUBLIC_ANDROID_STORE_URL` | frontend build: кнопки магазинів на лендінгу `/l/{code}` |
| `NEXT_PUBLIC_USE_MOCK=1`, `NEXT_PUBLIC_MOCK_ROLE` | demo без backend (`owner`/`admin`/`specialist`) |

## 4. Backend
```powershell
cd backend
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:ASPNETCORE_URLS="http://localhost:5000"
$env:ConnectionStrings__Default="Host=localhost;Port=55432;Database=beautycrm;Username=beautycrm_app;Password=<app-pw>"
$env:Auth__JwtSigningKey="<..>"; $env:Auth__PlatformKey="<..>"; $env:Channels__EncryptionKey="<..>"
$env:Beauty__AllowedOrigins="http://localhost:3100"; $env:Channels__UseMocks="true"
dotnet run --project BeautyCrm.Api --no-launch-profile
```
Перевірка: `curl http://localhost:5000/swagger/v1/swagger.json` дає 200. Якщо роль у `Default` суперкористувач або BYPASSRLS, API завершується з помилкою `DbRoleGuard`.

## 5. Перший tenant, заклад і дані
Публічної самореєстрації немає: бізнес створює оператор платформи (заголовок `X-Platform-Key`):
```bash
curl -X POST http://localhost:5000/api/platform/tenants -H "X-Platform-Key: <platform-key>" -H "Content-Type: application/json" \
  --data-binary @tenant.json
```
де `tenant.json`: `{"name":"Beauty Lab","slug":"beauty-lab","modules":["beauty_booking","beauty_catalog","beauty_clients","beauty_analytics","beauty_channels","beauty_ai"],"owner":{"email":"owner@beautylab.test","fullName":"Owner","password":"<мін. 12 символів>"}}` → `201`. Вхід: `POST /api/auth/login` з `{"tenant":"beauty-lab","email":"...","password":"..."}`. Далі власник створює заклади (`POST /api/beauty/locations`), послуги й ціни (`POST /api/beauty/services`, `PUT /api/beauty/services/{id}/prices`), працівників (`POST /api/beauty/specialists`) та запрошує їх (`POST /api/beauty/specialists/{id}/invite`; токен повертається один раз, email-доставки немає).

## 6. Worker
```powershell
cd worker
$env:WORKER_DATABASE_URL="postgres://beautycrm_app:<app-pw>@localhost:55432/beautycrm"
$env:WORKER_TENANT_IDS="<tenant-uuid>"; $env:Channels__EncryptionKey="<..>"
npm.cmd install; npm.cmd start
```
У логах видно `beauty.reminders`, `beauty.outbox`, `beauty.winback`, `beauty.reviews` (нагадування щохвилини, outbox кожні 30 с). Worker відмовляється працювати з роллю суперкористувача/BYPASSRLS.

**Кнопки в нагадуваннях (TASK-751, §23).** `WORKER_REMINDER_BUTTONS_ENABLED=true` (за замовч. `false`) додає під нагадування кнопки «Підтверджую візит» / «Скасувати візит» / «Не нагадувати»; потрібен `settings.assistant.enabled=true` каналу (і `buttons` не `false`), а в API `ChatAssistant:Enabled=true` та `ChatButtons:Enabled=true`, інакше натискання ніхто не обробить. GRANT для ролі воркера (після міграції `beauty_chat_buttons`): `GRANT SELECT, INSERT ON beauty_chat_keyboards TO <роль>`; `UPDATE` на `beauty_messages` (включно з `external_message_id`) уже входить у `SELECT,INSERT,UPDATE,DELETE ON ALL TABLES`; при вужчих правах: `GRANT UPDATE (status, sent_at, updated_at, attempts, last_error, external_message_id) ON beauty_messages TO <роль>`.

## 7. Frontend
Реальний API (збірка вбудовує `NEXT_PUBLIC_*` змінні):
```powershell
cd frontend
$env:NEXT_PUBLIC_API_URL="http://localhost:5000"; $env:API_URL="http://localhost:5000"; $env:NEXT_PUBLIC_TENANT_SLUG="beauty-lab"
npm.cmd install; npm.cmd run build; npm.cmd run start -- -p 3100
```
Відкрийте `http://localhost:3100/login` (код бізнесу, пошта, пароль) або `/book` (публічний запис). Demo без backend: `$env:NEXT_PUBLIC_USE_MOCK="1"; $env:NEXT_PUBLIC_MOCK_ROLE="owner"; npm.cmd run dev -- -p 3100`.

## 8. Тести
```powershell
$env:BEAUTY_TEST_REQUIRE_DB="1"; dotnet test backend      # потрібен Docker (Testcontainers), без змінної тести БД пропускаються
cd worker; npm.cmd test                                      # Docker потрібен
cd frontend; npm.cmd test; npx tsc --noEmit; npm.cmd run lint
```

## 8a. Збірка через npm workspaces (ризики, не перевірено)
Стан у worktree (2026-11-09), перевірено читанням файлів, команди не запускалися:
- Кореневий `package.json`: `workspaces: ["frontend", "packages/*"]`. `mobile/` не входить у workspaces і має окремий `mobile/package.json`.
- `frontend/package.json` залежить від `@beauty/booking-core: "*"`; `frontend/next.config.ts` задає `transpilePackages` і `turbopack.root` на корінь монорепо. Збірка frontend потребує `packages/booking-core`.
- Runbook §7 пише `cd frontend; npm.cmd install`. Не перевірено, чи правильно це працює з кореневими workspaces; безпечніше встановлювати з кореня репозиторію.
- Lock-файлів (`package-lock.json`) у корені, `frontend/` і `mobile/` **немає** в worktree. `npm ci` (типово для CI/Docker) без lock не спрацює. Не перевірено, чи lock є в git-історії / на `main`.
- `.github/workflows/staging-images.yml` (ручна публікація образів у ghcr.io) будує контексти `backend`, `worker`, `web`. Каталогу `web/` у репозиторії немає (фронтенд — `frontend/`). Dockerfile у репозиторії (worktree) не знайдено ні для одного сервісу. Workflow також очікує `ci.yml` з успішним запуском для того самого SHA, але `ci.yml` у `.github/workflows/` цього worktree немає. Не перевірено на `main` чи інших гілках.
- Docker-контекст для `frontend` має бути коренем репозиторію (пакет `packages/booking-core` лежить поза `frontend/`); контекст лише `frontend/` зламає збірку.
- `deploy/staging/README.md` і ADR-016, на які посилається workflow, у worktree не знайдено.

## 9. Пастки
- PowerShell: `npm.ps1` блокується політикою виконання, використовуйте `npm.cmd`.
- Один `next dev` на теку проєкту: другий запуск повертає помилку з адресою першого. Порт 3000 може бути зайнятий іншим контейнером Docker, беріть 3100.
- Git Bash на Windows псує українські літери в аргументах curl: передавайте тіло файлом (`--data-binary @file.json`, UTF-8).
- Публічний POST вимагає `Idempotency-Key` довжиною 16–128 з `[A-Za-z0-9._:-]`.
- Не використовуйте PgBouncer у transaction mode та Npgsql Multiplexing: `app.tenant_id` тримається на з'єднанні (Multiplexing перевіряється при старті).
- Міграції, що перемикають `FORCE ROW LEVEL SECURITY` для backfill, виконуйте роллю-власником схеми.

## 10. Відомі обмеження
- Нагадування клієнтам, які записались через сайт без розмови в месенджері, закриваються як `cancelled` («клієнт недосяжний»). Канали пошти й SMS не реалізовані; підключення заплановане.
- Telegram і Instagram: реальні адаптери з mock-режимом; Viber, WhatsApp, Messenger і віджет лише заглушки.
- Кампанії й розсилки по сегментах не реалізовані (немає таблиці); працює лише winback. AI-чат, план акції й кампанії в адмінці повертають `501 not_supported`.
- CAPTCHA-провайдер не обрано (хук `ICaptchaVerifier`, при `CaptchaRequired=true` відхиляє все).
- Платіжний провайдер не обрано: `MockPaymentService`.
- Лічильник блокувань входу для невідомих email зберігається в пам'яті процесу (при кількох екземплярах API рахується окремо).
- CSP у frontend без nonce: `script-src` і `style-src` містять `'unsafe-inline'` (потрібно для Next.js), `default-src 'self'`.
- Access-токен деактивованого користувача більше не діє до 15 хв: з ADR-023 кожен запит перевіряє активність у БД (`401 session_revoked`). Сесія access-токена 15 хв лишається для зміни пароля й «вийти з усіх пристроїв» (known-issues KI-001).
- Немає журналу дій персоналу (аудит).

## 11. Локальний smoke Telegram (scripts/dev)
Скрипти `scripts/dev/*.ps1` (PowerShell 5.1+) готують середовище для ручного smoke Telegram-асистента (контракт §21.10). Усе локально: Development, long polling `getUpdates`, mock-адаптери вимкнено. Секрети лежать лише в gitignored `.env.dev.local`; PID, логи й GUID каналу лежать у gitignored `scripts/dev/.state/`. Порти: PostgreSQL `127.0.0.1:55432` (контейнер `beautycrm-dev`), API `http://localhost:5000`. Порт 3001 і чужі контейнери скрипти не чіпають.

**Порядок запуску** (корінь репозиторію, `powershell -ExecutionPolicy Bypass -File ...` або просто `.\scripts\dev\...`):
1. `scripts\dev\setup-db.ps1`: один раз. Генерує `.env.dev.local` (ключі JWT/platform/encryption/Ai master `k1`, паролі ролей БД і owner; наявні значення не перезаписує), піднімає PostgreSQL 16, створює ролі `beautycrm_owner` / `beautycrm_app` (NOSUPERUSER NOBYPASSRLS), застосовує всі міграції, видає GRANT і `REVOKE UPDATE, DELETE ON beauty_ai_settings_audit` (append-only). Після кожної нової міграції запускайте повторно.
2. `scripts\dev\seed.ps1`: tenant `beauty-lab-dev` (6 модулів), owner, заклад «Beauty Lab Dev» (Europe/Kyiv, Пн-Нд 09:00-20:00), послуга «Стрижка» (60 хв, 500), майстер «Майстер Анна» (09:00-18:00, 7 днів), канал Telegram із плейсхолдер-токеном (`assistant.enabled=true`). Ідемпотентний. Сам запускає API, якщо він не працює, і перезапускає його після створення каналу. Логін owner: tenant `beauty-lab-dev`, пошта й пароль у `.env.dev.local` (`DEV_OWNER_EMAIL`, `DEV_OWNER_PASSWORD`).
3. `scripts\dev\set-telegram-token.ps1`: **вводить користувач** токен бота з @BotFather (приховане введення). Токен шифрується в БД, API перезапускається, polling стартує. У `getWebhookInfo` бота `url` має бути порожнім (інакше 409 і пауза 60 с; `deleteWebhook` або `Channels__Telegram__Polling__DeleteWebhookOnConflict=true`).
4. `scripts\dev\set-anthropic-key.ps1`: **вводить користувач** ключ Anthropic (приховане введення). Ключ перевіряється (`verify`) і зберігається зашифрованим; ставиться `keySource=own`, чат `claude-haiku-5-5`, аналітика `claude-sonnet-5-5`, режим `confirm`, ліміти `monthlyTokenLimit=200000`, `monthlyRequestLimit=300` (без них власний ключ не вмикає чат).
5. `scripts\dev\status.ps1`: контейнер, API, GUID каналу, останні записи `telegram_poll*`, стан ключа ШІ (`status.chat.state` має бути `ready`). Далі пишіть боту (сценарії §21.10, п. 7-9).

`start-api.ps1` можна запускати окремо: він зупиняє лише процес за PID з `scripts/dev/.state/api.pid` (дерево, `taskkill /PID /T`), задає `ASPNETCORE_ENVIRONMENT=Development`, `Channels__UseMocks=false`, `Channels__Telegram__Polling__Enabled=true`, `Channels__Telegram__Polling__ChannelIds__0=<GUID>`, `ChatAssistant__Enabled=true`, `Beauty__AllowedOrigins=http://localhost:3100`. Логи: `scripts\dev\.state\api.log`.

**Зупинка:** `taskkill /PID <PID з scripts\dev\.state\api.pid> /T /F`; БД: `docker stop beautycrm-dev` (дані лишаються; повне очищення: `docker rm -f beautycrm-dev`, після чого повторити `setup-db.ps1`; якщо `.env.dev.local` лишився, паролі збережуться).

**Відкликання токена після тесту:** у @BotFather `/revoke` для бота і/або повторний `set-telegram-token.ps1` з новим значенням; щоб лише відключити канал, `PUT /api/beauty/channels/{GUID}` з `isActive=false` або видалення `.env.dev.local` і контейнера. Ключ Anthropic: `DELETE /api/beauty/ai/settings/keys/anthropic` (owner) або відкликання в консолі Anthropic.

**Очікувана поведінка:** з плейсхолдер-токеном у логу `telegram_poll_stopped ... http=401` і `telegram_polling_stopped ... reason=Unauthorized` (причина в токені, а не в конфігурації). Токен, URL `api.telegram.org/bot...` і ключ Anthropic у логах бути не повинні.

### 11.1 Нагадування, inbox і кнопки (TASK-745)
Порядок: `setup-db.ps1` (міграції + GRANT; повторювати після кожної нової міграції) -> `start-api.ps1` -> `seed.ps1` -> `start-worker.ps1` -> (за потреби) `set-telegram-token.ps1`, `set-anthropic-key.ps1` -> `status.ps1`.

- **Роль воркера `beautycrm_worker`** (LOGIN, NOSUPERUSER, NOBYPASSRLS, пароль `DB_WORKER_PASSWORD` у `.env.dev.local`) створює `setup-db.ps1`. GRANT: SELECT на `beauty_clients`, `beauty_client_contact_preferences`, `beauty_conversations`, `beauty_channels`, `beauty_appointments`, `beauty_locations`, `beauty_services`, `beauty_reminder_settings`; SELECT, UPDATE на `beauty_reminders`; SELECT, INSERT, UPDATE на `beauty_messages`; SELECT, INSERT на `beauty_call_tasks`, `beauty_chat_keyboards`; USAGE на sequences. Роль застосунку: `GRANT ... ON ALL TABLES` і `REVOKE UPDATE, DELETE ON beauty_ai_settings_audit`.
- **`start-worker.ps1`**: бере tenant `beauty-lab-dev` із БД (`WORKER_TENANT_IDS`), задає `WORKER_DATABASE_URL` (роль воркера), `Channels__EncryptionKey`, `Channels__UseMocks=false`, `WORKER_REMINDERS_ENABLED=true`, `WORKER_REMINDER_BUTTONS_ENABLED=true`, запускає `npm.cmd start` у `worker/` у фоні; PID у `scripts\dev\.state\worker.pid`, логи `worker.log` / `worker.err.log`. Змінні знімаються із сесії після старту. Повторний запуск зупиняє лише власний попередній процес за PID.
- **API** (`start-api.ps1`) додатково: `ChatAssistant__Enabled=true`, `ChatButtons__Enabled=true`. Канал (seed): `settings.assistant.enabled=true`, `buttons=true`; `PUT /api/beauty/settings/reminders` з `callEnabled=true`.
- **Токен і ключ.** Воркер зчитує токен каналу з БД при кожній відправці, тому після `set-telegram-token.ps1` перезапускати воркер не потрібно (API скрипт перезапускає сам). Ключ Anthropic потрібен лише API. Скрипти не лишають значень у змінних процесу чи логах.
- **Зупинка:** `taskkill /PID <PID з worker.pid> /T /F` (так само для `api.pid`); `status.ps1` показує обидва процеси, лічильники `scheduled` нагадувань і `open` завдань дзвінків.
- **Відкликання токена:** як у розділі 11 (`/revoke` у @BotFather або `isActive=false` каналу).

### 11.2 Вхід і додаткові салони (TASK-761)
- **`scripts\dev\show-login.ps1`**: друкує в консоль адресу входу `http://localhost:3100/login`, код бізнесу (slug зі state seed), пошту й пароль власника з `.env.dev.local`. Нічого не пише у файли/логи; **не вставляйте вивід у чат**.
- **`scripts\dev\create-salon.ps1 -Name "Назва" -Slug my-salon [-WithDemoData]`**: створює новий tenant через `POST /api/platform/tenants` (ключ `Auth__PlatformKey` береться з `.env.dev.local`, 6 модулів). Slug: `[a-z0-9-]`, 3-40. Пошта, імʼя та пароль (двічі, приховано, мін. 12 символів) запитуються інтерактивно; пароль не друкується.
- `-WithDemoData` логіниться власником і створює заклад (09:00-20:00, Europe/Kyiv), послугу «Стрижка» 60 хв/500 і майстра з графіком на 7 днів, привʼязаного до закладу й послуги.
- Помилки API: 409 (slug/пошта зайняті), 422 (валідація), 401/403 (ключ) показуються українською без ехо пароля.
- Автотест: змінні `BEAUTY_NEW_OWNER_EMAIL` і `BEAUTY_NEW_OWNER_PASSWORD` вимикають запити `Read-Host`; після використання зніміть їх із сесії.
- Видалення тестового tenant немає в API: лише через `psql` під `postgres` у контейнері `beautycrm-dev` (видалити рядки з `tenant_id` у всіх таблицях і запис `tenants`, з `session_replication_role=replica`).

### 11.3 Маркетинг: GRANT/REVOKE, kill switch, legal guard (TASK-791)
Після кожного `setup-db.ps1` (він ідемпотентний; запускати після міграції `beauty_marketing`):
- **app** (`beautycrm_app`): `REVOKE UPDATE, DELETE ON beauty_marketing_consent_events` (журнал згод лише SELECT/INSERT; поряд уже є така сама схема для `beauty_ai_settings_audit`). Решта таблиць SELECT/INSERT/UPDATE/DELETE.
- **worker** (`beautycrm_worker`, точкові права, без `ALL TABLES`): `SELECT` на `tenants` (лише свій рядок за RLS; потрібен для перевірки модуля `beauty_marketing`), `beauty_marketing_consents`, `beauty_promotions`, `beauty_promotion_services`; `SELECT, UPDATE` на `beauty_campaigns`, `beauty_campaign_recipients`; `SELECT, INSERT, UPDATE` на `beauty_coupons`; `SELECT, INSERT` на `beauty_chat_keyboards`; `SELECT, INSERT, UPDATE` на `beauty_messages` (вже було). Воркер НЕ має права писати згоди, акції та `beauty_marketing_consent_events`.
- Перевірка: інтеграційний тест `worker/src/integration/marketing-security.integration.test.ts` бере GRANT-секцію прямо зі `setup-db.ps1`, створює роль лише з цими правами й проганяє кампанію, winback, outbox (без `42501`; заборонені записи дають `42501`).

**Kill switch (від найшвидшого):**
1. Зупинити воркер (`taskkill /PID <PID з scripts\dev\.state\worker.pid> /T /F`): жодна відправка не піде.
2. `WORKER_CAMPAIGNS_ENABLED=false` / `WORKER_WINBACK_ENABLED=false` (за замовчуванням false; `start-worker.ps1` задає їх явно) і перезапуск: `pollCampaigns`/`pollWinback` нічого не роблять, а `pollOutbox` не бере повідомлення `campaign:*`/`winback:*` (лишаються `pending`, нічого не втрачається; після ввімкнення підуть далі).
3. Прибрати модуль `beauty_marketing` у tenant: воркер для нього не ставить у чергу й не відправляє (повідомлення лишаються `pending`, одержувачі `pending`). Відновлення модуля продовжує роботу.
4. Скасування кампанії (`cancel`) закриває `pending` одержувачів, а `queued` відсікає guard воркера перед відправкою.

**Legal guard (§26.16 п. 1):** воркер не стартує, якщо `ASPNETCORE_ENVIRONMENT` не `Development` (або `NODE_ENV=production`) і `WORKER_CAMPAIGNS_ENABLED=true` чи `WORKER_WINBACK_ENABLED=true` без `WORKER_MARKETING_LEGAL_APPROVED=true`. Аналогічний guard в API: `Marketing:LegalGateApproved`.

**Код купона.** Повний персональний код лежить у `beauty_messages.body` лише від постановки в чергу до `sent`/фінального `failed`; у тій самій транзакції, що ставить статус, воркер замінює його на `[код купона …XYZ]` (останні 3 символи). Обмеження: поки повідомлення `pending`/`sending` (зазвичай секунди, але до 3 спроб або до відновлення маркетингу), код у тілі є; історію ШІ й inbox у цей проміжок має маскувати backend (TASK-790). Збій між відправкою й записом статусу лишає код у тілі `sending` до реапера бекенду.

**Winback:** лише Telegram, вікно 09:00-20:00 за зоною закладу, ліміт `WORKER_MARKETING_DAILY_LIMIT` і частота 1/7 днів перевіряються під одним advisory lock `marketing-daily:{tenant}`; поллер щогодини (раніше раз на добу), ключ на лапс гарантує відсутність дублів. Невалідна IANA-зона закладу: одержувач/клієнт `skipped:invalid_timezone` (без щохвилинних помилок у лозі).

## 12. CI і staging-деплой (TASK-800, ADR-016)
- **CI** (`.github/workflows/ci.yml`, `pull_request` і `push` у `main`): джоби `frontend` (tsc, lint, vitest, build), `worker` (tsc, vitest), `backend` (`dotnet test backend` з `BEAUTY_TEST_REQUIRE_DB=1`, PostgreSQL піднімають самі тести через Testcontainers), `secret-scan` (gitleaks, закріплена версія з перевіркою SHA256, уся історія), `dependency-audit` (`npm audit --audit-level=high` для frontend і worker, `dotnet list package --vulnerable --include-transitive`, High/Critical блокують). Required status checks у branch protection налаштовує людина.
- **Образи:** `backend/Dockerfile`, `worker/Dockerfile`, `frontend/Dockerfile` (Next `standalone` вмикається змінною `NEXT_OUTPUT=standalone`; `NEXT_PUBLIC_*` передаються build-arg). Публікація в ghcr.io лише вручну: `.github/workflows/staging-images.yml` (workflow_dispatch; вимагає зелений CI для SHA; тег `sha-<повний SHA>`, деплой за digest; артефакт `migrations-<SHA>` з ідемпотентним SQL).
- **Проби API:** `GET /healthz` (liveness), `GET /readyz` (`SELECT 1` до БД, 503 без деталей), без автентифікації.
- **Розгортання** на спільному сервері, ізольований Compose-проєкт `beauty_staging`, порти лише 127.0.0.1, nginx-приклад, бекап, міграції, відкат: `deploy/staging/README.md` (всі кроки на сервері виконує людина; автоматичного CD немає).

## 12a. Універсальні посилання закладу (release-крок)
- **Потік:** код закладу (`publicCode`) -> `https://beauty.<DOMAIN>/l/{code}`. Якщо застосунок встановлено і домен у ньому
  прописано (`EXPO_PUBLIC_WEB_DOMAIN`), iOS/Android відкривають його напряму (universal link / App Link). Інакше відкривається
  веб-лендінг `frontend/app/(public)/l/[code]/page.tsx` з кнопками App Store / Google Play (`NEXT_PUBLIC_*_STORE_URL`).
- **Звідки взяти значення:** `APPLE_TEAM_ID` — Apple Developer -> Membership details -> Team ID (10 символів). `IOS_BUNDLE_ID` —
  `mobile/app.config.ts` (за замовч. `com.beautycrm.booking`). `ANDROID_PACKAGE` — там само. `ANDROID_SHA256_CERT_FINGERPRINTS` —
  Play Console -> Setup -> App signing (SHA-256 сертифіката підпису; додайте й upload-сертифікат, якщо потрібен) або
  `keytool -list -v -keystore <keystore>`; кілька значень через кому.
- **Задати:** `$SECRETS_DIR/web.env` (шаблон `deploy/staging/.env.web.example`), далі `$DC up -d web`. nginx-блоки `/l/` і `/.well-known/*`
  з `auth_basic off` — `deploy/staging/README.md` §6.3.
- **Без цих змінних** `/.well-known/apple-app-site-association` (немає `APPLE_TEAM_ID`) і `/.well-known/assetlinks.json` (немає
  `ANDROID_PACKAGE` або відбитків) повертають **404**: universal links не працюють, лендінг продовжує працювати.
- **Перевірка:** `curl -si https://beauty.<DOMAIN>/.well-known/assetlinks.json` (200, `application/json`, без редиректу і без 401).
  Apple кешує AASA через CDN, зміни можуть з'являтися не одразу; після зміни переінсталюйте застосунок.
