---
name: lighthouse-testing
description: >-
  Lighthouse Testing — Use when writing or running tests.
x-generated-from: skills/qa/lighthouse-testing/SKILL.md
---

## Пороги (розділ продуктивності проєктного спеку)

| Категорія | Ціль |
|---|---|
| Performance | 90+ |
| Accessibility | 95+ |
| Best Practices | 95+ |
| SEO | 95+ |

## Checklist
- Прогнати на ключових сторінках: головна, сторінка послуги, сторінка кейсу, `/start-project`, сторінка блог-статті
- Перевіряти mobile і desktop профіль окремо — пороги орієнтовані на обидва
- Layout shifts (CLS), font-loading, image format/lazy-loading — типові джерела просідання Performance
- Regression-запуск після будь-якої зміни в hero/анімаціях/шрифтах, не тільки перед релізом

## Boundary
Accessibility-скор тут — **лише smoke-сигнал регресії** (впав з 98 на 80 → щось зламали), не остаточний вердикт відповідності. Реальне судження про WCAG 2.2 AA — зона `accessibility-specialist`; автоматичний скор може бути 100 і все одно пропустити keyboard trap чи неправильний focus order.

## Rules
- Не "підганяти" показник штучно на шкоду реальному UX (розділ продуктивності проєктного спеку) — напр. не прибирати анімацію лише заради CLS-цифри, якщо анімація реально потрібна для ієрархії.
- Просідання нижче порогу — блокер для merge задачі, що його спричинила, не "відкладемо на потім".
