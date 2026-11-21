# Known Issues

**Owner:** qa-engineer (підтримує структуру файлу) — **записує будь-який агент**, що знайшов чи виправив проблему
**Updated:** 2026-11-21

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

Resolution: планується — перевірка `sessions_valid_from`/версії сесії в `AccessContextMiddleware` (окрема задача, потребує рішення щодо TTL і навантаження на БД).

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

### KI-019: Guard службових повідомлень пропускає неканонічну форму команди
Severity: medium
Status: open (виправлення в роботі)
Discovered in: TASK-897 (QA-1), TASK-898
Resolved in: —
Area: backend, security

Description: `IsServiceShape` порівнює сирий текст: `" /start inv_…"`, `"\n/start inv_…"`, `/START`, `/start@Bot` потрапляють у клієнтський потік, і живий токен прив'язки зберігається в `beauty_messages`.

Root cause (з звіту): класифікація службового повідомлення без нормалізації форми команди.
Правило (до виправлення): перед класифікацією нормалізувати текст (Trim, регістронезалежно, зняти `@botname`); тест на кожну форму; токен, що потрапив у клієнтський потік, — дефект критичності high.

### KI-020: Запобіжник прод-вмикання не розрізняє staging і прод
Severity: medium
Status: open (M1, виправлення в роботі)
Discovered in: TASK-898
Resolved in: —
Area: backend, worker, config, ops

Description: `TelegramL3Approved` діє лише поза `Development`; staging не `Development`, тож тестова прив'язка вимагала б `L3Approved=true`, тобто ключ юридичного gate мав би двозначний сенс. Воркер запобіжника не має взагалі.
Правило (до виправлення): юридичний запобіжник розрізняє staging і прод явно (окремий ключ + allowlist tenant), не за `ASPNETCORE_ENVIRONMENT`; worker перевіряє його на старті так само, як backend. Кожен запобіжник тестувати на обох середовищах.

### KI-021: Два незалежні прапори — аварійне вимкнення вимикало лише backend
Severity: medium
Status: open (M2, виправлення в роботі)
Discovered in: TASK-898
Resolved in: —
Area: ops, backend, worker

Description: `/status` казав «вимкнено», а алерти воркера йшли: `TelegramOwnerAlertsEnabled=false` не зупиняв `WORKER_INVENTORY_TELEGRAM_ENABLED=true`.
Правило: аварійне вимкнення відправки = обидва прапори (runbook §2d); прапор, що керує одним процесом, не є вимикачем; статус, що описує стан системи, має відповідати фактичній поведінці (нейтральний текст, якщо не можна гарантувати).

### KI-022: Замовлення товарів — немає тесту лімітів створення за годину (заклад / телефон)
Severity: medium
Status: open (прогалина тестів)
Discovered in: TASK-902 (реалізація етапу 3a)
Resolved in: —
Area: backend, tests

Description: `MaxCreatesPerLocationPerHour` (60) і `MaxCreatesPerPhonePerHour` (5) покриті неповно: немає тесту межі «рівно ліміт / ліміт + 1» з часом у межах години і без реального годинника. Ліміти активних неоплачених на телефон (`MaxPendingPerPhone`) — окремий тест потрібен. Поки — ризик регресії лічильника (зокрема `0` = вимкнено для закладу).

### KI-023: Замовлення товарів — каталог не перевірено на truncated > 500
Severity: low
Status: open (прогалина тестів)
Discovered in: TASK-902
Resolved in: —
Area: backend, tests

Description: P1 має повертати перші 500 товарів і `truncated: true`, коли товарів більше. Тест із більш ніж 500 товарами у tenant не написано; сортування при обрізанні (categoryPath null в кінці, name, id) теж не перевірене на межі.

### KI-024: Замовлення товарів — межа північі `Europe/Kyiv` не покрита тестом
Severity: medium
Status: open (прогалина тестів)
Discovered in: TASK-902
Resolved in: —
Area: backend, tests

