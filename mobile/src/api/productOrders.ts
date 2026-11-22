import { BookingApiError } from "@beauty/booking-core";
import { z } from "zod";
import type { CatalogFetch } from "@/api/catalog";
import { httpsUrl } from "@/lib/urls";

/**
 * Замовлення товарів (ADR-026, контракт §33, P1-P5). Публічні маршрути `/api/public/{tenantSlug}`, без автентифікації.
 * `publicToken` - єдиний ключ доступу до замовлення: лише в шляху запиту, ніколи не логується.
 * Невідомі поля відповіді ігноруються (zod object стрипає їх).
 */

const money = z.number();
const locRef = z.object({ id: z.string(), name: z.string(), address: z.string().nullable().optional() });

/** V1.1 (ADR-027, §33.22.3): URL абсолютні, беруться лише з DTO. */

export const productImageSchema = z.object({
  url: httpsUrl,
  thumbUrl: httpsUrl,
  width: z.number().optional(),
  height: z.number().optional(),
});
export type PublicProductImage = z.infer<typeof productImageSchema>;

export const productSchema = z.object({
  id: z.string(),
  name: z.string(),
  unit: z.string(),
  price: money,
  categoryPath: z.string().nullable(),
  inStock: z.boolean(),
  // відсутнє (V1) або null = немає фото
  // невалідний/не-https URL = немає фото, каталог не ламається
  imageUrl: httpsUrl.nullable().optional().catch(null),
  images: z
    .array(productImageSchema.nullable().catch(null))
    .nullable()
    .optional()
    .catch(null)
    .transform((a) => a?.filter((i): i is PublicProductImage => i !== null))
    .optional(),
});
export type PublicProduct = z.infer<typeof productSchema>;

export const productCatalogSchema = z.object({
  location: locRef,
  currency: z.string(),
  reservationMinutes: z.number(),
  maxLines: z.number(),
  maxQuantityPerLine: z.number(),
  items: z.array(productSchema),
  truncated: z.boolean(),
});
export type PublicProductCatalog = z.infer<typeof productCatalogSchema>;

export const ORDER_STATUSES = ["pending_payment", "paid", "fulfilled", "cancelled", "expired", "refunded"] as const;
export type OrderStatus = (typeof ORDER_STATUSES)[number];

export const productOrderSchema = z.object({
  number: z.number(),
  status: z.enum(ORDER_STATUSES),
  location: locRef,
  lines: z.array(
    z.object({ productId: z.string(), name: z.string(), unit: z.string(), quantity: z.number(), unitPrice: money, lineTotal: money }),
  ),
  total: money,
  currency: z.string(),
  createdAt: z.string(),
  reservationExpiresAt: z.string().nullable(),
  paidAt: z.string().nullable(),
  fulfilledAt: z.string().nullable(),
  closedAt: z.string().nullable(),
  closeReason: z.string().nullable(),
  refundAmount: money.nullable(),
  paymentInProgress: z.boolean(),
  paymentAttemptsLeft: z.number(),
  canPay: z.boolean(),
  canCancel: z.boolean(),
});
export type PublicProductOrder = z.infer<typeof productOrderSchema>;

export interface CreateProductOrderRequest {
  locationId: string;
  customer: { name: string; phone: string };
  lines: { productId: string; quantity: number }[];
  expectedTotal?: number;
}

const createdSchema = z.object({ publicToken: z.string().regex(/^[A-Za-z0-9_-]{43}$/), order: productOrderSchema });
const orderEnvelope = z.object({ order: productOrderSchema });
/** P4: `paymentUrl` - сторінка оплати monobank (ADR-028), лише https; відсутнє/невалідне = без переходу (стан визначає `order`). */
const payEnvelope = orderEnvelope.extend({ paymentUrl: httpsUrl.nullable().optional().catch(null) });
export interface PayResponse {
  order: PublicProductOrder;
  /** Куди відкрити клієнта для оплати (рахунок створено, замовлення - «оплата в процесі»); null - оплата вже відпрацювала. */
  paymentUrl: string | null;
}
const errBody = z.object({ order: productOrderSchema.optional(), productIds: z.array(z.string()).optional(), total: z.number().optional() });

