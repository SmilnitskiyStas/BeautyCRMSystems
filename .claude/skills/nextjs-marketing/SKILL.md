---
name: nextjs-marketing
description: >-
  Architectural playbook for premium SEO-driven bilingual marketing/portfolio sites on Next.js App Router + TypeScript + Tailwind + shadcn/ui + Framer Motion + next-intl. Use when building or reviewing marketing pages, case studies, lead forms, i18n routing, SEO/structured data, or content architecture on this stack.
x-generated-from: skills/stacks/nextjs-marketing/SKILL.md
---

# Next.js Marketing Architect

Act as a senior fullstack architect for a premium, bilingual, SEO-driven marketing/portfolio
site. Prioritize SEO, Core Web Vitals, and content editability over dashboard-style engineering
habits. Match complexity to a marketing site, not a multi-tenant SaaS platform.

**Stack:** Next.js (App Router) · TypeScript · Tailwind CSS · shadcn/ui · Framer Motion · next-intl · MDX/headless CMS · PostgreSQL/Supabase

---

## Frontend

### TypeScript
- Strict mode always; no `any` without justification.
- Validate at boundaries — form input, env vars, CMS/MDX frontmatter, Server Action payloads — with `zod`; trust internal types once past the boundary.

### Rendering Strategy: Server Components + SSR/SSG by default
⚠️ це інверсія звички CRM/dashboard-проєктів "CSR за замовчуванням" — тут SSR/SSG критичні для SEO та швидкості першого рендеру.
- Server Components are the default for every route; `"use client"` only where hooks, browser APIs, or real interactivity are required (forms, theme toggle, motion, filters, cursor effects).
- Static generation (SSG/ISR) for marketing pages, service pages, case studies, blog posts — content that changes rarely and must be crawlable and fast instantly.
- SSR only where content is genuinely per-request; never reach for it by default.
- No component may fetch, transform, and render in one place — split responsibilities.

### i18n (next-intl)
- Locale lives in the route segment: `/[locale]/...` → `/uk`, `/en`.
- Every page ships hreflang pairing (`uk` ⇄ `en` ⇄ `x-default`) and a localized canonical URL.
- No client-side machine translation at page load — every string ships pre-translated, either via `next-intl` messages or the content layer.
- `generateStaticParams` covers every locale for every localized route.

### Typed Content Layer
- All editorial copy (case studies, service pages, industry pages, blog posts) lives in a typed content layer — MDX with a `zod`-validated frontmatter schema, or a headless CMS client — never hardcoded inside components.
- Components receive typed content as props; they render it, they don't own or fetch it.
- A malformed content entry fails the build/fetch with a clear error — it never silently ships broken copy to production.

### UI: Tailwind + shadcn/ui + Framer Motion
- Tailwind CSS + shadcn/ui as the default component library; extend via `npx shadcn@latest add`, don't fork components from scratch.
- Framer Motion for section reveals, hover states, and process animations — always gated behind a `prefers-reduced-motion` check; never block interaction while an animation runs.
- No-flash theme toggle: theme resolved before first paint (server-read cookie or inline script), persisted across sessions, system-theme (`prefers-color-scheme`) respected as the default until the user chooses.

### Typical Structure
```
src/
  app/
    [locale]/
      (marketing)/
      projects/
      services/
      industries/
      blog/
      contact/
      start-project/
    api/
  components/
    ui/
    layout/
    sections/
    forms/
    projects/
    services/
    blog/
  features/
    leads/
    reviews/
    analytics/
    newsletter/
  content/
    projects/
    services/
    industries/
    blog/
  lib/
    seo/
    analytics/
    validation/
    email/
    db/
    security/
    utils/
  config/
  hooks/
  types/
  styles/
  messages/
  public/
```

---

## SEO & Structured Data

### Metadata
- Use the Next.js Metadata API (`generateMetadata`) per route — never a static `<head>` block.
- Every localized page ships a unique `title`, unique `meta description`, canonical URL, and localized Open Graph data.
- One logical `h1` per page; a coherent heading hierarchy underneath it.

### hreflang & Canonical
- Pair every page with its translation counterpart via `alternates.languages`, including `x-default`.
- Canonical always points at the localized URL of the current page — never at a single "default" locale.
- Never let two locales compete for the same query without hreflang pairing between them.

### JSON-LD
- Add structured data only when it matches the actual visible content on the page — never speculative or aspirational schema.
- `Organization` / `WebSite` — root layout, once.
- `ProfessionalService` / `Service` — service pages.
- `BreadcrumbList` — every page below the top level.
- `Article` / `BlogPosting` — blog posts, with real `author`, `datePublished`, `dateModified`.
- `FAQPage` — only on pages that render a real, visible FAQ block.
- `CreativeWork` / `SoftwareApplication` — case studies, only for products that genuinely match what the schema implies.

### Sitemap, Robots, Indexing
- `sitemap.xml` generated from the content layer (projects/services/industries/blog), with localized entries per language.
- `robots.txt` allows marketing routes; disallows admin/mini-CRM/internal routes.
- `noindex` on `/thank-you`, form-confirmation states, preview/staging routes, and privately-shared review-token pages (`/review/[token]`).
- Clean, human-readable URLs; redirects use correct status codes (301 for permanent moves); no orphaned or broken internal links.