Description: FEFO «без прострочених» рахується за `localToday` закладу. Тести на межі локальної півночі (23:59 / 00:01 за Europe/Kyiv, включно з переходом на літній час) відсутні; партія з `expires_on` = сьогодні має лишатися доступною, з вчорашньою — ні. Ризик — розбіжність P1 (наявність) і оплати (FEFO) на межі доби.

### KI-025: Замовлення товарів — немає тестів паралельних P4+P5 і P4+воркер
Severity: medium
Status: open (прогалина тестів)
Discovered in: TASK-902, TASK-904
Resolved in: —
Area: backend, worker, tests

Description: §33.19 п.4 вимагає: P4 і P5 паралельно → рівно один виграє; P4 і воркер на межі TTL → узгоджений стан. У наявних тестах цих гонок на реальній БД немає. Потрібні тести з двома з'єднаннями і контрольованим часом; до їх появи коректність гонок спирається лише на умовні `UPDATE`.

### KI-026: Замовлення товарів — відсутній randomized-інваріант для скасування
Severity: low
Status: open (прогалина тестів)
Discovered in: TASK-903
Resolved in: —
Area: backend, tests

Description: §33.19 п.7 вимагає randomized-тест етапу 1, розширений на P2/P4/cancel, з перевіркою інваріантів §33.16 (1)–(6) після кожного кроку. Є детерміновані сценарії; випадкової послідовності (cancel неоплаченого, cancel оплаченого з `sale_return`, reconcile, паралельні дії адміна) ще немає.

### KI-027: Флейки інтеграційних тестів під навантаженням (SpecialistReviewsMigrationTests, ProductOrdersApiTests.client_abort)
Severity: low
Status: open (флейк, не відтворено на чистому прогоні в цій сесії)
Discovered in: прогін тестів ADR-027 (2026-11-21)
Resolved in: —
Area: backend, tests

Description: під паралельним навантаженням прогону (багато тестових збірок і PostgreSQL в одному середовищі) тести `SpecialistReviewsMigrationTests` і `ProductOrdersApiTests.client_abort…` падають інтермітентно. Причина не встановлена. Ймовірний напрямок: таймаути або гонки на спільній тестовій БД. Перевірити ізоляцію БД на клас тестів і сумарний паралелізм `dotnet test` перед тим, як вважати причину встановленою. Не маскувати повтором без розбору.

### KI-028: Mobile — 5 тестів P2 idempotency падають (наявні до змін ADR-027)
Severity: medium
Status: open
Discovered in: прогін mobile-тестів (2026-11-21), ці тести падали до змін ADR-027
Resolved in: —
Area: mobile, tests

Description: 5 тестів mobile (P2, ідемпотентність запиту замовлення) падають. Вони падали до гілки ADR-027, тобто не регресія від зображень товарів. Назви тестів — у звіті прогону mobile-тестів (у цьому документі не відтворено). Потрібен окремий розбір: або тести застаріли відносно контракту §33.10/§33.19, або реальна поведінка клієнта не відповідає контракту. Не змінювати тести, щоб вони пройшли, без розбору.

### KI-029: `database.md` не описує таблиці складу й замовлень (ADR-025, ADR-026)
Severity: low
Status: open (прогалина документації)
Discovered in: TASK-917 (ADR-027 docs)
Resolved in: —
Area: docs, database

Description: `.claude/docs/database.md` описує таблиці до ADR-022 і лише розділи «Склад, етап 2a/2b» і «роздрібна ціна / категорії». Таблиць `beauty_products`, `beauty_product_orders`, `beauty_product_order_lines` (ADR-025/026) у розділах таблиць немає. Таблиця `beauty_product_images` (ADR-027) описана в розділі 8. Повний опис складу й замовлень — окремою задачею documentation-writer зі звіряння з міграціями.

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

### KI-015: SAVEPOINT узято після читань кроку; ChangeTracker.Clear у catch відкидав незбережене
Severity: high
Status: resolved
Discovered in: TASK-885 (review), TASK-893 (security-review, I2/I5)
Resolved in: TASK-885 (SAVEPOINT до читань), інваріант зафіксовано коментарем у `EfBeautyStore.InventoryConsumption.cs`
Area: backend, database

