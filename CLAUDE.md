# CLAUDE.md

## ОБОВ'ЯЗКОВИЙ ПЕРШИЙ КРОК (entry protocol)

Для **кожного** нового запиту користувача, окрім тривіальних (читання файлу, питання без коду),
головна сесія ПЕРШИМ викликом запускає субагента `task-intake` (Agent tool,
`subagent_type: "task-intake"`), передаючи текст запиту. Лише після його класифікації
(`task_type`, `complexity`, `risk`, `candidate_agents`, `requires_*`) сесія:
1. показує користувачу коротке резюме: тип задачі, обрані агенти й порядок;
2. якщо `requires_human_clarification` або `ambiguity: high` — ставить питання (`AskUserQuestion`);
3. запускає агентів за `candidate_agents` у порядку залежностей (`.claude/workflow/task-dag.md`),
   незалежних — паралельно; `project-manager` веде статуси;
4. після реалізації викликає `review-orchestrator`, щоб визначити потрібні рев'ю.

Субагенти не можуть запускати інших субагентів, тому оркеструє завжди головна сесія.
Коротка команда: `/start <завдання>` (`.claude/commands/start.md`).

## Проєкт

`Beauty CRM` — B2B-платформа для б'юті-сфери (салони, барбершопи, косметологи, мережі).

CRM клієнтів, календар майстрів (запис = тривалість послуги), послуги й акції, онлайн-запис
з оплатою й нагадуваннями, канали (Telegram, Instagram, Facebook, WhatsApp, Viber, віджет),
AI-асистент з регульованою автономністю, аналітика мережі.

**`BEAUTY_CRM_PROMPT.md`** — джерело істини для вимог (мета, агенти, TASK-674…683,
Definition of Done). Читати релевантну секцію перед нетривіальною задачею.
Стек і розміщення в коді (A1) підтверджуються на gate з цього файлу.

**Рішення gate (2026-10-07):**
- A1: Next.js + ASP.NET Core 8 + PostgreSQL 16 (RLS, multi-tenant), модульний моноліт у цьому репозиторії.
- A2: скасування за 12 год і менше до візиту → повернення 50% (на картку, якщо платили карткою).
- A3: платіжний провайдер не обрано → `IPaymentService` без прив'язки (рекомендація з промпту).
- Канали першого релізу: Telegram + Instagram (Instagram спершу через mock-адаптер).
- AI-режим за замовчуванням: `confirm` («Готує, я підтверджую»).

## Стек і структура

- Frontend: Next.js 16 (App Router), React 19, TypeScript, Tailwind 4 — `frontend/`.
- Backend: ASP.NET Core 8 (net8.0), EF Core 8 + Npgsql, PostgreSQL 16 з RLS (multi-tenant, `app.tenant_id`) — `backend/` (Domain, Application, Infrastructure, Api, Tests).
- Worker: Node + TypeScript (tsx, vitest) — `worker/`: нагадування, outbox, поллери каналів.
- `scripts/dev/` — локальне dev-середовище (PowerShell, PostgreSQL :55432); `docs/reviews/` — звіти рев'ю; `.claude/docs/` — живі документи.
- Ролі в `.claude/agents/` залишаються stack-нейтральними.

## Команди розробки

Повний порядок — `.claude/docs/runbook.md` (§4–§8). Коротко:
- Backend: `$env:BEAUTY_TEST_REQUIRE_DB="1"; dotnet test backend` (потрібен Docker); API: `dotnet run --project BeautyCrm.Api --no-launch-profile` (порт 5000).
- Worker: `npm.cmd test`, `npm.cmd start`, `npm run build` (tsc --noEmit).
- Frontend: `npm.cmd test`, `npx tsc --noEmit`, `npm.cmd run lint`, `npm.cmd run build`; demo без backend — `npm.cmd run dev` з `NEXT_PUBLIC_USE_MOCK=1`.
- Локальний стек: `scripts/dev/*.ps1` (setup-db, start-api, start-worker, seed, status).

## Архітектура

Модульний моноліт (ADR-001), ізоляція tenant через RLS (ADR-002), transactional outbox + worker (ADR-003). Рішення щодо хостингу, даних і контенту — через ADR (`.claude/docs/adr/`; шаблон і створення — `skills/workflow/create-adr`, `.claude/templates/adr-template.md`).

## Multi-Agent Workflow

**Головне правило:** головна CLI-сесія **оркеструє**, не імплементує. Код/дизайн/контент
пишуть спеціалізовані субагенти (`.claude/agents/*`).

**Механізм (нативні субагенти).** Claude Code сам делегує задачу відповідному субагенту за
його `description`. Явно: «use the `backend-developer` subagent» або `@backend-developer`.
Патерн «спавн `general-purpose` + прочитай persona.md» більше не використовується.

