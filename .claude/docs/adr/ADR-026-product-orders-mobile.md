# ADR-026: Замовлення товарів клієнтами з мобільного застосунку (етап 3a складу): резерв, онлайн-оплата карткою, видача в закладі

**Date:** 2026-11-20
**Status:** accepted (2026-11-20, human gate G1 пройдено: схема БД та рішення §2 затверджено користувачем; P1: ПД гостя зберігаються без очищення, знеособлення — окремо за потреби). Прод-вмикання продажу — окремий gate (ПРРО L1, ПДВ L2, провайдер A3, юридична оферта P3); у проді продаж вимкнено.
**Author:** project-architect
**Deciders:** продуктові рішення (1)–(8) §1.2 — користувач, 2026-11-20. Схема (§3), рішення архітектора (§2, позначені «[А]») і ADR у цілому — чекають людини (`accepted` ставить людина).
**Уточнює (після `accepted`):** ADR-025 §2.4 п.1 (новий тип руху й операції `sale_return`; рух `sale` уперше використовується), ADR-025 §2.8 (етап 3 поділено: **3a** — цей ADR, **3b** — касовий продаж у закладі, override цін по закладах, `inventory.sales`, ПРРО, виручка в аналітиці).
**Не змінює:** ADR-002 (RLS: нових політик і SECURITY DEFINER-функцій немає), ADR-003 (outbox не потрібен, §2.11), ADR-014 (собівартість і виручка записів), ADR-018 (модулі й `tenant_suspended`), ADR-022 (каталог закладів, мітки клієнта, 426), ADR-023 (механізм і каталог прав: **нових ключів немає**).

Контракт: `.claude/docs/beauty-contracts.md` **§33** (`API-CONTRACT-PRODUCT-ORDERS-V1`, v0.40) — маршрути, DTO, коди, точні колонки, тести. При розбіжності діє цей ADR; зміна семантики — новим ADR.

---

## 1. Context

### 1.1 Стан коду (2026-11-20, перевірено)

