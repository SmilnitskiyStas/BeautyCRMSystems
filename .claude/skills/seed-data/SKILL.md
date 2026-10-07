---
name: seed-data
description: >-
  Seed Data — Use when working on database schema, migrations, or query performance.
x-generated-from: skills/database/seed-data/SKILL.md
---

## Розташування
Директорія seed-скриптів вашого проєкту (`prisma/seed.ts`, `Infrastructure/Data/Seeders/` тощо).

## Що засівати в dev
- 1 тестовий tenant/акаунт (якщо проєкт multi-tenant)
- По одному користувачу на кожну роль з моделі доступу
- Реалістичний, але явно тестовий набір основних сутностей (10-20 записів), що відповідає домену саме вашого проєкту
- Записи в різних станах (активний/архівний/на модерації тощо — залежно від доменних статусів)

## Правило
Засівати лише в Development-середовищі. Ніколи не хардкодити production-дані в seed-скриптах.
