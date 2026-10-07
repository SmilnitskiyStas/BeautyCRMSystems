<!--
  ШАБЛОН. Скопіювати як CLAUDE.md у корінь нового проєкту, поруч із .claude/
  (згенерованим адаптером) і власним spec-документом.

  Після копіювання:
  1. Заповнити секцію "Проєкт" — прибрати {PROJECT_NAME}/{PROJECT_SPEC}.
  2. Звірити "Технологічний стек" з реальним стеком; обрати stack pack у skills/stacks/.
  3. У "Agent → Task" прибрати/додати рядки під фактичний ростер `.claude/agents/`.
  4. Обрати варіант backend-шару: fullstack-developer АБО backend-developer + database-engineer.
  5. Видалити цей коментар.
-->

# CLAUDE.md

## Проєкт

`{PROJECT_NAME}` — [одне речення: що це за продукт і для кого].

[2-4 речення: бізнес-мета, аудиторія, чим відрізняється від альтернатив.]

**`{PROJECT_SPEC}`** (напр. `PROJECT_PROMPT.md`, `SPEC.md`) — джерело істини для вимог.
Читати релевантну секцію перед будь-якою нетривіальною задачею. Якщо вимог кілька
документів — перелічити тут з описом покриття.

## Технологічний стек

Перед активною розробкою перевірити актуальні стабільні версії залежностей.

| Шар | Технології |
|---|---|
| Frontend | [напр. Next.js App Router, React, TypeScript, Tailwind, shadcn/ui] |
| Forms & validation | [напр. React Hook Form, Zod] |
| i18n | [напр. next-intl — за потреби] |
| Дані | [напр. PostgreSQL / Supabase — фіксує ADR] |
| Хостинг | [напр. Vercel / Docker — фіксує ADR] |
| CI/CD | [напр. GitHub Actions] |

Обраний stack pack: `skills/stacks/{stack}` — постачає фреймворк-специфіку; ролі в
`.claude/agents/` залишаються stack-нейтральними.

## Команди розробки

Заповнити після технічного сетапу (`dev/build/lint/typecheck/test`). Синхронізувати з
`README.md` і `.claude/settings.local.json`.

## Архітектура

Описати планову структуру каталогів і ключові правила. Рішення щодо хостингу,
контент-шару, БД — через ADR (`skills/workflow/create-adr`, `templates/adr-template.md`).

## Multi-Agent Workflow

**Головне правило:** головна CLI-сесія **оркеструє**, не імплементує. Код/дизайн/контент
пишуть спеціалізовані субагенти (`.claude/agents/*`).

**Механізм (нативні субагенти).** Claude Code сам делегує задачу відповідному субагенту за
його `description`. Явно: «use the `backend-developer` subagent» або `@backend-developer`.
Патерн «спавн `general-purpose` + прочитай persona.md» більше не використовується.

**Ручний override має пріоритет** над авто-роутингом, якщо не порушує safety/project
rules (`workflow/model-routing.md` §manual): "use `X`", "use reasoning tier", "do not use
Opus", "do not spawn reviewer".

**Ворота уточнення перед делегуванням.** Якщо задача вимагає продуктового / UX / брендового
рішення, яке може ухвалити лише людина — запитати **до** делегування. Повністю
специфіковані задачі йдуть одразу. Якщо агент під час роботи натрапляє на нерозв'язне
питання — зупиняється й повертає результат (`workflow/escalation-policy.md`).

**Головна сесія діє напряму, без субагента:** читання/дослідження; `typecheck` / `lint` /
`git status` / `git push`; правки < ~10 рядків; архітектурні Q&A без коду.

### Model tiers

`cheap` / `standard` / `reasoning`. Кожен агент має `default_model_tier`; router підвищує
за ризиком, знижує для тривіальних задач. Правила — `workflow/model-routing.md`.
Резолвинг у модель — `adapters/claude/model-map.yaml` (не хардкодити в агентах).

