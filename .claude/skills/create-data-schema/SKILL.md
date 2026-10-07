---
name: create-data-schema
description: >-
  Create Data Schema — Use when implementing a light backend colocated with the frontend.
x-generated-from: skills/fullstack/create-data-schema/SKILL.md
---

# Create Data Schema

Джерело істини для сутностей: `{PROJECT_SPEC}` (напр. PROJECT_PROMPT.md), розділ моделі даних (приклад типового набору для marketing-сайту з лід-генерацією, ~14 сутностей: `leads`, `lead_files`, `testimonials`, `review_requests`, `projects`, `project_images`, `project_technologies`, `blog_posts`, `blog_categories`, `blog_tags`, `contact_events`, `newsletter_subscribers`, `seo_redirects`, `audit_logs`).

## Конвенції
- PK: `id UUID DEFAULT gen_random_uuid()`.
- Кожна таблиця: `created_at TIMESTAMPTZ DEFAULT NOW()`, `updated_at TIMESTAMPTZ DEFAULT NOW()` (тригер або ORM hook на update).
- FK у своєму типі (`project_id UUID REFERENCES projects(id)`), не вільний текст.
- Enum-подібні поля (`status`, `moderation_status`, `project_type`) — Postgres `ENUM` або `CHECK` constraint, не вільний string.
- М'яке видалення там, де потрібна історія (`projects`, `testimonials`) — `deleted_at TIMESTAMPTZ NULL`, не hard delete.

## Review-token поля (`review_requests`)
- `token` — унікальний, `UNIQUE` constraint, генерується криптографічно (див. `setup-review-token-flow.md`), не sequential ID.
- `expires_at TIMESTAMPTZ NOT NULL`.
- `used_at TIMESTAMPTZ NULL` — заповнюється при першому валідному submit, блокує повторне використання.
- Індекс на `token` (unique lookup при кожному відкритті `/review/[token]`).

## Міграції
- Розташування: `lib/db/migrations/`.
- Назва: `{timestamp}_{дія}_{сутність}.sql`, напр. `20260803120000_create_leads_table.sql`.
- Одна логічна зміна на міграцію (одна таблиця або один пов'язаний набір індексів), не одна велика міграція на всю модель даних.
- Кожна міграція — окремий, оборотний крок (down/rollback, якщо tooling підтримує).

## Rules
- Не додавати колонки "про запас" без сутності в моделі даних проєкту — розширювати тільки коли з'являється реальна вимога.
- `audit_logs` пише кожна зміна статусу ліда чи модерації відгуку (вимога проєктної специфікації: "усі зміни важливих даних мають бути придатні для аудиту").
