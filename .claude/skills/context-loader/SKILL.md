---
name: context-loader
description: >-
  Decide the minimum-sufficient set of files an agent must read before starting a task. Use at the start of any task to build a context package.
x-generated-from: skills/workflow/context-loader/SKILL.md
---

## Purpose
Визначає, які файли агент має прочитати ПЕРЕД початком будь-якої задачі.

## Required Read Order

### 1. Always (будь-який агент)
- `CLAUDE.md` — стек, архітектура, multi-agent workflow, правила проєкту
- `.claude/tasks/current.md` — що зараз в роботі
- `.claude/docs/known-issues.md` (розділ "Resolved Issues") — чи ця задача не наступає на вже відому граблю; коротко, шукати за `Area`/ключовим словом, не читати весь файл лінійно, коли він виросте

### 2. Domain context
- `{PROJECT_SPEC}` (напр. PROJECT_PROMPT.md) — єдине й повне джерело істини для вимог проєкту, розбите на пронумеровані секції. На відміну від проєктів з декількома `v1-spec.md`…`v4-spec.md`, тут один документ покриває все ТЗ — читати релевантну(і) секцію(ї), а не файл цілком щоразу
- Розділ "Context to Load" у власному файлі агента `.claude/agents/{роль}.md` — уточнює конкретні §-номери для цієї ролі

### 3. Architecture & living docs
- `.claude/docs/architecture.md`
- `.claude/docs/domain-model.md` або `.claude/docs/content-model.md` — залежно від типу задачі
- `.claude/docs/decisions.md` — перед будь-яким архітектурним рішенням

### 4. Task-specific
- `.claude/docs/frontend-structure.md` / `.claude/docs/fullstack-structure.md` — frontend/fullstack задачі
- `.claude/docs/database-schema.md` — задачі з даними (leads/testimonials/…)
- `.claude/docs/seo-map.md` — SEO/контентні задачі
- `.claude/docs/api-contracts.md` — задачі з Server Actions/Route Handlers

### 5. Recent history
- Останні 2-3 файли в `.claude/logs/handoffs/`
- Task log поточної задачі в `.claude/logs/tasks/`, якщо є

## Anti-patterns
- Не починати роботу без прочитання `CLAUDE.md`
- Не перечитувати весь `{PROJECT_SPEC}` щоразу — цілитись у релевантну секцію (Token Efficiency)
- Не ухвалювати архітектурні рішення без перевірки `decisions.md`
- Не хардкодити копірайт "бо контент-модель ще не прочитана"
- Наступити на вже задокументовану в `known-issues.md` граблю, бо файл не перевірили перед стартом
