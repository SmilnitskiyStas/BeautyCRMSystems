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

### 2b. Склад `beauty_inventory` (ADR-025, контракт §32)

**Міграції** (дві, під `beautycrm_owner`, у цьому порядку):
1. `20261118155039_beauty_inventory`: 9 таблиць, RLS ENABLE+FORCE, CHECK, `REVOKE UPDATE, DELETE` на журналах. `Down` відмовляє (`inventory history exists`), якщо `beauty_stock_movements` непорожня.
2. `20261118170000_beauty_inventory_permissions`: backfill прав `inventory.*` (ідемпотентний, одна транзакція, `migrated` в `user_access_audit`), `PermissionCatalog.Version = 2`. `Down` відмовляє (`inventory.* permissions were edited manually`), якщо будь-який рядок змінено вручну.

Застосування на staging/prod — human gate: бекап перед міграцією (`deploy/staging/README.md`), `Down` лише вручну після рішення людини.

**GRANT після міграцій** (обов'язково, як для ADR-023):
- staging: `deploy/staging/db/grants.sql`; локально: `scripts/dev/setup-db.ps1`. Обидва містять `REVOKE UPDATE, DELETE ON beauty_stock_movements` і `beauty_stock_operations` від `beautycrm_app`. Синхронність перевіряє `worker/src/staging-grants-sync.test.ts`.
- Воркер у етапі 1 GRANT-ів складу не має.

**Схема для тестів:** `worker/src/testing/schema.sql` перегенеровано без BOM, LF. Після будь-якої зміни міграцій складу перегенерувати його, інакше падає `schema-drift.test.ts`. Перевірити, що файл починається з `CREATE`/`--`, а не з BOM.

**Увімкнення модуля для tenant.** Модуль не входить у `All`, тож за замовчуванням вимкнено.
- Локально: `scripts/dev/enable-inventory.ps1 [-Slug beauty-lab-dev]`. Скрипт робить `PATCH /api/platform/tenants/{id}` з `modules` (додає `beauty_inventory` до наявних, ідемпотентно).
- Staging і control plane: `PATCH` tenant `{modules}` (ADR-018, §27). Це єдиний шлях увімкнення для реального бізнесу, human gate.
- ShelfGuard: парна правка `setModules` — окрема задача в репозиторії ShelfGuard (TASK-832). Поки її немає, увімкнення лише через API.
- Опції модуля в `create-salon.ps1`, `seed.ps1`, `deploy/staging/create-tenant.sh` ще не додано (ADR-025 §2.1).

**Перевірки після деплою:**
1. `SELECT has_table_privilege('beautycrm_app','beauty_stock_movements','UPDATE')` і те саме для `beauty_stock_operations` → `f`.
2. Backfill: для кожного `admin`, `deputy`, `specialist` у `user_permissions` є рядки `inventory.view`, `inventory.manage`, `inventory.costs`; у `user_access_audit` є `migrated`.
3. API: `GET /api/beauty/inventory/scope` від owner → `200` з `expiringSoonDays`; для tenant без модуля той самий запит → `403 module_disabled`; відповідь має `Cache-Control: no-store`.

**Відкат:** `Down` складу на staging/prod не виконувати без бекапу і рішення людини (див. §2b вище). Після `Down` GRANT-и `beauty_stock_*` з `grants.sql` стають неактуальними для видалених таблиць.

### 2c. Склад етап 2a: норми, автосписання, сповіщення, воркер (ADR-025 §11, контракт §32.15–§32.26)

**Прапори (за замовчуванням вимкнено):**

| Змінна | Де | За замовч. | Призначення |
|---|---|---|---|
| `Inventory:AutoConsumptionEnabled` | backend (appsettings / env `Inventory__AutoConsumptionEnabled`) | увімкнено, якщо ключ відсутній | Аварійний вимикач автосписання при завершенні візиту. **Вимикає будь-яке присутнє значення, крім `true` (без урахування регістру)**, включно з `0`, `off`, `no` і порожнім рядком. Вимкнення не створює помилок: візити завершуються, лог `inventory_consumption_skipped reason=disabled` |
| `WORKER_INVENTORY_ENABLED` | worker `.env` | `false` | Воркер `pollInventory` (сповіщення low_stock/expiring, розв'язання). `true` — лише `true` (без урахування регістру); інакше воркер нічого не робить |
| `WORKER_INVENTORY_EXPIRY_DAYS` | worker | `30,14,7` | Пороги терміну (1..5 різних цілих 1..365, через кому). Невалідне значення → воркер не стартує (`Invalid WORKER_INVENTORY_EXPIRY_DAYS`). Прострочення (`bucket 0`) додається завжди |
| `WORKER_INVENTORY_LOCAL_HOUR` | worker | `8` | Локальна година закладу (0..23), з якої створюються нові сповіщення за добу (`low_stock`/`expiring`). Розв'язання йде щогодини без гейту. Невалідне → воркер не стартує |

Модуль `beauty_inventory` для tenant — окремо (`PATCH` tenant `{modules}`, §2b). Обидва прапори (backend, worker) і модуль діють незалежно; вимкнений модуль = жодних записів.

**Порядок увімкнення на staging** (кожен крок — human gate; дії на сервері тільки за рішенням людини):
1. Бекап БД (`deploy/staging/README.md`).
2. Міграція `beauty_inventory_stage2a` від `beautycrm_owner` (Up). Після `Up` перевірити наявність `beauty_staff_alerts` і `shortage_quantity`.
3. GRANT: `deploy/staging/db/grants.sql` (той самий набір, що в `setup-db.ps1`, тест `staging-grants-sync`). Перевірка: `SELECT has_table_privilege('beautycrm_app','beauty_stock_movements','UPDATE')` → `f`; для `beautycrm_worker` — `UPDATE` лише на `beauty_staff_alerts`, без `DELETE`.
4. Деплой API з `Inventory__AutoConsumptionEnabled` **не заданим** (автосписання увімкнено за замовчуванням коду). Якщо потрібно спершу вимкнути — задати значення `false`, а не порожнє.
5. Модуль `beauty_inventory` увімкнути **лише для тестового бізнесу** (`PATCH` tenant `{modules}`); перевірити `GET /api/beauty/inventory/scope` → `200`, для інших tenant → `403 module_disabled`.
6. Внести норми в тестовому тенанті, провести тестовий візит → перевірити операцію `service_consumption` і рухи.
7. Увімкнути `WORKER_INVENTORY_ENABLED=true` на worker; перевірити лог `inventory_alerts tenant=… locations=… created=… resolved=… capped=…`.
8. Тільки після цього — інші tenant (окреме рішення людини). Перед увімкненням на prod: закрити M1 і L1 звіту security (виправлено в TASK-893) і L3/L4 (у роботі).

**Моніторинг:**
- `inventory_consumption_failed appointment={id} sqlstate={code}` — рівень **Error**. Кожен такий запис = візит `completed` без списання (I3). Алерт на будь-який такий запис у проді; `sqlstate=55P03` означає блокування (`lock_timeout`), інші коди — помилка даних/БД.
- `inventory_alerts_location_failed tenant={id} location={id} errorCode={code}` — помилка воркера за закладом (очікується після L3; до того — тиша від `catch`, слідкувати за `inventory_alerts` `locations` без `created`/`resolved`).
- `inventory_consumption appointment=… op=… products=… shortages=…` — успішні списання; `inventory_consumption_skipped … reason=…` — пропуски (`disabled|module_off|no_norms|no_eligible_products|already_consumed`).
- Логи без назв товарів, цін і кількостей; ПД клієнтів немає.

**Процедура: пропущене списання через технічний збій** (запис `inventory_consumption_failed` для візиту):
1. Знайти візит за `appointment=` у логах; переконатися, що операції `service_consumption` для `appointment_id` немає (`beauty_stock_operations`).
2. Не повторювати `completed` (статус уже `completed`, повтор нічого не спише).
3. Зняти фактичні залишки через інвентаризацію закладу (`POST /stocktakes` → рядки → `post`). Інвентаризація фіксує фізичний залишок і обнуляє дефіцит; розбіжність обробляється штатно.
4. Для справжнього списання використати списання `write_off` з причиною `internal_use` або `correction` з `note` (з посиланням на візит).
5. Зафіксувати випадок у task log або інциденті.

**Відкат:**
- `Down` міграції `beauty_inventory_stage2a` відмовляє (`inventory history exists`), якщо є рух `is_shortage` або операція з `appointment_id`. Це захист від втрати історії.
- Видалення історії 2a — лише після бекапу і окремого рішення людини (human gate).
- Повернення до стану етапу 1 **не є простим**: `Down` 2a відмовляє, доки є рухи `is_shortage` або операції `service_consumption` з `appointment_id`. Їх видалення — ручна робота власника БД (`beautycrm_owner`; app-роль не має `DELETE` на журналах), і воно має відновлювати `quantity` партій і рівнів, інакше інваріанти §32.17 зламаються. Окремого скрипта очищення немає.
- Порядок (лише з бекапом і рішенням людини): (1) вимкнути `WORKER_INVENTORY_ENABLED` і `Inventory:AutoConsumptionEnabled=false`; (2) зняти з журналу або компенсувати рухи 2a (`is_shortage` і `service_consumption`) разом із відновленням партій і рівнів; (3) видалити `beauty_staff_alerts`-рядки 2a; (4) `Down` міграції 2a; (5) прибрати GRANT-и 2a з `grants.sql`/`setup-db.ps1`.
- Повторний `Up` 2a після відкату до етапу 1 — лише після кроку (2), інакше CHECK `(batch_id IS NULL) = is_shortage` і сальдо не зійдуться з історією.
- Відкат спершу відпрацювати на копії БД (Up → Down → Up). Доки скрипт компенсації не написано й не перевірено, відкат на staging/prod — лише через відновлення з бекапу.

## 3. Секрети й змінні середовища
Згенеруйте (`openssl rand`): `Auth__JwtSigningKey` (`-base64 48`), `Auth__PlatformKey` (`-base64 36`), `Channels__EncryptionKey` (`-base64 32`, base64 від 32 байт; при заміні ключа вже збережені секрети каналів перестануть розшифровуватись). Файл `.env` у `.gitignore`, комітити заборонено. Повний перелік змінних і коментарі: `.env.example`.

| Змінна | Призначення |
|---|---|
| `ConnectionStrings__Default` | runtime-рядок, роль `beautycrm_app` |
| `ConnectionStrings__Migrator` | лише для `dotnet ef` (власник) |
| `Auth__JwtSigningKey`, `Auth__PlatformKey` | без них API не стартує / ендпоінти оператора вимкнені |
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
