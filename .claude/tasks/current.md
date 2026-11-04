# Поточні задачі

Деталі, тести й причини — у task-логах (колонка «Лог»). Повний попередній текст: `.claude/tasks/current.full.md.bak` (локальний, не в git).

| ID | Агент | Статус | Коротко | Лог |
|---|---|---|---|---|
| TASK-674 | database-engineer | review | БД: EF-модель, міграція beauty_*, RLS, exclusion (RLS-тест не запускався) | .claude/logs/tasks/TASK-674.md |
| TASK-677 | ai-agent-developer | done | AI-шар: клієнт Anthropic, 8 tools, режими, журнал, guardrails | .claude/logs/tasks/TASK-677.md |
| TASK-680 | frontend-developer | done | Публічний запис: заклад → майстер → послуга → час → підтвердження (mock) | .claude/logs/tasks/TASK-680.md |
| TASK-679 | frontend-developer | done | Адмінка: огляд, календар, клієнти, спеціалісти, ціни, аналітика, AI (mock) | .claude/logs/tasks/TASK-679.md |
| TASK-675 | backend-developer | review | Backend API beauty: слоти, запис/скасування A2, послуги, акції, канали, AI-журнал | .claude/logs/tasks/TASK-675.md |
| TASK-686 | backend-developer | review | Worker↔БД: outbox, поллери нагадувань/winback, відправник TG/IG, RLS-роль | .claude/logs/tasks/TASK-686.md |
| TASK-684 | backend-developer | review | Auth, tenants, ролі: JWT+refresh, RLS, lockout, rate limit | .claude/logs/tasks/TASK-684.md |
| TASK-685 | backend-developer | review | Політика повернення коштів: налаштування, CancellationService | .claude/logs/tasks/TASK-685.md |
| TASK-687 | frontend-developer | review | Адмінка: HTTP-клієнт, логін/запрошення (BFF), захист /beauty/*, перенос | .claude/logs/tasks/TASK-687.md |
| TASK-688 | backend-developer | review | Публічний API /api/public/{slug}: каталог, слоти, запис, скасування за токеном | .claude/logs/tasks/TASK-688.md |
| TASK-692 | frontend-developer | review | Адмінка: працівники (профіль, графік, відсутності, запрошення), mock + http | .claude/logs/tasks/TASK-692.md |
| TASK-693 | frontend-developer | review | Звірка http-клієнта з DTO, публічний запис на API, CSP, vitest | .claude/logs/tasks/TASK-693.md |
| TASK-691 | backend-developer | review | Backend: працівники (профіль, графік, відсутності), 409 specialist_unavailable | .claude/logs/tasks/TASK-691.md |
| TASK-694 | backend-developer | review | Worker: schema.sql з міграцій, відписка STOP, денний ліміт маркетингу | .claude/logs/tasks/TASK-694.md |
| TASK-689+690 | backend-developer | review | Аудит безпеки H/M/L + прогалини адмінки (CORS, locations, overview), §14 | .claude/logs/tasks/TASK-689.md |
| TASK-696 | backend-developer | review | Аудит TASK-695: каскадна деактивація, одне запрошення, advisory-lock | .claude/logs/tasks/TASK-696.md |
| TASK-698 | frontend-developer | review | Адмінка: екран «Заклади», календар без скасованих, історія скасувань | .claude/logs/tasks/TASK-698.md |
| TASK-697 | backend-developer | review | Backend §16: API закладів (409/422), історія скасувань, міграція | .claude/logs/tasks/TASK-697.md |
| TASK-701 | backend-developer | review | Backend §17: вихідні дні закладу, closures (EXCLUDE), 409 location_closed | .claude/logs/tasks/TASK-701.md |
| TASK-702 | frontend-developer | review | Frontend §17: вихідні дні, затінені дні в календарі, публічний запис | .claude/logs/tasks/TASK-702.md |
| TASK-703 | backend-developer | review | Backend §18: години роботи закладу, перетин із графіком майстра | .claude/logs/tasks/TASK-703.md |
| TASK-704 | frontend-developer | review | Frontend §18.3: список і сторінка закладу, години роботи (перегляд) | .claude/logs/tasks/TASK-704.md |
| TASK-705 | frontend-developer | review | Аудит узгодженості F1–F9: час у зоні закладу, доступність, GET /overview | .claude/logs/tasks/TASK-705.md |
| TASK-710 | frontend-developer | review | Frontend §19: діапазон аналітики, series, breakdown, порівняння, CSV | .claude/logs/tasks/TASK-710.md |
| TASK-706 | backend-developer | review | Backend §19: аналітика from/to, network/series, compare, завантаження | .claude/logs/tasks/TASK-706.md |
| TASK-713 | database-engineer | review | Схема ШІ §20.4 (ADR-010): налаштування, ключі (шифротекст), облік, RLS | .claude/logs/tasks/TASK-713.md |
| TASK-714 | backend-developer | review | Протектор ключів ШІ v2 (AES-256-GCM), ротація, ai-secrets reencrypt | .claude/logs/tasks/TASK-714.md |
| TASK-717 | frontend-developer | review | Frontend §20.9: «ШІ-асистент» у Налаштуваннях (провайдер, ключі, промпт) | .claude/logs/tasks/TASK-717.md |
| TASK-715 | ai-agent-developer | review | ШІ-шар ADR-010: реєстр провайдерів, runtime tenant, ліміти, PromptComposer | .claude/logs/tasks/TASK-715.md |
| TASK-716 | backend-developer | review | HTTP API налаштувань ШІ §20.5–§20.6, аудит без значень ключів | .claude/logs/tasks/TASK-716.md |
| TASK-722 | frontend-developer | done | Двоколонкова розкладка /beauty/settings | .claude/logs/tasks/TASK-722.md |
| TASK-723 | ai-agent-developer | review | Виправлення рев'ю безпеки M1, M3, L3, L5, L9 (TASK-719) | .claude/logs/tasks/TASK-723.md |
| TASK-724 | database-engineer | review | Міграція beauty_chat_assistant (ADR-011): poll_state, proposals, RLS | .claude/logs/tasks/TASK-724.md |
| TASK-726 | backend-developer | review | Backend §21.6: OnlineBookingCore, ChatBookingService (prepare/confirm) | .claude/logs/tasks/TASK-726.md |
| TASK-725 | integrations-developer | review | Канали §21.3: InboundIngestCore, парсер Telegram, TelegramPoller | .claude/logs/tasks/TASK-725.md |
| TASK-727 | ai-agent-developer | review | ШІ-шар чату клієнта §21.7: scope ClientChat, 7 tools | .claude/logs/tasks/TASK-727.md |
| TASK-728 | ai-agent-developer | review | Конвеєр розмов §21.5–§21.6: ConversationWorker, TurnProcessor | .claude/logs/tasks/TASK-728.md |
| TASK-733 | ai-agent-developer | review | Виправлення рев'ю чату (TASK-732): M1–M3, L1–L3, L6, L7 | .claude/logs/tasks/TASK-733.md |
| TASK-734 | devops-engineer | review | Локальне dev-середовище для smoke Telegram (scripts/dev, runbook §11) | .claude/logs/tasks/TASK-734.md |
| TASK-735 | ai-agent-developer | review | Виправлення за живим smoke Telegram: контекст ходу, чистий текст | .claude/logs/tasks/TASK-735.md |
| TASK-736 | database-engineer | review | Міграція beauty_reminders_contacts_inbox (ADR-012), RLS FORCE | .claude/logs/tasks/TASK-736.md |
| TASK-739 | backend-developer | review | Inbox API §22.6.5: список розмов, відповідь менеджера, resume-ai, read | .claude/logs/tasks/TASK-739.md |
| TASK-742 | backend-developer | review | Воркер нагадувань §22.5: час закладу (DST), тиша 22–08, delivery_failed | .claude/logs/tasks/TASK-742.md |
| TASK-743 | frontend-developer | review | Frontend inbox §22.6.5: /beauty/inbox, відповідь, «Повернути ШІ» | .claude/logs/tasks/TASK-743.md |
| TASK-744 | frontend-developer | review | Frontend дзвінки й нагадування §22.6: /beauty/calls, картка клієнта | .claude/logs/tasks/TASK-744.md |
| TASK-737 | backend-developer | review | ReminderPlanner §22.4: тиша, злиття, переплан у create/patch/cancel | .claude/logs/tasks/TASK-737.md |
| TASK-740 | ai-agent-developer | review | Нагадування й handoff §22.7–§22.8: held, вибір, «Стоп» | .claude/logs/tasks/TASK-740.md |
| TASK-749 | database-engineer | review | Міграція beauty_chat_buttons (ADR-013): клавіатури, actions, RLS | .claude/logs/tasks/TASK-749.md |
| TASK-738 | backend-developer | review | API вподобань, call-tasks, settings/reminders §22.6.2–§22.6.4 | .claude/logs/tasks/TASK-738.md |
| TASK-741 | ai-agent-developer | review | Блок «ФАКТИ ЗАКЛАДУ» §22.9, PolicyClaimGuard → handoff policy_unknown | .claude/logs/tasks/TASK-741.md |
| TASK-751 | backend-developer | review | Воркер кнопок §23.4: reply_markup, клавіатура нагадування | .claude/logs/tasks/TASK-751.md |
| TASK-750 | integrations-developer | review | Транспорт Telegram для кнопок §23.4: callback_query, setMyCommands | .claude/logs/tasks/TASK-750.md |
| TASK-752 | backend-developer | review | Клавіатури й дії клієнта §23.3–§23.10: токени, скасування з лімітом | .claude/logs/tasks/TASK-752.md |
| TASK-753 | ai-agent-developer | review | Конвеєр кнопок і команд §23.5–§23.8: callback без LLM, slot_pick | .claude/logs/tasks/TASK-753.md |
| TASK-754 | frontend-developer | review | Frontend кнопок Telegram §23.10–§23.12 (mock): чипи, перемикач кнопок | .claude/logs/tasks/TASK-754.md |
| TASK-745 | devops-engineer | review | Dev-середовище нагадувань/inbox/кнопок: міграції 12→14, start-worker.ps1 | .claude/logs/tasks/TASK-745.md |
| TASK-760 | backend-developer | review | Воркер: відгук і winback не йдуть відписаним/none (M2), прапорець | .claude/logs/tasks/TASK-760.md |
| TASK-759 | ai-agent-developer | review | Виправлення рев'ю M3, L2, L8, L10: ліміти шаблонних відповідей, throttle | .claude/logs/tasks/TASK-759.md |
| TASK-761 | devops-engineer | review | Dev-скрипти show-login.ps1 і create-salon.ps1 (новий салон, демо-дані) | .claude/logs/tasks/TASK-761.md |
| TASK-762 | database-engineer | review | Міграція beauty_service_costs §24.3: 2 таблиці, знімок на записі, RLS FORCE, тести БД (schema.sql — вручну) | .claude/logs/tasks/TASK-762.md |
| TASK-767 | database-engineer | review | Міграція beauty_client_phone_capture §25.3: телефон/pending/дубль клієнта, contact у повідомленнях, phone_request, sweeper-індекс, schema.sql, тести БД | .claude/logs/tasks/TASK-767.md |
| TASK-769 | backend-developer | review | API клієнтів для телефону §25.7: нормалізація E.164, phone-pending accept/reject, картка/маска Inbox/call-tasks, дублікати, ClientPhoneService | .claude/logs/tasks/TASK-769.md |
| TASK-768 | integrations-developer | review | Транспорт контакту Telegram §25.4: парсер contact (own/foreign/invalid), reply-keyboard/remove_keyboard, i:phone, порти contact_phone + sweeper, тести | .claude/logs/tasks/TASK-768.md |
| TASK-763 | backend-developer | review | Знімок собівартості при completed (умовний UPDATE, §24.4) + CRUD собівартості /services/{id}/costs (ролі, IDOR, аудит, ліміт), тести | .claude/logs/tasks/TASK-763.md |
| TASK-764 | backend-developer | review | Аналітика §24: фінансові поля series/network/locations, GET /analytics/network/details (курсор, ліміт, no-store), тести | .claude/logs/tasks/TASK-764.md |
| TASK-765 | frontend-developer | review | Frontend §24 (mock): клікабельний графік, панель «Деталі», чистий дохід, CSV деталей | .claude/logs/tasks/TASK-765.md |
| TASK-766 | frontend-developer | review | Frontend §24.8.2/§25.8 (mock): екран «Послуги» з собівартістю, телефон у картці/inbox/дзвінках | .claude/logs/tasks/TASK-766.md |
| TASK-770 | ai-agent-developer | review | Конвеєр збору телефону §25.5–25.6: PhoneCapture у чаті (запит лише при виборі дзвінка, контакт/ручний номер, відмова+пауза, конфлікт, handoff/suggest, removeReply), ChatPhoneParser, sweeper-хук, тести | .claude/logs/tasks/TASK-770.md |
| TASK-775 | frontend-developer | review | Календар: календар не перечитувався після переносу/скасування (ключ owner), дії «Підтвердити/Завершити/Не прийшов» у панелі запису | .claude/logs/tasks/TASK-775.md |
| TASK-783 | copywriter | review | Тексти маркетингу §26/ADR-015: згода mc-v1, шаблони розсилок, купони, відписка, адмінка, поради, список для юр. перевірки (.claude/docs/marketing-texts.md) | .claude/logs/tasks/TASK-783.md |
| TASK-776 | database-engineer | review | Міграція beauty_marketing §26.3: згода (стан+events), кампанії, одержувачі, купони, погашення, access/coupon_id/offer_*, скидання marketing_consent, RLS FORCE, schema.sql, тести БД | .claude/logs/tasks/TASK-776.md |
| TASK-778 | backend-developer | review | Купони й ціна §26.6: access public/code_only (витік закрито), CouponService (випуск, перевірка, ліміти спроб, повернення), QuoteWithCoupon, погашення в транзакції запису (staff/публічний/чат), API /coupons | .claude/logs/tasks/TASK-778.md |
| TASK-780 | backend-developer | review | Воркер §26.8–11: pollCampaigns, перевірки згоди/частоти/ліміту/вікна (IANA, DST), кнопки campaign_offer, персональні купони, прапорці WORKER_CAMPAIGNS/WINBACK_ENABLED, 17 тестів | .claude/logs/tasks/TASK-780.md |
| TASK-781 | integrations-developer | review | Транспорт кнопок маркетингу §26.9.1: verbs mc/co/i:mkt/i:mkoff, види marketing_consent/campaign_offer, /marketing у setMyCommands, перевірки §23.5, тести | .claude/logs/tasks/TASK-781.md |
| TASK-782 | ai-agent-developer | review | Чат: /marketing, згода mc, пропозиція один раз, i:mkoff/Стоп, co з купоном, код текстом з маскуванням, ціна купона в підсумку, факти/правила ШІ, 54 тести | .claude/logs/tasks/TASK-782.md |
| TASK-777 | backend-developer | review | Згода §26.4: MarketingConsentService (стан+події, advisory lock, Стоп, правило адміна), API картки, Marketing:Enabled/AdminGrantEnabled, модуль beauty_marketing, ігнор поля форми, тести | .claude/logs/tasks/TASK-777.md |
| TASK-779 | backend-developer | review | Аудиторії й кампанії §26.5/§26.7/§26.11: AudienceService (сегменти, придатність, перегляд без ПД), CampaignService (чернетка, перегляд, атомарне підтвердження з Idempotency-Key+revision, скасування, статистика V1, summary купонів), API `/campaigns`, `/marketing/audience/preview`, legal guard, фікс staff-згоди (TASK-777), golden-вектор хешу Node↔.NET, тести | .claude/logs/tasks/TASK-779.md |
| TASK-784 | frontend-developer | review | Розсилки §26 (mock): «Розсилки» /beauty/campaigns, майстер (акція→аудиторія з переглядом→текст→купон→перегляд→запуск), діалог підтвердження (Idempotency-Key, revision), деталі/статистика/CSV/скасування, прапорці marketing_disabled | .claude/logs/tasks/TASK-784.md |
| TASK-785 | frontend-developer | review | Згода й купони §26 (UI): картка згоди й купонів клієнта, «Доступ»/код/«Розіслати» в акціях, позначка купона в записі/візитах, промокод у публічній формі, чипи в Inbox, mock/demo-seed | .claude/logs/tasks/TASK-785.md |
| TASK-791 | backend-developer | review | Рев'ю безпеки маркетингу (воркер): маска коду купона, legal guard, GRANT/REVOKE, kill switch, winback Telegram/вікно/ліміт, L1/L5/L9/L11, 16 тестів | .claude/logs/tasks/TASK-791.md |
| TASK-790 | ai-agent-developer | review | Рев'ю безпеки маркетингу (backend): відписка на вході H1, маска коду M1/L10, kill switch/модуль M4/L4, ліміти й блок-список кодів M5/L7/L8, L2/L3/L6, ~45 тестів | .claude/logs/tasks/TASK-790.md |
| TASK-792 | frontend-developer | review | Фікс 422 при створенні акції: реальні заклади/послуги з API замість демо-id, поля дат type=date (yyyy-MM-dd -> ISO), валідація періоду, українські тексти 422, тести | .claude/logs/tasks/TASK-792.md |
| TASK-793 | backend-developer | review | Inbox «Опрацьовано»: POST /conversations/{id}/resolve (204, no-store, ідемпотентно; held->processed, скидання awaiting/handoff, ШІ active, прочитано, без відправки клієнту), тести, уточнення контракту | .claude/logs/tasks/TASK-793.md |
| TASK-794 | frontend-developer | review | Кнопка «Опрацьовано» в Inbox: POST /conversations/{id}/resolve, видима при очікуванні (handoff/needsReply/held), фокус/status, помилки 403/404/409/429, mock, тести | .claude/logs/tasks/TASK-794.md |
| TASK-795 | frontend-developer | review | Inbox: одна кнопка «Дії ▾» (меню: Повернути ШІ / Опрацьовано з точними підказками) замість двох кнопок, чип стану, a11y-меню, тести | .claude/logs/tasks/TASK-795.md |
| TASK-796 | frontend-developer | review | Бічне меню адмінки: 4 згортані блоки (NAV_GROUPS), aria-expanded/controls, localStorage, автороз гортання активного, індикатор непрочитаних на згорнутому блоці, «Запис клієнта» зверху, specialist плоский, тести | .claude/logs/tasks/TASK-796.md |
| TASK-797 | frontend-developer | review | Адаптивне меню адмінки: на вузьких екранах кнопка «Меню» + drawer (dialog, backdrop, trap фокуса, Esc, блок скролу, індикатор непрочитаних), широкі без змін, тести | .claude/logs/tasks/TASK-797.md |
| TASK-798 | ai-agent-developer | review | Чат: спершу повна ціна, потім акція (контекст ходу, промпт, list_services.promotionPrice, get_active_promotions.serviceIds), тести | .claude/logs/tasks/TASK-798.md |
| TASK-799 | ai-agent-developer | review | Межа епізоду діалогу для LLM (/book, підтвердження/скасування, resolve/resume_ai, тиша 6 год), без міграції, тести | .claude/logs/tasks/TASK-799.md |
| TASK-786 | qa-engineer | review | QA маркетингу §26.14: таблиця відповідності 30 сценаріїв, закриття прогалин (7, 11, 12, 22, 23, 24, 25), сканер логів (сц. 27, API-чат і воркер), чеклист ручного smoke SM1–SM12 | .claude/logs/tasks/TASK-786.md |
| TASK-800 | devops-engineer | review | CI (GitHub Actions), Dockerfile api/worker/web, /healthz і /readyz, deploy/staging (спільний сервер, nginx, 127.0.0.1), staging-images.yml (ghcr, вручну), runbook §12 | .claude/logs/tasks/TASK-800.md |
| TASK-801 | devops-engineer | review | npm audit: worker vitest ^2→^4.1.12 (tinypool critical), npm audit fix (undici) у worker і frontend (undici, zod); audit-level=high = 0 | .claude/logs/tasks/TASK-801.md |
| TASK-802 | backend-developer | review | NuGet audit: BeautyCrm.Tests закріплено System.Net.Http 4.3.4 і System.Text.RegularExpressions 4.3.1 (транзитивні через xunit 2.5.3, High); dotnet list --vulnerable = 0 | .claude/logs/tasks/TASK-802.md |
| TASK-804 | database-engineer | planned | ADR-017 етап A: міграція beauty_specialist_service_terms (price/duration_minutes NULL, CHECK, DO-блок FORCE RLS), EF, schema.sql, тести БД, runbook відкату | .claude/logs/tasks/TASK-804.md |
| TASK-805 | project-architect | planned | ADR-017 етап A: контракт beauty-contracts.md §27 (DTO, коди, зміна §12/§13/§21/§26), після відповіді людини на Q1–Q3 | .claude/logs/tasks/TASK-805.md |
| TASK-806 | backend-developer | planned | ADR-017 етап B: ServiceTermsResolver + порт GetServiceTermsAsync, слоти з тривалістю майстра і квотою, CreateAsync (ціна/тривалість майстра, duration_changed), тести | .claude/logs/tasks/TASK-806.md |
| TASK-807 | backend-developer | planned | ADR-017 етап B: PUT /specialists/{id}/services з terms[], SpecialistDto.services[].price/durationMinutes, права CanModify, тести | .claude/logs/tasks/TASK-807.md |
| TASK-808 | backend-developer | planned | ADR-017 етап C: публічний каталог «будь-який» без сум (termsBySpecialist), слоти без майстрів без ціни, expected* у create, coupons/check зі specialistId, тести | .claude/logs/tasks/TASK-808.md |
| TASK-809 | ai-agent-developer | planned | ADR-017 етап C: чат — find_slots з умовами майстра, list_services(specialistId, termsVary), ФАКТИ, prepare/confirm через resolver, duration_changed, промпт, тести | .claude/logs/tasks/TASK-809.md |
| TASK-810 | frontend-developer | planned | ADR-017 етап C: публічний запис — «будь-який» лише час, після вибору слота майстер+ціна+тривалість, expected* у POST, купон після вибору, 409 price/duration_changed, mock, тести | .claude/logs/tasks/TASK-810.md |
| TASK-811 | frontend-developer | planned | ADR-017 етап D: картка майстра — поля «Ціна»/«Тривалість» у списку послуг (порожнє = базове), права admin, «Запис клієнта» з умовами майстра, mock, тести | .claude/logs/tasks/TASK-811.md |
| TASK-812 | backend-developer | planned | ADR-017 етап D: огляд (вільні слоти) через resolver, get_prices variesBySpecialist, регресія собівартості/виплати/аналітики/нагадувань від знімка, тести | .claude/logs/tasks/TASK-812.md |
| TASK-813 | qa-engineer | planned | ADR-017: таблиця відповідності сценаріїв, регресія booking/public/chat/analytics, ручний smoke «будь-який майстер» | .claude/logs/tasks/TASK-813.md |
| TASK-814 | security-reviewer | planned | ADR-017: рев'ю прав (admin/owner, IDOR, RLS), відсутності витоку собівартості, публічного каталогу й ШІ (ціна лише з БД) | .claude/logs/tasks/TASK-814.md |
| TASK-815 | project-architect | in_progress | ADR-018: контракт beauty-contracts.md §27 написано (control plane, invite, feedback, тіло заявки, підпис v1, modules, 403 tenant_suspended; ендпоінт лідів ShelfGuard — Q4 відкрито). Тестовий вектор: вхідні дані + генератор `.ai/artifacts/contracts/ADR-018-hmac-test-vector.py`; хеші/підписи ще null — запустити `python -I` генератор, тоді review | .claude/logs/tasks/TASK-815.md |
| TASK-816 | database-engineer | planned | ADR-018: міграції external_ref+unique, suspended_*, owner state invited/password_hash NULL, platform_owner_invites, platform_audit_log, control_plane_nonces, beauty_platform_feedback (FORCE RLS), schema.sql, тести | .claude/logs/tasks/TASK-816.md |
| TASK-817 | backend-developer | review | ADR-018: схема ControlPlaneHmac (canonical v1, вікно 300 с, nonce, kid-ротація, fail closed), rate limit, Auth:PlatformKeyEnabled, тестовий вектор, тести | .claude/logs/tasks/TASK-817.md |
| TASK-818 | backend-developer | review | ADR-018: /api/control/v1/tenants (create ідемпотентно, get, suspend, resume, owner-invite) на спільних доменних операціях, аудит з актором, тести | .claude/logs/tasks/TASK-818.md |
| TASK-819 | backend-developer | review | ADR-018: /api/auth/invite/inspect і /accept (хеш токена, одноразово, атомарно, 410 invite_invalid, rate limit, аудит), тести | .claude/logs/tasks/TASK-819.md |
| TASK-820 | backend-developer | review | ADR-018: POST /api/beauty/feedback (валідація, rate limit, запис + outbox platform_feedback.submitted), тести | .claude/logs/tasks/TASK-820.md |
| TASK-821 | integrations-developer | planned | ADR-018: worker — доставка заявок у ShelfGuard з HMAC (вихідний ключ), повтори, feedbackId, статуси; очищення control_plane_nonces; тести | .claude/logs/tasks/TASK-821.md |
| TASK-822 | frontend-developer | review | ADR-018: сторінка /invite (токен у фрагменті, inspect, задати пароль, no-referrer, noindex), mock, тести | .claude/logs/tasks/TASK-822.md |
| TASK-823 | frontend-developer | planned | ADR-018: форма заявки в адмінці (тип, критичність, текст, примітка про передачу даних), 202/429/422, mock, тести, a11y | .claude/logs/tasks/TASK-823.md |
| TASK-824 | devops-engineer | planned | ADR-018: nginx allowlist /api/control/, deny /api/platform/ ззовні, змінні ControlPlane__*/ShelfGuard__* у bootstrap-env, runbook ротації (human gate) | .claude/logs/tasks/TASK-824.md |
| TASK-825 | qa-engineer | planned | ADR-018: таблиця відповідності, сценарії replay/skew/ротації/ідемпотентності/призупинення/invite, e2e зі stub ShelfGuard | .claude/logs/tasks/TASK-825.md |
| TASK-826 | security-reviewer | planned | ADR-018: рев'ю HMAC і replay, invite-токена, RLS platform-операцій, PII і логів, nginx-шарів, секретів | .claude/logs/tasks/TASK-826.md |
| TASK-827 | integrations-developer | planned | ADR-018 [репозиторій ShelfGuard]: бекенд-клієнт Beauty (підпис v1, тестовий вектор, create/get/suspend/resume/owner-invite, external_ref, повтори) | .claude/logs/tasks/TASK-827.md |
| TASK-828 | frontend-developer | planned | ADR-018 [репозиторій ShelfGuard]: UI підприємства — кнопки «Створити в Beauty», «Призупинити», «Відновити», «Нове запрошення», показ посилання один раз | .claude/logs/tasks/TASK-828.md |
| TASK-829 | backend-developer | planned | ADR-018 [репозиторій ShelfGuard]: прийом заявок Beauty (перевірка HMAC, ідемпотентність feedbackId, LandingLead source=beauty, тип/критичність, Telegram-пуш) на базі feat/beauty-leads-telegram | .claude/logs/tasks/TASK-829.md |
| TASK-830 | project-manager | planned | ADR-018: розгортання на staging через human gates (секрети, nginx, міграції, юр. текст), наскрізний smoke ShelfGuard↔Beauty | .claude/logs/tasks/TASK-830.md |
| TASK-831 | backend-developer | review | ADR-018 D4: PUT /api/control/v1/tenants/{externalRef}/modules (підпис, actor, noop, аудит before/after) на спільній доменній операції, тести | .claude/logs/tasks/TASK-831.md |
| TASK-832 | integrations-developer | planned | ADR-018 D4 [репозиторій ShelfGuard]: метод клієнта Beauty setModules (підпис v1, actor), тести | .claude/logs/tasks/TASK-832.md |
| TASK-833 | frontend-developer | planned | ADR-018 D4 [репозиторій ShelfGuard]: UI модулів tenant у картці підприємства (перемикачі, підтвердження, помилки) | .claude/logs/tasks/TASK-833.md |
| TASK-834 | frontend-developer | review | ADR-018 D2: публічний запис і віджет — екран «Запис тимчасово призупинено» на 403 tenant_suspended, mock, тести | .claude/logs/tasks/TASK-834.md |

