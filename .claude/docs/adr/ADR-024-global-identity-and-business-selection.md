# ADR-024: Глобальна ідентичність (пошта + пароль), членство в кількох бізнесах і вхід без «коду бізнесу»

**Date:** 2026-11-17
**Status:** Accepted 2026-11-17 (human gate пройдено: користувач погодив ADR із рекомендованими відповідями на Q1–Q13; злиття дублікатів на staging — лише після окремого рішення за результатами preflight). Було: Proposed. Потребувало human gate: змінює межу автентифікації (auth/security boundary), вводить першу глобальну (не-tenant) таблицю з ПІІ і містить міграцію даних зі злиттям облікових записів.
**Author:** project-architect
**Deciders:** користувач (human gate, 2026-11-17: ADR і рекомендовані відповіді на Q1–Q13). Продуктові рішення 1–3 (§1.2) ухвалила людина 2026-11-17, вони зафіксовані й тут не переглядаються.
**Уточнює (після `accepted`):** ADR-006 (логін, таблиця `users`), ADR-002 (виняток: глобальна таблиця ідентичностей поза tenant-RLS), ADR-018 (provisioning власника створює ідентичність, новий 409). Старі ADR на місці не редагуються, туди додаються примітки «уточнено ADR-024».
**Errata:** 2026-11-17 (TASK-860, threat-model) — §9 у кінці документа. Текст §1–§8 не редагувався. Де він суперечить errata, діє errata.
**Не змінює:** ADR-023. `user_permissions`, `access_version`, `user_access_audit`, `AccessContextMiddleware` і семантика прав лишаються прив'язаними до членства (рядка `users`). ADR-003 (outbox) теж не змінюється.

---

## 1. Context

### 1.1 Фактичний стан (з коду, 2026-11-17)

- **Логін вимагає slug tenant.** `LoginRequest(Tenant, Email, Password)` (`AuthModels.cs`). `AuthService.LoginAsync` спершу викликає `FindTenantBySlugAsync`: окреме з'єднання, транзакційний `app.tenant_slug`, політика `tenants_login_lookup`. Потім `UseTenant(tenant.Id)` і `FindUserByEmailAsync` під RLS.
- **`users` — рядок на tenant.** У ньому `email`, `password_hash`, `failed_login_count`, `lockout_until`, `last_login_at`, `state` (`active|invited`), `role`, `specialist_id`, `is_active`, `access_version`. Унікальність задає лише `ix_users_tenant_id_email (tenant_id, email)` з міграції `20261007134016`. Одна пошта **вже зараз** може бути в кількох tenant з різними паролями, і ці рядки ніщо не пов'язує.
- **На `users(tenant_id, id)` посилаються 30 композитних FK в 11 міграціях:** `refresh_tokens`, `invites`, `platform_owner_invites`, `user_permissions`, `user_access_audit`, `beauty_appointments.created_by_user_id`, маркетинг, налаштування ШІ, нагадування, собівартість, відсутності, заклади, feedback.
- **Блокування.** Для наявного користувача лічильник лежить у БД (`users.failed_login_count/lockout_until`, 5 спроб, 15 хв). Для невідомого ключа працює `LoginAttemptTracker` у пам'яті з ключем `sha256(slug\nemail)`. Для невідомого tenant/email `RejectUnknown` перевіряє фіктивний хеш, щоб вирівняти час.
- **Токени.** Access JWT (HS256, 15 хв) має claims `sub` = `users.id`, `tenant_id`, `role`, `[specialist_id]` і не містить ПІІ (`JwtTokenIssuer`). Refresh-токен і токен запрошення персоналу непрозорі, формату `{tenantId:N}.{random}` (`TokenCodec`). Префікс tenant потрібен, щоб задати RLS до пошуку.
- **ADR-023.** `AccessContextMiddleware` на кожен запит одним запитом під RLS читає роль, активність, `state`, `access_version`, `user_permissions` і перевіряє, що `tenant_id` з JWT = `TenantContext`.
- **Запрошення персоналу.** `POST /api/auth/invites/accept` → `EfAuthStore.AcceptInviteAsync` створює рядок `users` з паролем. Порушення унікальності дає `409 user_exists`.
- **Власник (ADR-018).** Control plane (`ControlPlaneService.CreateAsync` → `EfControlPlaneStore.TryCreateAsync`) створює `users` з роллю `owner` у стані `invited` з `password_hash = NULL`. `OwnerInviteController` (`/api/auth/invite/inspect|accept`) задає пароль через `EfOwnerInviteStore`: пошук у `platform_owner_invites` без tenant-RLS, далі `set_config('app.tenant_id', …, true)`.
- **Скидання пароля не існує**: немає ендпоінта, таблиці чи листа.
- **Обмеження реалізації:** `EfAuthStore.UseTenant` забороняє міняти tenant після першого звернення до БД у scope запиту.
- **Frontend.** `login-form.tsx` має обов'язкове поле «Код бізнесу» (`tenant`). BFF `app/api/session/login/route.ts` повертає 422 без `tenant`. Refresh-токен зберігається в httpOnly cookie, access-токен лише в пам'яті.
- **Mobile (Expo, ADR-021): входу персоналу немає.** Це клієнтський застосунок запису без акаунтів. Усі `tenantSlug` у `mobile/` належать публічному booking API і цим ADR не зачіпаються.
- **Гранти.** `deploy/staging/db/grants.sql` (і `setup-db.ps1`) дає `beautycrm_app` `SELECT/INSERT/UPDATE/DELETE ON ALL TABLES`, тому нова таблиця автоматично стає доступною застосунку.

### 1.2 Рішення людини (2026-11-17, остаточні)

1. «Код бізнесу» прибирається з форми входу.
2. Одна людина (одна пошта) може бути членом кількох бізнесів. Після входу: якщо бізнесів кілька, показується вибір, якщо один, вхід одразу.
3. Запрошення персоналу або реєстрація власника з поштою, яка вже зайнята в іншому бізнесі, **відхиляється з повідомленням**. Наявний акаунт до бізнесу не приєднується.

### 1.3 Наслідок рішень 2 і 3

Рішення 3 не дає створити нове членство для пошти, що вже існує. Тому в V1 кілька членств виникають **лише з наявних даних** (злиття дублікатів пошти між tenant, §2.10). Вибір бізнесу (рішення 2) працює для цих злитих акаунтів і закладає модель для майбутнього явного приєднання. Як людина отримуватиме друге членство надалі, вирішує людина (Q1).

---

## 2. Decision

### 2.1 Модель даних: ідентичність ≠ членство

**Ідентичність** (нова глобальна таблиця `identities`, без `tenant_id`) — це людина: пошта й облікові дані.
**Членство** — це наявний рядок `users`, роль людини в одному tenant. Таблиця **не переіменовується** і `users.id` не змінюється, тож усі 30 FK, RLS і код ADR-023 лишаються без змін.

```
identities (глобальна; ENABLE + FORCE RLS; для beautycrm_app/worker — жодних прав і жодних політик)
  id                  uuid PK DEFAULT gen_random_uuid()
  email               varchar(320) NOT NULL       -- нормалізована: lower(trim()); CHECK (email = lower(email))
  password_hash       varchar(255) NULL
  state               varchar(16)  NOT NULL       -- 'pending' (власник ще не задав пароль) | 'active'
  failed_login_count  integer NOT NULL DEFAULT 0
  lockout_until       timestamptz NULL
  last_login_at       timestamptz NULL
  password_changed_at timestamptz NULL
  created_at, updated_at timestamptz NOT NULL DEFAULT now()
  UNIQUE (email)                                   -- ux_identities_email
  CHECK ((state = 'active') = (password_hash IS NOT NULL))

users (+ колонка)
  identity_id uuid NOT NULL REFERENCES identities(id) ON DELETE RESTRICT
  UNIQUE (tenant_id, identity_id)                  -- одне членство на людину в бізнесі
  INDEX (identity_id)
```

