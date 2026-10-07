---
name: cross-browser-testing
description: >-
  Cross Browser Testing — Use when writing or running tests.
x-generated-from: skills/qa/cross-browser-testing/SKILL.md
---

## Матриця браузерів (розділ тестування проєктного спеку)
Chrome · Safari · Firefox · Edge · mobile Safari · mobile Chrome

## Breakpoints (розділ адаптивності проєктного спеку)
320px · 375px · 390px · 430px · tablet · laptop · desktop · wide desktop

## На кожному breakpoint перевірити (розділ адаптивності проєктного спеку)
mobile menu · hero · картки · таблиці · форми · галереї · модальні вікна · довгі заголовки · українські й англійські тексти (різна довжина рядка!) · landscape orientation · touch targets · hover-independent interaction

## Rules
- Mobile-версія — не спрощений/зламаний desktop; функціональність еквівалентна (розділ адаптивності проєктного спеку).
- Український текст зазвичай довший за англійський — перевіряти обидві локалі на найвужчому (320px) і найширшому breakpoint, а не тільки одну.
- Safari (desktop і mobile) перевіряти окремо навіть якщо Chrome-тест пройшов — типове джерело розбіжностей (flexbox/grid edge cases, date input, backdrop-filter).
- Touch targets — мінімум ~44x44px на сенсорних breakpoints, не тільки desktop hover-стан.
