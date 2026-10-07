---
name: define-design-tokens
description: >-
  Define Design Tokens — Use when producing the design foundation — tokens, specs, theme, motion.
x-generated-from: skills/ui-ux-design/define-design-tokens/SKILL.md
---

## Purpose
Визначає базові design tokens на основі рекомендованого візуального напряму від `product-designer`.

## Token Checklist
- [ ] Color — primary/secondary/accent, neutral scale, semantic (success/warning/error/info), окремо для dark і light теми
- [ ] Typography — font families (heading/body/mono за потреби), type scale (h1-h6, body, caption), line-height, font-weight, letter-spacing
- [ ] Spacing — базова шкала (напр. крок 4/8px), використовується всюди замість довільних значень
- [ ] Grid — breakpoints (320/375/390/430/tablet/laptop/desktop/wide), container max-widths, кількість колонок
- [ ] Radius — шкала для buttons/cards/inputs/modals
- [ ] Shadows/elevation — шкала для cards/dropdowns/modals, узгоджена з light/dark темою

## Process
1. Вивести палітру й типографіку з обраної концепції (`.claude/skills/product-design/propose-visual-direction.md`), не з нуля.
2. Визначити токени як Tailwind theme extension (`tailwind.config`) або CSS custom properties — джерело істини одне, не дублювати в обох місцях.
3. Перевірити контраст кожної пари text/background (WCAG 2.2 AA) для обох тем.
4. Перевірити, що вся шкала spacing/radius/shadow покриває реальні потреби компонентів з `create-component-visual-spec.md` — не залишати токен, який ніхто не використовує.

## Guardrails
- Низький контраст — заборонений патерн з проєктного брифу візуального напряму; перевіряти явно, не "на око".
- Дрібний базовий розмір тексту — заборонений патерн з проєктного брифу візуального напряму; перевірити body text на мобільних breakpoints окремо.

## Output
Запис у `DESIGN_SYSTEM.md` з токенами + diff `tailwind.config` — вхід для `create-component-visual-spec.md` і `design-theme-system.md`.