## ADR-017: індивідуальна ціна й тривалість майстра (TASK-804…814)

ADR: `.claude/docs/adr/ADR-017-specialist-service-price-and-duration.md` (proposed). Затверджені рішення користувача — ADR §1.2. Відкриті питання Q1–Q6 — ADR §8.

**DAG:** 804 → 806 → {807, 808, 812}; 805 (після Q1–Q3) → {808, 809, 810, 811}; 806 + 808 → 809; 808 → 810; 807 → 811; {807…812} → 813 → 814. Паралельно можна вести 807 ∥ 808 ∥ 812, а також 809 ∥ 810 ∥ 811.

- **TASK-804 (A, database-engineer).** Залежності: ADR-017.
  - Критерії:
    - міграція `beauty_specialist_service_terms` додає `price numeric(12,2) NULL`, `duration_minutes int NULL`, CHECK `ck_beauty_specialist_services_price` (0..1 000 000) і `_duration` (5..720);
    - `DO`-блок падає без FORCE RLS на `beauty_specialist_services`/`beauty_services`/`beauty_service_prices`;
    - наявні рядки після Up мають NULL;
    - Up→Down→Up на копії проходить;
    - EF `SpecialistServiceLink.Price/DurationMinutes`;
    - `worker/src/testing/schema.sql` синхронізовано;
    - тести БД: межі CHECK, RLS між tenant;
    - runbook містить відкат з експортом override (Down — human gate).