Що де лежить:

| Дані | Де | Примітка |
|---|---|---|
| пошта (джерело істини), пароль, блокування, останній вхід загалом | `identities` | |
| роль, `specialist_id`, `is_active`, `state` членства, `access_version`, `full_name`, `last_login_at` у цьому бізнесі | `users` | без змін; `full_name` лишається per tenant (у різних бізнесах можуть записати по-різному) |
| права | `user_permissions` (per `users.id`) | ADR-023 без змін |
| `users.email` | `users` | **денормалізована копія**: її читають воркер, список команди й зіставлення запрошень. Незмінна після створення. Тригер `SECURITY DEFINER` (власник `beautycrm_identity`) на `INSERT/UPDATE OF email, identity_id` перевіряє `users.email = identities.email`. Зміна пошти поза обсягом (майбутня задача змінюватиме обидві) |
| `users.password_hash`, `failed_login_count`, `lockout_until` | `users` | **застарілі**. Фаза 2 читає лише `identities`. `password_hash` пишеться дублюванням (dual-write) до фази 3, щоб можна було відкотити образ (§2.10) |

Інваріанти:
- **Активне членство можливе лише в активної ідентичності.** Членство власника в стані `invited` має ідентичність `pending`.
- **Вимкнення членства в бізнесі A (`is_active = false`) не впливає на бізнес B.** Пошта лишається зайнятою (Q11).
- Пошта нормалізується як зараз (`Trim().ToLowerInvariant()`), без Unicode-нормалізації й без правил Gmail щодо крапок.

### 2.2 Зв'язок з ADR-023 і аудитом

- JWT `sub` = `users.id` (членство), як і зараз. `AccessContextMiddleware`, `IAccessContext`, `RequirePermission`, `X-Access-Version` **не змінюються**. Права, `access_version` і scope належать членству. Перемикання бізнесу дає інший `sub`, а отже інші права.
- Вхід і перемикання `access_version` не збільшують.
- `user_access_audit` (per tenant) отримує ті самі записи, що й зараз: `invite_accepted` при прийнятті запрошення. Нових дій не додається.
- Події рівня ідентичності не належать жодному tenant. Вони пишуться в наявний append-only `platform_audit_log` з `tenant_id = NULL`. CHECK розширюється діями `identity.password_reset_completed` і `identity.locked`, `actor_type` додається значенням `identity`. У `details` лише `identity_id`, без пошти. Успішні й невдалі входи в журнал не пишуться (сьогодні теж ні), для них лише метрики й логи без ПІІ.

### 2.3 Пошук пошти до встановлення tenant, не порушуючи RLS

Механізм той самий, що вже прийнятий у ADR-022 (`beautycrm_catalog`) і ADR-023 (`beautycrm_audit_purge`):

1. **NOLOGIN-роль `beautycrm_identity`** (`NOSUPERUSER NOBYPASSRLS NOINHERIT`). Мігратор стає її членом `WITH INHERIT FALSE, SET TRUE`. Міграція створює роль або падає з інструкцією для ops, як у `auth_user_permissions`.
2. **`identities`, `auth_login_tickets`, `auth_password_resets`:** `ENABLE + FORCE RLS`. Політики лише `TO beautycrm_identity`. Для `beautycrm_app` і ролі воркера політик **немає** (deny-all), а `grants.sql` і `setup-db.ps1` після масового `GRANT ON ALL TABLES` робить `REVOKE ALL` на ці три таблиці. Захист двошаровий: права й RLS.
3. **Доступ лише через функції `SECURITY DEFINER`.** Власник функцій `beautycrm_identity`, `SET search_path = pg_catalog, pg_temp`, імена схем у тілі повні, вихід фіксований і мінімальний, `REVOKE ALL FROM PUBLIC`, `GRANT EXECUTE` лише `beautycrm_app`. Функцію `auth_identity_purge_expired` отримує лише воркер.

| Функція | Вхід → вихід | Використання |
|---|---|---|
| `auth_identity_find(email)` | → `identity_id, password_hash, state, failed_login_count, lockout_until` (0/1 рядок) | крок 1 логіну |
| `auth_identity_login_failed(identity, max, lock_until)` / `auth_identity_login_succeeded(identity, now)` / `auth_identity_reset_lockout(identity)` | → void | лічильник блокування (атомарно, як `RegisterFailedLoginAsync`) |
| `auth_identity_memberships(identity)` | → `tenant_id, user_id, tenant_name, tenant_slug, role` лише для `users.is_active AND users.state='active'` і `tenants.status='active'` | крок 1, `GET /auth/memberships`, перемикання |
| `auth_identity_is_active(identity)` | → bool | refresh |
| `auth_identity_email_in_use(email)` | → bool | створення запрошення, provisioning власника (§2.7) |
| `auth_identity_create(email, password_hash)` / `auth_identity_create_pending(email)` | → `identity_id`; `unique_violation` → застосунок віддає `email_in_use` | прийняття запрошення / provisioning власника (у транзакції створення членства) |
| `auth_identity_activate_pending(identity, password_hash)` | → bool (лише якщо `pending` і рівно одне членство) | прийняття запрошення власника |
| `auth_login_ticket_issue(identity, hash, expires)` / `auth_login_ticket_consume(hash, tenant_id, now)` | → void / `user_id` або NULL (атомарно: `consumed_at`, повторна перевірка членства) | вибір бізнесу |
| `auth_password_reset_issue(email, hash, expires)` / `auth_password_reset_consume(hash, new_hash, now)` | → bool / `identity_id` або NULL | скидання пароля (§2.8) |
| `auth_identity_purge_expired(now)` | → bigint | воркер: прострочені квитки й токени скидання |

Перехресно-tenant політики **лише `TO beautycrm_identity`** і лише потрібні колонки (колонкові GRANT):
- `users` — SELECT (`id, tenant_id, identity_id, role, is_active, state`); UPDATE (`password_hash`) лише до фази 3 (dual-write при скиданні);
- `tenants` — SELECT (`id, name, slug, status`);
- `refresh_tokens` — SELECT (`tenant_id, user_id, revoked_at`), UPDATE (`revoked_at`) (відкликання всіх сесій при скиданні).

FK `users.identity_id → identities` працює попри deny-all, бо RI-перевірки PostgreSQL обходять RLS. Застосунок при вставці членства знає лише `identity_id`, який повернула функція.

**Межа довіри.** RLS у цьому проєкті (ADR-002) захищає від помилок у запитах, а не від скомпрометованого застосунку: застосунок і так сам виставляє `app.tenant_id`. Функції тому не перевіряють, чи автентифікувався викликач. Вони обмежують **поверхню**: жодна функція не повертає список пошт, не приймає шаблонів (LIKE) і не віддає хеші інакше, ніж за точною поштою.

**Реалізація.** Новий `IIdentityStore` (Application) і `EfIdentityStore` (Infrastructure) працюють **на окремому короткому `NpgsqlConnection`**, як `FindTenantBySlugAsync`, і викликають лише функції. Причина — інваріант `UseTenant`: tenant-контекст DbContext запиту не можна торкатися до вибору членства. Транзакційні операції всередині tenant-транзакції (`auth_identity_create` при прийнятті запрошення, `activate_pending`) викликаються на з'єднанні DbContext у тій самій транзакції.

### 2.4 Токени і перемикання бізнесу

