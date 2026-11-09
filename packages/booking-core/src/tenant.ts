/** Slug tenant-а (§12): 3-64 символи `a-z0-9-`. Невалідний slug не відправляємо на сервер. */
export const SLUG_RE = /^[a-z0-9-]{3,64}$/;

export const isValidSlug = (s: string | null | undefined): s is string => !!s && SLUG_RE.test(s);

/**
 * Slug з маршруту/посилання має пріоритет над slug за замовчуванням (`envSlug` передає застосунок);
 * `null` — tenant не визначено.
 */
export function resolveTenant(routeSlug: string | null | undefined, envSlug: string): string | null {
  const r = routeSlug?.trim().toLowerCase();
  if (isValidSlug(r)) return r;
  return isValidSlug(envSlug) ? envSlug : null;
}
