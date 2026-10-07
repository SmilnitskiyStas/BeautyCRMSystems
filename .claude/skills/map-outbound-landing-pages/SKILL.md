---
name: map-outbound-landing-pages
description: >-
  Map Outbound Landing Pages — Use when shaping conversion structure — hero/CTA, forms, analytics.
x-generated-from: skills/cro/map-outbound-landing-pages/SKILL.md
---

## Галузеві сторінки (розділ галузевих сторінок проєктного спеку)
`/industries/retail` · `/industries/restaurants` · `/industries/healthcare` · `/industries/beauty` · `/industries/construction` · `/industries/logistics` · `/industries/education` · `/industries/startups` · `/industries/local-business`

## UTM-паттерн для персонального outreach (розділ outreach-кампаній проєктного спеку)
```text
?utm_source=google_maps
&utm_medium=outreach
&utm_campaign=local_business
&utm_content={business_slug}
```

`utm_campaign` відповідає ніші (напр. `restaurants`, `beauty`), `utm_content` — унікальний slug конкретного бізнесу з дослідження (для 1:1 атрибуції відповіді).

## Mapping-принцип
- Кожна галузева сторінка — самостійна ціль для одного типу outreach-кампанії, не generic homepage з довільним UTM.
- Контакти — тільки публічні бізнес-контакти (Google Maps, офіційні сайти, LinkedIn, публічні каталоги); без scraping, без обходу CAPTCHA, без масової автоматичної розсилки (розділ outreach-кампаній проєктного спеку).
- Кожна опублікована галузева сторінка має унікальний контент і власні metadata (розділ галузевих сторінок проєктного спеку) — не шаблон з підміною назви ніші.

## Rules
- Нову галузеву сторінку в mapping додає `cro-specialist` разом з `seo-specialist` (metadata/keyword-кластер) — не тільки копірайтинг-рішення.
- `utm_content` завжди slug, не персональні дані контакту (ім'я власника, email).
- Якщо приватний mini-CRM (розділ outreach/mini-CRM проєктного спеку) вже є — кампанії зв'язуються через `selected_service`/attribution-поля ліда (див. `define-analytics-event-taxonomy.md`), не окремою паралельною системою.
