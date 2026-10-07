---
name: create-component-visual-spec
description: >-
  Create Component Visual Spec — Use when producing the design foundation — tokens, specs, theme, motion.
x-generated-from: skills/ui-ux-design/create-component-visual-spec/SKILL.md
---

## Purpose
Стандартизує формат специфікації для кожного UI-компонента перед імплементацією (фаза Design foundation).

## Components in Scope
Core: buttons, form controls, cards, badges, navigation.
Overlay/feedback: modal, accordion, tabs, toast, skeleton, empty state, error state.

## Spec Format (на компонент)
1. **Variants** — напр. button: primary/secondary/ghost/destructive; badge: статусні варіанти (Concept/MVP/In Development/…)
2. **States** — default/hover/focus/active/disabled/loading; для форм ще invalid/valid
3. **Sizes** — sm/md/lg, якщо застосовно
4. **Tokens used** — які color/spacing/radius/shadow tokens застосовуються (посилання на `define-design-tokens.md`)
5. **Motion** — які переходи присутні (hover, open/close), посилання на `specify-motion-guidelines.md`
6. **Accessibility** — стиль focus-visible, ARIA role/attributes, keyboard interaction (Enter/Space/Esc/arrow keys для tabs/accordion)
7. **Responsive behavior** — що змінюється на mobile (напр. modal → full-screen sheet, nav → mobile menu)
8. **Empty/error/loading content** — для card/list-подібних компонентів обов'язково специфікувати всі три стани, не лише happy path

## Guardrails
- shadcn/ui компонент розширюється specification-first — спека визначає, що саме розширюється; `frontend-developer` не імпровізує варіанти на льоту.
- Модальні вікна, dropdown, accordion — спека явно описує keyboard-доступність, не залишає "на розсуд розробника".
- Каруселі — тільки якщо явно специфікована коректна поведінка на мобільних (заборонений список з проєктного брифу візуального напряму забороняє каруселі, зламані на мобільних).

## Output
Один markdown-файл на компонент (або згрупований по core/overlay) у `DESIGN_SYSTEM.md` — вхід для `frontend-developer` (`create-section-component.md`, `create-form.md`).
