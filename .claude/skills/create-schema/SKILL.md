---
name: create-schema
description: >-
  Create Schema — Use when working on database schema, migrations, or query performance.
x-generated-from: skills/database/create-schema/SKILL.md
---

# Create Schema

Джерело істини: розділ моделі даних у `{PROJECT_SPEC}`.

## Патерн (PostgreSQL — типовий вибір для цієї бібліотеки; адаптувати під свою СУБД)
- UUID PK: `gen_random_uuid()` (або аналог вашої СУБД)
- `created_at`/`updated_at TIMESTAMPTZ DEFAULT NOW()`
- Soft delete через `is_active`/`deleted_at`, а не hard `DELETE`, де це виправдано доменом
- Явні foreign keys з продуманою `ON DELETE`-поведінкою для кожного зв'язку (не завжди `CASCADE` за замовчуванням)

## Multi-tenant isolation
Застосовувати лише якщо проєкт справді multi-tenant — не типовий кейс за замовчуванням:
```sql
ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON {table}
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```
