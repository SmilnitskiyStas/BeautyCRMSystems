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
