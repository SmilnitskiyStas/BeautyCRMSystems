---
name: secrets-and-headers-review
description: >-
  Secrets And Headers Review — Use during a security review.
x-generated-from: skills/security/secrets-and-headers-review/SKILL.md
---

## Секрети
- Правило Next.js: будь-яка змінна з префіксом `NEXT_PUBLIC_*` потрапляє в client bundle і вважається публічною — ніколи не класти туди API-ключі, тільки dev-safe значення (analytics ID, публічний base URL)
- Email/storage/analytics server-side ключі — тільки без `NEXT_PUBLIC_` префіксу, читаються лише в Server Actions/route handlers
- `.env.example` містить назви змінних без реальних значень; реальні `.env*` файли в `.gitignore`
- Перевірити зібраний client bundle (build output), а не тільки вихідний код — helper/re-export може випадково протягнути секрет у клієнтський граф

## Headers / CSP
- Content-Security-Policy налаштований (мінімум `default-src`, `script-src`, `frame-ancestors`) — блокує довільний inline-скрипт третіх сторін
- Secure headers: `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy` за потреби
- Cookies (сесія admin, theme/locale-вибір) — `Secure`, `SameSite`, `HttpOnly` там, де кука не повинна читатись з JS

## Redirects
- Будь-який redirect на основі user input (напр. `?next=`) звіряється з allowlist внутрішніх шляхів — ніколи не редірект на довільний зовнішній URL з query-параметра (open redirect)

## Rules
- Аудит залежностей (розділ безпеки проєктного спеку) — регулярна перевірка на відомі вразливості, не одноразово на старті проєкту.
- Будь-яка third-party script (analytics, chat) підключається через той самий CSP-review, не окремим винятком "бо це тимчасово".
