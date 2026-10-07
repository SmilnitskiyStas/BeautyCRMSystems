---
name: implement-metadata-and-hreflang
description: >-
  Implement Metadata And Hreflang — Use when implementing technical SEO or planning content strategy.
x-generated-from: skills/seo/implement-metadata-and-hreflang/SKILL.md
---

## generateMetadata Pattern
Кожен route (`app/[locale]/.../page.tsx`) експортує `generateMetadata`, не статичний `export const metadata` для локалізованих сторінок:

```ts
export async function generateMetadata({ params }: { params: { locale: string; slug: string } }): Promise<Metadata> {
  const t = await getPageSeo(params.locale, params.slug);
  return {
    title: t.title,
    description: t.description,
    alternates: {
      canonical: `${BASE_URL}/${params.locale}/${params.slug}`,
      languages: { uk: `${BASE_URL}/uk/${params.slug}`, en: `${BASE_URL}/en/${params.slug}` },
    },
    openGraph: { title: t.title, description: t.description, locale: params.locale },
  };
}
```

## Canonical
- Canonical завжди абсолютний URL, вказує на саму сторінку в поточній локалі (не завжди на "головну" версію).
- Query-параметри (UTM, `?page=`) не потрапляють у canonical.

## Hreflang
- Кожна пара `/uk/{path}` ↔ `/en/{path}` посилається одна на одну через `alternates.languages` — обидва напрямки, не тільки uk→en.
- `x-default` вказує на мовну версію за замовчуванням (уточнити яку саме — рішення `project-architect`/ADR).
- Якщо сторінка існує лише в одній локалі — не додавати hreflang на неіснуючу пару (краще взагалі не публікувати "half" сторінку).

## Rules
- `title`/`description` унікальні на кожен маршрут (вимога SEO-стратегії проєкту) — не шаблон з підстановкою лише ключового слова.
- Один логічний `h1` на сторінку.
- Метадані — з content-шару (MDX/typed content), не хардкод-рядки в компоненті.
