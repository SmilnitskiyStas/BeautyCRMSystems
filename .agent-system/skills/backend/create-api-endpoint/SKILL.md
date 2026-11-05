---
name: create-api-endpoint
description: >-
  Add a REST/GraphQL endpoint with a thin controller that only routes, calls a service, and returns a typed result. Use when adding or changing a backend endpoint.
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
