---
name: admin-auth-review
description: >-
  Admin Auth Review — Use during a security review.
x-generated-from: skills/security/admin-auth-review/SKILL.md
---

# Admin Auth Review

Стосується приватного mini-CRM (розділ outreach/mini-CRM проєктного спеку) — outbound lead tracking, не публічної частини сайту.

## Checklist
- Кожен admin/mini-CRM маршрут вимагає автентифікації — перевірка на рівні middleware/layout, не тільки в окремому компоненті (щоб новий маршрут не "забув" додати перевірку)
- Жодне публічне посилання (nav, sitemap, footer) не веде на admin-маршрут; маршрут не індексується (див. `configure-sitemap-and-robots.md`, noindex)
- Пряме звернення на admin-URL без сесії → редірект на login, не 200 з порожніми даними і не 500
- Якщо кілька admin-користувачів — рольова модель explicit (розділ безпеки проєктного спеку: «рольова модель для admin-функцій»), не один спільний акаунт на всю команду
- Сесія/токен адміна — HttpOnly cookie, не localStorage

## Rules
- Mini-CRM не є частиною публічного сайту (розділ outreach/mini-CRM проєктного спеку) — окремий сегмент routing, ізольований від маркетингових сторінок.
- Failed login — generic-повідомлення (не розкриває, чи існує такий логін).
- Будь-яка зміна статусу ліда в mini-CRM (розділ outreach/mini-CRM проєктного спеку — статуси: New → ... → Won/Lost) пише в `audit_logs`.
