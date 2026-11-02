/**
 * Демо-дані §26 (ADR-015): акції, загальні й персональні купони, кампанії в різних статусах, згоди клієнтів.
 * Усе відносне до «зараз» (діб/годин); mock (`mock-marketing.ts`) перетворює їх на дати. Пов'язано з `demo-seed.ts`
 * (клієнти oc/sm/kl/tg/mp, послуги, розмова cv6).
 */

export interface PromotionSeed {
  id: string;
  name: string;
  discountType: "percent" | "fixed";
  discountValue: number;
  /** Діб тому початок / через скільки діб кінець (від'ємне - минуло). */
  startsDaysAgo: number;
  endsInDays: number | null;
  isActive: boolean;
  access: "public" | "code_only";
  locationIds: string[];
  serviceIds: string[];
}

export const PROMOTIONS_SEED: PromotionSeed[] = [
  { id: "autumn", name: "Осінній манікюр", discountType: "percent", discountValue: 20, startsDaysAgo: 19, endsInDays: 12, isActive: true, access: "public", locationIds: [], serviceIds: ["mn", "mn2"] },
  { id: "cl20", name: "Чистка обличчя −20%", discountType: "percent", discountValue: 20, startsDaysAgo: 5, endsInDays: 25, isActive: true, access: "public", locationIds: ["c"], serviceIds: ["cl"] },
  { id: "vip15", name: "Для своїх −15%", discountType: "percent", discountValue: 15, startsDaysAgo: 3, endsInDays: 35, isActive: true, access: "code_only", locationIds: [], serviceIds: [] },
  { id: "summer", name: "Літня знижка (минула)", discountType: "fixed", discountValue: 100, startsDaysAgo: 120, endsInDays: -60, isActive: false, access: "public", locationIds: [], serviceIds: ["pd"] },
];

export interface SharedCouponSeed {
  id: string;
  promotionId: string;
  code: string;
  validFromDaysAgo: number;
  validToInDays: number;
  maxUses?: number;
  usesCount: number;
  status: "active" | "revoked";
}

/** Загальні коди: `AUTUMN15` діє і в публічній формі запису (mock), `OLD2025` відкликано. */
export const SHARED_COUPONS_SEED: SharedCouponSeed[] = [
  { id: "cp1", promotionId: "vip15", code: "AUTUMN15", validFromDaysAgo: 3, validToInDays: 27, maxUses: 100, usesCount: 12, status: "active" },
  { id: "cp2", promotionId: "vip15", code: "OLD2025", validFromDaysAgo: 60, validToInDays: 5, maxUses: 50, usesCount: 4, status: "revoked" },
];

/** Загальний код для демо публічної форми запису (mock): знижка за акцією `vip15`. */
export const PUBLIC_DEMO_COUPON = { code: "AUTUMN15", percent: 15, promotionName: "Для своїх −15%" } as const;

export interface CampaignStatsSeed {
  byStatus: { pending: number; queued: number; sent: number; failed: number; skipped: number };
  failed: { botBlocked: number; chatNotFound: number; other: number };
  skipped: Record<string, number>;
  clicks: number;
  bookings: number;
  bookingsWithCoupon: number;
  couponRedemptionsActive: number;
  revenue: number;
  unsubscribedAfter: number;
}

export interface CampaignSeed {
  id: string;
  name: string;
  type: "promo" | "news";
  status: "draft" | "scheduled" | "sending" | "done" | "cancelled";
  promotionId?: string;
  segment: { kind: "lapsed" | "frequent" | "new" | "all_consented"; days?: number; serviceId?: string; minVisits?: number; periodDays?: number };
  locationId?: string;
  template: string;
  couponMode: "none" | "personal" | "shared";
  sharedCouponId?: string;
  /** Для персональних: строк дії в добах від створення. */
  couponValidInDays?: number;
  revision: number;
  /** Час відправки: через скільки годин (минуле - від'ємне). */
  scheduledInHours?: number;
  recipientsTotal?: number;
  createdDaysAgo: number;
  stats?: CampaignStatsSeed;
  couponSummary?: { issued: number; redeemedActive: number; released: number; revoked: number };
}

