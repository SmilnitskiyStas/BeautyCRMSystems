---
description: Create a new blog article
argument-hint: <topic, e.g. "Скільки коштує розробка сайту для бізнесу">
---

# create-blog-post.md

Створити статтю блогу, вказану тут: $ARGUMENTS

Playbook для планування й диспетчеризації — головна сесія не пише статтю чи код напряму (`CLAUDE.md`).

## Context to load before starting
1. Прочитати `CLAUDE.md`
2. Прочитати `{PROJECT_SPEC}` (напр. `PROJECT_PROMPT.md`, `v1-spec.md` тощо) — розділ таксономії блогу (категорії, обов'язкові елементи статті) і розділ тону
3. Прочитати `.claude/docs/seo-map.md` і `.claude/docs/content-model.md`
4. Прочитати поточну задачу в `.claude/tasks/current.md`, якщо вона вже існує

## Workflow
1. Підтвердити категорію: Web Development / Business Automation / AI for Business / Mobile Development / Product Design / Case Studies / Guides for Business Owners (відповідна секція `{PROJECT_SPEC}`)
2. Заповнити `templates/blog-post-template.md` — frontmatter (title, slug, category, tags, author, publishedAt, updatedAt, description, ogImage, relatedSlugs) і body (TOC, вступ, основний контент із внутрішніми посиланнями, CTA, related articles)
3. Перевірити, що `relatedSlugs` посилаються лише на реально існуючі статті — не вигадувати slug'и, яких ще немає
4. Заспавнити агентів у порядку: `seo-specialist` (відповідність keyword-кластеру й таксономії — відповідна секція `{PROJECT_SPEC}`) → `copywriter` (драфт uk/en) → `frontend-developer` (якщо маршрут `/blog/[slug]`/MDX-рендеринг ще не готовий) → `qa-engineer` (SEO-тест: metadata, structured data)
5. Перед переведенням статусу в `done` перевірити, що автор, дата створення й дата оновлення заповнені (відповідна секція `{PROJECT_SPEC}` — обов'язкові поля)

## Notes
- `BlogPosting` JSON-LD додається лише якщо відповідає видимому контенту (відповідна секція `{PROJECT_SPEC}`)
- Без реальних підтверджених фактів — не вигадувати статистику чи цитати
