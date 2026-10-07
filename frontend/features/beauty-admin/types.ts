/**
 * Доменні типи адмінки Beauty CRM.
 * Відповідають контракту `.claude/docs/beauty-contracts.md` (розділи 2, 3, 6).
 * Деталі полів уточнює backend; зміни вносити тут і в `api/`.
 */

export type LocationId = string;

export interface BeautyLocation {
  id: LocationId;
  name: string;
}

export type AppointmentStatus =
  | "pending"
  | "confirmed"
  | "in_progress"
  | "completed"
  | "cancelled"
  | "no_show";

export type AppointmentSource = "admin" | "online" | "telegram" | "instagram";

/** Тип блоку в календарі (легенда). */
export type CalendarKind = "visit" | "promo" | "new" | "online" | "break";

export interface Appointment {
  id: string;
  locationId: LocationId;
  specialistId: string;
  serviceId: string;
  clientId: string | null;
  clientName: string;
  serviceName: string;
  /** Локальний час закладу без зони: `YYYY-MM-DDTHH:mm`. */
  startsAt: string;
  /** Блок у календарі = тривалість послуги. */
  durationMinutes: number;
  status: AppointmentStatus;
  source: AppointmentSource;
  kind: CalendarKind;
  priceFinal: number;
  promotionId: string | null;
  promotionName?: string;
  /** Умови скасування запису (§11), якщо їх повернув backend. */
  cancellation?: CancellationSettings;
}

/** Політика повернення коштів (§11). */
export interface CancellationSettings {
  windowHours: number;
  refundPercentInWindow: number;
  refundPercentOutside: number;
  deductFee: boolean;
  feePercent: number;
}

export interface OverviewKpi {
  label: string;
  value: string;
  note: string;
}

export interface OverviewRow {
  id: string;
  time: string;
  clientId: string | null;
  clientName: string;
  serviceName: string;
  specialistId: string;
  specialistName: string;
  locationId: LocationId;
  locationName: string;
  status: AppointmentStatus;
}

export type ClientTag = "vip" | "new" | "sleep";

export interface ClientSummary {
  id: string;
  name: string;
  meta: string;
  tag: ClientTag;
}

export interface Overview {
  dateLabel: string;
  kpis: OverviewKpi[];
  rows: OverviewRow[];
  clients: ClientSummary[];
}

export interface ClientVisit {
  id: string;
  dateLabel: string;
  serviceName: string;
  specialistId: string;
  specialistName: string;
  locationName: string;
  sum: string;
  status: "completed" | "planned" | "cancelled";
  viaPromo: boolean;
}

export interface ClientPromoUse {
  id: string;
  name: string;
  when: string;
  saved: string;
}

export interface ClientNote {
  id: string;
  text: string;
  meta: string;
}

export interface ClientProfile {
  id: string;
  name: string;
  tag: ClientTag;
  contactLine: string;
  kpis: OverviewKpi[];
  visits: ClientVisit[];
  promos: ClientPromoUse[];
  notes: ClientNote[];
  preferences: { label: string; value: string }[];
  warning: string | null;
  loyalty: { balance: string; nextLevel: string; progressPct: number };
}

export interface StaffSummary {
  id: string;
  name: string;
  role: string;
  locations: string;
}

export interface StaffFeedItem {
  id: string;
  kind: "done" | "move" | "note" | "review" | "slot";
  kindLabel: string;
  text: string;
  when: string;
}

export interface StaffProfile {
  id: string;
  name: string;
  role: string;
  locationBadges: string[];
  kpis: OverviewKpi[];
  /** Візитів по днях за 14 днів. */
  activity: { label: string; value: number }[];
  feed: StaffFeedItem[];
  services: { name: string; duration: string; price: string; count: string }[];
  schedule: { day: string; hours: string; location: string; off: boolean }[];
  bars: { label: string; value: string; pct: number }[];
  contacts: { label: string; value: string }[];
}

export interface CalendarMaster {
  id: string;
  name: string;
  locationName: string;
}

export interface CalendarWeek {
  weekLabel: string;
  /** Понеділок тижня, `YYYY-MM-DD`. */
  weekStart: string;
  /** Підписи 7 днів, починаючи з понеділка. */
  days: string[];
  masters: CalendarMaster[];
  specialistId: string;
  appointments: Appointment[];
}

export interface PriceListRow {
  serviceId: string;
  name: string;
  durationMinutes: number;
  price: number;
}

export interface PromoDraft {
  name: string;
  percent: number;
  locationIds: LocationId[];
  serviceIds: string[];
  from: string;
  to: string;
}

export interface PromoPreview {
  rows: { serviceId: string; name: string; durationMinutes: number; price: number; promoPrice: number | null; where: string }[];
  locationSummary: string;
  samplePrice: number;
  sampleOriginal: number;
}

export type AnalyticsKpi = OverviewKpi;

export interface NetworkAnalytics {
  kpis: AnalyticsKpi[];
  weeks: { label: string; value: number }[];
  locations: { id: LocationId; label: string; value: string; pct: number; note: string }[];
}

export interface LocationAnalytics {
  locationId: LocationId;
  kpis: AnalyticsKpi[];
  specialists: { id: string; name: string; count: number; revenue: string; load: number }[];
  topServices: { label: string; value: string; pct: number }[];
}

export interface PromotionAnalytics {
  kpis: AnalyticsKpi[];
  rows: { id: string; name: string; period: string; where: string; count: number; fresh: number; revenue: string; cost: string }[];
  bars: { label: string; value: string; pct: number }[];
}

export type ChannelId = "tg" | "ig" | "fb" | "wa" | "vb" | "web";
export type AiMode = "suggest" | "confirm" | "auto";

export interface ChannelConfig {
  id: ChannelId;
  name: string;
  icon: string;
  description: string;
  connected: boolean;
  inbox: boolean;
  booking: boolean;
  ai: boolean;
  mode: AiMode;
  greeting: string;
  handoff: { negative: boolean; payment: boolean; human: boolean };
  /** Секрет завжди замаскований (контракт: секрети маскуються). */
  maskedSecret: string | null;
  connectHelp: { field: string; placeholder: string; how: string };
}

export type ChannelPatch = Partial<
  Pick<ChannelConfig, "inbox" | "booking" | "ai" | "mode" | "greeting">
> & { handoff?: Partial<ChannelConfig["handoff"]> };

export interface ConnectChannelInput {
  id: ChannelId;
  /** Токен / акаунт / номер. Зберігається на сервері, назад не повертається. */
  secret: string;
}

export interface PreviewMessage {
  from: "client" | "bot" | "manager";
  text: string;
}

export interface AiRequest {
  id: string;
  name: string;
  channel: string;
  time: string;
  text: string;
  intent: string;
  reply: string;
  replied: boolean;
}

export interface AiChatMessage {
  from: "client" | "staff";
  text: string;
}

export interface AiChat {
  clientName: string;
  channel: string;
  messages: AiChatMessage[];
  suggestions: string[];
  context: { label: string; value: string }[];
  warning: string | null;
}

export type PromoGoalId = "fill" | "back" | "fresh";

export interface AiSegment {
  id: string;
  label: string;
  note: string;
  count: number;
}

export interface AiPromoPlan {
  goalId: PromoGoalId;
  goals: { id: PromoGoalId; label: string }[];
  reason: string;
  when: string;
  percent: number;
  rows: { name: string; old: number; price: number }[];
  text: string;
  segments: AiSegment[];
  recommendedSegmentIds: string[];
}

export interface AiScreenContext {
  title: string;
  insight: { heading: string; body: string };
  question: string;
  answer: string;
  links: { label: string; href: string }[];
}
