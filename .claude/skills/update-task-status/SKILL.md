---
name: update-task-status
description: >-
  Move a task through the lifecycle states and keep the task store consistent. Use when a task's state changes.
x-generated-from: skills/workflow/update-task-status/SKILL.md
---

## Purpose
Визначає правила зміни статусів задач.

## Task States
```
planned -> in_progress -> review -> done
planned -> blocked
blocked -> in_progress (після вирішення блокера)
review -> in_progress (якщо знайдено проблеми)
```

## Files to Update

| Перехід | Файл для оновлення |
|---|---|
| -> in_progress | перемістити задачу з `.claude/tasks/backlog.md` до `.claude/tasks/current.md` |
| -> review | оновити статус у `.claude/tasks/current.md` |
| -> done | перемістити задачу з `.claude/tasks/current.md` до `.claude/tasks/done.md` |
| -> blocked | додати запис до `.claude/tasks/blocked.md` з причиною + створити handoff до `project-manager` |

## Task Format
**ID:** TASK-NNN
**Статус:** planned / in_progress / review / done / blocked
**Агент:** [призначений агент]
**Пріоритет:** critical / high / medium / low
**Залежності:** TASK-YYY (або none)
**Оновлено:** YYYY-MM-DD

## Rules
- Оновлювати статус одразу при зміні стану, не постфактум наприкінці дня
- Завжди вказувати дату оновлення
- Блокер = конкретна причина + хто може її вирішити (найчастіше — рішення користувача або `project-architect`)
- Task ID ніколи не перевикористовується, навіть якщо задачу скасовано
