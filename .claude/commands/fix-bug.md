---
description: Investigate and fix a bug with minimal, production-safe changes
argument-hint: <bug description or affected file/module>
---

# fix-bug.md

Дослідити й виправити баг, описаний тут: $ARGUMENTS

Workflow:

1. Прочитати `CLAUDE.md`.
2. Визначити зачеплену фічу/модуль і агента-власника (`frontend-developer` — UI/компонент/i18n; `fullstack-developer` — Server Action/дані/email).
3. Оглянути мінімальний релевантний набір файлів.
4. Пояснити ймовірну першопричину.
5. Запропонувати найбезпечніший фікс.
6. Якщо фікс маленький (орієнтовно до ~10 змінених рядків — межа винятків `CLAUDE.md` для правок напряму в головній сесії) — застосувати напряму. Інакше — передати агенту-власнику з кроку 2 через task log + handoff, а не розширювати обсяг у головній сесії.
7. Додати або оновити тести, якщо застосовно.
8. **Якщо це був не тривіальний typo, а реальний debugging** — застосувати skill `root-cause-analysis`, потім записати `Root cause` + `Prevention` в `.claude/docs/known-issues.md` за skill `resolve-known-issue`, поки причина ще свіжа в пам'яті. Якщо причина узагальнюється — перенести правило й у відповідний skill/agent-файл (не лише в лог).
9. Пояснити, що змінилось і чому.

Правила:
- Не переписувати непов'язаний код.
- Не додавати нові залежності без обґрунтування.
- Фікс мінімальний, але production-safe.
- Зберігати наявну архітектуру (`.claude/docs/architecture.md`, відповідна секція `{PROJECT_SPEC}`, напр. `PROJECT_PROMPT.md`, `v1-spec.md` тощо).
- Якщо баг знайдений `qa-engineer` — залогувати через `templates/bug-report-template.md` до або поряд із фіксом.
- Не закривати задачу без запису в `known-issues.md`, якщо це був реальний bug, а не одруку — фікс без зафіксованої причини легко повториться.

Output format:
- Root cause
- Files inspected
- Fix plan
- Code changes (або handoff, якщо передано далі)
- Test notes
- Final summary
