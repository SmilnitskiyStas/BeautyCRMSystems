import { USE_MOCK } from "@/features/beauty-auth/types";
import { isValidSlug, resolveTenant as resolveTenantCore } from "@beauty/booking-core";

export { SLUG_RE, isValidSlug } from "@beauty/booking-core";

/** Slug за замовчуванням: `NEXT_PUBLIC_TENANT_SLUG`; у демо-режимі — "demo". */
export const ENV_TENANT: string = (() => {
  const v = process.env.NEXT_PUBLIC_TENANT_SLUG?.trim().toLowerCase();
  if (isValidSlug(v)) return v;
  return USE_MOCK ? "demo" : "";
})();

/** Slug з маршруту (`/book/[tenant]`, `?tenant=`) має пріоритет над env; `null` — tenant не визначено. */
export const resolveTenant = (routeSlug?: string | null, envSlug: string = ENV_TENANT): string | null =>
  resolveTenantCore(routeSlug, envSlug);

/** Посилання на запис: токен у шляху; slug додається в query, лише якщо він не збігається зі slug за замовчуванням. */
export function appointmentHref(tenant: string, token: string, envSlug: string = ENV_TENANT): string {
  const base = `/book/appointment/${encodeURIComponent(token)}`;
  return tenant === envSlug ? base : `${base}?tenant=${encodeURIComponent(tenant)}`;
}

export const bookHref = (tenant: string, envSlug: string = ENV_TENANT) =>
  tenant === envSlug ? "/book" : `/book/${encodeURIComponent(tenant)}`;