- **TASK-805 (A, project-architect).** Залежності: ADR-017, відповіді на Q1–Q3.
  - Критерії: `beauty-contracts.md` §27 фіксує:
    - `FreeSlot` + `durationMinutes/price*/promotion*`;
    - `PublicServiceDto` (nullable + `termsBySpecialist`, позначено як «Зміна §12»);
    - `PUT /specialists/{id}/services` `terms[]` і коди 422;
    - `expectedPriceFinal/expectedDurationMinutes`, 409 `duration_changed`;
    - `coupons/check?specialistId`;
    - поля інструментів ШІ;
    - тестові сценарії.
- **TASK-806 (B, backend-developer).** Залежності: TASK-804.
  - Критерії:
    - `ServiceTermsResolver` (чиста функція, unit-тести пріоритетів майстер > заклад > мережа, майстер > база, незалежні override);
    - `IBookingStore.GetServiceTermsAsync` (+ пакетний варіант);
    - `GetSlotsAsync` рахує слоти кожного майстра з його тривалістю, у слоті є квота (акція від ціни майстра);
    - `CreateAsync` бере ціну/тривалість з resolver, `ExpectedDurationMinutes` → 409 `duration_changed`;
    - перенос лишає знімок;
    - фіксована знижка понад ціну майстра дає `price_final` ≥ 0 (тест);
    - копій правила «заклад/мережа» поза resolver немає (grep);
    - `dotnet test` зелений із `BEAUTY_TEST_REQUIRE_DB=1`.