**Ручний override має пріоритет** над авто-роутингом, якщо не порушує safety/project
rules (`.claude/workflow/model-routing.md` §manual): "use `X`", "use reasoning tier", "do not use
Opus", "do not spawn reviewer".

**Ворота уточнення перед делегуванням.** Якщо задача вимагає продуктового / UX / брендового
рішення, яке може ухвалити лише людина — запитати **до** делегування. Повністю
специфіковані задачі йдуть одразу. Якщо агент під час роботи натрапляє на нерозв'язне
питання — зупиняється й повертає результат (`.claude/workflow/escalation-policy.md`).

**Головна сесія діє напряму, без субагента:** читання/дослідження; `typecheck` / `lint` /
`git status` / `git push`; правки < ~10 рядків; архітектурні Q&A без коду.

### Model tiers

`cheap` / `standard` / `reasoning`. Кожен агент має `default_model_tier`; router підвищує
за ризиком, знижує для тривіальних задач. Правила — `.claude/workflow/model-routing.md`.
Резолвинг у модель — `.agent-system/adapters/claude/model-map.yaml` (не хардкодити в агентах).

### Agent → Task

Вибір агента — за `description` субагентів (видно в кожній сесії) і класифікацією `task-intake`; повна карта ролей — `.claude/agents/`. Backend: `backend-developer` + `database-engineer` (не `fullstack-developer`). Тільки рев'ю: `security-reviewer`, `review-orchestrator`; документи: `documentation-writer`.

### Процедура кожного субагента

Визначена в `workflow/` — не повторювати тут:
`context-policy.md` (що читати) → перевірка залежностей DAG → реалізація лише своєї
відповідальності → task log (`.claude/templates/task-log-template.md`) → handoff за потреби
(`handoff-policy.md`; блокер → `project-manager`) → оновлення статусу задачі
(`task-lifecycle.md`) → оновлення `.claude/docs/*`, якщо змінилась архітектура/дані.

### Artifacts, DAG, review, human gates

- Агенти обмінюються результатами через `.ai/artifacts/**` (`.claude/workflow/artifact-policy.md`).
- Задачі — граф залежностей, перевага artifact-залежностям (`.claude/workflow/task-dag.md`).
- Cross-agent запити проходять Interaction Gate (`.claude/workflow/agent-communication-policy.md`).
- Review робить незалежний агент, не сесія-імплементатор; ліміт циклів
  (`.claude/workflow/review-policy.md`).
- Irreversible/high-impact дії — human gate (`.claude/workflow/human-gates.md`).
- Паралельні гілки worktree-агентів зводить лише `integration-steward` в `integrate/*`;
  merge у base-гілку — human gate (`.claude/workflow/parallel-work.md`). `worktree.baseRef: head`.

## Документація

`.claude/docs/` — жива документація, веде `documentation-writer` за тригерами
(`workflow` — architecture/API/schema/integration/deployment/security/convention змінились).
`known-issues.md` — скопіювати скелет `.claude/templates/known-issues-doc-template.md`; кожен агент
робить retrieval релевантних lessons перед задачею (не читає весь файл).
- `.claude/docs/beauty-contracts.md` — контракти API/сутностей, великий файл: не відкривати цілком; `Grep` за `^## §` (список розділів з номерами рядків; напр. `^## §22\.`) і `Read` з `offset`/`limit` лише потрібного розділу.
- ADR — `.claude/docs/adr/`; звіти рев'ю — `docs/reviews/` (у git, не ігнорується).
- `.claude/settings.json` → `permissions.deny` закриває від читання `.env*`, `node_modules`, `bin/obj`, lock-файли, міграційні Designer/Snapshot, `worker/src/testing/schema.sql` і стан dev-скриптів.

## Rules hierarchy & source of truth

Порядок пріоритету при конфлікті — `.claude/workflow/rules-hierarchy.md`. Яке сховище
авторитетне для якого факту — `.claude/workflow/source-of-truth.md`. Документ, що суперечить коду,
— stale: позначити й виправити окремою задачею, не діяти за ним.

## Compact instructions

Коли контекст стискається (`/compact` або авто) — зберегти: поточний TASK-ID і його статус,
прийняті рішення/ADR з причинами, список змінених файлів, невирішені питання та блокери,
результат останнього запуску тестів/build (лише падіння). Не зберігати: повні виводи тестів/логів,
повністю прочитані файли, проміжні роздуми. Між несумісними задачами — `/clear`; кінець етапу —
skill `handoff-session`. Не міняти модель посеред сесії (кожна модель має власний кеш).

## Token efficiency

Посилатися на документи за назвою, не вставляти повний вміст. Один feature/шар за раз.
Task log і фінальні звіти — стисло: що зроблено, статус build/test, знайдені проблеми.
