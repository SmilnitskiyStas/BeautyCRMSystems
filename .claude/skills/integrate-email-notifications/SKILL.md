---
name: integrate-email-notifications
description: >-
  Integrate Email Notifications — Use when implementing a light backend colocated with the frontend.
x-generated-from: skills/fullstack/integrate-email-notifications/SKILL.md
---

## Провайдер-абстракція
Ніколи не викликати SDK конкретного провайдера (Resend/Postmark/SES) напряму з feature-коду. Уся бізнес-логіка звертається до `lib/email/sender.ts` (інтерфейс), сам провайдер підключається один раз під капотом.

```ts
interface EmailSender {
  send(input: { to: string; template: EmailTemplate; data: Record<string, unknown> }): Promise<{ success: boolean; error?: string }>;
}
```

## Патерн сповіщення про нову заявку
1. Lead записаний в DB (успішна транзакція) → тригер email до команди.
2. Шаблон — окремий файл/компонент (`lib/email/templates/lead-notification.tsx`), не рядковий literal у Server Action.
3. Помилка відправки email **не** відкочує вже збережений lead і не блокує thank-you сторінку — це side-effect, а не частина transaction.
4. Статус відправки логується (`sent` / `failed` / `retrying`) окремо від бізнес-даних ліда.

## Retry
- Retry на transient-помилках (мережа, 5xx провайдера) з backoff, обмежена кількість спроб.
- Не retry на permanent-помилках (invalid recipient, 4xx) — одразу лог `failed`.

## Rules
- Конфігурація (API key, from-адреса, домен) — тільки через env vars, ніколи хардкод у коді.
- Жодних секретів провайдера в client bundle — email завжди відправляється server-side.
- Шаблон містить мінімум PII, потрібний для дії (ім'я, project_description) — не весь запис `leads` цілком.
