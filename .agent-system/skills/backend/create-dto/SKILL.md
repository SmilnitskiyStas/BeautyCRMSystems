---
name: create-dto
description: >-
  Define request/response DTOs and their mapping, separate from domain entities. Use when an endpoint needs new input/output shapes.
---

## Розташування
Поруч із доменом, що описує (`{domain}/dto/` або еквівалент), не всередині route-шару.

## Іменування
- `ProductDto` / `ProductResponse` — відповідь
- `CreateProductRequest` — створення
- `UpdateProductRequest` — оновлення

## Патерн
- Immutable-структури (readonly-поля, `type` у TypeScript, records у C#, dataclass у Python тощо)
- Ніколи не повертати доменну модель/entity напряму з API — завжди через DTO
- Валідаційні схеми (Zod / class-validator / FluentValidation / еквівалент) — на request DTO

## Мапінг
Мапити в service-шарі через явну функцію (`toDto(entity)`), а не розкидати мапінг по контролерах. Бібліотеки автомапінгу (AutoMapper тощо) — тільки якщо мапінг справді складний і виправдовує додаткову залежність.
