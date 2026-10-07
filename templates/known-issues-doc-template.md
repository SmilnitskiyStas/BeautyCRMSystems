<!--
  Скопіювати цей файл як .claude/docs/known-issues.md на старті нового проєкту
  (звичайний живий документ, не частина .claude/ бібліотеки — на відміну від решти
  templates/, він одразу стає робочим станом конкретного проєкту). Видалити цей коментар.
-->

# Known Issues

**Owner:** qa-engineer (підтримує структуру файлу) — **записує будь-який агент**, що знайшов чи виправив проблему
**Updated:** YYYY-MM-DD

Два різні розділи нижче — не плутати:
- **Active Issues** — те, що зараз зламано й ще не виправлено (звичайний баг-трекер).
- **Resolved Issues** — те, що вже виправлено, **з обов'язковими `Root cause` і `Prevention`**. Це фактично журнал "не наступати на ті самі граблі" — його читає **кожен агент перед стартом будь-якої задачі** (`.claude/skills/workflow/context-loader.md`, крок "Always"). Запис без `Prevention` не виконує цю функцію — не закривати issue без нього.

## Format

```
### KI-NNN: [Title]
Severity: critical / high / medium / low
Status: open / resolved
Discovered in: TASK-XXX
Resolved in: TASK-YYY (якщо resolved)
Area: frontend / backend / database / security / infra / ... (для швидкого пошуку)

Description: [симптом — що спостерігалось, за яких умов відтворюється]

Root cause: [лише для resolved — чому це насправді сталося; технічна причина,
  не "не перевірили щось", а "X завжди повертає Y за умови Z, а код вважав що W"]

Resolution: [що конкретно змінено, щоб виправити]

Prevention: [лише для resolved — конкретне, дієве правило: лінт-правило,
  пункт чекліста в skill-файлі, тест, що тепер це покриває. Не "бути уважнішим".
  Якщо правило узагальнюється — перенести його також у відповідний
  `.claude/skills/{domain}/*.md` чи `.claude/agents/{role}.md`, а не лишати
  тільки тут (див. `.claude/skills/workflow/resolve-known-issue.md`)]
```

## Active Issues
_(порожньо)_

## Resolved Issues
_(порожньо)_
