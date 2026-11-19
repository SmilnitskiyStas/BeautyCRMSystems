---
name: semantic-html-aria-review
description: >-
  Review markup for semantic HTML, correct landmarks/labels, and ARIA used only where needed. Use when auditing page structure or form accessibility.
---

# Semantic HTML ARIA Review

Джерело: `{PROJECT_SPEC}` (напр. PROJECT_PROMPT.md), розділ доступності (accessibility).

## Правило пріоритету
Semantic HTML спочатку, ARIA — тільки коли семантики недостатньо ("No ARIA is better than bad ARIA"). Не `<div role="button">`, коли є `<button>`.

## Checklist
- Заголовки (`h1`–`h6`) — один логічний `h1` на сторінку, без пропуску рівнів заради стилю
- Навігація в `<nav>`, основний контент в `<main>`, не generic `<div>` всюди
- Кожен `<input>`/`<select>`/`<textarea>` має пов'язаний `<label>` (через `htmlFor`/`id`, не тільки placeholder)
- Помилки валідації форми оголошуються assistive-технології (`aria-describedby` на полі + `aria-live` регіон або `role="alert"` на повідомленні) — не тільки візуальний червоний текст
- Кнопки без видимого тексту (іконки: закрити, меню, theme-switch) мають `aria-label`
- Немає `<div>`/`<span>` з `onClick` замість `<button>`

## Rules
- ARIA-атрибут додається тільки коли є конкретна причина (немає нативного семантичного еквівалента) — не "про всяк випадок" на кожному елементі.
- Кожна форма (коротка, розширена, review) проходить цей чеклист окремо — вони не ідентичні за полями.
- Placeholder ніколи не замінює `<label>`.
