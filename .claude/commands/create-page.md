---
description: Create a new static/marketing page (service page, industry page, or core page like /about or /process)
argument-hint: <page, e.g. "/services/ai-integration" or "/about">
---

# create-page.md

Створити сторінку, вказану тут: $ARGUMENTS

Це playbook для планування й диспетчеризації роботи між агентами, не персона-команда — головна сесія тут не пише код напряму (`CLAUDE.md`: "ніколи не імплементувати код напряму в головній сесії. Головна сесія оркеструє задачі — спеціалізовані агенти виконують їх").

## Context to load before starting
1. Прочитати `CLAUDE.md`
2. Прочитати `{PROJECT_SPEC}` (напр. `PROJECT_PROMPT.md`, `v1-spec.md` тощо) — розділ структури сайту + розділ сторінки послуг (11-пунктна структура) або розділ галузевих сторінок, залежно від типу сторінки; для головної/про команду/процесу — відповідні розділи цих сторінок
3. Прочитати `.claude/docs/frontend-structure.md`, `.claude/docs/content-model.md`, `.claude/docs/seo-map.md`
4. Прочитати поточну задачу в `.claude/tasks/current.md`, якщо вона вже існує

## Workflow
1. Класифікувати сторінку: сервісна (`/services/{slug}`) / галузева (`/industries/{slug}`) / основна маркетингова (`/`, `/about`, `/process`, `/reviews`, `/contact`, `/start-project` — без фіксованого 11-пунктного шаблону)
2. Для сервісної сторінки використати `templates/service-page-template.md` (11 пунктів, відповідна секція `{PROJECT_SPEC}`). Для галузевої — `templates/industry-page-template.md` (відповідна секція `{PROJECT_SPEC}`, вимоги проти doorway pages). Якщо відповідний файл шаблону відсутній на диску — прочитати відповідну секцію `{PROJECT_SPEC}` напряму і повідомити про відсутній шаблон `project-architect`/`documentation-writer`, а не вигадувати структуру самостійно
3. Запропонувати розташування файлу під `src/app/[locale]/...` (відповідна секція `{PROJECT_SPEC}`) і відповідний запис контент-шару (`content/services/` / `content/industries/`)
4. Перевірити, що сторінка не дублює наявну лише заміною кількох ключових слів (відповідні секції `{PROJECT_SPEC}` — обов'язкова унікальність контенту й пошукового наміру)
5. Визначити потрібних агентів і заспавнити в порядку залежностей: `product-designer` (якщо контент-модель для цього типу сторінки ще не існує) → `copywriter` (текст uk/en за шаблоном) → `frontend-developer` (маршрут, layout, компонування секцій) → `seo-specialist` (metadata, hreflang, structured data) → за потреби `cro-specialist`, якщо сторінка вводить нову CTA/lead-form структуру, що виходить за межі вже затвердженої
6. Кожен заспавнений агент іде за власним 7-кроковим workflow з `CLAUDE.md`: контекст → перевірка залежностей → реалізація → task log → handoff → статус → документація
7. Перед переведенням статусу в `done` — `qa-engineer` перевіряє сторінку (responsive, SEO, a11y spot-check)

## Notes
- Галузеві сторінки (відповідна секція `{PROJECT_SPEC}`): без doorway pages — кожна повинна мати реальний унікальний контент, конкретний приклад проблеми й конкретне рішення
- Сторінки без фіксованого шаблону (`/about`, `/process`, `/reviews`) усе одно потребують metadata, structured data де застосовно, і текст виключно з типізованого контенту (відповідна секція `{PROJECT_SPEC}`) — жодного хардкодженого тексту в компонентах
