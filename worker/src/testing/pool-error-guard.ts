import pg from "pg";

// Тести: при зупинці Testcontainers сервер рве з'єднання (57P01), а pg.Pool без слухача 'error' кидає необроблену помилку
// і валить прогін. Якщо слухача немає — ігноруємо подію (лише у vitest; прод-код не зачіпається).
const origEmit = pg.Pool.prototype.emit;
pg.Pool.prototype.emit = function (this: pg.Pool, event: string | symbol, ...args: unknown[]): boolean {
  if (event === "error" && this.listenerCount("error") === 0) return false;
  return origEmit.call(this, event, ...args);
} as typeof pg.Pool.prototype.emit;
