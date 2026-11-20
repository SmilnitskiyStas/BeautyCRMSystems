import { apiJson } from "@/features/beauty-auth/session";
import type { BeautyAdminApi } from "./client";
import type {
  AlertKind,
  AlertStatus,
  LocationMaterialSet,
  MaterialSet,
  ServiceMaterials,
  StaffAlert,
  Batch,
  BatchStatus,
  CostsAccess,
  InventoryScope,
  LocationSetting,
  Movement,
  MovementType,
  NamedRef,
  Operation,
  OperationKind,
  OperationSummary,
  Page,
  Product,
  ProductPurpose,
  ProductRef,
  StockRow,
  StockSummaryRow,
  StockUnit,
  Stocktake,
  StocktakeLine,
  StocktakeStatus,
  StocktakeSummary,
  Supplier,
  WriteOffReason,
} from "../inventory/types";
import type { RevokeReason, TelegramLinkIssued, TelegramLinkState, TelegramLinkStatus } from "../inventory/telegram-link";

/**
 * HTTP-клієнт §32 (ADR-025). Явна вибірка полів; ціни (†) копіюються лише коли сервер їх віддав (без `inventory.costs`
 * поле відсутнє, і ми не підставляємо `0`). Усі відповіді `cache: "no-store"`. Мутації операцій шлють `Idempotency-Key`.
 */

const B = "/api/beauty/inventory";
const NO_STORE = { cache: "no-store" } as const;
type Raw = Record<string, unknown>;

const obj = (v: unknown): Raw => (v && typeof v === "object" ? (v as Raw) : {});
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);
const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const num0 = (v: unknown): number => num(v) ?? 0;
const list = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);
const oneOf = <T extends string>(v: unknown, all: readonly T[], fallback: T): T => (typeof v === "string" && (all as readonly string[]).includes(v) ? (v as T) : fallback);
/** Поле лише коли воно є в JSON (ціни †). */
const optNum = <K extends string>(o: Raw, key: K): Partial<Record<K, number | null>> => (key in o ? ({ [key]: num(o[key]) } as Record<K, number | null>) : {});

const UNITS = ["pcs", "ml", "g", "l", "kg", "pack"] as const satisfies readonly StockUnit[];
const PURPOSES = ["sale", "salon_use", "both"] as const satisfies readonly ProductPurpose[];
const BATCH_STATUSES = ["active", "expiring", "expired", "empty"] as const satisfies readonly BatchStatus[];
const MOVEMENT_TYPES = ["receipt", "write_off", "transfer_out", "transfer_in", "stocktake_adjust", "service_consumption", "sale"] as const satisfies readonly MovementType[];
const KINDS = ["receipt", "write_off", "transfer", "stocktake", "service_consumption", "sale"] as const satisfies readonly OperationKind[];
const REASONS = ["expired", "damaged", "lost", "internal_use", "correction", "other"] as const satisfies readonly WriteOffReason[];
const STOCKTAKE_STATUSES = ["draft", "posted", "cancelled"] as const satisfies readonly StocktakeStatus[];
const ALERT_KINDS = ["low_stock", "expiring", "shortage"] as const satisfies readonly AlertKind[];
const ALERT_STATUSES = ["open", "read", "resolved"] as const satisfies readonly AlertStatus[];
const TG_STATES = ["none", "pending", "awaitingConfirmation", "active"] as const satisfies readonly TelegramLinkState[];
const TG_REVOKES = ["user_unlink", "bot_stop", "declined", "replaced", "expired", "bot_blocked", "chat_not_found", "user_deactivated", "role_changed", "tenant_inactive"] as const satisfies readonly RevokeReason[];
const COSTS = ["none", "view", "edit"] as const satisfies readonly CostsAccess[];

function ref(v: unknown): NamedRef | null {
  const o = obj(v);
  const id = str(o.id);
  return id ? { id, name: str(o.name) ?? "" } : null;
}

