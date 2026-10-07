---
name: create-section-component
description: >-
  Create Section Component — Use when building the user-facing web layer.
x-generated-from: skills/frontend/create-section-component/SKILL.md
---

## Location
`src/components/sections/{section-name}/`

## Sections Covered (підрозділи структури головної сторінки з проєктної специфікації)
Header/nav, Hero, Social proof, Services grid, Featured projects, Process steps, Tech stack, Testimonials, CTA block.

## Pattern
- Named export, props = типізований content-об'єкт (з content layer) — секція не знає, звідки прийшли дані.
- Server Component за замовчуванням; у client-компонент переходить лише інтерактивна частина (напр. hover-картка проєкту, tab-перемикач послуг) — виносити цю частину в окремий дочірній `"use client"` компонент, а не позначати всю секцію.
- Кожна секція самодостатня й reusable між сторінками (напр. CTA block використовується на homepage і на service-сторінках).

## Section-Specific Rules
- **Hero** — короткий lead-form варіант може бути вбудований (див. специфікацію короткої лід-форми в проєктній документації); статус проєкту в кейсах ніколи не приховується.
- **Social proof** — тільки реальні цифри з конфігу/CMS; за відсутності даних показувати компетенції/технології замість порожнечі.
- **Featured projects** — картка показує статус (`Concept`/`MVP`/…), не видає концепт за запущений продукт.
- **Testimonials** — за відсутності реальних відгуків показувати принципи роботи/гарантії/прозорість замість порожнього блоку чи фейкових відгуків.
- **CTA block** — завжди дві дії: основна ("Обговорити проєкт") і другорядна ("Отримати попередню оцінку" / "Переглянути роботи").

## Anti-patterns
- Компонент, що одночасно фетчить дані, трансформує і рендерить — розділяти.
- Хардкод копірайту всередині JSX замість пропа з content layer.
- Карусель без перевіреної мобільної поведінки.
