---
name: color-contrast-and-motion-review
description: >-
  Check text/UI contrast ratios, text scaling, and reduced-motion handling. Use when reviewing design tokens, a visual spec, or animated UI for WCAG 2.2 AA.
---

# Color Contrast And Motion Review

Джерело: `{PROJECT_SPEC}` (напр. PROJECT_PROMPT.md), розділ доступності та розділ дизайну (тема/motion).

## Contrast (WCAG 2.2 AA)
- Звичайний текст: контраст ≥ 4.5:1 до фону
- Великий текст (≥24px або ≥19px bold): ≥ 3:1
- UI-компоненти й graphical objects (іконки-кнопки, межі полів форми): ≥ 3:1
- Перевіряти в обох темах (dark/light, розділ теми проєктного спеку) окремо — токен, що проходить у light, може провалюватись у dark

## Text scaling
- Layout не ламається при збільшенні тексту браузером до 200%
- Немає обрізаного/накладеного тексту на довших українських формулюваннях (розділ адаптивності проєктного спеку)

## Motion
- `prefers-reduced-motion: reduce` — вимикає або суттєво спрощує декоративні анімації (parallax, автоплей mockup-переходів), залишає лише необхідні для розуміння стану переходи
- Жодної автоплей-анімації, що блокує читання чи взаємодію (розділ motion/дизайну проєктного спеку)
- Курсорні/hover-ефекти — тільки desktop, не критичні для розуміння контенту (mobile не має альтернативи наведенню)

## Alt-тексти
- Кожне змістовне зображення (mockup, скріншот кейсу, фото відгуку) — описовий `alt`
- Декоративні зображення — `alt=""`, не відсутній атрибут

## Rules
- Контраст перевіряється на реальних design tokens інструментом (напр. axe, Lighthouse contrast checker), не на око — під час рев'ю токенів `ui-ux-designer`, до впровадження в код.