export function toProductRef(v: unknown): ProductRef {
  const o = obj(v);
  return {
    id: str(o.id) ?? "",
    name: str(o.name) ?? "",
    unit: oneOf<StockUnit>(o.unit, UNITS, "pcs"),
    purpose: oneOf<ProductPurpose>(o.purpose, PURPOSES, "both"),
    category: str(o.category),
    barcode: str(o.barcode),
    isActive: o.isActive !== false,
  };
}

export function toProduct(v: unknown): Product {
  const o = obj(v);
  return {
    id: str(o.id) ?? "",
    name: str(o.name) ?? "",
    category: str(o.category),
    unit: oneOf<StockUnit>(o.unit, UNITS, "pcs"),
    barcode: str(o.barcode),
    purpose: oneOf<ProductPurpose>(o.purpose, PURPOSES, "both"),
    supplier: ref(o.supplier),
    ...optNum(o, "purchasePrice"),
    salePrice: num(o.salePrice),
    minStock: num(o.minStock),
    trackExpiry: o.trackExpiry === true,
    isActive: o.isActive !== false,
    note: str(o.note),
    hasMovements: o.hasMovements === true,
    createdAt: str(o.createdAt) ?? "",
    updatedAt: str(o.updatedAt) ?? "",
  };
}

function toSetting(v: unknown): LocationSetting {
  const o = obj(v);
  return { locationId: str(o.locationId) ?? "", minStock: num(o.minStock), isEnabled: o.isEnabled !== false, updatedAt: str(o.updatedAt) ?? "" };
}

function toSupplier(v: unknown): Supplier {
  const o = obj(v);
  return {
    id: str(o.id) ?? "",
    name: str(o.name) ?? "",
    contactName: str(o.contactName),
    phone: str(o.phone),
    email: str(o.email),
    note: str(o.note),
    isActive: o.isActive !== false,
    productCount: num0(o.productCount),
  };
}

function toStockRow(v: unknown): StockRow {
  const o = obj(v);
  return {
    product: toProductRef(o.product),
    locationId: str(o.locationId) ?? "",
    quantity: num0(o.quantity),
    minStock: num(o.minStock),
    isLow: o.isLow === true,
    isEnabled: o.isEnabled !== false,
    nearestExpiresOn: str(o.nearestExpiresOn),
    expiredQuantity: num0(o.expiredQuantity),
    expiringQuantity: num0(o.expiringQuantity),
    shortageQuantity: num0(o.shortageQuantity),
    // Старий сервер без поля: сальдо = залишок.
    balance: num(o.balance) ?? num0(o.quantity) - num0(o.shortageQuantity),
    ...("value" in o ? { value: num0(o.value), valueIncomplete: o.valueIncomplete === true } : {}),
  };
}

function toSummaryRow(v: unknown): StockSummaryRow {
  const o = obj(v);
  return {
    product: toProductRef(o.product),
    totalQuantity: num0(o.totalQuantity),
    totalShortage: num0(o.totalShortage),
    isLowAnywhere: o.isLowAnywhere === true,
    locations: list(o.locations).map((l) => {
      const x = obj(l);
      return {
        locationId: str(x.locationId) ?? "",
        quantity: num0(x.quantity),
        shortageQuantity: num0(x.shortageQuantity),
        balance: num(x.balance) ?? num0(x.quantity) - num0(x.shortageQuantity),
        isLow: x.isLow === true,
      };
    }),
    ...("value" in o ? { value: num0(o.value), valueIncomplete: o.valueIncomplete === true } : {}),
  };
}

