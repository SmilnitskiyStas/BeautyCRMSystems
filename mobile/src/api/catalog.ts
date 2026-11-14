import { BookingApiError } from "@beauty/booking-core";
import { z } from "zod";

/**
 * Каталог закладів (ADR-022): міжtenant-ендпоінти, що живуть ТІЛЬКИ в mobile (не в packages/booking-core).
 * КОНТРАКТ (backend паралельно):
 *   GET  /api/public/resolve/{publicCode}            -> PublicLocationRef
 *   POST /api/public/locations/nearby {lat,lng,radiusKm?} -> {items:[PublicLocationRef & {distanceKm}]}
 *   POST /api/public/locations/search {city}          -> {items:[PublicLocationRef]}
 * Відповіді валідуються zod. Координати передаються разово в тілі й ніде не зберігаються/не логуються;
 * перед відправкою округлюються до 3 знаків (~110 м) - точніше для «заклади поблизу» не потрібно (security M2).
 * Пошук за містом (L5): POST з тілом, місто не потрапляє в URL.
 */

/** 3 знаки після коми (~110 м): достатньо для пошуку закладів, не розкриває точну адресу. */
export const roundCoord = (n: number): number => Math.round(n * 1000) / 1000;

export const locationRefSchema = z.object({
  publicCode: z.string().min(1),
  tenantSlug: z.string().min(1),
  locationId: z.string().min(1),
  name: z.string(),
  address: z.string().nullable().optional(),
  city: z.string().nullable().optional(),
  timezone: z.string(),
});
export type LocationRef = z.infer<typeof locationRefSchema>;

export const nearbyItemSchema = locationRefSchema.extend({ distanceKm: z.number() });
export type NearbyItem = z.infer<typeof nearbyItemSchema>;

const itemsOf = <T extends z.ZodTypeAny>(item: T) => z.object({ items: z.array(item) });

export type CatalogFetch = (
  url: string,
  init: { method: "GET" | "POST"; headers: Record<string, string>; body?: string; signal?: AbortSignal },
) => Promise<{ ok: boolean; status: number; statusText: string; headers: { get(n: string): string | null }; json(): Promise<unknown> }>;

export function createCatalogApi(opts: { baseUrl: string; fetch: CatalogFetch; timeoutMs?: number }) {
  const base = opts.baseUrl.replace(/\/+$/, "");

  async function call<S extends z.ZodTypeAny>(path: string, schema: S, init: { method?: "GET" | "POST"; body?: unknown } = {}): Promise<z.infer<S>> {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), opts.timeoutMs ?? 10_000);
    try {
      let res;
      try {
        res = await opts.fetch(`${base}${path}`, {
          method: init.method ?? "GET",
          headers: {
            Accept: "application/json",
            ...(init.body !== undefined ? { "Content-Type": "application/json" } : {}),
          },
          body: init.body === undefined ? undefined : JSON.stringify(init.body),
          signal: ctrl.signal,
        });
      } catch {
        throw new BookingApiError(0, "api_unreachable", "network");
      }
      if (!res.ok) {
        let code = "";
        try {
          const b = (await res.json()) as { code?: unknown };
          if (typeof b.code === "string") code = b.code;
        } catch {
          /* не JSON */
        }
        const retry = Number(res.headers.get("Retry-After"));
        throw new BookingApiError(res.status, code, code || res.statusText, retry > 0 ? retry : undefined);
      }
      const parsed = schema.safeParse(await res.json());
      if (!parsed.success) throw new BookingApiError(502, "invalid_response", "invalid_response");
      return parsed.data;
    } finally {
      clearTimeout(timer);
    }
  }

  return {
    resolve: (publicCode: string) => call(`/api/public/resolve/${encodeURIComponent(publicCode)}`, locationRefSchema),

    async nearby(lat: number, lng: number, radiusKm?: number): Promise<NearbyItem[]> {
      const r = await call("/api/public/locations/nearby", itemsOf(nearbyItemSchema), {
        method: "POST",
        body: { lat: roundCoord(lat), lng: roundCoord(lng), ...(radiusKm ? { radiusKm } : {}) },
      });
      return r.items;
    },

    /** Пошук за містом (L5): POST з тілом {city}, щоб місто не потрапляло в URL і access-логи. */
    async searchByCity(city: string): Promise<LocationRef[]> {
      const r = await call("/api/public/locations/search", itemsOf(locationRefSchema), {
        method: "POST",
        body: { city: city.trim() },
      });
      return r.items;
    },
  };
}
export type CatalogApi = ReturnType<typeof createCatalogApi>;

export const isNotFound = (e: unknown): boolean => e instanceof BookingApiError && e.status === 404;
