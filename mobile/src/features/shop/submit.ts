import { BookingApiError } from "@beauty/booking-core";
import type { CreateProductOrderRequest, ProductOrdersApi, PublicProductOrder } from "@/api/productOrders";
import type { LocalStore } from "@/storage/tokenStore";

/** Ключ очищається лише на остаточну відмову по тілу (валідація/склад/ціна); 429, 426, 403, 404 disabled - ключ зберігається для повтору. */
const FINAL_CODES = new Set(["insufficient_stock", "product_unavailable", "price_changed", "idempotency_key_reused"]);
const isFinalRejection = (e: BookingApiError) => e.status === 422 || FINAL_CODES.has(e.code);

/**
 * Створення замовлення (P2, §33.6/§33.10 C) з ідемпотентністю - за зразком `createBookingSubmitter`:
 *  - ключ прив'язаний до відбитка тіла; повтор після мережевої помилки/5xx з тим самим тілом = той самий ключ,
 *    будь-яка зміна кошика/полів = новий ключ;
 *  - pending-ключ персиститься ДО відправки (переживає перезапуск), очищається після успіху або остаточної відмови (4xx);
 *  - `localId` = ключ; `publicToken` зберігається в SecureStore одразу після 201/200, у індекс не потрапляє.
 */
export function createOrderSubmitter(deps: {
  api: Pick<ProductOrdersApi, "createOrder">;
  store: Pick<LocalStore, "saveOrder" | "getOrderPendingKey" | "setOrderPendingKey" | "clearOrderPendingKey">;
  generateKey: () => string;
  now?: () => Date;
}) {
  const now = () => deps.now?.() ?? new Date();
  let inFlight: Promise<{ localId: string; order: PublicProductOrder }> | null = null;

  async function keyFor(req: CreateProductOrderRequest): Promise<string> {
    const fingerprint = JSON.stringify(req);
    const pending = await deps.store.getOrderPendingKey(now().getTime());
    if (pending && pending.fingerprint === fingerprint) return pending.key;
    const key = deps.generateKey();
    await deps.store.setOrderPendingKey({ fingerprint, key, createdAt: now().getTime() });
    return key;
  }

  return {
    submit(tenantSlug: string, req: CreateProductOrderRequest, locationName: string) {
      if (inFlight) return inFlight;
      inFlight = (async () => {
        try {
          const key = await keyFor(req);
          const { publicToken, order } = await deps.api.createOrder(tenantSlug, req, key);
          await deps.store.saveOrder(
            {
              localId: key,
              tenantSlug,
              locationName: order.location.name || locationName,
              number: order.number,
              total: order.total,
              status: order.status,
              createdAt: now().toISOString(),
            },
            publicToken,
          );
          await deps.store.clearOrderPendingKey();
          return { localId: key, order };
        } catch (e) {
          if (e instanceof BookingApiError && isFinalRejection(e)) await deps.store.clearOrderPendingKey();
          throw e;
        } finally {
          inFlight = null;
        }
      })();
      return inFlight;
    },
  };
}