export const CAMPAIGNS_SEED: CampaignSeed[] = [
  {
    id: "k1", name: "Осінній манікюр: давні клієнти", type: "promo", status: "draft", promotionId: "autumn",
    segment: { kind: "lapsed", days: 60, serviceId: "mn" }, template: "{name}, давно не бачились! Для вас -{discount} на {service} до {valid_to}.",
    couponMode: "none", revision: 2, createdDaysAgo: 1,
  },
  {
    id: "k2", name: "Чистка обличчя для нових клієнтів", type: "promo", status: "scheduled", promotionId: "cl20",
    segment: { kind: "new" }, locationId: "c", template: "{name}, дякуємо, що обрали нас! Чистка обличчя зі знижкою {discount} діє до {valid_to}.",
    couponMode: "none", revision: 3, scheduledInHours: 20, recipientsTotal: 41, createdDaysAgo: 2,
  },
  {
    id: "k3", name: "Для своїх −15% (загальний код)", type: "promo", status: "sending", promotionId: "vip15",
    segment: { kind: "all_consented" }, template: "{name}, знижка {discount} для своїх. Назвіть код {code} під час запису.",
    couponMode: "shared", sharedCouponId: "cp1", revision: 2, scheduledInHours: -30, recipientsTotal: 120, createdDaysAgo: 3,
    stats: {
      byStatus: { pending: 38, queued: 4, sent: 74, failed: 2, skipped: 2 },
      failed: { botBlocked: 2, chatNotFound: 0, other: 0 },
      skipped: { unsubscribed: 1, frequency: 1 },
      clicks: 19, bookings: 6, bookingsWithCoupon: 6, couponRedemptionsActive: 6, revenue: 4860, unsubscribedAfter: 1,
    },
    couponSummary: { issued: 0, redeemedActive: 6, released: 0, revoked: 0 },
  },
  {
    id: "k4", name: "Повернення улюблених клієнтів", type: "promo", status: "done", promotionId: "vip15",
    segment: { kind: "lapsed", days: 45 }, template: "{name}, ми сумуємо! Ваш особистий код {code}: {discount} на {service} до {valid_to}.",
    couponMode: "personal", couponValidInDays: 20, revision: 4, scheduledInHours: -24 * 6, recipientsTotal: 85, createdDaysAgo: 8,
    stats: {
      byStatus: { pending: 0, queued: 0, sent: 78, failed: 3, skipped: 4 },
      failed: { botBlocked: 2, chatNotFound: 1, other: 0 },
      skipped: { unsubscribed: 2, frequency: 1, offer_expired: 1 },
      clicks: 27, bookings: 11, bookingsWithCoupon: 9, couponRedemptionsActive: 8, revenue: 9450, unsubscribedAfter: 2,
    },
    couponSummary: { issued: 78, redeemedActive: 8, released: 1, revoked: 5 },
  },
  {
    id: "k5", name: "Новини осені", type: "news", status: "cancelled",
    segment: { kind: "frequent", minVisits: 3, periodDays: 90 }, template: "{name}, ми оновили меню послуг. Дивіться новинки сезону!",
    couponMode: "none", revision: 5, scheduledInHours: -24 * 2, recipientsTotal: 52, createdDaysAgo: 5,
    stats: {
      byStatus: { pending: 0, queued: 0, sent: 0, failed: 0, skipped: 52 },
      failed: { botBlocked: 0, chatNotFound: 0, other: 0 },
      skipped: { campaign_cancelled: 52 },
      clicks: 0, bookings: 0, bookingsWithCoupon: 0, couponRedemptionsActive: 0, revenue: 0, unsubscribedAfter: 0,
    },
  },
];

type SeedLevel = "full" | "selective" | "none";

export interface ConsentSeedEvent {
  daysAgo: number;
  levelFrom?: SeedLevel;
  levelTo: SeedLevel;
  source: "chat" | "admin" | "stop";
  via?: "button" | "command" | "text" | "api";
  /** Подію виконав адміністратор (mock-користувач). */
  admin?: boolean;
  basis?: "written" | "in_person" | "other";
  basisNote?: string;
  campaignId?: string;
}

export interface ConsentSeed {
  clientId: string;
  /** Нові першими. */
  events: ConsentSeedEvent[];
  offeredDaysAgo?: number;
}

/** oc - повна, kl - вибіркова (знижено адміністратором), tg - «Стоп», mp - відписалась у розмові, sm - ще не обирала. */
export const CONSENT_SEED: ConsentSeed[] = [
  { clientId: "oc", offeredDaysAgo: 41, events: [{ daysAgo: 40, levelTo: "full", source: "chat", via: "button" }] },
  {
    clientId: "kl",
    offeredDaysAgo: 70,
    events: [
      { daysAgo: 12, levelFrom: "full", levelTo: "selective", source: "admin", admin: true, via: "api" },
      { daysAgo: 66, levelTo: "full", source: "chat", via: "button" },
    ],
  },
  {
    clientId: "tg",
    offeredDaysAgo: 30,
    events: [
      { daysAgo: 4, levelFrom: "full", levelTo: "none", source: "stop", via: "command", campaignId: "k4" },
      { daysAgo: 29, levelTo: "full", source: "chat", via: "button" },
    ],
  },
  {
    clientId: "mp",
    offeredDaysAgo: 1,
    events: [
      { daysAgo: 0, levelFrom: "selective", levelTo: "none", source: "chat", via: "button" },
      { daysAgo: 0.1, levelTo: "selective", source: "chat", via: "button" },
    ],
  },
  { clientId: "sm", offeredDaysAgo: 2, events: [] },
];

export interface ClientCouponSeed {
  id: string;
  clientId: string;
  codeHint: string;
  promotionId: string;
  campaignId: string;
  validToInDays: number;
  status: "active" | "revoked";
  /** `true` - використано на першому записі клієнта (mock підбирає його з демо-записів). */
  used?: boolean;
}

export const CLIENT_COUPONS_SEED: ClientCouponSeed[] = [
  { id: "pc1", clientId: "oc", codeHint: "M3D", promotionId: "vip15", campaignId: "k4", validToInDays: 14, status: "active", used: true },
  { id: "pc2", clientId: "tg", codeHint: "X3Q", promotionId: "vip15", campaignId: "k4", validToInDays: 12, status: "active" },
  { id: "pc3", clientId: "kl", codeHint: "7TP", promotionId: "vip15", campaignId: "k4", validToInDays: -1, status: "active" },
  { id: "pc4", clientId: "mp", codeHint: "B2R", promotionId: "vip15", campaignId: "k4", validToInDays: 10, status: "revoked" },
];
