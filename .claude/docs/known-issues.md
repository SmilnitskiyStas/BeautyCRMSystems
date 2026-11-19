# Known Issues

**Owner:** qa-engineer (підтримує структуру файлу) — **записує будь-який агент**, що знайшов чи виправив проблему
**Updated:** 2026-11-18 (TASK-869: ADR-024)

Два різні розділи нижче — не плутати:
- **Active Issues** — те, що зараз зламано й ще не виправлено (звичайний баг-трекер).
- **Resolved Issues** — те, що вже виправлено, **з обов'язковими `Root cause` і `Prevention`**. Це журнал «не наступати на ті самі граблі», який читає кожен агент перед стартом задачі (retrieval за area).

## Format

```
### KI-NNN: [Title]
Severity: critical / high / medium / low
Status: open / accepted-risk / resolved
Discovered in: TASK-XXX
Resolved in: TASK-YYY (якщо resolved)
Area: frontend / backend / database / security / infra / ...

Description: [симптом — що спостерігалось, за яких умов відтворюється]
Root cause: [лише для resolved]
Resolution: [що конкретно змінено]
Prevention: [лише для resolved — конкретне, дієве правило]
```

## Active Issues

### KI-001: Access-токен 15 хв не покриває зміну пароля і «вийти з усіх пристроїв»
Severity: low
Status: open
Discovered in: TASK-857 (security-review, minor 13)
Area: security, backend

Description: після зміни пароля або виходу з усіх пристроїв уже видані access-токени діють до закінчення TTL (15 хв). Права й активність перевіряються з БД на кожен запит (ADR-023 §2.5), тож вимкнення користувача і зміна прав діють одразу (`401 session_revoked` / `403`). Недостатньо покрито саме сценарій пароля й logout-all: їхній відкат не інвалідує access-токен миттєво.

Resolution: частково закрито ADR-024 E-5 (TASK-863/865): `users.sessions_valid_after` перевіряє `AccessContextMiddleware` (той самий запит, без другого SELECT); скидання пароля, звільнення пошти й злиття миттєво гасять access-токени (`401 session_revoked`). Лишається «вийти з усіх пристроїв» (logout-all): ендпоінта ще немає, окрема задача.

### KI-002: ChatFlowLlmTests — нестабільний тест (передує ADR-023)
Severity: low
Status: open
Discovered in: до TASK-848 (pre-existing; зафіксовано при TASK-858)
Area: backend, tests

Description: `backend/BeautyCrm.Tests/Conversations/ChatFlowLlmTests.cs` періодично падає. Не пов'язаний зі змінами прав ADR-023. Причину не розібрано.

Resolution: окремий root-cause аналіз (`root-cause-analysis`) і виправлення або ізоляція тесту. До того не вважати його сигналом регресії.

### KI-003: ChatLiveSmokeFixesTests — нестабільні тести
Severity: low
Status: open
Discovered in: до TASK-858
Area: backend, tests

Description: `backend/BeautyCrm.Tests/Conversations/ChatLiveSmokeFixesTests.cs` (PostgreSQL-тести потоку чату) нестабільні. Не пов'язані з ADR-023.

Resolution: окремий аналіз; до виправлення повторювати прогін перед висновком про регресію.

### KI-004: F3 — розширення scope `own` через не скасований запис (accept-risk)
Severity: medium
Status: accepted-risk
Discovered in: TASK-848/857 (security-review, F3)
Area: security, backend

Description: майстер з `clients.view = own` може створити запис для клієнта поза своїм scope, якщо збіг за телефоном знайдено; відповідь не розкриває збережені дані клієнта (M3), але створений запис, який не скасовано, розширює його scope на цього клієнта. Скасований запис, створений самим актором, scope не розширює.

Accept-risk: прийнято власником продукту. Контроль: структурований лог `client_scope_expanded {actorUserId, clientId, appointmentId}` і метрика `beauty.access.client_scope_expanded` (без ПІІ). Переглядати метрику при релізі; при зростанні — повернутися до питання.

### KI-005: Frontend-тести лише з `--maxWorkers=2`
Severity: low
Status: open
Discovered in: TASK-800 (ADR-016)
Area: frontend, infra

Description: при паралельному запуску vitest з jsdom тести нестабільні під навантаженням. CI запускає `npm test -- --maxWorkers=2`.

Resolution: лишати `--maxWorkers=2` у CI і в локальних командах; дослідити джерело нестабільності jsdom окремо.

### KI-006: Немає retention-job воркера для `purge_user_access_audit`
Severity: medium
Status: open
Discovered in: TASK-849 (ADR-023 Q7)
Area: backend, worker, database

