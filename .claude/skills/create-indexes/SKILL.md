---
name: create-indexes
description: >-
  Create Indexes — Use when working on database schema, migrations, or query performance.
x-generated-from: skills/database/create-indexes/SKILL.md
---

## Правило
- Кожна foreign key колонка — індексована
- Partial-індекси (`WHERE`-умова) для "гарячих" запитів, що завжди фільтрують за станом (наприклад, лише активні/незавершені записи)
- Складені індекси — порядок колонок відповідає порядку в `WHERE`/`ORDER BY` найчастіших запитів

## Приклад (адаптувати назви таблиць/колонок під власну схему)
```sql
CREATE INDEX idx_orders_tenant_status
  ON orders(tenant_id, status)
  WHERE status NOT IN ('completed', 'archived');
```
