---
name: manual-test-checklist
description: >-
  Produce a manual test checklist for what automation cannot cover. Use at release or for exploratory verification.
x-generated-from: .claude/skills/manual-test-checklist/SKILL.md
---

# Manual Test Checklist

Для кожної завершеної сторінки/фічі перевірити:

## Базове
- Сторінка вантажиться без console errors
- Усі CTA клікабельні й ведуть на очікувану дію (не заглушки)
- Loading / error / empty стани присутні там, де є async-дані
- 404 показує кастомну сторінку, не default Next.js/framework error

## Форми
- Client-side валідація показує зрозумілі повідомлення про помилки
- Server-side валідація теж спрацьовує (перевірити з вимкненим JS або прямим запитом — client-side не єдиний захист)
- Успішний submit веде на thank-you сторінку
- Невдалий submit не губить введені користувачем дані

## Контент
- Немає placeholder-тексту (`{BRAND_NAME}` тощо) там, де вже мав бути реальний контент
- Немає фейкових проєктів/клієнтів/відгуків/цифр (розділи достовірності контенту та показників проєктного спеку)

## Rules
- Перевіряти обома локалями (`/uk`, `/en`) — не тільки тією, в якій розроблялась фіча.
- Будь-який знайдений баг — окремий запис, не "начебто ок" на око.
