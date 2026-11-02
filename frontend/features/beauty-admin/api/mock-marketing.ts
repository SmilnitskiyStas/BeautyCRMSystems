import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CLIENT_SEED, CONTACT_PREFS_SEED } from "../../beauty-shared/demo-seed";
import { CAMPAIGNS_SEED, CLIENT_COUPONS_SEED, CONSENT_SEED, PROMOTIONS_SEED, SHARED_COUPONS_SEED } from "../../beauty-shared/marketing-seed";
import {
  BASIS_NOTE_MAX,
  MAX_RECIPIENTS,
  dmy,
  formatDiscount,
  normalizeCode,
  normalizeSegment,
  sampleButtons,
  sampleMessage,
  scheduleError,
  sharedCodeError,
  validateName,
  validateSegment,
  validateTemplate,
} from "../marketing/logic";
import type {
  Appointment,
  AppointmentCoupon,
  AudiencePreview,
  AudienceQueryInput,
  CampaignDto,
  CampaignList,
  CampaignListItem,
  CampaignListQuery,
  CampaignPreview,
  CampaignRequest,
  CampaignStats,
  ClientCoupon,
  ConfirmCampaignInput,
  ConsentBasis,
  ConsentEvent,
  ConsentInput,
  ConsentLevel,
  CouponCheck,
  CouponCheckInput,
  CouponInput,
  CouponSummary,
  MarketingConsent,
  PreviewWarning,
  PromoDraft,
  PromotionItem,
  SharedCoupon,
} from "../types";

const DAY = 86_400_000;
const HOUR = 3_600_000;
const DAILY_LIMIT = 200;
const INTERVAL_DAYS = 7;

export interface MarketingDeps {
  role: () => string;
  now?: () => number;
  /** `false` - без затримки (тести). */
  wait?: boolean;
  /** `Marketing:Enabled` (за замовчуванням - вмикнено, окрім `NEXT_PUBLIC_MOCK_MARKETING=off`). */
  marketingEnabled?: () => boolean;
  /** `Marketing:AdminGrantEnabled` (за замовчуванням mock вмикає, щоб показати форму; `NEXT_PUBLIC_MOCK_ADMIN_GRANT=0` вимикає). */
  adminGrantEnabled?: () => boolean;
  services: () => { id: string; name: string }[];
  locations: () => { id: string; name: string; timezone?: string }[];
  /** Сирі демо-записи (без перенесень і скасувань) - для запису за купоном. */
  appointments: () => Appointment[];
  priceOf?: (serviceId: string) => number;
  userName?: string;
  /** Підміна аудиторії (тести). */
  audience?: (q: AudienceQueryInput) => AudiencePreview;
}

const err = (status: number, code: string, retry?: number) => new BeautyApiError(status, code, code, retry);
const RANK: Record<ConsentLevel, number> = { none: 0, selective: 1, full: 2 };
const BASES: ConsentBasis[] = ["written", "in_person", "other"];

interface CampaignRec {
  dto: CampaignDto;
  stats?: CampaignStats;
  summary?: CouponSummary;
  confirm?: { key: string; fingerprint: string };
}

interface ConsentRec {
  level: ConsentLevel;
  source?: "chat" | "admin" | "stop";
  textVersion?: string;
  updatedAt?: number;
  updatedBy?: { id: string; name: string };
  offeredAt?: number;
  history: ConsentEvent[];
}

const zeroStats = (recipients: number, pending = 0): CampaignStats => ({
  recipients,
  byStatus: { pending, queued: 0, sent: 0, failed: 0, skipped: 0 },
  failed: { botBlocked: 0, chatNotFound: 0, other: 0 },
  skipped: {},
  clicks: 0,
  bookings: 0,
  bookingsWithCoupon: 0,
  couponRedemptionsActive: 0,
  revenue: 0,
  unsubscribedAfter: 0,
  viewed: null,
});