function toBatch(v: unknown): Batch {
  const o = obj(v);
  return {
    id: str(o.id) ?? "",
    product: toProductRef(o.product),
    locationId: str(o.locationId) ?? "",
    receivedAt: str(o.receivedAt) ?? "",
    expiresOn: str(o.expiresOn),
    lotNumber: str(o.lotNumber),
    initialQuantity: num0(o.initialQuantity),
    quantity: num0(o.quantity),
    ...optNum(o, "unitCost"),
    supplier: ref(o.supplier),
    operationId: str(o.operationId) ?? "",
    sourceBatchId: str(o.sourceBatchId),
    status: oneOf<BatchStatus>(o.status, BATCH_STATUSES, "active"),
    daysToExpiry: num(o.daysToExpiry),
  };
}

function toMovement(v: unknown): Movement {
  const o = obj(v);
  return {
    id: str(o.id) ?? "",
    operationId: str(o.operationId) ?? "",
    operationKind: oneOf<OperationKind>(o.operationKind, KINDS, "receipt"),
    type: oneOf<MovementType>(o.type, MOVEMENT_TYPES, "receipt"),
    occurredAt: str(o.occurredAt) ?? "",
    locationId: str(o.locationId) ?? "",
    product: toProductRef(o.product),
    batchId: str(o.batchId),
    quantity: num0(o.quantity),
    isShortage: o.isShortage === true,
    ...optNum(o, "unitCost"),
    reason: typeof o.reason === "string" && (REASONS as readonly string[]).includes(o.reason) ? (o.reason as WriteOffReason) : null,
    note: str(o.note),
    counterpartLocationId: str(o.counterpartLocationId),
    stocktakeId: str(o.stocktakeId),
    createdBy: ref(o.createdBy),
  };
}

function toOperationSummary(v: unknown): OperationSummary {
  const o = obj(v);
  return {
    id: str(o.id) ?? "",
    kind: oneOf<OperationKind>(o.kind, KINDS, "receipt"),
    locationId: str(o.locationId) ?? "",
    toLocationId: str(o.toLocationId),
    reason: typeof o.reason === "string" && (REASONS as readonly string[]).includes(o.reason) ? (o.reason as WriteOffReason) : null,
    documentNumber: str(o.documentNumber),
    createdAt: str(o.createdAt) ?? "",
    createdBy: ref(o.createdBy),
    productCount: num0(o.productCount),
    appointmentId: str(o.appointmentId),
    ...optNum(o, "totalCost"),
  };
}

function toOperation(v: unknown): Operation {
  const o = obj(v);
  return {
    ...toOperationSummary(v),
    supplier: ref(o.supplier),
    receivedOn: str(o.receivedOn),
    note: str(o.note),
    stocktakeId: str(o.stocktakeId),
    movements: list(o.movements).map(toMovement),
  };
}

function toStocktakeSummary(v: unknown): StocktakeSummary {
  const o = obj(v);
  return {
    id: str(o.id) ?? "",
    locationId: str(o.locationId) ?? "",
    status: oneOf<StocktakeStatus>(o.status, STOCKTAKE_STATUSES, "draft"),
    createdAt: str(o.createdAt) ?? "",
    createdBy: ref(o.createdBy) ?? { id: "", name: "" },
    postedAt: str(o.postedAt),
    lineCount: num0(o.lineCount),
    countedCount: num0(o.countedCount),
  };
}

export function toStocktake(v: unknown): Stocktake {
  const o = obj(v);
  return {
    ...toStocktakeSummary(v),
    note: str(o.note),
    version: num(o.version) ?? 1,
    postedBy: ref(o.postedBy),
    cancelledAt: str(o.cancelledAt),
    lines: list(o.lines).map((l): StocktakeLine => {
      const x = obj(l);
      return {
        product: toProductRef(x.product),
        expectedQuantity: num(x.expectedQuantity),
        expectedShortage: num(x.expectedShortage),
        countedQuantity: num(x.countedQuantity),
        difference: num(x.difference),
        surplusExpiresOn: str(x.surplusExpiresOn),
      };
    }),
  };
}

