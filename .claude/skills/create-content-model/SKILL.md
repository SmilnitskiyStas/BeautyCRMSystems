---
name: create-content-model
description: >-
  Create Content Model — Use during the discovery phase — positioning, IA, content model.
x-generated-from: skills/product-design/create-content-model/SKILL.md
---

## Purpose
Визначає повторювані структури контенту (content models) для кейсів і галузевих сторінок, які потім споживає типізований content layer (див. `.claude/skills/frontend/integrate-content.md`).

## Case Study Content Model
1. Назва
2. Коротка ціннісна пропозиція
3. Статус проєкту (`Concept`/`MVP`/`In Development`/`Internal Product`/`Prototype`)
4. Тип проєкту
5. Індустрія
6. Роль команди
7. Проблема
8. Цілі
9. Обмеження
10. Дослідження
11. Запропоноване рішення
12. Інформаційна архітектура
13. Основні функції
14. UX-рішення
15. UI-рішення
16. Технічна архітектура
17. Технології
18. Безпека
19. Інтеграції
20. Оптимізація продуктивності
21. Результат
22. Наступні етапи
23. Галерея
24. CTA

## Industry Page Content Model
- Ніша (retail / restaurants / healthcare / beauty / construction / logistics / education / startups / local-business)
- Реальні приклади проблем цієї ніші
- Релевантні рішення (сайт меню, онлайн-бронювання, доставка, CRM-інтеграція, програма лояльності, кабінет, аналітика, push, мобільний застосунок — обрати релевантну вибірку, не всі одразу)
- Конкретний CTA під нішу
- Унікальні metadata (title/description) — не шаблон із заміною одного слова

## Rules
- Кожен пункт моделі — типізоване поле в content layer (MDX frontmatter або CMS-схема), не вільний текст у компоненті.
- Case study без підтверджених цифр результату використовує описові формулювання (скорочення ручних операцій, централізація даних, автоматизація сповіщень) — не вигадує метрики.
- Industry-сторінка публікується лише з реальним унікальним вмістом — інакше не публікувати (анти-doorway-правило).
- Обидві моделі валідуються `zod`-схемою на етапі build/fetch (див. `.claude/skills/nextjs-marketing-architect/SKILL.md`).

## Output
Дві типізовані content-схеми (case study, industry page) + приклад заповненого чернетки для першого реального кейсу — вхід для `copywriter` (наратив) і `fullstack-developer`/`frontend-developer` (типи, рендеринг).
