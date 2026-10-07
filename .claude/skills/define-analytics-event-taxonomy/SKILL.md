---
name: define-analytics-event-taxonomy
description: >-
  Define Analytics Event Taxonomy — Use when shaping conversion structure — hero/CTA, forms, analytics.
x-generated-from: skills/cro/define-analytics-event-taxonomy/SKILL.md
---

## Провайдер-агностична абстракція
Події йдуть через єдиний internal `track(event, payload)` helper (`lib/analytics/`), провайдер (GA/Plausible/PostHog/Clarity) підключається один раз під капотом (розділ аналітики проєктного спеку) — feature-код ніколи не викликає SDK провайдера напряму.

## Приклад базової таксономії подій
Стартовий набір подій, що добре узагальнюється на більшість лід-генераційних маркетингових сайтів — адаптувати назви й перелік під власні CTA, канали зв'язку та фічі конкретного проєкту, а не копіювати дослівно:

`hero_cta_click` · `view_projects_click` · `service_open` · `case_study_open` · `contact_form_start` · `contact_form_submit` · `contact_form_error` · `telegram_click` · `email_click` · `phone_click` · `upwork_click` · `language_change` · `theme_change` · `download_brief` · `review_form_submit`

## Lead attribution fields
Для кожної заявки зберігати разом з lead-записом: `source` · `medium` · `campaign` · `content` · `term` · `referrer` · `landing_page` · `selected_service` · `selected_project_type`.

## Rules
- Нову подію додає `cro-specialist` — назва в `snake_case`, дієслово-суфікс (`_click`, `_open`, `_submit`, `_error`), консистентно з існуючим переліком.
- **Ніколи** не відправляти PII (ім'я, email, текст повідомлення) в analytics payload без окремої законної підстави (розділ аналітики проєктного спеку) — event має фіксований payload, не весь form state.
- `contact_form_error` фіксує тип помилки (validation/rate-limit/server), не вміст полів форми.
- UTM/referrer/landing_page записуються один раз при першому візиті і зберігаються з lead (не перезаписуються повторним візитом у тій самій сесії).
