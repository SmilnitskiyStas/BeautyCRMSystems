/**
 * Доменні типи адмінки Beauty CRM.
 * Відповідають контракту `.claude/docs/beauty-contracts.md` (розділи 2, 3, 6).
 * Деталі полів уточнює backend; зміни вносити тут і в `api/`.
 */

import type { ApiConflict } from "@/features/beauty-auth/errors";
import type { AppointmentCoupon, PromoAccess } from "./marketing/types";

export type * from "./marketing/types";

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
  /** Години роботи закладу (§18.1, формат §9). `null`/відсутнє = години не задано (обмеження немає). */
  workingHours?: WorkingHours | null;
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

/** Активний запис поза новими годинами закладу (409 `has_appointments_outside_hours`, §18.1). */
export type OutsideHoursConflict = ApiConflict;

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

/** Статуси, які ставить `PATCH /appointments/{id}` (скасування - окремий POST /cancel). */
export type PatchableStatus = "confirmed" | "completed" | "no_show";

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
  /** Ціна до знижки/акції (§24): якщо відсутня, вважається рівною `priceFinal`. */
  priceOriginal?: number;
  promotionId: string | null;
  promotionName?: string;
  /** Умови скасування запису (§11), якщо їх повернув backend. */
  cancellation?: CancellationSettings;
  /** Лише для `status === "cancelled"` (§16). */
  cancelledAt?: string;
  cancelledBy?: CancelledBy;
  /** Лише керівникам і лише якщо вказана. */
  cancelReason?: string;
  /** Нагадування запису (§22.6.1); відсутнє = backend ще не віддає. */
  reminders?: AppointmentReminders;
  /** UTC-момент (ISO), коли клієнт підтвердив візит кнопкою в чаті (§23.10); відсутнє = не підтверджував. */
  clientConfirmedAt?: string;
  /** §26.11.4: запис за купоном (`outcome=applied`); повного коду немає, `codeHint` лише керівникам. */
  coupon?: AppointmentCoupon;
  /** §26.11.4: запис походить із розсилки. */
  campaignId?: string;
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
  /** §26: запис створено за купоном; `couponDiscount` - підпис знижки («−135 ₴»), `couponName` - назва акції купона. */
  viaCoupon?: boolean;
  couponDiscount?: string;
  couponName?: string;
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
  /** Телефон клієнта для дзвінка (§25.7); відсутнє = backend ще не віддає. */
  phoneInfo?: ClientPhoneInfo;
}

/** Джерело номера (§25.3): NULL/відсутнє = спадкове чи невідоме. */
export type PhoneSource = "telegram_contact" | "manual_chat" | "web_form" | "staff";
export type PhonePendingSource = "telegram_contact" | "manual_chat";

/** Телефон у картці клієнта (§25.7). Повний номер бачать лише owner/admin. */
export interface ClientPhoneInfo {
  phone: string | null;
  phoneSource?: PhoneSource;
  /** UTC-момент (ISO) згоди на дзвінок. */
  callConsentAt?: string;
  /** Інший номер, наданий клієнтом у чаті, - чекає рішення адміністратора. */
  phonePending?: { phone: string; source: PhonePendingSource; at: string };
  /** М'який зв'язок «можливий дубль» (не злиття). */
  possibleDuplicate?: { id: string; name: string };
}

export type PhonePendingAction = "accept" | "reject";

