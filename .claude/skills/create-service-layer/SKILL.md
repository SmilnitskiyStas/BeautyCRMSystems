---
name: create-service-layer
description: >-
  Implement business logic in a service layer isolated from routing and persistence. Use when a feature needs orchestration, rules, or transactions.
x-generated-from: .claude/skills/create-service-layer/SKILL.md
---

## Interface / контракт
- Визначати сервіс окремо від route-шару (директорія за конвенцією фреймворку: `services/`, `Application/Features/{Domain}/` тощо)
- Повертати типізований результат (`{ ok: true, data }` / `{ ok: false, error }` або еквівалент вашої мови) для очікуваних бізнес-помилок
- Кидати виключення лише для непередбачуваних/інфраструктурних помилок

## Реалізація
- Один сервіс — одна доменна відповідальність
- Constructor/dependency injection замість прямого імпорту інфраструктури всередині бізнес-логіки
- Бізнес-логіка тільки тут, ніколи в route-хендлері/контролері

## DI / реєстрація
Зареєструвати сервіс у контейнері залежностей вашого фреймворку (модуль NestJS, `DependencyInjection.cs` в ASP.NET Core, ручний factory для Express/Fastify тощо) — конкретний механізм залежить від стеку, принцип (явна, тестована реєстрація замість прихованих singleton-імпортів) — ні.
