/**
 * @beauty/booking-core — framework-free ядро публічного запису (ADR-021 §5).
 * Правила: без React/Next/RN/DOM/`process.env`; конфіг (baseUrl, fetch, генератор ключів, годинник) передає застосунок.
 * Контролюється tsconfig (`lib: ["esnext"]`, `types: []`): будь-яке звернення до `window`/`document`/`process`/`crypto` не компілюється.
 */
export * from "./types";
export * from "./client";
export * from "./http-client";
export * from "./errors";
export * from "./idempotency";
export * from "./dates";
export * from "./tenant";
