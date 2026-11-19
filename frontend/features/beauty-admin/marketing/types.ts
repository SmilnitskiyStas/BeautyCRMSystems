/**
 * Типи §26 (ADR-015): маркетингова згода, розсилки, аудиторії, купони. Форми - за контрактом `.claude/docs/beauty-contracts.md` §26.11.
 * У типах немає імен, телефонів і chat id одержувачів: перегляд і статистика - лише кількості.
 */

export type PromoAccess = "public" | "code_only";

// ---------- Згода (§26.4, §26.11.2) ----------

export type ConsentLevel = "full" | "selective" | "none";
export type ConsentChannel = "telegram" | "push";
export type ConsentSource = "chat" | "admin" | "stop";
export type ConsentVia = "button" | "command" | "text" | "api";
export type ConsentBasis = "written" | "in_person" | "other";

export interface ConsentActor {
  id: string;
  name: string;
}

export interface ConsentChannelState {
  channel: ConsentChannel;
  level: ConsentLevel;
  /** `unset` - рядка згоди немає («не обирав»). */
  state: "unset" | "set";
  source?: ConsentSource;
  textVersion?: string;
  updatedAt?: string;
  updatedBy?: ConsentActor;
}

export interface ConsentEvent {
  at: string;
  channel: ConsentChannel;
  levelFrom?: ConsentLevel;
  levelTo: ConsentLevel;
  source: ConsentSource;
  via?: ConsentVia;
  actor?: ConsentActor;
  basis?: ConsentBasis;
  /** Лише owner (за контрактом). Не логується. */
  basisNote?: string;
  campaignId?: string;
}

export interface MarketingConsent {
  channels: ConsentChannelState[];
  offeredAt?: string;
  telegramAvailable: boolean;
  /** До 50 подій, нові першими. */
  history: ConsentEvent[];
  /**
   * Припущення: сервер може віддати `Marketing:AdminGrantEnabled`. `false` - підвищення недоступне;
   * відсутнє - інтерфейс пробує, а `409 admin_grant_disabled` ховає форму.
   */
  adminGrantEnabled?: boolean;
}

export interface ConsentInput {
  channel: "telegram";
  level: ConsentLevel;
  basis?: ConsentBasis;
  basisNote?: string;
}

// ---------- Акції (адитивно §26.11.3) ----------

export type DiscountType = "percent" | "fixed";

export interface PromotionItem {
  id: string;
  name: string;
  discountType: DiscountType;
  discountValue: number;
  startsAt: string | null;
  endsAt: string | null;
  isActive: boolean;
  access: PromoAccess;
  /** Кількість кампаній у `scheduled|sending`. */
  activeCampaigns: number;
  locationIds: string[];
  serviceIds: string[];
}

// ---------- Купони (§26.11.4) ----------

export type CouponStatus = "active" | "revoked";

export interface SharedCoupon {
  id: string;
  kind: "shared";
  promotionId: string;
  /** Загальний код публічний за природою: бачать лише owner/admin цього розділу. */
  code: string;
  validFrom: string;
  validTo: string;
  maxUses?: number;
  usesCount: number;
  status: CouponStatus;
}

export interface CouponInput {
  promotionId: string;
  code: string;
  validFrom: string;
  validTo: string;
  maxUses?: number;
}

export interface ClientCoupon {
  id: string;
  kind: "personal" | "shared";
  /** Останні 3 символи нормалізованого коду; повного коду немає ніколи. */
  codeHint: string;
  promotion: { id: string; name: string };
  campaign?: { id: string; name: string };
  validFrom: string;
  validTo: string;
  status: CouponStatus;
  usedAt?: string;
  appointmentId?: string;
}

export type CouponOutcome = "applied" | "not_applicable" | "not_better" | "none";

export interface CouponCheckInput {
  code: string;
  locationId: string;
  serviceId: string;
  clientId?: string;
}

