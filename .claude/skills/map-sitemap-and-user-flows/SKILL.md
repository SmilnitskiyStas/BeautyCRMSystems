---
name: map-sitemap-and-user-flows
description: >-
  Map Sitemap And User Flows — Use during the discovery phase — positioning, IA, content model.
x-generated-from: skills/product-design/map-sitemap-and-user-flows/SKILL.md
---

## Purpose
Валідує та деталізує інформаційну архітектуру сайту і критичні user flows перед стартом дизайну чи розробки.

## Baseline Sitemap (checklist)
Основні сторінки:
- [ ] `/`
- [ ] `/services`
- [ ] `/services/web-development`
- [ ] `/services/mobile-development`
- [ ] `/services/business-automation`
- [ ] `/services/ai-integration`
- [ ] `/services/crm-erp-development`
- [ ] `/projects`
- [ ] `/projects/[slug]`
- [ ] `/industries`
- [ ] `/industries/[slug]`
- [ ] `/about`
- [ ] `/process`
- [ ] `/reviews`
- [ ] `/blog`
- [ ] `/blog/[slug]`
- [ ] `/contact`
- [ ] `/start-project`
- [ ] `/privacy-policy`
- [ ] `/cookie-policy`
- [ ] `/terms`

Додатково:
- [ ] `/thank-you`
- [ ] `/404`
- [ ] `/500`

За потреби:
- [ ] `/estimate`
- [ ] `/free-audit`

Кожен маршрут існує в обох локалях (`/uk/...`, `/en/...`).

## Process
1. Підтвердити, що кожен пункт вище має чітке призначення й унікальний пошуковий намір (особливо `/services/*` і `/industries/*` — не дублікати з різними ключовими словами).
2. Побудувати user flows для критичних шляхів: hero CTA → `/start-project` → `/thank-you`; `/projects` → `/projects/[slug]` → CTA; `/industries/[slug]` (outreach-посилання з UTM) → форма.
3. Перевірити внутрішню перелінковку: кожна сторінка послуги веде до релевантних кейсів і назад.
4. Позначити маршрути, що вимагають `noindex` (thank-you, приватні/технічні сторінки) — на вхід для `seo-specialist`.

## Guardrails
- Не створювати десятки автоматично згенерованих `/industries/*` сторінок без унікального контенту (doorway pages).
- Розширення sitemap (нова мова, нова галузева сторінка) не повинно ламати існуючу URL-структуру.

## Output
Оновлений sitemap-документ + список user flows — вхід для `ui-ux-designer` (wireframes) і `seo-specialist` (SEO-мапа).
