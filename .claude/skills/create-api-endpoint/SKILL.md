---
name: create-api-endpoint
description: >-
  Create API Endpoint — Use when implementing a standalone backend service.
x-generated-from: skills/backend/create-api-endpoint/SKILL.md
---

## Route/Controller Pattern (приклад — Node.js/TypeScript, Express-style; адаптувати синтаксис під свій фреймворк)
```ts
router.get("/products/:id", async (req, res, next) => {
  try {
    const result = await productService.getById(req.params.id);
    if (!result) return res.status(404).json({ error: "Not found" });
    return res.status(200).json(result);
  } catch (err) {
    next(err);
  }
});
```

## Rules
- Жодної бізнес-логіки в route-хендлері/контролері — тільки routing, виклик сервісу, повернення результату
- RESTful naming: `/api/{resource}` (множина), стандартні HTTP-методи/коди
- Явно документувати response-статуси (OpenAPI/Swagger або еквівалент вашого стеку)
- Автентифікація за замовчуванням (middleware-guard/`[Authorize]`) — публічний endpoint є винятком, який треба явно позначити, а не типовим станом
- Timeout/cancellation для довгих операцій, де фреймворк це підтримує