- **Access JWT лишається прив'язаним до tenant.** Claims ті самі: `sub` (членство), `tenant_id`, `role`, `[specialist_id]`. Ідентичність у токен **не** додається, бо сервер виводить її з `users.identity_id` під RLS поточного tenant. Окремого токена ідентичності без tenant немає.
- **Refresh-токен:** формат `{tenantId}.{random}` і ротація без змін. Сесії, видані до міграції, переживають деплой. Refresh додатково викликає `auth_identity_is_active` (раз на ≤ 15 хв на сесію).
- **Перемикання:** `POST /api/auth/switch` `{ tenantId }` (JWT, політика `Staff`, `[AllowInactiveSpecialist]`). Сервер бере `identity_id` поточного членства, перевіряє цільове членство через `auth_identity_memberships` і видає **нову пару токенів** для цільового tenant. Refresh-токен поточної сесії відкликається (одна активна бізнес-сесія на браузер). BFF замінює cookie.
  - Видача в чужий tenant іде в **новому DI-scope** (`IServiceScopeFactory`) з `UseTenant(target)`, бо поточний scope уже зафіксований на tenant A.
  - Членства немає, воно вимкнене або tenant призупинено → один і той самий `404 membership_not_found`.
- `GET /api/auth/memberships` (JWT, `no-store`) повертає `[{ tenantId, name, role, current }]` для перемикача в шапці.

### 2.5 Логін у два кроки

```
POST /api/auth/login { email, password, tenant? }   (tenant — deprecated, §2.9)
  ├─ 401 invalid_credentials | 423 account_locked | 429 rate_limited   — однакові для «немає пошти» і «невірний пароль»
  ├─ 0 активних членств      → 401 invalid_credentials (як сьогодні для вимкненого користувача)
  ├─ 1 членство              → 200 TokenResponse (як сьогодні)
  └─ ≥2 членства             → 200 { selectionRequired: true, loginTicket, ticketExpiresInSeconds: 180,
                                     businesses: [{ tenantId, name, role }] }   (сортування за name)
POST /api/auth/login/select { loginTicket, tenantId }
  ├─ 200 TokenResponse
  └─ 401 login_ticket_invalid   — квиток невідомий/прострочений/використаний АБО tenant не з його списку АБО членство зникло
```

- **Квиток входу.** 32 випадкові байти, у БД лише `sha256` (`auth_login_tickets`: `token_hash`, `identity_id`, `expires_at`, `consumed_at`). TTL 3 хв, одноразовий, нова видача для тієї ж ідентичності гасить попередній. Квиток не автентифікує ні для чого, крім `select`.
- **BFF не віддає квиток у JS.** `/api/session/login` кладе його в httpOnly `SameSite=Strict` cookie (`Path=/api/session/login`, `Max-Age=180`), у браузер іде лише список бізнесів. `/api/session/login/select` читає cookie й видаляє її. Модель «секрет не доступний JS» та сама, що для refresh.
- **Порядок дій на сервері:** нормалізувати пошту → ліміти (§2.6) → `auth_identity_find` → невідома або `pending` ідентичність: `RejectUnknown` (фіктивний хеш, трекер) → блокування → `Verify` → `login_failed` / `login_succeeded` → лише **після** успішної перевірки `auth_identity_memberships`. Список членств не впливає на час відповіді до перевірки пароля.
- Після вибору (або при одному членстві) `UseTenant(membership.TenantId)`: `users.last_login_at`, refresh-токен і JWT, як у нинішньому `IssueAsync`.

### 2.6 Енумерація і перебір

| Поверхня | Захист |
|---|---|
| `login` | Однакові статус і тіло для «немає пошти», «невірний пароль», «немає членств». Фіктивний хеш вирівнює час. Блокування **на ідентичність** (`identities`, 5 спроб / 15 хв) діє на всі бізнеси людини (Q10). Для невідомих пошт `LoginAttemptTracker` бере ключ `sha256(email)` без slug і дає той самий `423`. Новий `LoginEmailThrottle` у пам'яті (як `OwnerInviteThrottle`): за замовчуванням 20 спроб / 15 хв на `sha256(email)` незалежно від існування, перевищення дає `429` для всіх. IP-ліміт `auth` (10/хв) лишається |
| `login/select` | IP-ліміт `auth`. Квиток прив'язаний до ідентичності, одна відповідь `401 login_ticket_invalid` на всі причини |
| `password/forgot` | Завжди `202` з однаковим тілом. Ліміти: IP 5 / 15 хв, `sha256(email)` 3 / год. Однакова робота з БД незалежно від існування пошти. Відправка асинхронна, через outbox |
| запрошення (§2.7) | Відмова `email_in_use` **за задумом розкриває** запрошувачу, що пошта зареєстрована на платформі (наслідок рішення 3). Пом'якшення: лише для автентифікованих із `team.manage`, ліміт на tenant (30 перевірок / год), текст не називає бізнес, лічильник у логах без пошти |
| control plane | Довірений викликач (HMAC / ключ платформи). `owner_email_in_use` без пом'якшень |

Окремий ризик: без slug для вгадування достатньо пошти, тож поверхня credential stuffing ширшає. Пом'якшення: ліміти й блокування з таблиці, алерт за часткою `invalid_credentials` (devops), CAPTCHA за рішенням людини (Q9).

### 2.7 Запрошення й власник за рішенням 3

- **Створення запрошення персоналу** (`POST /api/invites`): якщо `auth_identity_email_in_use(email)` повертає true, відповідь `409 email_in_use` з текстом «Ця пошта вже використовується в Beauty CRM. Вкажіть іншу пошту». Наявне `user_exists` для пошти, яка вже є членом **цього** tenant, лишається.
- **Прийняття запрошення персоналу** (`POST /api/auth/invites/accept`): в одній транзакції спершу `auth_identity_create(email, hash)` (гонка з іншим бізнесом закінчується `unique_violation` → `409 email_in_use`, запрошення **не** гаситься), потім вставка `users` з `identity_id` і (dual-write) `password_hash`, далі решта як зараз (`PlanAccept`, `user_permissions`, `invite_accepted`).
- **Provisioning власника** (control plane `POST /api/control/v1/tenants` і застарілий `POST /api/platform/tenants`): якщо пошта зайнята, `409 owner_email_in_use`, tenant не створюється. Інакше в тій самій транзакції `auth_identity_create_pending(email)`, потім `users(owner, invited, identity_id)`. Replay за `externalRef` працює без змін. Це **зміна контракту §27 для ShelfGuard** (Q8).
- **Прийняття запрошення власника** (`/api/auth/invite/accept`): `auth_identity_activate_pending` у транзакції погашення. Якщо ідентичність уже активна (можливо лише після злиття, §2.10), відповідь `410 invite_invalid`, як для будь-якого непридатного токена.
- Наявний акаунт **ніколи** не приєднується автоматично (рішення 3).

### 2.8 Скидання пароля (глобально, на ідентичність)

- `POST /api/auth/password/forgot { email }` → `202` завжди. Для активної ідентичності створюється токен: 32 байти, у `auth_password_resets` лише `sha256`, TTL 30 хв, одноразовий, нова видача гасить попередні.
- `POST /api/auth/password/reset { token, password }` → `204` | `410 reset_invalid` | `422 weak_password`. `auth_password_reset_consume` в одній транзакції: новий `identities.password_hash`, `password_changed_at`, зняття блокування, **відкликання refresh-токенів усіх членств**, dual-write `users.password_hash` усіх членств (до фази 3), запис `identity.password_reset_completed` у `platform_audit_log`.
- Уже видані access-токени живуть до 15 хв (та сама семантика, що й при logout). Миттєве відкликання через перевірку ідентичності в middleware на кожен запит відхилено, бо це другий запит на кожен API-виклик (§4). Security-рев'ю може переглянути це рішення.
- **Доставка листа.** Через outbox (ADR-003) і наявний email-відправник воркера (SMTP/mock з env). Сирий токен є лише в payload outbox і **затирається після відправки**. Лист без назви бізнесу. Канал, відправник і текст визначає людина (Q7). Поки рішення немає, ця частина не блокує фази 1–2.
- Зміна пароля з профілю (`POST /api/auth/password/change` зі старим паролем) — опційна задача того ж контракту.

### 2.9 Сумісність зі старим контрактом

