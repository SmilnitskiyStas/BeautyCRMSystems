---
name: configure-sitemap-and-robots
description: >-
  Configure Sitemap And Robots — Use when implementing technical SEO or planning content strategy.
x-generated-from: skills/seo/configure-sitemap-and-robots/SKILL.md
---

## sitemap.ts
Next.js `app/sitemap.ts`, генерує записи для обох локалей на кожен публічний маршрут:

```ts
export default function sitemap(): MetadataRoute.Sitemap {
  return publicRoutes.flatMap((route) => (["uk", "en"] as const).map((locale) => ({
    url: `${BASE_URL}/${locale}${route.path}`,
    lastModified: route.updatedAt,
    alternates: { languages: { uk: `${BASE_URL}/uk${route.path}`, en: `${BASE_URL}/en${route.path}` } },
  })));
}
```

- Джерело `publicRoutes` — той самий content-шар, що й навігація/routing, не окремий hardcoded список.
- Image sitemap додається лише за потреби, не за замовчуванням.

## robots.ts
```ts
export default function robots(): MetadataRoute.Robots {
  return {
    rules: [{ userAgent: "*", allow: "/", disallow: ["/admin", "/thank-you", "/api"] }],
    sitemap: `${BASE_URL}/sitemap.xml`,
  };
}
```

## noindex — приватні/технічні сторінки
- `/thank-you`, mini-CRM/admin-маршрути (описані в адмін-розділі проєктної специфікації), `/500`, службові API-роути → `robots: { index: false, follow: false }` у `generateMetadata`.
- `/404` — `noindex`, але не `disallow` в robots.txt (сторінка повинна бути crawlable, щоб пошуковик бачив 404 status).

## Rules
- Кожен новий приватний маршрут одразу отримує noindex — не постфактум після індексації.
- Sitemap і robots.txt перевіряються в CI (перевірка broken links, згідно з CI/CD вимогами проєкту) перед релізом.
