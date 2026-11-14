/** 426 upgrade_required (ADR-022 §3): сервер більше не підтримує цю версію застосунку. */
export const UPGRADE_STATUS = 426;

/**
 * Обгортка fetch: відповідь 426 викликає `onUpgrade` (глобальний екран «Оновіть застосунок»), відповідь передається далі
 * без змін (виклик також отримає помилку).
 */
export function withUpgradeGuard<A extends unknown[], R extends { status: number }>(
  doFetch: (...args: A) => Promise<R>,
  onUpgrade: () => void,
): (...args: A) => Promise<R> {
  return async (...args) => {
    const res = await doFetch(...args);
    if (res.status === UPGRADE_STATUS) onUpgrade();
    return res;
  };
}