function toScope(v: unknown): InventoryScope {
  const o = obj(v);
  return {
    locations: list(o.locations).map((l) => {
      const x = obj(l);
      return { id: str(x.id) ?? "", name: str(x.name) ?? "", isActive: x.isActive !== false, timezone: str(x.timezone) ?? "UTC", canManage: x.canManage === true };
    }),
    costs: oneOf<CostsAccess>(o.costs, COSTS, "none"),
    canEditCatalog: o.canEditCatalog === true,
    expiringSoonDays: num(o.expiringSoonDays) ?? 30,
  };
}

function toMaterialSet(v: unknown): MaterialSet {
  const o = obj(v);
  return {
    serviceId: str(o.serviceId) ?? "",
    locationId: str(o.locationId),
    lines: list(o.lines).map((l) => {
      const x = obj(l);
      return { product: toProductRef(x.product), quantity: num0(x.quantity) };
    }),
    updatedAt: str(o.updatedAt) ?? "",
    updatedBy: ref(o.updatedBy),
  };
}

export function toServiceMaterials(v: unknown): ServiceMaterials {
  const o = obj(v);
  return {
    serviceId: str(o.serviceId) ?? "",
    serviceName: str(o.serviceName) ?? "",
    serviceIsActive: o.serviceIsActive !== false,
    network: o.network ? toMaterialSet(o.network) : null,
    locations: list(o.locations).map((l): LocationMaterialSet => ({ ...toMaterialSet(l), locationName: str(obj(l).locationName) ?? "" })),
  };
}

/** Лише поля контракту §32.19: ПД клієнтів сюди не потрапляють навіть якщо сервер їх помилково віддасть. */
export function toStaffAlert(v: unknown): StaffAlert {
  const o = obj(v);
  const b = obj(o.batch);
  return {
    id: str(o.id) ?? "",
    kind: oneOf<AlertKind>(o.kind, ALERT_KINDS, "low_stock"),
    status: oneOf<AlertStatus>(o.status, ALERT_STATUSES, "open"),
    location: ref(o.location) ?? { id: "", name: "" },
    product: toProductRef(o.product),
    batch: str(b.id) ? { id: str(b.id)!, expiresOn: str(b.expiresOn), lotNumber: str(b.lotNumber) } : null,
    operationId: str(o.operationId),
    quantity: num0(o.quantity),
    threshold: num(o.threshold),
    expiresOn: str(o.expiresOn),
    daysBucket: num(o.daysBucket),
    createdAt: str(o.createdAt) ?? "",
    readAt: str(o.readAt),
    readBy: ref(o.readBy),
    resolvedAt: str(o.resolvedAt),
  };
}

function toPage<T>(raw: unknown, map: (v: unknown) => T): Page<T> {
  const o = obj(raw);
  const next = str(o.nextCursor);
  return { items: list(o.items).map(map), hasMore: o.hasMore === true, ...(next ? { nextCursor: next } : {}) };
}

const enc = encodeURIComponent;
type Params = Record<string, string | number | boolean | undefined>;
function qs(p: Params): string {
  const u = new URLSearchParams();
  for (const [k, v] of Object.entries(p)) if (v !== undefined && v !== "") u.set(k, String(v));
  const s = u.toString();
  return s ? `?${s}` : "";
}
const take = (n?: number) => (n === undefined ? undefined : Math.min(200, Math.max(1, n)));
const get = <T>(path: string, p: Params = {}) => apiJson<T>(`${B}${path}${qs(p)}`, NO_STORE);
const send = <T>(method: "POST" | "PUT", path: string, body?: unknown, headers?: Record<string, string>) =>
  apiJson<T>(`${B}${path}`, { method, ...(headers ? { headers } : {}), body: body === undefined ? undefined : JSON.stringify(body), cache: "no-store" });
const idem = (key: string) => ({ "Idempotency-Key": key });

