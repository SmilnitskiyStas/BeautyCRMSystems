---
name: create-task-log
description: >-
  Write a task log for completed work — what changed, decisions, test status. Use when finishing any task.
x-generated-from: skills/workflow/create-task-log/SKILL.md
---

## Purpose
Стандартизує створення task log файлу після завершення роботи агента.

## File Location
```
.claude/logs/tasks/
```

## File Naming
```
TASK-ID_YYYY-MM-DD_короткий-опис_агент.md
```
Приклад: `TASK-014_2026-08-03_hero-section_frontend-developer.md`

## Template
```markdown
# TASK-XXX: [Назва]

**Дата:** YYYY-MM-DD
**Агент:** [назва агента]
**Статус:** done
**Тривалість:** [оцінка]

## Що зроблено
[Короткий опис виконаної роботи]

## Змінені файли
- `src/шлях/до/файлу.tsx` — [що змінилось]
- `.claude/docs/файл.md` — [що змінилось]

## Прийняті рішення
[Рішення, ухвалені під час виконання; нетривіальні архітектурні рішення продублювати в `.claude/docs/decisions.md`]

## Тести
- [ ] Unit-тести написані
- [ ] `tsc --noEmit` проходить
- [ ] `npm run lint` проходить
- [ ] `npm run build` проходить
- [ ] Ручна перевірка пройдена

## Нотатки
[Важливе для наступного агента]
```

## When to Create
- Після завершення кожної задачі (статус → `done`)
- Навіть для маленької задачі — лог обов'язковий
- Task log ≠ handoff: лог фіксує факт виконання; handoff (`create-handoff.md`) передає естафету далі
