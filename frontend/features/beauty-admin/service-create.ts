import type { BeautyAdminApi } from "./api/client";
import type { ServiceCreateInput } from "./types";

/** Верхня межа ціни: як для собівартості (§24). */
export const MAX_PRICE = 1_000_000;
/** Межі тривалості збігаються з бекендом (5..720 хв). */
export const MIN_DURATION_MINUTES = 5;
export const MAX_DURATION_MINUTES = 720;

/** Ціна: число ≥ 0 з крапкою або комою, до 2 знаків після коми. Порожній/некоректний рядок -> `null`. */
export function parsePrice(raw: string): number | null {
  const t = raw.trim().replace(/\s/g, "");
  if (!/^\d+([.,]\d{1,2})?$/.test(t)) return null;
  const n = Number(t.replace(",", "."));
  return Number.isFinite(n) && n <= MAX_PRICE ? n : null;
}

export interface ServiceFormValues {
  name: string;
  category: string;
  duration: string;
  description: string;
  price: string;
  /** locationId -> введений рядок; порожній = діє мережева ціна. */
  locationPrices: Record<string, string>;
}

export interface ServiceFormErrors {
  name?: string;
  duration?: string;
  price?: string;
  locationPrices: Record<string, string>;
}

export function validateServiceForm(v: ServiceFormValues): ServiceFormErrors {
  const e: ServiceFormErrors = { locationPrices: {} };
  if (!v.name.trim()) e.name = "Вкажіть назву послуги.";
  const d = v.duration.trim();
  if (!d) e.duration = "Вкажіть тривалість у хвилинах.";
  else if (!/^\d+$/.test(d) || Number(d) < MIN_DURATION_MINUTES || Number(d) > MAX_DURATION_MINUTES)
    e.duration = `Тривалість — ціле число хвилин від ${MIN_DURATION_MINUTES} до ${MAX_DURATION_MINUTES}.`;
  if (!v.price.trim()) e.price = "Вкажіть мережеву ціну.";
  else if (parsePrice(v.price) === null) e.price = "Ціна — число від 0, наприклад 500 або 499,50.";
  for (const [id, raw] of Object.entries(v.locationPrices)) {
    if (raw.trim() && parsePrice(raw) === null) e.locationPrices[id] = "Ціна — число від 0, наприклад 500 або 499,50.";
  }
  return e;
}

export const hasErrors = (e: ServiceFormErrors) => Boolean(e.name || e.duration || e.price || Object.keys(e.locationPrices).length > 0);

export interface ServiceCreatePlan {
  input: ServiceCreateInput;
  networkPrice: number;
  locationPrices: { locationId: string; locationName: string; price: number }[];
  specialists: { id: string; name: string }[];
}

/** Що вже виконано: зберігається між спробами, щоб повтор не дублював послугу й кроки. */
export interface CreateProgress {
  serviceId: string | null;
  networkDone: boolean;
  locationsDone: string[];
  specialistsDone: string[];
  /** Майстри, яких пропущено після 403 forbidden_role (змінює лише власник). */
  specialistsSkipped: string[];
}

export const newProgress = (): CreateProgress => ({ serviceId: null, networkDone: false, locationsDone: [], specialistsDone: [], specialistsSkipped: [] });

/** Послугу створено, але наступний крок не вдався. */
export class PartialCreateError extends Error {
  constructor(
    public readonly step: string,
    public readonly reason: unknown,
    /** Майстер, на якому збій (лише для кроку призначення). */
    public readonly specialistId?: string,
  ) {
    super(`service created, step failed: ${step}`);
    this.name = "PartialCreateError";
  }
}

type FlowApi = Pick<BeautyAdminApi, "createService" | "setServicePrice" | "getStaffProfile" | "setStaffServices">;

/**
 * Послідовність: POST послуга -> мережева ціна -> ціни закладів (лише заповнені) -> послуги майстрів.
 * `PUT /specialists/{id}/services` замінює весь список, тож для кожного майстра беремо ПОТОЧНІ послуги й додаємо нову (merge).
 * Прогрес мутується на місці: повтор після збою пропускає виконане й не створює послугу вдруге.
 */
export async function runCreateService(api: FlowApi, plan: ServiceCreatePlan, progress: CreateProgress): Promise<void> {
  if (!progress.serviceId) {
    progress.serviceId = (await api.createService(plan.input)).id;
  }
  const serviceId = progress.serviceId;
  const step = async (label: string, fn: () => Promise<void>, specialistId?: string) => {
    try {
      await fn();
    } catch (err) {
      throw new PartialCreateError(label, err, specialistId);
    }
  };

  if (!progress.networkDone) {
    await step("не вдалося зберегти мережеву ціну", async () => {
      await api.setServicePrice(serviceId, plan.networkPrice);
      progress.networkDone = true;
    });
  }
  for (const lp of plan.locationPrices) {
    if (progress.locationsDone.includes(lp.locationId)) continue;
    await step(`не вдалося зберегти ціну для закладу «${lp.locationName}»`, async () => {
      await api.setServicePrice(serviceId, lp.price, lp.locationId);
      progress.locationsDone.push(lp.locationId);
    });
  }
  for (const sp of plan.specialists) {
    if (progress.specialistsDone.includes(sp.id) || progress.specialistsSkipped.includes(sp.id)) continue;
    await step(`не вдалося призначити послугу майстру «${sp.name}»`, async () => {
      const profile = await api.getStaffProfile(sp.id);
      if (!profile) throw new Error("specialist not found");
      const ids = Array.from(new Set([...profile.services.map((s) => s.id), serviceId]));
      await api.setStaffServices(sp.id, ids);
      progress.specialistsDone.push(sp.id);
    }, sp.id);
  }
}
