---
name: accessible-components-review
description: >-
  Audit interactive components (modal, menu, tabs, accordion, dropdown) for WCAG 2.2 AA behavior and ARIA patterns. Use when reviewing a built or specced component for accessibility.
x-generated-from: .claude/skills/accessible-components-review/SKILL.md
---

# Accessible Components Review

Джерело: `{PROJECT_SPEC}` (напр. PROJECT_PROMPT.md), розділ доступності. Стосується mobile-меню, modal windows, dropdown — компонентів з власним відкритим/закритим станом.

## Mobile-меню
- Кнопка відкриття має `aria-expanded`, що синхронізується з реальним станом
- При відкритті — focus переходить у меню; `Escape` і клік поза межами закривають
- Пункти меню досяжні Tab-ом у видимому порядку; закрите меню не залишає "невидимі" focusable елементи в DOM tab-порядку

## Modal windows
- Focus trap: Tab всередині модалки циклічний, не "витікає" на контент під нею, поки модалка відкрита
- При закритті — focus повертається на елемент, що викликав відкриття (кнопку/посилання)
- `role="dialog"` + `aria-modal="true"` + `aria-labelledby` на заголовок модалки

## Dropdown
- Відкриття клавіатурою (`Enter`/`Space`/стрілки для навігації опціями), не тільки click/hover
- `aria-expanded`, `aria-haspopup` на тригері
- Вибір опції клавіатурою (`Enter`) закриває dropdown і повертає focus на тригер

## Rules
- Жодна взаємодія не блокується анімацією відкриття/закриття (розділ доступності та розділ motion проєктного спеку) — компонент реагує на клавіатуру/клік одразу, не тільки після завершення transition.
- Ці три компоненти рецензуються `accessibility-specialist` на рівні specs `ui-ux-designer`, до імплементації — не тільки тестуються постфактум.
