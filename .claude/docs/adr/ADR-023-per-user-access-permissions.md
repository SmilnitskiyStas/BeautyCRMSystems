# ADR-023: Налаштовувані права доступу користувачів (RBAC per-user)

**Date:** 2026-11-15
**Status:** Accepted (2026-11-15, human gate пройдено). Змінює межу авторизації (auth/security boundary) і містить міграцію даних (backfill пресетів). Відкриті питання Q1–Q11 прийняті з рекомендованими дефолтами з ADR.
**Author:** project-architect
**Deciders:** людина (власник продукту), 2026-11-15. Ухвалено продуктові рішення (ролі, хто видає доступи, модель блоків, «свій виторг», модулі як верхня межа) і архітектурні рішення цього ADR, gate пройдено. Уточнення після security-рев'ю TASK-848 (B1, B2, M1–M7, M4 — рішення людини щодо цін у списках) внесено 2026-11-15 до §2.5, §2.6, §2.9 і до контракту §30. Семантику моделі вони не змінюють.
**Уточнює (після `accepted`):** ADR-006 (ролі й запрошення), ADR-008 (ієрархія керування, scope майстра). Старі рішення на місці не редагуються, туди додаються примітки «уточнено ADR-023» (TASK-858).
**Не змінює:** ADR-002 (RLS: ізоляція tenant), ADR-018 (власник створюється лише control plane).

---

## 1. Context

Зараз доступ визначається **лише роллю** з JWT (`role` = `owner | admin | specialist`):
- `AuthPolicies.Staff / Management / Owner` (`backend/BeautyCrm.Api/Auth/AuthSetup.cs`) через `RequireRole` на claim з токена;
- `[ReminderRoles(...)]` (`ReminderContactsController.cs`, order −3000, 403 `forbidden_role` ще до валідації тіла) на контролерах клієнтів, дзвінків, нагадувань, купонів, кампаній, собівартості;
- перевірки `actor.Role == Roles.…` у сервісах. Найважливіша з них, `AppointmentAccessService.EffectiveSpecialistId`, обмежує майстра власними записами (ADR-008). Решта: `AiSettingsService` (ключ ШІ лише owner), `InboxService`, маркетинг;
- `RolePolicy.CanManage`: owner → admin/specialist, admin → specialist, owner недоторканний (`TokenCodec.cs`);
- на frontend role-хелпери `canSeeFinance`, `canUseInbox`, `navFor`, `canOpen` (`frontend/features/beauty-auth/permissions.ts`).

Що не так:
1. Матриця фіксована в коді. Керівник не може, наприклад, відкрити майстру загальний календар, закрити адміністратору виторг або показати майстру суму його власного виторгу.
2. Роль береться з JWT (TTL 15 хв). Зміна ролі або вимкнення користувача діють лише після закінчення access-токена. `ActiveSpecialistFilter` латає це тільки для деактивованого профілю майстра.
3. Немає ролі «заступник».

**Рішення людини (остаточні, 2026-11-15):**
- Ролі: директор (= `owner`), адміністратор (`admin`), заступник (новий), майстер (`specialist`). Роль задає пресет, а права налаштовуються **на користувача** поверх нього.
- Доступи видають і змінюють директор та адміністратор. Адміністратор діє лише щодо ролей нижче себе. Директора змінити не можна.
- Модель: блоки (розділи меню), у кожного блоку свій рівень доступу з підпунктами. Рівні обирає керівник (приклад для календаря: «загальний», «лише свої», «немає доступу»). Для майстра є окремий рівень «свій виторг».
- Права відкривають розділи меню на frontend і **обов'язково** перевіряються на backend. Frontend відповідає лише за UX.
- Модулі tenant (`beauty_*`) залишаються верхньою межею: право не може відкрити вимкнений модуль.

---

## 2. Decision

### 2.1 Ролі та ранги

| Роль (код) | UI-назва | Ранг | Хто призначає |
|---|---|---|---|
| `owner` | Директор | 4 | лише control plane (ADR-018). Через tenant API не створюється, не змінюється, не вимикається |
| `admin` | Адміністратор | 3 | директор |
| `deputy` (новий) | Заступник | 2 | директор; адміністратор (бо ранг нижчий), див. Q1 |
| `specialist` | Майстер | 1 | директор, адміністратор |

- `RolePolicy.CanManage(actor, target)` = `rank(actor) > rank(target)` і `target ≠ owner`. Для наявних пар результат не змінюється (owner→admin/specialist, admin→specialist). Додаються пари owner→deputy і admin→deputy.
- **Заступник не видає й не змінює доступи** (рекомендація людини прийнята): ключ `team.manage` має стелю ролі, тож його може мати лише `admin` (§2.2). Директор не може видати його заступнику чи майстру.
- Директор має всі права неявно: рядків у БД для нього немає, і перевірка для `owner` повертає максимальний рівень кожного ключа (з урахуванням модулів). Тому директора «змінити не можна» на рівні моделі.

### 2.2 Каталог прав (код, а не БД)

Каталог задається у коді, в Application (`PermissionCatalog`), і має версію `catalogVersion`. **Ключ** має вигляд `блок.підпункт`. У кожного ключа впорядкований список **рівнів** від меншого до більшого. «Не можна видати більше, ніж маєш» перевіряється порівнянням індексів. Блок відповідає розділу меню, ключі блоку відповідають підпунктам на екрані доступу.

