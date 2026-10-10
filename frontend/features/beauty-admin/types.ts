/**
 * Доменні типи адмінки Beauty CRM.
 * Відповідають контракту `.claude/docs/beauty-contracts.md` (розділи 2, 3, 6).
 * Деталі полів уточнює backend; зміни вносити тут і в `api/`.
 */

import type { ApiConflict } from "@/features/beauty-auth/errors";

export type LocationId = string;

export interface BeautyLocation {
  id: LocationId;
  name: string;
  address?: string | null;
  phone?: string | null;
  /** IANA-зона закладу (`Europe/Kyiv`). */
  timezone?: string;
  /** Відсутнє = активний. Неактивні віддає лише `GET /locations?includeInactive=true` (керівники). */
  isActive?: boolean;
  /** Щотижневі вихідні закладу (§17); відсутнє/порожнє = працює 7 днів. */
  closedWeekdays?: Weekday[];
}

/** Закриття закладу на дати (§17): повні дні в зоні закладу, включно. `reason` приходить лише керівникам. */
export interface LocationClosure {
  id: string;
  locationId: LocationId;
  /** `YYYY-MM-DD` */
  dateFrom: string;
  dateTo: string;
  reason?: string;
}

export interface ClosureInput {
  dateFrom: string;
  dateTo: string;
  /** ≤ 200 символів. */
  reason?: string;
  /** `true` - повторний запит після підтвердження конфліктів. */
  confirm?: boolean;
}

/** Активний запис на день, що стає вихідним (409 `has_appointments_on_closed_days`, §17). */
export type ClosedDayConflict = ApiConflict;

/** Закритий день у колонці календаря. `reason` - лише для closures і лише керівникам. */
export interface ClosedDay {
  date: string;
  source: "weekday" | "closure";
  reason?: string;
}

/** Тіло POST/PUT /locations (§16). `isActive` — лише для PUT. */
export interface LocationInput {
  name: string;
  address: string;
  phone: string;
  timezone: string;
  isActive: boolean;
}

/** Хто скасував запис (§16). `name` — лише для staff і лише якщо API його віддав. */
export type CancelledByType = "client" | "staff" | "system";
export interface CancelledBy {
  type: CancelledByType;
  name?: string;
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
  /** Лише для `status === "cancelled"` (§16). */
  cancelledAt?: string;
  cancelledBy?: CancelledBy;
  /** Лише керівникам і лише якщо вказана. */
  cancelReason?: string;
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
  status: "completed" | "planned" | "cancelled" | "no_show";
  viaPromo: boolean;
  /** Підпис «5 жовт., 12:40»; лише для скасованих. */
  cancelledAtLabel?: string;
  cancelledBy?: CancelledBy;
  cancelReason?: string;
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
  /** Зведення скасувань (§16). */
  cancelledCount: number;
  cancelledByClientCount: number;
}

/** Ключі днів тижня у `working_hours` (§9): mon..sun. */
export type Weekday = "mon" | "tue" | "wed" | "thu" | "fri" | "sat" | "sun";
export interface TimeInterval {
  /** `HH:mm`, локальний час закладу. */
  from: string;
  to: string;
}
/** Відсутній день = вихідний. */
export type WorkingHours = Partial<Record<Weekday, TimeInterval[]>>;

/** Послуга з каталогу (для призначення працівникам). */
export interface ServiceItem {
  id: string;
  name: string;
  category: string | null;
  durationMinutes: number;
}

export interface StaffLocation {
  locationId: LocationId;
  locationName: string;
  workingHours: WorkingHours;
}

/** Працівник (§13). `phone` відсутній для ролі specialist (довідник). */
export interface StaffMember {
  id: string;
  name: string;
  phone: string | null;
  position: string | null;
  isActive: boolean;
  services: { id: string; name: string }[];
  locations: StaffLocation[];
}

export interface StaffCreateInput {
  name: string;
  phone: string;
  position: string;
  locationIds: LocationId[];
  serviceIds: string[];
  workingHours: WorkingHours;
}

export interface StaffUpdateInput {
  name: string;
  phone: string;
  position: string;
  isActive: boolean;
}

/** Одноразовий токен запрошення (показується лише один раз; не логувати й не кешувати). */
export interface InviteResult {
  token: string;
}

/** Незакритe запрошення працівника (без токена: він показується лише при створенні). */
export interface PendingInvite {
  id: string;
  email: string;
  expiresAt: string;
}

export type AbsenceType = "sick" | "vacation" | "day_off" | "other";
export type AbsenceStatus = "requested" | "approved" | "rejected" | "cancelled";

export interface Absence {
  id: string;
  specialistId: string;
  type: AbsenceType;
  /** Повні дні, включно, `YYYY-MM-DD` (часова зона закладу). */
  dateFrom: string;
  dateTo: string;
  status: AbsenceStatus;
  /** Чутливе: бекенд віддає лише owner/admin і автору; інакше поле відсутнє. Не логувати. */
  note?: string;
}

export interface AbsenceConflict {
  appointmentId: string;
  /** Локальний `YYYY-MM-DDTHH:mm` (mock) або повний ISO зі зсувом/Z (backend). */
  startsAt: string;
  /** IANA-зона закладу, якщо backend її віддає; інакше показуємо в зоні браузера. */
  timezone?: string;
  serviceName: string;
}

export interface AbsenceInput {
  type: AbsenceType;
  dateFrom: string;
  dateTo: string;
  note?: string;
}

export interface AbsenceResult {
  absence: Absence | null;
  /** Заповнюється лише для керівників. */
  conflicts: AbsenceConflict[];
}

export interface CalendarMaster {
  id: string;
  name: string;
  locationName: string;
  /** Неактивний майстер потрапляє в селектор, лише якщо має нескасовані записи у видимому періоді. */
  isActive: boolean;
  /** Кількість записів pending/confirmed, які треба перенести (для неактивних). */
  toMoveCount: number;
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
  /** 7 елементів (пн..нд): закритий день закладу майстра (§17) або `null`. Відсутнє = жодного закритого дня. */
  closedDays?: (ClosedDay | null)[];
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
