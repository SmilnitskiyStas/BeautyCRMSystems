---
name: add-motion-animation
description: >-
  Add Motion Animation — Use when building the user-facing web layer.
x-generated-from: skills/frontend/add-motion-animation/SKILL.md
---

## Purpose
Імплементує анімацію за specs від `ui-ux-designer` (`.claude/skills/ui-ux-design/specify-motion-guidelines.md`) з обов'язковим урахуванням `prefers-reduced-motion`.

## Pattern
1. Framer Motion `motion.*`-компоненти лише в `"use client"` дочірніх компонентах — не позначати всю секцію client через одну анімацію.
2. Перед застосуванням transform/opacity-анімації перевірити `useReducedMotion()` (Framer Motion hook) або медіа-запит `prefers-reduced-motion: reduce` — за true показувати миттєвий/спрощений стан замість руху.
3. Анімувати лише `opacity`/`transform` (translate/scale) — уникати анімації властивостей, що викликають layout/reflow (width/height/top/left).
4. `whileInView` з `once: true` для появи секцій при скролі — не переанімовувати при кожному повторному вході в область видимості.
5. Тривалість і easing — за specs з `specify-motion-guidelines.md`, не довільні значення на власний розсуд.

## Rules
- Жодна анімація не блокує клік/скрол/сабміт форми, поки триває.
- Без нескінченних loop-анімацій; без важких 3D (напр. React Three Fiber) без явного бізнес-обґрунтування.
- Parallax — тільки для суто декоративних елементів, ніколи для контенту чи interactive-елементів.
- Курсорні ефекти (cursor-follow тощо) — лише за `(hover: hover) and (pointer: fine)` (desktop), вимикати на touch.
- Динамічний `import()` важких анімаційних компонентів, якщо вони не критичні для above-the-fold контенту.

## Anti-patterns
- Анімація, що ігнорує `prefers-reduced-motion`.
- `"use client"` на всій секції замість ізоляції анімованого фрагмента.
- Автозапуск відео зі звуком під приводом "анімований hero".
