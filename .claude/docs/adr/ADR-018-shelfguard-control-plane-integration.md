## ADR-018: Інтеграція з ShelfGuard як control plane провайдера (provisioning tenant, призупинення, запрошення власника, заявки)

**Date:** 2026-11-04
**Status:** accepted (власник, 2026-11-04)
**Author:** project-architect
**Deciders:** власник продукту (рішення §1.2 і відповіді §9.1 від 2026-11-04 затверджені; ADR в цілому чекає human gate — auth/security boundary)

### 1. Context

#### 1.1 Що є зараз (перевірено в коді)

- `backend/BeautyCrm.Api/Auth/PlatformKeyAuthentication.cs` — схема `PlatformKey`, заголовок `X-Platform-Key`,
  значення з `Auth:PlatformKey` (.env). Короткий або не заданий ключ означає, що ендпоінти вимкнені (fail closed).
  JWT користувача platform-ендпоінти не відкриває.
- `POST /api/platform/tenants` (тести `backend/BeautyCrm.Tests/Auth/AuthApiTests.cs`): тіло
  `{ name, slug, modules?, owner: { email, fullName, password } }` → `201` (DTO з `modules`; без `modules` вмикаються всі beauty-модулі);
  `409 tenant_slug_taken`; `422` для slug, невідомого модуля, короткого пароля.
- `PATCH /api/platform/tenants/{tenantId}` з `{ modules }` або `{ status: "suspended" }` → `204`. Після призупинення
  API tenant віддає `403`, `POST /api/auth/login` і `POST /api/auth/refresh` — `401`.
- Помилки мають поле `code`.

Не перевірено (див. §9): де лежить контролер/endpoint-група platform, назва таблиці реєстру tenant, як provisioning
обходить RLS під час створення owner, чи є повернення зі `suspended` в `active`, чи є журнал аудиту platform-дій.

#### 1.2 Затверджені рішення власника (не переглядаються)

1. ShelfGuard (окремий репозиторій `CRMProductSystems`) — центральна панель провайдера. Адмін ShelfGuard створює
   бізнес-клієнта Beauty CRM (tenant і власника), призупиняє та відновлює його. Видалення буде пізніше, м'яке.
2. S2S ShelfGuard→Beauty: окремий ключ, HMAC-підпис запиту (timestamp + nonce, вікно ~5 хв, захист від replay).
   Ключ є лише на сервері ShelfGuard. Beauty зберігає ідентифікатор/хеш ключа, підтримує ротацію. Браузер ключа не бачить.
   `X-Platform-Key` лишається для ручних скриптів.
3. Власник отримує одноразове запрошення з терміном дії й сам задає пароль. Пароль через ShelfGuard не проходить.
   Токен зберігається хешем. Email-сервісу може не бути: адмін ShelfGuard бачить посилання й передає його сам (MVP).
4. Ідемпотентність: `external_ref` (id підприємства в ShelfGuard) з унікальним індексом.
5. Призупинення блокує вхід і API tenant, дані зберігаються. Відновлення працює навпаки. Кожна дія потрапляє в аудит з актором.
6. Заявки «співпраця / покращити / не працює» надсилає форма всередині Beauty CRM через backend Beauty у ShelfGuard
   (LandingLead з типом і критичністю; гілка ShelfGuard `feat/beauty-leads-telegram` уже має `source=beauty`, критичність і Telegram-пуш).
   Заявка містить tenant і користувача.
7. Поза MVP: SSO, Telegram-бот і AI-агенти ShelfGuard, маркетингові AI-агенти, видалення tenant.

#### 1.3 Технічне уточнення до рішення 2

HMAC-SHA256 перевіряється лише тим самим секретом. Тому зберегти в Beauty тільки хеш секрету неможливо.
У межах рішення 2 це трактується так:
- секрет (по одному на `kid`) зберігається лише в server-side env Beauty, як інші секрети (ADR-016), і ніколи не потрапляє в БД, логи, відповіді чи frontend;
- у БД, аудиті й логах Beauty є лише `kid` і відбиток `sha256(secret)` (перші 12 hex), щоб розрізняти ключі під час ротації.

Власник підтвердив це трактування (§9.1, D1). Ed25519 не потрібен.

### 2. Decision (огляд)

