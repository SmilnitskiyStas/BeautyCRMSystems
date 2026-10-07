import type {
  AiChat,
  AiPromoPlan,
  AiRequest,
  BeautyLocation,
  CancellationSettings,
  CalendarWeek,
  ChannelConfig,
  ChannelId,
  ChannelPatch,
  ClientProfile,
  ClientSummary,
  ConnectChannelInput,
  LocationAnalytics,
  LocationId,
  NetworkAnalytics,
  Overview,
  PriceListRow,
  PromoDraft,
  PromoPreview,
  PromotionAnalytics,
  PromoGoalId,
  StaffProfile,
  StaffSummary,
} from "../types";

/**
 * Контракт клієнта адмінки. Шляхи — `/api/beauty/...` (`.claude/docs/beauty-contracts.md`, розділ 3).
 * Щоб підключити реальний backend, реалізуйте цей інтерфейс (fetch) і змініть
 * експорт у `api/index.ts`; хуки й компоненти не змінюються.
 */
export interface BeautyAdminApi {
  /** GET /locations */
  getLocations(): Promise<BeautyLocation[]>;
  /** GET /appointments?date&locationId (огляд дня) + /clients (останні) */
  getOverview(locationId: LocationId | null): Promise<Overview>;
  /** GET /appointments?from&to[&specialistId]; порожній `specialistId` = перший майстер зі списку */
  getCalendarWeek(specialistId: string): Promise<CalendarWeek>;
  /** PATCH /appointments/{id} { startsAt } — перенос. `startsAt` = локальний час закладу `YYYY-MM-DDTHH:mm` */
  moveAppointment(id: string, startsAt: string): Promise<void>;
  /** GET /settings/cancellation (§11) */
  getCancellationSettings(): Promise<CancellationSettings>;
  /** PUT /settings/cancellation (§11, лише owner) */
  updateCancellationSettings(s: CancellationSettings): Promise<CancellationSettings>;
  /** POST /appointments/{id}/cancel — refundAmount рахує backend за політикою §11 */
  cancelAppointment(id: string): Promise<{ refundAmount: number }>;
  /** GET /clients */
  getClients(): Promise<ClientSummary[]>;
  /** GET /clients/{id} */
  getClient(id: string): Promise<ClientProfile | null>;
  /** POST /clients/{id}/notes */
  addClientNote(id: string, text: string): Promise<void>;
  /** GET /specialists */
  getStaff(): Promise<StaffSummary[]>;
  /** GET /specialists/{id} */
  getStaffProfile(id: string): Promise<StaffProfile | null>;
  /** GET /services (+ /services/{id}/prices) */
  getPriceList(): Promise<PriceListRow[]>;
  /** GET /promotions/preview */
  previewPromotion(draft: PromoDraft): Promise<PromoPreview>;
  /** POST /promotions */
  createPromotion(draft: PromoDraft): Promise<{ id: string }>;
  /** GET /analytics/network */
  getNetworkAnalytics(): Promise<NetworkAnalytics>;
  /** GET /analytics/locations?locationId */
  getLocationAnalytics(locationId: LocationId): Promise<LocationAnalytics>;
  /** GET /analytics/promotions */
  getPromotionAnalytics(): Promise<PromotionAnalytics>;
  /** GET /channels (секрети маскуються) */
  getChannels(): Promise<ChannelConfig[]>;
  /** PUT /channels/{id} */
  updateChannel(id: ChannelId, patch: ChannelPatch): Promise<ChannelConfig>;
  /** PUT /channels/{id} (connect) */
  connectChannel(input: ConnectChannelInput): Promise<ChannelConfig>;
  /** PUT /channels/{id} (disconnect) */
  disconnectChannel(id: ChannelId): Promise<ChannelConfig>;
  /** GET /ai/actions (вхідні заявки та чернетки) */
  getAiRequests(): Promise<AiRequest[]>;
  /** POST /ai/actions/{id}/approve */
  approveAiRequest(id: string): Promise<void>;
  /** GET /conversations/{id} */
  getAiChat(): Promise<AiChat>;
  /** POST /conversations/{id}/messages */
  sendAiChatMessage(text: string): Promise<void>;
  /** GET /ai/suggest_promotion?goal */
  getAiPromoPlan(goalId: PromoGoalId): Promise<AiPromoPlan>;
  /** POST /ai/actions (build_audience + campaign) */
  launchAiCampaign(input: { goalId: PromoGoalId; segmentIds: string[]; text: string }): Promise<{ reach: number }>;
}
