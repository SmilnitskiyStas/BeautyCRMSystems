# ADR-017: Індивідуальна ціна й тривалість послуги для майстра

**Date:** 2026-11-04

**Status:** proposed. Рішення продукту (§1.2) затвердив користувач. Перед `accepted` потрібно, щоб людина закрила відкриті питання §8 (Q1–Q3 впливають на контракт). Міграція адитивна, але її `Down` знищує дані: відкат на staging/prod проходить через human gate (`.claude/workflow/human-gates.md`).

**Author:** project-architect

**Deciders:** продуктові рішення A–F (§1.2) затвердив користувач 2026-11-04. Позначка *(можна змінити)* — припущення архітектора: їх можна змінити правкою контракту без нового ADR.

**Уточнює:**
- ADR-008 / контракт §13: `beauty_specialist_services` отримує атрибути зв'язку (ціна, тривалість). Правило «майстер пропонує лише призначені послуги» лишається чинним.
- ADR-011 / §21.6: підсумок і пропозиція чату беруть ціну й тривалість конкретного майстра. З'являється код `duration_changed`.
- ADR-014 / §24: виплата майстру й надалі рахується як % від `price_final`. Собівартість лишається на рівні послуги/закладу.
- ADR-015 / §26: купони й акції рахуються від ціни майстра.

Решта рішень цих ADR чинна. Задачі: TASK-804…814 (`.claude/tasks/current.md`). Контракт: новий розділ `beauty-contracts.md` §27 (TASK-805).

---

## 1. Контекст

### 1.1 Стан коду (2026-11-04, перевірено читанням)
- **Ціна.** `beauty_service_prices(service_id, location_id NULL = мережа, price numeric(12,2) ≥ 0)`, UNIQUE `(tenant_id, service_id, location_id)` NULLS NOT DISTINCT. Ефективна ціна визначається в трьох місцях окремими копіями логіки «заклад, інакше мережа»:
  - `EfBeautyStore.GetPriceAsync`: його викликають `BookingService.CreateAsync` і `CouponService.CheckAsync`;
  - `EfPublicBookingStore.ListServicesAsync`: публічний каталог, `ChatBookingService.PrepareAsync`, порт чату `ClientChatPortAdapter`;
  - `EfBeautyStore.Catalog` (мережева ціна в `ServiceDto`, `get_prices` менеджерського ШІ через `AiPortAdapters`).
- **Тривалість.** `beauty_services.duration_minutes` (CHECK > 0, валідація API 5..720). Її беруть:
  - `BookingService.GetSlotsAsync`: одна тривалість для **всіх** майстрів;
  - `BookingService.CreateAsync` (коментар «duration_minutes завжди з послуги»);
  - `ChatBookingService.PrepareAsync`;
  - `OverviewService` (лічильник вільних слотів через `GetActiveServiceDurationsAsync`);
  - публічний і чатовий каталог.
- **Зв'язок майстер–послуга.** `beauty_specialist_services(tenant_id, specialist_id, service_id)`: PK з трьох колонок, композитні FK на `beauty_specialists` і `beauty_services` (CASCADE), RLS ENABLE+FORCE `tenant_isolation`. `ReplaceServicesAsync` видаляє лише зняті зв'язки, а наявні рядки зберігає. `SpecialistSchedule.Offers(serviceId)` перевіряє призначення. `BlockReasonAsync` повторно перевіряє його під advisory-lock майстра.
- **Знімок на записі вже є.** `beauty_appointments.duration_minutes`, `price_original`, `price_final`, `promotion_id`. `ends_at` рахує тригер із `duration_minutes`. Перенос (`PatchAsync`) використовує `appt.DurationMinutes`, тобто знімок, і майстра не змінює. Зайнятість (`GetBusyAsync`) береться з `starts_at/ends_at` записів. Нагадування (`ChatTemplates`), аналітика (§19/§24) і знімок собівартості читають лише поля запису.
- **Собівартість/виплата (ADR-014).** `beauty_service_costs(service, location)`. `master_payout_snapshot = round(price_final × master_percent / 100, 2)` при переході в `completed`.
- **«Будь-який майстер».**
  - Публічний `/slots` без `specialistId` повертає слоти всіх майстрів (`{specialistId, startsAt, endsAt, …}`), відсортовані за `startsAt`, потім за `specialistId`.
  - Frontend (`toSlots`) дедуплікує за часом і бере першого майстра (фактично за порядком GUID).
  - `POST /appointments` вимагає `specialistId`.
  - Публічний create **не має** `expectedPriceFinal` (він є лише в чаті, `ExpectedPriceFinal` → 409 `price_changed`).
