/**
 * Типи складу (§32.6, ADR-025). Поля з позначкою † (ціни) відсутні в JSON без `inventory.costs >= view`:
 * у типах вони необов'язкові, UI показує їх лише коли поле є.
 */

export type StockUnit = "pcs" | "ml" | "g" | "l" | "kg" | "pack";
export type ProductPurpose = "sale" | "salon_use" | "both";
export type BatchStatus = "active" | "expiring" | "expired" | "empty";
export type StockState = "all" | "low" | "in_stock" | "out_of_stock";
export type MovementType = "receipt" | "write_off" | "transfer_out" | "transfer_in" | "stocktake_adjust" | "service_consumption" | "sale";
export type OperationKind = "receipt" | "write_off" | "transfer" | "stocktake" | "service_consumption" | "sale";
export type WriteOffReason = "expired" | "damaged" | "lost" | "internal_use" | "correction" | "other";
export type StocktakeStatus = "draft" | "posted" | "cancelled";
export type CostsAccess = "none" | "view" | "edit";

export interface NamedRef {
  id: string;
  name: string;
}

export interface InventoryScopeLocation {
  id: string;
  name: string;
  isActive: boolean;
  timezone: string;
  canManage: boolean;
}
export interface InventoryScope {
  locations: InventoryScopeLocation[];
  costs: CostsAccess;
  canEditCatalog: boolean;
  expiringSoonDays: number;
}

export interface ProductRef {
  id: string;
  name: string;
  unit: StockUnit;
  purpose: ProductPurpose;
  category: string | null;
  barcode: string | null;
  isActive: boolean;
}

export interface Product {
  id: string;
  name: string;
  category: string | null;
  unit: StockUnit;
  barcode: string | null;
  purpose: ProductPurpose;
  supplier: NamedRef | null;
  purchasePrice?: number | null;
  minStock: number | null;
  trackExpiry: boolean;
  isActive: boolean;
  note: string | null;
  hasMovements: boolean;
  createdAt: string;
  updatedAt: string;
}
export interface LocationSetting {
  locationId: string;
  minStock: number | null;
  isEnabled: boolean;
  updatedAt: string;
}
export interface ProductDetail extends Product {
  locationSettings: LocationSetting[];
}
export interface ProductRequest {
  name: string;
  category?: string | null;
  unit: StockUnit;
  barcode?: string | null;
  purpose: ProductPurpose;
  supplierId?: string | null;
  /** Лише за `inventory.costs = edit`; без права поле не передається взагалі. */
  purchasePrice?: number | null;
  minStock?: number | null;
  trackExpiry?: boolean;
  isActive?: boolean;
  note?: string | null;
}

export interface Supplier {
  id: string;
  name: string;
  contactName: string | null;
  phone: string | null;
  email: string | null;
  note: string | null;
  isActive: boolean;
  productCount: number;
}
export interface SupplierRequest {
  name: string;
  contactName?: string | null;
  phone?: string | null;
  email?: string | null;
  note?: string | null;
  isActive?: boolean;
}

export interface StockRow {
  product: ProductRef;
  locationId: string;
  quantity: number;
  /** Ефективний мінімальний залишок (заклад або мережа). */
  minStock: number | null;
  isLow: boolean;
  isEnabled: boolean;
  nearestExpiresOn: string | null;
  expiredQuantity: number;
  expiringQuantity: number;
  value?: number;
  valueIncomplete?: boolean;
}
export interface StockSummaryRow {
  product: ProductRef;
  totalQuantity: number;
  isLowAnywhere: boolean;
  locations: { locationId: string; quantity: number; isLow: boolean }[];
  value?: number;
  valueIncomplete?: boolean;
}
export interface Batch {
  id: string;
  product: ProductRef;
  locationId: string;
  receivedAt: string;
  expiresOn: string | null;
  lotNumber: string | null;
  initialQuantity: number;
  quantity: number;
  unitCost?: number | null;
  supplier: NamedRef | null;
  operationId: string;
  sourceBatchId: string | null;
  status: BatchStatus;
  daysToExpiry: number | null;
}
export interface Movement {
  id: string;
  operationId: string;
  operationKind: OperationKind;
  type: MovementType;
  occurredAt: string;
  locationId: string;
  product: ProductRef;
  batchId: string | null;
  quantity: number;
  unitCost?: number | null;
  reason: WriteOffReason | null;
  note: string | null;
  counterpartLocationId: string | null;
  stocktakeId: string | null;
  createdBy: NamedRef | null;
}
export interface OperationSummary {
  id: string;
  kind: OperationKind;
  locationId: string;
  toLocationId: string | null;
  reason: WriteOffReason | null;
  documentNumber: string | null;
  createdAt: string;
  createdBy: NamedRef | null;
  productCount: number;
  totalCost?: number | null;
}
export interface Operation extends OperationSummary {
  supplier: NamedRef | null;
  receivedOn: string | null;
  note: string | null;
  stocktakeId: string | null;
  movements: Movement[];
}

