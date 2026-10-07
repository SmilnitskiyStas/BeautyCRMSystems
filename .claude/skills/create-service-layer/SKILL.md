---
name: create-service-layer
description: >-
  Create Service Layer — Use when implementing a standalone backend service.
x-generated-from: skills/backend/create-service-layer/SKILL.md
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
