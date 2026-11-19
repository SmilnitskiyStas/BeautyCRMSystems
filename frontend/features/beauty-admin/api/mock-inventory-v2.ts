import { BeautyApiError } from "@/features/beauty-auth/errors";
import { daysBucketFor, isIntegerUnit, toMilli } from "../inventory/logic";
import type {
  AlertQuery,
  LocationMaterialSet,
  MaterialSet,
  MaterialSetRequest,
  NamedRef,
  Page,
  ProductRef,
  ServiceMaterials,
  StaffAlert,
} from "../inventory/types";
import type { BeautyLocation } from "../types";
import { peekInventory } from "./mock-inventory";

/**
 * Mock етапу 2a (§32.16, §32.19): норми матеріалів послуг (мережа + override закладу) і сповіщення персоналу.
 * Правила контракту: override замінює весь набір; порожній override дозволено; товар лише `salon_use|both`, активний;
 * зміна лише для owner/admin (deputy/specialist -> `403 forbidden_role`); сповіщення без ПД клієнтів, `read` ідемпотентний,
 * поза scope -> `404 alert_not_found`. Сповіщення будуються один раз зі стану складу (те, що знайшов би воркер).
 */

export interface MaterialsMockDeps {
  role: () => string;
  services: () => { id: string; name: string; isActive?: boolean }[];
  locations: () => BeautyLocation[];
  specialistLocationIds?: () => string[];
  /** Mock складу: довідник товарів і знімок для сповіщень. */
  inventory: { getInventoryProducts: (q: { active: "all"; take: number }) => Promise<Page<{ id: string; name: string; unit: ProductRef["unit"]; purpose: ProductRef["purpose"]; category: string | null; barcode: string | null; isActive: boolean }>> };
  now?: () => number;
  userName?: string;
}

const EXPIRY_BUCKETS = [30, 14, 7];

const err = (status: number, code: string, body: Record<string, unknown> = {}) =>
  new BeautyApiError(status, code, code, undefined, undefined, undefined, undefined, typeof body.permission === "string" ? body.permission : undefined, body);

interface SetRec {
  lines: { productId: string; quantity: number }[];
  updatedAt: string;
}
interface MaterialsRec {
  network: SetRec | null;
  locations: Map<string, SetRec>;
}

