---
name: write-fullstack-tests
description: >-
  Write Fullstack Tests — Use when implementing a light backend colocated with the frontend.
x-generated-from: skills/fullstack/write-fullstack-tests/SKILL.md
---

## Обсяг
Server Actions і route handlers для `leads`, `review_requests`/`testimonials`, contact-форм.

## Required Coverage
- Happy path (валідний вхід → успішний запис + правильний return shape)
- Validation edge cases: порожні required-поля, невалідний email, відсутній consent, over-length text
- Rate limit guard: N+1 запит за короткий проміжок → відхилення, не проходить до DB
- Review-token guard: прострочений токен, вже використаний токен, невідомий токен — кожен свій test case
- Спроба обійти боундарі: прямий виклик Server Action з "сирим" непарсеним payload (не тільки через UI-форму)

## Mocking
- Мокати тільки зовнішні провайдери: email sender, file storage — через інтерфейс з `integrate-email-notifications.md`.
- **Ніколи** не мокати саму Zod-валідацію чи бізнес-правила — тест повинен проходити через реальну validation logic.
- DB — тестова БД або transaction rollback per test, не мок репозиторію для validation-тестів.

## Rules
- Тест називати за конвенцією `{дія}_{умова}_{очікуваний результат}`, напр. `submitLead_missingConsent_returnsValidationError`.
- Build + усі тести проходять перед handoff іншому агенту.