- Склад етапів 1/2a/2b реалізовано (ADR-025, §32): партії, рівень `beauty_stock_levels` як точка серіалізації (`FOR UPDATE` у порядку `(location_id, product_id)`), FEFO, append-only `beauty_stock_operations`/`beauty_stock_movements` (REVOKE + тригери `beauty_stock_journal_block_mutation`), дефіцит `shortage_quantity`. CHECK типів уже містить `sale` (рух < 0) і `kind = 'sale'` (ADR-025 §2.4 п.1 «одразу надмножиною»).
- `beauty_products.sale_price` (одна мережева ціна, `NULL` для `salon_use`; `0` допустимо) — §32.26; дерево категорій і `categoryPath` — §32.27.
- `IPaymentService` (`backend/BeautyCrm.Application/Features/BeautyBooking/IPaymentService.cs`): `ChargeAsync(Guid appointmentId, decimal amount)`, `RefundAsync(Guid paymentId, decimal amount)` (ідемпотентний за `beauty_payments.id`); єдина реалізація — `MockPaymentService` (`TryAddSingleton`, провайдер не обрано, A3). `beauty_payments.appointment_id` — `NOT NULL`; читається лише в `EfBeautyStore.Booking.cs` (`GetPaymentAsync`, `UpdatePaymentAsync`).
- Публічний запис (§12, `PublicBookingController`): tenant за slug через `PublicTenantResolver` (окреме з'єднання + `app.tenant_slug` + вузька політика `tenants_login_lookup`, далі звичайний RLS `app.tenant_id`; **нових політик немає**); невідомий slug / без `beauty_booking` → `404 not_found`, призупинений → `403 tenant_suspended`. Токен = base64url(HMAC-SHA256), у БД лише SHA-256. `Idempotency-Key` (хеш ключа + хеш тіла, `200 + Idempotent-Replayed`). `[PublicClient]` (мітки `X-Booking-Client`/`X-App-Version`, `426 upgrade_required`), Protection-режими й `ICaptchaVerifier` (§29.4–29.6), rate-limit політики `public-read|token|write` за IP (/64 для IPv6).
- Клієнт публічного запису: `ResolveClientAsync` шукає `beauty_clients` за нормалізованим телефоном (найстаріший оригінал), інакше створює нового з `phone_source = web_form`; ім'я наявного клієнта не перезаписується й не повертається.
- Скасування запису з поверненням (`CancellationService`): атомарний claim статусу → `RefundAsync` → компенсація `ReleaseCancelAsync` при збої. Цей патерн повторюємо.
- Cross-tenant каталог (§29) читає дані лише через SECURITY DEFINER `beauty_public_*`, бо tenant невідомий до запиту. Для маршрутів з `{tenantSlug}` такого обмеження немає.
- Мобільний застосунок (`mobile/`, патерн `mobile/src/api/catalog.ts`) реалізує інша команда/чат за точним контрактом.

### 1.2 Рішення користувача (2026-11-20)

1. Оплата онлайн-карткою через `IPaymentService`; mock у dev/staging; провайдер не обрано (A3); у prod продаж **вимкнено** прапором `ProductOrders:Enabled` до вибору ПРРО-провайдера й юридичних вимог (ADR-025 §2.8, прецеденти ADR-010 §7.3, ADR-015).
2. Резерв на 15 хв при створенні; FEFO-списання при підтвердженні оплати механізмом етапу 1; TTL-скасування неоплачених; оверселінг заборонено → `422 insufficient_stock` без точних кількостей.
3. Видимість без окремого прапорця: активні товари `purpose ∈ sale|both` з `sale_price IS NOT NULL`; булеве `inStock`, без кількостей; `salon_use` ніколи.
4. Клієнт-гість: ім'я + телефон, публічний токен, прив'язка до `beauty_clients` за телефоном, мінімум ПД, ліміти, captcha, `Idempotency-Key`, мітки клієнта й 426.
5. Ціна — знімок у рядках; `salePrice` **з ПДВ включно**; override цін по закладах — не в цьому етапі.
6. Лише самовивіз: клієнт обирає заклад; `paid` → видача адміном → `fulfilled`.
7. Повернення коштів у 3a — лише адмін (до видачі, 100%), автоматичних повернень за ініціативою клієнта немає.
8. Права CRM: перегляд `inventory.view`, дії `inventory.manage`; **нових ключів прав немає**; потрібен модуль `beauty_inventory`; `tenant_suspended` діє.

---

## 2. Decision

### 2.1 Обсяг етапу 3a

Робить: публічний каталог товарів закладу, замовлення гостем, резерв, онлайн-оплата (mock), FEFO-списання при оплаті, скасування клієнтом до оплати, TTL-закриття, екран замовлень у CRM (перелік, деталь, видача, скасування з поверненням, перевірка зависої оплати), лічильник оплачених до видачі.

**Не робить** (етап 3b або окремі рішення): касовий продаж у закладі (`beauty_product_sales`), override цін по закладах, ключ `inventory.sales`, ПРРО/`IFiscalService`, ПДВ-колонки, `productRevenue` в аналітиці, доставку, повернення після видачі, часткове повернення, знижки/акції/купони на товари, сповіщення клієнту (SMS/Telegram) про статус, покупки в картці клієнта CRM, ШІ-tools.

### 2.2 Вмикання (fail closed)

Публічні маршрути замовлень доступні, лише якщо виконано **всі** умови:
1. tenant `active`, модуль `beauty_booking` (його вимагає `PublicTenantResolver`; без нього tenant публічно не існує — `404 not_found`);
2. модуль `beauty_inventory` увімкнено для tenant;
3. `ProductOrders:Enabled` = `true` (без урахування регістру; **будь-яке інше значення, порожнє або відсутній ключ — вимкнено**, урок §32.26 D6).

Порушення 2 або 3 → `404 { "code": "product_orders_disabled" }` на каталозі товарів, створенні, оплаті й скасуванні; мобільний застосунок ховає розділ «Товари». **[А]** `GET` замовлення за токеном працює й тоді (лише читання), щоб клієнт бачив уже оплачене замовлення після вимкнення.

**[А] Prod-запобіжник:** у середовищі `Production` (і невизначеному) `ProductOrders:Enabled=true` за зареєстрованого `MockPaymentService` — **помилка старту**. Отже, прод-вмикання фізично неможливе до реєстрації реального провайдера (окремий ADR, A3) і закриття L1/L2.

### 2.3 Публічний API, tenant і RLS — **вибір: tenant за slug + звичайний RLS app-ролі (як §12)**

Маршрути живуть у новому `PublicProductOrdersController` під `/api/public/{tenantSlug}/…` з тими самими фільтрами, що й публічний запис: `[AllowAnonymous]`, `[PublicClient]` (426), `[PublicTenant]` (404/403), rate limiting. Після резолву slug з'єднання працює під `app.tenant_id` і RLS FORCE; **нових політик і SECURITY DEFINER-функцій немає**.

Обґрунтування: tenant відомий зі шляху до будь-якого читання даних — це рівно ситуація §12, вже пройдена security-review; SECURITY DEFINER (§29) потрібен лише для **cross-tenant** читань без tenant-контексту. Definer-функції для запису (резерв, оплата, списання) винесли б FEFO-логіку й блокування етапу 1 у PL/pgSQL, подвоївши реалізацію складу й обійшовши RLS там, де він якраз корисний.

### 2.4 Видимість товарів для клієнта

Товар видно в закладі, якщо: `is_active`, `purpose ∈ sale|both`, `sale_price IS NOT NULL` **і [А] `sale_price > 0`** (нульова ціна не оплачується: `ck_beauty_payments_amount_positive`), товар **не** вимкнений у закладі (`beauty_product_location_settings.is_enabled = false` ховає), заклад активний. `inStock = available ≥ 1` (§2.6). Кількості, закупівельні ціни, штрихкод, примітка, постачальник, партії **не** віддаються. Категорія — лише рядок `categoryPath` (§32.27). **[А]** Без пагінації: до 500 товарів, ознака `truncated`.

### 2.5 Життєвий цикл замовлення

Збережені статуси: `pending_payment | paid | fulfilled | cancelled | expired | refunded`.

| Перехід | Хто | Умова (атомарний умовний `UPDATE`) | Склад | Гроші |
|---|---|---|---|---|
| — → `pending_payment` | клієнт (create) | доступно ≥ кількості під блокуванням | резерв (§2.6) | — |
| `pending_payment` → `paid` | оплата | `payment_started_at` встановлено цим запитом, Charge успішний, FEFO вистачає | `sale` (−), FEFO без прострочених | Charge |
| `pending_payment` → `refunded` | система (компенсація) | Charge успішний, але FEFO не вистачає | — | Refund 100% |
| `pending_payment` → `cancelled` | клієнт або адмін | `payment_started_at IS NULL` | резерв зникає | — |
| `pending_payment` → `expired` | воркер (або обчислено, §2.6) | `reservation_expires_at ≤ now`, `payment_started_at IS NULL` | резерв зникає | — |
| `paid` → `fulfilled` | адмін (`inventory.manage`) | `refund_started_at IS NULL` | — | — |
| `paid` → `refunded` | адмін | claim `refund_started_at`, Refund успішний | `sale_return` (+) у ті самі партії | Refund 100% |
| `fulfilled`, `cancelled`, `expired`, `refunded` | — | термінальні в 3a | — | — |

**Ефективний статус** (віддають API, фільтрують списки): `pending_payment` з `payment_started_at IS NULL` і `reservation_expires_at ≤ now` читається як `expired` незалежно від того, чи воркер уже записав це в БД. Тож коректність не залежить від воркера.

**[А] Номер замовлення** `order_number` — послідовний у межах tenant (`№1042`), для видачі в закладі; публічно нічого не відкриває (доступ до замовлення — лише за токеном або в CRM).

### 2.6 Резерв — **вибір: похідний від рядків активних замовлень, без лічильника**

```
reserved(location, product) = Σ lines.quantity  замовлень цього закладу, де
    status = 'pending_payment' AND (reservation_expires_at > now OR payment_started_at IS NOT NULL)
available(location, product) = Σ quantity непрострочених партій (expires_on IS NULL OR expires_on >= localToday)
                               − reserved(location, product)
```

- Обчислюється **під `FOR UPDATE` рядка `beauty_stock_levels`** пари — тієї самої точки серіалізації, що й усі операції складу; два паралельні створення на останню одиницю серіалізуються, другий отримує `422 insufficient_stock`.
- Рядка рівня немає → `available = 0`, **рядок не створюється** (анонімний трафік не пише в склад) **[А]**.
- Ручні операції (списання, переміщення, інвентаризація) і автосписання (2a) **резерву не враховують** — фізична реальність має пріоритет (зіпсований товар списують, візит завершується). Наслідок — рідкісна нестача при оплаті, яку закриває компенсація (§2.7).

Чому не колонка `reserved_quantity` у `beauty_stock_levels`: це другий денормалізований лічильник, який треба інкрементувати/декрементувати в кожному переході (створення, оплата, скасування, TTL) і який дрейфує при будь-якому збої; TTL-воркер мусив би отримати `UPDATE` на рівні складу (ширші права, участь у блокуваннях складу, ризик дедлоків) і був би **обов'язковим для коректності** — зупинений воркер «заморожує» залишок назавжди. Також CHECK `reserved ≤ quantity` неможливий (ручне списання законно зменшує `quantity` нижче резерву), а без нього лічильник не захищений БД. Похідний резерв не має чого рознести: інваріант «резерв = Σ рядків активних замовлень» істинний за визначенням, а закінчення TTL діє автоматично за часом.
Чому не окрема таблиця `beauty_product_reservations`: вона дублювала б `beauty_product_order_lines` (той самий товар, кількість, заклад, строк) і мала б той самий ризик розбіжності з замовленням.
Ціна рішення — агрегат за рядками активних замовлень у транзакції створення й у каталозі; обсяг обмежено лімітами (§2.9), є частковий індекс за `pending_payment`.

### 2.7 Оплата

**Узагальнення `IPaymentService` (адитивно, без зміни потоку запису):**

```csharp
public enum PaymentPurpose { Appointment, ProductOrder }
public sealed record PaymentCharge(Guid PaymentId, PaymentPurpose Purpose, Guid SubjectId, decimal Amount, string Currency);

public interface IPaymentService
{
    Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct);      // без змін (запис)
    /// Ідемпотентний за PaymentId (beauty_payments.id): повтор не списує вдруге, повертає той самий результат.
    Task<PaymentResult> ChargeAsync(PaymentCharge charge, CancellationToken ct)
        => Task.FromResult(new PaymentResult(false, null, "not_supported"));                        // fail closed за замовчуванням
    Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct);           // без змін, ідемпотентний за paymentId
}
```
Реалізація за замовчуванням не дає наявним тестовим фейкам зламатися й відмовляє (fail closed). `MockPaymentService` реалізує нову перевантажену: успіх з `mock_pay_{PaymentId:N}`; **[А] детермінована відмова для QA**: сума з копійками `.13` (напр. `100.13`) → `mock_declined`. Перехід запису на нову перевантажену — поза 3a. **Вимога до майбутнього провайдера:** ідемпотентність за `PaymentId` (його передають як ключ ідемпотентності/order reference провайдера).

**Алгоритм `POST …/pay`** (тіло v1 — `{}`; картку в mock не вводять):
1. **Claim (коротка транзакція):** `UPDATE … SET payment_started_at = now, payment_attempts = payment_attempts + 1 WHERE status = 'pending_payment' AND payment_started_at IS NULL AND reservation_expires_at > now AND payment_attempts < MaxPaymentAttempts` + `INSERT beauty_payments (product_order_id, amount = total, method = 'card', status = 'pending')`. 0 рядків → відповідь за поточним станом: `paid|fulfilled` → `200` + `Idempotent-Replayed: true`; у процесі → `409 payment_in_progress` (або reconcile, якщо застаріле, п.5); прострочено → `409 order_expired`; вичерпано спроби → `422 payment_attempts_exceeded`; інше → `409 order_closed`.
2. **Charge поза транзакцією** (`PaymentCharge`, `PaymentId` = id рядка платежу). Блокувань БД під час зовнішнього виклику немає.
3. **Відмова:** платіж `failed`, `payment_started_at := NULL` → `402 payment_failed` з замовленням (лишається `pending_payment`, резерв діє до `reservationExpiresAt`, можна повторити).
4. **Успіх → фіналізація (одна транзакція):** платіж `paid` + `provider_payment_id`; `FOR UPDATE` замовлення → рівні пар у порядку `(location_id, product_id)` → FEFO **без прострочених** за `localToday` закладу на кожен рядок → операція `kind = sale` (`product_order_id`, `created_by_user_id NULL`) + рухи `sale` (−) з партіями й `unit_cost` партій; партії й `level.quantity` зменшуються; прив'язка клієнта (§2.9); замовлення `paid`, `paid_at`, `payment_id`, `payment_started_at := NULL`. → `200`.
   **Нестача під блокуванням** (ручне списання/автосписання з'їли зарезервоване): відкат фіналізації, платіж фіксується `paid` окремою транзакцією, далі **компенсація**: `RefundAsync(paymentId, total)` → успіх → замовлення `refunded` (`close_reason = stock_unavailable`, `refund_amount = total`), платіж `refunded` → `409 stock_unavailable` з замовленням. Збій повернення → замовлення лишається «оплата в процесі» (гроші списані, склад не змінено), лог `Error product_order_compensation_failed` → п.5.
5. **Reconcile зависої оплати** (`payment_started_at` старше `ProductOrders:PaymentStaleSeconds` = 120 с: процес упав між Charge і фіналізацією, або компенсація не вдалася): перехоплення умовним `UPDATE … SET payment_started_at = now WHERE payment_started_at = <старе>`; платіж `pending` → повторний `ChargeAsync` з **тим самим** `PaymentId` (ідемпотентно, без подвійного списання) → п.3/п.4; платіж `paid` → одразу п.4. Запускається повторним `POST /pay` клієнта або дією адміна «Перевірити оплату» в CRM. **[А] Фонового reconcile у backend немає** (крос-tenant hosted service потребував би обходу RLS); воркер лише логує зависі (§2.11).

Гарантія: для кожного замовлення не більше одного успішного списання грошей (claim + ідемпотентний `PaymentId`), не більше однієї операції `sale` (UNIQUE `(tenant_id, product_order_id, kind)`), і стан «гроші списано, склад не змінено» завжди має шлях до `paid` або `refunded`.

**Повернення адміном (`paid` → `refunded`)** за патерном `CancellationService`: claim `refund_started_at` (блокує видачу) → `RefundAsync(paymentId, total)` (100%, без комісій політики §11 — це не запис) → успіх → транзакція: `FOR UPDATE` замовлення → рівні → операція `kind = sale_return` (`created_by_user_id` = адмін) + рухи `sale_return` (+) **у ті самі партії**, з яких був `sale` (дзеркально, з тим самим `unit_cost`) → статус `refunded`. Збій повернення → `refund_started_at := NULL`, `402 refund_failed`, нічого не змінено. Падіння після успішного Refund → повтор дії адміном перехоплює застарілий claim, `RefundAsync` з тим самим `paymentId` не повертає вдруге.

### 2.8 Склад: `sale` і `sale_return`

- `sale` (вже в CHECK, < 0): лише з партіями (`is_shortage = false`), FEFO без прострочених; **дефіцит не змінює** (як ручне списання, §32.17).
- **Новий** тип руху й операції `sale_return` (> 0): відновлює кількість **рівно в партіях** продажу (`batch.quantity += q`; `≤ initial_quantity` гарантовано, бо до цієї партії нічого інше не додає); `level.quantity += q`; дефіцит **не погашає** (повернення — точна інверсія продажу, а не надходження; погашення лишається за `receipt|transfer_in|stocktake`). Прострочена тим часом партія відновлюється як є (фізично це той самий товар) — її списують звичайним порядком.
- Журнал лишається append-only: повернення — новий рух, жодного `UPDATE`/`DELETE` рухів. Інваріанти §32.17 не змінюються (обидва типи — рухи з партіями).
- `OperationSummaryDto` + `productOrderId` (адитивно); `GET /operations?kind=` і `/movements?type=` приймають `sale|sale_return`.

### 2.9 Клієнт-гість і ПД

- Вхід: `customer.name` (2..100), `customer.phone` (нормалізація E.164 як §12). Email, згода на маркетинг, адреса — **не приймаються**.
- На замовленні зберігаються `guest_name`, `guest_phone` (контакт для видачі — знімок, бо ім'я наявного клієнта не перезаписується й не розкривається).
- **[А] Прив'язка до `beauty_clients` — лише при успішній оплаті** (у транзакції фіналізації, `ResolveClientAsync`, `phone_source = web_form`), а не при створенні: неоплачені/прострочені замовлення (зокрема спам) не створюють карток клієнтів у CRM. `client_id` nullable (до оплати й для майбутнього стирання ПД).
- Публічні відповіді **не** містять імені, телефону, `client_id`, внутрішніх id замовлення чи платежу.
- Ліміти (усі → `422 order_limit_reached`, порядок як §29.6): створень на заклад за годину (`MaxCreatesPerLocationPerHour` = 60), активних `pending_payment` на телефон у tenant (`MaxPendingPerPhone` = 2), створень на телефон за годину (`MaxCreatesPerPhonePerHour` = 5); IP — політики `public-*`. Лічильники рахуються під advisory-блокуванням tenant (§33.6), тож паралельні запити їх не обходять.
- Captcha/integrity — ті самі режими `PublicBooking:Protection:*` і `CaptchaRequired` (§29.5) на створенні замовлення; перевірка після пошуку ідемпотентного ключа.
- Строк зберігання ПД неоплачених/прострочених замовлень — відкрите питання P1 (§9); у 3a без очищення.

### 2.10 Права CRM (без нових ключів)

- Маршрути `/api/beauty/inventory/product-orders*` у межах класу складу: `Staff` + `[RequireModule("beauty_inventory")]`; перегляд — `inventory.view ≥ own`, дії (видача, скасування/повернення, reconcile) — `inventory.manage ≥ own`; scope закладу за правилами §32.2 (поза scope → `404 product_order_not_found`). Owner/admin/deputy за пресетами мають `all`, specialist — лише перегляд своїх закладів.
- **Телефон гостя** — за наявним правом `clients.contacts`: `full` → `phone`, інакше `phoneMasked` (формат §30.5 «Маскування контактів», `+••••••••••33`). `clientId` — лише за `clients.view = all`.
- Чому без `inventory.sales`: новий ключ вимагає міграції backfill, `catalogVersion 3`, змін «Команди й доступів» і `AuthzBaselineMatrixTests` (ADR-023 §2.11). Для 3a видача й повернення — операції складу того ж закладу; ключ з'явиться з касовим продажем 3b окремим рішенням, і тоді дії над замовленнями можуть перейти під нього (зміна семантики — новим ADR).

### 2.11 Воркер TTL

`pollProductOrders` у `worker/src/scheduler/pollers.ts`, раз на хвилину, через `forEachTenant`, прапор `WORKER_PRODUCT_ORDERS_ENABLED` (лише `true`; за замовчуванням вимкнено) і модульний гейт `beauty_inventory`. Робить лише **косметичний** перехід `pending_payment → expired` (умовний `UPDATE … WHERE status = 'pending_payment' AND payment_started_at IS NULL AND reservation_expires_at <= now − 1 хв`, пачками ≤ 500 через `FOR UPDATE SKIP LOCKED`) і логує кількість зависих оплат (> 30 хв). Склад, платежі й рядки воркер **не чіпає**; коректність резерву від нього не залежить (§2.6). Outbox (ADR-003) не потрібен: немає зовнішнього побічного ефекту, перехід ідемпотентний. GRANT — лише колонковий (§3).

### 2.12 Ціни, ПДВ, валюта

- Ціна рядка — знімок `sale_price` на момент створення (`unit_price`), разом зі знімками `product_name` і `unit`; `line_total = unit_price × quantity`; `total_amount = Σ line_total`. Пізніші зміни ціни/назви на замовлення не впливають.
- **`salePrice` вважається ціною з ПДВ включно**; ставки й суми ПДВ у 3a не зберігаються (L2 відкрите — можливі колонки в 3b).
- Валюта — `UAH` (CHECK); мультивалютності немає.
- **[А]** Кількість у замовленні — **ціле** 1..10 на рядок для будь-якої одиниці (роздріб продається штуками/упаковками); `inStock = available ≥ 1`. ≤ 20 рядків, `total ≤ ProductOrders:MaxOrderTotal` (50 000).
- **[А]** `expectedTotal` (необов'язково) у створенні: розбіжність з поточною сумою → `409 price_changed` з актуальною сумою (прецедент `ExpectedPriceFinal` запису).

### 2.13 Безпека

| Загроза | Захист |
|---|---|
| Доступ до чужого tenant | slug → `tenants_login_lookup` → RLS FORCE `app.tenant_id`; композитні FK на всіх нових зв'язках |
| Перебір токенів замовлень | 256-біт HMAC з окремим контекстом (`product_order.`), у БД лише SHA-256; однакова `404 not_found` для невідомого/чужого/зіпсованого; `public-token` 30/хв на IP; `no-store`, `no-referrer` |
| Перебір номерів | `order_number` публічно не приймається жодним маршрутом |
| Резерв-DoS (бот блокує весь залишок) | TTL 15 хв, ліміти на телефон/заклад/IP, captcha/integrity, цілі кількості ≤ 10, ≤ 20 рядків, `MaxPendingPerPhone` |
| Карткове тестування (carding) | ≤ 5 спроб оплати на замовлення, `public-write` 10/хв на IP, captcha на створенні; у prod без реального провайдера продаж неможливий (§2.2) |
| Подвійне списання грошей / товару | claim `payment_started_at`, ідемпотентний `PaymentId`, UNIQUE операції `sale`/`sale_return` на замовлення |
| «Гроші списано — склад не оновлено» | фіналізація в одній транзакції; збій → reconcile (§2.7 п.5); нестача → автоматичне повернення 100% |
| Оверселінг | доступність під `FOR UPDATE` рівня; при оплаті FEFO під блокуванням; партії/рівні `≥ 0` (CHECK) |
| Анонім пише в CRM | клієнт створюється лише після успішної оплати; ім'я наявного клієнта не перезаписується |
| Спуфінг ціни/статусу | ціна, статус, `clientId`, `source` з тіла ігноруються; сума рахується сервером |
| Витік ПД | публічні DTO без ПД; телефон у CRM за `clients.contacts`; логи без імені, телефону, токена й сум |
| Обхід вимкнення | три умови §2.2 перевіряються на кожному змінному маршруті; prod-запобіжник mock |

### 2.14 Спостережуваність (логи без ПД, токенів і сум)

`product_order_created order={id} location={id} lines={n} client={web|ios|android} replayed={bool}`, `product_order_payment order={id} attempt={n} result={paid|failed|stock_unavailable|in_progress|reconciled}`, `product_order_compensation_failed order={id}` (Error), `product_order_fulfilled order={id} user={id}`, `product_order_cancelled order={id} by={client|staff} refund={bool}`, `product_order_refund_failed order={id}` (Warning), воркер `product_orders_expire tenant={id} expired={n} stuck={n}`. Метрики (тег Activity) — `beauty.product_order.result`. Шлях `/product-orders/{token}` у access-логах проксі — правило §12 (не зберігати).

---

## 3. Схема (на затвердження людини; точні колонки, CHECK і індекси — контракт §33.15)

Одна міграція `beauty_product_orders` (адитивна, без backfill даних):

1. **`beauty_product_orders`** (`Base`/`TenantFk`, RLS ENABLE+FORCE+`tenant_isolation`): `location_id`, `order_number` (UNIQUE у tenant), `public_token_hash` (UNIQUE), `idempotency_key_hash` + `idempotency_request_hash` (UNIQUE у tenant), `status` (6 значень, CHECK), `guest_name`, `guest_phone` (E.164, CHECK), `client_id NULL`, `total_amount`, `currency = 'UAH'`, `reservation_expires_at`, `payment_attempts`, `payment_started_at`, `refund_started_at`, `payment_id NULL` (→ `beauty_payments`), `paid_at`, `fulfilled_at`, `fulfilled_by_user_id`, `closed_at`, `close_reason` (`client|staff|expired|stock_unavailable`), `cancelled_by_user_id`, `cancel_note`, `refund_amount`, `created_at`, `updated_at`. CHECK-и узгодженості статусу з часовими мітками й причинами. Індекси: частковий за `pending_payment` (резерв і TTL), за закладом і часом, за телефоном і часом (ліміти). **Без `DELETE`** для app-ролі.
2. **`beauty_product_order_lines`**: `order_id`, `line_no`, `product_id`, `product_name`, `unit`, `quantity` (ціле > 0), `unit_price` (> 0), `line_total` (= `unit_price × quantity`, CHECK). UNIQUE `(tenant_id, order_id, product_id)`. **Append-only** (REVOKE `UPDATE, DELETE`).
3. **`beauty_payments`**: `appointment_id` → `NULL`; новий `product_order_id NULL` (композитний FK RESTRICT); CHECK `num_nonnulls(appointment_id, product_order_id) = 1`; частковий індекс за `product_order_id`. Кілька рядків на замовлення (спроби); успішний — `orders.payment_id`.
4. **`beauty_stock_operations`**: `kind` += `sale_return`; новий `product_order_id NULL` (FK RESTRICT); CHECK `(kind IN ('sale','sale_return')) = (product_order_id IS NOT NULL)`; UNIQUE `(tenant_id, product_order_id, kind) WHERE product_order_id IS NOT NULL`.
5. **`beauty_stock_movements`**: `type` += `sale_return`, знак `sale_return > 0`. Зміна CHECK — DDL на append-only таблиці (прецедент 2a; тригери DDL не блокують, рядки не оновлюються).
6. **GRANT** (синхронно `scripts/dev/setup-db.ps1` і `deploy/staging/db/grants.sql`): app — `REVOKE DELETE ON beauty_product_orders`, `REVOKE UPDATE, DELETE ON beauty_product_order_lines`; воркер — `SELECT (id, tenant_id, status, reservation_expires_at, payment_started_at)` і `UPDATE (status, closed_at, close_reason, updated_at)` на `beauty_product_orders`, більше нічого.
7. **Down:** відмовляє (`RAISE EXCEPTION`), якщо є хоч одне замовлення або платіж з `product_order_id`, або рух/операція `sale|sale_return`; інакше повертає CHECK-и й `NOT NULL`.

---

## 4. Consequences

**Positive**
- Клієнт мобільного застосунку купує товар салону з гарантованою наявністю на момент оплати; CRM бачить оплачене замовлення й веде видачу.
- Один механізм складу (FEFO під блокуванням рівня) обслуговує ручні операції, автосписання й онлайн-продаж; журнал лишається append-only.
- Резерв не має лічильника, що може розійтися з реальністю; воркер не потрібен для коректності й не має прав на склад.
- Жодних нових RLS-політик, definer-функцій і ключів прав; повторно використано перевірений публічний конвеєр §12/§29.
- Прод захищено від продажу без ПРРО й реального провайдера на рівні старту застосунку.

**Negative / accepted trade-offs**
- Mock-оплата не вводить даних картки: контракт `pay` v1 майже напевно зміниться з реальним провайдером (3-D Secure, hosted checkout/redirect) → `API-CONTRACT-PRODUCT-ORDERS-V2` і підвищення `MinAppVersion` (426). Мобільний клієнт має ізолювати крок оплати.
- Ручні операції складу можуть «з'їсти» резерв → рідкісне автоматичне повернення коштів клієнту (`stock_unavailable`).
- Агрегат резерву рахується в транзакції створення й у каталозі (дешевше за лічильник у підтримці, дорожче за читання; обмежено лімітами й індексом).
- Створення замовлень серіалізується в межах tenant (advisory-блокування заради номера й лімітів) — прийнятно для обсягів салону.
- `beauty_payments.appointment_id` стає nullable — будь-який майбутній код платежів має враховувати обидва джерела.
- Виручка від товарів не потрапляє в аналітику до 3b; клієнт не отримує повідомлень про статус (лише екран застосунку).
- ПД неоплачених замовлень зберігаються без строку до рішення P1.

---

## 5. Alternatives considered

- **SECURITY DEFINER `beauty_public_*` для всіх операцій замовлення** — відхилено (§2.3): tenant відомий зі slug; дублювання FEFO/блокувань у PL/pgSQL і обхід RLS.
- **Колонка `reserved_quantity` у `beauty_stock_levels`** — відхилено (§2.6): денормалізований лічильник без можливого CHECK, обов'язковий воркер з правами на склад.
- **Таблиця `beauty_product_reservations`** — відхилено (§2.6): дубль рядків замовлення.
- **Створення й оплата одним запитом (як публічний запис)** — відхилено: рішення користувача про 15-хвилинний резерв і повтор після `payment_failed`; окремий крок оплати також готує місце для redirect/3-DS провайдера.
- **Списання при створенні, повернення при TTL** — відхилено: неоплачені замовлення писали б у append-only журнал пари рухів на кожен кошик (шум, ризик), а анонім змінював би партії.
- **Списання при видачі (`fulfilled`), а не при оплаті** — відхилено рішенням користувача; до того ж між оплатою й видачею товар міг би бути проданий іншому.
- **Повернення через `receipt` нової партії** — відхилено: губить термін придатності, партію й `unit_cost`, гасить дефіцит, хоча товар не надходив. `sale_return` у ті самі партії — точна інверсія.
- **`UPDATE`/видалення руху `sale` при скасуванні** — відхилено: порушує append-only (ADR-025 §2.4).
- **Окрема таблиця платежів замовлень** — відхилено: один реєстр платежів і один `RefundAsync(paymentId)`; ціна — nullable `appointment_id` і CHECK «рівно одне джерело».
- **Створювати клієнта CRM при створенні замовлення (як запис)** — відхилено: анонім без оплати наповнював би базу клієнтів.
- **Новий ключ `inventory.sales` / `orders.*` у 3a** — відхилено (§2.10): backfill і `catalogVersion` заради двох дій, які вже покриває `inventory.manage`.
- **Окремий прапорець «показувати в застосунку» на товарі** — відхилено рішенням користувача (видимість за `purpose` і ціною).
- **Точна кількість у каталозі / в `insufficient_stock`** — відхилено рішенням користувача (комерційна інформація, розвідка залишків).
- **Фоновий reconcile у backend (hosted service)** — відхилено в 3a: крос-tenant сканування потребує обходу RLS; reconcile запускає повтор клієнта або адмін.
- **Outbox-подія для TTL** — відхилено: немає зовнішнього ефекту; ідемпотентний умовний `UPDATE` достатній.

---

## 6. Ризики

| # | Ризик | Пом'якшення |
|---|---|---|
| R1 | Гонки резерву (дві покупки останньої одиниці) | `FOR UPDATE` рівня + агрегат під блокуванням; тест паралельних створень |
| R2 | Дедлок оплати з операціями складу | єдиний порядок: замовлення → рівні `(location_id, product_id)` → (товари/налаштування `FOR SHARE`); створення: advisory tenant → рівні; воркер рівнів не блокує |
| R3 | Подвійне списання грошей при повторі/паралелі | claim `payment_started_at`; `PaymentId` як ключ ідемпотентності провайдера; тест паралельних `pay` |
| R4 | Частковий збій «гроші списано — склад не оновлено» | фіналізація атомарна; стан видимий (`paymentInProgress`); reconcile клієнтом або адміном; воркер логує зависі > 30 хв |
| R5 | Резерв «з'їдено» ручним списанням → компенсація; збій компенсації | `409 stock_unavailable` + повернення; збій → reconcile; лог Error |
| R6 | Провайдер без ідемпотентності за ключем | вимога в ADR провайдера; без неї — метод статусу платежу (`GetChargeStatusAsync`) у V2 |
| R7 | Mock у prod | помилка старту §2.2 |
| R8 | Ліміти обходять ротацією телефонів | ліміт на заклад/годину й IP; captcha/integrity в enforce; моніторинг |
| R9 | Кодові шляхи `beauty_payments`, що припускають `appointment_id NOT NULL` | лише `EfBeautyStore.Booking.cs` (перевірено); entity `Guid?`; тест регресії запису |
| R10 | Різночитання статусу (`pending_payment` vs ефективний `expired`) | єдина функція ефективного статусу в Application, використана публічним і CRM API; тест на межі часу |

---

## 7. Human gates

1. **Прийняття ADR-026 і схеми §3** (нові таблиці, зміна CHECK append-only журналу, `beauty_payments.appointment_id` → nullable) — до старту задачі БД.
2. **Приватність ПД гостя:** склад полів (ім'я, телефон), прив'язка до клієнта після оплати, видимість у CRM, строк зберігання (P1) — до ввімкнення на staging з реальними тестерами.
3. **Міграція на staging/prod** і будь-який `Down` (бекап; `Down` відмовляє за наявних даних).
4. **Увімкнення `ProductOrders:Enabled=true` на staging** (mock) для конкретних tenant-ів і `WORKER_PRODUCT_ORDERS_ENABLED=true`.
5. **Prod-вмикання продажу:** вибір платіжного провайдера (A3, окремий ADR), ПРРО (L1) і ПДВ (L2, ADR-025 §7.2), юридичний текст умов продажу/повернення в застосунку (P3). До закриття — prod-запобіжник.
6. **Мердж `integrate/*`** після security-review.

---

## 8. План міграції й розгортання

1. Gate 1 → міграція `beauty_product_orders` (dev), GRANT у двох файлах, `schema.sql` (без BOM, LF), тести БД.
2. Backend: `IPaymentService` + mock, публічний контролер, CRM-маршрути, фіналізація/компенсація/reconcile, prod-запобіжник. `ProductOrders:Enabled` відсутній у всіх `appsettings*` (тобто вимкнено); dev вмикає через `scripts/dev/` (env), staging — через env сервера після gate 4.
3. Worker: `pollProductOrders` за прапором (за замовчуванням вимкнено).
4. Frontend CRM (mock → http) і мобільний клієнт (інша команда, за §33) паралельно від контракту.
5. QA → security-review → docs → gate 3/4 на staging (93.127.143.98 запущено з іншої гілки — спершу інтеграція).
6. Prod: міграцію можна застосувати (адитивна, прапор вимкнено, запобіжник), продаж лишається вимкненим до gate 5.
Відкат коду: вимкнути прапор (замовлення в `pending_payment` закриваються за часом; оплачені видаються/повертаються в CRM, бо CRM-маршрути від прапора не залежать). Відкат схеми — лише за порожніх таблиць.

---

## 9. Відкриті питання

| # | Питання | Дефолт у контракті |
|---|---|---|
| P1 | Строк зберігання ПД (`guest_name`, `guest_phone`) неоплачених/прострочених/скасованих замовлень | **Рішення користувача 2026-11-20: зберігати без очищення** (знеособлення воркером через 90 днів відхилено на цей етап; повернутись можна окремою задачею з колонковим GRANT) |
| P2 | Повернення після видачі (товар повернули в заклад) | Поза 3a; вручну поза системою (оплату повертає провайдер у кабінеті, товар — надходження/інвентаризація) |
| P3 | Юридичні умови продажу й повернення в застосунку (публічна оферта, права споживача) | Блокує prod (gate 5) |
| P4 | Чи потрібне сповіщення персоналу про нове оплачене замовлення (бейдж є; Telegram власнику — ні) | Лише лічильник `paid` у меню |
| P5 | Чи показувати в картці клієнта CRM його покупки | Ні в 3a |
| P6 | TTL резерву 15 хв і ліміти (10 шт/рядок, 20 рядків, 50 000 грн, 2 активні на телефон) | Значення з §33.3 (налаштовуються, крім кількостей) |
| P7 | Чи можна оплачувати замовлення, якщо для tenant вимкнено `beauty_booking` (а склад увімкнено) | Ні: без `beauty_booking` tenant публічно не існує |

---

## 10. Scope of truth

Після `accepted` ADR-026 авторитетний для: обсягу етапу 3a; умов вмикання (модуль + прапор + prod-запобіжник); вибору tenant-за-slug + RLS для публічних маршрутів замовлень; моделі резерву (похідний від рядків активних замовлень, без лічильника); життєвого циклу й ефективного статусу замовлення; моменту списання (оплата) і моделі повернення (`sale_return` у ті самі партії); алгоритму оплати, компенсації й reconcile; узагальнення `IPaymentService` (`PaymentCharge`, ідемпотентність за `PaymentId`); правил ПД гостя й прив'язки клієнта після оплати; прав CRM без нових ключів; ролі воркера (лише косметичний TTL).

Не авторитетний для: RLS (ADR-002), прав (ADR-023), FEFO/журналу/дефіциту (ADR-025 + §32), вибору провайдера й ПРРО (майбутні ADR). Поля, коди, ліміти, тести — контракт §33.

Потребує нового ADR: реальний провайдер і зміна кроку оплати; касовий продаж і `inventory.sales` (3b); override цін по закладах; доставка; часткові повернення й повернення після видачі; знижки на товари; повідомлення клієнту про статус.

---

## 11. Задачі (орієнтовні ID; PM підтверджує)

Таблиця з агентами, обсягом і залежностями — контракт §33.20. DAG: 900 → [gate 1] → 901 → {902 ∥ 904} → 903 → 906 → 907 → 908; 905 (CRM frontend) — mock після 900, http після 903; мобільний клієнт — інша команда від `API-CONTRACT-PRODUCT-ORDERS-V1`.

---

## 12. References

- `backend/BeautyCrm.Application/Features/BeautyBooking/IPaymentService.cs`, `BookingService.cs` (оплата запису), `CancellationService.cs` (claim → refund → компенсація)
- `backend/BeautyCrm.Application/Features/BeautyPublicBooking/PublicTokens.cs` (`PublicTenantResolver`, токени), `backend/BeautyCrm.Api/PublicBooking/PublicBookingSetup.cs` (`[PublicClient]`, `[PublicTenant]`, rate limit), `Controllers/PublicBookingController.cs`
- `backend/BeautyCrm.Infrastructure/Data/Beauty/EfBeautyStore.Booking.cs` (`ResolveClientAsync`, платежі), `EfBeautyStore.InventoryOps.cs` (FEFO, блокування)
- Міграція `20261007115130_add_beauty_schema.cs` (`beauty_payments`)
- `deploy/staging/db/grants.sql`, `scripts/dev/setup-db.ps1`, `worker/src/scheduler/pollers.ts`
- `mobile/src/api/catalog.ts` (патерн клієнта)
- ADR-002, ADR-003, ADR-018 D2, ADR-022, ADR-023, ADR-025 (§2.4, §2.8, §7.2, §11); контракт §11, §12, §27.5, §29.4–29.7, §30.6, §32
