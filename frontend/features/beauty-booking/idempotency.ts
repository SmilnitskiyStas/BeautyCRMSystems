import { createKeyHolder as createCoreKeyHolder } from "@beauty/booking-core";

/** Idempotency-Key для web: `crypto.randomUUID()` (генератор передається в core ззовні). */
export const newIdempotencyKey = (): string => crypto.randomUUID();

export const createKeyHolder = (generate: () => string = newIdempotencyKey) => createCoreKeyHolder(generate);