- **TASK-807 (B, backend-developer).** Залежності: TASK-804; контракт — TASK-805.
  - Критерії:
    - `terms[]` адитивно, відсутнє `terms` не змінює override;
    - 422 `terms_service_not_assigned|duplicate_service|invalid_price|invalid_duration`;
    - admin → 403 `forbidden_role` для профілів owner/admin, specialist → 403;
    - чужий tenant → 404;
    - `SpecialistDto.services[]` має `price?/durationMinutes?`;
    - зняття послуги видаляє override;
    - тести API.
- **TASK-808 (C, backend-developer).** Залежності: TASK-806, TASK-805.
  - Критерії:
    - `services` без `specialistId` віддає суми/тривалість = null і `termsBySpecialist: true`, а з `specialistId` — точні умови майстра;
    - послуга з ціною лише в майстра доступна;
    - публічні слоти не містять майстрів без ефективної ціни;
    - `expected*` → 409, не входять у відбиток ідемпотентності;
    - `coupons/check` без майстра віддає лише `outcome`;
    - тести.
- **TASK-809 (C, ai-agent-developer).** Залежності: TASK-806, TASK-808, TASK-805 (Q1).
  - Критерії:
    - `find_slots` дає умови майстра в кожному слоті;
    - `list_services` приймає `specialistId`, без нього правило Q1 (`termsVary`);
    - ФАКТИ без сум для різних умов;
    - `prepare_booking`/confirm через resolver, `duration_changed` дає шаблон `price_changed`;
    - промпт: точна ціна лише після майстра/слота;
    - тести.