- **Права.** `StaffService.SetServicesAsync`: `IsManager` (owner/admin) + `Guard` → `StaffRoles.CanModify` (admin не змінює профілі, прив'язані до owner/admin, 403 `forbidden_role`).

### 1.2 Рішення продукту (затверджено користувачем)
- **A.** Пріоритет ціни: майстер > заклад > мережа. Пріоритет тривалості: майстер > базова тривалість послуги.
- **B.** Акції й купони рахуються **від ціни майстра**.
- **C.** Публічний запис, «будь-який майстер»: ціна й тривалість клієнту **не показуються**, доки не визначено конкретного майстра. Після вибору слота (а отже, майстра) показуються точні значення.
- **D.** Значення задаються в картці майстра, у списку його послуг. Поля «Ціна» і «Тривалість» необов'язкові, порожнє поле означає базове значення. Редагують owner і admin (admin — за чинними правилами `StaffService`).
- **E.** Наявні записи зберігають знімок на момент створення і не перераховуються.
- **F.** Наявні дані: NULL означає базове значення. Backfill не потрібен.

## 2. Рішення: модель даних

### 2.1 Nullable-колонки в `beauty_specialist_services`, без окремої таблиці
Міграція `beauty_specialist_service_terms` (адитивна):
```sql
ALTER TABLE beauty_specialist_services
  ADD COLUMN price numeric(12,2) NULL,
  ADD COLUMN duration_minutes integer NULL;
ALTER TABLE beauty_specialist_services
  ADD CONSTRAINT ck_beauty_specialist_services_price
      CHECK (price IS NULL OR (price >= 0 AND price <= 1000000)),
  ADD CONSTRAINT ck_beauty_specialist_services_duration
      CHECK (duration_minutes IS NULL OR duration_minutes BETWEEN 5 AND 720);
```
- `NULL` означає «діє базове» (рішення F). Override ціни й override тривалості незалежні: можна задати лише одне з двох.
- PK `(tenant_id, specialist_id, service_id)` і композитні FK `(tenant_id, specialist_id)` / `(tenant_id, service_id)` (CASCADE) лишаються без змін. Override не може існувати без призначення, бо живе в тому самому рядку.
- RLS уже ENABLE+FORCE. Міграція додає `DO`-блок, який падає, якщо FORCE немає на `beauty_specialist_services`, `beauty_services`, `beauty_service_prices` (як у `beauty_staff_hardening`).
- GRANT: API-роль уже має SELECT/INSERT/UPDATE/DELETE на всіх таблицях (`deploy/staging/db/grants.sql`). Воркеру нових прав не треба: він ціну й тривалість не обчислює.
- EF: `SpecialistServiceLink.Price` (`HasPrecision(12,2)`), `SpecialistServiceLink.DurationMinutes` (`int?`), CHECK-обмеження в `BeautyDbContext`. `worker/src/testing/schema.sql` синхронізувати вручну (schema-drift).
- `ADD COLUMN … NULL` без default змінює лише метадані (переписування таблиці немає). CHECK на порожніх колонках перевіряється миттєво.
- **Up/Down:** `Down` видаляє CHECK і колонки, тобто **індивідуальні ціни/тривалості втрачаються**. Записи не страждають, бо їхні знімки у `beauty_appointments`. Відкат на staging/prod проходить через human gate. Перед відкатом експорт: `COPY (SELECT … WHERE price IS NOT NULL OR duration_minutes IS NOT NULL)`. Процедуру записати в runbook (TASK-804).

### 2.2 Обґрунтування
- Атрибут належить саме парі «майстер × послуга», тому зв'язок 1:1 з наявним рядком.
- Життєвий цикл збігається автоматично: зняли послугу з майстра — override зник; видалили послугу чи майстра — CASCADE.
- Ні нового RLS, ні нових FK, ні синхронізації двох таблиць.
- `GetSchedulesAsync` / `BlockReasonAsync` уже читають цей рядок, тож override доступний тим самим запитом.
- **Прийнятий компроміс:** ціна майстра одна для всіх закладів, де він працює (рішення A ставить майстра вище закладу). Ціна «майстер × заклад» потребуватиме нового ADR і, ймовірно, окремої таблиці з `location_id` (Q4).

## 3. Рішення: єдиний resolver ефективних умов

### 3.1 Місце в коді
- **Application**, `Features/BeautyBooking/ServiceTerms.cs`:
  ```csharp
  public enum PriceSource { Specialist, Location, Network }
  public sealed record EffectiveServiceTerms(
      Guid SpecialistId, Guid ServiceId, Guid LocationId,
      decimal? Price, PriceSource? PriceSource,   // null = ціни немає на жодному рівні
      int DurationMinutes, bool DurationOverridden);
  public static class ServiceTermsResolver
  {
      // чиста функція, без I/O: master ?? location ?? network; master ?? base
      public static EffectiveServiceTerms Resolve(Guid specialistId, ServiceInfo service, Guid locationId,
          decimal? networkPrice, decimal? locationPrice, decimal? specialistPrice, int? specialistDuration);
  }
  ```
- **Порт** `IBookingStore.GetServiceTermsAsync(Guid serviceId, Guid locationId, IReadOnlyCollection<Guid> specialistIds, CancellationToken ct)` → `IReadOnlyDictionary<Guid, EffectiveServiceTerms>`.
  - Не більше трьох запитів: послуга; ціни закладу/мережі; рядки зв'язку для `specialistIds`.
  - Майстер без призначення в словник не потрапляє.
  - Реалізація в `EfBeautyStore.Booking.cs`, потім викликає `ServiceTermsResolver.Resolve`.
- Пакетний варіант для каталогів: `GetServiceTermsForLocationAsync(locationId, specialistIds)` → `(specialistId, serviceId) → terms`. Він обслуговує публічний/чатовий список послуг без N+1.
- `GetPriceAsync(serviceId, locationId)` перейменувати на `GetBasePriceAsync`. Його лишають лише для «базових» поверхонь: `/promotions/preview`, `ServiceDto.networkPrice`, менеджерський `get_prices`. Решта викликів переходить на resolver.
- Інших копій правила «заклад, інакше мережа» в коді бути не повинно. Рев'ю перевіряє це grep-ом `ServicePrices` поза resolver і CRUD каталогу.

### 3.2 Хто викликає

| Споживач | Зміна |
|---|---|
| `BookingService.GetSlotsAsync` (staff і через `OnlineBookingCore` — публічний/чат) | `SlotCalculator.Compute` отримує тривалість **кожного майстра** `terms[s.SpecialistId].DurationMinutes`. `FreeSlot` адитивно отримує `durationMinutes`, `priceOriginal?`, `priceFinal?`, `promotionId?`, `promotionName?` (квота майстра на зараз, акція від ціни майстра). `SlotCalculator` не змінюється: він уже приймає `durationMinutes` |
| `BookingService.CreateAsync` (staff, публічний, чат) | `duration = terms.DurationMinutes`, `original = terms.Price` (null → 422 `price_not_set`). `PromotionPricing.QuoteWithCoupon(original, …)` — від ціни майстра (B). Нове `ExpectedDurationMinutes?` у `CreateAppointmentRequest`: розбіжність → 409 `duration_changed`, перевірка після `price_changed` |
| `BookingService.PatchAsync` (перенос) | Без змін: знімок `appt.DurationMinutes` (E). Майстер при переносі не змінюється. Якщо колись з'явиться зміна майстра, умови треба перерахувати (новий контракт) |
| `CouponService.CheckAsync` + `GET /coupons/check` (staff/публічний) | Новий необов'язковий `specialistId`. З ним ціна береться з resolver. Без нього — лише `outcome`, а `priceOriginal/priceFinal = null` (C) |
| `PublicBookingService.ListServicesAsync` | З `specialistId`: точні ціна/тривалість майстра. Без нього («будь-який»): `durationMinutes`, `priceOriginal`, `priceFinal` = **null**, нове поле `termsBySpecialist: true`. `promotionId/promotionName` віддаються: акція не залежить від майстра (бейдж без суми). Послуга входить у список, якщо хоча б один придатний майстер має ефективну ціну |
| `ChatBookingService.PrepareAsync` / `ConfirmAsync` | Prepare: terms обраного майстра (а не `pub.ListServicesAsync(…, null)`); пропозиція зберігає `duration_minutes`/ціни майстра. Confirm передає `ExpectedPriceFinal` і `ExpectedDurationMinutes`. `duration_changed` обробляється тим самим шаблоном, що й `price_changed` |
| ШІ чату: `list_services`, блок «ФАКТИ ЗАКЛАДУ» (`ChatTurnContext`), `find_slots`, `prepare_booking` | §5 |
| Нагадування (воркер, `ChatTemplates`) | Без змін: беруть знімок запису. Потрібен лише регресійний тест |
| Аналітика §19/§24, знімок собівартості | Без змін: Σ `price_final` / `duration_minutes` записів. Регресійний тест: запис із ціною майстра дає виручку й виплату від `price_final` майстра |
| `OverviewService` (вільні слоти огляду) | Тривалість через resolver для кожного майстра замість `GetActiveServiceDurationsAsync` (TASK-812) |
| `/promotions/preview`, `ServiceDto.networkPrice`, менеджерський `get_prices` | Лишаються базовими. `get_prices` отримує ознаку `variesBySpecialist` *(можна змінити)* |

### 3.3 Семантика знімка в `beauty_appointments`
- Нових колонок не потрібно: `duration_minutes`, `price_original` (ефективна ціна майстра до знижки), `price_final`, `promotion_id` уже є знімком.
- Значення фіксуються в `CreateAsync` з квоти, обчисленої перед транзакцією вставки. Зміна override після створення наявні записи не чіпає (E): ні `ends_at`, ні ціну, ні платіж, ні нагадування.
- Паралельна зміна override між квотою й вставкою не блокується: запис отримує значення, прочитані на початку створення. Захист від розбіжності з тим, що бачив клієнт, — `ExpectedPriceFinal` / `ExpectedDurationMinutes` (чат завжди, публічний — §4.3).
- Джерело ціни (`specialist|location|network`) на записі не зберігається *(можна змінити, Q5)*.

## 4. Рішення: «будь-який майстер»

### 4.1 Механіка слотів
1. `GET /slots` без `specialistId`. Для кожного придатного майстра слоти рахуються з **його** тривалістю: сітка 15 хв від його графіка, перетин із годинами закладу, зайнятість — лише його записи. Різні майстри на той самий `startsAt` мають різні `endsAt`. Слот «вміщується» незалежно для кожного: у кінці дня короткий майстер може мати слот, а довгий — ні.
2. Публічний/чатовий шлях пропускає майстрів без ефективної ціни. Staff-шлях віддає таких майстрів із `price* = null`: адміністратор бачить «ціну не задано», а create поверне 422 `price_not_set`.
3. Кожен елемент відповіді несе `specialistId`, `durationMinutes` і квоту майстра.
4. Public UI в режимі «будь-який» групує слоти за `startsAt` і показує **лише час** (C). Майстра для часу визначає детермінований порядок сервера: `startsAt`, потім `specialistId`, як зараз *(можна змінити, Q2)*.
5. Після вибору часу UI показує майстра, точну тривалість (`startsAt–endsAt`) і ціну з обраного слота ще **до** підтвердження. Клієнт може повернутися й вибрати інший час. Окремий запит не потрібен: квота вже в слоті.
6. `POST /appointments` надсилає `specialistId` обраного слота та `expectedPriceFinal` і `expectedDurationMinutes` (§4.3).

Ціни всіх майстрів у payload `/slots` не є таємницею: після вибору їх однаково показують. Правило C виконується на рівні UI. Каталог послуг для «будь-якого» (`services` без `specialistId`) сум не віддає взагалі.

### 4.2 Ризики й пом'якшення
- **«Сюрприз» ціни.** Клієнт обрав «будь-який» і після вибору часу бачить вищу ціну професіонала. Пом'якшення: точні умови до підтвердження, кнопка «Обрати інший час/майстра». Політику вибору майстра закриває Q2.
- **Нерівномірне завантаження.** Порядок за GUID стабільний, але бізнес-сенсу не має: той самий майстер систематично отримує «будь-якого» клієнта. Відкрите питання Q2.
- **Гонка слота.** 409 `slot_unavailable`, як і зараз. UI перечитує слоти.
- **Гонка умов.** Адміністратор змінив override між показом і записом → 409 `price_changed` / `duration_changed`. UI перечитує слоти й показує нові умови.
- **Купон до вибору майстра.** Перевірка без `specialistId` дає лише `outcome`. Підсумкову суму UI показує після вибору слота (перевірка вже з `specialistId`).
- **Фіксована знижка більша за ціну майстра** (дешевий майстер-початківець). Обмеження `price_final ≥ 0` гарантує CHECK БД, а `PromotionPricing` має обрізати знижку до 0. **Не перевірено читанням**: TASK-806 має покрити тестом.
- **Продуктивність.** +1–2 запити на виклик `/slots` (рядки зв'язку для майстрів закладу). Обсяг — десятки рядків, індекс PK. Нових індексів не потрібно.

### 4.3 Публічний create
Адитивно: `expectedPriceFinal?`, `expectedDurationMinutes?` у тілі `POST /api/public/{slug}/appointments`. Розбіжність → 409 `price_changed` / `duration_changed`. Поля не входять у відбиток ідемпотентності (`idempotency_request_hash`): це твердження клієнта, а не зміст запису. Frontend передає значення з обраного слота.

## 5. Рішення: ШІ-інструменти чату
- **`find_slots`:** кожен слот адитивно отримує `durationMinutes`, `priceOriginal`, `priceFinal`, `promotionPrice` (правило TASK-798: спершу повна ціна, потім акція).
- **`list_services`:** новий необов'язковий `specialistId` (точні умови майстра). Без нього — правило Q1 *(за замовчуванням: якщо умови однакові для всіх придатних майстрів закладу, віддаються значення; якщо різні — `priceOriginal/priceFinal/durationMinutes = null`, `termsVary: true`)*.
- **Блок «ФАКТИ ЗАКЛАДУ»** для послуги з різними умовами пише «ціна й тривалість залежать від майстра» без сум. Промпт: точну ціну й тривалість називати лише після вибору майстра або слота. Остаточну суму визначає система (`prepare_booking`).
- **`prepare_booking`:** підсумок із ціною й тривалістю майстра (через resolver). Модель не може підставити ціну: її рахує код.
- Захист від prompt injection не змінюється. Ціни й тривалості беруться лише з БД.

## 6. Рішення: API редагування, права, безпека
- **Адитивне розширення** `PUT /api/beauty/specialists/{id}/services`:
  `{serviceIds: [...], terms?: [{serviceId, price: number|null, durationMinutes: int|null}]}`.
  - `terms` відсутнє: override залишених послуг **не змінюються** (зворотна сумісність).
  - `terms` присутнє: для кожної перелічених послуги значення встановлюються точно (`null` = базове). Послуги поза `terms` не змінюються.
  - Валідація 422:
    - `terms_service_not_assigned` (serviceId не входить у `serviceIds`);
    - `duplicate_service`;
    - `invalid_price` (0..1 000 000, ≤ 2 знаки після коми);
    - `invalid_duration` (ціле 5..720).
  - Ліміт: `terms` ≤ 500, як `serviceIds`.
  - Усе в одній транзакції `ReplaceServicesAsync`.
  - Повторне призначення раніше знятої послуги створює рядок без override: значення не відновлюються.
- **Права:** ті самі, що для `SetServicesAsync`. `IsManager` (owner/admin), інакше 403 `forbidden`. `Guard` → `CanModify` (admin не змінює профілі, прив'язані до owner/admin, 403 `forbidden_role`). Чужий tenant → 404 через RLS.
- **Читання:** `SpecialistDto.services[]` адитивно отримує `price?` і `durationMinutes?`, лише override, `null` = базове. Їх бачать усі staff-ролі, як і зараз `services[]`. Ціни й так публічні. Собівартість сюди **не** потрапляє (ADR-014 §2.4).
- **Логування:** id майстра/послуги й назви змінених полів. Ціни не є ПД, але в логи їх не пишемо, щоб не зашумлювати. Аудит змін — Q3.
- **RLS/IDOR:** усі читання й записи під `app.tenant_id`. `serviceId` перевіряється через `ExistingServiceIdsAsync` (RLS).

## 7. Вплив на собівартість, виплату майстру й аналітику
- **Виплата майстру** (ADR-014 §2.2) = `round(price_final × master_percent / 100, 2)` у момент `completed`. Ціна майстра входить у `price_final` автоматично, тому виплата пропорційна ціні майстра. Змін у SQL знімка немає.
- **Собівартість** лишається рівнем «послуга × заклад/мережа» і від майстра не залежить. Окремий `master_percent` для майстра в цей ADR не входить (Q6).
- **Аналітика** (§19 виручка, завантаження за `duration_minutes`, §24 чистий дохід) рахується зі знімків запису. Змін немає, лише регресійні тести. Розрізу «за джерелом ціни» немає (Q5).

## 8. Відкриті питання (для людини)
- **Q1. Чат без обраного майстра.** Рішення C сформульоване для публічного запису. Пропозиція для чату: показувати умови, якщо вони однакові для всіх придатних майстрів, інакше «залежить від майстра». Підтвердити. Також вирішити, чи застосовувати те саме послаблення («однакові — показати») у вебі для «будь-якого». За замовчуванням у вебі суми для «будь-якого» не показуються **ніколи**.
- **Q2. Політика вибору майстра для «будь-якого»** при кількох вільних на той самий час: зараз фактично порядок GUID. Варіанти: найнижча ціна; найменше завантаження дня; ротація; пріоритет, заданий салоном.
- **Q3. Аудит змін цін/тривалостей майстра** (хто й коли змінив): append-only таблиця, як `beauty_service_cost_audit`, чи не потрібно у V1.
- **Q4. Ціна майстра в різних закладах.** Одна на всі заклади (прийнято) чи потрібна «майстер × заклад». Друге — новий ADR.
- **Q5.** Чи зберігати на записі джерело ціни (`price_source`) для аналітики «скільки записів за індивідуальною ціною».
- **Q6.** Чи потрібен індивідуальний `master_percent` / собівартість для майстра (професіонал отримує інший %).
- **Не перевірено читанням (перевірити в TASK-806/810):**
  - обрізання фіксованої знижки до 0 у `PromotionPricing`;
  - поточна форма адмінського екрана «Запис клієнта» (звідки він бере ціну/тривалість);
  - чи `TelegramPoller`/кнопки `slot_pick` (TASK-753) показують ціну в тексті кнопок;
  - точна логіка `ChatTurnContext` «хто які послуги надає».

## 9. Наслідки
Позитивні:
- Одне правило ефективних умов в одному місці (resolver). Прибираються три наявні копії логіки «заклад/мережа».
- Мінімальна адитивна міграція без backfill і без нових RLS/GRANT. Наявні записи, нагадування, аналітика й собівартість коректні без змін завдяки знімку.
- Акції, купони й виплата майстру автоматично рахуються від ціни майстра.

Негативні / прийняті компроміси:
- Зміна контракту §12: `GET /locations/{id}/services` без `specialistId` віддає `null` у сумах і тривалості, тож frontend і чат треба оновити в тому самому релізі.
- Ціна майстра одна для всіх закладів.
- Повторне призначення послуги губить колишній override.
- `Down` міграції губить індивідуальні значення.
- `/slots` стає трохи важчим (квота на кожного майстра).
- Порядок вибору майстра для «будь-якого» поки технічний (Q2).

## 10. Альтернативи
- **Окрема таблиця `beauty_specialist_service_overrides(tenant_id, specialist_id, service_id, [location_id], price, duration_minutes)`.** Відхилено для V1:
  - потрібна синхронізація з призначенням (FK на зв'язок або тригер), окремий RLS і GRANT;
  - додатковий join у гарячих шляхах.
  
  Перевага — природне розширення на «майстер × заклад». Повернутися до неї, якщо Q4 вирішиться на користь закладу.
- **Рядки `beauty_service_prices` з `specialist_id`.** Відхилено:
  - змішує рівні в одному UNIQUE (NULLS NOT DISTINCT на трьох колонках ускладнює пріоритет);
  - не покриває тривалість;
  - ціни читає specialist і публічний шлях, тож межі доступу змішуються.
- **Рівні кваліфікації (junior/pro) з цінами на рівень.** Відхилено: користувач вирішив задавати значення для конкретного майстра. Рівні — окрема функція.
- **Перераховувати наявні записи після зміни override.** Відхилено рішенням E: оплати, нагадування й `ends_at` уже зафіксовані.
- **Для «будь-якого» показувати діапазон «від–до».** Відхилено рішенням C.
- **Окремий ендпоінт `PUT /specialists/{id}/services/{serviceId}/terms`.** Відхилено: UI редагує список послуг майстра однією дією «Зберегти», а одна транзакція з `serviceIds` уникає проміжних станів.

## 11. Scope of truth
Цей ADR авторитетний щодо:
- місця зберігання й обмежень індивідуальних ціни/тривалості (§2);
- пріоритету й єдиного resolver ефективних умов (§3);
- семантики знімка на записі (§3.3);
- механіки слотів і показу умов для «будь-якого майстра» (§4);
- правил ШІ щодо ціни/тривалості майстра (§5);
- прав на редагування (§6).

Точні форми DTO й коди помилок належать до `beauty-contracts.md` §27 (TASK-805). Розширення на «майстер × заклад», рівні кваліфікації чи індивідуальну виплату потребують нового ADR.
