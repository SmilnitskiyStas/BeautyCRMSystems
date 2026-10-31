import { apiJson } from "@/features/beauty-auth/session";
import type { BeautyAdminApi } from "./client";
import type {
  AppointmentCoupon,
  AudiencePreview,
  CampaignActor,
  CampaignDto,
  CampaignListItem,
  CampaignPreview,
  CampaignRequest,
  CampaignStats,
  CampaignStatus,
  CampaignType,
  ClientCoupon,
  ConsentBasis,
  ConsentChannelState,
  ConsentEvent,
  ConsentLevel,
  ConsentSource,
  ConsentVia,
  CouponCheck,
  CouponMode,
  CouponOutcome,
  CouponSummary,
  MarketingConsent,
  PreviewWarning,
  PromotionItem,
  Segment,
  SegmentKind,
  SharedCoupon,
} from "../types";

/**
 * HTTP-клієнт §26 (ADR-015): явна вибірка полів - зайве (зокрема коди купонів, хеші, імена одержувачів) у UI не потрапляє.
 * Усі відповіді читаються з `cache: "no-store"`. Помилки - `BeautyApiError` з `{code,message}` (тексти формує UI).
 */

const B = "/api/beauty";
const NO_STORE = { cache: "no-store" } as const;
type Raw = Record<string, unknown>;

const obj = (v: unknown): Raw => (v && typeof v === "object" ? (v as Raw) : {});
const str = (v: unknown): string | undefined => (typeof v === "string" && v !== "" ? v : undefined);
const num = (v: unknown): number | undefined => (typeof v === "number" && Number.isFinite(v) ? v : undefined);
const int0 = (v: unknown): number => num(v) ?? 0;
const list = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);
const oneOf = <T extends string>(v: unknown, all: readonly T[], fallback: T): T => (typeof v === "string" && (all as readonly string[]).includes(v) ? (v as T) : fallback);

const CAMPAIGN_STATUSES = ["draft", "scheduled", "sending", "done", "cancelled"] as const;
const SEGMENT_KINDS = ["lapsed", "frequent", "new", "all_consented"] as const;
const COUPON_MODES = ["none", "personal", "shared"] as const;
const LEVELS = ["full", "selective", "none"] as const;
const SOURCES = ["chat", "admin", "stop"] as const;
const VIAS = ["button", "command", "text", "api"] as const;
const BASES = ["written", "in_person", "other"] as const;
const WARNINGS = ["promotion_ends_soon", "coupon_expires_before_send", "exceeds_daily_limit", "no_buttons_channel"] as const;

function actor(v: unknown): CampaignActor | undefined {
  if (typeof v === "string" && v) return { id: "", name: v };
  const o = obj(v);
  const name = str(o.name);
  return name || str(o.id) ? { id: str(o.id) ?? "", name: name ?? "" } : undefined;
}
const put = <K extends string, V>(key: K, v: V | undefined): Partial<Record<K, V>> => (v === undefined ? {} : ({ [key]: v } as Record<K, V>));

function toSegment(v: unknown): Segment {
  const o = obj(v);
  const kind = oneOf<SegmentKind>(o.kind, SEGMENT_KINDS, "all_consented");
  return { kind, ...put("days", num(o.days)), ...put("serviceId", str(o.serviceId)), ...put("minVisits", num(o.minVisits)), ...put("periodDays", num(o.periodDays)) };
}

export function toCampaignDto(raw: unknown): CampaignDto {
  const o = obj(raw);
  return {
    id: str(o.id) ?? "",
    name: str(o.name) ?? "",
    type: oneOf<CampaignType>(o.type, ["promo", "news"], "promo"),
    ...put("promotionId", str(o.promotionId)),
    segment: toSegment(o.segment),
    ...put("locationId", str(o.locationId)),
    template: typeof o.template === "string" ? o.template : "",
    couponMode: oneOf<CouponMode>(o.couponMode, COUPON_MODES, "none"),
    ...put("sharedCouponId", str(o.sharedCouponId)),
    ...put("couponValidFrom", str(o.couponValidFrom)),
    ...put("couponValidTo", str(o.couponValidTo)),
    status: oneOf<CampaignStatus>(o.status, CAMPAIGN_STATUSES, "draft"),
    revision: num(o.revision) ?? 1,
    ...put("scheduledAt", str(o.scheduledAt)),
    ...put("recipientsTotal", num(o.recipientsTotal)),
    ...put("confirmedBy", actor(o.confirmedBy)),
    ...put("confirmedAt", str(o.confirmedAt)),
    ...put("cancelledBy", actor(o.cancelledBy)),
    ...put("cancelledAt", str(o.cancelledAt)),
    ...put("startedAt", str(o.startedAt)),
    ...put("finishedAt", str(o.finishedAt)),
    createdBy: actor(o.createdBy) ?? { id: "", name: "" },
    createdAt: str(o.createdAt) ?? "",
  };
}