- `LoginRequest.Tenant` стає необов'язковим (`string?`, `[StringLength(64)]`) і **deprecated**. Якщо поле передано, сервер після перевірки пароля обирає членство з `tenant_slug = tenant` і **завжди** повертає `TokenResponse` (стара форма). Членства в цьому tenant немає → `401 invalid_credentials`, без збільшення лічильника, бо пароль правильний. Відповідь несе заголовки `Deprecation: true` і `Sunset: <дата>` (Q12), у логах лічильник використань.
- Хто це використовує: frontend до оновлення (кешовані збірки й BFF) і сторонні скрипти. **Мобільний застосунок входу персоналу не має**, його змінювати не треба. Майбутній мобільний вхід персоналу будується одразу на §31.
- Refresh, logout, `me`, формат токенів, `TokenResponse` не змінюються. Нові коди: `email_in_use`, `owner_email_in_use`, `login_ticket_invalid`, `membership_not_found`, `reset_invalid`.
- `tenants_login_lookup`, `FindTenantBySlugAsync` і поле `tenant` видаляються у фазі 3.

### 2.10 Міграція даних, фази й відкат

**Фаза 0. Preflight (лише читання, до міграції).** Ops виконує на staging від `postgres`:
```sql
SELECT lower(trim(email)) AS email_norm, count(*) AS memberships,
       array_agg(tenant_id ORDER BY last_login_at DESC NULLS LAST) AS tenants,
       array_agg(role ORDER BY last_login_at DESC NULLS LAST)      AS roles,
       array_agg(state || '/' || is_active::text ORDER BY last_login_at DESC NULLS LAST) AS states,
       count(DISTINCT email) AS case_variants
FROM users GROUP BY 1 HAVING count(*) > 1 OR count(DISTINCT email) > 1;
```
Звіт (без пошт у git/логах) іде людині. Зі слів людини, на staging таких користувачів **2**. Я цього не перевіряв, доступу до БД staging немає. Як їх розв'язати, вирішує людина (Q4).

**Фаза 1. Expand-міграція `auth_global_identities` (database-engineer).**
1. Роль `beautycrm_identity`, таблиці `identities`, `auth_login_tickets`, `auth_password_resets`, функції, політики, тригер узгодженості пошти, розширення CHECK `platform_audit_log`.
2. `users.identity_id` NULL → backfill → `NOT NULL`, `UNIQUE (tenant_id, identity_id)`, FK, індекс. FORCE RLS на `users` на час backfill знімається, як у `20261115120000`.
3. **Backfill:** одна ідентичність на `lower(trim(email))`.
   - Одне членство → ідентичність отримує його `password_hash`. `state = active`, якщо хеш є, інакше `pending` (запрошений власник).
   - **Кілька членств (злиття).** Пароль береться з активного членства з паролем, у якого найбільший `last_login_at` (NULLS LAST), далі найбільший `updated_at`. Лічильники обнуляються. Злиття відбувається **лише** якщо сесійний параметр `beautycrm.identity_merge = 'latest_login'` (ops задає через `PGOPTIONS`/`Options` рядка підключення). Без нього міграція падає з `RAISE EXCEPTION`, якщо дублікати є. Це human gate, закодований у міграції.
   - **Завжди відмова:** серед злитих членств є власник у стані `invited`, а ідентичність стає активною (така людина ніколи не прийме запрошення власника). Розв'язується вручну до міграції.
   - **`users.password_hash` міграція НЕ змінює.** Старий образ API на тих самих даних поводиться точно як раніше.
4. Ідемпотентність: повторний запуск backfill нічого не змінює. Звіт через `RAISE NOTICE` лише з кількостями.
5. **`Down`:** видаляє колонку, таблиці, функції, політики й гранти ролі (саму роль не видаляє). Відмовляє (`RAISE`), якщо існує активне членство з `users.password_hash IS NULL` (тобто dual-write порушено). Втрачаються лише блокування, квитки й токени скидання. **Human gate** на staging.

**Фаза 2. Код (backend, frontend).** Читання лише з `identities`, dual-write `users.password_hash` (прийняття запрошення: той самий рядок; скидання: усі членства через функцію). Відкат образу API на попередній **працює**: старий код читає `users.password_hash`, і він актуальний.

**Фаза 3. Contract (окрема міграція, не раніше ніж після стабілізації й `Sunset`; human gate).** Видалити `users.password_hash`, `failed_login_count`, `lockout_until`, `ck_users_password_or_invited` (замінюється інваріантом через `identities.state`), політику `tenants_login_lookup`, UPDATE-політику `users` для `beautycrm_identity`, поле `tenant` у `LoginRequest`. Після фази 3 відкат лише з бекапу.

---

## 3. Consequences

Позитивні:
- Вхід без коду бізнесу. Одна людина має один пароль на всі свої бізнеси, а скидання й блокування працюють для неї одним механізмом.
- `users.id`, 30 FK, RLS-таблиці, ADR-023 (права, `access_version`, middleware, аудит доступу) не змінюються, тож ризик регресу авторизації мінімальний.
- Токени лишаються tenant-bound: `TenantMiddleware` і перевірка `tenant_id == TenantContext` діють як раніше. Сесії переживають деплой.
- Глобальні ПІІ закриті двома шарами (гранти + FORCE RLS deny-all), доступ лише через вузькі функції за перевіреним патерном (ADR-022/023).
- Expand/contract з dual-write дає відкат образу без відкату БД.

Негативні / прийняті компроміси:
- **Перша глобальна таблиця з ПІІ й хешами паролів поза tenant-RLS.** Це виняток з ADR-002, компенсований функціями.
- Рішення 3 **за задумом** розкриває запрошувачу, що пошта зареєстрована на платформі (§2.6).
- Без slug поверхня перебору ширша. Блокування ідентичності діє на всі її бізнеси, тож атакувальник, який знає пошту, може тимчасово заблокувати людину (DoS). Це вже можливо зараз, але в межах одного бізнесу.
- Злиття дублікатів незворотно обирає один пароль. Інші паролі тієї ж пошти перестають працювати в новому коді.
- Тимчасовий борг: dual-write і застарілі колонки до фази 3, legacy-гілка `tenant`, in-memory троттли (у multi-instance кожен інстанс рахує окремо, як і зараз).
- Access-токени після скидання пароля живуть до 15 хв.
- Нова зміна контракту control plane для ShelfGuard (`owner_email_in_use`).
- `users.email` дублює `identities.email`. Узгодженість тримає тригер, а зміна пошти в майбутньому мусить міняти обидві колонки.

---

## 4. Alternatives considered

- **Лишити `users` per tenant і шукати пошту в усіх tenant через функцію, перевіряючи пароль для кожного кандидата.** Відхилено: в однієї пошти N паролів, що суперечить «одна людина = один акаунт». N перевірок bcrypt дають витік через час і DoS. Скидання й блокування лишаються неоднозначними.
- **Перенести `users` у глобальну таблицю й додати `memberships`.** Відхилено: 30 композитних FK на `users(tenant_id, id)`, `user_permissions`, `user_access_audit`, `created_by_user_id` довелося б переводити на новий ключ. Це масштабна міграція з ризиком для RLS і ADR-023 без виграшу для бізнесу.
- **Глобальний індекс «пошта → список tenant» без пароля, пароль per tenant.** Відхилено з тих самих причин, що й перша альтернатива.
- **JWT без tenant (токен ідентичності) і tenant у заголовку кожного запиту.** Відхилено: кожен запит потребує перевірки членства. Ламаються інваріант `tenant_id == TenantContext`, tenant-bound refresh і middleware ADR-023. З'являється клас помилок «запит не в той tenant».
- **Вхід через піддомен бізнесу (`slug.beautycrm…`).** Відхилено: суперечить рішенню 1, потребує wildcard DNS і сертифікатів.
- **Stateless підписаний квиток вибору (JWT, 3 хв).** Відхилено: немає одноразовості й відкликання. Повтор у межах TTL на кількох інстансах неконтрольований. Таблиця коштує мало.
- **Глобальна таблиця без RLS з прямим SELECT для `beautycrm_app`.** Відхилено: порушує fail-closed. Будь-яка помилка в запиті або ін'єкція віддасть усі пошти й хеші.
- **Автоматичне приєднання наявного акаунта при прийнятті запрошення.** Відхилено рішенням людини 3. Явне приєднання з підтвердженням паролем винесено в Q1.
- **Квиток вибору в JSON для JS.** Відхилено: BFF тримає секрети сесії поза JS (як refresh), httpOnly cookie нічого не коштує.
- **Перевірка ідентичності в middleware на кожен запит (миттєве відкликання після скидання).** Відхилено для V1: другий запит на кожен API-виклик заради вікна ≤ 15 хв, яке вже прийняте для logout.

