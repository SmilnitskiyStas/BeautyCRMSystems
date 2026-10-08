# Beauty CRM

B2B-платформа для б'юті-сфери: салони, барбершопи, косметологи й мережі закладів. Мультитенантна (PostgreSQL RLS), модульний моноліт.

## Що реалізовано
- **Записи й календар:** вільні слоти з урахуванням тривалості послуги, графіка по закладах і відсутностей; створення, перенесення, підтвердження, скасування. Скасовані записи зникають з календаря, але зберігаються з відміткою, хто, коли й чому скасував.
- **Онлайн-запис клієнта** (публічний API без входу, `/book`): заклад → майстер → послуга й час → оформлення; ідемпотентні запити, захист від зловживань.
- **Послуги, ціни, акції:** ціна мережі й override закладу, акції зі знижкою.
- **Працівники:** профіль, призначені послуги, графіки по закладах, відсутності (лікарняний, відпустка, вихідний) з приватними примітками, запити від працівників зі схваленням, деактивація з відкликанням доступу.
- **Заклади:** CRUD, часові зони.
- **Налаштовувана політика повернення коштів** (вікно, відсотки, комісія).
- **Автентифікація без самореєстрації:** бізнес створює оператор платформи, ролі owner / admin / specialist.
- **Канали:** Telegram і Instagram (адаптери з mock-режимом для розробки), outbox із повторами.
- **AI-асистент:** режими `suggest` / `confirm` (за замовчуванням) / `auto`, журнал дій, захист від prompt injection.
- **Worker:** нагадування, outbox, winback, запит відгуку; читає чергу з PostgreSQL.

Чого **немає** або реалізовано частково: див. «Відомі обмеження» у [runbook](.claude/docs/runbook.md) (розсилки поштою/SMS, кампанії по сегментах, платіжний провайдер, CAPTCHA, AI-чат в адмінці).

## Стек
| Шар | Технології |
|---|---|
| Frontend | Next.js 16 (App Router), React 19, TypeScript, Tailwind 4, React Query 5, Zustand 5, Zod 4 |
| Backend | .NET 8 (ASP.NET Core), EF Core 8, Npgsql |
| БД | PostgreSQL 16, RLS (FORCE), 8 міграцій |
| Worker | Node.js 22, TypeScript, `pg` (черга читається з БД, Redis не потрібен для локального запуску) |
| AI | Anthropic Claude (Messages API), ізольовано в `Infrastructure/AI/Beauty` |

## Структура
```
backend/    BeautyCrm.{Domain,Application,Infrastructure,Api,Tests}
frontend/   Next.js: адмінка (/beauty), вхід (/login), публічний запис (/book), e2e/
worker/     фонові задачі (нагадування, outbox, winback)
docs/beauty-crm/prototype/   вихідний UI-прототип
.claude/    агенти, skills, контракти й документація (.claude/docs), task logs
```

## Запуск
Покроковий, перевірений порядок (ролі БД, міграції, API, worker, frontend, тести, пастки Windows): [.claude/docs/runbook.md](.claude/docs/runbook.md). Швидкий перегляд інтерфейсу без backend: `NEXT_PUBLIC_USE_MOCK=1`, `npm run dev` у `frontend/`.

## Документація
- [api.md](.claude/docs/api.md), [database.md](.claude/docs/database.md), [domain-model.md](.claude/docs/domain-model.md), [runbook.md](.claude/docs/runbook.md)
- Контракти й рішення: [beauty-contracts.md](.claude/docs/beauty-contracts.md); ADR: [.claude/docs/adr/](.claude/docs/adr/) (001 стек, 002 RLS, 003 outbox і worker, 004 канали, 005 AI, 006 автентифікація, 007 політика повернення, 008 працівники, 009 скасування й заклади)
- [known-discrepancies.md](.claude/docs/known-discrepancies.md): розбіжності між документами й кодом
- Джерело вимог: [BEAUTY_CRM_PROMPT.md](BEAUTY_CRM_PROMPT.md)

## Принципи
Секрети лише в `.env` (у `.gitignore`), RLS на кожній таблиці з tenant-даними, API працює під роллю БД без BYPASSRLS (перевіряється при старті), приватні дані (примітки відсутностей) не потрапляють у логи й публічний API. Роботу над проєктом ведуть спеціалізовані агенти за [CLAUDE.md](CLAUDE.md).