type InventoryApi = Pick<
  BeautyAdminApi,
  | "getInventoryScope"
  | "getInventoryProducts"
  | "getInventoryProduct"
  | "getInventoryCategories"
  | "createInventoryProduct"
  | "updateInventoryProduct"
  | "setProductLocationSetting"
  | "deleteProductLocationSetting"
  | "getSuppliers"
  | "createSupplier"
  | "updateSupplier"
  | "getStock"
  | "getStockSummary"
  | "getBatches"
  | "getMovements"
  | "getOperations"
  | "getOperation"
  | "createReceipt"
  | "createWriteOff"
  | "createTransfer"
  | "getStocktakes"
  | "createStocktake"
  | "getStocktake"
  | "saveStocktakeLines"
  | "postStocktake"
  | "cancelStocktake"
  | "getServiceMaterialsList"
  | "getServiceMaterials"
  | "setServiceMaterials"
  | "deleteServiceMaterials"
  | "getInventoryAlerts"
  | "getInventoryAlertsUnreadCount"
  | "markInventoryAlertRead"
  | "markAllInventoryAlertsRead"
  | "getTelegramLink"
  | "createTelegramLink"
  | "deleteTelegramLink"
>;

export function toTelegramLinkStatus(v: unknown): TelegramLinkStatus {
  const o = obj(v);
  const rev = obj(o.lastRevoke);
  const reason = str(rev.reason);
  const at = str(rev.at);
  return {
    featureEnabled: o.featureEnabled === true,
    state: oneOf<TelegramLinkState>(o.state, TG_STATES, "none"),
    channel: ref(o.channel),
    expiresAt: str(o.expiresAt),
    activatedAt: str(o.activatedAt),
    claimedAt: str(o.claimedAt),
    lastDeliveryAt: str(o.lastDeliveryAt),
    lastRevoke: reason && at && (TG_REVOKES as readonly string[]).includes(reason) ? { reason: reason as RevokeReason, at } : null,
    consentVersion: str(o.consentVersion) ?? "inv-tg-v1",
  };
}

