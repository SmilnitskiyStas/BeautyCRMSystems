import {
  normalizeSegment,
  scheduleError,
  validateName,
  validateSegment,
  validateTemplate,
} from "./logic";
import type { CampaignDto, CampaignRequest, CampaignType, CouponMode, PromotionItem, Segment, SegmentKind, SharedCoupon } from "./types";

/** Стан майстра розсилки (§26.12): поля форми - рядки, щоб поле вводу можна було очистити. */
export interface WizardForm {
  name: string;
  type: CampaignType;
  promotionId: string;
  kind: SegmentKind;
  days: string;
  serviceId: string;
  minVisits: string;
  periodDays: string;
  locationId: string;
  template: string;
  couponMode: CouponMode;
  sharedCouponId: string;
  /** `YYYY-MM-DD` (локально). */
  couponFrom: string;
  couponTo: string;
}

export const PROMO_TEMPLATE = "{name}, для вас знижка {discount} на {service} до {valid_to}.";
export const CODE_TEMPLATE = "{name}, ваш код {code}: знижка {discount} на {service} до {valid_to}.";
export const NEWS_TEMPLATE = "{name}, у нас новини! Дивіться, що з'явилося в закладі.";
const DEFAULTS = new Set([PROMO_TEMPLATE, CODE_TEMPLATE, NEWS_TEMPLATE]);

export const defaultTemplate = (type: CampaignType, couponMode: CouponMode) =>
  type === "news" ? NEWS_TEMPLATE : couponMode === "none" ? PROMO_TEMPLATE : CODE_TEMPLATE;

export function emptyForm(): WizardForm {
  return {
    name: "",
    type: "promo",
    promotionId: "",
    kind: "lapsed",
    days: "60",
    serviceId: "",
    minVisits: "3",
    periodDays: "90",
    locationId: "",
    template: PROMO_TEMPLATE,
    couponMode: "none",
    sharedCouponId: "",
    couponFrom: "",
    couponTo: "",
  };
}

const pad = (n: number) => String(n).padStart(2, "0");
export const isoToDate = (iso: string | undefined): string => {
  if (!iso) return "";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
};
/** Початок / кінець доби в зоні браузера -> ISO (UTC). Кінець у контракті виключний, тому беремо 23:59:59. */
export const dateToIso = (ymd: string, endOfDay: boolean): string => new Date(`${ymd}T${endOfDay ? "23:59:59" : "00:00:00"}`).toISOString();

export function formFromDto(c: CampaignDto): WizardForm {
  const s = c.segment;
  return {
    name: c.name,
    type: c.type,
    promotionId: c.promotionId ?? "",
    kind: s.kind,
    days: s.days !== undefined ? String(s.days) : "60",
    serviceId: s.serviceId ?? "",
    minVisits: s.minVisits !== undefined ? String(s.minVisits) : "3",
    periodDays: s.periodDays !== undefined ? String(s.periodDays) : "90",
    locationId: c.locationId ?? "",
    template: c.template,
    couponMode: c.couponMode,
    sharedCouponId: c.sharedCouponId ?? "",
    couponFrom: isoToDate(c.couponValidFrom),
    couponTo: isoToDate(c.couponValidTo),
  };
}

const toNum = (v: string): number | undefined => (v.trim() === "" ? undefined : Number(v));

export function segmentOf(f: WizardForm): Segment {
  return normalizeSegment({
    kind: f.kind,
    days: toNum(f.days),
    ...(f.serviceId ? { serviceId: f.serviceId } : {}),
    minVisits: toNum(f.minVisits),
    periodDays: toNum(f.periodDays),
  });
}

export function toCampaignRequest(f: WizardForm): CampaignRequest {
  return {
    name: f.name.trim(),
    type: f.type,
    ...(f.type === "promo" && f.promotionId ? { promotionId: f.promotionId } : {}),
    segment: segmentOf(f),
    ...(f.locationId ? { locationId: f.locationId } : {}),
    template: f.template,
    couponMode: f.type === "news" ? "none" : f.couponMode,
    ...(f.type === "promo" && f.couponMode === "shared" && f.sharedCouponId ? { sharedCouponId: f.sharedCouponId } : {}),
    ...(f.type === "promo" && f.couponMode === "personal" && f.couponFrom ? { couponValidFrom: dateToIso(f.couponFrom, false) } : {}),
    ...(f.type === "promo" && f.couponMode === "personal" && f.couponTo ? { couponValidTo: dateToIso(f.couponTo, true) } : {}),
  };
}

/** Режим купона за доступом акції: публічна - без купона; «лише за кодом» - персональний (за замовч.) або загальний. */
export function couponModeFor(type: CampaignType, promo: PromotionItem | undefined, current: CouponMode): CouponMode {
  if (type === "news" || !promo || promo.access === "public") return "none";
  return current === "none" ? "personal" : current;
}

/** Після зміни акції/типу/купона стандартний текст підміняється, а текст менеджера - ніколи. */
export function retemplate(prev: WizardForm, next: WizardForm): WizardForm {
  if (prev.template.trim() !== "" && !DEFAULTS.has(prev.template)) return next;
  return { ...next, template: defaultTemplate(next.type, next.couponMode) };
}

/** Перевірка кроку (1..4); `null` - можна йти далі. Дзеркало серверних 422 для швидкого зворотного зв'язку. */
export function validateStep(step: number, f: WizardForm, promo: PromotionItem | undefined, coupons: SharedCoupon[] | undefined): string | null {
  if (step === 1) {
    const n = validateName(f.name);
    if (n) return n;
    if (f.type === "promo" && !f.promotionId) return "Оберіть акцію або вкажіть, що це новина.";
    if (f.type === "promo" && promo && !promo.isActive) return "Акція вимкнена: оберіть активну.";
    return null;
  }
  if (step === 2) return validateSegment(segmentOf(f));
  if (step === 3) return validateTemplate(f.template, { type: f.type, couponMode: f.couponMode });
  if (step === 4) {
    if (f.couponMode === "personal") {
      if (!f.couponTo) return "Вкажіть, до якої дати діють персональні коди.";
      if (f.couponFrom && f.couponTo < f.couponFrom) return "Кінець дії кодів має бути не раніше початку.";
      if (promo?.endsAt && f.couponTo > isoToDate(promo.endsAt)) return "Коди не можуть діяти довше за акцію.";
      if (promo?.startsAt && f.couponFrom && f.couponFrom < isoToDate(promo.startsAt)) return "Коди не можуть діяти раніше за початок акції.";
    }
    if (f.couponMode === "shared") {
      const c = coupons?.find((x) => x.id === f.sharedCouponId);
      if (!c) return "Оберіть загальний код.";
      if (c.status !== "active") return "Цей код відкликано: оберіть інший.";
    }
    return null;
  }
  return null;
}

/** Розклад: `""` - зараз; `datetime-local` -> ISO або помилка. */
export function scheduleFromInput(mode: "now" | "later", local: string, nowMs: number): { iso?: string; error: string | null } {
  if (mode === "now") return { error: null };
  if (!local) return { error: "Вкажіть дату й час відправки." };
  const t = new Date(local).getTime();
  if (Number.isNaN(t)) return { error: "Вкажіть дату й час відправки." };
  const iso = new Date(t).toISOString();
  return { iso, error: scheduleError(iso, nowMs) };
}
