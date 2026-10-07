---
name: seo-testing
description: >-
  SEO Testing — Use when writing or running tests.
x-generated-from: skills/qa/seo-testing/SKILL.md
---

## Категорія тестів (розділ тестування проєктного спеку)
- Metadata: `title`/`description` присутні й унікальні на кожен маршрут
- Canonical: коректний абсолютний URL, без query-параметрів, вказує на себе (не на чужу сторінку)
- Hreflang: пара `/uk` ↔ `/en` присутня в обидва боки для кожної локалізованої сторінки
- Sitemap: `sitemap.xml` валідний, містить усі публічні маршрути обома локалями
- Robots: `robots.txt` доступний, приватні/технічні шляхи в `disallow`
- Structured data: JSON-LD валідний (Google Rich Results Test), тип відповідає видимому контенту сторінки
- Noindex: приватні сторінки (`/thank-you`, admin/mini-CRM, `/500`) мають `noindex`

## Rules
- SEO-тести гонити після будь-якої зміни в `app/[locale]/**` чи `lib/seo/**`, не тільки коли задача явно "SEO".
- Розбіжність між metadata і фактичним видимим контентом сторінки — блокер, не "дрібниця" (розділ structured data проєктного спеку: markup має відповідати видимому змісту).
- Перевіряти обидві локалі окремо — canonical/hreflang помилка часто асиметрична (правильно в один бік, неправильно у зворотний).
