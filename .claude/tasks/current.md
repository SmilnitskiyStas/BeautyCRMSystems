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