/** Детермінована оцінка аудиторії (mock): без імен і ПД; `recipients + Σ excluded = matched`. */
export function estimateAudience(q: AudienceQueryInput): AudiencePreview {
  const s = q.segment;
  let matched: number;
  switch (s.kind) {
    case "lapsed":
      matched = Math.round(220 / (1 + (s.days ?? 60) / 45)) * (s.serviceId ? 0.4 : 1);
      break;
    case "frequent":
      matched = (160 / (1 + ((s.minVisits ?? 3) - 1) * 0.5)) * Math.min(1, (s.periodDays ?? 90) / 90);
      break;
    case "new":
      matched = 64;
      break;
    default:
      matched = 240;
  }
  if (q.locationId) matched *= 0.45;
  matched = Math.max(0, Math.round(matched));
  const part = (p: number) => Math.floor(matched * p);
  const excluded = {
    noTelegram: part(0.12),
    unsubscribed: part(0.06),
    noConsent: part(0.32),
    levelMismatch: part(q.type === "news" ? 0.18 : 0.07),
    frequency: part(0.05),
  };
  const sum = excluded.noTelegram + excluded.unsubscribed + excluded.noConsent + excluded.levelMismatch + excluded.frequency;
  return { matched, recipients: matched - sum, excluded };
}

