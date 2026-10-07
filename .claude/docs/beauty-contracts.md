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