export interface ReceiptLine {
  productId: string;
  quantity: number;
  /** Лише за `inventory.costs = edit`. */
  unitCost?: number;
  expiresOn?: string;
  lotNumber?: string;
}
export interface ReceiptRequest {
  locationId: string;
  supplierId?: string;
  documentNumber?: string;
  receivedOn?: string;
  note?: string;
  lines: ReceiptLine[];
}
export interface StockLineRequest {
  productId: string;
  quantity: number;
  batchId?: string;
}
export interface WriteOffRequest {
  locationId: string;
  reason: WriteOffReason;
  note?: string;
  lines: StockLineRequest[];
}
export interface TransferRequest {
  fromLocationId: string;
  toLocationId: string;
  note?: string;
  lines: StockLineRequest[];
}

export interface StocktakeSummary {
  id: string;
  locationId: string;
  status: StocktakeStatus;
  createdAt: string;
  createdBy: NamedRef;
  postedAt: string | null;
  lineCount: number;
  countedCount: number;
}
export interface StocktakeLine {
  product: ProductRef;
  expectedQuantity: number | null;
  countedQuantity: number | null;
  difference: number | null;
  surplusExpiresOn: string | null;
}
export interface Stocktake extends StocktakeSummary {
  note: string | null;
  version: number;
  postedBy: NamedRef | null;
  cancelledAt: string | null;
  lines: StocktakeLine[];
}
export interface StocktakeLineInput {
  productId: string;
  countedQuantity: number | null;
  surplusExpiresOn?: string | null;
}
export interface StocktakePostResult {
  stocktake: Stocktake;
  operation: Operation | null;
}

export interface Page<T> {
  items: T[];
  hasMore: boolean;
  nextCursor?: string;
}

export interface ProductQuery {
  search?: string;
  category?: string;
  purpose?: ProductPurpose;
  supplierId?: string;
  active?: "true" | "false" | "all";
  cursor?: string;
  take?: number;
}
export interface StockQuery {
  locationId: string;
  search?: string;
  category?: string;
  purpose?: ProductPurpose;
  state?: StockState;
  expiringWithinDays?: number;
  cursor?: string;
  take?: number;
}
export interface StockSummaryQuery {
  search?: string;
  category?: string;
  purpose?: ProductPurpose;
  lowOnly?: boolean;
  cursor?: string;
  take?: number;
}
export interface StockPage extends Page<StockRow> {
  location: { id: string; name: string; timezone: string; isActive: boolean };
}
export interface StockSummaryPage extends Page<StockSummaryRow> {
  locations: { id: string; name: string; isActive: boolean }[];
}
export interface BatchQuery {
  locationId?: string;
  productId?: string;
  status?: BatchStatus | "all";
  expiringWithinDays?: number;
  cursor?: string;
  take?: number;
}
export interface MovementQuery {
  locationId?: string;
  productId?: string;
  type?: MovementType;
  operationId?: string;
  from?: string;
  to?: string;
  cursor?: string;
  take?: number;
}
export interface OperationQuery {
  kind?: OperationKind;
  locationId?: string;
  from?: string;
  to?: string;
  cursor?: string;
  take?: number;
}
export interface StocktakeQuery {
  locationId?: string;
  status?: StocktakeStatus;
  cursor?: string;
  take?: number;
}
export interface SupplierQuery {
  search?: string;
  active?: "true" | "false" | "all";
  cursor?: string;
  take?: number;
}