export function createMockMarketing(deps: MarketingDeps) {
  const now = deps.now ?? (() => Date.now());
  const isManager = () => deps.role() === "owner" || deps.role() === "admin";
  const isOwner = () => deps.role() === "owner";
  const enabled = deps.marketingEnabled ?? (() => process.env.NEXT_PUBLIC_MOCK_MARKETING !== "off");
  const grantEnabled = deps.adminGrantEnabled ?? (() => process.env.NEXT_PUBLIC_MOCK_ADMIN_GRANT !== "0");
  const me = { id: "mock-user", name: deps.userName ?? "Світлана Коваленко" };
  const iso = (ms: number) => new Date(ms).toISOString();
  const wait = async <T>(v: T): Promise<T> => {
    if (deps.wait !== false) await new Promise((r) => setTimeout(r, 120));
    return structuredClone(v);
  };
  const t0 = now();

  // ---------- ліміти (in-memory, як на сервері) ----------
  const hits: Record<string, number[]> = {};
  function limit(kind: "write" | "preview" | "check", max: number) {
    const t = now();
    const list = (hits[kind] ??= []).filter((x) => t - x < 60_000);
    hits[kind] = list;
    if (list.length >= max) throw err(429, "rate_limited", 30);
    list.push(t);
  }

  const guardManager = () => {
    if (!isManager()) throw err(403, "forbidden_role");
  };
  const guardMarketing = () => {
    guardManager();
    if (!enabled()) throw err(403, "marketing_disabled");
  };

  // ---------- акції ----------
  const promotions: PromotionItem[] = PROMOTIONS_SEED.map((p) => ({
    id: p.id,
    name: p.name,
    discountType: p.discountType,
    discountValue: p.discountValue,
    startsAt: iso(t0 - p.startsDaysAgo * DAY),
    endsAt: p.endsInDays === null ? null : iso(t0 + p.endsInDays * DAY),
    isActive: p.isActive,
    access: p.access,
    activeCampaigns: 0,
    locationIds: [...p.locationIds],
    serviceIds: [...p.serviceIds],
  }));

  // ---------- купони ----------
  const coupons: SharedCoupon[] = SHARED_COUPONS_SEED.map((c) => ({
    id: c.id,
    kind: "shared",
    promotionId: c.promotionId,
    code: c.code,
    validFrom: iso(t0 - c.validFromDaysAgo * DAY),
    validTo: iso(t0 + c.validToInDays * DAY),
    ...(c.maxUses ? { maxUses: c.maxUses } : {}),
    usesCount: c.usesCount,
    status: c.status,
  }));

  // ---------- кампанії ----------
  const campaigns: CampaignRec[] = CAMPAIGNS_SEED.map((s) => {
    const dto: CampaignDto = {
      id: s.id,
      name: s.name,
      type: s.type,
      ...(s.promotionId ? { promotionId: s.promotionId } : {}),
      segment: structuredClone(s.segment),
      ...(s.locationId ? { locationId: s.locationId } : {}),
      template: s.template,
      couponMode: s.couponMode,
      ...(s.sharedCouponId ? { sharedCouponId: s.sharedCouponId } : {}),
      ...(s.couponMode === "personal"
        ? { couponValidFrom: iso(t0 - s.createdDaysAgo * DAY), couponValidTo: iso(t0 - s.createdDaysAgo * DAY + (s.couponValidInDays ?? 20) * DAY) }
        : {}),
      status: s.status,
      revision: s.revision,
      ...(s.scheduledInHours !== undefined ? { scheduledAt: iso(t0 + s.scheduledInHours * HOUR) } : {}),
      ...(s.recipientsTotal !== undefined ? { recipientsTotal: s.recipientsTotal } : {}),
      ...(s.status !== "draft" ? { confirmedBy: me, confirmedAt: iso(t0 - s.createdDaysAgo * DAY + 3600_000) } : {}),
      ...(s.status === "cancelled" ? { cancelledBy: me, cancelledAt: iso(t0 - 24 * HOUR) } : {}),
      ...(s.status === "sending" || s.status === "done" ? { startedAt: iso(t0 + (s.scheduledInHours ?? 0) * HOUR) } : {}),
      ...(s.status === "done" ? { finishedAt: iso(t0 - 4 * DAY) } : {}),
      createdBy: me,
      createdAt: iso(t0 - s.createdDaysAgo * DAY),
    };
    return {
      dto,
      stats: s.stats
        ? { recipients: s.recipientsTotal ?? 0, ...s.stats, skipped: { ...s.stats.skipped }, viewed: null }
        : s.status === "scheduled"
          ? zeroStats(s.recipientsTotal ?? 0, s.recipientsTotal ?? 0)
          : undefined,
      summary: s.couponSummary,
    };
  });

  const activeCampaignsOf = (promotionId: string) =>
    campaigns.filter((c) => c.dto.promotionId === promotionId && (c.dto.status === "scheduled" || c.dto.status === "sending")).length;
  const promoOf = (id?: string) => promotions.find((p) => p.id === id);
  const serviceNames = (ids: string[]) => ids.map((id) => deps.services().find((s) => s.id === id)?.name).filter((n): n is string => !!n);
  const audienceFor = (q: AudienceQueryInput) => (deps.audience ? deps.audience(q) : estimateAudience(q));
  const find = (id: string): CampaignRec => {
    const c = campaigns.find((x) => x.dto.id === id);
    if (!c) throw err(404, "campaign_not_found");
    return c;
  };

  function validate(r: CampaignRequest): void {
    const nameErr = validateName(r.name);
    if (nameErr) throw err(422, "invalid_name");
    if (r.type !== "promo" && r.type !== "news") throw err(422, "invalid_type");
    if (validateSegment(r.segment) || !["lapsed", "frequent", "new", "all_consented"].includes(r.segment.kind)) throw err(422, "invalid_segment");
    if (r.segment.serviceId && !deps.services().some((s) => s.id === r.segment.serviceId)) throw err(422, "service_not_found");
    if (r.locationId && !deps.locations().some((l) => l.id === r.locationId)) throw err(422, "location_not_found");
    if (!["none", "personal", "shared"].includes(r.couponMode)) throw err(422, "invalid_coupon_mode");
    if (r.type === "news") {
      if (r.promotionId || r.couponMode !== "none") throw err(422, "invalid_coupon_mode");
    } else {
      if (!r.promotionId) throw err(422, "promotion_required");
      const promo = promoOf(r.promotionId);
      if (!promo) throw err(422, "promotion_not_found");
      if (r.couponMode === "none" && promo.access !== "public") throw err(422, "promotion_must_be_public");
      if (r.couponMode !== "none" && promo.access !== "code_only") throw err(422, "promotion_not_code_only");
      if (r.couponMode === "personal") {
        if (!r.couponValidTo) throw err(422, "invalid_coupon_mode");
        const from = r.couponValidFrom ? Date.parse(r.couponValidFrom) : now();
        const to = Date.parse(r.couponValidTo);
        if (!(to > from)) throw err(422, "invalid_period");
        if ((promo.startsAt && from < Date.parse(promo.startsAt) - DAY) || (promo.endsAt && to > Date.parse(promo.endsAt) + DAY)) {
          throw err(422, "coupon_period_outside_promotion");
        }
      }
      if (r.couponMode === "shared") {
        const c = coupons.find((x) => x.id === r.sharedCouponId);
        if (!c) throw err(422, "coupon_not_found");
        if (c.promotionId !== r.promotionId) throw err(422, "coupon_promotion_mismatch");
      }
    }
    if (validateTemplate(r.template, { type: r.type, couponMode: r.couponMode })) throw err(422, "invalid_template");
  }

  const toItem = (c: CampaignRec): CampaignListItem => {
    const d = c.dto;
    const p = promoOf(d.promotionId);
    return {
      id: d.id,
      name: d.name,
      type: d.type,
      status: d.status,
      ...(p ? { promotion: { id: p.id, name: p.name } } : {}),
      ...(d.scheduledAt ? { scheduledAt: d.scheduledAt } : {}),
      ...(d.recipientsTotal !== undefined ? { recipientsTotal: d.recipientsTotal } : {}),
      sent: c.stats?.byStatus.sent ?? 0,
      createdAt: d.createdAt,
    };
  };

  function previewOf(c: CampaignRec): CampaignPreview {
    const d = c.dto;
    const promo = promoOf(d.promotionId);
    const a = audienceFor({ type: d.type, promotionId: d.promotionId, segment: d.segment, locationId: d.locationId });
    const shared = coupons.find((x) => x.id === d.sharedCouponId);
    const validFrom = d.couponMode === "personal" ? d.couponValidFrom : (shared?.validFrom ?? promo?.startsAt ?? undefined);
    const validTo = d.couponMode === "personal" ? d.couponValidTo : (shared?.validTo ?? promo?.endsAt ?? undefined);
    const tz = deps.locations().find((l) => l.id === d.locationId)?.timezone ?? deps.locations()[0]?.timezone;
    const estimatedDays = Math.max(1, Math.ceil(a.recipients / DAILY_LIMIT));
    const warnings: PreviewWarning[] = [];
    if (promo?.endsAt && Date.parse(promo.endsAt) - now() < 3 * DAY) warnings.push("promotion_ends_soon");
    if (validTo && Date.parse(validTo) < now() + estimatedDays * DAY) warnings.push("coupon_expires_before_send");
    if (a.recipients > DAILY_LIMIT) warnings.push("exceeds_daily_limit");
    return {
      revision: d.revision,
      sampleText: sampleMessage(
        { type: d.type, couponMode: d.couponMode, template: d.template },
        {
          ...(promo ? { discount: formatDiscount(promo.discountType, promo.discountValue), services: serviceNames(promo.serviceIds) } : {}),
          ...(validFrom ? { validFrom: dmy(validFrom, tz) } : {}),
          ...(validTo ? { validTo: dmy(validTo, tz, true) } : {}),
          ...(d.couponMode === "shared" && shared ? { code: shared.code } : {}),
        },
      ),
      buttons: sampleButtons(d.couponMode),
      ...a,
      limits: { dailyLimit: DAILY_LIMIT, clientIntervalDays: INTERVAL_DAYS, estimatedDays },
      window: { from: "09:00", to: "20:00" },
      warnings,
    };
  }

  // ---------- згоди ----------
  const consents = new Map<string, ConsentRec>();
  for (const s of CONSENT_SEED) {
    const events: ConsentEvent[] = s.events.map((e) => ({
      at: iso(t0 - e.daysAgo * DAY),
      channel: "telegram",
      ...(e.levelFrom ? { levelFrom: e.levelFrom } : {}),
      levelTo: e.levelTo,
      source: e.source,
      ...(e.via ? { via: e.via } : {}),
      ...(e.admin ? { actor: me } : {}),
      ...(e.basis ? { basis: e.basis } : {}),
      ...(e.basisNote ? { basisNote: e.basisNote } : {}),
      ...(e.campaignId ? { campaignId: e.campaignId } : {}),
    }));
    const last = s.events[0];
    consents.set(s.clientId, {
      level: last?.levelTo ?? "none",
      ...(last ? { source: last.source, ...(last.source === "chat" ? { textVersion: "mc-v1" } : {}), updatedAt: t0 - last.daysAgo * DAY, ...(last.admin ? { updatedBy: me } : {}) } : {}),
      ...(s.offeredDaysAgo !== undefined ? { offeredAt: t0 - s.offeredDaysAgo * DAY } : {}),
      history: events,
    });
  }

  function consentView(clientId: string): MarketingConsent {
    const c = consents.get(clientId) ?? { level: "none" as ConsentLevel, history: [] };
    const seed = CONTACT_PREFS_SEED.find((x) => x.clientId === clientId);
    return {
      channels: [
        {
          channel: "telegram",
          level: c.level,
          state: c.source ? "set" : "unset",
          ...(c.source ? { source: c.source } : {}),
          ...(c.textVersion ? { textVersion: c.textVersion } : {}),
          ...(c.updatedAt ? { updatedAt: iso(c.updatedAt) } : {}),
          ...(c.updatedBy ? { updatedBy: c.updatedBy } : {}),
        },
      ],
      ...(c.offeredAt ? { offeredAt: iso(c.offeredAt) } : {}),
      telegramAvailable: seed?.telegram ?? false,
      // `basisNote` віддається лише owner (§26.11.2).
      history: c.history.slice(0, 50).map((e) => {
        const { basisNote, ...rest } = e;
        return isOwner() && basisNote ? { ...rest, basisNote } : rest;
      }),
      adminGrantEnabled: grantEnabled(),
    };
  }

  // ---------- записи за купоном (демо) ----------
  let apptCoupons: Map<string, { coupon: AppointmentCoupon; discount: number }> | null = null;
  function couponMap() {
    if (apptCoupons) return apptCoupons;
    apptCoupons = new Map();
    const vip = promotions.find((p) => p.id === "vip15")!;
    const pick = (clientId: string) =>
      deps
        .appointments()
        .filter((a) => a.clientId === clientId && a.status !== "cancelled")
        .sort((a, b) => a.startsAt.localeCompare(b.startsAt))[0];
    const oc = pick("oc");
    const tg = pick("tg");
    if (oc) apptCoupons.set(oc.id, { coupon: { id: "pc1", kind: "personal", codeHint: "M3D", promotionName: vip.name, outcome: "applied" }, discount: vip.discountValue });
    if (tg) apptCoupons.set(tg.id, { coupon: { id: "cp1", kind: "shared", codeHint: "N15", promotionName: vip.name, outcome: "applied" }, discount: vip.discountValue });
    return apptCoupons;
  }

  const priceOf = deps.priceOf ?? (() => 1000);

  return {
    // ----- вигляд для запису в календарі / картці клієнта -----
    /** Позначка купона на записі; `codeHint` лише керівникам. Ціни перераховує mock-клієнт. */
    couponFor(a: Pick<Appointment, "id">): { coupon: AppointmentCoupon; discountPercent: number } | undefined {
      const m = couponMap().get(a.id);
      if (!m) return undefined;
      const { codeHint, ...rest } = m.coupon;
      return { coupon: isManager() && codeHint ? { ...rest, codeHint } : rest, discountPercent: m.discount };
    },
    registerPromotion(draft: PromoDraft): { id: string } {
      const parse = (d: string, end: boolean) => {
        const m = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(d.trim());
        return m ? new Date(`${m[3]}-${m[2]}-${m[1]}T${end ? "23:59:59" : "00:00:00"}Z`).toISOString() : null;
      };
      const id = `promo-${promotions.length + 1}-${now()}`;
      promotions.push({
        id,
        name: draft.name,
        discountType: "percent",
        discountValue: draft.percent,
        startsAt: parse(draft.from, false),
        endsAt: parse(draft.to, true),
        isActive: true,
        access: draft.access ?? "public",
        activeCampaigns: 0,
        locationIds: [...draft.locationIds],
        serviceIds: [...draft.serviceIds],
      });
      return { id };
    },

    getPromotions: async (): Promise<PromotionItem[]> => {
      guardManager();
      return wait(promotions.map((p) => ({ ...p, activeCampaigns: activeCampaignsOf(p.id) })));
    },

    // ----- купони -----
    getCoupons: async (q?: { promotionId?: string }): Promise<SharedCoupon[]> => {
      guardMarketing();
      return wait(coupons.filter((c) => !q?.promotionId || c.promotionId === q.promotionId));
    },
    createCoupon: async (input: CouponInput): Promise<SharedCoupon> => {
      guardMarketing();
      limit("write", 30);
      if (sharedCodeError(input.code)) throw err(422, "invalid_code");
      const from = Date.parse(input.validFrom);
      const to = Date.parse(input.validTo);
      if (Number.isNaN(from) || Number.isNaN(to) || to <= from) throw err(422, "invalid_period");
      if (input.maxUses !== undefined && !(Number.isInteger(input.maxUses) && input.maxUses >= 1 && input.maxUses <= 100000)) throw err(422, "invalid_max_uses");
      const promo = promoOf(input.promotionId);
      if (!promo) throw err(404, "promotion_not_found");
      if (promo.access !== "code_only") throw err(422, "promotion_not_code_only");
      if (coupons.some((c) => normalizeCode(c.code) === normalizeCode(input.code))) throw err(409, "coupon_code_taken");
      const created: SharedCoupon = {
        id: `cp-${coupons.length + 1}-${now()}`,
        kind: "shared",
        promotionId: input.promotionId,
        code: input.code.trim().toUpperCase(),
        validFrom: input.validFrom,
        validTo: input.validTo,
        ...(input.maxUses !== undefined ? { maxUses: input.maxUses } : {}),
        usesCount: 0,
        status: "active",
      };
      coupons.push(created);
      return wait(created);
    },
    revokeCoupon: async (id: string): Promise<SharedCoupon> => {
      guardMarketing();
      limit("write", 30);
      const c = coupons.find((x) => x.id === id);
      if (!c) throw err(404, "coupon_not_found");
      c.status = "revoked";
      return wait(c);
    },
    getClientCoupons: async (clientId: string): Promise<ClientCoupon[]> => {
      guardMarketing();
      if (!CLIENT_SEED.some((c) => c.id === clientId)) throw err(404, "client_not_found");
      const used = couponMap();
      return wait(
        CLIENT_COUPONS_SEED.filter((c) => c.clientId === clientId).map((c) => {
          const promo = promoOf(c.promotionId);
          const camp = campaigns.find((x) => x.dto.id === c.campaignId);
          const apptId = c.used ? [...used.entries()].find(([, v]) => v.coupon.id === c.id)?.[0] : undefined;
          const a = apptId ? deps.appointments().find((x) => x.id === apptId) : undefined;
          return {
            id: c.id,
            kind: "personal" as const,
            codeHint: c.codeHint,
            promotion: { id: c.promotionId, name: promo?.name ?? "Акція" },
            ...(camp ? { campaign: { id: camp.dto.id, name: camp.dto.name } } : {}),
            validFrom: iso(t0 - 6 * DAY),
            validTo: iso(t0 + c.validToInDays * DAY),
            status: c.status,
            ...(c.used ? { usedAt: iso(t0 - 10 * DAY), ...(a ? { appointmentId: a.id } : {}) } : {}),
          };
        }),
      );
    },
    checkCoupon: async (input: CouponCheckInput): Promise<CouponCheck> => {
      guardManager();
      limit("check", 30);
      const c = coupons.find((x) => normalizeCode(x.code) === normalizeCode(input.code));
      if (!c) throw err(409, "coupon_not_found");
      if (c.status === "revoked") throw err(409, "coupon_revoked");
      if (Date.parse(c.validTo) <= now()) throw err(409, "coupon_expired");
      if (Date.parse(c.validFrom) > now()) throw err(409, "coupon_not_started");
      if (c.maxUses !== undefined && c.usesCount >= c.maxUses) throw err(409, "coupon_exhausted");
      const promo = promoOf(c.promotionId);
      if (!promo?.isActive) throw err(409, "promotion_inactive");
      const applies = promo.serviceIds.length === 0 || promo.serviceIds.includes(input.serviceId);
      const original = priceOf(input.serviceId);
      const final = Math.round(original * (promo.discountType === "percent" ? (100 - promo.discountValue) / 100 : 1) - (promo.discountType === "fixed" ? promo.discountValue : 0));
      return wait({
        outcome: applies ? "applied" : "not_applicable",
        priceOriginal: original,
        priceFinal: applies ? final : original,
        couponId: c.id,
        promotionName: promo.name,
      } as CouponCheck);
    },

    // ----- аудиторія й кампанії -----
    previewAudience: async (q: AudienceQueryInput): Promise<AudiencePreview> => {
      guardMarketing();
      limit("preview", 20);
      if (validateSegment(q.segment)) throw err(422, "invalid_segment");
      if (q.type === "promo" && q.promotionId && !promoOf(q.promotionId)) throw err(422, "promotion_not_found");
      return wait(audienceFor({ ...q, segment: normalizeSegment(q.segment) }));
    },
    getCampaigns: async (q: CampaignListQuery = {}): Promise<CampaignList> => {
      guardMarketing();
      const all = campaigns
        .filter((c) => !q.status || c.dto.status === q.status)
        .sort((a, b) => b.dto.createdAt.localeCompare(a.dto.createdAt));
      const take = Math.min(Math.max(q.take ?? 50, 1), 50);
      const start = q.cursor ? Number(q.cursor) : 0;
      if (q.cursor && (!Number.isInteger(start) || start < 0)) throw err(422, "invalid_cursor");
      const page = all.slice(start, start + take).map(toItem);
      return wait({ items: page, ...(start + take < all.length ? { nextCursor: String(start + take) } : {}) });
    },
    getCampaign: async (id: string): Promise<CampaignDto> => {
      guardMarketing();
      return wait(find(id).dto);
    },
    createCampaign: async (r: CampaignRequest): Promise<CampaignDto> => {
      guardMarketing();
      limit("write", 30);
      validate(r);
      const dto: CampaignDto = { ...structuredClone(r), id: `k${campaigns.length + 1}-${now()}`, status: "draft", revision: 1, createdBy: me, createdAt: iso(now()) };
      campaigns.push({ dto });
      return wait(dto);
    },
    updateCampaign: async (id: string, r: CampaignRequest): Promise<CampaignDto> => {
      guardMarketing();
      limit("write", 30);
      const c = find(id);
      if (c.dto.status !== "draft") throw err(409, "campaign_not_draft");
      validate(r);
      c.dto = { ...structuredClone(r), id, status: "draft", revision: c.dto.revision + 1, createdBy: c.dto.createdBy, createdAt: c.dto.createdAt };
      return wait(c.dto);
    },
    deleteCampaign: async (id: string): Promise<void> => {
      guardMarketing();
      limit("write", 30);
      const c = find(id);
      if (c.dto.status !== "draft") throw err(409, "campaign_not_draft");
      campaigns.splice(campaigns.indexOf(c), 1);
      await wait(null);
    },
    getCampaignPreview: async (id: string): Promise<CampaignPreview> => {
      guardMarketing();
      limit("preview", 20);
      return wait(previewOf(find(id)));
    },
    confirmCampaign: async (id: string, input: ConfirmCampaignInput, key: string): Promise<CampaignDto> => {
      guardMarketing();
      limit("write", 30);
      if (typeof key !== "string" || key.length < 8 || key.length > 128) throw err(400, "idempotency_key_required");
      const c = find(id);
      const fingerprint = JSON.stringify({ r: input.revision, s: input.scheduledAt ?? null });
      if (c.confirm?.key === key) {
        if (c.confirm.fingerprint !== fingerprint) throw err(409, "idempotency_conflict");
        return wait(c.dto);
      }
      if (c.dto.revision !== input.revision) throw err(409, "campaign_changed");
      if (c.dto.status !== "draft") throw err(409, "campaign_not_draft");
      if (scheduleError(input.scheduledAt, now())) throw err(422, "invalid_schedule");
      const at = input.scheduledAt ? Date.parse(input.scheduledAt) : now();
      const promo = promoOf(c.dto.promotionId);
      if (promo && (!promo.isActive || (promo.endsAt && Date.parse(promo.endsAt) <= at))) throw err(409, "promotion_inactive");
      const a = audienceFor({ type: c.dto.type, promotionId: c.dto.promotionId, segment: c.dto.segment, locationId: c.dto.locationId });
      if (a.recipients === 0) throw err(409, "audience_empty");
      if (a.recipients > MAX_RECIPIENTS) throw err(409, "audience_too_large");
      c.dto = { ...c.dto, status: "scheduled", scheduledAt: iso(at), recipientsTotal: a.recipients, confirmedBy: me, confirmedAt: iso(now()) };
      c.stats = zeroStats(a.recipients, a.recipients);
      c.confirm = { key, fingerprint };
      return wait(c.dto);
    },
    cancelCampaign: async (id: string): Promise<CampaignDto> => {
      guardMarketing();
      limit("write", 30);
      const c = find(id);
      if (c.dto.status === "cancelled") return wait(c.dto);
      if (c.dto.status === "done") throw err(409, "campaign_finished");
      c.dto = { ...c.dto, status: "cancelled", cancelledBy: me, cancelledAt: iso(now()) };
      if (c.stats) {
        const pending = c.stats.byStatus.pending;
        c.stats.byStatus.pending = 0;
        c.stats.byStatus.skipped += pending;
        if (pending > 0) c.stats.skipped.campaign_cancelled = (c.stats.skipped.campaign_cancelled ?? 0) + pending;
      }
      return wait(c.dto);
    },
    getCampaignStats: async (id: string): Promise<CampaignStats> => {
      guardMarketing();
      const c = find(id);
      return wait(c.stats ?? zeroStats(c.dto.recipientsTotal ?? 0));
    },
    getCampaignCouponSummary: async (id: string): Promise<CouponSummary> => {
      guardMarketing();
      const c = find(id);
      return wait(c.summary ?? { issued: 0, redeemedActive: 0, released: 0, revoked: 0 });
    },

    // ----- згода (працює й за `Marketing:Enabled=false`) -----
    getMarketingConsent: async (clientId: string): Promise<MarketingConsent> => {
      guardManager();
      if (!CLIENT_SEED.some((c) => c.id === clientId)) throw err(404, "client_not_found");
      return wait(consentView(clientId));
    },
    setMarketingConsent: async (clientId: string, input: ConsentInput): Promise<MarketingConsent> => {
      guardManager();
      limit("write", 30);
      if (!CLIENT_SEED.some((c) => c.id === clientId)) throw err(404, "client_not_found");
      if ((input.channel as string) === "push") throw err(422, "channel_unsupported");
      if (!(input.level in RANK)) throw err(422, "invalid_level");
      const cur = consents.get(clientId) ?? { level: "none" as ConsentLevel, history: [] as ConsentEvent[] };
      if (cur.level === input.level) return wait(consentView(clientId));
      const raise = RANK[input.level] > RANK[cur.level];
      if (raise) {
        if (!input.basis) throw err(422, "basis_required");
        if (!BASES.includes(input.basis)) throw err(422, "invalid_basis");
        const note = (input.basisNote ?? "").trim();
        if (note.length < 1 || [...note].length > BASIS_NOTE_MAX) throw err(422, "invalid_basis_note");
        if (!grantEnabled()) throw err(409, "admin_grant_disabled");
      }
      const at = now();
      const hadRow = consents.has(clientId) && !!cur.source;
      const ev: ConsentEvent = {
        at: iso(at),
        channel: "telegram",
        ...(hadRow ? { levelFrom: cur.level } : {}),
        levelTo: input.level,
        source: "admin",
        via: "api",
        actor: me,
        ...(raise && input.basis ? { basis: input.basis, basisNote: (input.basisNote ?? "").trim() } : {}),
      };
      consents.set(clientId, {
        ...cur,
        level: input.level,
        source: "admin",
        textVersion: undefined,
        updatedAt: at,
        updatedBy: me,
        history: [ev, ...cur.history].slice(0, 50),
      });
      return wait(consentView(clientId));
    },
  };
}

export type MockMarketing = ReturnType<typeof createMockMarketing>;