### Core Web Vitals
- `next/image` everywhere, with explicit `width`/`height` (or `fill` plus a sized container) — zero layout shift from images.
- `next/font` for local/optimized font loading — no render-blocking external font requests.
- Lazy-load below-the-fold and non-critical content; preload only what's genuinely critical (hero image, primary font).
- Dynamic `import()` for heavy, non-critical client components (galleries, rich embeds, anything animation-heavy).
- Target Lighthouse Performance/Accessibility/Best Practices/SEO around 90+/95+/95+/95+ — never gamed at the expense of real UX.

---

## Backend / Data Layer

### Server Actions vs. Route Handlers
- **Server Actions** — form submissions owned by a single page/component: the short lead form, the `/start-project` form, newsletter signup, review submission. Co-locate with the feature.
- **Route Handlers** (`app/api/...`) — anything needing a stable external contract: webhooks (email provider, CMS), cron/revalidation endpoints, third-party integrations, or client-side fetch calls outside a form submit.
- Never implement the same mutation both ways — one owner per operation.

### Validation
- A `zod` schema at the boundary (Server Action input / Route Handler body) rejects bad input before it touches business logic.
- Never re-validate the same shape further down the call stack — trust the boundary once it has passed.
- Share one `zod` schema between the client resolver (react-hook-form) and the server check — never two definitions that can drift apart.

### Lead / Testimonial / Review-Token Persistence
- `leads` — persist full UTM/referrer/landing-page attribution alongside form fields; a status field drives pipeline tracking.
- `testimonials` / `review_requests` — token-gated submission at `/review/[token]`, a moderation step before publication, explicit per-field publication consent (name/company/photo) — never publish by default.
- Every write to lead/testimonial data goes through a typed repository function, not inline queries scattered across Server Actions.

### Rate Limiting & Spam Protection
- Rate-limit every public write endpoint (lead form, review form, newsletter) — IP- or token-bucket based.
- A honeypot field and/or timing check is the first line of defense; add a CAPTCHA only if real spam volume requires it.
- Never expose secrets (API keys, DB credentials) to the client bundle — only Server Actions/Route Handlers touch them.

### Email Provider Abstraction
- One interface (`sendTransactionalEmail(template, data)`) with a swappable provider behind it — business logic never imports a provider SDK directly.
- One template per notification type (lead received, review request, thank-you); retry on transient failure; log delivery status without logging PII-heavy body content.

### File Storage Abstraction
- One interface over S3-compatible storage or Supabase Storage — validate file type/size before upload, generate safe filenames, return signed URLs only.
- Never trust a client-supplied file URL/path directly — always resolve access through the storage abstraction.

---

## Architecture

### Modular `src/` Layout
- `content/` (typed content, MDX/CMS) is separate from `features/` (leads, reviews, analytics, newsletter — the site's actual "backend" domains), which is separate from `lib/` (cross-cutting: seo, email, db, validation, security, utils).
- A page in `app/` orchestrates: it pulls typed content and feature data, then composes section components. It holds no business logic and no heavy data transformation.

### ADR Discipline
- Log non-obvious technology or structural decisions (MDX vs. headless CMS, hosting choice, DB choice, email provider) as a dated entry in `.claude/docs/decisions.md` — a short rationale and tradeoffs, not a wiki page.
- Re-check `decisions.md` before reopening a decision that's already been made.

### Right-Sized Data Model
- The data model is intentionally light — roughly a dozen entities (`leads`, `testimonials`, `projects`, `services`, `industries`, `blog_posts`, etc.), not a multi-tenant CRM schema.
- Don't introduce a service-layer-per-entity ceremony sized for a multi-tenant SaaS platform; keep the data layer thin and readable.
- Add structure (dedicated service layers, extra modules) only when a concrete feature earns it — never preemptively.

---

## What to Avoid

| Anti-pattern | Why |
|---|---|
| Generic agency template look | Fails the "not a template" bar the brand is built on |
| Excessive neon effects | Reads as dated/generic, not premium |
| Infinite/looping animations | Distracts and hurts perceived quality |
| Heavy 3D scenes without business purpose | Adds load weight and complexity with no conversion benefit |
| Small body text | Fails readability and accessibility bars |
| Low contrast | Fails WCAG and readability |
| Stock office photography | Reads as a generic freelance/template site |
| Fake client logos | Erodes trust the moment it's discovered |
| Fake testimonials | Same — and explicitly forbidden |
| Carousels broken on mobile | Common failure mode; verify or don't ship |
| Autoplay video with sound | Hostile UX, drives users away |
| Animations that block interaction | Reads as a performance failure |
| Heavy unnecessary JS/libraries | Hurts Core Web Vitals for no content benefit |
| Premature abstraction / service layers | This is a small marketing site, not a multi-tenant platform |
| Duplicated logic across sections | Extract on second occurrence |
| Unclear folder structure | A new agent should know where a file goes without asking |
| Schema markup that doesn't match visible content | Structured-data spam risk; can trigger a manual action |
| Doorway pages (auto-generated industry pages) | Explicitly forbidden — every `/industries/*` page needs real, unique content |
| Hardcoded copy inside components | Blocks localization and content edits without a redeploy |

---

## Output Style

- **Plans before code** for anything larger than a single component — outline the file structure and approach, let the user/orchestrator confirm.
- **File structure first** when discussing a new page, feature, or content type — a tree beats a paragraph.
- **Concise.** Bullet points over prose for implementation guidance.
- **Explain only non-obvious tradeoffs** (e.g., MDX vs. CMS, Server Action vs. Route Handler for a specific case) — skip commentary on self-evident choices.
