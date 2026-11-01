# Beauty CRM: локальний запуск (runbook)

Версія 1.0 (2026-10-08). Кожен крок нижче виконано вручну на Windows 11 (Git Bash + PowerShell + Docker Desktop) і дав описаний результат. Redis для локального запуску **не потрібен**: worker читає чергу безпосередньо з PostgreSQL (див. ADR-003).

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

## 2. Міграції (під власником схеми)
PowerShell:
```powershell
cd backend
$env:ConnectionStrings__Migrator = "Host=localhost;Port=55432;Database=beautycrm;Username=beautycrm_owner;Password=<owner-pw>"
dotnet ef database update --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Infrastructure
```
Очікувано: застосовуються 8 міграцій (від `add_beauty_schema` до `beauty_locations_and_cancellation_history`), далі `Done.`, у схемі 25+ таблиць. Потім видайте права ролі застосунку (потрібно повторювати після кожної міграції з новими таблицями):
```bash
docker exec beautycrm psql -U postgres -d beautycrm \
  -c "GRANT USAGE ON SCHEMA public TO beautycrm_app" \
  -c "GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO beautycrm_app" \
  -c "GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app"
```

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
| `WORKER_DATABASE_URL`, `WORKER_TENANT_IDS`, `WORKER_MARKETING_DAILY_LIMIT` | worker |
| `NEXT_PUBLIC_API_URL`, `API_URL`, `NEXT_PUBLIC_TENANT_SLUG` | frontend (читаються під час **build**) |
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
- Access-токен деактивованого користувача діє до закінчення (15 хв) на ендпоінтах поза записами й відсутностями.
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
