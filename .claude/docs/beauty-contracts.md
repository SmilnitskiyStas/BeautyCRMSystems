# Beauty CRM — контракти (v0.1, чернетка)

Єдине джерело для паралельних агентів. Деталі полів уточнює `database-engineer` / `backend-developer` і версіонують через `create-contract`.

## 0. Відповідність ролей промпту й репозиторію
| Промпт | Агент у `.claude/agents/` |
|---|---|
| database-engineer | `database-engineer` |
| backend-developer | `backend-developer` |
| integrations-developer | `integrations-developer` (новий) |
| ai-agent-developer | `ai-agent-developer` (новий) |
| worker-developer | `backend-developer` + skill `beauty-worker-jobs` |
| frontend (адмін / запис) | `frontend-developer` + `beauty-admin-ui` / `beauty-booking-ui` |
| qa-tester | `qa-engineer` + `beauty-qa` |
| security-reviewer | `security-reviewer` + `beauty-security` |
| documentation-writer | `documentation-writer` + `beauty-docs` |

## 1. Рішення gate
Стек Next.js + ASP.NET Core 8 + PostgreSQL 16 (RLS); скасування ≤12 год → 50%; канали: Telegram + Instagram (mock); AI-режим `confirm`; оплата через `IPaymentService`.

## 2. Сутності (tenant_id + RLS на всіх)
`beauty_locations`, `beauty_specialists`, `beauty_specialist_locations`, `beauty_services`, `beauty_service_prices`, `beauty_appointments`, `beauty_clients`, `beauty_client_notes`, `beauty_promotions` (+`_locations`, `_services`), `beauty_channels`, `beauty_conversations`, `beauty_messages`, `beauty_ai_actions`, `beauty_reminders`, `beauty_payments`.

`beauty_appointments`: `id`, `tenant_id`, `location_id`, `specialist_id`, `service_id`, `client_id`, `starts_at`, `duration_minutes`, `status` (pending|confirmed|completed|cancelled|no_show), `source` (admin|online|telegram|instagram), `price_original`, `price_final`, `promotion_id`, `reminder_option` (none|1h|2h), `payment_method` (card|cash).