- **TASK-810 (C, frontend-developer).** Залежності: TASK-808 (контракт TASK-805).
  - Критерії:
    - «будь-який»: каталог без цін/тривалості, слоти — лише час;
    - після вибору слота: майстер, `startsAt–endsAt`, ціна до підтвердження, кнопка повернення;
    - POST з `expectedPriceFinal/expectedDurationMinutes`, обробка 409 (перечитати слоти, показати нові умови);
    - купон перевіряється з `specialistId` після вибору;
    - mock-клієнт оновлено;
    - `npm test`, `tsc`, `lint`, `build` зелені;
    - a11y (оголошення зміни ціни).
- **TASK-811 (D, frontend-developer).** Залежності: TASK-807.
  - Критерії:
    - у картці майстра, у списку послуг, поля «Ціна» і «Тривалість»: placeholder — базове значення, порожнє = базове, валідація 0..1 000 000 / 5..720;
    - одне «Зберегти» (`PUT services` з `terms[]`);
    - для admin на профілі owner/admin поля недоступні;
    - «Запис клієнта» показує ціну/тривалість обраного майстра (зі слота);
    - mock, тести.
- **TASK-812 (D, backend-developer).** Залежності: TASK-806.
  - Критерії:
    - `OverviewService` рахує вільні слоти з тривалістю майстра;
    - `get_prices` має `variesBySpecialist`;
    - регресійні тести: виплата майстру = % від `price_final` майстра; виручка/чистий дохід зі знімка; нагадування й `ends_at` не змінюються після зміни override; аналітика завантаження за `duration_minutes` запису.