function toListItem(raw: unknown): CampaignListItem {
  const o = obj(raw);
  const p = obj(o.promotion);
  return {
    id: str(o.id) ?? "",
    name: str(o.name) ?? "",
    type: oneOf<CampaignType>(o.type, ["promo", "news"], "promo"),
    status: oneOf<CampaignStatus>(o.status, CAMPAIGN_STATUSES, "draft"),
    ...(str(p.id) ? { promotion: { id: str(p.id)!, name: str(p.name) ?? "" } } : {}),
    ...put("scheduledAt", str(o.scheduledAt)),
    ...put("recipientsTotal", num(o.recipientsTotal)),
    sent: int0(o.sent),
    createdAt: str(o.createdAt) ?? "",
  };
}

function toAudience(raw: unknown): AudiencePreview {
  const o = obj(raw);
  const e = obj(o.excluded);
  return {
    matched: int0(o.matched),
    recipients: int0(o.recipients),
    excluded: { noTelegram: int0(e.noTelegram), unsubscribed: int0(e.unsubscribed), noConsent: int0(e.noConsent), levelMismatch: int0(e.levelMismatch), frequency: int0(e.frequency) },
  };
}

function toPreview(raw: unknown): CampaignPreview {
  const o = obj(raw);
  const l = obj(o.limits);
  const w = obj(o.window);
  return {
    ...toAudience(raw),
    revision: num(o.revision) ?? 1,
    sampleText: typeof o.sampleText === "string" ? o.sampleText : "",
    buttons: list(o.buttons).filter((b): b is string => typeof b === "string" && b.trim() !== "").slice(0, 10),
    limits: { dailyLimit: int0(l.dailyLimit), clientIntervalDays: int0(l.clientIntervalDays), estimatedDays: Math.max(1, int0(l.estimatedDays)) },
    window: { from: str(w.from) ?? "09:00", to: str(w.to) ?? "20:00" },
    warnings: list(o.warnings).filter((x): x is PreviewWarning => (WARNINGS as readonly unknown[]).includes(x)),
  };
}

function toStats(raw: unknown): CampaignStats {
  const o = obj(raw);
  const s = obj(o.byStatus);
  const f = obj(o.failed);
  const skipped: Record<string, number> = {};
  for (const [k, v] of Object.entries(obj(o.skipped))) {
    const n = num(v);
    if (n !== undefined && /^[a-z_]{1,40}$/.test(k)) skipped[k] = n;
  }
  return {
    recipients: int0(o.recipients),
    byStatus: { pending: int0(s.pending), queued: int0(s.queued), sent: int0(s.sent), failed: int0(s.failed), skipped: int0(s.skipped) },
    failed: { botBlocked: int0(f.botBlocked), chatNotFound: int0(f.chatNotFound), other: int0(f.other) },
    skipped,
    clicks: int0(o.clicks),
    bookings: int0(o.bookings),
    bookingsWithCoupon: int0(o.bookingsWithCoupon),
    couponRedemptionsActive: int0(o.couponRedemptionsActive),
    revenue: int0(o.revenue),
    unsubscribedAfter: int0(o.unsubscribedAfter),
    viewed: null,
  };
}

function toPromotion(raw: unknown): PromotionItem {
  const o = obj(raw);
  return {
    id: str(o.id) ?? "",
    name: str(o.name) ?? "",
    discountType: o.discountType === "fixed" ? "fixed" : "percent",
    discountValue: int0(o.discountValue),
    startsAt: str(o.startsAt) ?? null,
    endsAt: str(o.endsAt) ?? null,
    isActive: o.isActive !== false,
    access: o.access === "code_only" ? "code_only" : "public",
    activeCampaigns: int0(o.activeCampaigns),
    locationIds: list(o.locationIds).filter((x): x is string => typeof x === "string"),
    serviceIds: list(o.serviceIds).filter((x): x is string => typeof x === "string"),
  };
}