| Блок (розділ меню) | Ключ | Рівні (зростання) | Модуль-межа | Інваріант |
|---|---|---|---|---|
| Календар і записи (`/beauty/calendar`, `/beauty`) | `calendar.view` | `none` · `own` · `all` | `beauty_booking` | — |
| | `calendar.manage` (створити, перенести, скасувати, статус) | `none` · `own` · `all` | `beauty_booking` | `≤ calendar.view` |
| Клієнти (`/beauty/clients`) | `clients.view` | `none` · `own` · `all` | `beauty_clients` | — |
| | `clients.edit` (картка, нотатки, спосіб зв'язку, згода, телефон із чату) | `none` · `own` · `all` | `beauty_clients` | `≤ clients.view` |
| | `clients.contacts` (повний телефон/email, §25.9) | `masked` · `full` | `beauty_clients` | — |
| | `clients.calls` (задачі «зателефонувати», `/beauty/calls`) | `none` · `all` | `beauty_clients` | потребує `clients.view = all` |
| Виторг і аналітика (`/beauty/analytics`, KPI огляду) | `revenue.view` | `none` · `own` («свій виторг») · `all` | `beauty_analytics` | — |
| | `revenue.costs` (собівартість, виплата, чистий дохід, ADR-014) | `none` · `view` · `edit` | `beauty_analytics` (редактор також `beauty_catalog`) | потребує `revenue.view = all` |
| Розмови й канали (`/beauty/inbox`, `/beauty/channels`) | `channels.inbox` | `none` · `view` · `reply` | `beauty_channels` | — |
| | `channels.settings` (підключення каналів, токени) | `none` · `edit` | `beauty_channels` | — |
| ШІ-асистент (`/beauty/ai`, віджет) | `ai.use` (віджет, підтвердження дій у режимі `confirm`) | `none` · `use` | `beauty_ai` | — |
| | `ai.settings` (моделі, режим, промпт, ліміти) | `none` · `edit` | `beauty_ai` | ключ провайдера ШІ лише в директора, не делегується (§2.6) |
| Послуги, ціни, акції (`/beauty/services`, `/beauty/promos`) | `services.catalog` | `view` · `edit` | `beauty_catalog` | читання потрібне календарю, тому рівня `none` немає |
| Розсилки й купони (`/beauty/campaigns`) | `marketing.campaigns` | `none` · `edit` | `beauty_marketing` | — |
| Спеціалісти (`/beauty/staff`) | `staff.access` | `own` («Мій профіль») · `all` (перегляд команди й графіків) · `edit` (профілі, графіки, послуги майстрів, рішення щодо відсутностей) | `beauty_booking` | нотатки відсутностей (ADR-008) бачить лише `edit` |
| Заклади (`/beauty/locations`) | `locations.access` | `own` (сторінки своїх закладів) · `all` (список) · `edit` | `beauty_booking` | — |
| Налаштування (`/beauty/settings`) | `settings.general` (нагадування, загальні налаштування) | `none` · `view` · `edit` | — | політика скасування: див. §2.6 / Q6 |
| Команда й доступи (`/beauty/team`) | `team.manage` | `none` · `manage` | — | **стеля ролі:** лише `admin` |

Правила каталогу:
- **`own`** означає дані, прив'язані до `users.specialist_id` користувача: власні записи, клієнти, які мали або мають записи до цього майстра, виторг за власними записами, власний профіль і заклади, де майстер працює. Без прив'язки `own` дає порожню вибірку (так само, як нинішній `EffectiveSpecialistId → Guid.Empty`), а UI показує попередження «Користувач не прив'язаний до профілю майстра». Чи можна прив'язувати профіль до admin/deputy, вирішує Q5.
- **Відсутній рядок** означає найнижчий рівень ключа (fail-closed). Невідомий ключ або рівень у БД (наприклад, ключ вилучено з каталогу) ігнорується й теж дає найнижчий рівень.
- Інваріанти перевіряються під час запису. Порушення дає `422 permission_inconsistent`, автоматичного «виправлення» немає.
- Новий ключ з'являється лише разом із міграцією, яка явно проставляє його значення наявним користувачам за пресетом ролі (без мовчазних дефолтів у коді).

### 2.3 Пресети ролей

Пресети відтворюють **поточну** матрицю для owner/admin/specialist, тому після міграції поведінка цих ролей не змінюється. Пресет заступника є пропозицією (Q2).

| Ключ | owner (неявно) | admin | deputy | specialist |
|---|---|---|---|---|
| `calendar.view` | all | all | all | own |
| `calendar.manage` | all | all | all | own |
| `clients.view` | all | all | all | none |
| `clients.edit` | all | all | all | none |
| `clients.contacts` | full | full | full | masked |
| `clients.calls` | all | all | all | none |
| `revenue.view` | all | all | none | none (Q3) |
| `revenue.costs` | edit | edit | none | none |
| `channels.inbox` | reply | reply | reply | none |
| `channels.settings` | edit | edit | none | none |
| `ai.use` | use | use | use | none |
| `ai.settings` | edit | edit | none | none |
| `services.catalog` | edit | edit | view | view |
| `marketing.campaigns` | edit | edit | none | none |
| `staff.access` | edit | edit | all | own |
| `locations.access` | edit | edit | all | own |
| `settings.general` | edit | view | none | none |
| `team.manage` | manage | manage | none | none |

Пресет застосовується під час створення користувача (прийняття запрошення), зміни ролі та дії «Скинути до пресету». Після цього рядки зберігаються як звичайні значення: пізніша зміна пресету в коді **не** переписує права вже наявних користувачів.

### 2.4 Зберігання (1)

Нові таблиці в схемі auth створює `database-engineer` (TASK-849):

```sql
-- поточні права (матеріалізовано: що в БД, те й діє)
user_permissions (
  tenant_id          uuid NOT NULL,
  user_id            uuid NOT NULL,
  permission         text NOT NULL,   -- 'calendar.view'
  level              text NOT NULL,   -- 'own'
  updated_at         timestamptz NOT NULL DEFAULT now(),
  updated_by_user_id uuid NULL,       -- NULL = система/міграція
  PRIMARY KEY (tenant_id, user_id, permission),
  FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,  -- реалізовано TASK-849: без крос-tenant автора
  CHECK (permission ~ '^[a-z_]{1,32}\.[a-z_]{1,32}$'),
  CHECK (level ~ '^[a-z_]{1,16}$')
)
-- RLS ENABLE + FORCE, політика tenant_isolation за app.tenant_id (ADR-002)

users.access_version  integer NOT NULL DEFAULT 1   -- +1 за кожної зміни ролі, прав, активності

invites.role           CHECK розширено: ('admin','deputy','specialist')
invites.permissions    jsonb NOT NULL   -- (B1) повний матеріалізований знімок усіх ключів, обрізаний стелею запрошувача;
                                        --  backfill: ВСІМ наявним запрошенням (не лише pending) записується пресет ролі;
                                        --  DEFAULT '{}' (порожній знімок = найнижчі рівні) до TASK-851, CHECK jsonb_typeof = 'object'

beauty_appointments.created_by_user_id uuid NULL   -- автор запису (scope own, §30.2 M3); композитний FK на users RESTRICT,
                                                   -- частковий індекс (tenant_id, created_by_user_id) WHERE NOT NULL; наявні рядки NULL

users.role             CHECK розширено: ('owner','admin','deputy','specialist')

-- журнал змін доступу (append-only)
user_access_audit (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL,
  actor_user_id   uuid NULL,           -- NULL = міграція/система
  target_user_id  uuid NULL,           -- NULL для подій запрошення без користувача (Q-C2)
  invite_id       uuid NULL,
  action          text NOT NULL CHECK (action IN ('permissions_changed','role_changed','preset_reset',
                                                  'invited','invite_revoked','invite_accepted','activated','deactivated','migrated')),
  changes         jsonb NOT NULL,      -- [{ "permission": "...", "from": "...", "to": "..." }] / {"role":{"from","to"}}
  created_at      timestamptz NOT NULL DEFAULT now(),
  CHECK (target_user_id IS NOT NULL OR invite_id IS NOT NULL),
  FOREIGN KEY (tenant_id, target_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
  FOREIGN KEY (tenant_id, actor_user_id)  REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
  FOREIGN KEY (tenant_id, invite_id)      REFERENCES invites (tenant_id, id) ON DELETE RESTRICT
)
-- RLS ENABLE + FORCE; роль застосунку має лише INSERT і SELECT (без UPDATE/DELETE); індекси (tenant_id, target_user_id, created_at DESC),
-- (tenant_id, created_at DESC, id DESC) для списку директора, часткові (tenant_id, invite_id, created_at DESC) WHERE invite_id IS NOT NULL.
-- Append-only також тригером (UPDATE/DELETE/TRUNCATE); єдиний виняток - рядковий DELETE від current_user = beautycrm_audit_purge.
-- Реалізація purge: політики audit_purge_select/audit_purge_delete TO beautycrm_audit_purge; EXECUTE ... TO beautycrm_worker (grants).
-- Retention (Q7, 3 роки): лише через SECURITY DEFINER-функцію purge_user_access_audit(before timestamptz), власник — окрема NOLOGIN-роль;
-- EXECUTE має тільки роль воркера; прямого DELETE ні в кого немає. Для композитного FK до invites потрібен UNIQUE (tenant_id, id).
```

- Якщо в `users` ще немає `UNIQUE (tenant_id, id)`, його треба додати: на ньому тримаються композитні FK, які не дають прив'язати права до користувача іншого tenant.
- Валідність ключів і рівнів перевіряє Application за каталогом. БД перевіряє лише формат, щоб новий ключ не вимагав міграції CHECK.

### 2.5 Як права потрапляють в авторизацію (2)

**Джерело істини – БД на кожен автентифікований запит. JWT не розширюється.**

1. Новий `AccessContextMiddleware` стоїть після автентифікації й встановлення tenant-контексту та перед `UseAuthorization`. Для JWT користувача tenant він **одним запитом** під RLS читає `users.role, is_active, state, specialist_id, access_version` разом із рядками `user_permissions` (PK-індекс, ≤ ~20 рядків).
   - Користувача немає, `is_active = false` або `state ≠ active` → `401 session_revoked`. Frontend вилоговує. Це закриває нинішнє вікно до 15 хв після вимкнення.
   - **Fail-closed (уточнено TASK-848, M6):**
     - немає `sub` або `tenant_id` чи вони невалідні → `401 invalid_token`;
     - помилка або таймаут БД → `503 access_unavailable`, запит далі не йде;
     - middleware діє для схеми JwtBearer, а для `PlatformKey` і `ControlPlaneHmac` не застосовується.
   - Claims `role` і `specialist_id` у `ClaimsPrincipal` **замінюються значеннями з БД**: усі наявні екземпляри цих claims видаляються з усіх identity, потім додається по одному значенню з БД. Через це наявні політики `Staff/Management/Owner`, `ReminderRoles` і `ToActor()` одразу бачать актуальну роль, ще до переведення ендпоінтів на права.
   - Результат кладеться в scoped `IAccessContext` (`AccessSet` = рівні після застосування модулів, §2.7). За запит він завантажується один раз.
2. `[RequirePermission("clients.view", "own")]` – action-фільтр з order −3000 (як `ReminderRoles`, тобто 403 повертається до 422). Порядок перевірок такий: автентифікація → `TenantActiveFilter` → `[RequireModule]` (`403 module_disabled`) → `[RequirePermission]` (`403 forbidden_role` з полями `permission`, `required`) → `ActiveSpecialistFilter` → прив'язка й валідація тіла.
3. Scope `own/all` застосовує **Application**, а не контролер. `Actor` отримує властивість `Access` (`AccessSet`), а `AppointmentAccessService.EffectiveSpecialistId` і подібні перевірки дивляться на `Access.Level("calendar.view")`, а не на `Role`. **Уточнено TASK-848 (B2):** `Actor.Access` обов'язковий, і в production-коді дефолту «пресет за роллю» немає. `ToActor()` без `IAccessContext` (наприклад, middleware не відпрацював) повертає `AccessSet.None`, тобто fail-closed. Пресет для тестів дає лише тестова фабрика (`TestActors.WithPreset(role)`) у тестовому проєкті. Архітектурний тест забороняє виклик пресету як дефолту `Actor` у збірках Api, Application і Infrastructure.
4. **Швидкість дії змін:** зміна або відкликання права діє **з наступного API-запиту** цього користувача, без перелогіну. TTL access-токена (15 хв) і refresh (14 днів) більше не впливають на актуальність прав. Refresh-токени при зміні прав не відкликаються (у цьому немає потреби). Вимкнення користувача відкликає їх, як і досі.
5. **Синхронізація UI:** кожна автентифікована відповідь несе заголовок `X-Access-Version`. Якщо він розходиться з версією в пам'яті, frontend перечитує `GET /api/auth/me`. Так само він робить після `403 forbidden_role` з полем `permission` і після повернення фокусу у вкладку. Меню перебудовується без перезавантаження, а відкрита сторінка без доступу показує «Доступ змінено керівником».
6. Роль у JWT лишається тільки для інформації й сумісності. Refresh видає токен з актуальною роллю з БД, як і зараз.

### 2.6 Захист від ескалації привілеїв (4)

Усі перевірки виконує Application (`AccessAdminService`) в одній транзакції. Порядок блокувань такий: спершу `SELECT … FOR SHARE` рядка актора, потім `SELECT … FOR UPDATE` рядка цільового користувача (уточнено TASK-848). Права того, хто змінює, читаються з БД у тій самій транзакції під цим блокуванням, а не з JWT.

1. **Ранг:** змінювати роль, права, статус і запрошення можна лише щодо користувачів з нижчим рангом (`CanManage`). Інакше `403 forbidden_role`.
2. **Право керувати:** потрібен `team.manage = manage` (директор має його неявно). Без нього `403 forbidden_role`.
3. **Стеля «не більше, ніж маєш»:** кожне нове значення ключа має бути ≤ ефективного рівня актора. Ключі, поточне значення яких **вище** за стелю актора (їх видав директор), адміністратор не може змінювати ні вгору, ні вниз. Такий запит дає `403 permission_ceiling`, а ключі, яких запит не торкається, зберігаються. Запит частковий (PATCH-семантика за ключами).
4. **Стеля ролі:** `team.manage` можна мати лише ролі `admin`, і видати його може лише директор. Для інших ролей відповідь `422 permission_not_allowed_for_role`.
5. **Самозміна заборонена** будь-кому, включно з директором: `422 cannot_modify_self` (код уже існує в `SetUserActiveAsync`).
6. **Директор:** ролі `owner` немає серед допустимих значень tenant API. Директора не можна понизити, вимкнути або змінити йому права. Отже, інваріант «в tenant завжди є директор» тримається без окремого лічильника: жоден шлях tenant API не змінює рядок `owner`. Передача й додавання директорів відбуваються поза tenant API (Q10, control plane).
7. **Зміна ролі** застосовує пресет нової ролі, обрізаний стелею актора. Обрізані ключі повертаються в `clamped[]`, а UI показує їх до підтвердження.
8. **Модуль:** ручне надання ключа вимкненого модуля дає `422 module_disabled`. Збережені раніше значення лишаються, бо модуль обмежує їх під час обчислення (§2.7).
9. **Конкурентність:** запис вимагає `expectedVersion` (`users.access_version`). Розбіжність дає `409 access_version_conflict`, тож зміни двох керівників не губляться мовчки.
10. **Зарезервовано за директором (не делегується через права):** ключ провайдера ШІ (§20.9), керування адміністраторами, політика скасування й повернень (A2, фінансова; рекомендація, Q6), дії control plane.
11. **Запрошення** (уточнено TASK-848, B1 і M5). `POST /api/invites` приймає `role ∈ {admin, deputy, specialist}` і необов'язкові `permissions`.
    - При створенні сервер обчислює **повний матеріалізований набір**: пресет, на нього перевизначення, далі кожен ключ обрізається до стелі запрошувача. Обрізані ключі повертаються в `clamped[]`.
    - Набір зберігається знімком у `invites.permissions` (`NOT NULL`).
    - При прийнятті перевіряється, що запрошувач досі активний, має `team.manage` і вищий ранг, ніж роль запрошення. Набір повторно обрізається **поточною** стелею запрошувача. Якщо запрошувач вимкнений або вже не може керувати цією роллю, запрошення недійсне (`404 invite_invalid`, той самий код, що й для простроченого).
    - Pending-запрошення запрошувача автоматично відкликаються, коли його вимикають, змінюють йому роль або знижують `team.manage`. Аудит `invite_revoked` з `actor = null`.

### 2.7 Модулі як верхня межа

`effective(key) = moduleEnabled(key.module) ? granted(key) : lowest(key)`. Обчислення відбувається під час читання, рядки в БД не змінюються, тому після повторного ввімкнення модуля права повертаються. Ендпоінти зберігають `[RequireModule]`, і він спрацьовує **першим**. Ключ, прив'язаний до двох модулів (редактор собівартості), потребує обох. `GET /api/auth/me` повертає `effective`, а екран доступу повертає ще й `granted` та прапорець `moduleDisabled`.

### 2.8 Аудит (5)

**Рекомендація: робити в V1.** Кожна зміна з §2.6, а також invite, revoke, activate і deactivate, пише рядок у `user_access_audit` у **тій самій транзакції**. Пишеться diff, тобто лише змінені ключі. Міграція пише `migrated` з `actor_user_id = NULL`.

- Журнал не містить ПІІ клієнтів, паролів чи токенів, лише id користувачів, ключі й рівні.
- Перегляд `GET /api/users/access-audit` у V1 доступний лише директору (Q7).
- Строк зберігання визначає Q7. Очищення робить окрема задача воркера, не в межах цього ADR.

### 2.9 Приватність (6)

- **Сервер формує відповідь за правами.** Дані без права не потрапляють у JSON, і frontend не просто «ховає» їх. Поле, на яке немає права, **відсутнє** (`null` або не серіалізується), а не дорівнює 0.
- **Ціни в списках (рішення людини M4, 2026-11-15).** `priceOriginal`/`priceFinal` у списках записів і в `/overview` бачить лише `revenue.view = all`. Іншим ці поля не повертаються: з них можна підсумувати виторг. Деталь запису `GET /appointments/{id}` зберігає ціну для всіх, хто бачить запис, бо вона потрібна для оплати. `OverviewKpi.Revenue` стає `decimal?` з `JsonIgnore(WhenWritingNull)`.
- **Виторг:**
  - `revenue.view = own` – новий `GET /api/beauty/me/revenue?from&to` віддає лише суму й кількість **власних** записів (визначення «виторгу» – Q4). Без інших майстрів, без загальної суми tenant, без собівартості, виплат і чистого доходу.
  - `revenue.view = all` – `/analytics/*` і KPI виручки в `/overview`.
  - Без `revenue.view = all` поле виручки в `/overview` відсутнє, а сам `/overview` потребує `calendar.view = all`.
  - `revenue.costs` – поля ADR-014 і редактор собівартості.
- **ПІІ клієнтів:**
  - Без `clients.contacts = full` телефон і email маскуються на сервері в усіх DTO: записи, клієнти, inbox.
  - `clients.view = own` обмежує вибірку клієнтами, які мають записи до свого майстра. Картка чужого клієнта дає 404 без витоку факту існування, як для записів у ADR-008.
  - Нотатки відсутностей бачить лише `staff.access = edit` (ADR-008 без змін).
- **ШІ-асистент (ADR-005):** tools, які виконуються від імені співробітника (`AiToolExecutor`, роль `analytics`), перевіряють ті самі права актора. Через ШІ не можна отримати виторг, клієнтів чи розмови, недоступні напряму. Це окремий критерій приймання (TASK-853).
- Логи не містять наборів прав разом з ПІІ. 403 логуються з ключем права, без тіла запиту.

### 2.10 RLS і tenant-ізоляція (7)

- Обидві нові таблиці мають `tenant_id` і `RLS FORCE` з політикою за `app.tenant_id` (ADR-002). Композитні FK `(tenant_id, user_id)` не дають прив'язати права до користувача іншого tenant.
- Middleware читає права **під RLS поточного tenant** з JWT. Якщо користувача іншого tenant не знайдено, повертається 401, а не порожній набір.
- **RLS не реалізує права всередині tenant** (див. альтернативи). RLS ізолює tenant-и, а права є авторизацією в Application. Ці рівні незалежні: помилка в правах не може відкрити дані іншого tenant.
- Ролі БД: застосунок має SELECT/INSERT/UPDATE/DELETE на `user_permissions` і SELECT/INSERT на `user_access_audit`. Воркеру й control plane доступ не потрібен (оновити `deploy/staging/db/grants.sql` і `scripts/dev/setup-db.ps1`).

### 2.11 Зворотна сумісність і міграція (3)

- **Ролі:** коди `owner/admin/specialist` не змінюються. Додається `deputy`, і `Roles.IsTenantRole` та `ToActor()` його приймають. Політику `Staff` розширено на `deputy`. `Management` лишається `owner/admin` до повного переведення ендпоінтів, тобто заступник поки fail-closed.
- **Переведення ендпоінтів:** кожен `[Authorize(Policy = Management)]`, `[ReminderRoles]` і рольова перевірка в сервісах tenant-ендпоінтів замінюється на `[RequirePermission]` або перевірку `Access` за картою в контракті §30 (TASK-846). Політика `Owner` лишається тільки для зарезервованих дій (§2.6 п.10). Критерій завершення: на tenant-ендпоінтах не лишається жодного `Management` чи `ReminderRoles` (grep-перевірка в TASK-852/853), після чого `ReminderRolesAttribute` видаляється.
- **Коди помилок:** 403 через брак права зберігає код `forbidden_role`, на який уже покладаються frontend і тести, і додає поля `permission` та `required`. Стандартні 403 політик без тіла лишаються 403.
- **Тести:** до переведення ендпоінтів QA фіксує **базову матрицю** «ендпоінт × owner/admin/specialist → статус/код» на поточному коді (TASK-847). Після міграції та сама матриця на пресетах має збігатися повністю. Будь-яка розбіжність вважається регресією, якщо вона не позначена явно в §30. Юніт-тести, що створюють `new Actor(..., role, specialistId)`, переходять на тестову фабрику `TestActors.WithPreset` (B2). Обов'язкові тест-вимоги зведено в §30.13 контракту.
- **Запрошення:** міграція записує наявним pending-запрошенням знімок пресету ролі (`invites.permissions` стає `NOT NULL`, B1). При прийнятті діють правила §2.6 п.11. Формат токена й потік ADR-006/§27.8 не змінюються.
- **Міграція користувачів** (адитивна, ідемпотентна, одна транзакція): для кожного наявного `admin` і `specialist` вставити рядки пресету (`ON CONFLICT DO NOTHING`), для `owner` нічого не вставляти, у журнал записати `migrated`. Поведінка не змінюється, бо пресет дорівнює поточній матриці.
- **Відкат (`Down`):** якщо існують користувачі `deputy` або права відрізняються від пресетів, `Down` **відмовляє** з явною помилкою: втрата налаштувань має бути свідомою. Інакше він видаляє таблиці, колонки й розширення CHECK. Відкат на staging/prod проходить через окремий human gate.
- **Mobile (ADR-021/022)** і публічні ендпоінти не зачіпаються, бо там немає JWT співробітника. Worker не зачіпається, крім оновлення тестової схеми за наявною конвенцією.

### 2.12 API (ескіз; повний контракт – §30, TASK-846)

| Метод | Шлях | Право | Примітка |
|---|---|---|---|
| GET | `/api/access/catalog` | автентифікований | блоки, ключі, рівні, модуль, інваріанти, стелі ролей, `catalogVersion`. Підписи (uk) живуть у frontend content |
| GET | `/api/auth/me` | автентифікований | + `accessVersion`, `modules`, `permissions` (effective) |
| GET | `/api/users` | `team.manage` | + `accessVersion`, прив'язка до майстра, `manageable` |
| GET | `/api/users/{id}/access` | `team.manage` | `role`, `accessVersion`, `granted`, `effective`, `assignable` (стеля актора за ключами), `editable`, `moduleDisabled[]` |
| PUT | `/api/users/{id}/access` | `team.manage` | `{ expectedVersion, role?, permissions?: {key: level} }` → новий стан + `clamped[]` |
| POST | `/api/users/{id}/access/reset` | `team.manage` | `{ expectedVersion }` → пресет ролі, обрізаний стелею |
| POST | `/api/invites` | `team.manage` | + `role: deputy`, `permissions?` |
| GET | `/api/users/access-audit?userId&cursor` | директор | курсорна пагінація |
| GET | `/api/beauty/me/revenue?from&to` | `revenue.view ≥ own` | лише власні записи |

Помилки: `403 forbidden_role` (+`permission`,`required`) · `403 permission_ceiling` · `401 session_revoked` · `409 access_version_conflict` · `422 invalid_permission | invalid_level | permission_inconsistent | permission_not_allowed_for_role | module_disabled | cannot_modify_self | invalid_role`.

### 2.13 UI: екран «Команда й доступи» (8)

Маршрут `/beauty/team`, пункт у групі «Система». Видно лише з `team.manage`.

- **Список:** ім'я, роль (бейдж), статус, останній вхід, прив'язаний профіль майстра, pending-запрошення. Рядки директора й власний рядок доступні лише для читання (замок і пояснення).
- **Картка користувача** (сторінка або drawer):
  - вибір ролі (лише ролі нижче актора);
  - кнопка «Скинути до пресету ролі»;
  - блоки-картки в порядку меню. У кожній картці головний рівень радіогрупою («Немає доступу / Лише свої / Усі»), а нижче підпункти. Підпункт вимикається, якщо головний рівень недостатній за інваріантом.
- **Стани варіантів:**
  - вище стелі актора: disabled з поясненням «Не можна надати більше, ніж маєте ви»;
  - вимкнений модуль: картка disabled з позначкою «Модуль вимкнено для закладу»;
  - `own` без прив'язки до майстра: попередження.
- **Збереження:** діалог з diff («Буде надано: …», «Буде відкликано: …», «Обрізано стелею: …»). `409` пропонує перечитати стан. Після збереження показується повідомлення, що зміни діють з наступного запиту користувача.
- **Запрошення:** форма отримує роль «Заступник» і необов'язковий крок «Налаштувати доступи» (за замовчуванням пресет).
- **Журнал:** вкладка «Журнал змін», лише для директора.
- **Гейтинг меню й маршрутів:**
  - `navFor`, `canOpen` і `can*`-хелпери переходять з `role` на `permissions` з `me`, а `homeFor` дає `/beauty` лише за `calendar.view = all`, інакше календар;
  - пункт «Мій профіль» відповідає `staff.access = own`;
  - AI-віджет показується за `ai.use`;
  - frontend лише керує видимістю, а гарантію дає backend.
- **Доступність:** радіогрупи з `fieldset`/`legend`, пояснення disabled-станів через `aria-describedby`, повна робота з клавіатури (skill `accessible-components-review`).

---

## 3. Consequences

**Positive**
- Керівник налаштовує доступ під свій салон без змін у коді, а пресети дають розумний старт.
- Зміна ролі, прав або вимкнення діє з наступного запиту. Нинішнє вікно до 15 хв закривається для всіх ролей, а не лише для деактивованих майстрів.
- Один механізм (`RequirePermission` + `Access` у `Actor`) замінює три (політики, `ReminderRoles`, рольові `if`).
- Приватність виторгу й ПІІ забезпечує сервер, і сюди входить шлях через ШІ.
- Повний журнал «хто, кому, що і коли змінив».
- Поведінка owner/admin/specialist після міграції не змінюється, і це доводить базова матриця.

**Negative / accepted trade-offs**
- Плюс один індексований запит до БД на кожен автентифікований запит. Прийнятно для масштабу салону. Кеш у процесі свідомо не робимо, бо при кількох інстансах API він потребує інвалідації.
- Каталог у коді: новий ключ потребує релізу й міграції backfill. Це свідомий вибір: права не з'являються мовчки.
- Переведення ~80 атрибутів і рольових перевірок – великий диф із ризиком регресій. Його пом'якшують базова матриця (TASK-847) і поетапне перевиведення (A/B).
- Рівні `own` залежать від `specialist_id`. Admin/deputy без прив'язки отримують порожні «свої» дані (Q5).
- `Down` міграції може відмовити, якщо вже є заступники або кастомні права. Це навмисно.

---

## 4. Alternatives considered

- **Права в JWT claims (`perm` або bitmask) з TTL 15 хв** – відхилено. Відкликання затримується до 15 хв, а для виторгу й ПІІ це неприйнятно. Скорочення TTL збільшує навантаження на refresh і не дає гарантії. Відкликання refresh-токенів при кожній зміні змушує людину перелогінитися.
- **Claims + `access_version` у JWT з перевіркою версії в БД** – відхилено. Запит до БД потрібен однаково, тож набір прав дешевше прочитати тим самим запитом, ніж ускладнювати токен.
- **Кеш прав у пам'яті процесу з TTL або pub/sub-інвалідацією** – відхилено для V1: кілька інстансів, інвалідація через outbox/Redis, ризик застарілого доступу. Повернутися можна, якщо профілювання покаже вузьке місце (окремий ADR).
- **JSON-колонка `users.permissions`** – відхилено. Слабша цілісність (немає PK за ключем), складні запити на кшталт «хто бачить виторг», diff для аудиту треба рахувати в коді, а `users` належить чутливому шляху логіну. Перевага «один рядок» неістотна, бо рядків ≤ ~20 і PK-індекс.
- **Зберігати лише перевизначення відносно пресету** – відхилено. Зміна пресету в коді мовчки змінила б доступ тисячам користувачів, і в БД не видно, що діє насправді. Обрано матеріалізацію.
- **Каталог прав у БД, редагований (custom roles)** – відхилено. Сенс прав визначає код, тобто ендпоінти й scope, і ключ без коду нічого не означає. Іменовані кастомні ролі можливі як V2 (шаблони пресетів) без зміни моделі зберігання.
- **Права всередині tenant через RLS (`app.user_id`, політики на кожну таблицю)** – відхилено. Довелося б складно змінювати кожну політику, scope `own` залежить від бізнес-зв'язків (запис → майстер → клієнт), маскування полів не виражається через RLS, а будь-яка помилка ламала б і ізоляцію tenant-ів.
- **Фіксована матриця з новою роллю `deputy` без прав на користувача** – відхилено, бо суперечить рішенню людини («керівник сам обирає»).
- **Новий код помилки `permission_denied` замість `forbidden_role`** – відхилено для V1 через зламану сумісність з frontend і тестами. Нові поля `permission`/`required` дають ту саму інформацію.
- **Дозволити адміністратору знижувати права, видані директором вище його стелі** – відхилено як рекомендацію (Q9): права, видані директором, залишаються під його контролем.

---

## 5. Scope of truth

Після `accepted` ADR-023 авторитетний для:
- ролей tenant, їхніх рангів і правила «хто ким керує» (уточнює ADR-006 і ADR-008 у частині ієрархії);
- моделі прав: каталог ключів і рівнів, семантика `own`, fail-closed, інваріанти, стелі ролей, пресети (§2.2–2.3; конкретні значення каталогу живуть у `PermissionCatalog` і §30 контракту, а зміна **семантики** потребує нового ADR);
- зберігання прав і журналу (§2.4);
- механізму авторизації: БД на запит, заміна claims, порядок фільтрів, синхронізація UI через `X-Access-Version` (§2.5);
- правил захисту від ескалації (§2.6), модулів як верхньої межі (§2.7), серверного формування відповіді для виторгу й ПІІ (§2.9);
- стратегії міграції й відкату (§2.11).

Не авторитетний для: RLS і tenant-контексту (ADR-002), автентифікації й токенів (ADR-006), створення власника (ADR-018), режимів ШІ (ADR-005).

---

## 6. Розбиття на задачі (DAG; ID запропоновані, остаточні призначає `project-manager`)

Порядок: gate ADR → контракт → (базова матриця ∥ threat-model ∥ БД) → ядро доступу → (team API ∥ ендпоінти A ∥ ендпоінти B ∥ frontend на mock) → екран доступу → QA → security review → docs → gate мерджу.

| Задача | Агент | Обсяг | Залежить / артефакти |
|---|---|---|---|
| TASK-846 | project-architect | Контракт **§30 «Права доступу користувачів»** у `beauty-contracts.md`: каталог (JSON-схема `/api/access/catalog`), пресети, **карта «ендпоінт → право/рівень/scope»** для всіх tenant-контролерів (див. перелік у §1), DTO §2.12, коди помилок, правила маскування полів, визначення «свого виторгу» за відповіддю Q4. Надає `API-CONTRACT-ACCESS-V1` | ADR-023 `accepted` (human gate) |
| TASK-847 | qa-engineer | **Базова матриця авторизації на поточному коді**: для кожного tenant-ендпоінта × {owner, admin, specialist} зафіксувати статус і код 403/404 (data-driven тест, що перелічує маршрути через `EndpointDataSource`, щоб новий ендпоінт без рядка в матриці валив тест). Надає `ARTIFACT-AUTHZ-BASELINE-V1` | немає (стартує одразу; рекомендовано до будь-яких змін у фільтрах) |
| TASK-848 | security-reviewer | Threat-model рев'ю контракту §30 і карти ендпоінтів: шляхи ескалації (§2.6), обхід через ШІ-tools, IDOR у scope `own`, маскування ПІІ, порядок фільтрів, `session_revoked`. Висновки йдуть правкою §30 до старту TASK-852/853 | `API-CONTRACT-ACCESS-V1` |
| TASK-849 | database-engineer | Міграція `auth_user_permissions` за §2.4: таблиці `user_permissions` і `user_access_audit`, `users.access_version`, CHECK ролей (+`deputy`) у `users` та `invites`, `invites.permissions`, `UNIQUE(tenant_id,id)` за потреби, композитні FK, RLS FORCE, GRANT (застосунок; audit лише INSERT/SELECT; `grants.sql`, `setup-db.ps1`), **backfill пресетів** для admin/specialist + `migrated` в аудит (ідемпотентно), `Down` з відмовою за наявності deputy або кастомних прав, EF-моделі, тестова схема worker за конвенцією. Тести БД: FORCE RLS, крос-tenant FK, заборона UPDATE/DELETE audit, ідемпотентність backfill, `Down`. Runbook відкату. Надає `ARTIFACT-ACCESS-SCHEMA-V1` | `API-CONTRACT-ACCESS-V1` (ключі для backfill) |
| TASK-850 | backend-developer | **Ядро доступу**: `PermissionCatalog`/пресети/`AccessSet` (Application), `Roles.Deputy`, ранги в `RolePolicy`, `Staff` + deputy; `AccessContextMiddleware` (один запит, заміна claims, `401 session_revoked`, заголовок `X-Access-Version`); `RequirePermissionAttribute` (order −3000, після `RequireModule`); модулі як межа; `Actor.Access` обов'язковий (без дефолту; `AccessSet.None` без контексту; тестова фабрика; архітектурний тест — B2); `GET /api/access/catalog`; `me` + `permissions/accessVersion/modules`; **винести `AppointmentAccessService` з `UserAdminService.cs` в окремий файл** (щоб 851 і 852 не конфліктували). Юніт-тести каталогу, інваріантів, effective з модулями; інтеграційний тест «зміна права → наступний запит 403 без перелогіну». Надає `ARTIFACT-ACCESS-CORE-V1` | `ARTIFACT-ACCESS-SCHEMA-V1` |
| TASK-851 | backend-developer | **API «Команда й доступи»**: `AccessAdminService` (§2.6, п.1–11: ранг, стеля, стеля ролі, самозміна, owner, `clamped`, `expectedVersion`/409, `FOR UPDATE`), `GET/PUT /users/{id}/access`, `reset`, invites з `deputy` і `permissions` (+ матеріалізація при accept), записи аудиту в тій самій транзакції для всіх дій (включно з наявними status/invite/revoke), `GET /users/access-audit` (директор). Тести на кожне правило ескалації й гонку двох керівників | `ARTIFACT-ACCESS-CORE-V1` |
| TASK-852 | backend-developer | **Переведення ендпоінтів A** (календар, записи, скасування, огляд, спеціалісти/графіки/відсутності, заклади, аналітика, собівартість): `[RequirePermission]` замість `Management`/`ReminderRoles`, scope `own/all` у сервісах замість `Role`, поле виручки в `/overview` лише за `revenue.view=all`, новий `GET /api/beauty/me/revenue`. Базова матриця на пресетах зелена без змін очікувань | `ARTIFACT-ACCESS-CORE-V1`, `ARTIFACT-AUTHZ-BASELINE-V1`, висновки TASK-848 (∥ 851, ∥ 853) |
| TASK-853 | backend-developer (skill `ai-business-assistant` для частини ШІ) | **Переведення ендпоінтів B** (клієнти + scope `own`, маскування телефону/email за `clients.contacts` у всіх DTO, дзвінки, вподобання, згода, телефон із чату, inbox/канали, ШІ-налаштування й дії, **права в staff-tools ШІ**, маркетинг/купони, каталог, налаштування). Після 852+853: grep-перевірка «0 `Management`/`ReminderRoles` на tenant-ендпоінтах», видалення `ReminderRolesAttribute`. Базова матриця зелена | ті самі, що в 852 (∥ 851, ∥ 852) |
| TASK-854 | frontend-developer (skill `beauty-admin-ui`) | **Модель прав у frontend**: `can(perms, key, level)`, `navFor`/`canOpen`/`homeFor` і всі `can*`-хелпери на `permissions` з `me` (рольові обгортки лише на перехідний період), реакція на `X-Access-Version`, `401 session_revoked`, `403 forbidden_role` з `permission` (перечитати `me`, екран «Доступ змінено»), mock за контрактом (`NEXT_PUBLIC_USE_MOCK=1`) з пресетами 4 ролей. Тести `permissions.test.ts` на пресети = поточна поведінка | `API-CONTRACT-ACCESS-V1` (mock) → інтеграція після `ARTIFACT-ACCESS-CORE-V1` |
| TASK-855 | frontend-developer | **Екран «Команда й доступи»** за §2.13: список, картка користувача з блоками й підпунктами, disabled-стани (стеля, модуль, директор/сам), попередження `own` без прив'язки, diff-діалог, 409, скидання до пресету, запрошення з роллю «Заступник» і кроком доступів, вкладка журналу (директор). uk-тексти в content-шарі, a11y-рев'ю (`accessible-components-review`) | TASK-854; `API-CONTRACT-ACCESS-V1` (mock) → інтеграція після TASK-851 |
| TASK-856 | qa-engineer (skill `beauty-qa`) | Повна матриця: пресети = базова матриця; deputy; кастомні комбінації (майстер з `calendar.view=all` + `manage=own`; адмін без виторгу; майстер зі «своїм виторгом» не бачить чужого й собівартості); модуль вимкнено → право не діє, після ввімкнення повертається; усі правила ескалації; самозміна; директор недоторканний; **швидкість відкликання** (наступний запит 403, вимкнення → 401); гонка 409; аудит (кожна дія, diff, відсутність ПІІ); маскування ПІІ в усіх DTO; ШІ не обходить права; RLS: крос-tenant для нових таблиць; e2e: меню перебудовується після зміни прав без перелогіну. Регресія staging smoke | TASK-851, TASK-852, TASK-853, TASK-855 |
| TASK-857 | security-reviewer (skill `beauty-security`, `admin-auth-review`) | Рев'ю реалізації TASK-849…855 за §2.5–2.10: middleware (порядок, заміна claims, fail-closed), `RequirePermission` на кожному ендпоінті (з картою §30), ескалація, IDOR у `own`, серверне маскування, ШІ-tools, аудит append-only, RLS/GRANT. Звіт у `docs/reviews/` | TASK-856 |
| TASK-858 | documentation-writer (skill `beauty-docs`) | `api.md`, `database.md`, `domain-model.md` (ролі й права), runbook (міграція, backfill, відкат, gate), примітки «уточнено ADR-023» в ADR-006 і ADR-008 (рішення на місці не редагуються), `known-issues.md` за потреби, оновлення CLAUDE.md-рядка про архітектуру (ADR-023) | TASK-857 |

**Паралельність і конфлікти файлів.**
- TASK-847 ∥ TASK-848 ∥ TASK-849 одразу після TASK-846 (847 може стартувати й раніше).
- TASK-851 ∥ TASK-852 ∥ TASK-853 після TASK-850, бо файли різні: 851 – `UserAdminService`/`AuthController`/auth store, 852 – booking/staff/overview/analytics, 853 – clients/inbox/AI/marketing/catalog/settings. Умова паралельності: TASK-850 виносить `AppointmentAccessService` з `UserAdminService.cs` і створює спільний тестовий хелпер токенів для `deputy`.
- TASK-854 стартує на mock одразу після TASK-846, паралельно з backend.
- Зведення гілок виконує `integration-steward` в `integrate/*`, мердж у base-гілку проходить через human gate.

**Human gates у цьому DAG:**
1. Прийняття ADR-023 (межа авторизації).
2. Застосування міграції з backfill на staging/prod і будь-який `Down` там.
3. Мердж `integrate/*` у base-гілку після TASK-857.

---

## 7. Відкриті питання для людини

| # | Питання | Рекомендація (дефолт, якщо не відповісти) |
|---|---|---|
| Q1 | Де в ієрархії **заступник**: нижче адміністратора (помічник адміністратора) чи вище (заступник директора)? Від цього залежить, чи може адміністратор керувати заступником | **Нижче адміністратора** (ранг 2): адміністратор запрошує заступника й керує ним. Якщо вище, адміністратор не керує ним, а керує лише директор |
| Q2 | Пресет заступника (§2.3): календар і клієнти всі, inbox відповідати, ШІ використовувати, без виторгу, налаштувань каналів/ШІ, маркетингу, керування командою | Прийняти як є, бо керівник змінює його на користувача |
| Q3 | Чи вмикати майстрам «свій виторг» **за замовчуванням** у пресеті? | **Ні** (зберігає поточну поведінку; керівник вмикає на користувача) |
| Q4 | Що таке «свій виторг»: сума яких записів і яких сум? | Ціна записів зі статусом «виконано» за період у часовому поясі закладу, без собівартості, виплат і повернень; передоплати скасованих записів не входять |
| Q5 | Чи можна прив'язати профіль майстра до адміністратора або заступника (адмін, який теж працює майстром), щоб рівні «лише свої» мали для нього сенс? | **V1: ні**, прив'язка лише для `specialist`, як зараз. V2 окремою задачею (зачіпає слоти, `ActiveSpecialistFilter`, запрошення) |
| Q6 | Політика скасування й повернень (A2, фінансова): лише директор чи делегується через `settings.general = edit`? | **Лише директор** (зарезервовано) |
| Q7 | Журнал змін доступу: хто бачить і скільки зберігати? | Бачить **директор**. Зберігати **3 роки**, очищення окремою задачею воркера |
| Q8 | «Свій виторг» без модуля `beauty_analytics`: доступний чи ні? | **Ні**: правило «модуль = верхня межа» без винятків |
| Q9 | Чи може адміністратор **знижувати** права, які директор видав вище стелі адміністратора? | **Ні**: такі ключі для адміністратора недоторканні |
| Q10 | Кілька директорів / передача ролі директора | Поза цим ADR: лише control plane (ADR-018), окрема задача за потреби |
| Q11 | Рівень «свої клієнти» (`clients.view = own`) у V1 чи пізніше? | **V1** (природний сценарій для майстра; реалізація – фільтр за записами до свого майстра) |

Відповіді вносяться правкою §30 контракту (TASK-846) і, якщо змінюється семантика, позначкою в цьому ADR до `accepted`.

---

## 8. References

- `backend/BeautyCrm.Api/Auth/AuthSetup.cs` – політики, `ToActor`
- `backend/BeautyCrm.Api/Controllers/ReminderContactsController.cs` – `ReminderRolesAttribute`
- `backend/BeautyCrm.Api/Tenancy/RequireModuleAttribute.cs`, `backend/BeautyCrm.Api/Infrastructure/ActiveSpecialistFilter.cs`
- `backend/BeautyCrm.Application/Features/BeautyAuth/AuthModels.cs` (`Roles`, `Actor`), `TokenCodec.cs` (`RolePolicy`), `UserAdminService.cs` (`AppointmentAccessService`)
- `backend/BeautyCrm.Infrastructure/Data/BeautyDbContext.cs` – `ck_users_role`, `ck_invites_role`
- `frontend/features/beauty-auth/permissions.ts`, `frontend/features/beauty-admin/components/admin-shell.tsx`
- ADR-002, ADR-005, ADR-006, ADR-008, ADR-014, ADR-018; `beauty-contracts.md` §20.9, §24.9, §25.9, §26.11.1, §27.8

---

## 9. Реалізація (TASK-849…857, зафіксовано TASK-858, 2026-11-16)

Статус лишається **Accepted**. Розділ описує фактичний код. Там, де код відрізняється від §30, зазначено «відхилення». Контракт §30 не редагується цим розділом; відхилення подаються на розгляд `project-architect`.

### 9.1 Де що лежить

- **Application:** `Features/BeautyAuth/Access/PermissionCatalog.cs` (ключі, рівні, пресети, DTO каталогу), `AccessSet.cs` (effective, модулі, обмеження, fail-closed), `AccessAdminService.cs` і `AccessAdminModels.cs` (запис доступів, §30.10, журнал), `ContactMasking.cs`, `ClientScope.cs`; `AuthModels.cs` (`Roles.Deputy`, `Actor.Access`, `Actor.ForTest`); `TokenCodec.cs` (`RolePolicy.CanManage`); `BeautyStaff/StaffModels.cs` (`StaffRoles.CanModify`); `BeautyAuth/AppointmentAccessService.cs` (винесено з `UserAdminService.cs`, TASK-850); `BeautyAnalytics/MyRevenueService.cs`.
- **Infrastructure:** `Data/Auth/EfAccessStore.cs` (знімок доступу одним запитом), `EfAccessAdminStore.cs`, `EfAuthStore.cs` (прийняття запрошення, M5), `Data/Beauty/EfBeautyStore.Staff.cs` (каскад M1), `Data/Migrations/20261115120000_auth_user_permissions.cs`, `Data/Entities/AuthEntities.cs`, `BeautyDbContext.cs`, `AI/Beauty/AiToolExecutor.cs` (`AiToolPermissions`).
- **API:** `Auth/AccessContextMiddleware.cs` (після `TenantMiddleware`), `Auth/RequirePermissionAttribute.cs`, `Auth/AuthSetup.cs` (політика `Staff` включає `deputy`), `Tenancy/RequireModuleAttribute.cs` (authorization-фільтр, Order −3100), `Tenancy/TenantActiveFilter.cs` (−3200), `Infrastructure/ActiveSpecialistFilter.cs` (глобальний, `Program.cs`), `Controllers/AuthController.cs` (`/auth/me`, `/access/catalog`, `UsersController`), `Controllers/ClientsAnalyticsController.cs` (`/beauty/me/revenue`), `Controllers/BookingController.cs` (scope клієнта M3, ціни M4), `Infrastructure/CostFieldRedaction.cs`.
- **Deploy/БД:** `deploy/staging/db/init-catalog-role.sql`, `deploy/staging/db/grants.sql`, `scripts/dev/setup-db.ps1`.
- **Frontend:** `features/beauty-auth/permissions.ts` (`canOpen`, `navFor`, `homeFor`), `session.ts` (`X-Access-Version`, перечитування `me`), `errors.ts` (`session_revoked`), `app/(dashboard)/beauty/team/page.tsx`, `features/beauty-admin/components/user-access-card.tsx`.
- **Тести:** `backend/BeautyCrm.Tests/Auth/` (`AccessAdminTests`, `AccessApiTests`, `AccessGroupATests`/`BTests`, `AccessQaGapTests`, `AuthzBaselineMatrixTests`, `PermissionCatalogTests`, `ActorAccessArchitectureTests`), `Data/UserPermissionsMigrationTests.cs`, `Data/UserPermissionsRlsTests.cs`.

### 9.2 Механізм (як реалізовано)

- Middleware читає знімок доступу одним запитом під RLS. Замінює claims `role` і `specialist_id` значеннями з БД і додає `AccessIdentity` з `IAccessContext`. Збій БД → `503 access_unavailable` (`Retry-After: 5`). Невалідний `sub`/`tenant_id` → `401 invalid_token`. Користувач неактивний → `401 session_revoked`.
- `Actor.Access` береться з `IAccessContext`. Без контексту `AccessSet.None` (fail-closed). `Actor.ForTest` — `internal static` у Application, лише для тестів.
- `RequirePermission` (Order −3000) повертає `403 forbidden_role {permission, required}`, або `403 module_disabled`, якщо право втрачено лише через вимкнений модуль.
- Заголовок `X-Access-Version` виставляється в `OnStarting` для всіх відповідей, крім `401`; CORS експонує його (`HostingSetup.cs`).

### 9.3 Відхилення від §30 (код — джерело істини)

1. **`StaffRoles.CanModify` для профілю з прив'язаним owner** повертає `false` для всіх акторів: ранг owner = 4, вищої ролі немає. Owner-профіль може змінити лише control plane. §30.1 цього явно не описує. Пропонується додати в §30.1.
2. **`Actor.ForTest` замість `TestActors.WithPreset`.** Фабрика названа й розміщена інакше, ніж у §30.5. `ActorAccessArchitectureTests` перевіряє, що `ForTest` не public і що `PermissionPresets` не є джерелом `Actor.Access`.
3. **Порядок кроків `AccessAdminService.WriteAsync`:** перевірка «ціль ≠ актор» (крок 3) виконується до блокування цілі (крок 2), щоб уникнути дедлоку з власним рядком (коментар у коді). Відповідь не змінюється.
4. **`invites.permissions DEFAULT '{}'`** лишено в схемі (§2.4 передбачав прибрати). Причина: сумісність зі старими образами API. Див. KI-007.
5. **`user_access_audit.action`** — `VARCHAR(32)`, не `text` (§2.4). Функціонально не впливає.
6. **Не перевірено цим проходом:** фільтр `GET /ai/actions` за правом tools (§30.6.10) і маскування контактів в inbox (§30.6.9). Покрито тестами TASK-856, але прямої перевірки в документації немає.

Перевірено без відхилень: коди `session_revoked`, `invalid_token`, `access_unavailable` (§30.11); `RequireModule` −3100 і порядок фільтрів §30.5; `ActiveSpecialistFilter` глобальний з винятками `me`/`access/catalog`/`feedback`; `kpi.revenue` відсутній (`decimal?`, `WhenWritingNull`); `phone`/`email` відсутні в `ClientDto` замість `null`, натомість `phoneMasked`/`emailMasked`; ціни в списках за `revenue.view = all` або власний майстер (§30.5 M4); `ReminderRoles`, `InboxManagementOnly`, `AuthPolicies.Management` більше не використовуються в `Controllers`; журнал доступу тільки для owner.

### 9.4 Примітки до старих ADR

ADR-006 і ADR-008 не редагуються по суті. Їхня ієрархія ролей уточнена цим ADR (§5).