- **TASK-813 (qa-engineer).** Залежності: TASK-807…812.
  - Критерії:
    - таблиця відповідності ADR-017 §3–§7;
    - сценарії: різні тривалості на той самий час, межа дня, гонка умов (409), купон/акція від ціни майстра, зняття/повернення послуги;
    - чеклист ручного smoke.
- **TASK-814 (security-reviewer).** Залежності: TASK-813.
  - Критерії: звіт у `docs/reviews/`, що покриває:
    - права редагування;
    - RLS/IDOR;
    - відсутність собівартості в нових DTO;
    - публічний каталог без сум для «будь-якого»;
    - ціни ШІ лише з БД;
    - без логування сум.

## ADR-018: ShelfGuard як control plane (TASK-815…834)

ADR: `.claude/docs/adr/ADR-018-shelfguard-control-plane-integration.md` (proposed, human gate auth/security boundary). Затверджені рішення власника — ADR §1.2 і §9.1 (D1–D5, 2026-11-04). Відкриті питання — ADR §9.2:
- Q2b — worker і канали для призупиненого tenant; пропозиція, потребує підтвердження, задачі ще немає;
- Q4 — блокує 829 → 821 і розділ тіла заявки в 815;
- Q5 — технічні факти, звіряються на старті 816/818;
- Q8 — юридичний текст, блокує фінальний текст у 823 і gate у 830.

**Репозиторії.** TASK-815…826, TASK-830, TASK-831 і TASK-834 виконуються в цьому репозиторії. TASK-827, TASK-828, TASK-829, TASK-832 і TASK-833 — в **окремому репозиторії ShelfGuard** (`C:\Users\stass\source\CRMProductSystems`), за його власними правилами й гілками. Тут ведуться лише статус і посилання на результат.

**DAG:** 815 → {818, 819, 820, 822, 823, 827, 829, 831}; 816 → {818, 819, 820, 831}; 817 → {818, 821, 827, 831}; 818 → 834; 819 → 822; 820 → {821, 823}; 829 → 821 (вхідний контракт ShelfGuard); 827 → 828; {827, 831} → 832 → 833; {816…824, 831, 834} → 825 → 826; {824, 826, 827, 828, 829, 832, 833} → 830. Паралельно: 816 ∥ 817; 818 ∥ 819 ∥ 820 ∥ 831; 822 ∥ 823 ∥ 834; 827 ∥ 829.