function toSharedCoupon(raw: unknown): SharedCoupon {
  const o = obj(raw);
  return {
    id: str(o.id) ?? "",
    kind: "shared",
    promotionId: str(o.promotionId) ?? "",
    code: str(o.code) ?? "",
    validFrom: str(o.validFrom) ?? "",
    validTo: str(o.validTo) ?? "",
    ...put("maxUses", num(o.maxUses)),
    usesCount: int0(o.usesCount),
    status: o.status === "revoked" ? "revoked" : "active",
  };
}

/** Персональні купони: лише `codeHint` (≤ 3 символи); навіть якщо сервер надішле `code`, у UI він не потрапляє. */
function toClientCoupon(raw: unknown): ClientCoupon {
  const o = obj(raw);
  const p = obj(o.promotion);
  const c = obj(o.campaign);
  return {
    id: str(o.id) ?? "",
    kind: o.kind === "shared" ? "shared" : "personal",
    codeHint: (str(o.codeHint) ?? "").slice(-3),
    promotion: { id: str(p.id) ?? "", name: str(p.name) ?? "" },
    ...(str(c.id) ? { campaign: { id: str(c.id)!, name: str(c.name) ?? "" } } : {}),
    validFrom: str(o.validFrom) ?? "",
    validTo: str(o.validTo) ?? "",
    status: o.status === "revoked" ? "revoked" : "active",
    ...put("usedAt", str(o.usedAt)),
    ...put("appointmentId", str(o.appointmentId)),
  };
}

function toCheck(raw: unknown): CouponCheck {
  const o = obj(raw);
  return {
    outcome: oneOf<CouponCheck["outcome"]>(o.outcome, ["applied", "not_applicable", "not_better"], "not_applicable"),
    priceOriginal: int0(o.priceOriginal),
    priceFinal: int0(o.priceFinal),
    couponId: str(o.couponId) ?? "",
    promotionName: str(o.promotionName) ?? "",
  };
}

/** `AppointmentDto.coupon` (§26.11.4): без повного коду; `codeHint` - лише останні 3 символи. */
export function toAppointmentCoupon(raw: unknown): AppointmentCoupon | undefined {
  const o = obj(raw);
  if (!str(o.id)) return undefined;
  const hint = str(o.codeHint)?.slice(-3);
  return {
    id: str(o.id)!,
    kind: o.kind === "shared" ? "shared" : "personal",
    ...(hint ? { codeHint: hint } : {}),
    promotionName: str(o.promotionName) ?? "",
    outcome: oneOf<CouponOutcome>(o.outcome, ["applied", "not_applicable", "not_better", "none"], "applied"),
  };
}

function toConsent(raw: unknown): MarketingConsent {
  const o = obj(raw);
  const channels: ConsentChannelState[] = list(o.channels).map((c) => {
    const x = obj(c);
    return {
      channel: x.channel === "push" ? "push" : "telegram",
      level: oneOf<ConsentLevel>(x.level, LEVELS, "none"),
      state: x.state === "set" ? "set" : "unset",
      ...(SOURCES.includes(x.source as ConsentSource) ? { source: x.source as ConsentSource } : {}),
      ...put("textVersion", str(x.textVersion)),
      ...put("updatedAt", str(x.updatedAt)),
      ...put("updatedBy", actor(x.updatedBy)),
    };
  });
  const history: ConsentEvent[] = list(o.history)
    .slice(0, 50)
    .map((e) => {
      const x = obj(e);
      return {
        at: str(x.at) ?? "",
        channel: x.channel === "push" ? "push" : "telegram",
        ...(LEVELS.includes(x.levelFrom as ConsentLevel) ? { levelFrom: x.levelFrom as ConsentLevel } : {}),
        levelTo: oneOf<ConsentLevel>(x.levelTo, LEVELS, "none"),
        source: oneOf<ConsentSource>(x.source, SOURCES, "chat"),
        ...(VIAS.includes(x.via as ConsentVia) ? { via: x.via as ConsentVia } : {}),
        ...put("actor", actor(x.actor)),
        ...(BASES.includes(x.basis as ConsentBasis) ? { basis: x.basis as ConsentBasis } : {}),
        ...put("basisNote", str(x.basisNote)),
        ...put("campaignId", str(x.campaignId)),
      };
    });
  return {
    channels,
    ...put("offeredAt", str(o.offeredAt)),
    telegramAvailable: o.telegramAvailable === true,
    history,
    ...(typeof o.adminGrantEnabled === "boolean" ? { adminGrantEnabled: o.adminGrantEnabled } : {}),
  };
}