/** Extras помилки (`order`, `productIds`, `total`) з `BookingApiError.details`; усе необов'язкове. */
export function errorExtras(e: unknown): { order?: PublicProductOrder; productIds?: string[]; total?: number } {
  if (!(e instanceof BookingApiError)) return {};
  const r = errBody.safeParse(e.details);
  return r.success ? r.data : {};
}

export function createProductOrdersApi(opts: { baseUrl: string; fetch: CatalogFetch; timeoutMs?: number }) {
  const base = opts.baseUrl.replace(/\/+$/, "");

  async function call<S extends z.ZodTypeAny>(
    path: string,
    schema: S,
    init: { method?: "GET" | "POST"; body?: unknown; idempotencyKey?: string; timeoutMs?: number } = {},
  ): Promise<{ data: z.infer<S>; replayed: boolean }> {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), init.timeoutMs ?? opts.timeoutMs ?? 10_000);
    try {
      let res;
      try {
        res = await opts.fetch(`${base}${path}`, {
          method: init.method ?? "GET",
          headers: {
            Accept: "application/json",
            ...(init.body !== undefined ? { "Content-Type": "application/json" } : {}),
            ...(init.idempotencyKey ? { "Idempotency-Key": init.idempotencyKey } : {}),
          },
          body: init.body === undefined ? undefined : JSON.stringify(init.body),
          signal: ctrl.signal,
        });
      } catch {
        throw new BookingApiError(0, "api_unreachable", "network");
      }
      if (!res.ok) {
        let code = "";
        let details: unknown;
        try {
          const b = (await res.json()) as { code?: unknown };
          if (typeof b.code === "string") code = b.code;
          details = b;
        } catch {
          /* не JSON */
        }
        const retry = Number(res.headers.get("Retry-After"));
        throw new BookingApiError(res.status, code, code || res.statusText, retry > 0 ? retry : undefined, details);
      }
      const parsed = schema.safeParse(await res.json());
      if (!parsed.success) throw new BookingApiError(502, "invalid_response", "invalid_response");
      return { data: parsed.data, replayed: res.headers.get("Idempotent-Replayed") === "true" };
    } finally {
      clearTimeout(timer);
    }
  }

  const slug = (s: string) => `/api/public/${encodeURIComponent(s)}`;
  const orderPath = (s: string, token: string) => `${slug(s)}/product-orders/${encodeURIComponent(token)}`;

  return {
    async getCatalog(tenantSlug: string, locationId: string): Promise<PublicProductCatalog> {
      return (await call(`${slug(tenantSlug)}/locations/${encodeURIComponent(locationId)}/products`, productCatalogSchema)).data;
    },

    async createOrder(tenantSlug: string, req: CreateProductOrderRequest, idempotencyKey: string) {
      const r = await call(`${slug(tenantSlug)}/product-orders`, createdSchema, { method: "POST", body: req, idempotencyKey });
      return { publicToken: r.data.publicToken, order: r.data.order, replayed: r.replayed };
    },

    async getOrder(tenantSlug: string, token: string): Promise<PublicProductOrder> {
      return (await call(orderPath(tenantSlug, token), productOrderSchema)).data;
    },

    /** P4: тайм-аут 30 с (§33.10 D). Мережеву помилку не повторювати наосліп - спершу `getOrder`. */
    async pay(tenantSlug: string, token: string): Promise<PayResponse> {
      const d = (await call(`${orderPath(tenantSlug, token)}/pay`, payEnvelope, { method: "POST", body: {}, timeoutMs: 30_000 })).data;
      return { order: d.order, paymentUrl: d.paymentUrl ?? null };
    },

    async cancel(tenantSlug: string, token: string): Promise<PublicProductOrder> {
      return (await call(`${orderPath(tenantSlug, token)}/cancel`, orderEnvelope, { method: "POST", body: {} })).data.order;
    },
  };
}
export type ProductOrdersApi = ReturnType<typeof createProductOrdersApi>;