Description: якщо SAVEPOINT береться після першого читання кроку, помилка читання переводить транзакцію в aborted, і відкат лише кроку неможливий. Окремо: `ChangeTracker.Clear()` у catch відкинув би незбережені tracked-зміни, якщо складовий крок колись використає `SaveChangesAsync`.

Root cause: SAVEPOINT сприймався як «обгортка запису», а не як межа відкату для всіх запитів кроку. Інваріант «крок пише лише raw SQL / `ExecuteUpdate`» не був записаний.

Resolution: SAVEPOINT береться першою дією кроку (до будь-якого читання); крок не викликає `SaveChangesAsync`; коментар над кодом фіксує інваріант.

Prevention: у будь-якому кроці з `SAVEPOINT` перший SQL після нього — або сам крок. Крок, що відкочується через SAVEPOINT, пише лише raw SQL / `ExecuteUpdate`; якщо з'являється `SaveChangesAsync` — `ChangeTracker.Clear()` у catch заборонено (замість нього `ROLLBACK TO SAVEPOINT` і `Entry.State` reset). Тест: ін'єкція помилки в першому читанні кроку → візит `completed`, рухів немає.

### KI-016: Складовий крок списання без `lock_timeout` тримав рядок візиту до 30 с
Severity: medium
Status: resolved
Discovered in: TASK-885 (security-review M1, TASK-893)
Resolved in: TASK-893 (`SET LOCAL lock_timeout` після SAVEPOINT; перевірити тестом перед закриттям)
Area: backend, database

Description: якщо рівень складу заблоковано іншою транзакцією, завершення візиту чекало до `CommandTimeout` (30 с), тримаючи рядок візиту. При обриві HTTP-запиту `OperationCanceledException` не потрапляла в `catch` і відкочувала весь візит, включно зі статусом `completed`, що суперечить «візит завершується завжди».

Root cause: `CommandTimeout` і `CancellationToken` були єдиним обмеженням очікування блокування; SAVEPOINT не обмежував очікування, а лише відкочував помилку.

Resolution: `SET LOCAL lock_timeout` одразу після SAVEPOINT (`55P03` відкочує SAVEPOINT); рішення щодо `CancellationToken` для складового кроку й коміту — у TASK-893.

Prevention: у кожній транзакції, що має «не блокувати основну дію» (складський крок, сповіщення), ставити `lock_timeout` короткий і в межах SAVEPOINT. Тест: блокування рівня іншою транзакцією → візит `completed` за обмежений час, лог `inventory_consumption_failed sqlstate=55P03`.

### KI-017: Модульний гейт автосписання покладався лише на RLS
Severity: low
Status: resolved
Discovered in: TASK-885 (security-review L2)
Resolved in: TASK-893 (явний фільтр `tenant_id`, `lower(module)`, `InventoryModuleGate`)
Area: backend, security

Description: перевірка модуля в складовому кроці не фільтрувала `tenant_id` явно і порівнювала регістр по-різному з API (`OrdinalIgnoreCase`) і воркером (`lower(m)`). Коректно лише завдяки RLS.

Root cause: гейт писався як «запит, що RLS і так обмежить», без уніфікації з іншими шляхами.

Resolution: окремий `InventoryModuleGate` з явним `tenant_id` і регістронезалежним порівнянням; API, воркер і склад використовують його.

Prevention: кожен гейт (модуль, прапор, право) має явний фільтр tenant і одну реалізацію порівняння. Не покладатися на RLS як єдиний захист у коді; RLS — другий шар (ADR-002). Тест: модуль увімкнено в іншому tenant → автосписання не відбувається.

### KI-018: Нерозпізнане значення вимикача `Inventory:AutoConsumptionEnabled` лишало автосписання увімкненим
Severity: low
Status: resolved
Discovered in: TASK-885 (security-review L1)
Resolved in: TASK-893 (будь-яке присутнє значення, крім `true`, вимикає)
Area: backend, config, ops