- **TASK-815 (project-architect).** Залежності: ADR-018 (Q1–Q3, Q6, Q7 закрито як D1–D5). Розділ тіла доставки заявки — після Q4.
  - Додатково до критеріїв нижче контракт містить `PUT …/modules` і `403 tenant_suspended` для публічного запису й віджета.
  - Критерії: новий розділ `beauty-contracts.md` фіксує:
    - DTO і коди `/api/control/v1/*`, `/api/auth/invite/*`, `/api/beauty/feedback`;
    - заголовки й canonical string v1;
    - тіло доставки заявки в ShelfGuard;
    - поведінку призупиненого tenant (D2; Q2b позначено як пропозицію).
  - Артефакт `.ai/artifacts/contracts/ADR-018-hmac-test-vector.json`: kid, secret, timestamp, nonce, method, path, body → canonical і підпис.
- **TASK-816 (database-engineer).** Залежності: ADR-018 §4; Q5 закрито звіркою з кодом.
  - Критерії:
    - реєстр tenant має `external_ref`/`external_source`/`suspended_at`/`suspended_reason` і частковий `UNIQUE (external_source, external_ref)`;
    - користувач у стані `invited` без `password_hash` (CHECK);
    - є `platform_owner_invites` (`token_hash` UNIQUE, одне активне на user), `platform_audit_log` (append-only, без `UPDATE/DELETE` для ролі застосунку) і `control_plane_nonces` (PK `(key_id, nonce)`);
    - `beauty_platform_feedback` з FORCE RLS (DO-блок падає без нього);
    - Up→Down→Up на копії;
    - EF-моделі;
    - `worker/src/testing/schema.sql` синхронізовано;
    - тести БД: унікальність `external_ref`, RLS feedback між tenant, append-only аудит;
    - runbook відкату (Down — human gate).
- **TASK-817 (backend-developer).** Залежності: TASK-815 (тестовий вектор). Q1 закрито (D1): секрет лише в env, у БД/аудиті/логах — `kid` і відбиток.
  - Критерії:
    - схема `ControlPlaneHmac` перевіряє в порядку ADR §3.1, порівнює за сталий час і записує nonce лише після валідного підпису;
    - будь-яка невдача → `401 control_auth_failed` з однаковим тілом, причина лише в лог/аудит;
    - ключі `ControlPlane:Inbound:Keys:{kid}` (active/retiring), секрет < 32 байт або нуль активних ключів → група вимкнена;
    - `X-Platform-Key` не відкриває `/api/control`, HMAC не відкриває `/api/platform`;
    - `Auth:PlatformKeyEnabled` (false за замовчуванням у Staging/Production);
    - rate limit 60/хв на kid;
    - тести: тестовий вектор, skew ±301 с, replay nonce, невідомий/retiring kid, змінене тіло/шлях, query у v1 → 400, ліміт тіла.
- **TASK-818 (backend-developer).** Залежності: TASK-816, TASK-817, TASK-815.
  - Критерії:
    - create/get/suspend/resume/owner-invite за ADR §3.2 на тих самих доменних операціях, що й `/api/platform/tenants` (без копії логіки);
    - повторний create → `200` + `Idempotent-Replayed`, розбіжність → `409 external_ref_conflict`, паралельні дублікати дають один tenant (тест);
    - owner створюється в стані `invited`, `owner.password` → `422 password_not_allowed`;
    - suspend → API `403`, login/refresh `401`, refresh-токени відкликано, resume відновлює вхід;
    - публічні ендпоінти запису й віджета призупиненого tenant → `403 tenant_suspended` (D2; спершу звірити чинну поведінку, Q5);
    - поведінку worker і каналів (Q2b) не змінювати до підтвердження;
    - кожна мутація пише `platform_audit_log` з актором і kid;
    - `app.tenant_id` встановлюється лише через `set_config(..., true)`, без BYPASSRLS;
    - get не віддає бізнес-даних;
    - `dotnet test` зелений із `BEAUTY_TEST_REQUIRE_DB=1`.
- **TASK-819 (backend-developer).** Залежності: TASK-816, TASK-815.
  - Критерії:
    - токен 32 байти CSPRNG, у БД лише sha256, TTL з конфігурації;
    - перевипуск відкликає попередній;
    - `inspect` маскує email;
    - `accept` атомарно погашає токен і задає пароль за чинною політикою, повертає відповідь у формі login;
    - прострочений/використаний/відкликаний/невідомий → `410 invite_invalid`, призупинений tenant → `403 tenant_suspended`;
    - паралельні accept — успішний лише один (тест);
    - rate limit 10/хв на IP;
    - токен і пароль не логуються;
    - аудит `owner_invite.accepted`.
- **TASK-820 (backend-developer).** Залежності: TASK-816, TASK-815.
  - Критерії:
    - `POST /api/beauty/feedback` для всіх ролей активного tenant;
    - валідація type/severity/message/pageUrl (`422`);
    - rate limit 5/год на користувача і 30/добу на tenant (`429`);
    - в одній транзакції запис `beauty_platform_feedback` і outbox `platform_feedback.submitted` (ADR-003);
    - `202 { id }`;
    - текст заявки не логується;
    - тести, зокрема RLS між tenant.
- **TASK-821 (integrations-developer, worker/).** Залежності: TASK-820, TASK-817 (тестовий вектор), TASK-829 (вхідний контракт ShelfGuard, Q4).
  - Критерії:
    - доставка підписує запит за v1 ключем `ShelfGuard:Outbound:Keys:{kid}`, тіло за ADR §3.4;
    - повтори з експоненційною затримкою до 24 год, далі `failed` + лог;
    - `delivered_at`/`external_lead_id` оновлюються;
    - без конфігурації ShelfGuard події чекають;
    - очищення `control_plane_nonces` старших за 15 хв;
    - vitest: тестовий вектор, повтор після 5xx, без повтору на 4xx (окрім 429);
    - `npm run build` зелений.
- **TASK-822 (frontend-developer).** Залежності: TASK-819.
  - Критерії:
    - `/invite` читає токен з `location.hash` і прибирає його з адресного рядка (`history.replaceState`);
    - викликає inspect і показує tenant та маскований email;
    - форма пароля з чинною політикою;
    - після accept — вхід в адмінку;
    - стани `410`/`403`/`422`/`429` з текстами;
    - `Referrer-Policy: no-referrer`, `noindex`;
    - mock (`NEXT_PUBLIC_USE_MOCK=1`);
    - `npm test`, `tsc`, `lint`, `build` зелені;
    - a11y форми.