export function createMockInventoryV2(deps: MaterialsMockDeps) {
  const now = deps.now ?? (() => Date.now());
  const me: NamedRef = { id: "mock-user", name: deps.userName ?? "Світлана Коваленко" };
  const iso = () => new Date(now()).toISOString();
  const role = () => deps.role();
  const requireWrite = () => {
    if (role() !== "owner" && role() !== "admin") throw err(403, "forbidden_role", { permission: "inventory.manage", required: "all" });
  };
  const scopeIds = (): Set<string> => {
    const all = deps.locations();
    if (role() !== "specialist") return new Set(all.map((l) => l.id));
    const mine = new Set(deps.specialistLocationIds?.() ?? []);
    return new Set(all.filter((l) => mine.has(l.id) && l.isActive !== false).map((l) => l.id));
  };
  const locName = (id: string) => deps.locations().find((l) => l.id === id)?.name ?? "";

  // ---------- норми ----------
  const norms = new Map<string, MaterialsRec>();
  let normsSeeded = false;
  let productCache: Map<string, ProductRef> | null = null;
  async function products(): Promise<Map<string, ProductRef>> {
    if (!productCache) {
      const page = await deps.inventory.getInventoryProducts({ active: "all", take: 200 });
      productCache = new Map(page.items.map((p) => [p.id, { id: p.id, name: p.name, unit: p.unit, purpose: p.purpose, category: p.category, barcode: p.barcode, isActive: p.isActive }]));
    }
    return productCache;
  }
  async function seedNorms() {
    if (normsSeeded) return;
    normsSeeded = true;
    const list = await products();
    const first = deps.services()[0];
    const gloves = [...list.values()].find((p) => p.name.startsWith("Рукавички"));
    const disinfect = [...list.values()].find((p) => p.name.startsWith("Дезінфікуючий"));
    if (first && gloves && disinfect) {
      norms.set(first.id, { network: { lines: [{ productId: gloves.id, quantity: 1 }, { productId: disinfect.id, quantity: 0.05 }], updatedAt: iso() }, locations: new Map() });
    }
  }
  const serviceOf = (id: string) => {
    const s = deps.services().find((x) => x.id === id);
    if (!s) throw err(404, "service_not_found");
    return s;
  };
  const toSet = (serviceId: string, locationId: string | null, rec: SetRec, list: Map<string, ProductRef>): MaterialSet => ({
    serviceId,
    locationId,
    lines: rec.lines.map((l) => ({ product: list.get(l.productId)!, quantity: l.quantity })),
    updatedAt: rec.updatedAt,
    updatedBy: me,
  });
  function toDto(serviceId: string, list: Map<string, ProductRef>): ServiceMaterials {
    const s = serviceOf(serviceId);
    const rec = norms.get(serviceId);
    const scope = scopeIds();
    const locations: LocationMaterialSet[] = rec
      ? [...rec.locations].filter(([lid]) => scope.has(lid)).map(([lid, r]) => ({ ...toSet(serviceId, lid, r, list), locationName: locName(lid) }))
      : [];
    return { serviceId, serviceName: s.name, serviceIsActive: s.isActive !== false, network: rec?.network ? toSet(serviceId, null, rec.network, list) : null, locations };
  }
  const clone = <T,>(v: T): T => structuredClone(v);

  // ---------- сповіщення ----------
  let alerts: StaffAlert[] | null = null;
  function ensureAlerts(): StaffAlert[] {
    if (alerts) return alerts;
    const snap = peekInventory(deps.inventory);
    const list: StaffAlert[] = [];
    let n = 0;
    const base = (kind: StaffAlert["kind"], location: NamedRef, product: ProductRef, quantity: number): StaffAlert => ({
      id: `al${++n}`,
      kind,
      status: "open",
      location,
      product,
      batch: null,
      operationId: null,
      quantity,
      threshold: null,
      expiresOn: null,
      daysBucket: null,
      createdAt: new Date(now() - n * 3_600_000).toISOString(),
      readAt: null,
      readBy: null,
      resolvedAt: null,
    });
    for (const s of snap.shortages) list.push({ ...base("shortage", s.location, s.product, s.quantity), operationId: s.operationId });
    for (const l of snap.lows) list.push({ ...base("low_stock", l.location, l.product, l.balance), threshold: l.threshold });
    for (const e of snap.expiring) {
      const bucket = daysBucketFor(e.days, EXPIRY_BUCKETS);
      if (bucket === null) continue;
      list.push({ ...base("expiring", e.location, e.product, e.quantity), batch: { id: e.batchId, expiresOn: e.expiresOn, lotNumber: e.lotNumber }, expiresOn: e.expiresOn, daysBucket: bucket });
    }
    // один розв'язаний для повноти демо
    if (list.length > 3) {
      const last = list[list.length - 1]!;
      last.status = "resolved";
      last.resolvedAt = iso();
    }
    alerts = list;
    return alerts;
  }
  const visible = () => {
    const scope = scopeIds();
    return ensureAlerts().filter((a) => scope.has(a.location.id));
  };
  const page = <T,>(items: T[], q: { cursor?: string; take?: number }): Page<T> => {
    const size = Math.min(200, Math.max(1, q.take ?? 50));
    const start = q.cursor === undefined ? 0 : Number(q.cursor);
    if (!Number.isInteger(start) || start < 0) throw err(422, "invalid_cursor");
    const more = start + size < items.length;
    return { items: items.slice(start, start + size), hasMore: more, ...(more ? { nextCursor: String(start + size) } : {}) };
  };

  return {
    async getServiceMaterialsList(includeInactive = false): Promise<ServiceMaterials[]> {
      await seedNorms();
      const list = await products();
      return clone(
        [...norms.keys()]
          .map((id) => toDto(id, list))
          .filter((d) => (includeInactive || d.serviceIsActive) && (d.network || d.locations.length > 0))
          .sort((a, b) => a.serviceName.toLowerCase().localeCompare(b.serviceName.toLowerCase(), "uk") || a.serviceId.localeCompare(b.serviceId)),
      );
    },

    async getServiceMaterials(serviceId: string): Promise<ServiceMaterials> {
      await seedNorms();
      return clone(toDto(serviceId, await products()));
    },

    async setServiceMaterials(serviceId: string, r: MaterialSetRequest): Promise<MaterialSet> {
      requireWrite();
      await seedNorms();
      const list = await products();
      if (!Array.isArray(r.lines)) throw err(422, "invalid_lines");
      if (r.lines.length > 50) throw err(422, "too_many_lines");
      if (r.locationId === null && r.lines.length === 0) throw err(422, "invalid_lines");
      serviceOf(serviceId);
      if (r.locationId !== null && !deps.locations().some((l) => l.id === r.locationId)) throw err(404, "location_not_found");
      const seen = new Set<string>();
      r.lines.forEach((l, line) => {
        const p = list.get(l.productId);
        if (!p) throw err(404, "product_not_found", { line });
        if (seen.has(l.productId)) throw err(422, "duplicate_line", { line });
        seen.add(l.productId);
        if (typeof l.quantity !== "number" || !(l.quantity > 0) || l.quantity > 100_000 || Math.abs(toMilli(l.quantity) - l.quantity * 1000) > 1e-6 || (isIntegerUnit(p.unit) && !Number.isInteger(l.quantity))) {
          throw err(422, "invalid_quantity", { line });
        }
        if (!p.isActive) throw err(422, "product_inactive", { line });
        if (p.purpose === "sale") throw err(422, "product_not_for_salon_use", { line });
      });
      const rec: SetRec = { lines: r.lines.map((l) => ({ productId: l.productId, quantity: l.quantity })), updatedAt: iso() };
      const cur = norms.get(serviceId) ?? { network: null, locations: new Map<string, SetRec>() };
      if (r.locationId === null) cur.network = rec;
      else cur.locations.set(r.locationId, rec);
      norms.set(serviceId, cur);
      return clone(toSet(serviceId, r.locationId, rec, list));
    },

    async deleteServiceMaterials(serviceId: string, locationId: string | null): Promise<void> {
      requireWrite();
      await seedNorms();
      serviceOf(serviceId);
      const cur = norms.get(serviceId);
      if (locationId === null) {
        if (!cur?.network) throw err(404, "materials_not_found");
        cur.network = null;
      } else {
        if (!cur?.locations.has(locationId)) throw err(404, "materials_not_found");
        cur.locations.delete(locationId);
      }
    },

    async getInventoryAlerts(q: AlertQuery = {}): Promise<Page<StaffAlert>> {
      const status = q.status ?? "active";
      if (!["active", "open", "read", "resolved", "all"].includes(status)) throw err(422, "invalid_alert_status");
      if (q.kind && !["low_stock", "expiring", "shortage"].includes(q.kind)) throw err(422, "invalid_alert_kind");
      if (q.locationId && !scopeIds().has(q.locationId)) throw err(404, "location_not_found");
      const items = visible()
        .filter((a) => (status === "all" ? true : status === "active" ? a.status !== "resolved" : a.status === status))
        .filter((a) => (!q.kind || a.kind === q.kind) && (!q.locationId || a.location.id === q.locationId))
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt) || b.id.localeCompare(a.id));
      return clone(page(items, q));
    },

    async getInventoryAlertsUnreadCount(): Promise<number> {
      return Math.min(1000, visible().filter((a) => a.status === "open").length);
    },

    async markInventoryAlertRead(id: string): Promise<StaffAlert> {
      const a = visible().find((x) => x.id === id);
      if (!a) throw err(404, "alert_not_found");
      if (a.status === "open") {
        a.status = "read";
        a.readAt = iso();
        a.readBy = me;
      }
      return clone(a);
    },

    async markAllInventoryAlertsRead(locationId?: string): Promise<number> {
      if (locationId && !scopeIds().has(locationId)) throw err(404, "location_not_found");
      let updated = 0;
      for (const a of visible()) {
        if (a.status === "open" && (!locationId || a.location.id === locationId)) {
          a.status = "read";
          a.readAt = iso();
          a.readBy = me;
          updated += 1;
        }
      }
      return updated;
    },
  };
}