## 3. API (`/api/beauty/...`, `[RequireModule("beauty_*")]`)
| Метод | Шлях | Призначення |
|---|---|---|
| GET | `/slots?locationId&specialistId&serviceId&date` | Вільні слоти з урахуванням тривалості й графіка |
| POST | `/appointments` | Створити запис (body: location, specialist, service, startsAt, client, reminder, paymentMethod) |
| PATCH | `/appointments/{id}` | Перенести / змінити статус |
| POST | `/appointments/{id}/cancel` | Скасувати; відповідь містить `refundAmount` (50% якщо ≤12 год) |
| GET/POST/PUT | `/services`, `/services/{id}/prices` | Послуги й ціни (мережа + override закладу) |
| GET/POST/PUT | `/promotions` | Акції; `GET /promotions/preview` — перерахунок цін |
| GET/POST | `/clients`, `/clients/{id}` | Клієнти, історія, нотатки |
| GET | `/analytics/network`, `/analytics/locations`, `/analytics/promotions` | Аналітика |
| GET/PUT | `/channels` | Налаштування каналів (секрети маскуються) |
| POST | `/webhooks/{channel}` | Вхідні повідомлення (підпис обов'язковий) |
| GET/POST | `/ai/actions`, `/ai/actions/{id}/approve|revert` | Журнал і підтвердження дій AI |

Помилки: типізовані (`409` перетин слоту, `422` валідація). Деталі request/response уточнює агент 2 і оновлює цей файл.

## 4. Події черги (BullMQ)
`beauty.reminder.send {appointmentId, offset}`, `beauty.outbox.send {messageId}` (ретраї ×3), `beauty.campaign.run {campaignId}`, `beauty.winback.run`, `beauty.review.request {appointmentId}`. Усі ідемпотентні за ключем повідомлення.

## 5. Інтерфейси
```csharp
public interface IChannelAdapter {
  string Channel { get; }
  bool VerifySignature(HttpRequest req, string body);
  Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body);
  Task SendAsync(OutboundMessage msg, CancellationToken ct);
}
public interface IPaymentService {
  Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct);
  Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct);
}
```

## 6. AI tools
`find_free_slots`, `create_appointment`, `get_prices`, `get_active_promotions`, `get_client_context`, `draft_reply`, `suggest_promotion`, `build_audience`. Режими: `suggest` | `confirm` (за замовчуванням) | `auto`. Кожна дія → `beauty_ai_actions {id, action, target, createdAt, status, revertible}`.

## 7. Структура коду (scaffold)
- Backend: `backend/BeautyCrm.{Domain,Application,Infrastructure,Api,Tests}` (.NET 8, namespace `BeautyCrm.*`; у промпті `ShelfGuard.*` читати як `BeautyCrm.*`).
- DI: кожна зона має власний `Add*` у своєму файлі (`AddBeautyData`, `AddBeautyChannels`, `AddBeautyAi`, `AddBeautyApplication`); `Program.cs` вже їх викликає, **не редагувати `Program.cs`**.
- Frontend: `frontend/` (Next.js App Router + Tailwind + React Query + Zustand + Zod). Адмін: `app/(dashboard)/beauty/`, `features/beauty-admin/`. Запис: `app/(public)/book/`, `features/beauty-booking/`. Спільні `package.json` і кореневий `layout.tsx` не змінювати без головної сесії.
- Worker: `worker/src/jobs/beauty-*`.

## 8. Підтверджені рішення (Хвиля B)
- Webhook: `POST /api/beauty/webhooks/{channel}/{channelId}`, `channelId` = `beauty_channels.id`; tenant визначається за каналом.
- «Ретраї ×3» = 3 спроби загалом (BullMQ `attempts: 3`, outbox аналогічно).
- Instagram поза 24-годинним вікном Meta: повідомлення відхиляється, менеджер бачить, що вікно закрите.
- AI/розсилки: макс. знижка 20%, години розсилок 09:00–20:00, значення в налаштуваннях закладу (за замовчуванням ці). Worker привести до того ж вікна.
- Скасування: ≤12 год до візиту → повернення 50%.
- Wave B дозволено змінювати `Program.cs` (middleware tenant, реєстрація портів) і реалізовувати порти з Wave A у `Infrastructure`.

## 9. Реалізація API (TASK-675, v0.2)
Базовий шлях `/api/beauty`. Помилка = `{ "code", "message" }`; `422` валідація, `404`, `409` (`slot_unavailable` — перетин, `already_cancelled`, `appointment_closed`, `phone_exists`), `402` (`payment_failed`/`refund_failed`), `401 tenant_required`, `403 module_disabled`. Тіла/відповіді — camelCase JSON; `status/source/reminder/paymentMethod` — рядки з §2.

| Метод | Шлях | Тіло / відповідь |
|---|---|---|
| GET | `/slots?locationId&serviceId&date[&specialistId]` | `[{specialistId, startsAt, endsAt, label}]`; крок 15 хв, `date` — локальна дата закладу; без `specialistId` — усі майстри закладу |
| POST | `/appointments` | `{locationId, specialistId, serviceId, startsAt, client:{id?|name,phone,email?}, reminder, paymentMethod, source?}` -> `201 AppointmentDto` (`source` за замовч. `online`) |
| GET | `/appointments?from&to&locationId&specialistId`, `/appointments/{id}` | календар (за замовч. 7 днів від сьогодні UTC) |
| PATCH | `/appointments/{id}` | `{startsAt?, status?}`; status ∈ confirmed\|completed\|no_show; скасування лише `/cancel` |
| POST | `/appointments/{id}/cancel` | `{appointment, refundAmount, refundPercent}`; >12 год — 100%, ≤12 год — 50% (A2; 100% для >12 год — припущення) |
| GET/POST | `/services`, PUT `/services/{id}`, GET/PUT `/services/{id}/prices` | PUT prices: `{locationId?, price}` (null = мережева, інакше override закладу) |
| GET/POST/PUT | `/promotions`, GET `/promotions/preview?locationId[&promotionId&at]` | `{name, discountType percent\|fixed, discountValue, startsAt?, endsAt?, isActive, locationIds[], serviceIds[]}`; порожні списки = усі; з кількох акцій діє одна з найбільшою знижкою |
| GET/POST | `/clients?search&page&pageSize`, `/clients/{id}`, POST `/clients/{id}/notes` | картка = клієнт + нотатки + історія |
| GET | `/analytics/network\|locations\|promotions?from&to` | за замовч. 30 днів; revenue = сума `price_final` completed |
| GET/PUT | `/channels`, `/channels/{id}` | PUT: `{type?, name, locationId?, isActive, token?, webhookSecret?, settingsJson?}`; відповідь: `maskedToken` (`********`+last4), `hasWebhookSecret`, `webhookPath` |
| POST/GET | `/webhooks/{channel}/{channelId}` | публічний, підпис обов'язковий (401 без нього); GET — Meta hub.challenge |
| GET/POST | `/ai/actions`, POST `/ai/actions/{id}/approve\|reject\|revert` | `{content,isError,status}` |

- `specialist_locations.working_hours` (jsonb): `{"mon":[{"from":"09:00","to":"18:00"}],"tue":[...]}` (ключі mon..sun, час локальний для `locations.timezone`; відсутній день = вихідний).
- Модулі: `beauty_booking` (slots/appointments), `beauty_catalog` (services/promotions), `beauty_clients`, `beauty_analytics`, `beauty_channels`, `beauty_ai`.
- Tenant: claim `tenant_id` з JWT (TASK-684, §10); заголовок `X-Tenant-Id` лише в Development і лише без токена.
- Схема (міграція `beauty_messaging_and_consent`): `beauty_messages.status|attempts|last_error|idempotency_key` (unique per tenant), `beauty_clients.marketing_consent|unsubscribed`, policy `channel_webhook_lookup` (SELECT одного каналу за `app.channel_id`).
- Секрети каналів: AES-256-GCM, ключ `Channels__EncryptionKey` (base64, 32 байти); `credentials_encrypted` = JSON `{token, webhookSecret}`.

## 10. Автентифікація, tenants, ролі (TASK-684, v0.3)
Самореєстрації немає: tenant + власника створює оператор платформи, власник/адмін запрошують решту. Ролі: `owner`, `admin`, `specialist` (+ `platform_operator` — лише ключ із .env, не користувач). Помилка = `{ "code", "message" }`: `401` (немає/невірний токен, `invalid_credentials`, `invalid_token`), `403` (роль, `forbidden_role`, `module_disabled`), `404`, `409`, `422`, `423 account_locked`, `429 rate_limited`.

| Метод | Шлях | Доступ | Тіло / відповідь |
|---|---|---|---|
| POST | `/api/auth/login` | публічний, rate limit | `{tenant(slug), email, password}` -> `{accessToken, expiresInSeconds, refreshToken, user}` |
| POST | `/api/auth/refresh` | публічний, rate limit | `{refreshToken}` -> нова пара (ротація; повторне використання старого закриває всі сесії) |
| POST | `/api/auth/logout` | публічний | `{refreshToken}` -> 204 |
| POST | `/api/auth/invites/accept` | публічний, rate limit | `{token, fullName, password}` -> 201 `UserDto`; 404 `invite_invalid` |
| GET | `/api/auth/me` | owner/admin/specialist | `UserDto` |
| GET | `/api/users`, PATCH `/api/users/{id}/status` `{isActive}` | owner, admin | admin керує лише specialist; owner недоторканний; не себе |
| POST/GET | `/api/invites`, DELETE `/api/invites/{id}` | owner, admin | POST `{email, role: admin\|specialist, specialistId?}` -> 201 `{invite, token}`; owner запрошує admin/specialist, admin — specialist; `specialistId` обов'язковий для specialist; токен показується один раз |
| POST | `/api/platform/tenants` | заголовок `X-Platform-Key` | `{name, slug, modules?, owner:{email, fullName, password}}` -> 201 `{tenantId, name, slug, modules, ownerUserId}`; 409 `tenant_slug_taken` |
| PATCH | `/api/platform/tenants/{id}` | `X-Platform-Key` | `{modules?, status?: active\|suspended}` -> 204 |

- Access JWT (HS256, 15 хв): `sub`, `tenant_id`, `role`, `specialist_id` (для specialist). `Authorization: Bearer`. Refresh — непрозорий токен (14 днів), у БД лише SHA-256.
- Матриця beauty-ендпоінтів: booking (`slots`, `appointments*`) — усі ролі, specialist бачить/змінює лише записи свого `specialist_id` (чужий запис = 404, створення для чужого майстра = 403, `specialistId` у запитах підміняється власним); catalog — читання всі ролі, зміни owner/admin; clients, analytics, channels, ai — owner/admin; webhooks — публічні (підпис).
- Блокування: 5 невдалих входів -> 423 на 15 хв (навіть із правильним паролем); rate limit 10 запитів/хв на IP для login/refresh/accept/platform.
- Модулі: `tenants.modules`; призупинений tenant (`status=suspended`) -> `403 module_disabled`, логін/refresh -> 401.
- Схема (міграція `auth_tenants_users_invites`): `tenants`, `users` (унікальний email per tenant, `specialist_id` -> `beauty_specialists`), `invites`, `refresh_tokens`; RLS на users/invites/refresh_tokens (`tenant_isolation`), на tenants — `tenant_self` + SELECT-політика `tenants_login_lookup` (за транзакційним `app.tenant_slug`). Runtime-роль потребує GRANT на нові таблиці.

## 11. Політика скасування й повернення коштів (TASK-685, v0.4)
Налаштовується власником на рівні tenant (override закладу немає). **Замінює** константи «≤12 год -> 50%, >12 год -> 100%» з §3/§8/§9: це лише значення за замовчуванням. Відсутній рядок у БД = значення за замовчуванням.

| Метод | Шлях | Доступ | Тіло / відповідь |
|---|---|---|---|
| GET | `/api/beauty/settings/cancellation` | owner, admin, specialist (модуль `beauty_booking`) | `{windowHours, refundPercentInWindow, refundPercentOutside, deductFee, feePercent}` |
| PUT | `/api/beauty/settings/cancellation` | лише owner (admin/specialist -> 403) | те саме тіло, усі 5 полів обов'язкові -> 200 з збереженими значеннями |

- Значення за замовчуванням: `windowHours=12`, `refundPercentInWindow=50`, `refundPercentOutside=100`, `deductFee=false`, `feePercent=0`.
- Валідація (422, `{code,message}`): `windowHours` 0..720 (`invalid_window_hours`); обидва `refundPercent*` 0..100 (`invalid_refund_percent`); `feePercent` 0..100 завжди, навіть при `deductFee=false` (`invalid_fee_percent`); пропущене поле -> `settings_incomplete` (не підставляється 0).
- Розрахунок (`POST /appointments/{id}/cancel`): у вікні, якщо `startsAt - now <= windowHours` (межа включна, розпочатий візит теж у вікні) -> `refundPercentInWindow`, інакше `refundPercentOutside`. `refundAmount = paid * percent/100 * (100 - fee)/100`, де `fee = feePercent` лише при `deductFee=true`, інакше 0. Множення до ділення; округлення **до копійок вниз** (до нуля) — клієнту не повертається більше за розрахункове. Якщо `refundAmount = 0`, виклик `IPaymentService.RefundAsync` не робиться, платіж лишається `paid`.
- Відповідь `/cancel` — `{appointment, refundAmount, refundPercent, feePercent}` (`feePercent` новий, 0 коли комісії немає; `refundPercent` — відсоток вікна до комісії).
- Умови для UI запису (щоб не хардкодити текст): кожен елемент `GET /slots` і кожен `AppointmentDto` (`/appointments`, `/appointments/{id}`, відповіді POST/PATCH) містять `cancellation: {windowHours, refundPercentInWindow, refundPercentOutside, deductFee, feePercent}`; `feePercent` = 0, якщо `deductFee=false`. Поле адитивне.
- Схема (міграція `beauty_cancellation_settings`): таблиця `beauty_cancellation_settings` (`tenant_id` unique, CHECK на діапазони, RLS `tenant_isolation` ENABLE+FORCE). Runtime-ролі потрібен GRANT SELECT/INSERT/UPDATE на нову таблицю.

## 12. Публічний API онлайн-запису (TASK-688, v0.5)
Базовий шлях `/api/public/{tenantSlug}`. **Без JWT** (явний `[AllowAnonymous]` — виняток із «автентифікація за замовчуванням»), лише для tenant-ів із модулем `beauty_booking`. Помилка = `{ "code", "message" }`. Camel-case JSON.

| Метод | Шлях | Відповідь |
|---|---|---|
| GET | `/locations` | `[{id, name, address?, phone?, timezone}]` (лише активні) |
| GET | `/locations/{id}/specialists` | `[{id, name, title?, photoUrl?}]` (без email/телефону) |
| GET | `/locations/{id}/services` | `[{id, name, description?, category?, durationMinutes, priceOriginal, priceFinal, promotionId?, promotionName?}]`; `priceOriginal` = override закладу або мережева, `priceFinal` = після акції (найбільша знижка) на зараз |
| GET | `/slots?locationId&serviceId&date[&specialistId]` | як §9/§11 (`[{specialistId, startsAt, endsAt, label, cancellation}]`); `date` від вчора до `MaxDaysAhead` (180), інакше 422 `invalid_date` |
| POST | `/appointments` | `{locationId, specialistId, serviceId, startsAt, client:{name, phone}, reminder?: none\|1h\|2h (за замовч. none), paymentMethod: card\|cash, captchaToken?}` -> `201 {publicToken, appointment}`; повтор того ж `Idempotency-Key` -> `200` + `Idempotent-Replayed: true` |
| GET | `/appointments/{publicToken}` | `PublicAppointment` (див. нижче) |
| POST | `/appointments/{publicToken}/cancel` | `{appointment, refundAmount, refundPercent, feePercent}` за політикою §11 |

`PublicAppointment`: `{locationId, locationName, specialistId, specialistName, serviceId, serviceName, startsAt, endsAt, durationMinutes, status, priceOriginal, priceFinal, promotionId?, reminderOption, paymentMethod?, paymentStatus?, cancellation}`. **Немає** id запису/клієнта, імені, телефону, email, source. Запис завжди `source=online`.

**Tenant за slug.** Slug (3-64, `a-z0-9-`) -> tenant тим самим безпечним механізмом, що й логін: окреме з'єднання + транзакційний `app.tenant_slug` + вузька SELECT-політика `tenants_login_lookup` (один рядок). Далі всі запити під звичайним RLS (`app.tenant_id`); **нових політик немає**, RLS не розширено. Невідомий slug, призупинений tenant, вимкнений модуль, поганий формат slug — **однакова** `404 {code:"not_found"}`.

**Токен запису.** `publicToken` = base64url(HMAC-SHA256(ключ, tenantId.appointmentId)), 43 символи (256 біт), не послідовний. У БД лише SHA-256 (`public_token_hash`). Ключ: `PublicBooking:TokenKey` (base64, >= 32 байти) або похідний від `Auth:JwtSigningKey`. Невідомий / чужий (іншого tenant) / зіпсований токен -> **однакова** `404 not_found` (і GET, і cancel). Відповіді з токеном мають `Cache-Control: no-store`, `Referrer-Policy: no-referrer`.

**Створення запису.** Валідація (422): `idempotency_key_required` / `invalid_idempotency_key` (заголовок `Idempotency-Key`, 16-128 символів `A-Za-z0-9._:-`, обов'язковий), `invalid_request`, `invalid_name` (2-100), `invalid_phone`, `invalid_reminder`, `invalid_payment_method`, `invalid_start` (не на сітці 15 хв або далі `MaxDaysAhead`), `slot_in_past`, `outside_working_hours`, `captcha_failed`, `booking_limit_reached`; `409 slot_unavailable` (перетин), `402 payment_failed`. Оплата через `IPaymentService` (card -> Charge, paid/confirmed; cash -> confirmed). Телефон нормалізується до E.164 (`+380...`): приймається `+CC...`, `00CC...`, `0XXXXXXXXX` (10 цифр -> `+38`), `CC...` (11-15 цифр); решта 422. Клієнт дедуплікується за нормалізованим телефоном у межах tenant; ім'я наявного клієнта **не** перезаписується.
- Ідемпотентність: ключ зберігається як SHA-256 разом із відбитком тіла (`idempotency_key_hash`, `idempotency_request_hash`), унікальний у межах tenant. Той самий ключ і тіло -> той самий результат без повторного запису/списання; інше тіло -> `422 idempotency_key_reused`; паралельні повтори -> один запис (решта 200).
- CAPTCHA-хук: `ICaptchaVerifier.VerifyAsync(token, remoteIp)`; токен — заголовок `X-Captcha-Token` або `captchaToken` у тілі. `PublicBooking:CaptchaRequired=false` (за замовч.) вимикає перевірку. Провайдера не обрано: типова реалізація відхиляє все (fail closed) — при `true` потрібно зареєструвати власний `ICaptchaVerifier`.
- Захист на телефон: активних майбутніх онлайн-записів <= `PublicBooking:MaxActiveBookingsPerPhone` (3); створених за годину (будь-який статус) <= `MaxCreatesPerPhonePerHour` (5); перевищення -> 422 `booking_limit_reached`. Honeypot: непорожнє поле `website` у тілі -> 422 `invalid_request` без створення. `source` завжди `online` (поля source/status/price/clientId з тіла ігноруються). `client.marketingConsent` лише явне `true` і лише для нового клієнта (наявний профіль анонімом не змінюється; ім'я наявного клієнта не повертається й не перезаписується).
- Rate limit за IP (fixed window `PublicBooking:RateLimit:WindowSeconds`=60): читання каталогу/слотів 120, перегляд за токеном 30, POST create/cancel 10 (ключі `ReadPermit|TokenPermit|WritePermit`); перевищення -> `429 rate_limited`. За reverse proxy потрібні ForwardedHeaders із довіреними проксі.
- PII не логується (код публічного потоку нічого не логує); slug і токен у шляху — Kestrel/hosting не логує шлях на рівні Warning (`Microsoft.AspNetCore: Warning`); reverse proxy/access-логи треба налаштувати так, щоб не зберігати шлях `/appointments/{token}`.
- Схема (міграція `beauty_public_booking`, лише AddColumn/CreateIndex): `beauty_appointments.public_token_hash`, `idempotency_key_hash`, `idempotency_request_hash` (varchar(64), nullable) + partial unique-індекси `ux_beauty_appointments_public_token`, `ux_beauty_appointments_idempotency` на `(tenant_id, hash)`. Нових таблиць і GRANT-ів не потрібно.

## 13. Керування працівниками (TASK-691 backend, TASK-692 frontend)
Рішення користувача: (1) адмін створює профіль працівника, запрошення на вхід необов'язкове; (2) відсутність блокує нові слоти, наявні записи не чіпаємо автоматично, адмін бачить список конфліктів і сам переносить/скасовує (дані зараз демо, міграції можуть їх змінювати); (3) усі працівники бачать ТИП відсутності, текст примітки бачать лише owner/admin та автор; (4) працівник може створити ЗАПИТ на власну відсутність, адмін підтверджує.

**Таблиці** (tenant_id + RLS FORCE + композитні FK `(tenant_id, id)`, як решта):
- `beauty_specialists`: додати `phone`, `position` (text), `is_active`; зв'язок із `users.specialist_id` уже є.
- `beauty_specialist_services(tenant_id, specialist_id, service_id)` PK по трьох. Майстер пропонує ЛИШЕ призначені послуги. Міграція заповнює для наявних майстрів усі наявні послуги (щоб не зламати демо).
- `beauty_specialist_absences(id, tenant_id, specialist_id, type, date_from, date_to, status, note, requested_by_user_id, decided_by_user_id, decided_at, created_at)`; `type` ∈ sick|vacation|day_off|other; `status` ∈ requested|approved|rejected|cancelled; `date_from..date_to` — повні дні, включно, у часовій зоні закладу; CHECK date_to >= date_from; `note` ≤ 500 символів, це чутливі дані: НЕ логувати, не повертати без права.
- Графік: `beauty_specialist_locations.working_hours` (формат §9) вже є; редагується через API нижче.

**API** (`/api/beauty`, модуль `beauty_booking`, помилки `{code,message}`):
| Метод | Шлях | Права | Опис |
|---|---|---|---|
| GET | `/specialists` | staff | список із `services[]`, `locations[]` (графік), `isActive`; specialist бачить усіх як довідник (без phone) |
| POST | `/specialists` | owner, admin | `{name, phone?, position?, locationIds[], serviceIds[], workingHours?}`; створює профіль |
| GET/PUT | `/specialists/{id}` | owner, admin (GET: staff) | PUT: name, phone, position, isActive; деактивація не видаляє записи |
| PUT | `/specialists/{id}/services` | owner, admin | `{serviceIds[]}` повна заміна |
| PUT | `/specialists/{id}/schedule` | owner, admin | `{locationId, workingHours}` для закладу; валідація формату §9, 422 |
| POST | `/specialists/{id}/invite` | owner, admin | `{email}` → існуючий потік invites з role=specialist і прив'язкою до профілю; токен у відповіді, як у TASK-684 |
| GET | `/absences?from&to&specialistId?` | staff | календарний огляд: `{id, specialistId, type, dateFrom, dateTo, status}`; `note` лише owner/admin і автору, інакше поле відсутнє |
| POST | `/specialists/{id}/absences` | owner/admin: будь-кому (одразу `approved`); specialist: лише собі (статус `requested`) | `{type, dateFrom, dateTo, note?}`; відповідь містить `conflicts[]` (для керівників): `{appointmentId, startsAt, serviceName}`; 409 `absence_overlap` при перетині з активною відсутністю |
| POST | `/absences/{id}/approve` , `/reject` | owner, admin | з `approve` повертає `conflicts[]` |
| POST | `/absences/{id}/cancel` | керівник або автор запиту | статус cancelled |

**Слоти:** `SlotCalculator` і публічний API пропускають дні з `approved` відсутністю та послуги, не призначені майстру; майстер без активного графіка/послуг не з'являється в публічних списках. Створення запису на такий слот → 409 `specialist_unavailable`. Права: specialist НЕ редагує чужі профілі, графіки, послуги; admin не змінює owner.

### Уточнення реалізації (TASK-691 backend, v0.6)
Адитивні уточнення; рішення §13 не змінено.
- **Форма відповідей.** `SpecialistDto`: `{id, name, phone?, position?, photoUrl?, isActive, hasAccount?, services:[{id,name}], locations:[{locationId, locationName, isActive, workingHours}]}`; `phone` і `hasAccount` лише для owner/admin (для specialist поля відсутні). `workingHours` — об'єкт формату §9 або `null`. Помилки `{code,message}`; профіль чужого tenant = `404 specialist_not_found`.
- **Запити.** `POST /specialists`: `{name, phone?, position?, locationIds?, serviceIds?, workingHours?}` (`workingHours` застосовується до всіх `locationIds`; без закладів -> 422 `location_required`). `PUT /specialists/{id}`: `{name, phone?, position?, isActive?}` — повна заміна name/phone/position, `isActive` пропущено = без змін. `PUT /schedule`: `{locationId, workingHours}` (створює зв'язок майстра із закладом, якщо його немає; `{}` = без робочих днів). Валідація графіка суворa: ключі `mon..sun`, `HH:mm`, `from < to`, без перекриттів, <= 8 інтервалів на день; у БД зберігається нормалізований JSON; помилка -> 422 `invalid_working_hours`. Інші 422: `invalid_name`, `invalid_phone`, `invalid_position`, `location_not_found`, `service_not_found`.
- **Колонка `position`** окрема від `title`; у публічному API поле `title` = `position ?? title`.
- **Відсутності.** `AbsenceDto`: `{id, specialistId, type, dateFrom, dateTo, status, note?, conflicts?}` (дати `yyyy-MM-dd`; `note` та `conflicts` пропускаються, коли їх не можна віддати/не обчислено). `conflicts[]` — для owner/admin при створенні та `approve` (майбутні pending/confirmed записи майстра, чий ЛОКАЛЬНИЙ день закладу потрапляє в період); для запиту specialist — не віддається. `GET /absences`: за замовч. `from`=сьогодні UTC, `to`=from+31 день, максимум 366 днів (422 `invalid_range`); owner/admin бачать усі статуси, specialist — усі СВОЇ та лише `approved` колег. Створення: 403 `forbidden` для specialist, якщо `{id}` не його профіль (також для неіснуючого — існування не розкривається); 422 `invalid_type|invalid_dates|invalid_note` (період <= 366 днів, `dateFrom` не раніше ніж за рік, `dateTo` не пізніше ніж через 2 роки); 409 `absence_overlap` — перетин із `requested`/`approved` (гарантовано також exclusion constraint БД). `approve`/`reject`: лише з `requested` (інакше 409 `absence_not_pending`). `cancel`: керівник — з `requested`/`approved`; автор-specialist — лише поки `requested` (після рішення 403 `forbidden`); чужа відсутність для specialist = 404 `absence_not_found`; повторне -> 409 `absence_closed`.
- **admin не змінює owner:** профіль, прив'язаний до користувача owner/admin, змінює лише owner (403 `forbidden_role` для admin) — стосується PUT профілю, services, schedule, invite і створення відсутності.
- **Запис/перенос:** `POST /appointments` і `PATCH` (перенос) -> 409 `specialist_unavailable` при дні відсутності (`approved`, локальна дата закладу) або непризначеній послузі; пріоритет перевірок: `slot_in_past` -> `specialist_unavailable` -> `outside_working_hours` -> `slot_unavailable`. Деактивований майстер не віддає слотів; запис до нього -> 422 `specialist_not_at_location` (як і раніше).
- **Публічний API (адитивно):** `GET /locations/{id}/specialists[?serviceId]` і `GET /locations/{id}/services[?specialistId]`. «Придатний» майстер = активний профіль і зв'язок із закладом, у графіку закладу є хоча б один робочий інтервал, призначена хоча б одна активна послуга. `services` без `specialistId` — лише послуги, призначені хоча б одному придатному майстру закладу.
- **Схема** (міграція `beauty_staff_management`): `beauty_specialists.position`; `beauty_specialist_services` (PK `tenant_id,specialist_id,service_id`, композитні FK, cascade); `beauty_specialist_absences` (CHECK type/status/dates/note, композитні FK на specialists та users, `ex_beauty_specialist_absences_no_overlap` — EXCLUDE gist по `daterange(date_from,date_to,'[]')` для `requested`/`approved`); RLS ENABLE+FORCE і `tenant_isolation` на обох. Backfill: кожному наявному майстру призначено всі наявні послуги його tenant (на час backfill знімається і повертається FORCE RLS на `beauty_specialists`/`beauty_services`). Runtime-ролі потрібен GRANT SELECT/INSERT/UPDATE/DELETE на дві нові таблиці.

## 14. Захист API (TASK-689, виправлення аудиту TASK-682) і прогалини адмінки (TASK-690) — v0.7
Адитивний розділ: §3–§13 не змінено; де поведінка змінилась, це сказано явно. Помилка = `{ "code", "message" }`.

### 14.1 Конфігурація (нові/змінені ключі)
| Ключ (env `__`) | За замовч. | Призначення |
|---|---|---|
| `ConnectionStrings:Default` | — | **Runtime**-рядок API. Роль має бути `NOSUPERUSER NOBYPASSRLS` без прав DDL. |
| `ConnectionStrings:Migrator` | — | Рядок для `dotnet ef database update` (роль-власник схеми). `DesignTimeBeautyDbContextFactory` бере його першим, потім `Default`. У runtime API не використовується. |
| `Beauty:AllowPrivilegedDbRole` | `false` | Лише Development: дозволяє superuser/BYPASSRLS-роль у runtime (з попередженням у логах). Поза Development ігнорується. |
| `Channels:UseMocks` | **`false`** (було `true`) | `true` дозволено лише в Development; в іншому середовищі API **не стартує**. |
| `Beauty:TrustedProxies` | порожньо | IP/CIDR довірених reverse proxy (масив `:0,:1` або рядок через кому). Порожньо = `X-Forwarded-*` ігноруються. |
| `Beauty:AllowedOrigins` | порожньо (Development: `http://localhost:3000`) | Точні origin адмінки для CORS (`scheme://host[:port]`). `*`, шлях, не-http(s) -> помилка старту. Порожньо = CORS вимкнено. |
| `Auth:RateLimit:RefreshPermitLimit` / `RefreshPerTokenPermit` / `RefreshPerTenantPermit` | 120 / 5 / 600 за вікно | Ліміти `POST /api/auth/refresh`: на IP, на токен, на tenant. `PermitLimit` (10) лишається для login/logout/accept/platform. |

### 14.2 Виправлення аудиту
- **H1 — роль БД.** При старті API (`IHostedService` `DbRoleGuard`) виконує `SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user` на `ConnectionStrings:Default` і **падає**, якщо `true` (виняток: Development + `Beauty:AllowPrivilegedDbRole=true`). Без рядка підключення перевірка пропускається (попередження); у Development недоступна БД теж лише попередження. `.env.example`: окремі `Default` (`beautycrm_app`) і `Migrator` (`beautycrm_owner`), без `postgres`. Runbook (`.claude/docs/runbook.md`) оновити має documentation-writer.
- **H2 — канали.** Mock-адаптери реєструються лише за `Channels:UseMocks=true` у Development. `MockChannelAdapter` без секрету каналу відхиляє підпис (літерал `"mock"` прибрано).
- **H3 — проксі й rate limit.** `UseForwardedHeaders` (X-Forwarded-For/Proto) лише з `Beauty:TrustedProxies`; ланцюг розбирається справа наліво до першого недовіреного адреса, спуфлений лівий елемент і XFF від недовіреного піра ігноруються. Rate limit login/accept/logout/platform — 10/хв на IP з довіреного ланцюга (кошик `login:`). Refresh — окрема політика `auth-refresh` (за замовч. 120/хв на IP, кошик `refresh:`, не витрачає login-кошик) + ліміт на токен (5/хв) і tenant (600/хв, з префікса токена) -> `429 rate_limited`.
- **M1 — скасування атомарне** (`CancellationService`, обидва шляхи: staff і публічний за токеном; AI revert теж). Порядок: розрахунок -> **умовний UPDATE** `status = cancelled WHERE status IN (pending, confirmed)` (`IBookingStore.TryClaimCancelAsync`) -> повернення коштів -> фіналізація (нагадування). Програвший claim отримує `409 already_cancelled` (або `422 cannot_cancel`/`404`) без повернення коштів. **Ключ ідемпотентності повернення = `payment.Id`** (`IPaymentService.RefundAsync(paymentId, ...)`; реальний провайдер має передавати його як Idempotency-Key). Якщо повернення не вдалося (`402 refund_failed`) — claim скасовується (`ReleaseCancelAsync`: статус повертається, запис лишається активним, платіж `paid`); якщо слот за цей час зайнято, запис лишається скасованим, а платіж `paid` — потрібна ручна обробка.
- **M2 — AI approve/reject/revert** через атомарний compare-and-swap статусу в журналі (`IAiActionJournal.TryTransitionAsync`, `UPDATE ... WHERE result->>'Status' = from`): `pending_confirmation -> executing` (approve), `-> rejected` (reject), `done -> reverting` (revert). Паралельні дублі отримують `action_not_pending` / `not_revertible`; дія виконується один раз. Якщо виконання кинуло виняток — статус повертається (`executing -> pending_confirmation`, `reverting -> done`) і виняток піднімається. `executing`/`reverting` — внутрішні статуси (в колонці `status` = `executed`).
- **M3 — lockout не розкриває існування акаунта.** Невідомий tenant/email після `Auth:MaxFailedAttempts` (5) спроб отримує **ту саму** `423 account_locked` (однакові `code` і `message`), що й справжній заблокований акаунт, на `Auth:LockoutMinutes`; заблокований ключ відповідає без bcrypt (як і справжній). Лічильник за ключем SHA-256(`slug` + нормалізований `email`) у пам'яті процесу (`LoginAttemptTracker`); при кількох інстанціях API кожна рахує окремо (спільний лічильник — окрема задача). Для наявних користувачів лічильник у БД (без змін).
- **M4 — tenant context.** Лишається **сесійним** (`set_config('app.tenant_id', ..., false)` при відкритті з'єднання). Транзакційний варіант відхилено: він вимагає однієї транзакції на запит, а наявні шляхи ловлять `23P01`/`23505` (перетин слоту, Idempotency-Key, колізія id каналу) і продовжують читати — у вже перерваній транзакції Postgres відхилить подальші запити. **Вимога до інфраструктури: прямі з'єднання або session pooling; PgBouncer у transaction pooling і Npgsql `Multiplexing=true` заборонені** (API при старті відхиляє `Multiplexing=true`). Npgsql скидає сесійний стан при поверненні з'єднання в пул (`DISCARD ALL`), а tenant перевстановлюється при кожному відкритті.
- **M5 — Instagram.** Окремі секрети в зашифрованих credentials каналу: `appSecret` — лише ключ HMAC `X-Hub-Signature-256` для POST; `verifyToken` — лише для GET handshake (`hub.verify_token`); `webhookSecret` для Instagram більше **не** використовується (раніше був і ключем HMAC, і verify token). Без `appSecret` підпис завжди відхиляється, без `verifyToken` handshake завжди `403`. Наявні Instagram-канали треба перезберегти з обома новими полями.
- **L3.** Ключ дедуплікації вхідних: `{channel}:{channelId:N}:{externalMessageId}` (було `{channel}:{externalMessageId}`; Telegram `chat_id:message_id` не збігається між ботами одного tenant). Повідомлення, збережені зі старими ключами, при повторній доставці дублюються один раз.
- **L4.** `PUT /api/beauty/channels/{id}`: колізія PK (id існує в іншому tenant, RLS його не показує) -> `409 channel_id_conflict`, чужий канал не змінюється й не розкривається.
- **L7.** `DraftReplyAdapter.SendAsync`: атомарний `draft -> sending` (умовний UPDATE) перед inline-відправкою. Воркер бере лише `pending`, тож повідомлення не йде двічі; повторний виклик програє claim (`draft_not_found`). Тимчасова помилка -> `pending` (повтор воркером), постійна -> `failed`, виняток до результату -> `pending`.
- **L8.** `GET /api/beauty/appointments`: діапазон `to - from` > 62 днів -> `422 range_too_large`; `to <= from` -> `422 invalid_range`.

### 14.3 CORS і заголовки безпеки
- CORS для адмінки: дозволені методи `GET, POST, PUT, PATCH, DELETE, OPTIONS`; заголовки `Authorization`, `Content-Type`, `Idempotency-Key`, `X-Captcha-Token`; відкритий `Idempotent-Replayed`; **без credentials** (cookies не потрібні). Preflight обробляється до автентифікації.
- Усі відповіді: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`. Поза Development додатково `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` і, на HTTPS-запитах (за довіреним `X-Forwarded-Proto`), `Strict-Transport-Security: max-age=31536000; includeSubDomains`.

### 14.4 Зміни контракту каналів (доповнення до §9)
`PUT /api/beauty/channels/{id}` приймає додатково `appSecret?`, `verifyToken?` (null/відсутнє = без змін, до 512 символів); відповідь `ChannelDto` має додатково `hasAppSecret`, `hasVerifyToken` (секрети не повертаються). Помилка `409 channel_id_conflict`.

### 14.5 Прогалини адмінки (TASK-690)
| Метод | Шлях | Доступ | Відповідь |
|---|---|---|---|
| GET | `/api/beauty/locations` | owner, admin, specialist | `[{id, name, address?, timezone, isActive}]` (усі заклади tenant, за назвою) |
| POST | `/api/beauty/specialists/{id}/locations` | owner, admin | тіло `{locationId, workingHours?}` (формат §9, без нього графік не змінюється/порожній) -> `200 SpecialistDto`; `404 location_not_found`/`specialist_not_found`; `409 location_already_assigned`; `422 invalid_working_hours`; `403 forbidden_role` (admin і профіль owner) |
| DELETE | `/api/beauty/specialists/{id}/locations/{locationId}` | owner, admin | `200 SpecialistDto` (заклад лишається в `locations[]` з `isActive=false`, графік збережено); `409 has_future_appointments` (є майбутні pending/confirmed записи майстра в цьому закладі; перевірка в тому ж UPDATE); `404 location_not_assigned` |
| GET | `/api/beauty/overview?date=yyyy-MM-dd[&locationId]` | **owner, admin** (містить виручку; specialist -> 403) | `{date, timezone, appointments: AppointmentDto[], kpi: {appointmentsCount, revenue, newClients, freeSlots}}` |

- **timezone.** `FreeSlot` (елементи `GET /slots`, публічні слоти) і `AppointmentDto` отримали поле `timezone` — IANA-зона закладу (`"Europe/Kyiv"`); `startsAt`/`label` слід показувати в ній. Для публічного `PublicAppointment` поле не додавалось.
- **Прибрати заклад** = `is_active=false` (м'яко): слоти й запис у закладі зникають (`422 specialist_not_at_location`), `POST .../locations` без `workingHours` повертає збережений графік. Наявні записи не чіпаються.
- **overview.** `date` — локальна дата закладу; без `date` — «сьогодні» за зоною еталонного закладу. Без `locationId` беруться всі активні заклади, доба кожного рахується у його часовій зоні (запис Окленда 08:00 належить дню Окленда). `timezone` у відповіді — зона еталонного закладу (вибраного або першого за назвою). `appointments` — усі статуси, за часом. KPI: `appointmentsCount` — без скасованих; `revenue` — сума `price_final` записів `completed` (як в аналітиці); `newClients` — клієнти, створені в цю добу еталонного закладу; `freeSlots` — кількість вільних слотів найкоротшої активної послуги майстра, що не перетинаються (жадібно, крок 15 хв), у робочих інтервалах без відсутностей, зайнятих і минулих годин. `404 location_not_found` для невідомого/чужого `locationId`; tenant без закладів -> порожня відповідь `200`.
- **Права/GRANT:** нових таблиць і міграцій немає; додаткових GRANT не потрібно.

## 15. Виправлення за аудитом керування працівниками (TASK-696) — v0.8
Адитивний розділ: §13–§14 не змінено; де поведінка змінилась, це сказано явно. Помилка = `{ "code", "message" }`.

### 15.1 Деактивація працівника (`PUT /api/beauty/specialists/{id}` з `isActive=false`)
В **одній транзакції** (під advisory-lock профілю): профіль `is_active=false`; прив'язаний користувач `users.is_active=false` (окрім ролі **owner** — власник ніколи не вимикається разом із профілем, ним керує `PATCH /api/users/{id}/status`); усі його refresh-токени відкликано; pending-запрошення з цим `specialist_id` відкликано (`status=revoked`). Наслідки: логін і `POST /api/auth/refresh` -> `401`; прийняти відкликане запрошення неможливо.
- **Повторна активація (`isActive=true`) користувача НЕ вмикає.** Доступ повертається лише явною дією керівника: `PATCH /api/users/{id}/status {isActive:true}` (користувач лишається прив'язаний до профілю, тому нове запрошення для профілю з користувачем неможливе: `409 specialist_linked`).
- **Запрошення** (`POST /specialists/{id}/invite` і `POST /api/invites` з role=specialist) для неактивного профілю -> `409 specialist_inactive`.
- **Дії specialist з неактивним профілем** (ще чинний access-токен): усі ендпоінти `/api/beauty/slots|appointments*` та `/api/beauty/absences*`, `/specialists/{id}/absences` -> `403 specialist_inactive` (action-filter `ActiveSpecialistFilter`); `AbsenceService` додатково відхиляє запит відсутності. owner/admin не зачіпаються.

### 15.2 Запрошення
- Нове запрошення для того ж `specialist_id` **відкликає** попередні pending (будь-який email): рівно одне активне; серіалізовано advisory-lock профілю (паралельні запити теж дають одне). `GET /api/invites` повертає `specialistId` (було в DTO, закріплено тестом).
- Відповіді `201` на `POST /api/beauty/specialists/{id}/invite` і `POST /api/invites` мають `Cache-Control: no-store` (у тілі одноразовий токен).

### 15.3 Публічний API не розкриває причину
`POST /api/public/{slug}/appointments`: внутрішній `specialist_unavailable` (день відсутності / послуга не призначена) віддається як **`409 slot_unavailable`** з тим самим `message`, що й для зайнятого слота (тіла збігаються побайтно). Staff API (`/api/beauty/appointments`) як і раніше розрізняє: `409 specialist_unavailable`. `outside_working_hours` лишається `422` (графік не секретний).

### 15.4 Гонка «відсутність ↔ запис»
`pg_advisory_xact_lock(hashtextextended(app.tenant_id || ':specialist:' || specialist_id, 0))` у транзакціях: створення відсутності, `approve`/`reject`/`cancel` (разом із вибіркою `conflicts[]` у ТІЙ САМІЙ транзакції), створення запису (`POST /appointments`, публічний запис — після lock ключа ідемпотентності), перенос запису, деактивація профілю. Під lock-ом запис повторно перевіряє відсутність/призначену послугу/активність: якщо відсутність щойно затверджено -> `409 specialist_unavailable`; якщо запис створено до коміту відсутності — він потрапляє в `conflicts[]`. Втрачених конфліктів немає. (Окремий lock-простір `:invite:` — для видачі запрошень.)

### 15.5 Ліміти
- Активних відсутностей `requested` на майстра — **10** (`Staff:MaxRequestedAbsencesPerSpecialist`); перевищення -> `422 too_many_requests` (перевіряється під lock; керівницькі `approved` не рахуються).
- `POST /specialists/{id}/absences`: ліміт на користувача **20 / 60 с** (`Staff:AbsenceRateLimit:PermitLimit` / `WindowSeconds`) -> `429 rate_limited`. Перевіряється в контролері після автентифікації (вбудований rate limiter стоїть до неї й не знає користувача).
- `[RequestSizeLimit(64 КБ)]` додано на `PUT /specialists/{id}`, `POST /specialists/{id}/invite`, `POST .../absences`, `POST /api/invites`.

### 15.6 Схема
Міграція `beauty_staff_hardening` (адитивна; `beauty_staff_management` не змінено): `beauty_specialist_absences.cancelled_by_user_id` (композитний FK на `users`, RESTRICT) і `cancelled_at`; заповнюються при `cancel` (хто й коли; `decided_*` не підміняються). Для `beauty_appointments` аналогічних полів немає (є лише `cancelled_at`), тому не додавались. Наприкінці `Up` — `DO`-блок: якщо на `beauty_specialists`, `beauty_services`, `beauty_specialist_services`, `beauty_specialist_absences` не ввімкнено `relrowsecurity`+`relforcerowsecurity`, міграція падає (`RAISE EXCEPTION`).
