# Beauty CRM — Доменна модель

**Current version:** 0.3 (TASK-691/697, 2026-10-08)

---

## 1. Основні сутності

### Структурні одиниці (каталог)

- **Location** — заклад (салон, барбершоп). Містить таймзону (`timezone` за IANA, валідована), адресу, телефон. Деактивація (is_active=false) блокується, якщо є майбутні pending/confirmed записи. Видалення немає — лише м'яка дез активація.
- **Specialist** — майстер (перукар, косметолог). Може працювати в кількох закладах. Поля: full_name, title, phone, email, photo_url, position (новий, TASK-691). Деактивація (is_active=false) в одній трансакції: користувач (якщо є, крім owner) — is_active=false, refresh-токени відкликані, pending запрошення — revoked.
- **SpecialistLocation** — зв'язок майстра з закладом + графік роботи (`working_hours` — JSON, JSONB: per IANA-таймзона закладу, формат {mon: [{from: "09:00", to: "18:00"}, ...], ...}). Деактивація = is_active=false при видаленні з закладу (графік зберігається). Перевірка: майбутні active записи блокують видалення.
- **SpecialistService** — призначення послуги до майстра (M2M). Порожна множина = невидимий майстер в слотах. При create слоти перевіряють наявність послуги; при PATCH запису теж.
- **Service** — послуга (стрижка, фарбування) з тривалістю в хвилинах (>0). Статус is_active.
- **ServicePrice** — ціна послуги. Може бути мережевою (location_id NULL) або override закладу. UNIQUE (tenant_id, service_id, location_id).

### Записи & клієнти