1. Додається окрема група ендпоінтів **control plane** `/api/control/v1/*` з новою схемою автентифікації
   `ControlPlaneHmac`. `X-Platform-Key` цю групу не відкриває, а HMAC не відкриває `/api/platform/*`.
2. `/api/platform/*` з `X-Platform-Key` лишається для ручних скриптів. **Ззовні він закритий:** на публічному vhost nginx
   стоїть `deny all` для `location /api/platform/`. Скрипти звертаються до Kestrel на сервері (loopback або docker-мережа).
   Новий прапорець `Auth:PlatformKeyEnabled` (за замовчуванням `true` у Development і `false` у Staging/Production, якщо його не ввімкнули явно)
   дає змогу повністю вимкнути схему.
3. Provisioning, призупинення й зміна модулів у control plane використовують **ті самі доменні операції**, що й `/api/platform/tenants`
   (створення tenant + owner, зміна статусу, зміна модулів). Логіка не дублюється. Відмінності control plane: власник створюється без пароля
   (стан `invited`), є `external_ref`, ідемпотентність, аудит з актором ShelfGuard.
4. Власник активується через **invite-token** (§3.3). Посилання повертається лише у відповіді ShelfGuard. ShelfGuard показує його
   адміну один раз і не зберігає відкритим текстом.
5. **Заявки** з Beauty: `POST /api/beauty/feedback` (JWT користувача) записує заявку в tenant-таблицю та подію в transactional outbox
   (ADR-003). Worker доставляє її в ShelfGuard підписаним HMAC запитом з **окремим** ключем напрямку Beauty→ShelfGuard.
6. Аудит platform-дій ведеться в append-only таблиці `platform_audit_log` (§4).

### 3. Контракт ендпоінтів Beauty

Формат помилок — чинний (`{ "code": "...", ... }`). Усі тіла — `application/json; charset=utf-8`, ліміт тіла 64 KB.

#### 3.1 Підпис запиту ShelfGuard→Beauty (`ControlPlaneHmac`, версія v1)