export interface CouponCheck {
  outcome: Exclude<CouponOutcome, "none">;
  priceOriginal: number;
  priceFinal: number;
  couponId: string;
  promotionName: string;
}

/** Позначка на записі (`AppointmentDto.coupon`): без повного коду. */
export interface AppointmentCoupon {
  id: string;
  kind: "personal" | "shared";
  /** Лише керівникам. */
  codeHint?: string;
  promotionName: string;
  outcome: CouponOutcome;
}

export interface CouponSummary {
  issued: number;
  redeemedActive: number;
  released: number;
  revoked: number;
}

// ---------- Кампанії (§26.5, §26.7, §26.11.3) ----------

export type CampaignType = "promo" | "news";
export type CampaignStatus = "draft" | "scheduled" | "sending" | "done" | "cancelled";
export type SegmentKind = "lapsed" | "frequent" | "new" | "all_consented";
export type CouponMode = "none" | "personal" | "shared";

export interface Segment {
  kind: SegmentKind;
  /** lapsed: 1..730 */
  days?: number;
  /** lapsed: послуга (необов'язково) */
  serviceId?: string;
  /** frequent: 1..100 */
  minVisits?: number;
  /** frequent: 1..730 */
  periodDays?: number;
}

export interface CampaignRequest {
  name: string;
  type: CampaignType;
  promotionId?: string;
  segment: Segment;
  locationId?: string;
  template: string;
  couponMode: CouponMode;
  sharedCouponId?: string;
  couponValidFrom?: string;
  couponValidTo?: string;
}

export interface CampaignActor {
  id: string;
  name: string;
}

export interface CampaignDto extends CampaignRequest {
  id: string;
  status: CampaignStatus;
  revision: number;
  scheduledAt?: string;
  recipientsTotal?: number;
  confirmedBy?: CampaignActor;
  confirmedAt?: string;
  cancelledBy?: CampaignActor;
  cancelledAt?: string;
  startedAt?: string;
  finishedAt?: string;
  createdBy: CampaignActor;
  createdAt: string;
}

export interface CampaignListItem {
  id: string;
  name: string;
  type: CampaignType;
  status: CampaignStatus;
  promotion?: { id: string; name: string };
  scheduledAt?: string;
  recipientsTotal?: number;
  sent: number;
  createdAt: string;
}

export interface CampaignListQuery {
  status?: CampaignStatus;
  cursor?: string;
  take?: number;
}

export interface CampaignList {
  items: CampaignListItem[];
  nextCursor?: string;
}

export interface AudienceQueryInput {
  type: CampaignType;
  promotionId?: string;
  segment: Segment;
  locationId?: string;
}

export interface ExcludedCounts {
  noTelegram: number;
  unsubscribed: number;
  noConsent: number;
  levelMismatch: number;
  frequency: number;
}

/** Лише кількості: імен, id і телефонів немає. */
export interface AudiencePreview {
  matched: number;
  recipients: number;
  excluded: ExcludedCounts;
}

export type PreviewWarning = "promotion_ends_soon" | "coupon_expires_before_send" | "exceeds_daily_limit" | "no_buttons_channel";

export interface CampaignPreview extends AudiencePreview {
  revision: number;
  sampleText: string;
  buttons: string[];
  limits: { dailyLimit: number; clientIntervalDays: number; estimatedDays: number };
  window: { from: string; to: string };
  warnings: PreviewWarning[];
}

export interface ConfirmCampaignInput {
  revision: number;
  scheduledAt?: string;
}

export interface CampaignStats {
  recipients: number;
  byStatus: { pending: number; queued: number; sent: number; failed: number; skipped: number };
  failed: { botBlocked: number; chatNotFound: number; other: number };
  skipped: Record<string, number>;
  clicks: number;
  bookings: number;
  bookingsWithCoupon: number;
  couponRedemptionsActive: number;
  revenue: number;
  unsubscribedAfter: number;
  /** Telegram не віддає «переглянуто»: завжди `null`. */
  viewed: null;
}
