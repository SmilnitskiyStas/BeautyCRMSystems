---
name: keyboard-and-focus-review
description: >-
  Keyboard And Focus Review — Use during a WCAG 2.2 AA review of design specs or built UI.
x-generated-from: skills/accessibility/keyboard-and-focus-review/SKILL.md
---

# Keyboard And Focus Review

Джерело: `{PROJECT_SPEC}` (напр. PROJECT_PROMPT.md), розділ доступності (accessibility).

## Checklist
- Кожен інтерактивний елемент досяжний Tab-ом у логічному порядку (DOM-порядок відповідає візуальному)
- Focus state видимий завжди (не `outline: none` без заміни власним видимим індикатором)
- Skip link ("Перейти до основного контенту") — перший focusable елемент на сторінці
- Жодного keyboard trap — з будь-якого компонента (модалка, dropdown, mobile-меню) можна вийти клавіатурою (`Escape` або `Tab` за межі)
- Кастомні інтерактивні елементи (картки-посилання, кастомні dropdown) мають `tabindex="0"` і обробляють `Enter`/`Space`, якщо це не нативний `<a>`/`<button>`
- Modal/dropdown при відкритті переносить focus усередину, при закритті — повертає на елемент, що відкрив

## Rules
- Перевіряти ключові флоу (navigation, форма заявки, mobile-меню, project filters) виключно клавіатурою, без миші, від початку до кінця.
- Порядок Tab повинен збігатися з логічним порядком читання, навіть якщо CSS (flex/grid `order`) візуально міняє розташування.
- Рев'ю проводиться **до** імплементації нового інтерактивного компонента (на рівні specs `ui-ux-designer`), не тільки постфактум.
