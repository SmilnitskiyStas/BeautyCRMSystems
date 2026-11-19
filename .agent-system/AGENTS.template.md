<!--
  ШАБЛОН для Codex CLI. Скопіювати як AGENTS.md у корінь проєкту.
  Codex завантажує цей файл щотурну — тримати < 150 рядків.
  Повноцінний Codex-адаптер (`.codex/agents/*.toml`, `adapters/codex/`) — Phase 6.
  Наразі: ролі описані інструкційно тут; делегування — через ці правила.
  Видалити цей коментар.
-->

# AGENTS.md

## Проєкт

`{PROJECT_NAME}` — [одне речення]. Джерело істини для вимог — `{PROJECT_SPEC}`.
Стек: [коротко]. Обраний stack pack: `skills/stacks/{stack}`.

## Робота через ролі

Кожна нетривіальна задача виконується у ролі з `agents/` (та сама бібліотека, що й для
Claude Code). Делегувати спеціалізованому агенту; складні паралельні задачі —
розбивати на субагентів (`multi_agent`).

Ручний override користувача має пріоритет над авто-вибором.

**Ворота уточнення:** продуктове/UX/брендове рішення, яке може ухвалити лише людина, —
питати до початку роботи, не здогадуватись.

**Головна сесія напряму:** читання/дослідження, `typecheck`/`lint`/`git status`,
правки < ~10 рядків, архітектурні Q&A.

## Model tiers

`cheap` / `standard` / `reasoning` → `adapters/codex/model-map.yaml` (Phase 6).
Правила підвищення/зниження — `workflow/model-routing.md`.

## Ролі

Див. `agents/**` — 23 ролі: control (`task-intake`, `requirements-analyst`,
`project-researcher`, `context-manager`, `project-architect`, `project-manager`,
`review-orchestrator`, `integration-steward`), engineering (`frontend-`, `backend-`, `fullstack-`, `database-`,
`mobile-`, `devops-`, `qa-`, `security-*`), product (`product-designer`, `ui-ux-designer`,
`accessibility-`, `seo-`, `cro-`, `copywriter`), documentation (`documentation-writer`).
Кожна має `responsibilities`, `must_not`, tier, `handoff_targets`.

## Процедура

`workflow/context-policy.md` (що читати) → перевірка залежностей → реалізація лише своєї
відповідальності → task log → handoff (`handoff-policy.md`; блокер → `project-manager`) →
статус задачі → `docs/` за потреби.

## Artifacts / DAG / review / human gates

- Обмін результатами — `.ai/artifacts/**` (`workflow/artifact-policy.md`).
- Задачі — граф; перевага artifact-залежностям (`workflow/task-dag.md`).
- Cross-agent запити — через Interaction Gate (`workflow/agent-communication-policy.md`).
- Review — незалежний агент; ліміт циклів (`workflow/review-policy.md`).
- Irreversible/high-impact — human gate (`workflow/human-gates.md`).

## Rules hierarchy

`workflow/rules-hierarchy.md`; джерела істини — `workflow/source-of-truth.md`.
Документ проти коду = stale.
