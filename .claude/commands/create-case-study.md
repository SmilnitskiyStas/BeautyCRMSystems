---
description: Create a new project case study following the 23-point case-study structure
argument-hint: <project name or slug, e.g. "customer-portal-redesign">
---

# create-case-study.md

Створити case study, вказаний тут: $ARGUMENTS

Playbook для планування й диспетчеризації — головна сесія не пише наратив чи код напряму (`CLAUDE.md`).

## Context to load before starting
1. Прочитати `CLAUDE.md`
2. Прочитати `{PROJECT_SPEC}` (напр. `PROJECT_PROMPT.md`, `v1-spec.md` тощо) — розділ вибраних проєктів і чесності статусу, розділ картки в списку проєктів, розділ 23-пунктної структури кейсу
3. Прочитати `.claude/docs/content-model.md` і `.claude/docs/domain-model.md` (сутності `projects`/`project_images`/`project_technologies`, відповідна секція `{PROJECT_SPEC}`)
4. Прочитати поточну задачу в `.claude/tasks/current.md`, якщо вона вже існує

## Workflow
1. Визначити чесний статус проєкту: `Concept` / `MVP` / `In Development` / `Internal Product` / `Prototype` — ніколи не видавати концепт за запущений клієнтський продукт (відповідна секція `{PROJECT_SPEC}`)
2. Заповнити `templates/case-study-template.md` (23 пункти, відповідна секція `{PROJECT_SPEC}`) для проєкту — кожне текстове поле окремо для uk/en, без вигаданих бізнес-результатів (якщо підтверджених цифр немає — описові результати: скорочення кількості ручних операцій, централізація даних, автоматизація сповіщень тощо)
3. Запропонувати розташування файлу під `content/projects/{slug}` (або еквівалент за ADR контент-шару від `project-architect`) і категорію фільтра списку проєктів (відповідна секція `{PROJECT_SPEC}`: Websites / Web Applications / SaaS / CRM/ERP / Mobile / AI / Automation / Internal Products / Concepts)
4. Заспавнити агентів у порядку: `product-designer` (якщо категорія/індустрія ще не визначені в контент-моделі) → `copywriter` (наратив 23 пунктів, uk/en) → `frontend-developer` (маршрут `/projects/[slug]`, картка в списку) → `seo-specialist` (JSON-LD `CreativeWork`/`SoftwareApplication` — лише якщо відповідає реальному продукту, відповідна секція `{PROJECT_SPEC}`)
5. Перед переведенням статусу в `done` — `qa-engineer`/`accessibility-specialist` перевіряють галерею (alt-тексти обома мовами, пункт 23 шаблону) і responsive-поведінку

## Notes
- Не вигадувати клієнтів, цифри чи результати, яких не було (відповідні секції `{PROJECT_SPEC}`)
- Кожне зображення в галереї потребує alt-тексту uk/en
- CTA в кінці кейсу (пункт 24 шаблону) веде на `/start-project` або `/contact`
