---
name: lead-form-security-review
description: >-
  Lead Form Security Review — Use during a security review.
x-generated-from: skills/security/lead-form-security-review/SKILL.md
---

# Lead Form Security Review

Стосується короткої форми, розширеної форми (`/start-project`) і review-форми.

## Checklist
- Server-side Zod-валідація присутня і незалежна від client-side (перевірити прямим запитом в обхід UI — client-side валідація нічого не гарантує)
- Rate limiting на кожному form-endpoint (per IP і/або per session), не тільки на "головному" контактному
- Spam-захист: honeypot-поле і/або CAPTCHA-альтернатива (без порушення принципів розділу безпеки проєктного спеку — не блокувати легітимних користувачів заради боротьби зі спамом)
- File upload (`lead_files`): перевірка MIME-типу **за вмістом**, не лише за розширенням; ліміт розміру; safe filename (генерований, не оригінальне ім'я файлу користувача напряму в шлях)
- Жодного секретного ключа (email provider, storage) не потрапляє в client bundle — форма звертається до Server Action/route handler, не напряму до провайдера

## Rules
- Перевірка типу/розміру файлу — до запису у storage, не після.
- Rate limit і spam-check спрацьовують **до** запису в DB (див. `create-server-action.md`) — заблокований запит не створює lead-запис "про всяк випадок".
- Повідомлення про помилку валідації — зрозуміле користувачу, без розкриття внутрішньої деталі (стек, назва таблиці, тип rate-limiter).
