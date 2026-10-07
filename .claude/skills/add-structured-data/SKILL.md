---
name: add-structured-data
description: >-
  Add Structured Data — Use when implementing technical SEO or planning content strategy.
x-generated-from: skills/seo/add-structured-data/SKILL.md
---

## Типи JSON-LD за контентом

| Тип сторінки | Schema.org type |
|---|---|
| Головна | `Organization` + `WebSite` |
| Сторінка компанії/послуг загалом | `ProfessionalService` |
| Окрема послуга (`/services/[slug]`) | `Service` |
| Будь-яка глибша сторінка | `BreadcrumbList` |
| Блог-стаття | `Article` або `BlogPosting` |
| Сторінка з FAQ-блоком | `FAQPage` |
| Кейс/проєкт | `CreativeWork` |
| Реально запущений продукт | `SoftwareApplication` |

## Pattern
Рендерити через `<script type="application/ld+json">` в самому route (server-rendered), дані з того ж content-об'єкта, що й видимий текст сторінки — не окремий "SEO-only" датасет.

```ts
export function ServiceJsonLd({ service }: { service: ServiceContent }) {
  return (
    <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify({
      "@context": "https://schema.org", "@type": "Service",
      name: service.title, description: service.summary, provider: { "@type": "Organization", name: BRAND_NAME },
    }) }} />
  );
}
```

## Rules
- Markup лише те, що реально видно користувачу на сторінці — не додавати FAQPage без видимого FAQ-блоку, не Article-дату, якої немає в UI.
- `SoftwareApplication` тільки для реально відвантажених продуктів, не для Concept/MVP-кейсів (статуси проєктів, визначені в контент-моделі).
- Перевіряти валідність через Google Rich Results Test перед merge.
- Не дублювати один тип schema кілька разів на одній сторінці без потреби.