Description: журнал `user_access_audit` має зберігатися 3 роки і очищуватися через `purge_user_access_audit(p_before)`. Функція створена і доступна лише `beautycrm_worker`, але воркер її не викликає. Журнал росте без обмеження.

Resolution: окрема задача воркера (планове очищення з `p_before = now() - 3 years`, метрика видалених рядків). Потребує окремого TASK.

### KI-007: Залишок `DEFAULT '{}'` на `invites.permissions`
Severity: low
Status: open
Discovered in: TASK-849 / TASK-858
Area: database

Description: контракт §2.4 передбачав прибрати `DEFAULT '{}'` після TASK-851. Міграція `20261115120000` його залишає, бо старі образи API створюють запрошення без поля `permissions`. Поточний код завжди передає повний знімок, тож default зайвий.

Resolution: окрема міграція `DROP DEFAULT` після виведення старих образів API з обігу. Не змінювати без human gate на staging/prod.

### KI-008: Пошук складу (ILIKE) залежить від локалі БД
Severity: low
Status: open
Discovered in: TASK-880 (security-review складу)
Area: backend, database

Description: пошук товарів і залишків (`search`) виконується через `EF.Functions.ILike` з екрануванням `%_\`. Для кирилиці коректний результат потребує UTF-8-локалі БД. Prod і Docker-postgres відповідають; інша локаль може давати хибні збіги або пропуски.

Resolution: планується перевірка локалі в bootstrap або перехід на нормалізований пошук (окрема задача). До того не вважати пошук надійним у нестандартних середовищах.

### KI-009: Сторінка залишків читає всі відфільтровані товари в пам'ять
Severity: medium
Status: open
Discovered in: TASK-880 (security-review складу)
Area: backend, database, performance

Description: `GET /stock` читає легкі ключі (id, назва) усіх товарів, що пройшли фільтри, а важкі агрегати — лише для сторінки. Для каталогу в десятки тисяч товарів це дорого.

Resolution: keyset-пагінація цілком у SQL з `collate "C"` для сортування за назвою (окремий крок, після заміру плану запиту через `create-indexes`).

### KI-010: Порядок рухів у відповіді операції не відповідає FEFO
Severity: low
Status: open
Discovered in: TASK-881 (звірка документації з кодом)
Area: backend, api

Description: `GET /operations/{id}` повертає рухи, відсортовані за `occurred_at, id`. Рухи однієї операції мають однаковий час, тому порядок їх списання з партій за FEFO у відповіді не відображається.

Resolution: окрема задача: або зберігати порядок списання (наприклад, колонка порядку рядка), або явно задокументувати в §32.6 як не FEFO. Клієнти не повинні покладатися на порядок.


### KI-015: Приклади env не містять обов'язкових змінних ADR-024 (API не стартує на staging)
Severity: high
Status: open
Discovered in: TASK-869 (перевірка env після TASK-863/865)
Area: infra, backend

Description: `deploy/staging/.env.api.example` і корневий `.env.example` не містять `Auth__LoginAttemptKeyPepper` (≥ 32 байти) і `Auth__LegacyTenantLogin__DeprecatedAt` / `__SunsetAt`. Поза Development `AuthSetup` кидає виняток на старті. Скопійований приклад дає падіння API.
Resolution: додати змінні з коментарями в обидва приклади (людина задає значення на сервері); до того — runbook §2b.4 як попередження. Код не змінювався.

### KI-016: Нормалізація пошти в preflight (SQL) і в застосунку (C#) може розходитися
Severity: medium
Status: open
Discovered in: TASK-861
Area: database, backend

Description: preflight і міграція нормалізують `lower(btrim(email))` (SQL), застосунок — `Trim().ToLowerInvariant()`. Для Unicode-регістру й не-пробільних whitespace результати можуть різнитися. Частина таких адрес у звіті preflight (розділ 4) видна лише як аномалія. Пошта, яку застосунок не знайде через SQL-нормалізацію (або навпаки), може дати дубль або хибне `email_in_use`.
Resolution: до застосування міграції на staging переглянути розділ 4 звіту preflight; аномалії виправити вручну. Вирівнювання правил нормалізації — окреме рішення (ADR-024: без Unicode-нормалізації).

### KI-017: Лист «Пароль змінено» без часу зміни
Severity: low
Status: accepted-risk
Discovered in: TASK-872
Area: worker, database

Description: контракт §31.7.3 вимагає час зміни (UTC) у листі. `auth_password_reset_claim_delivery` не повертає timestamp, тому лист без часу. Бюджет повторів (24 год) рахується в пам'яті процесу воркера.
Accepted-risk: плейсхолдер погоджується людиною. Зміна потребує claim-функції з timestamp і міграції (окрема задача).

### KI-018: Воркер без метрик; бюджети повторів і троттли процес-локальні
Severity: low
Status: accepted-risk
Discovered in: TASK-865, TASK-872
Area: worker, infra, backend

Description: у воркері немає метрик (лише логи без пошти й токенів). Backoff і 24-годинний бюджет листів зберігаються в пам'яті; після рестарту відлік починається знову. У API `LoginEmailThrottle`, ліміти `password/forgot`, `InviteEmailCheckThrottle` — in-memory на інстанс (кожен інстанс рахує окремо).
Accepted-risk: однаково з `OwnerInviteThrottle`; один інстанс API і воркера на staging. Перед масштабуванням — спільний лічильник (окрема задача). Квоти ідентичностей і блокування в БД і від рестарту не залежать.

### KI-019: Алерти ADR-024 ще не налаштовані
Severity: medium
Status: open
Discovered in: TASK-864, TASK-860 (§31.9, §31.12)
Area: infra

Description: передбачено алерти (`identity_created{via="staff_invite"}` > 10 / добу на tenant; сплеск `invite_email_in_use`; частка `invalid_credentials`). Метрики в API є, алертів у devops немає.
Resolution: devops налаштовує алерти (окрема задача). До того — перегляд метрик вручну при релізі.

### KI-020: Вимога релізу F-1 (IP клієнта за BFF) ще не перевірена на сервері
Severity: high
Status: open
Discovered in: TASK-871 (ADR-024 E-14)
Area: infra, security

Description: без правильного `Beauty__TrustedProxies` + статичної адреси `web` (`BEAUTY_WEB_IP`) + nginx `X-Forwarded-For $remote_addr` ліміт `auth` (10/хв) спільний для всіх користувачів, тобто будь-хто без автентифікації блокує вхід усім. Код і конфіг у репозиторії готові; на сервері нічого не застосовано, smoke §6.6 не виконано.
Resolution: людина застосовує конфіг, виконує smoke (`deploy/staging/README.md` §6.6), результат — у `.claude/logs/tasks/TASK-871.md`. Реліз TASK-866 і фази 2 заблоковано до цього.


## Resolved Issues

### KI-011: Неявне перетворення null у Result<T> давало 201 з порожнім тілом
Severity: high
Status: resolved
Discovered in: TASK-877 (review)
Resolved in: TASK-877
Area: backend, api

Description: `MapStocktake` повертав `Result` з `null` у `Value`. Неявний оператор `T → Result<T>` на `null` створював успішний результат, тому конфлікт інвентаризації віддавав `201` з порожнім тілом замість `409`.

Root cause: неявне перетворення з `T` вважало `null` успіхом; тип результату не розрізняв «успіх без значення» й «значення відсутнє».

Resolution: `MapStocktake` більше не повертає успішний `Result` з `null`; конфлікт `stocktake_in_progress` (409) перевіряє `InventoryApiTests`.

Prevention: API-тест на конфлікт перевіряє і статус, і тіло відповіді (`Fails(..., HttpStatusCode.Conflict, "<code>")`). Нові `Result<T>` для сутностей, що можуть бути відсутні, будують через явні `Ok`/`Error`, а не через неявне перетворення від `null`.

### KI-012: Колізія імені DTO `NamedRef` ламала генерацію Swagger
Severity: medium
Status: resolved
Discovered in: TASK-877 (review)
Resolved in: TASK-877
Area: backend, api

Description: DTO `NamedRef` у модулі складу збігався за назвою зі схемою іншого типу, і Swagger не міг згенерувати документ.

Root cause: схеми Swagger ідентифікуються за коротким іменем типу; два різні типи з однаковою назвою в різних просторах імен конфліктували.

Resolution: тип перейменовано на `InventoryNamedRef`.

Prevention: DTO модуля мають префікс модуля (`Inventory*`). Перед додаванням DTO з загальною назвою перевіряти наявні схеми Swagger.

### KI-013: Нестабільний тест залежав від порядку рухів з однаковим часом
Severity: low
Status: resolved
Discovered in: TASK-880 (security-review складу)
Resolved in: TASK-880
Area: backend, tests

Description: `Stocktake_surplus_shortage_zero_and_closed_rules` періодично падав. Усі рухи однієї операції мають однаковий `occurred_at` (час береться з `TimeProvider`), тому порядок їх у вибірці визначався випадковим `id`.

Root cause: тест перевіряв рухи за позицією в списку, а порядок при однаковому часі не детермінований.

Resolution: перевірка йде за складом залишків (сума за заклад і товар), а не за позицією рядка.

Prevention: тести не покладаються на порядок рухів з однаковим `occurred_at`. Для перевірки порядку — різний час у `FakeTimeProvider` або порівняння за агрегатом.

### KI-014: BOM у `schema.sql` ламав завантаження в PostgreSQL
Severity: high
Status: resolved
Discovered in: TASK-876 (database)
Resolved in: TASK-877 (перегенеровано `worker/src/testing/schema.sql`)
Area: database, worker, infra

Description: після перегенерації файл `schema.sql` починався з BOM, і завантаження скрипта в PostgreSQL падало на першому оператора.

Root cause: генерація схеми записала BOM на початку файлу; контракт §32.3 вимагає UTF-8 без BOM і LF.

Resolution: файл перегенеровано без BOM, LF; `schema-drift.test.ts` зелений.

Prevention: після генерації схеми перевіряти, що файл починається з `CREATE`/`--`, а не з BOM; генерувати без BOM і LF (правило §32.3).


### KI-R1 (з TASK-862): тестові сиди ламаються від `users.identity_id NOT NULL`
Severity: medium
Status: resolved
Discovered in: TASK-862
Resolved in: TASK-863
Area: backend, tests, database

Description: після `users.identity_id NOT NULL` кожен INSERT у `users` без ідентичності падав (сотні тестів, worker-інтеграційні тести).
Root cause: зміна інваріанту БД без оновлення всіх місць вставки членств (~25 сидів у тестах, `EfAuthStore`, `EfControlPlaneStore`).
Resolution: спільний хелпер `backend/BeautyCrm.Tests/Data/IdentitySeed.cs` (UserSql/InsertUserAsync/EnsureAsync/NewUserAsync) і `worker/src/testing/seed-user.ts`; прод-код створює ідентичність у тій самій транзакції.
Prevention: будь-який INSERT у `users` (тести, сиди, воркер) іде лише через хелпер, що створює `identities`. Прямий `INSERT INTO users` без `identity_id` у код не додавати.

### KI-R2 (з TASK-864): квота й дубль пошти мапилися на `invite_invalid` / `user_exists`
Severity: medium
Status: resolved
Discovered in: TASK-864 (самоперевірка до мерджу)
Resolved in: TASK-864
Area: backend

Description: `429`-квота нових ідентичностей і `unique_violation` при прийнятті запрошення повертали `invite_invalid` або `user_exists`, а не `429 rate_limited` / `409 email_in_use`.
Root cause: загальний обробник винятків прийняття запрошення не розрізняв `SQLSTATE` `P0001` (квота) і `23505` (унікальність).
Resolution: явне мапування: `unique_violation` → `email_in_use`, `P0001 identity_quota` → `429 rate_limited` + `Retry-After`; тести на кожен код.
Prevention: винятки БД мапити за `SQLSTATE` у одному місці з тестом на кожен код. Загальний код помилки (`invite_invalid`) для збоїв БД не використовувати.

### KI-R3 (з TASK-864): лічильник SQL-команд не бачить інтерсептор із DI
Severity: low
Status: resolved
Discovered in: TASK-864
Resolved in: TASK-864
Area: backend, tests

Description: тест `AccessContextQueryCountTests` не фіксував команди: EF 8 не підхоплює `IInterceptor` з DI.
Root cause: інтерсептори EF 8 реєструються через `DbContextOptions`, а не через DI-сервіс.
Resolution: тест перереєстровує `BeautyDbContext` з інтерсептором.
Prevention: тести лічильника запитів перереєстровують контекст з інтерсептором явно; не покладатися на DI-інтерсептор.

### KI-R4 (з TASK-863): `switch` без refresh повертав 422 замість 401
Severity: low
Status: resolved
Discovered in: TASK-863 / TASK-864
Resolved in: TASK-864
Area: backend, api

Description: `POST /auth/switch` без `refreshToken` відповідав `422 validation_failed`.
Root cause: `[Required]` на полі секрету сесії; контракт §31.15 п.10 вимагає `401 invalid_token`.
Resolution: прибрано `[Required]` з `SwitchRequest.RefreshToken`; `422` лишився для відсутнього `tenantId` і `same_tenant`.
Prevention: відсутність секрету сесії = `401`, а не `422`. `[Required]` лише для несекретних полів.
