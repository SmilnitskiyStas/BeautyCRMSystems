---
name: create-page-route
description: >-
  Create Page Route — Use when building the user-facing web layer.
x-generated-from: skills/frontend/create-page-route/SKILL.md
---

## Location
`src/app/[locale]/(marketing)/...` для маркетингових сторінок; `src/app/[locale]/projects|services|industries|blog|contact|start-project/...` за плановою структурою проєкту.

## Pattern
1. Server Component за замовчуванням — `"use client"` тільки якщо сама сторінка (не дочірній компонент) реально потребує хуків.
2. `generateMetadata` для локалізованих title/description/canonical/hreflang/OG (див. `.claude/skills/nextjs-marketing-architect/SKILL.md`, розділ SEO).
3. `generateStaticParams` для локалей (і slug-based маршрутів — projects/services/industries/blog).
4. Сторінка = тонка оркестрація: отримати типізований контент (`integrate-content.md`) → передати в section-компоненти (`create-section-component.md`). Жодної важкої трансформації даних у самому page-файлі.
5. `loading.tsx` і `error.tsx` поруч із кожним маршрутом, що виконує асинхронну роботу.

## Rules
- Page-файл не фетчить, не трансформує і не рендерить деталі одночасно — розділяти на функції/компоненти.
- Дані для статичного контенту (projects/services/industries/blog) приходять із content layer, не інлайняться в JSX.
- Динамічні сегменти (`[slug]`) повертають `notFound()` для невідомого slug — не порожню сторінку.
- Breadcrumbs (`BreadcrumbList` JSON-LD) додаються на кожній сторінці глибше рівня 1.
- noindex-маршрути (`/thank-you`, preview) явно позначені в metadata — не покладатись лише на robots.txt.

## Anti-patterns
- Fetch + важка трансформація + JSX-розмітка в одному компоненті
- `"use client"` на всій сторінці через один інтерактивний віджет всередині — виносити віджет в окремий client-компонент
