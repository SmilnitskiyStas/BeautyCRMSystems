---
name: add-validation
description: >-
  Validate at the request boundary. Use when adding input validation to an API endpoint, server action, or form — schema at the edge, business rules in the service, one shared schema, consent booleans never pre-checked.
x-generated-from: skills/shared/add-validation/SKILL.md
---

# Add Validation

Validate at the boundary of a request, not deeper. One schema per form/entity; the same schema object on both client and server where the stack allows it. A schema error is a 400; a business-rule conflict is a 409. Consent / GDPR-style booleans are required and never pre-checked.

## Stack-agnostic (layered service)

## Валідація на межі (request DTO)
Схема (Zod / class-validator / FluentValidation / еквівалент вашого стеку) на вхідні дані запиту. Помилка схеми → HTTP 400.

## Бізнес-валідація (service-шар)
Перевірка бізнес-правил у сервісі (унікальність, стан сутності, права доступу до конкретного ресурсу). Конфлікт бізнес-правила → HTTP 409.

## Правило
Валідувати лише на межі (вхід запиту). Не повторювати валідацію між внутрішніми шарами — довіряти власним типам усередині системи.

## Colocated backend (server action / route handler)

## Pattern
Zod-схема на межі (Server Action / route handler), одна схема на форму/entity, розташування `lib/validation/{domain}.schema.ts`.

```ts
export const leadSchema = z.object({
  name: z.string().trim().min(1).max(120),
  email: z.string().email().optional(),
  messenger: z.string().max(120).optional(),
  projectDescription: z.string().trim().min(10).max(2000),
  consent: z.literal(true, { errorMap: () => ({ message: "consent_required" }) }),
}).refine((d) => d.email || d.messenger, {
  message: "email_or_messenger_required",
  path: ["email"],
});
```

## Форми і межі валідації
- Коротка форма (hero/CTA): ім'я, email або Telegram, короткий опис, consent.
- Розширена форма (`/start-project`): повний ~16-полів список — своя, ширша схема, не розширення короткої через `.extend()` без перегляду required-полів.
- `testimonials`/review-форма: рейтинг, текст відгуку, `allow_name`/`allow_company`/`allow_photo`/`allow_publication` — boolean-поля з explicit default `false`, ніколи `true` без дії користувача.

## Rules
- Валідація тільки на межі виклику (Server Action input) — не дублювати ту саму перевірку глибше в service/db-шарі.
- Помилка завжди одного шейпу: `{ success: false, error: string, fieldErrors?: Record<string, string[]> }` — компонент форми не парсить довільні error-формати.
- Client-side (React Hook Form + zodResolver) використовує **той самий** Zod-schema-об'єкт, що й Server Action — не дублювати правила вручну у двох місцях.
- Consent/GDPR-подібні поля — завжди required boolean, ніколи pre-checked.
