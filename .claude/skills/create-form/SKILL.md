---
name: create-form
description: >-
  Create Form — Use when building the user-facing web layer.
x-generated-from: skills/frontend/create-form/SKILL.md
---

## Libraries
react-hook-form + zod + @hookform/resolvers + shadcn/ui `Form`/`FormField`/`FormControl`/`FormMessage`.

## Forms in Scope (форми, визначені в проєктній специфікації)
- **Коротка форма** (hero/CTA): ім'я, email або Telegram, короткий опис задачі, згода з політикою конфіденційності.
- **Розширена форма** (`/start-project`): ім'я, компанія, email, телефон/месенджер, країна, тип проєкту, опис бізнесу, проблема/задача, потрібні послуги, наявність дизайну/ТЗ, бажаний строк, бюджет, посилання на поточний сайт, файли, спосіб зв'язку, згода на обробку даних.

## Pattern
1. Одна zod-схема на форму — спільна для client resolver і server-side валідації (не дублювати визначення).
2. `useForm` з `zodResolver`; поля через `FormField` + `FormControl` + `FormMessage` (inline помилки).
3. `onSubmit` викликає Server Action, що: валідує ще раз на сервері (та сама схема) → перевіряє rate limit → зберігає lead у БД → записує UTM/referrer/landing-page (згідно з правилами аналітики проєкту) → надсилає email-сповіщення → повертає редирект на `/thank-you`.
4. `isPending`/`isSubmitting` блокує повторний сабміт і показує стан завантаження на кнопці.
5. Honeypot-поле (приховане, не для людини) як перший рівень anti-spam.

## Rules
- Client-side і server-side валідація обидві обов'язкові — client для UX, server бо client можна обійти.
- Не розкривати секретні ключі чи внутрішні деталі помилки в client-facing повідомленні.
- Форма деградує коректно без JS там, де це практично можливо (progressive enhancement через Server Action).
- Прикріплені файли йдуть через file-storage abstraction (`.claude/skills/nextjs-marketing-architect/SKILL.md`) — перевірка типу/розміру перед збереженням.
- Успішний сабміт логує аналітичну подію (`contact_form_submit` / `review_form_submit`, згідно з таксономією аналітичних подій проєкту); помилка валідації — `contact_form_error`.

## Anti-patterns
- Дві окремі zod-схеми (client і server), які можуть розійтися.
- Показ сирої серверної помилки/stack trace користувачу.
