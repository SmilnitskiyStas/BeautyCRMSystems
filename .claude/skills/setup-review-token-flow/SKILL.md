---
name: setup-review-token-flow
description: >-
  Setup Review Token Flow — Use when implementing a light backend colocated with the frontend.
x-generated-from: skills/fullstack/setup-review-token-flow/SKILL.md
---

# Setup Review Token Flow

Маршрут: `/review/[token]`.

## Генерація токена
- Криптографічно випадковий (`crypto.randomBytes(32).toString("base64url")` або еквівалент), ніколи sequential ID чи predictable pattern.
- `UNIQUE` constraint в `review_requests.token` — DB рівень, не тільки application-check.
- `expires_at` — обмежений термін дії (напр. 30 днів), перевіряється при кожному GET/POST на маршруті.
- Токен пов'язаний з конкретним `project_id`/клієнтом — форма перед-заповнена, не анонімна.

## Стани модерації
`draft → submitted → in_review → approved → published` (або `rejected`) — зберігається в `testimonials.moderation_status`, не виводиться напряму з `used_at`.

## Захист від повторного надсилання
- Перший валідний submit виставляє `review_requests.used_at`.
- Повторний GET/POST з тим самим токеном після `used_at` → показати "вже надіслано", не форму повторно.
- Дозволити зберегти як draft (вимога проєктної специфікації: "можливість зберегти відгук як draft") без виставлення `used_at` — тільки фінальний submit його закриває.

## Rules
- Публікація відгуку — тільки після ручної модерації (`moderation_status = approved`), ніколи auto-publish одразу з форми.
- `allow_name`/`allow_company`/`allow_photo`/`allow_publication` — окремі поля, кожне явно підтверджене клієнтом, не одне загальне "consent".
- Прострочений або вже використаний токен повертає зрозумілу помилку, не 500 і не порожню сторінку.
