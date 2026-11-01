---
name: create-indexes
description: >-
  Add database indexes only for measured or clearly critical queries, and verify with the query plan. Use when a query is slow or a new hot path appears.
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
