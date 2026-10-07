---
description: Generate or update project documentation in .claude/docs/ or root-level docs
argument-hint: <topic, module, or document name, e.g. "database-schema" or "ADR-011 hosting decision">
---

# generate-docs.md

Згенерувати або оновити документацію проєкту для: $ARGUMENTS

Це задача `documentation-writer` (`CLAUDE.md`, Agent → Task mapping: "Оновлення `.claude/docs/*` або кореневого документа / опис ADR"). Використовувати `CLAUDE.md` і `{PROJECT_SPEC}` (напр. `PROJECT_PROMPT.md`, `v1-spec.md` тощо) як джерело правил проєкту — не вигадувати факти, які ще не ухвалені.

`{PROJECT_NAME}` підтримує документацію у двох місцях одночасно — на відміну від проєктів з єдиним `.claude/docs/` без кореневих living-документів:

**`.claude/docs/` (internal, living):**
`architecture.md`, `domain-model.md`, `content-model.md`, `api-contracts.md`, `database-schema.md`, `frontend-structure.md`, `fullstack-structure.md`, `seo-map.md`, `integrations.md`, `decisions.md`, `known-issues.md`, `glossary.md`

**Кореневі документи (human-facing, перелік визначає відповідна секція `{PROJECT_SPEC}`):**
`README.md`, `ARCHITECTURE.md`, `DESIGN_SYSTEM.md`, `SEO_STRATEGY.md`, `CONTENT_GUIDE.md`, `DEPLOYMENT.md`, `SECURITY.md`, `TESTING.md`

Workflow:
1. Визначити, який документ(и) з обох списків зачіпає тема — часто одна зміна вимагає оновлення і internal-, і кореневого документа (напр. нове ADR оновлює `decisions.md` і, за потреби, `ARCHITECTURE.md`)
2. Прочитати наявний вміст цільового документа — ніколи не видаляти існуючі рішення, тільки позначати `superseded`
3. Оновити/створити вміст, спираючись на факти з `{PROJECT_SPEC}`, task log чи ADR — кореневий документ переходить від `PENDING (Etap N)` до реального змісту тільки коли рішення справді ухвалене
4. Дати — лише у форматі ISO (`YYYY-MM-DD`); технічні терміни англійською, прозовий текст українською
5. Зберігати документи стислими — посилатись на `{PROJECT_SPEC}`/ADR за розділом, не дублювати їх повністю

Output format:
- Document(s) created/updated (обидві локації, якщо застосовно)
- Summary of content
- Important decisions referenced
- Open questions
- Next suggested document