### Agent → Task

| Тип задачі | Агент |
|---|---|
| Класифікація запиту, вибір агента/tier | `task-intake` |
| Уточнення неоднозначних вимог (`grill-me`) | `requirements-analyst` |
| Пошук релевантного коду/патернів/контрактів | `project-researcher` / native `Explore` |
| Збірка minimum-sufficient контексту | `context-manager` |
| Архітектурне рішення / ADR / декомпозиція спеку | `project-architect` |
| Backlog / статуси / handoff-координація / daily | `project-manager` |
| Які review потрібні на зміну | `review-orchestrator` |
| Сторінка / секція / компонент / форма / i18n / theming | `frontend-developer` |
| Server Action / route handler / легкий колокований backend | `fullstack-developer` |
| API endpoint / service-шар окремого backend-сервісу¹ | `backend-developer` |
| Схема БД / міграції / індекси для великої моделі даних¹ | `database-engineer` |
| Мобільний застосунок (RN/Expo, offline, push) | `mobile-developer` |
| CI/CD / хостинг / env / деплой | `devops-engineer` |
| Тести (unit/e2e/SEO), крос-браузер, Lighthouse-пороги | `qa-engineer` |
| Аудит безпеки форм / auth / secrets (тільки рев'ю) | `security-reviewer` |
| Discovery: позиціонування / sitemap / контент-модель | `product-designer` |
| Design tokens / specs компонентів / theme / motion | `ui-ux-designer` |
| WCAG-аудит дизайну й реалізації | `accessibility-specialist` |
| Metadata / structured data / keyword-стратегія | `seo-specialist` |
| Hero/CTA / friction форм / аналітична таксономія | `cro-specialist` |
| Копірайтинг / наратив кейсів / блог / локалізація | `copywriter` |
| Живі `.claude/docs/*` і кореневі документи | `documentation-writer` |

¹ `backend-developer` + `database-engineer` — **альтернатива** `fullstack-developer`, не
доповнення. Обрати один варіант; видалити зайві рядки й `.claude/agents/` файли.

### Процедура кожного субагента

Визначена в `workflow/` — не повторювати тут:
`context-policy.md` (що читати) → перевірка залежностей DAG → реалізація лише своєї
відповідальності → task log (`templates/task-log-template.md`) → handoff за потреби
(`handoff-policy.md`; блокер → `project-manager`) → оновлення статусу задачі
(`task-lifecycle.md`) → оновлення `.claude/docs/*`, якщо змінилась архітектура/дані.

### Artifacts, DAG, review, human gates

- Агенти обмінюються результатами через `.ai/artifacts/**` (`workflow/artifact-policy.md`).
- Задачі — граф залежностей, перевага artifact-залежностям (`workflow/task-dag.md`).
- Cross-agent запити проходять Interaction Gate (`workflow/agent-communication-policy.md`).
- Review робить незалежний агент, не сесія-імплементатор; ліміт циклів
  (`workflow/review-policy.md`).
- Irreversible/high-impact дії — human gate (`workflow/human-gates.md`).

## Документація

`.claude/docs/` — жива документація, веде `documentation-writer` за тригерами
(`workflow` — architecture/API/schema/integration/deployment/security/convention змінились).
`known-issues.md` — скопіювати скелет `templates/known-issues-doc-template.md`; кожен агент
робить retrieval релевантних lessons перед задачею (не читає весь файл).

## Rules hierarchy & source of truth

Порядок пріоритету при конфлікті — `workflow/rules-hierarchy.md`. Яке сховище
авторитетне для якого факту — `workflow/source-of-truth.md`. Документ, що суперечить коду,
— stale: позначити й виправити окремою задачею, не діяти за ним.

## Token efficiency

Посилатися на документи за назвою, не вставляти повний вміст. Один feature/шар за раз.
Task log і фінальні звіти — стисло: що зроблено, статус build/test, знайдені проблеми.
