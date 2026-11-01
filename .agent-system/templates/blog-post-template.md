# Blog Post Template

**Джерело:** структура блог-статті, адаптована з практики SEO-контент-маркетингу
**Використання:** один MDX-файл (або еквівалент — залежно від обраного рішення для контент-шару, див. відповідний ADR проєкту) на статтю, `content/blog/{slug}.md`. Frontmatter заповнюється окремо для `uk`/`en` (два файли або двомовні поля, залежно від того ж рішення).

---

## Frontmatter

```yaml
---
title: ""                # заголовок статті (основа H1 + <title>)
slug: ""                  # /blog/{slug}
category: ""              # приклад категорій — адаптувати під власний блог: Web Development | Business Automation | AI for Business | Mobile Development | Product Design | Case Studies | Guides for Business Owners
tags: []                  # blog_tags
author: ""                # автор статті (обовʼязково)
publishedAt: ""           # YYYY-MM-DD — дата створення (обовʼязково)
updatedAt: ""             # YYYY-MM-DD — дата оновлення (обовʼязково; = publishedAt, якщо ще не оновлювалась)
description: ""           # meta description — також основа Open Graph description
ogImage: ""               # шлях до соціального preview-зображення
relatedSlugs: []          # 2-3 related articles — лише існуючі slug'и, не вигадані
---
```

## Body Structure

### Заголовки / зміст (Table of Contents)
_Логічна ієрархія заголовків: один H1 = title, далі H2/H3. TOC генерується з цих заголовків, не хардкодиться окремо._

### Вступ
_Про що стаття і чому це важливо для власника бізнесу — без зайвого жаргону._

### Основний контент
_Розбити на логічні H2/H3-секції. Включити внутрішні посилання — на релевантні `/services/*`, `/projects/*`, інші статті блогу._

### CTA
_Заклик до дії в кінці статті — напр. посилання на `/start-project` або `/contact`, релевантне темі статті._

### Related articles
_2-3 картки, побудовані з `relatedSlugs` вище._

## Metadata / SEO checklist
- [ ] Унікальні `title` / `description` (uk + en)
- [ ] Open Graph: title, description, image, locale
- [ ] Canonical URL + hreflang (uk/en)
- [ ] Structured data: `BlogPosting` (author, datePublished, dateModified, headline, image) — лише якщо відповідає видимому контенту
- [ ] Внутрішні посилання перевірені (не broken)
