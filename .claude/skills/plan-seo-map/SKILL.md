---
name: plan-seo-map
description: >-
  Plan SEO Map — Use during the discovery phase — positioning, IA, content model.
x-generated-from: skills/product-design/plan-seo-map/SKILL.md
---

## Purpose
Формує SEO-мапу контенту (сторінка → пошуковий намір → keyword-кластер) спільно з `seo-specialist`, до старту контентно-SEO фази.

## Inputs
- `{PROJECT_SPEC}` (напр. PROJECT_PROMPT.md) — розділ content SEO / keyword-кластерів
- Затверджений sitemap (`map-sitemap-and-user-flows.md`)
- Content model (`create-content-model.md`)

## Keyword Clusters (вхідні напрямки)
EN: web development, website development for business, corporate website development, landing page development, CRM development, ERP development, SaaS development, mobile app development, AI integration, business automation, Next.js development, React development.

UK (природні запити): розробка сайтів, створення сайту для бізнесу, замовити сайт, розробка корпоративного сайту, створення інтернет-магазину, розробка CRM, автоматизація бізнесу, розробка мобільних застосунків, інтеграція штучного інтелекту, створення SaaS-платформи.

## Process
1. Для кожної сторінки з sitemap визначити основний keyword-кластер і пошуковий намір (informational / commercial / navigational).
2. Розподілити кластери 1:1 на сторінки — уникати двох сторінок, що конкурують за той самий запит у тій самій мові (canonical/hreflang-конфлікт).
3. Для `/services/*` — прив'язати до конкретного кластеру з переліку вище, не переписувати головну під ті самі ключові слова.
4. Для `/industries/*` — прив'язати до нішевих запитів (напр. "сайт для стоматології"), тільки якщо сторінка матиме реальний унікальний контент.
5. Для `/blog/*` — категорії (Web Development, Business Automation, AI for Business, Mobile Development, Product Design, Case Studies, Guides for Business Owners), кожна стаття → один основний намір.
6. Передати мапу `seo-specialist` для технічної реалізації (metadata, hreflang, JSON-LD, sitemap.xml) і `copywriter` для наративу під намір.

## Guardrails
- Не створювати SEO-сторінки без реальної користі для відвідувача.
- Не переоптимізувати під ключові слова на шкоду читабельності.

## Output
Таблиця: сторінка | мова | keyword-кластер | пошуковий намір | статус контенту → `.claude/docs/seo-map.md`.