Заголовки (обов'язкові):

| Заголовок | Значення |
|---|---|
| `X-BCRM-Key-Id` | `kid`, `[a-z0-9-]{3,32}` |
| `X-BCRM-Timestamp` | Unix-секунди UTC, десяткове ціле |
| `X-BCRM-Nonce` | 22..64 символи `[A-Za-z0-9_-]`, щонайменше 128 біт випадковості, новий для кожного запиту |
| `X-BCRM-Signature` | `v1=<base64url без padding від HMAC-SHA256(secret[kid], canonical)>` |
| `X-Request-Id` | необов'язковий, до 64 символів; пишеться в аудит |

Canonical string складається з рядків через `\n` (LF), без кінцевого переводу рядка:

```
BCRM-HMAC-SHA256-v1
{kid}
{timestamp}
{nonce}
{METHOD}                      # верхній регістр: GET | POST
{path}                        # шлях саме так, як його бачить Kestrel, напр. /api/control/v1/tenants/sg-123/suspend
{query}                       # сирий query без '?'; для v1 завжди порожній рядок (query у v1 заборонений → 400)
{hex_lower(sha256(body))}     # байти тіла як є; порожнє тіло → sha256("")
```

Перевірка на боці Beauty, строго в такому порядку:
1. Заголовки на місці й мають правильний формат.
2. `kid` відомий і активний.
3. `|now − timestamp| ≤ 300 с`.
4. Тіло ≤ 64 KB.
5. Підпис порівнюється за сталий час (`CryptographicOperations.FixedTimeEquals`).
6. Лише після валідного підпису nonce записується в `control_plane_nonces` (`INSERT … ON CONFLICT DO NOTHING`). Якщо вставлено 0 рядків, це replay.
   Nonce зберігається 15 хв, тобто довше за подвоєне вікно.

Будь-яка невдача дає `401 control_auth_failed` з однаковим тілом. Справжня причина (`missing_header | unknown_key | timestamp_skew |
body_too_large | bad_signature | nonce_replayed`) пишеться лише в лог/аудит без значень підпису й секрету. Сервер ставить заголовок
`X-BCRM-Server-Time`, щоб ShelfGuard міг діагностувати розсинхрон годинника.

Ключі задаються конфігурацією `ControlPlane:Inbound:Keys:{kid}:Secret` (≥ 32 байти випадковості, base64) і
`ControlPlane:Inbound:Keys:{kid}:Status` = `active | retiring`. Секрет коротший за 32 байти або відсутність активних ключів
вимикають групу (fail closed, як `PlatformKey`).
**Ротація:** додати `kid2` (active), ShelfGuard переходить на `kid2`, `kid1` стає `retiring` (приймається ще ≤ 7 днів), потім `kid1` видаляється з env і робиться рестарт.
Ротація — human gate.

Обов'язковий **тестовий вектор** (фіксовані kid/secret/timestamp/nonce/body → очікуваний підпис) публікується артефактом
`.ai/artifacts/contracts/ADR-018-hmac-test-vector.json` і перевіряється тестами в обох репозиторіях.

#### 3.2 Ендпоінти control plane (`/api/control/v1`, схема `ControlPlaneHmac`)

Ідентифікатор у шляху — `externalRef` (id підприємства ShelfGuard), `[A-Za-z0-9_-]{1,64}`.
Змінні запити містять `actor: { id: string(≤64), displayName: string(≤120) }`. Це адмін ShelfGuard; дані йдуть в аудит. Без `actor` повертається `422 actor_required`.

**`POST /api/control/v1/tenants`** — створити tenant і запрошення власника.

```json
{ "externalRef": "sg-123", "name": "Salon X", "slug": "salon-x", "modules": ["beauty_booking"],
  "owner": { "email": "owner@x.test", "fullName": "Ім'я Прізвище" },
  "actor": { "id": "sg-admin-7", "displayName": "Admin" } }
```

- `201` → `{ tenantId, externalRef, slug, status: "active", modules[], owner: { userId, email, state: "invited" }, invite: { url, expiresAt } }`.
- Ідемпотентність: tenant з таким `externalRef` уже є, а `slug` і `owner.email` збігаються → `200` з поточним станом, `invite: null`
  (нове посилання видає лише §owner-invite), заголовок `Idempotent-Replayed: true`. Відрізняються → `409 external_ref_conflict`.
  Паралельні дублікати ловить унікальний індекс: той, хто програв, перечитує стан і отримує `200`.
- `409 tenant_slug_taken` — slug зайнятий іншим tenant.
- `422` — `invalid_slug | unknown_module | invalid_email | invalid_name | invalid_external_ref | actor_required` (ті самі правила slug/модулів, що в `/api/platform/tenants`).
- Пароля в тілі немає. Поле `owner.password`, якщо його передали, дає `422 password_not_allowed`.

**`GET /api/control/v1/tenants/{externalRef}`** → `200 { tenantId, externalRef, slug, name, status: "active"|"suspended", modules[],
suspendedAt?, owner: { userId, email, state: "invited"|"active", inviteExpiresAt? }, createdAt }`, або `404 tenant_not_found`.
Бізнес-дані tenant (клієнти, записи, суми) **не повертаються**.

**`POST /api/control/v1/tenants/{externalRef}/suspend`** — тіло `{ reason: string(≤500), actor }` → `200 { status: "suspended", suspendedAt }`.
Повторний виклик дає `200` без нового запису аудиту про зміну, лише `noop`. Інші відповіді: `404 tenant_not_found`, `422 reason_required`.
Ефект той самий, що в чинного `PATCH status=suspended`: API tenant → `403`, login/refresh → `401`, refresh-токени tenant відкликаються. Дані не змінюються.
Публічний запис і віджет призупиненого tenant (каталог, слоти, створення запису, чат віджета) відповідають `403 tenant_suspended`.
Публічний UI показує «Запис тимчасово призупинено» (D2). Вхідні повідомлення каналів, нагадування й кампанії — Q2b (пропозиція, потребує підтвердження).

**`POST /api/control/v1/tenants/{externalRef}/resume`** — тіло `{ actor }` → `200 { status: "active" }`. Повторний виклик — `200` noop, невідомий tenant — `404`.
Сесії не відновлюються: користувачі входять знову.

**`PUT /api/control/v1/tenants/{externalRef}/modules`** (D4) — повний набір увімкнених модулів. Тіло `{ modules: string[], actor }` → `200 { modules[] }`.
- Набір збігається з поточним → `200` noop.
- Помилки: `404 tenant_not_found`, `422 unknown_module | modules_required` (порожній масив), `422 actor_required`.
- Дозволено й для призупиненого tenant.
- Працює на тій самій доменній операції, що й `PATCH /api/platform/tenants/{id}` `{ modules }`. Ефект той самий: вимкнений модуль → `403 module_disabled`.
- Аудит `tenant.modules_changed`, `details: { before[], after[] }`.

**`POST /api/control/v1/tenants/{externalRef}/owner-invite`** — перевипуск запрошення. Тіло `{ actor }` → `201 { url, expiresAt }`.
Попереднє невикористане запрошення відкликається. Якщо власник уже активований, повертається `409 owner_already_active`
(скидання пароля — окрема функція поза ADR). Також можливі `404` і `409 tenant_suspended`.

Rate limit групи: 60 запитів/хв на `kid` (`429 rate_limited`, `Retry-After`).

#### 3.3 Запрошення власника (публічні, без автентифікації, в Beauty)

- Токен: 32 байти CSPRNG, base64url. У БД зберігається лише `sha256(token)`. TTL за замовчуванням 72 год (`ControlPlane:InviteTtlHours`).
  На одного користувача є не більше одного активного запрошення.
- URL: `{PublicAppUrl}/invite#t={token}`. Токен стоїть у **фрагменті**, тому не потрапляє в логи nginx/Kestrel і в Referer.
  Сторінка `/invite` має `Referrer-Policy: no-referrer` і `noindex`.
- **`POST /api/auth/invite/inspect`** `{ token }` → `200 { tenantName, email (замаскований: o***@x.test), expiresAt }`.
  Невідомий, прострочений, використаний або відкликаний токен дає `410 invite_invalid` (один код для всіх випадків, щоб не допомагати перебору).
  Tenant призупинений → `403 tenant_suspended`.
- **`POST /api/auth/invite/accept`** `{ token, password }` → `200` з тілом у тій самій формі, що й `POST /api/auth/login`
  (одразу вхід). Помилки: `410 invite_invalid`, `422` за чинною політикою пароля, `403 tenant_suspended`.
  Погашення атомарне: `UPDATE … SET used_at = now() WHERE token_hash = $1 AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now() RETURNING …`
  у тій самій транзакції, що встановлює пароль і переводить користувача в `active`.
- Rate limit: 10/хв на IP для обох ендпоінтів.

#### 3.4 Заявки з Beauty у ShelfGuard

**`POST /api/beauty/feedback`** (JWT, будь-яка роль, tenant `active`):
`{ type: "cooperation"|"improvement"|"bug", severity: "low"|"normal"|"high"|"critical", message: string(10..2000), pageUrl?: string(≤500) }`
→ `202 { id }`. Помилки: `422 invalid_type | invalid_severity | invalid_message`, `429 rate_limited` (5/год на користувача, 30/добу на tenant).
`severity` має сенс для `bug`. Для інших типів сервер ставить `normal`, якщо поле не передали.

Доставка (worker, TASK-821; Q4 вирішено, шлях і формат зафіксовані в `beauty-contracts.md` §27.9): worker бере рядок
`beauty_platform_feedback` з `delivery_status=pending` і робить `POST {SHELFGUARD_BASE_URL}/api/integrations/beauty/feedback`
з підписом `ControlPlaneHmac` v1 (§3.1) ключем напрямку Beauty→ShelfGuard `SHELFGUARD_FEEDBACK_KEY_ID` / `SHELFGUARD_FEEDBACK_SECRET`
(окремий від вхідного). Тіло — плоский JSON:

```json
{ "sourceTenantRef": "sg-123", "tenantName": "Salon X", "type": "bug", "message": "...", "feedbackId": "uuid",
  "severity": "high", "contact": "owner@x.test", "userRole": "admin", "pageUrl": "/admin/calendar" }
```

`feedbackId` — ключ ідемпотентності на боці ShelfGuard. `sourceTenantRef` — `tenants.external_ref`, а якщо він `NULL`, uuid tenant.
Відповіді: `201`/`200` (replayed) — delivered; `401`, `422`, інші `4xx` — failed без повторів; `429`, `408`, `5xx`, мережа — повтор
з backoff 60с×2^n (до 4 год, ±20% jitter), максимум 12 спроб, потім `failed` і алерт у лог.

### 4. Модель даних (міграції Beauty)

Назви орієнтовні. Таблицю реєстру tenant і користувачів database-engineer звіряє з кодом (Q5).

| Зміна | Деталі | RLS |
|---|---|---|
| Реєстр tenant: `external_ref text NULL`, `external_source text NULL` (`'shelfguard'`), `suspended_at timestamptz NULL`, `suspended_reason text NULL` | `UNIQUE (external_source, external_ref) WHERE external_ref IS NOT NULL`; CHECK формату `external_ref` | `tenants` має FORCE RLS: `tenant_self` (за `app.tenant_id`) для запису і SELECT-політика `tenants_control_lookup` (за транзакційним `app.tenant_external_ref`, лише `external_source='shelfguard'`) для пошуку за `external_ref` |
| Користувачі: `password_hash` стає NULL-able, стан `invited` | CHECK: `password_hash IS NOT NULL OR state = 'invited'`; login для `invited` → `401` | без змін (tenant RLS) |
| `platform_owner_invites` | `id uuid PK, tenant_id uuid FK, user_id uuid FK, token_hash bytea UNIQUE, expires_at, used_at NULL, revoked_at NULL, created_at, created_by_actor text`; частковий унікальний індекс «одне активне на user_id» | платформна (без tenant RLS), доступ лише через platform/auth-шлях |
| `platform_audit_log` | `id bigserial, occurred_at, action` (`tenant.created, tenant.create_replayed, tenant.suspended, tenant.resumed, tenant.modules_changed, owner_invite.issued, owner_invite.revoked, owner_invite.accepted, control_auth.failed`), `tenant_id NULL, actor_type` (`shelfguard_admin, platform_key, owner, system`), `actor_id, actor_display, key_id, request_id, ip inet, details jsonb` | платформна; у ролі застосунку відкликано `UPDATE/DELETE` (append-only) |
| `control_plane_nonces` | `key_id text, nonce text, seen_at timestamptz`, PK `(key_id, nonce)`; очищення > 15 хв робить worker або періодична задача | платформна |
| `beauty_platform_feedback` | `id, tenant_id, user_id, type, severity, message, page_url, delivery_status` (`pending, delivered, failed`), `attempts, delivered_at, external_lead_id NULL, created_at` | tenant RLS + FORCE RLS (DO-блок, як у ADR-017) |

`details` в аудиті не містить секретів, токенів, паролів і тексту заявок. Email власника замінюється на `user_id`.
`worker/src/testing/schema.sql` синхронізується. Up→Down→Up перевіряється на копії. Down для `password_hash NULL` — human gate (втрата стану `invited`).

### 5. Безпека

- **Межа довіри.** Секрети обох напрямків є лише в server-side env (Beauty — ADR-016 bootstrap-env, ShelfGuard — його сервер).
  Їх немає в репозиторії, `NEXT_PUBLIC_*`, логах і відповідях. Frontend Beauty і браузер ShelfGuard ключів не бачать.
- **Шари:** (1) HMAC + вікно + nonce; (2) nginx IP-allowlist для `location /api/control/` (лише egress-IP сервера ShelfGuard, інше — `403`)
  як **додатковий** шар, бо HMAC перевіряється завжди; (3) `deny all` ззовні для `/api/platform/`; (4) rate limit (§3.2–3.4);
  (5) nginx не переписує шлях `/api/control/…`, бо canonical path має збігатися з тим, що бачить Kestrel.
- **PII.** Control plane передає лише назву, slug, email і ім'я власника. Бізнес-даних tenant ShelfGuard не отримує.
  Заявка передає ім'я, email і роль автора та текст. Форма показує користувачу примітку, що заявка йде провайдеру (юридичний текст — human gate).
  Логи містять маскований email і не містять тексту заявки.
- **Аудит:** кожна control-plane мутація (зокрема noop і replay створення), видача, відкликання й погашення запрошення, невдала автентифікація
  (з агрегацією: не більше 1 запису/хв на IP+причину) записуються з актором, `kid` і `request_id`.
- **Invite:** high-entropy токен, хеш у БД, одноразовий, TTL, фрагмент URL, rate limit, єдиний код помилки.
- **Fail closed:** немає валідних ключів — група вимкнена. Доставка заявок вимкнена (`SHELFGUARD_FEEDBACK_ENABLED` не `true` або конфіг невалідний) — feedback приймається (`202`) і чекає зі статусом `pending`.

### 6. Наслідки для RLS

- Control-plane операції — **платформні**: вони працюють з реєстром tenant, `platform_owner_invites`, `platform_audit_log`, `control_plane_nonces`.
  `platform_owner_invites`, `platform_audit_log`, `control_plane_nonces` — таблиці без tenant RLS. `tenants` — з FORCE RLS (§4): пошук за `external_ref`
  іде через SELECT-політику `tenants_control_lookup` (транзакційний `app.tenant_external_ref`), інші операції з реєстром — після встановлення `app.tenant_id`.
- Tenant-таблиці, які зачіпає control plane: `users` (створення owner під час provisioning і встановлення пароля під час accept) та `tenants` (статус, модулі).
  Для цього транзакція після визначення `tenant_id` (з реєстру або з запрошення) встановлює `set_config('app.tenant_id', $tenant, true)`,
  тобто **transaction-local**, і працює під звичайною роллю застосунку з RLS. Це той самий механізм, що має вже використовувати `/api/platform/tenants`;
  backend-developer підтверджує це й використовує його повторно (Q5).
- **Заборонено:** роль з `BYPASSRLS`, окремий superuser-конект для control plane, `SET` (session-level) замість `set_config(..., true)` під пулом з'єднань.
- Control plane не читає бізнес-таблиці `beauty_*`. Це закріплює тест: ендпоінти не повертають і не змінюють дані іншого tenant.
- `beauty_platform_feedback` — звичайна tenant-таблиця. Worker читає outbox за чинною моделлю ADR-003.

### 7. Consequences

Positive:
- ShelfGuard керує життєвим циклом клієнта без доступу до бізнес-даних і паролів.
- Підписані запити з вікном і nonce: перехоплений запит не можна відтворити, ключ можна ротувати без простою.
- Ідемпотентне створення витримує повтори й таймаути ShelfGuard.
- Заявки не губляться, коли ShelfGuard недоступний (outbox).
- Ручні скрипти з `X-Platform-Key` працюють далі, але без зовнішньої поверхні атаки.

Negative / accepted trade-offs:
- Симетричний HMAC: секрет є на обох сторонах (§1.3).
- Посилання-запрошення передається вручну. Відповідальність за канал передачі несе адмін ShelfGuard; при компрометації посилання перевипускають (TTL 72 год).
- Дві поверхні platform-доступу (`/api/platform` і `/api/control`) треба підтримувати узгодженими через спільні доменні операції.
- Nonce-таблиця в PostgreSQL дає запис на кожен control-plane запит. При 60/хв це прийнятно.
- Одночасної зміни IP ShelfGuard nginx не витримає: потрібна процедура оновлення allowlist (human gate).

### 8. Alternatives considered

- **Віддати ShelfGuard `X-Platform-Key`** — відхилено: статичний bearer-секрет без захисту від replay, спільний з ручними скриптами, ротація ламає всіх одразу.
- **Асиметричний підпис (Ed25519) замість HMAC** — відхилено власником (D1): секрет HMAC у серверних env Beauty прийнятний. Формат заголовка
  `X-BCRM-Signature: v{n}=…` лишає змогу додати іншу версію пізніше новим ADR.
- **mTLS** — відхилено для MVP: керування сертифікатами й nginx-конфігом дорожче за користь. Можливий як додатковий шар пізніше.
- **OAuth2 client credentials / JWT від ShelfGuard** — відхилено: потрібен IdP і керування ключами, а для одного клієнта вигоди мало.
- **ShelfGuard задає пароль власника** — відхилено власником: пароль проходив би через третю систему.
- **Надсилати запрошення email** — відхилено для MVP: немає email-сервісу. Ручне посилання — рішення MVP, email додається пізніше без зміни моделі токена.
- **Заявки з браузера напряму в ShelfGuard** — відхилено: ключ у браузері, CORS і спам, ShelfGuard не може перевірити tenant і користувача.
- **Синхронний виклик ShelfGuard з backend під час `POST /feedback`** — відхилено: недоступність ShelfGuard означала б втрату заявки або помилку користувачу; outbox дає повтори.
- **Роль `BYPASSRLS` для control plane** — відхилено: ламає модель ізоляції ADR-002.
- **Ідемпотентність через заголовок `Idempotency-Key`** — відхилено: природний ключ `external_ref` уже є й унікальний.

### 9. Прийняті рішення та відкриті питання

#### 9.1 Прийняті рішення (відповіді власника, 2026-11-04)

ADR прийнято власником 2026-11-04 («ADR-018 прийнято»). Окремі human gates §10 (секрети, nginx, міграції на staging, deploy, юридичний текст) лишаються.

- **D1 (був Q1).** Секрет HMAC зберігається лише в серверних env Beauty. У БД, аудиті й логах є тільки `kid` і відбиток. Ed25519 не потрібен
  (лишається в §8 як відхилена альтернатива). Розблоковує TASK-817.
- **D2 (був Q2, частково).** Призупинений tenant: публічний запис і віджет відповідають `403 tenant_suspended`, UI показує «призупинено» (§3.2 suspend).
  Вхідні повідомлення каналів, нагадування й кампанії власник **не підтверджував** — див. Q2b.
- **D3 (був Q3).** Поки `source = "beauty"`. Внутрішні назви продуктів визначаються пізніше й не впливають на MVP-контракт.
- **D4 (був Q7).** Зміна модулів tenant із ShelfGuard входить у MVP: `PUT /api/control/v1/tenants/{externalRef}/modules` (§3.2), підписаний, з `actor` і аудитом.
- **D5 (був Q6).** TTL запрошення — 72 год, вікно `retiring`-ключа під час ротації — 7 днів. Підтверджено.

#### 9.2 Відкриті питання

- **Q2b (пропозиція, потребує підтвердження).** Поведінка призупиненого tenant для worker і каналів. Пропозиція: вхідні повідомлення каналів приймати
  й зберігати без AI-відповідей, нагадування й кампанії не надсилати. Не блокує TASK-818/834. Блокує лише окрему реалізацію поведінки worker і каналів:
  у поточному DAG задачі для неї немає, її додають після підтвердження. До того часу поведінка worker для призупиненого tenant лишається чинною (не перевірено, що вона робить).
- **Q4 — вирішено.** Доставка заявок Beauty→ShelfGuard: шлях `POST /api/integrations/beauty/feedback`, підпис `ControlPlaneHmac` v1 (ключ напрямку Beauty→ShelfGuard), плоске тіло з `type` і `severity` як рядками, відповіді й повторні спроби за `beauty-contracts.md` §27.9 (worker `worker/src/feedback/`). Відповідність полів LandingLead ShelfGuard — на боці ShelfGuard (TASK-829).
- **Q5 (технічне; закривається звіркою з кодом на початку TASK-816 і TASK-818, рішення власника не потрібне).** Не перевірено:
  - назви таблиць реєстру tenant і користувачів;
  - механізм встановлення `app.tenant_id` у чинному provisioning;
  - повернення `suspended → active` і відкликання refresh-токенів;
  - чи `password_hash` зараз NOT NULL;
  - чи є журнал аудиту, який можна використати повторно;
  - розташування контролера `/api/platform`;
  - як зараз публічний запис і віджет поводяться для призупиненого tenant.
- **Q8 (блокує фінальний текст примітки в TASK-823 і human gate у TASK-830; реалізацію не блокує — у формі плейсхолдер).** Юридичний текст: обробка даних
  клієнтів провайдером, передача імені/email автора заявки в ShelfGuard, примітка у формі.

### 10. Human gates

- Генерація й розміщення секретів обох напрямків (env Beauty staging/prod, env ShelfGuard), ротація ключів.
- Зміни nginx: allowlist `/api/control/`, `deny` для `/api/platform/` ззовні.
- Застосування міграцій на staging/prod, особливо Down для `password_hash NULL`.
- Deploy на staging і prod, увімкнення інтеграції в ShelfGuard prod.
- Юридичний текст про дані (Q8) і текст примітки у формі заявки.
- Прийняття цього ADR (auth/security boundary) — статус `accepted` лише після підтвердження власника.

### 11. Scope of truth

ADR авторитетний для: схеми `ControlPlaneHmac` і canonical string v1, шляхів і кодів `/api/control/v1/*`, `/api/auth/invite/*`,
`/api/beauty/feedback`, моделі invite-токена, `external_ref` і ідемпотентності створення, правил доступу до `/api/platform/*` ззовні,
таблиць `platform_owner_invites`, `platform_audit_log`, `control_plane_nonces`, `beauty_platform_feedback`, підходу до RLS для platform-операцій.
Детальні DTO переносяться в `.claude/docs/beauty-contracts.md` (TASK-815). Розбіжність між ними й цим ADR вирішується новим ADR.
Код ShelfGuard (репозиторій `CRMProductSystems`) цей ADR визначає лише як контракт взаємодії.
