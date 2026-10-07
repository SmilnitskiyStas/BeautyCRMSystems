---
name: write-backend-tests
description: >-
  Write Backend Tests — Use when implementing a standalone backend service.
x-generated-from: skills/backend/write-backend-tests/SKILL.md
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