- **Appointment** — запис на послугу (візит клієнта до майстра в закладі у часовому проміжку).
  - **Статуси:** `pending` (новий) → `confirmed` (підтверджено) → `completed` (завершено) або `cancelled` / `no_show` (скасовано/не прийшов).
  - **Період:** `starts_at` (UTC) + `duration_minutes` → `ends_at` (обраховується тригером БД, read-only).
  - **Вартість:** `price_original` (без знижок), `price_final` (після акцій).
  - **Джерело:** `admin` (адмін), `online` (веб), `telegram` / `instagram` (канали).
  - **Оплата:** `payment_method` (card / cash), окремий рядок у Payment з статусом (pending/paid/failed/refunded).
  - **Скасування (TASK-697):** `cancelled_at` (коли скасовано), `cancelled_by_type` (client/staff/system), `cancelled_by_user_id` (для staff; NULL для client/system), `cancel_reason` (текст до 300 сим., необов'язковий). Ніяких даних не видаляються. Видимість: staff/owner бачать всі поля; specialist не бачить name автора й reason. Публічний API не розкриває.
  - **Публічний API (TASK-688):** `public_token_hash` (SHA-256 base64url(HMAC), 43 символи, недослідовний), для перегляду без JWT. Ідемпотентність за `idempotency_key_hash` (SHA-256) + `idempotency_request_hash` (SHA-256 тіла).
  - **Видимість слотів:** Майстер без активного графіка / без послуги не з'являється. День з approved відсутністю пропускається.
- **Client** — клієнт (full_name, phone нормалізований E.164 або NULL, email, birth_date). M'яка видалення (deleted_at). Поля: marketing_consent (false за замовч., для сегментації), unsubscribed (false за замовч., для розсилок). Дедупліка по нормалізованому телефону в межах tenant; старе ім'я **не** перезаписується.
- **ClientNote** — нотатка до клієнта (автор user_id, текст). Автор + керівники можуть читати; інші (specialist) не бачать.
- **Payment** — платіж за запис (amount, method: card/cash, status: pending/paid/failed/refunded, provider, provider_payment_id, paid_at, refunded_at).
- **Reminder** — нагадування (за 1h / 2h до запису, обробляється worker).

### Відсутності (TASK-691, TASK-696)

- **Absence** — період відсутності майстра (тип: sick/vacation/day_off/other, дати з-по включно, локальна дата за закладом).
  - **Статуси:** `requested` (від specialist, на затвердження) → `approved` (адмін дозволив) / `rejected` (адмін відхилив) → `cancelled` (скасовано).
  - **Видимість:** Тип бачить увесь staff; примітка (note) — лише owner/admin + автор. Specialist колег бачить лише approved.
  - **Конфлікти:** При create/approve вибираються future pending/confirmed записи цього майстра, чий локальний день закладу падає в період. Перевіряються під advisory lock до тої ж трансакції, щоб уникнути race condition запис↔відсутність.
  - **Ліміти:** На одного specialist max 10 активних requested (approved не рахуються); rate limit 20/хв per user.
  - **Блокування слотів:** День з approved відсутністю не видає слоти; запис в такий день — 409 specialist_unavailable (staff API) → 409 slot_unavailable (публічний API).

### Акції

- **Promotion** — акція/знижка (%, фіксована сума) зі скидом на послуги/локації. Період (starts_at, ends_at). Статус is_active.
- **PromotionLocation**, **PromotionService** — розподіл акції по закладам/послугам (порожня = усі).

### Канали & комунікація

- **Channel** — інтеграція з каналом (telegram, instagram, viber, whatsapp, facebook, widget). Секрети зашифровані (AES-256-GCM, Channels__EncryptionKey). Credentials JSON: {token, webhookSecret, appSecret (Instagram HMAC, TASK-689), verifyToken (Instagram GET, TASK-689)}. Поле settings (JSONB) для не-секретних параметрів.
- **Conversation** — чат/тред між закладом й клієнтом у каналі (external_chat_id від каналу, який унікален per channel).
- **Message** — повідомлення (direction: inbound/outbound, sender_type: client/assistant/specialist/system, status: received/draft/pending/sent/failed, attempts, last_error). Ідемпотентність за idempotency_key (унікальний per tenant).
- **AiAction** — дія AI (tool_name, payload JSONB, status: pending_confirmation/executing/done/rejected/reverting, result JSONB, error). Статус-машина атомарна (compare-and-swap). Audit: confirmed_by_user_id, confirmed_at, executed_at.

### Управління кампаніями (не реалізовано, резерв)

- **Campaign** — розсилка на сегменту. Таблиця не створена. Поки ручна розсилка через механік расписания.
- **Audience** — сегмент клієнтів (фільтри: lokacija(s), service(s), last_visit_from/to, marketing_consent, unsubscribed). Не реалізовано.

---

## 2. Статуси запису (Appointment.Status)

```
pending      → новий запис, потребує підтвердження (для cash завжди pending; для card може быть confirmed після оплати)
confirmed    → підтверджено, буде надіслано нагадування
completed    → завершено (після часу starts_at + duration, або явна марка адміном)
cancelled    → скасовано (є дані про автора, час, причину; залишається в БД)
no_show      → записаний, але клієнт не прийшов (без явки)
```

**Переходи:**
- нове створення → `pending` (staff API) або `pending`/`confirmed` (публічний, залежно від оплати)
- `pending` → `confirmed` (адмін підтверджує, або система після оплати карткою)
- `confirmed` / `pending` → `completed` (адмін, після часу)
- `confirmed` / `pending` → `no_show` (адмін, якщо клієнт не прийшов)
- `pending` / `confirmed` → `cancelled` (на запит; перевірка скасування за часом до starts_at, політика повернення)
- `cancelled` / `no_show` / `completed` → інші тільки під명령명 адміна (додаткові переходи на власний ризик)

**Видимість в UI:** За замовч. GET /appointments без скасованих (includeCancelled=false); вмикаючи прапорець бачать cancelled з деталями (хто/коли/чому). Overview містить усі статуси. Аналітика лише completed.

---

## 3. Скасування (TASK-697)

### Дані

- **cancelled_at** — коли скасовано (UTC)
- **cancelled_by_type** — хто скасував: `client` (публічний токен), `staff` (адміністратор/спеціаліст), `system` (AI revert або збій оплати при створенні)
- **cancelled_by_user_id** — для staff: ID користувача у таблиці users; для client/system = NULL
- **cancel_reason** — текст від 0 до 300 символів (trim, порожнє = NULL)

### Видимість (DTO.ForViewer)

| Поле | owner/admin | specialist (if own) | client (public API) |
|------|---|---|---|
| cancelledAt | ✓ | ✓ | ✗ |
| cancelledBy.type | ✓ | ✓ | ✗ |
| cancelledBy.name | ✓ | ✗ | ✗ |
| cancelReason | ✓ | ✗ | ✗ |

### Політика повернення коштів (TASK-685)

**Розрахунок (за налаштуванням tenant, або default):**
```
time_until = starts_at - now
if time_until <= windowHours:
  refund_percent = refund_percent_in_window
else:
  refund_percent = refund_percent_outside
fee = (deduct_fee ? fee_percent : 0)
refund_amount = round_down(paid * refund_percent / 100 * (100 - fee) / 100)
```
Округлення **вниз до копійок** (за правилами мат. до нуля). Якщо refund_amount = 0, виклик RefundAsync не робиться.

**Параметри (GET/PUT /beauty/settings/cancellation):**
- `windowHours` — кількість годин до星期開時, протягом яких діє спеціальний відсоток (0..720, за замовч. 12)
- `refundPercentInWindow` — % від вартості, якщо скасовано в вікні (0..100, за замовч. 50)
- `refundPercentOutside` — % від вартості, якщо скасовано поза вікном (0..100, за замовч. 100)
- `deductFee` — ліцензійна комісія платіжної системи (за замовч. false)
- `feePercent` — розмір комісії (0..100, за замовч. 0)

**Видимість в UI:** Кожен слот (GET /slots) у полі `cancellation` несе ці параметри; AppointmentDto також.

---

## 4. Специалист профил & деактивация (TASK-696)

**При is_active=false:**
- Користувач (якщо пов'язаний, крім owner) — is_active=false (не робиться auto-enable при реактивації профілю)
- Всі refresh-токени користувача — revoked_at = now
- Всі pending запрошення з цим specialist_id → status=revoked
- Слоти для цього майстра не видаються (ні в публічному, ні в staff API)
- Наявні active записи (pending/confirmed) залишаються (не видаляються); вони позначаються як від неактивного майстра, адмін мав би їх перенести

**Специ́альні ендпоінти для неактивного користувача:**
- `GET /appointments`, POST/PATCH, `/cancel` → 403 specialist_inactive
- `POST /absences`, `/approve`, `/reject`, `/cancel` → 403 specialist_inactive
- Інші ендпоінти (каталог, клієнти) — доступні (поки access-token не закінчився)

**Публічний API:** За словником «спеціаліст» вибираються тільки активні профілі з графіком і послугами.

---

## 5. Деактивация заклада (TASK-697)

**При is_active=false:**
- Слоти в цьому закладі не видаються
- Запис в закритий заклад — 422 specialist_not_at_location
- Наявні записи не чіпаються
- Деактивація блокується (409 has_future_appointments), якщо є pending/confirmed записи
- Зміна таймзони теж блокується (409 timezone_locked)

---

## 6. Напрямок повідомлень (Message.Direction, SenderType)

- **Inbound:** клієнт → платформа (webhook від каналу)
- **Outbound:** платформа → клієнт (AI-відповідь, нагадування, сповіщення)
- **SenderType:** `client`, `assistant` (AI), `specialist` (майстер відповідає), `system` (автоматичне сповіщення)

---

## 7. AI-режими

- **`suggest`** — AI пропонує дію (status=proposed), не виконує. Адмін переглядає й схвалює/відхиляє.
- **`confirm`** (за замовч.) — AI пропонує, потім виконує лише низькоризикові (find_slots, get_prices, draft_reply).
- **`auto`** — AI виконує все (потребує явної прив'язки per tenant)

**Статус-машина (M2, TASK-689):**
- `pending_confirmation` —> approve —> `executing` —> (успіх) `done` або (помилка) `executing` —> `pending_confirmation`
- `pending_confirmation` —> reject —> `rejected`
- `done` —> revert —> `reverting` —> (успіх) `done` або (помилка) `reverting` —> `done`
- Атомарна транзакція: UPDATE ... WHERE result->>'Status' = from (compare-and-swap)

---

## 8. Сегментація клієнтів для розсилок (не реалізовано)

Потенційні фільтри (структура для майбутньої таблиці Audience):
- **Розташування(я):** client наявний у закладі (мінімум один запис)
- **Послуга(и):** останній запис була ця послуга
- **Історія:** активні (підтверджено й не завершено), колишні (усі завершені), нові (перший запис за останній місяць)
- **Дата останнього візиту:** від / до
- **Обмеження:** `marketing_consent = true` AND `unsubscribed = false`
- **Часові вікна:** розсилки 09:00–20:00 за локальною таймзоною (per location), max знижка 20%

---

## 9. Таймзони й локалізація часу

- Кожний **Location** має `timezone` — валідна IANA-зона ("Europe/Kyiv", "America/New_York" тощо; Windows-ідентифікатори відхиляються).
- **WorkingHours** (JSONB) — per SpecialistLocation, локальний час для таймзони закладу. Формат: `{mon: [{from: "09:00", to: "13:00"}, {from: "14:00", to: "18:00"}], tue: [...], ...}`; відсутні дні = вихідні.
- **Розрахунок слотів:** DateOnly (локальна дата закладу) + часовий пояс → UTC для порівняння з `starts_at`.
- **Запис:** `starts_at` завжди у UTC (DateTimeOffset).
- **Absence dates:** `date_from`/`date_to` — локальні дати закладу (DATE без часу); перевірка: день у локальній зоні перетинається з EXCLUDE constraint.
- **AppointmentDto / FreeSlot:** Поле `timezone` — IANA-зона закладу; UI має показувати `startsAt` / `label` у цій зоні.

---

## 10. Модулі функціоналу (per tenant)

Контролюються `Tenant.modules[]`, активуються через [RequireModule] на ендпоінтах:

- `beauty_booking` — слоти, запис, отримання, скасування, налаштування скасування, відсутності.
- `beauty_catalog` — послуги, ціни, акції, превью ціни.
- `beauty_clients` — картка клієнта, нотатки, історія.
- `beauty_analytics` — звіти й метрики.
- `beauty_channels` — налаштування каналів, вхідні webhook.
- `beauty_ai` — журнал AI-дій, підтвердження/відхилення.

**Поведінка:** Якщо модуль вимкнений — ендпоінт повертає `403 module_disabled`; login/refresh → `401`.

---

## 11. Ролі користувачів

- **`platform_operator`** — створення tenant (API-ключ `.env`, не користувач у БД)
- **`owner`** — власник tenant (один на tenant, не може бути видалено; керує адмін/спеціалістами, контрактами)
- **`admin`** — адміністратор; керує спеціалістами, каталогом, каналами, звітами, але не власником
- **`specialist`** — майстер; бачить/змінює лише власні записи, може запрошувати на власну відсутність (requested)

**Матриця доступу:**
- Booking endpoints: читання — усі staff; зміни — owner/admin; specialist — лише власні + can_create_for_self
- Catalog: читання — усі; зміни — owner/admin
- Clients/Analytics/Channels/AI: owner/admin
- Staff management: owner/admin керують; specialist читає довідник (без телефону)
- Platform API: тільки platform_operator (X-Platform-Key)

---

## 12. Ідемпотентність

### Вхідні повідомлення (webhook)

- **IdempotencyKey** на Message — унікально per tenant, напр. `{channel}:{channelId:N}:{externalMessageId}` (TASK-689 L3: dodano channelId:N для Telegram, щоб не мішати різних ботів одного tenant).

### Публічний запис (TASK-688)

- **Idempotency-Key** заголовок (обов'язковий, 16-128 символів [A-Za-z0-9._:-]) — повтор того ж ключа + те саме тіло → той самий результат (200 + Idempotent-Replayed: true).
- Хеші в БД: `idempotency_key_hash` (SHA-256 ключа), `idempotency_request_hash` (SHA-256 тіла).
- Інше тіло, той самий ключ → 422 idempotency_key_reused.

### Worker job'и

- Ключ за типом подій (напр. `reminder:{appointmentId}:{offset}`, `outbox:message_id`); BullMQ `attempts: 3`.

---

## 13. Видалення даних

- **Soft delete:** `Client.deleted_at` (історія збережена, схована з UI, виключена з сегментів).
- **Cascade:** дочірні рядки (ClientNote, Appointment, Message, Reminder, Payment, Absence) видаляються при видаленні батька.
- **Appointment:** ніяких даних не видаляються при скасуванні; лишаються з cancelled_at + деталями.
- **PII:** клієнти зберігаються для історії, але з `deleted_at` позначені; паролі — bcrypt; сесійне кеш не зберігається.

---

## 14. Обмеження, які перевіряє БД

- **Exclusion constraint:** `ex_beauty_appointments_specialist_no_overlap` — спеціаліст не може мати 2 pending/confirmed записи, що перетинаються.
- **Exclusion constraint:** на `beauty_specialist_absences` — відсутність не може перетинатися з іншою (requested/approved).
- **Check constraints:** статус, тип источника, напрямок повідомлення, тип платежу, формат таймзони, дієапазони для скасування
- **Composite foreign keys:** (tenant_id, ID) для всіх кросс-таблич références.
- **Partial unique indexes:** public_token_hash, idempotency_key_hash (дозволяють NULL).

---

## 15. Інтеграційні точки (не реалізовано)

- **Платіжна система:** `IPaymentService` port; Stripe/2Checkout/PayPal потім.
- **SMS/Email:** `IChannelAdapter` для SMS & Email (поки немає).
- **CAPTCHA:** `ICaptchaVerifier` hook (поки fail-closed).
- **AI:** LLM integration (поки mock).