Description: значення `0`, `off`, `no` при аварійному вимкненні не вимикали автосписання, бо парсинг `bool.TryParse` при невдачі повертав «увімкнено».

Root cause: для аварійного вимикача використано правило «невалідне = за замовчуванням», а не «невалідне = безпечний стан». Для вимикача безпечний стан — вимкнено.

Resolution: вимкнено, якщо ключ присутній і його значення не `true` (без урахування регістру); відсутній ключ — увімкнено за замовчуванням.

Prevention: аварійний вимикач (аварійне вимкнення) вважає вимкненим будь-яке присутнє значення, крім явного `true`; відсутній ключ дає дефолт коду. Документувати в runbook (§2c). Тест: `0`, `off`, `no`, порожнє → вимкнено.

### KI-030: Очищувач файлів фото видаляв живі файли, якщо `t/` відсутній або неповний
Severity: high
Status: resolved
Discovered in: TASK-916 (security-review M2, ADR-027)
Resolved in: 56c90ea (ADR-027: виправлення security-рев'ю)
Area: backend, infra, security

Description: `MediaOrphanSweeper` будував «живий» набір лише з tenant-ів, для яких є теки `t/*`. Варіанти без маркера, старші за 24 год, вважались сиротами. Після часткового відновлення тома з бекапу, або ручного прибирання `t/`, усі варіанти tenant-а ставали «страй» і видалялись безповоротно.

Root cause: очищувач сприймав відсутність маркерів як доказ, що файли нікому не належать. Перевірки стану каталогу маркерів не було, а grace 24 год був занадто коротким для ручних дій з томом.

Resolution: страй-варіанти не видаляються, якщо `t/` відсутня, маркерів немає при наявних варіантах, або страй > 50% валідних (за > 20). Grace страй-варіантів — 7 днів (`StrayGrace`). Лог `media_sweep_aborted reason=…` (Error). Тести: відсутній `t/`, порожній live-набір.

Prevention: для будь-якого очищувача, що видаляє файли за відсутністю посилання, порожній або відсутній джерело істини (каталог маркерів, таблиця) означає «нічого не видаляти», а не «все сирота». Перед переходом у `delete` — режим `dry-run` з перевіркою логів (runbook §2f). Grace для ручних дій з томом не коротший за тиждень.

### KI-031: Upload зображень без глобального ліміту міг вичерпати пам'ять API (OOM-kill спільного процесу)
Severity: high
Status: resolved
Discovered in: TASK-916 (security-review M1, ADR-027)
Resolved in: 56c90ea (ADR-027: виправлення security-рев'ю)
Area: backend, infra, security

Description: тіло upload буферизувалось у `MemoryStream` до семафора обробки. Ліміт `AllocationLimitMegabytes = 256` обмежував одну алокацію, а не суму. З 20 паралельних запитів на користувача і кількома користувачами найгірше декодування (16-бітний PNG 25 Мп плюс копія від `AutoOrient`) могло дати ~400 МБ при `mem_limit` 768m. Наслідок: OOM-kill всього API, а не виняток.

Root cause: ліміт пам'яті ImageSharp прочитано як ліміт процесу. Перевірки глобальної паралельності на вході не було.

Resolution: неблокувальний семафор `Media:MaxConcurrentUploads` (3) до читання тіла, відмова `503 media_busy` + `Retry-After: 5`. Формат піксела `Rgba32`, пул 64 МБ, старт перевіряє `MaxPixels × 8 × MaxConcurrentProcessing ≤ 256 МБ`, таймаут обробки 20 с.

Prevention: ліміти пам'яті для декодерів задавати і читати як ліміт однієї операції. Для процесу потрібен окремий глобальний ліміт паралельності на вході (до буферизації тіла) і перевірка бюджету при старті. Тест на бюджет: `MediaSetupLimitsTests`. Вхідні ліміти розміру тіла в nginx, Kestrel і читачі мають узгоджуватися (ADR-027 §2.5 п.1).
