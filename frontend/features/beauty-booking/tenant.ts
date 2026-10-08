import { USE_MOCK } from "@/features/beauty-auth/types";

/** Slug tenant-а (§12): 3-64 символи `a-z0-9-`. Невалідний slug не відправляємо на сервер. */
export const SLUG_RE = /^[a-z0-9-]{3,64}$/;

export const isValidSlug = (s: string | null | undefined): s is string => !!s && SLUG_RE.test(s);

/** Slug за замовчуванням: `NEXT_PUBLIC_TENANT_SLUG`; у демо-режимі — "demo". */
export const ENV_TENANT: string = (() => {
  const v = process.env.NEXT_PUBLIC_TENANT_SLUG?.trim().toLowerCase();
  if (isValidSlug(v)) return v;
  return USE_MOCK ? "demo" : "";
})();

/** Slug з маршруту (`/book/[tenant]`, `?tenant=`) має пріоритет над env; `null` — tenant не визначено. */
export function resolveTenant(routeSlug?: string | null, envSlug: string = ENV_TENANT): string | null {
  const r = routeSlug?.trim().toLowerCase();
  if (isValidSlug(r)) return r;
  return isValidSlug(envSlug) ? envSlug : null;
}

/** Посилання на запис: токен у шляху; slug додається в query, лише якщо він не збігається зі slug за замовчуванням. */
export function appointmentHref(tenant: string, token: string, envSlug: string = ENV_TENANT): string {
  const base = `/book/appointment/${encodeURIComponent(token)}`;
  return tenant === envSlug ? base : `${base}?tenant=${encodeURIComponent(tenant)}`;
}

export const bookHref = (tenant: string, envSlug: string = ENV_TENANT) =>
  tenant === envSlug ? "/book" : `/book/${encodeURIComponent(tenant)}`;
