---
name: design-theme-system
description: >-
  Design Theme System — Use when producing the design foundation — tokens, specs, theme, motion.
x-generated-from: skills/ui-ux-design/design-theme-system/SKILL.md
---

## Purpose
Специфікує dark/light theme-систему перед імплементацією theme-toggle (`frontend-developer`).

## Requirements Checklist
- [ ] Основна тема (dark або light) відповідає обраній візуальній концепції
- [ ] Перемикання між dark/light (де це передбачає концепція)
- [ ] Вибір користувача зберігається (cookie або localStorage) між сесіями
- [ ] Підтримка системної теми (`prefers-color-scheme`) як стан за замовчуванням, поки користувач не обрав вручну
- [ ] Відсутність мерехтіння теми під час завантаження (no flash of wrong theme)

## Spec Details to Define
1. Джерело істини для поточної теми (cookie, читаний на сервері, щоб перший SSR-рендер вже був правильним — уникнути client-only рішення, яке й спричиняє flash).
2. Порядок пріоритету: збережений вибір користувача → системна тема → дефолт концепції.
3. Візуальна специфікація toggle-контролу (іконка/позиції/анімація переходу — коротка, без стрибків контенту).
4. Токени, що відрізняються між темами (з `define-design-tokens.md`) — явний перелік, а не "все, що не назване".
5. Контраст перевірений окремо для кожної теми (WCAG 2.2 AA).

## Guardrails
- Ніколи не покладатись лише на client-side `useEffect` для визначення теми при першому рендері — це і є джерело flash (див. `.claude/skills/nextjs-marketing-architect/SKILL.md`, "no-flash theme toggle").
- Перемикання теми не повинно скидати позицію скролу чи стан форми.

## Output
Специфікація theme-системи (джерело істини, пріоритет, токени по темах) — вхід для `frontend-developer`, реалізується разом із root layout.
