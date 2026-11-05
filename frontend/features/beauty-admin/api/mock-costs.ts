import { BeautyApiError } from "@/features/beauty-auth/errors";
import { SERVICE_COSTS_SEED } from "../../beauty-shared/demo-seed";
import type { BeautyLocation, ServiceCost, ServiceCostDetail, ServiceCostInput, ServiceCostRow, ServiceItem } from "../types";

/**
 * Mock §24.6.1: собівартість послуг (мережа + override закладу). Дотримується кодів контракту:
 * specialist -> 403 forbidden_role (до читання тіла), 404 service_not_found|location_not_found|cost_not_found,
 * 422 invalid_cost|invalid_master_percent, 429 rate_limited (ліміт на користувача, як `ServiceCosts:WritePerUserPerMinute`).
 * Знімки завершених записів від змін НЕ залежать (вони в demo-seed): зміна діє лише на записи, завершені після збереження.
 */

export const COST_WRITES_PER_MINUTE = 60;
export const MAX_COST = 1_000_000;

interface Deps {
  role: () => string;
  services: () => ServiceItem[];
  locations: () => BeautyLocation[];
  now?: () => number;
  userName?: string;
  /** `false` - без затримки (тести). */
  wait?: boolean;
}

const err = (status: number, code: string, retry?: number) => new BeautyApiError(status, code, code, retry);
/** Не більше `digits` знаків після коми (без похибки плаваючої коми). */
const hasMaxDecimals = (v: number, digits: number) => Math.abs(Math.round(v * 10 ** digits) - v * 10 ** digits) < 1e-7;

interface Entry {
  costAmount: number;
  masterPercent: number | null;
  updatedAt: number;
  updatedBy: { id: string; name: string };
}
const keyOf = (serviceId: string, locationId: string | null) => `${serviceId}|${locationId ?? ""}`;

export function createMockCosts(deps: Deps) {
  const now = deps.now ?? (() => Date.now());
  const me = { id: "mock-user", name: deps.userName ?? "Світлана Коваленко" };
  const isManager = () => deps.role() === "owner" || deps.role() === "admin";
  const guard = () => {
    if (!isManager()) throw err(403, "forbidden_role");
  };
  const wait = async <T>(v: T): Promise<T> => {
    if (deps.wait !== false) await new Promise((r) => setTimeout(r, 120));
    return structuredClone(v);
  };

  const entries = new Map<string, Entry>();
  for (const c of SERVICE_COSTS_SEED) {
    entries.set(keyOf(c.serviceId, c.locationId), { costAmount: c.costAmount, masterPercent: c.masterPercent, updatedAt: now() - 50 * 86_400_000, updatedBy: me });
  }
  const writes: number[] = [];

  const toCost = (e: Entry): ServiceCost => ({ costAmount: e.costAmount, masterPercent: e.masterPercent, updatedAt: new Date(e.updatedAt).toISOString(), updatedBy: e.updatedBy });
  const serviceOrThrow = (id: string) => {
    const s = deps.services().find((x) => x.id === id);
    if (!s) throw err(404, "service_not_found");
    return s;
  };
  const locationOrThrow = (id: string) => {
    const l = deps.locations().find((x) => x.id === id);
    if (!l) throw err(404, "location_not_found");
    return l;
  };

  function detailOf(serviceId: string): ServiceCostDetail {
    const network = entries.get(keyOf(serviceId, null));
    const overrides = deps
      .locations()
      .filter((l) => entries.has(keyOf(serviceId, l.id)))
      .map((l) => ({ ...toCost(entries.get(keyOf(serviceId, l.id))!), locationId: l.id, locationName: l.name }));
    return { serviceId, ...(network ? { network: toCost(network) } : {}), locations: overrides };
  }

  function rateLimit() {
    const t = now();
    while (writes.length > 0 && t - writes[0] >= 60_000) writes.shift();
    if (writes.length >= COST_WRITES_PER_MINUTE) throw err(429, "rate_limited", 30);
    writes.push(t);
  }

  return {
    getServiceCosts: async (): Promise<ServiceCostRow[]> => {
      guard();
      return wait(deps.services().map((s) => ({ ...detailOf(s.id), serviceName: s.name, isActive: true })));
    },
    getServiceCostDetail: async (serviceId: string): Promise<ServiceCostDetail> => {
      guard();
      serviceOrThrow(serviceId);
      return wait(detailOf(serviceId));
    },
    setServiceCost: async (serviceId: string, input: ServiceCostInput): Promise<ServiceCost & { locationId: string | null }> => {
      guard();
      serviceOrThrow(serviceId);
      const locationId = input.locationId ?? null;
      if (locationId !== null) locationOrThrow(locationId);
      const c = input.costAmount;
      if (typeof c !== "number" || !Number.isFinite(c) || c < 0 || c > MAX_COST || !hasMaxDecimals(c, 2)) throw err(422, "invalid_cost");
      const p = input.masterPercent ?? null;
      if (p !== null && (typeof p !== "number" || !Number.isFinite(p) || p < 0 || p > 100 || !hasMaxDecimals(p, 2))) throw err(422, "invalid_master_percent");
      rateLimit();
      const k = keyOf(serviceId, locationId);
      const old = entries.get(k);
      // Без змін - 200 без оновлення (аудиту немає, `updatedAt` не зсувається).
      if (!old || old.costAmount !== c || old.masterPercent !== p) entries.set(k, { costAmount: c, masterPercent: p, updatedAt: now(), updatedBy: me });
      return wait({ ...toCost(entries.get(k)!), locationId });
    },
    deleteServiceCost: async (serviceId: string, locationId: string | null): Promise<void> => {
      guard();
      serviceOrThrow(serviceId);
      if (locationId !== null) locationOrThrow(locationId);
      rateLimit();
      if (!entries.delete(keyOf(serviceId, locationId))) throw err(404, "cost_not_found");
      await wait(null);
    },
  };
}

export type MockCosts = ReturnType<typeof createMockCosts>;