export const httpInventoryApi: InventoryApi = {
  getInventoryScope: async () => toScope(await get("/scope")),
  getInventoryProducts: async (q = {}) => toPage(await get("/products", { ...q, take: take(q.take) }), toProduct),
  getInventoryProduct: async (id) => {
    const raw = await get<unknown>(`/products/${enc(id)}`);
    return { ...toProduct(raw), locationSettings: list(obj(raw).locationSettings).map(toSetting) };
  },
  getInventoryCategories: async () => list(obj(await get("/categories")).items).filter((x): x is string => typeof x === "string"),
  createInventoryProduct: async (r) => toProduct(await send("POST", "/products", r)),
  updateInventoryProduct: async (id, r) => toProduct(await send("PUT", `/products/${enc(id)}`, r)),
  setProductLocationSetting: async (productId, locationId, input) => toSetting(await send("PUT", `/products/${enc(productId)}/locations/${enc(locationId)}`, input)),
  deleteProductLocationSetting: async (productId, locationId) => {
    await apiJson<void>(`${B}/products/${enc(productId)}/locations/${enc(locationId)}`, { method: "DELETE", cache: "no-store" });
  },
  getSuppliers: async (q = {}) => toPage(await get("/suppliers", { ...q, take: take(q.take) }), toSupplier),
  createSupplier: async (r) => toSupplier(await send("POST", "/suppliers", r)),
  updateSupplier: async (id, r) => toSupplier(await send("PUT", `/suppliers/${enc(id)}`, r)),
  getStock: async (q) => {
    const raw = await get<unknown>("/stock", { ...q, take: take(q.take) });
    const l = obj(obj(raw).location);
    return {
      ...toPage(raw, toStockRow),
      location: { id: str(l.id) ?? q.locationId, name: str(l.name) ?? "", timezone: str(l.timezone) ?? "UTC", isActive: l.isActive !== false },
    };
  },
  getStockSummary: async (q = {}) => {
    const raw = await get<unknown>("/stock/summary", { ...q, take: take(q.take) });
    return {
      ...toPage(raw, toSummaryRow),
      locations: list(obj(raw).locations).map((l) => {
        const x = obj(l);
        return { id: str(x.id) ?? "", name: str(x.name) ?? "", isActive: x.isActive !== false };
      }),
    };
  },
  getBatches: async (q = {}) => toPage(await get("/batches", { ...q, take: take(q.take) }), toBatch),
  getMovements: async (q = {}) => toPage(await get("/movements", { ...q, take: take(q.take) }), toMovement),
  getOperations: async (q = {}) => toPage(await get("/operations", { ...q, take: take(q.take) }), toOperationSummary),
  getOperation: async (id) => toOperation(await get(`/operations/${enc(id)}`)),
  createReceipt: async (r, key) => toOperation(await send("POST", "/receipts", r, idem(key))),
  createWriteOff: async (r, key) => toOperation(await send("POST", "/write-offs", r, idem(key))),
  createTransfer: async (r, key) => toOperation(await send("POST", "/transfers", r, idem(key))),
  getStocktakes: async (q = {}) => toPage(await get("/stocktakes", { ...q, take: take(q.take) }), toStocktakeSummary),
  createStocktake: async (input) => toStocktake(await send("POST", "/stocktakes", input)),
  getStocktake: async (id) => toStocktake(await get(`/stocktakes/${enc(id)}`)),
  saveStocktakeLines: async (id, expectedVersion, lines) => toStocktake(await send("PUT", `/stocktakes/${enc(id)}/lines`, { expectedVersion, lines })),
  postStocktake: async (id, expectedVersion) => {
    const o = obj(await send("POST", `/stocktakes/${enc(id)}/post`, { expectedVersion }));
    return { stocktake: toStocktake(o.stocktake), operation: o.operation ? toOperation(o.operation) : null };
  },
  cancelStocktake: async (id, expectedVersion) => toStocktake(await send("POST", `/stocktakes/${enc(id)}/cancel`, { expectedVersion })),

  getServiceMaterialsList: async (includeInactive = false) => list(obj(await get("/service-materials", { includeInactive })).items).map(toServiceMaterials),
  getServiceMaterials: async (serviceId) => toServiceMaterials(await get(`/services/${enc(serviceId)}/materials`)),
  setServiceMaterials: async (serviceId, r) => toMaterialSet(await send("PUT", `/services/${enc(serviceId)}/materials`, r)),
  deleteServiceMaterials: async (serviceId, locationId) => {
    await apiJson<void>(`${B}/services/${enc(serviceId)}/materials${qs({ locationId: locationId ?? undefined })}`, { method: "DELETE", cache: "no-store" });
  },
  getInventoryAlerts: async (q = {}) => toPage(await get("/alerts", { ...q, take: take(q.take) }), toStaffAlert),
  getInventoryAlertsUnreadCount: async () => Math.max(0, num0(obj(await get("/alerts/unread-count")).count)),
  markInventoryAlertRead: async (id) => toStaffAlert(await send("POST", `/alerts/${enc(id)}/read`)),
  markAllInventoryAlertsRead: async (locationId) => num0(obj(await send("POST", `/alerts/read-all${qs({ locationId })}`)).updated),

  getTelegramLink: async () => toTelegramLinkStatus(await get("/telegram/link")),
  createTelegramLink: async (r) => {
    const o = obj(await send("POST", "/telegram/link", r));
    return { linkId: str(o.linkId) ?? "", deepLink: str(o.deepLink) ?? "", expiresAt: str(o.expiresAt) ?? "" } satisfies TelegramLinkIssued;
  },
  deleteTelegramLink: async () => {
    await apiJson<void>(`${B}/telegram/link`, { method: "DELETE", cache: "no-store" });
  },
};
