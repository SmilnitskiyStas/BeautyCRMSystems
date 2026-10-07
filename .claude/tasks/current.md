# Поточні задачі

| ID | Опис | Статус | Агент |
|---|---|---|---|
| TASK-674 | БД: EF Core модель + міграція beauty_*, RLS, exclusion constraint, app.tenant_id interceptor, AddBeautyData | review (RLS-тест написано, не запускався — немає Docker/PG creds) | database-engineer |
| TASK-677 | AI-шар: клієнт Anthropic, 8 tools, режими, журнал, guardrails | done | ai-agent-developer |
| TASK-680 | Публічний запис: заклад → майстер → послуга й час → оформлення → підтвердження (mock API) | done | frontend-developer |
| TASK-679 | Адмінка: огляд, календар (блок = тривалість послуги), клієнти, спеціалісти, ціни й акції, аналітика, AI, канали; спільне меню, AI-FAB, mock API | done (lint/tsc OK) | frontend-developer |
| TASK-675 | Backend API beauty (слоти, запис/перенос/скасування A2, послуги/ціни, акції, клієнти, аналітика, канали, webhook, AI-журнал) + інтеграція портів Хвилі A, tenant middleware, RequireModule | review (build + 104 тести зелені; DB-інтеграція не запускалась: немає PG) | backend-developer |
| TASK-686 | Worker <-> БД: pg-адаптери портів, transactional outbox, поллери нагадувань/outbox/winback, HTTP-відправник Telegram/Instagram (AES-GCM), RLS-роль + app.tenant_id | review (build + 30 тестів зелені, integration на Testcontainers PG; питання: міграція для списку tenant, таблиця кампаній) | backend-developer |
| TASK-684 | Автентифікація, tenants, ролі: tenants/users/invites/refresh_tokens + RLS, JWT+refresh, політики ролей, specialist бачить лише свої записи, оператор створює tenant+owner (ключ із .env), lockout, rate limit | review (build + 151 тест зелені з BEAUTY_TEST_REQUIRE_DB=1, інтеграція на Docker PostgreSQL) | backend-developer |
| TASK-685 | Налаштовувана політика повернення коштів: beauty_cancellation_settings + RLS, GET/PUT /settings/cancellation (owner пише), умови скасування в слотах/записі, CancellationService за налаштуваннями | review (build + 194 тести зелені з BEAUTY_TEST_REQUIRE_DB=1) | backend-developer |
| TASK-687 | Адмінка: реальний HTTP-клієнт (§9–§11), логін/запрошення (BFF, httpOnly refresh, access у пам'яті), захист /beauty/*, ролі, «Перенести запис», Налаштування -> Повернення коштів | review (lint/tsc/build OK; потрібні CORS і ForwardedHeaders на backend) | frontend-developer |
| TASK-688 | Публічний API онлайн-запису `/api/public/{slug}`: каталог, слоти, POST запису (Idempotency-Key, rate limit, CAPTCHA-хук), перегляд/скасування за токеном, tenant за slug без розширення RLS | review (build ok; `BEAUTY_TEST_REQUIRE_DB=1 dotnet test` 261/261 x3) | backend-developer |