/** Відповідь `POST /clients/{id}/phone-pending` (§25.7). */
export interface PhonePendingResult {
  phone: string;
  phoneSource?: PhoneSource;
  callConsentAt?: string;
  phonePending: null;
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
  /** `false` = зв'язок знято (графік збережено, але слотів і записів у закладі немає). Відсутнє = активний. */
  isActive?: boolean;
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

/** Інтервал у хвилинах від початку доби (локальний час закладу). */
export interface MinuteInterval {
  from: number;
  to: number;
}

/** Чому день повністю недоступний (або `open`). Пріоритет як у backend: закритий день -> відсутність -> години -> графік. */
export type DayState = "open" | "location_closed" | "absence" | "location_hours_off" | "master_off";

/** Доступність майстра в закладі на один день: графік ∩ години закладу ∩ не вихідний (§18.1). */
export interface DayAvailability {
  /** `YYYY-MM-DD` */
  date: string;
  state: DayState;
  /** Графік майстра в цьому закладі. */
  master: MinuteInterval[];
  /** Години закладу; `null` = не задано (обмеження немає). */
  location: MinuteInterval[] | null;
  /** Перетин (де можна записати); порожньо для закритого дня й відсутності. */
  free: MinuteInterval[];
  /** Заклад працює, а майстер ні: підпис «Не працює». */
  masterOff: MinuteInterval[];
  /** Поза годинами закладу: підпис «Заклад зачинено». */
  hoursOff: MinuteInterval[];
}

export interface CalendarLocation {
  id: LocationId;
  name: string;
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
  /** Активні заклади майстра (чипи вибору закладу). Відсутнє = один заклад без вибору. */
  locations?: CalendarLocation[];
  /** Вибраний заклад: доступність і записи показані саме для нього. */
  locationId?: LocationId;
  /** 7 елементів (пн..нд). Відсутнє (старий клієнт) = календар без затінення доступності. */
  availability?: DayAvailability[];
  /** Діапазон сітки, години доби [start, end); підлаштований під години закладу/графік. */
  gridStartHour?: number;
  gridEndHour?: number;
  /** Не вдалося завантажити закриття/вихідні закладу: календар може показувати закриті дні робочими. */
  availabilityWarning?: boolean;
  /** Скільки записів майстра цього тижня в інших його закладах (не показані). */
  otherLocationsCount?: number;
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
  /** §26.11.3: за замовчуванням `public`. */
  access?: PromoAccess;
}

export interface PromoPreview {
  rows: { serviceId: string; name: string; durationMinutes: number; price: number; promoPrice: number | null; where: string }[];
  locationSummary: string;
  samplePrice: number;
  sampleOriginal: number;
}

/** Діапазон аналітики (§19.1): локальні дні `from..to` включно, `yyyy-MM-dd`. */
export interface AnalyticsQuery {
  from: string;
  to: string;
  /** §19.3: додати `previous` (попередній період тієї ж тривалості). */
  compare: boolean;
}

export interface NetworkTotals extends FinanceFields {
  appointments: number;
  completed: number;
  cancelled: number;
  noShow: number;
  revenue: number;
  averageCheck: number;
  /** Частка 0..1. */
  cancellationRate: number;
  newClients: number;
}

/**
 * Фінанси (§24.5, лише owner/admin). Усі поля необов'язкові: відповідь без них (старий backend) дає UI без фінансових KPI.
 * `marginPercent` - число у відсотках з 1 знаком (42,5), `null` при нульовій виручці.
 */
export interface FinanceFields {
  cost?: number;
  masterPayout?: number;
  netIncome?: number;
  marginPercent?: number | null;
  /** Завершених записів без собівартості. */
  withoutCost?: number;
  /** Виручка таких записів. */
  revenueWithoutCost?: number;
}

export interface NetworkAnalytics {
  fromDate: string;
  toDate: string;
  /** Еталонна IANA-зона мережі (§19.1). */
  timezone: string;
  totals: NetworkTotals;
  previous: (NetworkTotals & { fromDate: string; toDate: string }) | null;
}

export type SeriesGranularity = "day" | "week" | "month";

export interface SeriesPoint {
  from: string;
  to: string;
  appointments: number;
  completed: number;
  cancelled: number;
  noShow: number;
  revenue: number;
  /** §24.6.2: фінансові поля точки (без `marginPercent`: клієнт рахує за формулою §24.5). */
  cost?: number;
  masterPayout?: number;
  netIncome?: number;
  withoutCost?: number;
}

export interface NetworkSeries {
  fromDate: string;
  toDate: string;
  timezone: string;
  granularity: SeriesGranularity;
  points: SeriesPoint[];
}

export interface LocationMetrics extends Omit<FinanceFields, "revenueWithoutCost"> {
  appointments: number;
  completed: number;
  cancelled: number;
  noShow: number;
  revenue: number;
  busyMinutes: number;
  availableMinutes: number;
  /** 0..1 або `null` (немає доступних хвилин). */
  load: number | null;
}

export interface LocationStat extends LocationMetrics {
  locationId: LocationId;
  name: string;
  timezone: string;
  isActive: boolean;
  previous: LocationMetrics | null;
}

export interface SpecialistStat {
  specialistId: string;
  name: string;
  isActive: boolean;
  appointments: number;
  completed: number;
  cancelled: number;
  noShow: number;
  revenue: number;
  busyMinutes: number;
  availableMinutes: number;
  load: number | null;
}

export interface ServiceStat {
  serviceId: string;
  name: string;
  completed: number;
  revenue: number;
}

export interface LocationBreakdown {
  locationId: LocationId;
  name: string;
  timezone: string;
  fromDate: string;
  toDate: string;
  busyMinutes: number;
  availableMinutes: number;
  load: number | null;
  specialists: SpecialistStat[];
  services: ServiceStat[];
}

export interface PromotionStat {
  id: string;
  name: string;
  period: string;
  where: string;
  uses: number;
  discountTotal: number;
  revenue: number;
  previous: { uses: number; discountTotal: number; revenue: number } | null;
}

export interface PromotionAnalytics {
  rows: PromotionStat[];
}

// ---------- §24: деталі періоду та собівартість ----------

/** Параметри `GET /analytics/network/details` (§24.6.3): `date` (день) АБО `from`+`to` (кошик тижня/місяця, ≤ 31 день). */
export interface AnalyticsDetailsQuery {
  date?: string;
  from?: string;
  to?: string;
  locationId?: LocationId;
  cursor?: string;
  take?: number;
}

export interface DetailsSummary {
  appointments: number;
  completed: number;
  cancelled: number;
  noShow: number;
  revenue: number;
  discountTotal: number;
  cost: number;
  masterPayout: number;
  netIncome: number;
  marginPercent: number | null;
  withoutCost: number;
  revenueWithoutCost: number;
}

export interface DetailsLocationRow {
  locationId: LocationId;
  name: string;
  timezone: string;
  completed: number;
  revenue: number;
  cost: number;
  masterPayout: number;
  netIncome: number;
  marginPercent: number | null;
  withoutCost: number;
}

/** Запис `completed` у деталях. Імені, телефону, email і `clientId` клієнта тут немає за контрактом (§24.6.3). */
export interface DetailsItem {
  appointmentId: string;
  /** `yyyy-MM-dd` у зоні закладу. */
  localDate: string;
  /** `HH:mm` у зоні закладу. */
  localTime: string;
  location: { id: LocationId; name: string };
  service: { id: string; name: string };
  specialist: { id: string; name: string };
  priceOriginal: number;
  discount: number;
  promotion?: { id: string; name: string };
  priceFinal: number;
  /** `null`, якщо `costMissing`. */
  cost: number | null;
  masterPercent: number | null;
  masterPayout: number | null;
  net: number;
  costMissing: boolean;
}

export interface AnalyticsDetails {
  fromDate: string;
  toDate: string;
  timezone: string;
  location?: { id: LocationId; name: string; timezone: string };
  summary: DetailsSummary;
  locations: DetailsLocationRow[];
  items: DetailsItem[];
  hasMore: boolean;
  nextCursor?: string;
}

/** Собівартість послуги (§24.6.1). */
export interface ServiceCost {
  costAmount: number;
  masterPercent: number | null;
  updatedAt: string;
  updatedBy?: { id: string; name: string };
}

export type ServiceCostOverride = ServiceCost & { locationId: LocationId; locationName: string };

/** `GET /services/{id}/costs`. */
export interface ServiceCostDetail {
  serviceId: string;
  network?: ServiceCost;
  locations: ServiceCostOverride[];
}

/** Рядок `GET /service-costs`. */
export interface ServiceCostRow extends ServiceCostDetail {
  serviceName: string;
  isActive: boolean;
}

/** Тіло `PUT /services/{id}/costs`; `locationId: null` = мережевий рядок. */
export interface ServiceCostInput {
  locationId: LocationId | null;
  costAmount: number;
  masterPercent?: number | null;
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
  /** `settingsJson.assistant.enabled` (§21): без нього кнопки в чаті не діють. Відсутнє = невідомо / вимкнено. */
  assistantEnabled?: boolean;
  /** `settingsJson.assistant.buttons` (§23.2): відсутнє на сервері = `true`. */
  buttons?: boolean;
  /** Секрет завжди замаскований (контракт: секрети маскуються). */
  maskedSecret: string | null;
  connectHelp: { field: string; placeholder: string; how: string };
}

export type ChannelPatch = Partial<
  Pick<ChannelConfig, "inbox" | "booking" | "ai" | "mode" | "greeting" | "buttons">
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

// ---------- Налаштування ШІ (§20) ----------

export type AiRoleId = "chat" | "analytics";
export type AiKeySource = "own" | "platform";
export type AiModelTier = "economy" | "balanced" | "smartest";

export interface AiRoleStatus {
  state: "ready" | "unavailable";
  /** provider_not_available | secrets_unavailable | own_key_missing | own_key_unreadable | platform_disabled | platform_key_missing | quota_exceeded */
  reason?: string;
}

export interface AiRoleSetting {
  provider: string;
  model: string;
  tier: AiModelTier | string;
  modelFallback: boolean;
}

/** Ключ провайдера: ніколи не містить повного ключа, лише `last4`. Для admin властивості `keys` немає. */
export interface AiKeyInfo {
  provider: string;
  hasKey: boolean;
  last4: string | null;
  updatedAt: string | null;
  updatedByName: string | null;
  usedByRoles: AiRoleId[];
}

export interface AiLimits {
  monthlyTokenLimit: number | null;
  monthlyRequestLimit: number | null;
  maxOutputTokensPerRequest: number;
}

export interface AiSettings {
  keySource: AiKeySource;
  chat: AiRoleSetting;
  analytics: AiRoleSetting;
  mode: AiMode;
  /** Ефективний текст додатка (типовий, якщо не змінено). */
  promptAddendum: string;
  isDefaultPrompt: boolean;
  defaultPromptAddendum: string;
  promptAddendumMaxLength: number;
  limits: AiLimits;
  platformQuota: { enabled: boolean; monthlyTokens: number | null; monthlyRequests: number | null };
  status: { chat: AiRoleStatus; analytics: AiRoleStatus };
  keys?: AiKeyInfo[];
  updatedAt: string | null;
  updatedByName: string | null;
}

export interface AiWarning {
  code: string;
  message: string;
}

export interface AiSettingsSaved {
  settings: AiSettings;
  warnings: AiWarning[];
}

export interface AiSettingsInput {
  chat: { provider: string; model: string };
  analytics: { provider: string; model: string };
  mode: AiMode;
  /** `null` = типовий текст. */
  promptAddendum: string | null;
  limits: AiLimits;
  acknowledgeAutoMode?: boolean;
}

export interface AiCatalogProvider {
  id: string;
  label: string;
  available: boolean;
  availabilityLabel: string | null;
}

export interface AiCatalogModel {
  id: string;
  provider: string;
  available: boolean;
  tier: AiModelTier | string;
  tierLabel: string;
  costRank: number;
  costLabel: string;
  inputPricePerMTok: number | null;
  outputPricePerMTok: number | null;
  currency: string;
  priceUpdatedAt: string | null;
  isDefaultFor: AiRoleId[];
}

export interface AiCatalog {
  roles: { id: AiRoleId; label: string; inUse?: boolean }[];
  providers: AiCatalogProvider[];
  models: AiCatalogModel[];
}

export interface AiKeySaveInput {
  provider: string;
  /** Секрет: живе лише в цьому виклику, ніколи не потрапляє в кеш запитів. */
  apiKey: string;
  forRole: AiRoleId;
  acknowledgeAffectedRoles?: boolean;
}

export interface AiKeySaved {
  provider: string;
  hasKey: boolean;
  last4: string | null;
  updatedAt: string | null;
  keySource: AiKeySource;
  usedByRoles: AiRoleId[];
}

export interface AiKeyDeleteInput {
  provider: string;
  forRole: AiRoleId;
  acknowledgeAffectedRoles?: boolean;
}

export interface AiKeyTestResult {
  ok: boolean;
  reason?: string;
  provider: string;
  model: string;
  checkedAt: string;
}

export interface AiUsage {
  keySource: AiKeySource;
  period: { from: string; to: string };
  limits: { monthlyTokens: number | null; monthlyRequests: number | null; source: "tenant" | "platform" | "min" };
  current: { requests: number; errors: number; inputTokens: number; outputTokens: number; estimatedCost: number | null; currency: string };
  byModel: {
    role: AiRoleId;
    provider: string;
    model: string;
    keySource: AiKeySource;
    requests: number;
    inputTokens: number;
    outputTokens: number;
    estimatedCost: number | null;
  }[];
  history: { period: string; requests: number; inputTokens: number; outputTokens: number }[];
}


// ---------- Нагадування, зв'язок, дзвінки, inbox (§22) ----------

/** Спосіб нагадування (§22.6.2). `auto` - спадковий варіант публічної форми (`reminder` 1h/2h), лише читання. */
export type ReminderMethod = "none" | "telegram" | "call";

export interface ReminderPoint {
  kind: "message" | "call";
  offsetMinutes: number;
  /** UTC-момент (ISO). */
  dueAt: string;
  status: string;
  /** Код пропуску (`quiet_hours`, `merged`, `no_channel`…); лише код, без тексту. */
  skipReason?: string;
}

export interface AppointmentReminders {
  method: ReminderMethod | "auto";
  offsetsMinutes: number[];
  points?: ReminderPoint[];
}

export type ContactSource = "chat" | "admin" | "stop" | "web";

export interface ContactPreferences {
  state: "unset" | "set";
  method?: ReminderMethod;
  offsetsMinutes: number[];
  source?: ContactSource;
  updatedAt?: string;
  updatedBy?: { id: string; name: string };
  telegramAvailable: boolean;
  phoneAvailable: boolean;
  telegramEnabled: boolean;
  callEnabled: boolean;
}

export interface ContactPreferencesInput {
  method: ReminderMethod;
  offsetsMinutes: number[];
  /** За замовчуванням `true`. */
  applyToUpcoming?: boolean;
}

export interface ContactPreferencesSaved extends ContactPreferences {
  replannedAppointments: number;
}

export interface ReminderSettings {
  telegramEnabled: boolean;
  callEnabled: boolean;
  /** `HH:mm` за зоною закладу. */
  quietHoursStart: string;
  quietHoursEnd: string;
}

export type CallTaskStatus = "open" | "done" | "skipped" | "cancelled";
export type CallTaskReason = "reminder" | "delivery_failed";

/** `today`, `tomorrow` або `YYYY-MM-DD` (локальна дата `due_at` у зоні закладу). */
export type CallTaskDay = string;

export interface CallTaskQuery {
  day: CallTaskDay;
  status: CallTaskStatus | "all";
  locationId?: LocationId;
  take?: number;
}

export interface CallTask {
  id: string;
  /** UTC-момент (ISO). */
  dueAt: string;
  /** Локальний час `HH:mm` у зоні закладу. */
  localDueTime: string;
  status: CallTaskStatus;
  reason: CallTaskReason;
  offsetMinutes?: number;
  /** `phone` віддається лише керівникам. */
  client: { id: string; name: string; phone?: string; /** §25.7: інший номер від клієнта в чаті (повний, лише тут і в картці). */ phonePending?: string };
  appointment: { id: string; startsAt: string; localStartsAt: string; serviceName: string; specialistName: string; status: string };
  location: { id: string; name: string; timezone: string };
  completedAt?: string;
  completedBy?: { id: string; name: string };
  /** Не логувати. */
  note?: string;
}

export interface CallTaskList {
  items: CallTask[];
  counts: { open: number; done: number; skipped: number };
}

export interface CallTaskPatch {
  status: "done" | "skipped" | "open";
  note?: string;
}

export type ConversationFilter = "needs_reply" | "all";
export type MessageSenderType = "client" | "ai" | "staff" | "system";
export type MessageStatus = "received" | "processed" | "skipped" | "held" | "draft" | "sending" | "pending" | "sent" | "failed";

export interface ConversationSummary {
  id: string;
  channel: { id: string; type: string; name: string };
  location?: { id: string; name: string };
  client?: { id: string; name: string; /** §25.7: `+` і останні 2 цифри, решта `•`. Повного номера в inbox немає. */ phoneMasked?: string; /** Клієнт надав інший номер - чекає рішення в картці. */ phonePending?: boolean };
  /** Ім'я клієнта або «Клієнт Telegram». */
  title: string;
  /** `active` | `handoff`. */
  aiState: string;
  handoffReason?: string;
  /** Відсутнє при `handoff` = безстроково, до «Повернути ШІ». */
  aiPausedUntil?: string;
  awaitingStaffSince?: string;
  lastMessageAt: string;
  lastMessagePreview: string;
  lastMessageSenderType: MessageSenderType;
  unreadCount: number;
}

export interface ConversationListQuery {
  filter: ConversationFilter;
  locationId?: LocationId;
  channelType?: string;
  cursor?: string;
  take?: number;
}

export interface ConversationList {
  items: ConversationSummary[];
  nextCursor?: string;
  meta: { needsReplyCount: number };
}

export interface ConversationMessage {
  id: string;
  direction: "inbound" | "outbound";
  senderType: MessageSenderType;
  senderUser?: { id: string; name: string };
  kind: string;
  /** Текст розмови (ПД): лише в UI inbox, не в console/аналітику. */
  body: string;
  status: MessageStatus;
  failureCode?: string;
  sentAt?: string;
  createdAt: string;
  /** Підписи кнопок клавіатури вихідного повідомлення (§23.10); без `callback_data` і токенів. */
  buttons?: string[];
  /** Підпис кнопки, яку натиснув клієнт (`kind === "callback"`). UI показує «Клієнт натиснув: {підпис}», не сирий `body`. */
  pressedLabel?: string;
  /** §25.4: результат перевірки контакту (`kind === "contact"`); номера в повідомленні немає. */
  contactVerdict?: ContactVerdict;
}

export type ContactVerdict = "own" | "foreign" | "invalid";

export interface ConversationDetail {
  conversation: ConversationSummary & { canReply: boolean; replyBlockedReason?: "channel_inactive" | "channel_unsupported" };
  messages: ConversationMessage[];
  activeProposal?: { status: string; expiresAt: string; summaryText: string };
  upcomingAppointments: {
    id: string;
    startsAt: string;
    serviceName: string;
    locationName: string;
    reminders: { method: ReminderMethod | "auto"; offsetsMinutes: number[] };
    /** UTC-момент (ISO) підтвердження візиту клієнтом кнопкою (§23.10). */
    clientConfirmedAt?: string;
  }[];
}

export interface ReplyResult {
  message: { id: string; status: MessageStatus };
}
