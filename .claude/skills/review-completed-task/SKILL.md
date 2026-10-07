---
name: review-completed-task
description: >-
  Independent review of a completed implementation against acceptance criteria and standards. Use when reviewing another agent's finished work.
x-generated-from: skills/workflow/review-completed-task/SKILL.md
---

## Purpose
Визначає процес рев'ю після завершення задачі, перед переходом статусу в `done`.

## Review Checklist

### Code Quality
- Код відповідає `.claude/skills/personal-engineering-standards/SKILL.md` і `.claude/skills/nextjs-marketing-architect/SKILL.md`
- Server Components за замовчуванням; `"use client"` лише де виправдано
- Типи строгі, немає `any` без обґрунтування
- Немає закоментованого коду чи `// TODO: пізніше`

### Tests
- Unit-тести написані для нової валідаційної/доменної логіки
- Тести покривають не лише happy path (порожні значення, невалідний input, помилки мережі)
- `npm run typecheck`, `npm run lint`, `npm run build` проходять

### Security ({PROJECT_NAME}-specific)
- Форми мають і server-side, і client-side валідацію (zod)
- Rate limiting присутній на кожному публічному write-ендпоінті (лід-форма, review-форма, newsletter)
- Секрети (API-ключі, DB-креденшли) відсутні у client bundle й у git-історії
- Review-token flow (`/review/[token]`) не дозволяє повторне надсилання чи перебір токенів
- Файлові завантаження перевіряють тип і розмір, повертають лише signed URLs

### Content Rules
- Жоден текст не хардкодиться в компоненті — тільки типізований content/MDX/CMS
- Немає фейкових клієнтів, відгуків, нагород, команди чи статистики
- Незавершені проєкти позначені статусом (`Concept`/`MVP`/`In Development`/`Prototype`)

### Accessibility (spot-check)
- Keyboard navigation працює, немає keyboard traps
- Видимі focus states, коректний контраст
- Форми мають labels і error announcements
- Анімації враховують `prefers-reduced-motion`

### Documentation
- `.claude/docs/*` оновлено, якщо змінилась архітектура, доменна модель чи SEO-мапа
- Task log створено в `.claude/logs/tasks/`
- **Якщо задача включала виправлення неочікуваної помилки** (не тривіальний typo) — `.claude/docs/known-issues.md` має запис із заповненими `Root cause` і `Prevention`, не лише `Resolution` (`.claude/skills/workflow/resolve-known-issue.md`). Без цього задача не переходить у `done`.

## Review Log Location
`.claude/logs/reviews/TASK-ID_YYYY-MM-DD_review.md`
