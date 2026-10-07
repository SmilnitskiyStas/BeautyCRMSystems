---
description: Refactor a module to improve architecture, readability, and maintainability without changing behavior
argument-hint: <file path or module name>
---

# refactor-module.md

Рефакторити модуль, вказаний тут: $ARGUMENTS

Діяти як senior fullstack architect для кодової бази `{PROJECT_NAME}`.

Workflow:

1. Прочитати `CLAUDE.md`.
2. Оглянути цільовий модуль.
3. Визначити проблеми: неправильна межа Server Component / Client Component, хардкоджений текст, що має бути в типізованому content/MDX, прогалини i18n-паритету, дублювання, типізація, змішана відповідальність.
4. Запропонувати план рефакторингу до зміни коду.
5. Не змінювати поведінку, якщо це не запитано явно.
6. Якщо рефакторинг маленький і механічний — застосувати напряму. Якщо структурний (нові файли, перенесена відповідальність, змінені публічні контракти) — передати план `frontend-developer` або `fullstack-developer` (залежно, який шар володіє модулем) для імплементації, згідно з правилом стриманості головної сесії з `CLAUDE.md`.
7. Оновити тести, якщо застосовно.
8. Оновити `.claude/docs/*` (через `documentation-writer`), якщо змінилась архітектура.

Правила:
- Без зайвих переписувань.
- Без нових залежностей без обґрунтування.
- Зберігати публічні API/Server Action контракти, якщо не узгоджено інше.
- Пріоритет — читабельність і підтримуваність, а не винахідливість.

Output format:
- Current problems
- Refactor plan
- File structure changes
- Code changes (або handoff, якщо передано далі)
- Risk notes
- Final summary
