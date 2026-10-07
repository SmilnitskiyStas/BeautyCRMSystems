---
name: specify-motion-guidelines
description: >-
  Specify Motion Guidelines — Use when producing the design foundation — tokens, specs, theme, motion.
x-generated-from: skills/ui-ux-design/specify-motion-guidelines/SKILL.md
---

## Purpose
Визначає принципи й межі використання анімації перед тим, як `frontend-developer` додає Framer Motion до будь-якого компонента.

## Motion Principles (анімація повинна)
- Пояснювати взаємодію (напр. розкриття accordion, перехід між станами)
- Підкреслювати ієрархію (напр. послідовна поява елементів hero)
- Створювати відчуття якості, не відволікати від контенту
- Не погіршувати швидкість (жодна анімація не блокує First Contentful Paint чи interaction)
- Враховувати `prefers-reduced-motion` — завжди, без винятків

## Allowed Patterns (приклади)
- Делікатна поява секцій при скролі
- Анімовані hover-стани (кнопки, картки)
- Плавна зміна зображень у кейсах
- Мікроанімації кнопок (натискання, завантаження)
- Анімований процес роботи (кроки процесу з проєктного брифу)
- Інтерактивні картки послуг
- Легкий parallax лише для декоративних елементів (не для контенту)
- Курсорні ефекти — лише desktop, ніколи на шкоду usability

## Forbidden Animation Patterns (заборонено — checklist)
- [ ] Нескінченні анімації (looping без завершення)
- [ ] Важкі 3D-сцени без бізнес-користі
- [ ] Каруселі, що погано працюють на мобільних пристроях
- [ ] Автозапуск відео зі звуком
- [ ] Анімації, що блокують взаємодію (неможливо клікнути/скролити, поки анімація не завершиться)
- [ ] Важкі JS-анімаційні бібліотеки понад Framer Motion без реальної потреби

## Spec Format (на анімовану секцію/компонент)
1. Trigger (on scroll into view / on hover / on mount / on state change)
2. Properties animated (opacity/transform — уникати анімації layout-властивостей)
3. Duration/easing
4. Reduced-motion fallback (зазвичай — миттєва поява без transform)

## Output
Motion-гайдлайн документ (принципи + specs по секціях) — вхід для `frontend-developer` (`add-motion-animation.md`).
