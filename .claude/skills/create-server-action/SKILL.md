---
name: create-server-action
description: >-
  Create Server Action — Use when implementing a light backend colocated with the frontend.
x-generated-from: skills/fullstack/create-server-action/SKILL.md
---

## Pattern
`"use server"` на початку файлу (colocated у `features/{domain}/actions.ts`, не в `components/`).

1. Парсити й валідувати вхід через Zod **першим кроком** — до будь-якого звернення до DB, email чи storage.
2. Ніколи не довіряти лише клієнтській валідації (React Hook Form) — Server Action це реальна межа довіри (trust boundary).
3. Повертати типізований результат, не кидати exception для очікуваних відмов:

```ts
type ActionResult<T> =
  | { success: true; data: T }
  | { success: false; error: string; fieldErrors?: Record<string, string[]> };

export async function submitLead(input: unknown): Promise<ActionResult<{ id: string }>> {
  const parsed = leadSchema.safeParse(input);
  if (!parsed.success) {
    return { success: false, error: "validation_failed", fieldErrors: parsed.error.flatten().fieldErrors };
  }
  // rate limit + spam check тут, до запису в DB
  const lead = await createLead(parsed.data);
  return { success: true, data: { id: lead.id } };
}
```

## Rules
- `throw` лише для непередбачених/infra помилок (DB недоступна) — хай їх ловить error boundary.
- Очікувані відмови (validation, rate limit, spam-check) завжди `{ success: false }`, ніколи exception.
- Rate limiting і spam-перевірка виконуються до запису в DB, не після (вимога форм та anti-spam політики проєкту).
- Будь-який запис, що змінює `leads`/`testimonials`/`review_requests`, додає запис в `audit_logs` (вимога аудиту з моделі даних проєкту).
- `redirect()`/`revalidatePath()` викликати тільки у success-гілці, після підтвердження запису.
- Не змішувати кілька непов'язаних дій в одному Server Action-файлі — один domain = один `actions.ts`.