---

## 5. Scope of truth

Після `accepted` цей ADR авторитетний щодо:
- розділення «ідентичність (глобальна) ↔ членство (`users`)», місця зберігання облікових даних і блокування;
- механізму доступу до глобальних auth-даних (роль `beautycrm_identity`, deny-all RLS, перелік і контракти `SECURITY DEFINER`-функцій);
- форми логіну (2 кроки, квиток, перемикання, tenant-bound токени), правил енумерації й перебору;
- правила «зайнята пошта → відмова» для запрошень і provisioning власника;
- глобального скидання пароля;
- плану міграції (preflight, правило злиття, фази expand/contract, dual-write, відкат) і застарівання поля `tenant`.

Деталі API (DTO, коди, ліміти) — у контракті `beauty-contracts.md` §31 (TASK-859). Зміна будь-якого пункту вище вимагає нового ADR.

---

## 6. Розбиття на задачі (DAG; ID запропоновані, остаточні призначає `project-manager`)

Порядок: gate ADR → контракт → (threat-model ∥ preflight) → БД → (backend логін ∥ backend запрошення) → скидання → frontend → QA → security review → docs → gate мерджу → (пізніше) фаза 3.

| Задача | Агент | Обсяг | Залежить / артефакти |
|---|---|---|---|
| TASK-859 | project-architect | Контракт **§31 «Глобальна ідентичність і вибір бізнесу»**: `login` (обидві форми відповіді, `tenant` deprecated + заголовки), `login/select`, `memberships`, `switch`, `password/forgot|reset`, коди помилок, ліміти, BFF-cookie квитка; дельта §27 (`owner_email_in_use`); дельта запрошень персоналу (`email_in_use`). Надає `API-CONTRACT-IDENTITY-V1` | ADR-024 `accepted` (human gate) |
| TASK-860 | security-reviewer (skills `beauty-security`, `admin-auth-review`) | Threat-model контракту й §2.3: функції (поверхня, `search_path`, гранти), енумерація, квиток, перемикання (IDOR між tenant), скидання, злиття. Висновки йдуть правкою §31 до TASK-862 | `API-CONTRACT-IDENTITY-V1` |
| TASK-861 | database-engineer | **Preflight** (§2.10, фаза 0): скрипт лише для читання + інструкція для ops (`scripts/dev/` і runbook), прогін на локальній копії. Звіт про дублікати на staging для людини. Надає `ARTIFACT-IDENTITY-PREFLIGHT-V1` | немає (стартує одразу); відповідь на Q4 потрібна до застосування TASK-862 на staging |
| TASK-862 | database-engineer (skills `create-migration`, `beauty-schema`) | Міграція `auth_global_identities` (фаза 1): роль, 3 таблиці, функції, політики, колонкові гранти, тригер узгодженості пошти, `users.identity_id` + backfill з GUC-gate злиття й відмовою для invited-власника, CHECK `platform_audit_log`, `Down` з відмовою. `grants.sql`, `setup-db.ps1`, `init-*-role.sql` (REVOKE на нові таблиці). EF-моделі, тестова схема воркера за конвенцією. Тести БД: deny-all для app і worker, вихід функцій, ізоляція tenant у `memberships`, злиття, ідемпотентність, `Down`. Надає `ARTIFACT-IDENTITY-SCHEMA-V1` | `API-CONTRACT-IDENTITY-V1`, висновки TASK-860, `ARTIFACT-IDENTITY-PREFLIGHT-V1` |
| TASK-863 | backend-developer | **Логін і сесії**: `IIdentityStore`/`EfIdentityStore` (окреме з'єднання), `AuthService.LoginAsync` (2 кроки, legacy `tenant`), `login/select`, блокування на ідентичність, трекер за `sha256(email)`, `LoginEmailThrottle`, refresh + `auth_identity_is_active`, `GET memberships`, `POST switch` (новий DI-scope, відкликання старого refresh). Тести: однаковість відповідей, блокування, одноразовість квитка, ізоляція перемикання, legacy-шлях | `ARTIFACT-IDENTITY-SCHEMA-V1` |
| TASK-864 | backend-developer | **Запрошення й власник**: `email_in_use` при створенні/прийнятті запрошення (ліміт на tenant), `auth_identity_create` у транзакції прийняття + dual-write; control plane і `/api/platform/tenants`: `owner_email_in_use`, `create_pending`; прийняття запрошення власника → `activate_pending`. Файли: `UserAdminService`, частина запрошень `EfAuthStore`, `ControlPlaneService`/`EfControlPlaneStore`, `EfOwnerInviteStore`. Конфлікт з 863 в `EfAuthStore` → 863 бере логін/refresh, 864 запрошення/tenant | `ARTIFACT-IDENTITY-SCHEMA-V1` (∥ TASK-863) |
| TASK-865 | backend-developer + integrations-developer (воркер, email) | **Скидання пароля**: `forgot`/`reset`, ліміти, outbox-подія з затиранням payload після відправки, шаблон листа, `purge_expired` у воркері | TASK-863; відповідь на Q7 |
| TASK-866 | frontend-developer (skill `beauty-admin-ui`) | Форма входу без «Коду бізнесу»; екран вибору бізнесу (список, клавіатура, фокус, остання обрана позиція за Q5); перемикач у шапці (`memberships`/`switch`); BFF `/api/session/login` (дві форми, cookie квитка), `/login/select`, `/switch`, `/password/*`; сторінки «Забули пароль» і «Новий пароль»; тексти помилок (`email_in_use` на сторінках запрошень). Mock-режим за контрактом; a11y-рев'ю | `API-CONTRACT-IDENTITY-V1` (mock) → інтеграція після TASK-863/864 (скидання після TASK-865) |
| — | mobile-developer | **Змін немає**: у мобільному застосунку входу персоналу немає (ADR-021). Будь-який майбутній вхід персоналу будується на §31 | — |
| TASK-867 | qa-engineer (skill `beauty-qa`) | Матриця: 0/1/N членств; legacy `tenant`; енумерація (статус, тіло, межі часу); блокування на ідентичність; квиток (TTL, повтор, чужий tenant); перемикання (права нового членства, ADR-023 не зламано, старий refresh відкликано); запрошення й provisioning з зайнятою поштою, гонки; скидання (усі сесії відкликано); міграція на фікстурі з дублікатами; e2e вхід → вибір → перемикання. Регресія staging smoke | TASK-863…866 |
| TASK-868 | security-reviewer | Рев'ю реалізації за §2.3–2.8 (функції, гранти, RLS, ліміти, BFF-cookie, скидання). Звіт у `docs/reviews/` | TASK-867 |
| TASK-869 | documentation-writer (skill `beauty-docs`) | `api.md`, `database.md`, `domain-model.md` (ідентичність/членство), runbook (preflight, GUC злиття, застосування, відкат, фаза 3), примітки «уточнено ADR-024» в ADR-002/006/018, рядок архітектури в CLAUDE.md | TASK-868 |
| TASK-870 | database-engineer + backend-developer | **Фаза 3 (contract)**: видалення застарілих колонок, `tenants_login_lookup`, legacy `tenant`, UPDATE-політики dual-write | після `Sunset` (Q12) і стабілізації; human gate |

**Паралельність:** TASK-860 ∥ TASK-861 одразу після контракту (861 може стартувати й раніше). TASK-863 ∥ TASK-864. TASK-866 стартує на mock одразу після TASK-859. Гілки зводить `integration-steward` в `integrate/*`.

**Human gates:** (1) прийняття ADR-024; (2) розв'язання дублікатів і застосування TASK-862 на staging з `beautycrm.identity_merge` (так само для будь-якого `Down`); (3) зміна контракту control plane для ShelfGuard; (4) мердж `integrate/*` після TASK-868; (5) фаза 3 (TASK-870).

---

## 7. Питання, які може вирішити лише людина

**Усі Q1–Q13 вирішено 2026-11-17: прийнято рекомендовані значення** (деталізація в контракті — `beauty-contracts.md` §31.14). Q4 лишає дію для людини: розвести або злити дублікати за звітом preflight до застосування TASK-862 на staging (human gate).

| # | Питання | Рекомендація (дефолт) |
|---|---|---|
| Q1 | Рішення 3 забороняє нові членства для наявної пошти. Як людина надалі отримуватиме другий бізнес? | **Вирішено: рекомендоване значення.** V1: лише через злиття наявних даних. V2: окремий ADR «прийняти запрошення, увійшовши наявним акаунтом» (явне підтвердження паролем, без автоприєднання). Модель цього ADR підходить без змін схеми |
| Q2 | Чи діє рішення 3 і на запрошення персоналу, і на provisioning власника? Перевіряти вже при **створенні** запрошення (запрошувач одразу бачить відмову) чи лише при прийнятті? | **Вирішено: рекомендоване значення.** Обидва випадки. Перевіряти при створенні й повторно при прийнятті (гонка). Запрошувач і так тримає токен, тож відкладена перевірка витік не зменшує |
| Q3 | Текст відмови: нейтральний чи «зареєстрована в іншому бізнесі»? | **Вирішено: рекомендоване значення.** Нейтральний: «Ця пошта вже використовується в Beauty CRM. Вкажіть іншу пошту». Бізнес не називати |
| Q4 | 2 користувачі з однаковою поштою на staging: це одна людина (зливати) чи тестові акаунти (змінити пошту / видалити один до міграції)? Чий пароль лишається? | **Вирішено: рекомендоване значення** (виконання — за звітом preflight, human gate). Перевірити з власниками акаунтів. Якщо це одна людина, злиття за правилом «найсвіжіший вхід». Якщо тестові, розвести до міграції |
| Q5 | Екран вибору: показувати завжди чи запам'ятовувати останній бізнес і входити в нього автоматично? | **Вирішено: рекомендоване значення.** Показувати завжди, якщо бізнесів ≥ 2 (рішення 2). Останній обраний — першим і підсвіченим (лише `tenantId` у localStorage) |
| Q6 | Чи потрібен перемикач бізнесу в шапці без повторного входу? | **Вирішено: рекомендоване значення.** Так (`switch`) |
| Q7 | Скидання пароля в цьому релізі? Відправник, адреса, текст листа, TTL? | **Вирішено: рекомендоване значення.** Так, як TASK-865 після фаз 1–2. Наявний SMTP воркера, TTL 30 хв, лист без назви бізнесу. Адресу відправника визначає людина |
| Q8 | `409 owner_email_in_use` у control plane змінює контракт для ShelfGuard. Хто погоджує з їхньою командою й коли? | **Вирішено: рекомендоване значення.** Погодити до TASK-864. До того адитивний код помилки не ламає наявні виклики |
| Q9 | CAPTCHA після N невдалих спроб? | **Вирішено: рекомендоване значення.** Не у V1: ліміт на пошту, блокування й алерт. Повернутися за даними моніторингу |
| Q10 | Блокування на ідентичність (5 невдалих спроб блокують вхід у **всі** бізнеси людини на 15 хв). Прийнятно? | **Вирішено: рекомендоване значення.** Так |
| Q11 | Якщо людину вимкнено в усіх бізнесах, чи лишається пошта зайнятою? | **Вирішено: рекомендоване значення.** Так. Звільняє лише оператор платформи (окрема процедура) |
| Q12 | Термін `Sunset` для поля `tenant` у логіні й дата фази 3 | **Вирішено: рекомендоване значення.** 30 днів після релізу фази 2 (відомих зовнішніх клієнтів немає, мобільний застосунок не зачіпається) |
| Q13 | Правильний пароль, але активних членств немає: `401 invalid_credentials` (як зараз) чи пояснення «Доступ до бізнесу вимкнено»? | **Вирішено: рекомендоване значення.** `401 invalid_credentials` (не підтверджувати чинність пароля) |

---

## 8. References

- `backend/BeautyCrm.Api/Controllers/AuthController.cs`, `OwnerInviteController.cs`; `Api/Auth/JwtTokenIssuer.cs`, `AccessContextMiddleware.cs`, `AuthSetup.cs` (ліміти)
- `backend/BeautyCrm.Application/Features/BeautyAuth/AuthService.cs`, `AuthModels.cs`, `TokenCodec.cs`, `LoginAttemptTracker.cs`; `BeautyControlPlane/OwnerInviteService.cs`, `ControlPlaneService.cs`
- `backend/BeautyCrm.Infrastructure/Data/Auth/EfAuthStore.cs`, `EfAccessStore.cs`, `EfOwnerInviteStore.cs`, `EfControlPlaneStore.cs`
- Міграції: `20261007134016_auth_tenants_users_invites` (`ix_users_tenant_id_email`, `tenants_login_lookup`), `20261104182836_platform_control_plane` (`platform_audit_log`, `ck_users_password_or_invited`), `20261109075625_beauty_public_location_catalog` (патерн `beautycrm_catalog`), `20261115120000_auth_user_permissions` (патерн `beautycrm_audit_purge`, зняття FORCE на час backfill)
- `deploy/staging/db/grants.sql`, `scripts/dev/setup-db.ps1`
- `frontend/features/beauty-auth/components/login-form.tsx`, `session.ts`, `frontend/app/api/session/login/route.ts`
- ADR-002, ADR-003, ADR-006, ADR-018, ADR-021, ADR-022, ADR-023; `beauty-contracts.md` §27, §30

---

## 9. Errata 2026-11-17 (TASK-860)

**Підстава:** threat-model `docs/reviews/identity-threat-model.md` (security-reviewer, verdict approved-with-notes, critical немає).
**Рішення людини (2026-11-17):** Q-C1 — так (errata до §2.9); F-2 — варіант A; F-4 — варіант 1.
**Прийнято за рекомендацією рев'ю:** Q-C2, Q-C3, Q-C5, F-3, F-5, F-6, F-8, F-9 (Q-C4 відповідно), F-10, F-11, F-13. F-1 — вимога релізу. F-7 — без змін (штучної затримки немає). F-12 — Q10 лишається, додано метрику й алерт.
**Деталі API й схеми:** `beauty-contracts.md` §31 v0.34.1 (§31.2.1, §31.6, §31.7, §31.8.4, §31.9, §31.12, §31.15 п.19–31, §31.16).

Межа автентифікації, модель «ідентичність ≠ членство», deny-all RLS і доступ через `SECURITY DEFINER` **не змінюються**. Errata уточнюють механізми всередині цієї моделі. Тому це errata, а не новий ADR (рішення людини).

| # | Що змінено (розділ ADR) | Було | Стало |
|---|---|---|---|
| E-1 | §2.9, §2.5 (Q-C1) | Legacy `tenant` без членства → `401` «без збільшення лічильника, бо пароль правильний» | Правильний пароль + 0 придатних членств **і** legacy `tenant` без членства — **невдала спроба**: лічильник збільшується, `401 invalid_credentials`. Інакше момент `423` видає правильний пароль. Ціна: legacy-клієнт із не тим slug блокується після 5 спроб |
| E-2 | §2.1, §2.3, §2.6, §1.1 (F-2) | Лічильник наявної ідентичності — `identities.failed_login_count/lockout_until` без згасання; невідома пошта — `LoginAttemptTracker` у пам'яті з `sha256(email)` | **Єдина глобальна таблиця `auth_login_attempts`** з ключем `login_key = HMAC-SHA256(pepper, email_norm)`, deny-all як інші три таблиці. Функції `auth_login_attempt_is_locked`, `auth_login_attempt_failed`, `auth_login_attempt_reset`. Однакове вікно (`Auth:LoginAttemptWindowSeconds`, 900 с), поріг і блокування для наявної, невідомої й `pending` пошти та Q-C1. Перевірка блокування — **до** `auth_identity_find`. `identities.failed_login_count/lockout_until` **прибрано** (не створюються). `auth_identity_find` повертає `identity_id, password_hash, state`. `auth_identity_login_failed` і `auth_identity_reset_lockout` прибрано. `auth_identity_login_succeeded` пише лише `last_login_at`. `LoginAttemptTracker` для логіну видаляється. Pepper (`Auth:LoginAttemptKeyPepper`) обов'язковий поза Development |
| E-3 | §2.6, §2.3 (F-3) | `forgot`: «однакова робота з БД незалежно від існування пошти»; `auth_password_reset_issue` → `bool` | `202` віддається **до** роботи з БД. Пошта йде в обмежену in-process чергу, фоновий обробник викликає `auth_password_reset_issue(email, login_key, expires)` → **`void`**. Переповнення → `202` і метрика |
| E-4 | §2.8 «Доставка листа» (F-9, Q-C4) | Сирий токен у payload outbox, затирається після відправки | **Токен генерує воркер.** У БД лише `sha256`, сирого токена немає ніколи. Черга — рядок `auth_password_resets`. Воркер: `claim_delivery` (`SKIP LOCKED`) → генерує токен → `attach_token` → відправка → `mark_delivery`. Повторна спроба генерує новий токен. У застосунку немає функції, що повертає токен |
| E-5 | §2.8, §3, §4 (F-6) | Access-токени після скидання живуть до 15 хв; миттєве відкликання відхилено як «другий запит на кожен виклик» | **`users.sessions_valid_after`**. `AccessContextMiddleware` уже читає рядок `users` і порівнює `iat` → `401 session_revoked`. Refresh відхиляє токени з `issued_at` < `sessions_valid_after`. Другого запиту немає, тож заперечення з §4 знято. Ставлять колонку скидання пароля, злиття (E-6) і звільнення пошти (E-7). Уточнення до «Не змінює ADR-023»: middleware отримує одну додаткову перевірку колонки. Права, `access_version`, `X-Access-Version` і семантика ADR-023 без змін |
| E-6 | §2.10 Backfill (F-5) | Злиття вмикає глобальний GUC `beautycrm.identity_merge = 'latest_login'` | Злиття вмикає **лише allowlist** `beautycrm.identity_merge_allowlist` (список `email_key` з preflight). Група поза allowlist → `RAISE`, ключ без групи → `RAISE`. Для кожної злитої групи в тій самій транзакції — відкликання refresh-токенів **усіх** злитих членств і `sessions_valid_after = now`. Інакше сесія «програвшого» членства через `switch` входить у бізнес «переможця» без пароля. Правило вибору пароля («найсвіжіший вхід») і human gate без змін |
| E-7 | §2.7, §3 (F-4, варіант 1) | — | Самоприйняте запрошення може зайняти чужу пошту на платформі: **прийнятий ризик V1**. Пом'якшення: аудит `identity.created` (`platform_audit_log`, `tenant_id` бізнесу-джерела, `via`); квота нових ідентичностей через запрошення персоналу на tenant за 24 год (`Auth:IdentityCreate:PerTenantPerDay`, 20, рахує БД) → `429 rate_limited` при прийнятті; алерт devops; процедура оператора `auth_identity_release` (лише ops-роль, human gate, аудит `identity.released`), що реалізує Q11. `auth_identity_create` отримує `origin_tenant_id`, `via`, ліміт. **`email_verified_at` — V2** (окремий ADR разом із листом запрошення на адресу запрошеного) |
| E-8 | §2.3 (F-8) | Загальні правила функцій | Обов'язковий чекліст для TASK-862 (§31.16): `REVOKE` нових глобальних таблиць **після** `GRANT ON ALL TABLES` для app, воркера й `PUBLIC`; явні `REVOKE`/`GRANT EXECUTE` для кожної `auth_*`; стартова перевірка за зразком `DbRoleGuard`; `RETURNS TABLE` з явними колонками; нормалізація пошти всередині функцій; `consume` одним `UPDATE … RETURNING`; без `CREATE` на `public`; жоден DTO чи вхід не містить `identity_id`. Матриця `EXECUTE` у §31.16 замінює стовпчик «використання» таблиці §2.3 |
| E-9 | §2.4 (F-11) | Перевірка цілі через `auth_identity_memberships`, видача в новому scope | Додатково в новому scope під `UseTenant(target)` рядок членства цілі перечитується під RLS **до** відкликання поточного refresh (TOCTOU). Frontend після `switch` очищає кеші даних попереднього tenant |
| E-10 | §2.8 (F-10) | — | Посилання скидання будується **лише з конфігу** (`PublicAppUrl` воркера), не з `Host`. Після успішного скидання — лист «Пароль змінено» (черга в тому самому рядку, окрема задача TASK-872). Тихий ліміт `forgot` на пошту — прийнятий ризик |
| E-11 | §2.5 (F-13) | «Фіктивний хеш вирівнює час» | Фіктивний хеш має той самий bcrypt cost, що й реальні. Генерується на старті тим самим хешером. Preflight показує розподіл cost |
| E-12 | §2.9 (Q-C3) | `Deprecation: true` | `Deprecation: @<unix-секунди>` (RFC 9745) + `Sunset: <HTTP-date>` (RFC 8594). Обидві дати обов'язкові в конфігу поза Development |
| E-13 | §2.4, §2.5 (Q-C2, Q-C5) | `switch { tenantId }`; квиток одноразовий | `switch` вимагає `refreshToken` поточної сесії, BFF бере його лише з cookie `beauty_rt`. Квиток гаситься першою ж спробою `select`, навіть при чужому `tenantId` |
| E-14 | розгортання (F-1, high) | — | **Вимога релізу:** за BFF API має бачити IP клієнта: статична адреса `web` у мережі staging, вона ж у `Beauty__TrustedProxies` (разом зі шлюзом), nginx перезаписує XFF, порт `web` лише на loopback, smoke двох клієнтських IP. Без цього реліз TASK-866 і фази 2 на staging/prod заблоковано (TASK-871). ADR-016 не змінюється, це конфігурація в його межах |

**Наслідки errata:**
- Плюс: блокування не є оракулом існування пошти й переживає рестарт і кілька інстансів. `forgot` не має часового оракула. Сирий токен скидання ніде не зберігається. Скидання пароля й злиття миттєво гасять усі сесії. Злиття не відкриває бізнес «переможця» сесії «програвшого».
- Ціна: ще одна глобальна таблиця й секрет (pepper). Запис у БД на кожну невдалу спробу входу (раніше для невідомої пошти — пам'ять). Ще одна колонка в запиті middleware. Квота може заважати масовому онбордингу команди: стеля 200 на добу в конфігу. Legacy-клієнт із не тим slug блокується (E-1).
- **Human gates** (§6) доповнюються: (6) виклик `auth_identity_release` на staging/prod; (7) реліз TASK-866 / фази 2 лише після smoke F-1.

**Задачі (§6) доповнюються:** TASK-871 (devops-engineer, F-1) і TASK-872 (backend-developer + integrations-developer, лист «Пароль змінено», F-10). Обсяг TASK-862 доповнює §31.16, TASK-865 — §31.7, TASK-868 перевіряє реалізацію за §31.15 і звітом TASK-860.

**Відхилені варіанти рев'ю:** F-2 варіант B (мінімум з `last_failed_at`, задокументований залишковий ризик) відхилено: оракул лишається, а рестарт скидає лічильник невідомої пошти. F-4 варіанти 2–3 (`email_verified_at`, лист запрошення на адресу запрошеного) відкладено до V2, бо змінюють потік прийняття запрошення. F-9 варіант «сирий токен у БД із `SKIP LOCKED` і обнуленням» відхилено, бо генерація у воркері дешевша й безпечніша.

---

## 10. Реалізація (статус на 2026-11-18, TASK-869)

**Стан:** ADR `Accepted`. Код фаз 0–2 і контракт §31 є в гілці `claude/mobile-appointment-booking-app-0728c3` (worktree). Нічого не змерджено в `main`, нічого не застосовано на staging. Усі задачі в статусі `review`, крім TASK-859/860 (done) і planned-задач 867/868/870.

| Задача | Що зроблено | Статус |
|---|---|---|
| TASK-859 | Контракт `beauty-contracts.md` §31 (v0.34, errata v0.34.1 з TASK-860) | done; лог TASK-859 відсутній у `.claude/logs/tasks/` |
| TASK-860 | Threat-model `docs/reviews/identity-threat-model.md` (approved-with-notes), errata §9 ADR | done; лог відсутній |
| TASK-861 | Preflight `deploy/staging/db/preflight-identity-duplicates.sql` (лише читання), перевірено локально на синтетиці | review; на staging не запускався (Q4 відкрито) |
| TASK-862 | Міграція `20261117140201_auth_global_identities`: ролі, 4 глобальні таблиці (deny-all RLS), 20 функцій `auth_*`, `users.identity_id`, `sessions_valid_after`, тригер пошти, allowlist злиття, `Down` з відмовою; `grants.sql`, `setup-db.ps1`, `schema.sql` воркера | review |
| TASK-863 | Логін у 2 кроки, `login/select`, `memberships`, `switch` (новий DI-scope), legacy `tenant`, блокування на ідентичність (`auth_login_attempts`), `LoginEmailThrottle`, `sessions_valid_after` у middleware | review |
| TASK-864 | `email_in_use` (створення/прийняття), квота 20/добу (429), `owner_email_in_use` (control plane і `/api/platform/tenants`), `activate_pending`, метрики, тест SQL-запитів middleware | review; Q8 (погодження з ShelfGuard) — gate до мерджу |
| TASK-865 | Скидання пароля: `forgot` (202, черга, ліміти), `reset`, воркер (`password-reset.ts`), токен генерує воркер | review; текст листа — плейсхолдер (Q7) |
| TASK-866 | Frontend: вхід без коду бізнесу, вибір бізнесу, перемикач, «Забули пароль», BFF (cookie квитка, `switch`, `password/*`) | review; mock + BFF; інтеграція з реальним backend — після 863–865 і F-1 |
| TASK-871 | F-1 (E-14): статична адреса `web` (`BEAUTY_WEB_IP`), `Beauty__TrustedProxies` = шлюз + web, nginx перезаписує XFF, smoke-скрипт | review; на сервері не застосовано, smoke не виконано (блокує реліз) |
| TASK-872 | Лист «Пароль змінено» (`changed_notice`), повтори до 24 год | review; текст — плейсхолдер (Q7), без часу зміни (обмеження) |
| TASK-867, 868 | QA-матриця, security-review реалізації | planned |
| TASK-869 | Ця документація: `api.md`, `database.md`, `domain-model.md`, `runbook.md` §2b, `known-issues.md`, `beauty-contracts.md` §14/§31, `deploy/staging/README.md` | in progress: ADR-024, api, database, domain-model, runbook, known-issues, contract §14/§31, staging README — готово; примітки в ADR-002/006/018 — ні |
| TASK-870 | Фаза 3 (contract): видалення `users.password_hash`, лічильників, `tenants_login_lookup`, поля `tenant` | planned; після `Sunset` і стабілізації, human gate |

### Відхилення від ADR (і від першого тексту §31)

1. **Роль для звільнення.** ADR §2.3 описував одну роль `beautycrm_identity`. Міграція додає `beautycrm_identity_release` (NOLOGIN) лише для `auth_identity_release` (E-7), щоб не розширювати права першої. Ops створює обидві ролі (`init-catalog-role.sql`). `EXECUTE` на звільнення отримують `beautycrm_owner` (через `grants.sql`) і `beautycrm_identity_release`.
2. **Скидання пароля.** Черга доставки — рядок `auth_password_resets` (не outbox з payload), токен генерує воркер (E-4). API лише ставить запит у in-process чергу й віддає `202` до роботи з БД (E-3).
3. **`forgot`:** IP-ліміт стоїть перед валідацією `422 invalid_email` (як і для login).
4. **Квота ідентичностей.** Не діє на `owner_provisioning` і `platform` (новий tenant, довірений викликач). Для запрошень персоналу — 20 / 24 год, діапазон конфігу 1..200. Початкова реалізація мапила квоту на `invite_invalid`, виправлено в TASK-864 (`429 rate_limited` + `Retry-After`).
5. **`/api/platform/tenants`.** Власник задає пароль одразу, ідентичність `active` (контракт §31.8.3). ADR §2.7 згадував `create_pending` для обох шляхів; для цього шляху це уточнено контрактом.
6. **`switch`.** Відсутній або чужий `refreshToken` → `401 invalid_token`, не `422` (§31.15 п.10). `422` лишився для `tenantId` і `same_tenant`.
7. **Поля й таблиці.** `identities.failed_login_count` і `lockout_until` не створювались (E-2). Блокування — `auth_login_attempts` з HMAC-ключем (pepper).
8. **Dual-write.** `users.password_hash` пишеться й при скиданні всіх членств до фази 3. Міграція цю колонку не змінює.
9. **Синонім назви allowlist.** Приймається синонім `beautycrm.identity_merge_allow` (з постановки TASK-862); різні значення → помилка.
10. **Лист «Пароль змінено».** Без часу зміни (`claim_delivery` не повертає timestamp), 24-годинний бюджет у пам'яті процесу, без метрик у воркері (`beauty-contracts.md` §31.7.3, KI-I3, KI-I4).
11. **Тести.** ~25 сидів `users` переписано на спільний хелпер (`IdentitySeed.cs`, `seed-user.ts`); повний backend-прогін: 0 падінь, окрім відомого flaky `ChatLiveSmokeFixesTests` (KI-003).

### Відкриті gates (не виконано)

- **Q4:** рішення щодо 2 груп дублікатів пошти на staging (злиття чи розведення) за звітом preflight. До цього міграція не застосовується (human gate).
- **Q7:** текст листів скидання й «Пароль змінено», адреса відправника `EMAIL_FROM`.
- **Q8:** погодження з ShelfGuard `409 owner_email_in_use` до мерджу TASK-864.
- **F-1 / E-14:** застосування конфігу на сервері і smoke (`deploy/staging/README.md` §6.6). Реліз TASK-866 і фази 2 на staging/prod заблоковано (KI-I6).
- **Міграція на staging:** preflight, allowlist, `grants.sql` (`runbook.md` §2b).
- **Env-приклади:** `Auth__LoginAttemptKeyPepper` і `Auth__LegacyTenantLogin__*` відсутні в `.env.api.example` / `.env.example` (KI-I1).
- **Незавершені тести з §31.15:** TASK-867 (QA-матриця), TASK-868 (security-review реалізації).
- **Примітки «уточнено ADR-024» в ADR-002, ADR-006, ADR-018** (з TASK-869) — не виконано в цьому проході; потрібен окремий крок.
- **Фаза 3 (TASK-870)** — після Sunset (Q12: 30 днів після релізу фази 2) і стабілізації; human gate.
