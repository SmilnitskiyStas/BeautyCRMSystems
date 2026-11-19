/**
 * Idempotency-Key для POST /appointments (§12): 16-128 символів `A-Za-z0-9._:-`, обов'язковий.
 * Той самий ключ + те саме тіло = той самий результат без повторного запису; інше тіло з тим самим ключем = 422.
 * Тому ключ прив'язаний до відбитка тіла: повтор після збою мережі/таймауту використовує ключ, зміна даних - новий.
 *
 * Генератор ключів передає застосунок (web: `crypto.randomUUID`, mobile: `expo-crypto`); core не залежить від платформи.
 */
export function createKeyHolder(generate: () => string) {
  let current: { fingerprint: string; key: string } | null = null;
  return {
    keyFor(body: unknown): string {
      const fingerprint = JSON.stringify(body);
      if (current?.fingerprint !== fingerprint) current = { fingerprint, key: generate() };
      return current.key;
    },
    /** Після успіху наступний запис - новий намір, тож новий ключ. */
    clear() {
      current = null;
    },
  };
}
