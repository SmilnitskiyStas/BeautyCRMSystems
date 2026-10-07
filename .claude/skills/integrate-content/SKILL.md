---
name: integrate-content
description: >-
  Integrate Content — Use when building the user-facing web layer.
x-generated-from: skills/frontend/integrate-content/SKILL.md
---

## Purpose
Визначає, як компоненти споживають типізований content layer (MDX або headless CMS) — жодна секція не хардкодить копірайт.

## Pattern
1. Контент визначається за моделлю з `.claude/skills/product-design/create-content-model.md` (case study — 24 поля, industry page — своя модель; точний перелік полів фіксується в проєктній специфікації) і типізується `zod`-схемою в `content/` або через CMS-клієнт у `lib/`.
2. Читання контенту — окрема функція в `content/` чи `lib/`, що повертає вже провалідовані типізовані дані; компонент її викликає, не парсить сирий MDX/CMS-response самостійно.
3. Page-компонент (`create-page-route.md`) отримує контент і передає його вниз як типізовані props у section-компоненти (`create-section-component.md`) — компонент секції ніколи сам не імпортує MDX-файл чи CMS SDK.
4. Локалізований контент — окремий запис на мову (MDX frontmatter `locale` або поле CMS), не runtime-переклад (без client-side машинного перекладу під час завантаження сторінки).

## Rules
- Малі UI-лейбли (кнопки, aria-labels) — через `next-intl` messages; великий редакційний контент (кейси, статті, послуги) — через content layer, не messages-файли.
- Короткі тексти теж не хардкодяться напряму в JSX, якщо вони підлягають редагуванню без деплою.
- Невалідний контент (не пройшов zod-схему) — падає на етапі build/fetch із зрозумілою помилкою, не рендериться "якось" у production.
- Перехід з MDX на headless CMS (архітектурна вимога проєкту — рішення про контент-шар повинно це дозволяти) не повинен вимагати переписування section-компонентів — тільки шар читання контенту.

## Anti-patterns
- `<h1>Наші послуги</h1>` захардкоджений у секції замість `{content.title}`.
- Компонент, що напряму імпортує `fs`/MDX-парсер чи CMS SDK замість виклику функції з `content`/`lib`.