- **TASK-823 (frontend-developer).** Залежності: TASK-820.
  - Критерії:
    - точка входу в адмінці (меню/шапка) відкриває форму «Співпраця / Покращити / Не працює»;
    - критичність для «Не працює»;
    - лічильник символів, підставляється pageUrl;
    - примітка про передачу даних провайдеру (текст — плейсхолдер до human gate Q8);
    - обробка `202`/`422`/`429`;
    - mock, тести;
    - a11y (фокус, оголошення результату).
- **TASK-824 (devops-engineer).** Залежності: ADR-018 §5, §10. Усі зміни на сервері — human gate.
  - Критерії:
    - nginx: `location /api/control/` з allowlist egress-IP ShelfGuard (інше — `403`), без перезапису шляху;
    - `location /api/platform/ { deny all; }` на публічному vhost;
    - bootstrap-env містить імена `ControlPlane__Inbound__Keys__*`, `ShelfGuard__*`, `Auth__PlatformKeyEnabled` без значень;
    - runbook: генерація ключа (≥ 32 байти), ротація kid, оновлення allowlist, перевірка, що ручні скрипти працюють через loopback.
- **TASK-825 (qa-engineer).** Залежності: TASK-816…824, TASK-831, TASK-834. Сценарії включають також зміну модулів (noop, `module_disabled`) і публічний `403 tenant_suspended`.
  - Критерії:
    - таблиця відповідності ADR-018 §3–§6;
    - сценарії: повторний і паралельний create, replay, skew, ротація kid, suspend/resume (вхід, API, refresh), invite (прострочення, повторне використання, перевипуск), feedback (rate limit, доставка, ShelfGuard недоступний);
    - e2e зі stub ShelfGuard, що підписує запити;
    - чеклист ручного smoke.
- **TASK-826 (security-reviewer).** Залежності: TASK-825.
  - Критерії: звіт у `docs/reviews/`, що покриває:
    - реалізацію HMAC (сталий час, порядок перевірок, nonce, вікно);
    - відсутність секретів/токенів/паролів у логах, відповідях і frontend;
    - RLS platform-операцій (без BYPASSRLS, transaction-local tenant);
    - IDOR за `externalRef`;
    - invite (ентропія, одноразовість, фрагмент URL);
    - PII у заявках і аудиті;
    - nginx-шари.
- **TASK-827 (integrations-developer) — репозиторій ShelfGuard.** Залежності: TASK-815, TASK-817 (тестовий вектор).
  - Критерії:
    - серверний клієнт Beauty підписує v1, секрет лише в env сервера ShelfGuard;
    - тестовий вектор проходить;
    - операції create/get/suspend/resume/owner-invite;
    - `externalRef` = id підприємства;
    - повтор create після таймауту безпечний;
    - зберігається лише `tenantId`/`slug`/статус/`inviteExpiresAt` (не URL запрошення);
    - `actor` = поточний адмін ShelfGuard;
    - тести.
- **TASK-828 (frontend-developer) — репозиторій ShelfGuard.** Залежності: TASK-827.
  - Критерії:
    - у картці підприємства: стан у Beauty, кнопки «Створити в Beauty», «Призупинити» (з причиною й підтвердженням), «Відновити», «Нове запрошення»;
    - посилання показується один раз з кнопкою копіювання й попередженням передати його безпечним каналом;
    - помилки `409`/`422`/`401` зрозумілі адміну;
    - браузер не викликає Beauty напряму.
- **TASK-829 (backend-developer) — репозиторій ShelfGuard.** Залежності: TASK-815, Q4; база — гілка `feat/beauty-leads-telegram`.
  - Критерії:
    - вхідний ендпоінт перевіряє підпис v1 ключем напрямку Beauty→ShelfGuard (вікно, nonce);
    - ідемпотентність за `feedbackId`;
    - LandingLead `source=beauty` з типом, критичністю, tenant і користувачем;
    - Telegram-пуш за критичністю;
    - контракт відповіді (коди) передано для TASK-821;
    - тести.
- **TASK-830 (project-manager).** Залежності: TASK-824, TASK-826, TASK-827, TASK-828, TASK-829, TASK-832, TASK-833. Smoke також перевіряє зміну модулів із ShelfGuard.
  - Критерії:
    - human gates пройдено й записано: секрети, nginx, міграції staging, юридичний текст (Q8), прийняття ADR-018 (`accepted`);
    - наскрізний smoke на staging: створення з ShelfGuard → посилання → пароль → вхід → призупинення → відмова → відновлення → заявка доходить у ShelfGuard і Telegram;
    - `documentation-writer` оновив `.claude/docs/` (архітектура, runbook).
- **TASK-831 (backend-developer).** Залежності: TASK-815, TASK-816, TASK-817.
  - Критерії:
    - `PUT /api/control/v1/tenants/{externalRef}/modules` за ADR §3.2 на тій самій доменній операції, що й `PATCH /api/platform/tenants/{id}` `{ modules }`;
    - той самий набір → `200` noop;
    - `404`, `422 unknown_module | modules_required | actor_required`;
    - працює для призупиненого tenant;
    - вимкнений модуль → `403 module_disabled` в API tenant (тест);
    - аудит `tenant.modules_changed` з `before/after` і актором;
    - `dotnet test` зелений із `BEAUTY_TEST_REQUIRE_DB=1`.
- **TASK-832 (integrations-developer) — репозиторій ShelfGuard.** Залежності: TASK-827, TASK-831.
  - Критерії:
    - метод клієнта `setModules(externalRef, modules, actor)` з підписом v1;
    - обробка `404`/`422`;
    - список модулів не хардкодиться без звірки з контрактом TASK-815;
    - тести.
- **TASK-833 (frontend-developer) — репозиторій ShelfGuard.** Залежності: TASK-832.
  - Критерії:
    - у картці підприємства перемикачі модулів Beauty з поточного стану (`GET`);
    - підтвердження перед вимкненням модуля;
    - помилки зрозумілі адміну;
    - браузер не викликає Beauty напряму.
- **TASK-834 (frontend-developer).** Залежності: TASK-818 (контракт TASK-815).
  - Критерії:
    - публічний запис (і віджет, якщо він у `frontend/`; інакше зафіксувати, де він) на `403 tenant_suspended` показує «Запис тимчасово призупинено» без технічних деталей;
    - не показує каталог і слоти;
    - mock-сценарій;
    - `npm test`, `tsc`, `lint`, `build` зелені;
    - a11y (оголошення стану).