const enc = encodeURIComponent;
const send = <T>(method: "POST" | "PUT" | "PATCH", path: string, body?: unknown, headers?: Record<string, string>) =>
  apiJson<T>(`${B}${path}`, { method, ...(headers ? { headers } : {}), body: body === undefined ? undefined : JSON.stringify(body), cache: "no-store" });

type MarketingApi = Pick<
  BeautyAdminApi,
  | "getPromotions"
  | "getCoupons"
  | "createCoupon"
  | "revokeCoupon"
  | "getClientCoupons"
  | "checkCoupon"
  | "previewAudience"
  | "getCampaigns"
  | "getCampaign"
  | "createCampaign"
  | "updateCampaign"
  | "deleteCampaign"
  | "getCampaignPreview"
  | "confirmCampaign"
  | "cancelCampaign"
  | "getCampaignStats"
  | "getCampaignCouponSummary"
  | "getMarketingConsent"
  | "setMarketingConsent"
>;

export const httpMarketingApi: MarketingApi = {
  getPromotions: async () => list(await apiJson<unknown>(`${B}/promotions`, NO_STORE)).map(toPromotion),
  getCoupons: async (q) => {
    const p = new URLSearchParams({ kind: "shared" });
    if (q?.promotionId) p.set("promotionId", q.promotionId);
    return list(await apiJson<unknown>(`${B}/coupons?${p}`, NO_STORE)).map(toSharedCoupon);
  },
  createCoupon: async (input) => toSharedCoupon(await send("POST", "/coupons", input)),
  revokeCoupon: async (id) => toSharedCoupon(await send("PATCH", `/coupons/${enc(id)}`, { status: "revoked" })),
  getClientCoupons: async (clientId) => list(await apiJson<unknown>(`${B}/clients/${enc(clientId)}/coupons`, NO_STORE)).map(toClientCoupon),
  checkCoupon: async (input) => toCheck(await send("POST", "/coupons/check", input)),

  previewAudience: async (q) => toAudience(await send("POST", "/marketing/audience/preview", q)),
  getCampaigns: async (q) => {
    const p = new URLSearchParams();
    if (q?.status) p.set("status", q.status);
    if (q?.cursor) p.set("cursor", q.cursor);
    p.set("take", String(Math.min(50, Math.max(1, q?.take ?? 50))));
    const d = obj(await apiJson<unknown>(`${B}/campaigns?${p}`, NO_STORE));
    return { items: list(d.items).map(toListItem), ...put("nextCursor", str(d.nextCursor)) };
  },
  getCampaign: async (id) => toCampaignDto(await apiJson<unknown>(`${B}/campaigns/${enc(id)}`, NO_STORE)),
  createCampaign: async (r: CampaignRequest) => toCampaignDto(await send("POST", "/campaigns", r)),
  updateCampaign: async (id, r) => toCampaignDto(await send("PUT", `/campaigns/${enc(id)}`, r)),
  deleteCampaign: async (id) => {
    await apiJson<void>(`${B}/campaigns/${enc(id)}`, { method: "DELETE", cache: "no-store" });
  },
  getCampaignPreview: async (id) => toPreview(await apiJson<unknown>(`${B}/campaigns/${enc(id)}/preview`, NO_STORE)),
  confirmCampaign: async (id, input, idempotencyKey) =>
    toCampaignDto(await send("POST", `/campaigns/${enc(id)}/confirm`, { revision: input.revision, ...(input.scheduledAt ? { scheduledAt: input.scheduledAt } : {}) }, { "Idempotency-Key": idempotencyKey })),
  cancelCampaign: async (id) => toCampaignDto(await send("POST", `/campaigns/${enc(id)}/cancel`)),
  getCampaignStats: async (id) => toStats(await apiJson<unknown>(`${B}/campaigns/${enc(id)}/stats`, NO_STORE)),
  getCampaignCouponSummary: async (id): Promise<CouponSummary> => {
    const o = obj(await apiJson<unknown>(`${B}/campaigns/${enc(id)}/coupons/summary`, NO_STORE));
    return { issued: int0(o.issued), redeemedActive: int0(o.redeemedActive), released: int0(o.released), revoked: int0(o.revoked) };
  },

  getMarketingConsent: async (clientId) => toConsent(await apiJson<unknown>(`${B}/clients/${enc(clientId)}/marketing-consent`, NO_STORE)),
  setMarketingConsent: async (clientId, input) =>
    toConsent(
      await send("PUT", `/clients/${enc(clientId)}/marketing-consent`, {
        channel: input.channel,
        level: input.level,
        ...(input.basis ? { basis: input.basis, basisNote: input.basisNote } : {}),
      }),
    ),
};
