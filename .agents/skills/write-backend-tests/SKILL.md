---
name: write-backend-tests
description: >-
  Write unit and integration tests for services and endpoints: happy path, not-found, conflict, auth guard. Use after implementing backend logic.
x-generated-from: .claude/skills/write-backend-tests/SKILL.md
---

## Налаштування
Тестовий фреймворк вашого стеку (Vitest/Jest для Node.js, xUnit для .NET, pytest для Python тощо) + мок-бібліотека для зовнішніх залежностей.

## Конвенція іменування тестів
`methodName_умова_очікуваний-результат`
Приклад: `createProduct_returns-error_when-sku-already-exists`

## Обов'язкове покриття
- Happy path
- Сутність не знайдена
- Дублікат / конфлікт
- Guard авторизації

## Правила
- Тестувати поведінку, не реалізацію
- Мокати тільки репозиторії/зовнішні сервіси — ніколи доменні сутності
- Build + усі тести зелені перед handoff
