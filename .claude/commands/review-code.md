---
description: Review code as a senior fullstack architect for architecture, quality, and security issues
argument-hint: <file path, directory, or module name>
---

# review-code.md

Рецензувати код або модуль, вказаний тут: $ARGUMENTS

Діяти як senior fullstack architect і code reviewer для кодової бази `{PROJECT_NAME}` (Next.js App Router / TypeScript / Tailwind / shadcn/ui). Читання й рецензування коду дозволене напряму в головній сесії (`CLAUDE.md` — виключення зі spawn-правила: "читання й дослідження коду/документів... архітектурні Q&A без запису коду"); самі правки — ні, якщо вони виходять за межі ~10 рядків.

Review for:
- відповідність `.claude/docs/architecture.md` і шаровим правилам (відповідна секція `{PROJECT_SPEC}`, напр. `PROJECT_PROMPT.md`, `v1-spec.md` тощо)
- коректність межі Server Component / Client Component (Server Components за замовчуванням; `"use client"` тільки де реально потрібні хуки чи інтерактивність)
- коректність валідації на межі Server Action / route handler (Zod, навіть якщо клієнт уже валідував)
- відсутність хардкодженого тексту в компонентах — увесь текст з типізованого content/MDX/CMS (відповідна секція `{PROJECT_SPEC}`)
- паритет i18n — uk і en обидві змістовно заповнені, без відсутніх ключів і без machine-translation "заглушок" (відповідна секція `{PROJECT_SPEC}`)
- строгість типів (немає невиправданого `any`)
- дублювання логіки й зайву складність
- безпеку (вхідна валідація, секрети поза client bundle, rate limiting на публічних write-ендпоінтах)
- продуктивність (зайвий client-side JS, відсутня оптимізація зображень, layout shifts)
- accessibility spot-check (semantic HTML, labels, focus, contrast) — фіксувати як знахідку, але остаточний вердикт WCAG 2.2 AA лишати за `accessibility-specialist`

Output format:
- Overall verdict
- Critical issues
- Improvements
- Refactoring suggestions
- Security / accessibility notes
- Final recommendation

Be concise and practical. Do not rewrite code unless requested.
