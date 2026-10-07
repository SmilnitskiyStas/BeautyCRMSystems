---
name: create-handoff
description: >-
  Write a handoff record and run the Interaction Gate before passing a task to another agent. Use when work transfers between agents or hits a blocker.
x-generated-from: skills/workflow/create-handoff/SKILL.md
---

## Purpose
Стандартизує передачу роботи між агентами.

## File Location
```
.claude/logs/handoffs/
```

## File Naming
```
TASK-ID_YYYY-MM-DD_від-агента_до-агента.md
```
Приклад: `TASK-014_2026-08-03_frontend-developer_qa-engineer.md`

## Template
```markdown
# Handoff: TASK-XXX

**Дата:** YYYY-MM-DD
**Від:** [агент]
**До:** [агент]
**Задача:** [назва]

## Що завершено
[Короткий підсумок виконаної роботи]

## Що потрібно зробити далі
1. [Конкретний пункт]
2. [Конкретний пункт]

## Важливий контекст
[Що наступному агенту треба знати — рішення, обмеження, залежності]

## Ризики / Блокери
[Відомі ризики або потенційні блокери]

## Файли для перегляду
- `шлях/до/файлу` — [чому важливо]

## Definition of Done для наступного агента
- [ ] [Критерій 1]
- [ ] [Критерій 2]
```

## Rules
- Handoff обов'язковий, якщо результат передається іншому агенту в ланцюжку
- Наступний агент читає handoff як частину `context-loader.md` (крок "Recent history") перед стартом
- **Якщо є блокер — handoff іде до `project-manager`, а не до наступного агента в послідовності.** `project-manager` вирішує, кому і коли передати задачу далі
- Один handoff = одна передача; не змішувати кілька задач в одному файлі
