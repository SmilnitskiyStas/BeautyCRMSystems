---
name: sensitive-data-review
description: >-
  Sensitive Data Review — Use during a security review.
x-generated-from: skills/security/sensitive-data-review/SKILL.md
---

## Заборонено логувати (розділ безпеки проєктного спеку)
паролі · секретні ключі · повний вміст приватних повідомлень (напр. `project_description` ліда цілком у логах) · конфіденційні персональні дані

Логи фіксують дію і ідентифікатор (напр. "lead created, id=uuid"), не тіло запиту цілком.

## Мінімальний збір персональних даних
- Форми збирають лише задокументовані поля короткої/розширеної лід-форми (розділ лід-форм проєктного спеку) — не додавати "про запас" без потреби
- Analytics (розділ аналітики проєктного спеку) не отримує PII — ім'я, email, текст заявки ніколи не йдуть у track-payload (див. `define-analytics-event-taxonomy.md`)
- `testimonials`: показ імені/компанії/фото — тільки за explicit `allow_name`/`allow_company`/`allow_photo`, ніколи за замовчуванням `true`

## Rules
- `audit_logs`, `contact_events` — призначені для аудиту дій, не для зберігання довільного "про всяк випадок" знімку персональних даних.
- Будь-яке нове поле, що потрапляє в лог чи аналітику, проходить цей чеклист до merge — не постфактум security review.
- Файли (`lead_files`) видаляються/анонімізуються за retention-політикою, якщо вона визначена (`project-architect`/ADR); до визначення — мінімум не публічно доступні без signed URL.
