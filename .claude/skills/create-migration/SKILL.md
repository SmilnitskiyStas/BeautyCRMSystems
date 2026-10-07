---
name: create-migration
description: >-
  Create Migration — Use when working on database schema, migrations, or query performance.
x-generated-from: skills/database/create-migration/SKILL.md
---

# Create Migration

Команда залежить від обраного інструменту міграцій (Prisma: `npx prisma migrate dev --name {name}`; Drizzle: `npx drizzle-kit generate`; TypeORM: `typeorm migration:generate`; EF Core: `dotnet ef migrations add {Name}`) — зафіксувати конкретний інструмент у `.claude/docs/decisions.md` (ADR) і використовувати його послідовно.

## Конвенція іменування
- `add_products_table`
- `add_stock_indexes`
- `add_tenant_isolation_policies`

## Після створення міграції
1. Переглянути згенерований SQL/код міграції вручну — не довіряти генератору наосліп
2. Перевірити, що up/down (або еквівалент відкату) коректні
3. Додати вручну те, що інструмент міграцій не генерує автоматично (RLS-політики тощо)
4. Застосувати міграцію на dev-БД і перевірити, що застосунок стартує без помилок
