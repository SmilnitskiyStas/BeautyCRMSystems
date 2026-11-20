import type { AccessCatalog } from "@/features/beauty-auth/access";
import type {
  Batch,
  BatchQuery,
  InventoryScope,
  LocationSetting,
  Movement,
  MovementQuery,
  Operation,
  OperationQuery,
  OperationSummary,
  Page,
  Product,
  ProductDetail,
  ProductQuery,
  ProductRequest,
  Category,
  CategoryRequest,
  ReceiptRequest,
  StockPage,
  StockQuery,
  StockSummaryPage,
  StockSummaryQuery,
  Stocktake,
  StocktakeLineInput,
  StocktakePostResult,
  StocktakeQuery,
  StocktakeSummary,
  Supplier,
  SupplierQuery,
  SupplierRequest,
  TransferRequest,
  WriteOffRequest,
  AlertQuery,
  MaterialSet,
  MaterialSetRequest,
  ServiceMaterials,
  StaffAlert,
} from "../inventory/types";
import type { TelegramLinkCreateRequest, TelegramLinkIssued, TelegramLinkStatus } from "../inventory/telegram-link";
import type { CancelProductOrderResult, ProductOrder, ProductOrderCounts, ProductOrderQuery, ProductOrderSummary } from "../inventory/orders";
import type {
  AccessSaveInput,
  AccessSaveResult,
  AuditPage,
  MyRevenue,
  TeamInvite,
  TeamInviteCreated,
  TeamInviteInput,
  TeamUser,
  UserAccess,
} from "../team/types";
import type {
  AiCatalog,
  AiChat,
  AiKeyDeleteInput,
  AiKeySaved,
  AiKeySaveInput,
  AiKeySource,
  AiKeyTestResult,
  AiSettings,
  AiSettingsInput,
  AiSettingsSaved,
  AiUsage,
  PatchableStatus,
  AiPromoPlan,
  AiRequest,
  BeautyLocation,
  CancellationSettings,
  CallTask,
  CallTaskList,
  CallTaskPatch,
  CallTaskQuery,
  CalendarWeek,
  ContactPreferences,
  ContactPreferencesInput,
  ContactPreferencesSaved,
  ConversationDetail,
  ConversationList,
  ConversationListQuery,
  ReminderSettings,
  ReplyResult,
  ClosureInput,
  LocationClosure,
  Weekday,
  ChannelConfig,
  ChannelId,
  ChannelPatch,
  ClientProfile,
  ClientSummary,
  ConnectChannelInput,
  AnalyticsDetails,
  AnalyticsDetailsQuery,
  AnalyticsQuery,
  PhonePendingAction,
  PhonePendingResult,
  ServiceCost,
  ServiceCostDetail,
  ServiceCostInput,
  ServiceCostRow,
  LocationBreakdown,
  LocationStat,
  NetworkSeries,
  LocationInput,
  LocationId,
  NetworkAnalytics,
  Overview,
  PriceListRow,
  PromoDraft,
  PromoPreview,
  PromotionAnalytics,
  PromoGoalId,
  Absence,
  AbsenceInput,
  AbsenceResult,
  InviteResult,
  PendingInvite,
  ServiceCreateInput,
  FeedbackInput,
  ServiceItem,
  StaffCreateInput,
  StaffMember,
  StaffUpdateInput,
  WorkingHours,
  AudiencePreview,
  AudienceQueryInput,
  CampaignDto,
  CampaignList,
  CampaignListQuery,
  CampaignPreview,
  CampaignRequest,
  CampaignStats,
  ClientCoupon,
  ConfirmCampaignInput,
  ConsentInput,
  CouponCheck,
  CouponCheckInput,
  CouponInput,
  CouponSummary,
  MarketingConsent,
  PromotionItem,
  SharedCoupon,
} from "../types";

/**
 * Контракт клієнта адмінки. Шляхи — `/api/beauty/...` (`.claude/docs/beauty-contracts.md`, розділ 3).
 * Щоб підключити реальний backend, реалізуйте цей інтерфейс (fetch) і змініть
 * експорт у `api/index.ts`; хуки й компоненти не змінюються.
 */
export interface BeautyAdminApi {
  /** GET /locations - активні заклади (для вибору в формах і фільтрів) */
  getLocations(): Promise<BeautyLocation[]>;
  /** GET /locations?includeInactive=true (керівники, §16) - для екрана «Заклади» */
  getManagedLocations(): Promise<BeautyLocation[]>;
  /** POST /locations (owner, admin) - 422 invalid_timezone, 409 location_name_taken */
  createLocation(input: LocationInput): Promise<BeautyLocation>;
  /** PUT /locations/{id} (owner, admin) - 409 has_future_appointments | timezone_locked | location_name_taken */
  updateLocation(id: LocationId, input: LocationInput): Promise<BeautyLocation>;
  /**
   * PUT /locations/{id}/closed-weekdays (owner, admin; §17) - повна заміна переліку. Без `confirm` і з активними записами
   * на нові вихідні: 409 `has_appointments_on_closed_days` (`BeautyApiError.conflicts`), змін немає.
   * Повертає застосований перелік.
   */
  setClosedWeekdays(id: LocationId, weekdays: Weekday[], confirm?: boolean): Promise<Weekday[]>;
  /**
   * PUT /locations/{id}/working-hours (owner, admin; §18.1) - `null` скидає в «не задано». Без `confirm` і з активними
   * записами поза новими годинами: 409 `has_appointments_outside_hours` (`BeautyApiError.conflicts`), змін немає.
   * Повертає застосовані години.
   */
  setLocationWorkingHours(id: LocationId, workingHours: WorkingHours | null, confirm?: boolean): Promise<WorkingHours | null>;
  /** GET /locations/{id}/closures?from&to (staff читає; `reason` лише керівникам; діапазон ≤ 366 днів) */
  getClosures(id: LocationId, q: { from: string; to: string }): Promise<LocationClosure[]>;
  /** POST /locations/{id}/closures (owner, admin) - 409 closure_overlap | has_appointments_on_closed_days (+conflicts), 422 */
  addClosure(id: LocationId, input: ClosureInput): Promise<LocationClosure>;
  /** DELETE /locations/{id}/closures/{closureId} (owner, admin) */
  deleteClosure(id: LocationId, closureId: string): Promise<void>;
  /** GET /api/access/catalog (§30.9.2): каталог прав, пресети, обмеження; однаковий для всіх ролей (кеш 5 хв на клієнті). */
  getAccessCatalog(): Promise<AccessCatalog>;
  /** GET /overview?date&locationId (§14.5, owner/admin) + /clients (останні) */
  getOverview(locationId: LocationId | null): Promise<Overview>;
  /**
   * GET /appointments?from&to[&includeCancelled=true]; порожній `specialistId` = перший майстер зі списку.
   * За замовчуванням БЕЗ скасованих (§16); `includeCancelled` додає їх до записів вибраного майстра.
   * `locationId` - заклад, обраний у календарі (майстер може працювати в кількох); порожній = перший активний заклад майстра.
   * Час записів - локальний час закладу; тиждень і «сьогодні» - за зоною закладу (аудит TASK-705).
   */
  getCalendarWeek(specialistId: string, includeCancelled?: boolean, locationId?: LocationId): Promise<CalendarWeek>;
  /** GET /appointments?specialistId (найближчі 60 днів): кількість майбутніх pending/confirmed записів майстра */
  getUpcomingAppointmentsCount(specialistId: string): Promise<number>;
  /** PATCH /appointments/{id} { startsAt } — перенос. `startsAt` = локальний час закладу `YYYY-MM-DDTHH:mm` */
  moveAppointment(id: string, startsAt: string): Promise<void>;
  /**
   * PATCH /appointments/{id} { status } (§9, §24.4): confirmed | completed | no_show. `completed` фіксує знімок собівартості;
   * закритий запис (completed/cancelled/no_show) -> 409 `appointment_closed`. Скасування - лише `cancelAppointment`.
   */
  setAppointmentStatus(id: string, status: PatchableStatus): Promise<void>;
  /** GET /settings/cancellation (§11) */
  getCancellationSettings(): Promise<CancellationSettings>;
  /** PUT /settings/cancellation (§11, лише owner) */
  updateCancellationSettings(s: CancellationSettings): Promise<CancellationSettings>;
  /** POST /appointments/{id}/cancel {reason?} — refundAmount рахує backend за політикою §11; reason ≤300 (§16) */
  cancelAppointment(id: string, reason?: string): Promise<{ refundAmount: number }>;
  /** GET /clients */
  getClients(): Promise<ClientSummary[]>;
  /** GET /clients/{id} */
  getClient(id: string): Promise<ClientProfile | null>;
  /** POST /clients/{id}/notes */
  addClientNote(id: string, text: string): Promise<void>;
  // ---- «Команда й доступи» (ADR-023, §30.9; team.manage = manage, журнал - лише owner) ----
  /** GET /api/users: + accessVersion, manageable. */
  getTeamUsers(): Promise<TeamUser[]>;
  /** GET /api/invites: усі запрошення з permissions (знімок). */
  getTeamInvites(): Promise<TeamInvite[]>;
  /** GET /api/users/{id}/access: 404 user_not_found, 403 forbidden_role. */
  getUserAccess(id: string): Promise<UserAccess>;
  /** PUT /api/users/{id}/access (dryRun = лише обчислення): 409 access_version_conflict {currentVersion}, 403 permission_ceiling, 422 permission_inconsistent|module_disabled|... */
  saveUserAccess(id: string, input: AccessSaveInput): Promise<AccessSaveResult>;
  /** POST /api/users/{id}/access/reset: скидання до пресету ролі. */
  resetUserAccess(id: string, input: { expectedVersion: number; dryRun?: boolean }): Promise<AccessSaveResult>;
  /** PATCH /api/users/{id}/status {isActive, expectedVersion?} */
  setUserActive(id: string, isActive: boolean, expectedVersion?: number): Promise<void>;
  /** POST /api/invites {email, role, specialistId?, permissions?}: токен показується один раз; clamped - обрізані стелею ключі. */
  createTeamInvite(input: TeamInviteInput): Promise<TeamInviteCreated>;
  /** GET /api/users/access-audit?userId&cursor&take (лише owner). */
  getAccessAudit(q?: { userId?: string; cursor?: string; take?: number }): Promise<AuditPage>;
  /** GET /api/beauty/me/revenue?from&to (§30.9.7): лише власний виторг; потрібен revenue.view >= own. */
  getMyRevenue(q: { from: string; to: string }): Promise<MyRevenue>;
  /** GET /api/invites (§10) - лише незакриті (pending) запрошення цього працівника */
  getStaffInvites(staffId: string): Promise<PendingInvite[]>;
  /** DELETE /api/invites/{id} (§10) - відкликати запрошення */
  revokeInvite(inviteId: string): Promise<void>;
  /** GET /specialists (§13; specialist бачить довідник без телефонів) */
  getStaff(): Promise<StaffMember[]>;
  /** GET /specialists/{id} */
  getStaffProfile(id: string): Promise<StaffMember | null>;
  /** GET /services — каталог для призначення послуг */
  getServices(): Promise<ServiceItem[]>;
  /** POST /services (owner, admin): створює послугу (без ціни). 422/409 - див. `humanizeError`. */
  createService(input: ServiceCreateInput): Promise<ServiceItem>;
  /**
   * PUT /services/{id}/prices (owner, admin): `{price}` - мережева ціна; з `locationId` - перевизначення закладу
   * (`null`/відсутній = мережева). 404 service_not_found|location_not_found.
   */
  setServicePrice(serviceId: string, price: number, locationId?: LocationId | null): Promise<void>;
  /**
   * POST /feedback (§27.9, owner/admin/specialist): заявка розробникам -> 202 `{id}`. 422 invalid_type|invalid_severity|invalid_message|
   * invalid_page_url; 429 rate_limited + Retry-After (5/год на користувача, 30/добу на заклад); 403 tenant_suspended|user_inactive.
   */
  sendFeedback(input: FeedbackInput): Promise<{ id: string }>;
  /** POST /specialists (owner, admin) */
  createStaff(input: StaffCreateInput): Promise<StaffMember>;
  /** PUT /specialists/{id} (owner, admin) */
  updateStaff(id: string, input: StaffUpdateInput): Promise<void>;
  /** PUT /specialists/{id}/services — повна заміна */
  setStaffServices(id: string, serviceIds: string[]): Promise<void>;
  /** PUT /specialists/{id}/schedule — графік у закладі, формат §9 */
  setStaffSchedule(id: string, locationId: string, workingHours: WorkingHours): Promise<void>;
  /** POST /specialists/{id}/invite — токен показується один раз */
  inviteStaff(id: string, email: string): Promise<InviteResult>;
  /** GET /absences?from&to&specialistId — `note` лише owner/admin і автору */
  getAbsences(q: { from: string; to: string; specialistId?: string }): Promise<Absence[]>;
  /** POST /specialists/{id}/absences — керівник: одразу approved + conflicts; specialist: requested */
  createAbsence(specialistId: string, input: AbsenceInput): Promise<AbsenceResult>;
  /** POST /absences/{id}/approve — повертає conflicts */
  approveAbsence(id: string): Promise<AbsenceResult>;
  /** POST /absences/{id}/reject */
  rejectAbsence(id: string): Promise<void>;
  /** POST /absences/{id}/cancel */
  cancelAbsence(id: string): Promise<void>;
  /** GET /services (+ /services/{id}/prices) */
  getPriceList(): Promise<PriceListRow[]>;
  /** GET /promotions/preview */
  previewPromotion(draft: PromoDraft): Promise<PromoPreview>;
  /** POST /promotions */
  createPromotion(draft: PromoDraft): Promise<{ id: string }>;
  /**
   * GET /analytics/network?from&to[&compare=true] (§19.1, §19.3): датний режим `yyyy-MM-dd`; 422 invalid_range.
   * Доступ лише owner/admin.
   */
  getNetworkAnalytics(q: AnalyticsQuery): Promise<NetworkAnalytics>;
  /** GET /analytics/network/series?from&to (§19.2): динаміка одним запитом, гранулярність обирає сервер. */
  getNetworkSeries(q: Pick<AnalyticsQuery, "from" | "to">): Promise<NetworkSeries>;
  /**
   * GET /analytics/network/details?date | from&to[&locationId][&cursor][&take] (§24.6.3, owner/admin; `no-store`): підсумок, заклади
   * й сторінка записів `completed` БЕЗ даних клієнта. 422 invalid_range|invalid_date|invalid_cursor, 404 location_not_found, 429 rate_limited.
   */
  getNetworkDetails(q: AnalyticsDetailsQuery): Promise<AnalyticsDetails>;
  /** GET /analytics/locations?from&to[&compare=true] (§19.4): усі активні + неактивні із записами; `load` зі §19.4. */
  getLocationsAnalytics(q: AnalyticsQuery): Promise<LocationStat[]>;
  /** GET /analytics/locations/{id}/breakdown?from&to (§19.4): спеціалісти й топ послуг; 404 location_not_found. */
  getLocationBreakdown(locationId: LocationId, q: Pick<AnalyticsQuery, "from" | "to">): Promise<LocationBreakdown>;
  /** GET /analytics/promotions?from&to[&compare=true] (+ /promotions для періоду й закладів) */
  getPromotionAnalytics(q: AnalyticsQuery): Promise<PromotionAnalytics>;
  /** GET /ai/settings (§20.5, owner/admin; `no-store`); для admin `keys` відсутній. Повного ключа в відповіді немає. */
  getAiSettings(): Promise<AiSettings>;
  /** PUT /ai/settings (owner/admin): повна заміна; 422 prompt_addendum_*|unknown_model|auto_mode_ack_required…; повертає warnings[]. */
  saveAiSettings(input: AiSettingsInput): Promise<AiSettingsSaved>;
  /** PUT /ai/settings/key-source (owner) */
  setAiKeySource(keySource: AiKeySource): Promise<AiSettings>;
  /** GET /ai/models (owner/admin): ролі, провайдери, моделі з рівнями й цінами (цін у коді frontend немає). */
  getAiModels(): Promise<AiCatalog>;
  /**
   * PUT /ai/settings/keys/{provider} (owner). Ключ іде лише в тілі запиту. 409 key_affects_other_roles
   * (`BeautyApiError.affectedRoles`), 422 invalid_key_format|key_rejected|provider_not_available, 429, 502, 503.
   */
  saveAiKey(input: AiKeySaveInput): Promise<AiKeySaved>;
  /** DELETE /ai/settings/keys/{provider}?forRole&acknowledgeAffectedRoles (owner) - 204, 409 key_affects_other_roles */
  deleteAiKey(input: AiKeyDeleteInput): Promise<void>;
  /** POST /ai/settings/keys/{provider}/test (owner): без `apiKey` перевіряється збережений ключ. */
  testAiKey(provider: string, apiKey?: string): Promise<AiKeyTestResult>;
  /** GET /ai/usage?months=1..12 (owner/admin) */
  getAiUsage(months?: number): Promise<AiUsage>;
  /** GET /channels (секрети маскуються) */
  getChannels(): Promise<ChannelConfig[]>;
  /** PUT /channels/{id} */
  updateChannel(id: ChannelId, patch: ChannelPatch): Promise<ChannelConfig>;
  /** PUT /channels/{id} (connect) */
  connectChannel(input: ConnectChannelInput): Promise<ChannelConfig>;
  /** PUT /channels/{id} (disconnect) */
  disconnectChannel(id: ChannelId): Promise<ChannelConfig>;
  /** POST /channels/{id}/telegram/sync-commands (§23.4, owner/admin): 204; 404 channel_not_found, 409 channel_unsupported|channel_inactive|token_missing, 502 telegram_unavailable. */
  syncTelegramCommands(id: ChannelId): Promise<void>;
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

  // ---------- §24: собівартість послуг (owner/admin; specialist -> 403 forbidden_role) ----------
  /** GET /service-costs?includeInactive=false (§24.6.1): рядок на послугу з мережевим значенням і override закладів. */
  getServiceCosts(includeInactive?: boolean): Promise<ServiceCostRow[]>;
  /** GET /services/{id}/costs: 404 service_not_found. */
  getServiceCostDetail(serviceId: string): Promise<ServiceCostDetail>;
  /**
   * PUT /services/{id}/costs (§24.6.1): upsert мережевого (`locationId: null`) чи override закладу. 404 service_not_found|location_not_found,
   * 422 invalid_cost|invalid_master_percent, 429 rate_limited. Минулі завершені записи не перераховуються.
   */
  setServiceCost(serviceId: string, input: ServiceCostInput): Promise<ServiceCost & { locationId: string | null }>;
  /** DELETE /services/{id}/costs?locationId= (§24.6.1): без `locationId` - мережевий рядок. 404 cost_not_found. */
  deleteServiceCost(serviceId: string, locationId: string | null): Promise<void>;

  // ---------- §25: телефон клієнта ----------
  /** POST /clients/{id}/phone-pending {action} (§25.7, owner/admin): 404 client_not_found, 409 no_pending_phone, 422 invalid_action. */
  resolvePhonePending(clientId: string, action: PhonePendingAction): Promise<PhonePendingResult>;

  // ---------- §22: нагадування, зв'язок, дзвінки, inbox (owner/admin; specialist -> 403 forbidden_role) ----------
  /** GET /clients/{id}/contact-preferences (§22.6.2): 404 client_not_found. */
  getContactPreferences(clientId: string): Promise<ContactPreferences>;
  /**
   * PUT /clients/{id}/contact-preferences (§22.6.2): 422 invalid_method|invalid_offsets, 409 telegram_unavailable|phone_required|
   * call_reminders_disabled|telegram_reminders_disabled. `applyToUpcoming` за замовчуванням true.
   */
  setContactPreferences(clientId: string, input: ContactPreferencesInput): Promise<ContactPreferencesSaved>;
  /** GET /call-tasks (§22.6.3): день за локальною датою `due_at` у зоні закладу; 422 invalid_day|invalid_status. */
  getCallTasks(q: CallTaskQuery): Promise<CallTaskList>;
  /** PATCH /call-tasks/{id} (§22.6.3): 404 call_task_not_found, 422 invalid_status|invalid_note, 409 task_already_closed (+`task`)|task_cancelled. */
  updateCallTask(id: string, patch: CallTaskPatch): Promise<CallTask>;
  /** GET /settings/reminders (§22.6.4, owner/admin). */
  getReminderSettings(): Promise<ReminderSettings>;
  /** PUT /settings/reminders (§22.6.4, лише owner): 422 settings_incomplete|invalid_quiet_hours. */
  saveReminderSettings(s: ReminderSettings): Promise<ReminderSettings>;
  /** GET /conversations (§22.6.5): `needs_reply` - найдовше очікувані першими; `meta.needsReplyCount` для бейджа меню. */
  getConversations(q: ConversationListQuery): Promise<ConversationList>;
  /** GET /conversations/{id} (§22.6.5): повідомлення від старих до нових; 404 conversation_not_found. */
  getConversation(id: string, q?: { before?: string; take?: number }): Promise<ConversationDetail>;
  /**
   * POST /conversations/{id}/reply (§22.6.5) з `Idempotency-Key` (8..128): 400 idempotency_key_required, 404, 409 channel_inactive|
   * channel_unsupported|idempotency_conflict, 422 invalid_text, 429 rate_limited. Повтор того самого ключа й тексту = те саме повідомлення.
   */
  replyToConversation(id: string, text: string, idempotencyKey: string): Promise<ReplyResult>;
  /** POST /conversations/{id}/resume-ai (§22.6.5): 204, ідемпотентно. */
  resumeConversationAi(id: string): Promise<void>;
  /** POST /conversations/{id}/resolve (TASK-793): без тіла; закрити очікування без відповіді (ШІ active, held -> processed). 403/404/409/429. Відповідь толерантно ігнорується. */
  resolveConversation(id: string): Promise<void>;
  /** POST /conversations/{id}/read (§22.6.5): 204. */
  markConversationRead(id: string, upToMessageId?: string): Promise<void>;

  // ---------- §26: згода, розсилки, аудиторії, купони (ADR-015) ----------
  // Кампанії й купони: owner/admin, модуль `beauty_marketing` + `Marketing:Enabled` (інакше 403 module_disabled | marketing_disabled);
  // specialist -> 403 forbidden_role. Згода в картці працює й за `Marketing:Enabled=false`. Усі відповіді `no-store`.
  /** GET /promotions (§26.11.3): `access`, `activeCampaigns`. */
  getPromotions(): Promise<PromotionItem[]>;
  /** GET /coupons?promotionId&kind=shared (§26.11.4): загальні коди (код публічний за природою). */
  getCoupons(q?: { promotionId?: string }): Promise<SharedCoupon[]>;
  /** POST /coupons: 422 invalid_code|invalid_period|invalid_max_uses|promotion_not_code_only, 409 coupon_code_taken. */
  createCoupon(input: CouponInput): Promise<SharedCoupon>;
  /** PATCH /coupons/{id} {status:"revoked"}: ідемпотентно. */
  revokeCoupon(id: string): Promise<SharedCoupon>;
  /** GET /clients/{id}/coupons (§26.11.4): персональні купони клієнта лише з `codeHint` (повного коду немає). */
  getClientCoupons(clientId: string): Promise<ClientCoupon[]>;
  /** POST /coupons/check (усі ролі персоналу): 409 coupon_not_found|expired|not_started|exhausted|revoked|foreign_client|already_used, promotion_inactive; 429. */
  checkCoupon(input: CouponCheckInput): Promise<CouponCheck>;
  /** POST /marketing/audience/preview (§26.11.3): лише кількості й розбивка виключень, без ПД. 429 (20/хв). */
  previewAudience(q: AudienceQueryInput): Promise<AudiencePreview>;
  /** GET /campaigns?status&cursor&take=50 */
  getCampaigns(q?: CampaignListQuery): Promise<CampaignList>;
  /** GET /campaigns/{id}: 404 campaign_not_found. */
  getCampaign(id: string): Promise<CampaignDto>;
  /** POST /campaigns -> 201 draft; 422 коди §26.7. */
  createCampaign(r: CampaignRequest): Promise<CampaignDto>;
  /** PUT /campaigns/{id}: повна заміна draft (`revision+1`); інакше 409 campaign_not_draft. */
  updateCampaign(id: string, r: CampaignRequest): Promise<CampaignDto>;
  /** DELETE /campaigns/{id}: лише draft -> 204. */
  deleteCampaign(id: string): Promise<void>;
  /** GET /campaigns/{id}/preview: приклад тексту, кнопки, кількості, ліміти, вікно, попередження. */
  getCampaignPreview(id: string): Promise<CampaignPreview>;
  /**
   * POST /campaigns/{id}/confirm (§26.7) з `Idempotency-Key` (8..128): обов'язкове підтвердження людиною. Помилки: 409 campaign_changed|
   * campaign_not_draft|audience_empty|audience_too_large|promotion_inactive|idempotency_conflict, 422 invalid_schedule, 400 idempotency_key_required.
   */
  confirmCampaign(id: string, input: ConfirmCampaignInput, idempotencyKey: string): Promise<CampaignDto>;
  /** POST /campaigns/{id}/cancel: ідемпотентно; 409 campaign_finished. */
  cancelCampaign(id: string): Promise<CampaignDto>;
  /** GET /campaigns/{id}/stats (§26.11.5): лише кількості; `viewed` завжди null. */
  getCampaignStats(id: string): Promise<CampaignStats>;
  /** GET /campaigns/{id}/coupons/summary: без кодів. */
  getCampaignCouponSummary(id: string): Promise<CouponSummary>;
  /** GET /clients/{id}/marketing-consent (§26.11.2, owner/admin): стан, журнал (≤50). */
  getMarketingConsent(clientId: string): Promise<MarketingConsent>;
  /**
   * PUT /clients/{id}/marketing-consent: адмін знижує завжди, підвищує лише з `basis`+`basisNote` і за `Marketing:AdminGrantEnabled`.
   * 404 client_not_found; 422 invalid_level|channel_unsupported|basis_required|invalid_basis|invalid_basis_note; 409 admin_grant_disabled.
   */
  setMarketingConsent(clientId: string, input: ConsentInput): Promise<MarketingConsent>;
  // ---------- §32: склад (ADR-025, модуль `beauty_inventory`; всі відповіді `no-store`) ----------
  // Права: `inventory.view|manage|costs` (§32.2). Без `costs` ціни (†) у відповідях відсутні. Заклад поза scope -> 404 location_not_found.
  /** GET /inventory/scope: заклади scope з `canManage`, рівень цін, `canEditCatalog`, `expiringSoonDays`. */
  getInventoryScope(): Promise<InventoryScope>;
  /** GET /inventory/products (§32.5): `active` за замовчуванням `true`. */
  getInventoryProducts(q?: ProductQuery): Promise<Page<Product>>;
  /** GET /inventory/products/{id}: з `locationSettings` закладів scope. 404 product_not_found. */
  getInventoryProduct(id: string): Promise<ProductDetail>;
  /** GET /inventory/categories?includeArchived: плоский список вузлів дерева категорій (до 3 рівнів). */
  getInventoryCategories(includeArchived?: boolean): Promise<Category[]>;
  /** POST /inventory/categories (`inventory.manage`): 409 category_name_taken, 422 category_depth|category_limit|category_not_found|category_archived|invalid_name. */
  createInventoryCategory(r: CategoryRequest): Promise<Category>;
  /** PUT /inventory/categories/{id}: перейменування / порядок. 404/422 category_not_found, 409 category_name_taken. */
  updateInventoryCategory(id: string, r: { name: string; sortOrder?: number }): Promise<Category>;
  /** PUT /inventory/categories/{id}/move: 422 category_cycle|category_depth|category_archived, 409 category_name_taken. */
  moveInventoryCategory(id: string, r: { parentId?: string | null; sortOrder?: number }): Promise<Category>;
  /** POST /inventory/categories/{id}/archive | /unarchive. */
  archiveInventoryCategory(id: string): Promise<Category>;
  unarchiveInventoryCategory(id: string): Promise<Category>;
  /** DELETE /inventory/categories/{id}: 409 category_not_empty (є підкатегорії або товари). */
  deleteInventoryCategory(id: string): Promise<void>;
  /** POST /inventory/products (`inventory.manage = all`): 409 barcode_taken, 422 invalid_*, 403 forbidden_role (`purchasePrice` без `costs = edit`). */
  createInventoryProduct(r: ProductRequest): Promise<Product>;
  /** PUT /inventory/products/{id}: + 422 unit_locked. */
  updateInventoryProduct(id: string, r: ProductRequest): Promise<Product>;
  /** PUT /inventory/products/{id}/locations/{locationId}: `minStock: null` = успадкувати мережевий. */
  setProductLocationSetting(productId: string, locationId: string, input: { minStock: number | null; isEnabled: boolean }): Promise<LocationSetting>;
  /** DELETE /inventory/products/{id}/locations/{locationId}: 404 setting_not_found. */
  deleteProductLocationSetting(productId: string, locationId: string): Promise<void>;
  /** GET /inventory/suppliers (`inventory.manage >= own`). */
  getSuppliers(q?: SupplierQuery): Promise<Page<Supplier>>;
  /** POST /inventory/suppliers: 409 supplier_name_taken. */
  createSupplier(r: SupplierRequest): Promise<Supplier>;
  /** PUT /inventory/suppliers/{id}. */
  updateSupplier(id: string, r: SupplierRequest): Promise<Supplier>;
  /** GET /inventory/stock: `locationId` обов'язковий (422 location_required). */
  getStock(q: StockQuery): Promise<StockPage>;
  /** GET /inventory/stock/summary: усі заклади scope («Усі заклади»). */
  getStockSummary(q?: StockSummaryQuery): Promise<StockSummaryPage>;
  /** GET /inventory/batches: FEFO-порядок; статус за датою закладу. */
  getBatches(q?: BatchQuery): Promise<Page<Batch>>;
  /** GET /inventory/movements: за замовчуванням останні 30 діб. */
  getMovements(q?: MovementQuery): Promise<Page<Movement>>;
  /** GET /inventory/operations. */
  getOperations(q?: OperationQuery): Promise<Page<OperationSummary>>;
  /** GET /inventory/operations/{id}: 404 operation_not_found. */
  getOperation(id: string): Promise<Operation>;
  /**
   * POST /inventory/receipts з обов'язковим `Idempotency-Key`: 201 або 200 (повтор). 422 product_inactive|product_disabled_for_location|
   * location_inactive|supplier_inactive|expiry_required|invalid_expiry|invalid_*; 403 forbidden_role (`unitCost` без `costs = edit`).
   */
  createReceipt(r: ReceiptRequest, idempotencyKey: string): Promise<Operation>;
  /** POST /inventory/write-offs (`Idempotency-Key`): 422 insufficient_stock|note_required|batch_not_found(404). */
  createWriteOff(r: WriteOffRequest, idempotencyKey: string): Promise<Operation>;
  /** POST /inventory/transfers (`Idempotency-Key`): 422 insufficient_stock|batch_expired|same_location|location_inactive. */
  createTransfer(r: TransferRequest, idempotencyKey: string): Promise<Operation>;
  /** GET /inventory/stocktakes. */
  getStocktakes(q?: StocktakeQuery): Promise<Page<StocktakeSummary>>;
  /** POST /inventory/stocktakes: чернетка; 409 stocktake_in_progress (+`stocktakeId`), 422 too_many_lines. */
  createStocktake(input: { locationId: string; note?: string; productIds?: string[] }): Promise<Stocktake>;
  /** GET /inventory/stocktakes/{id}. */
  getStocktake(id: string): Promise<Stocktake>;
  /** PUT /inventory/stocktakes/{id}/lines: 409 stocktake_version_conflict (+`currentVersion`)|stocktake_closed. */
  saveStocktakeLines(id: string, expectedVersion: number, lines: StocktakeLineInput[]): Promise<Stocktake>;
  /** POST /inventory/stocktakes/{id}/post: 422 stocktake_empty|expiry_required; 409 stocktake_closed|stocktake_version_conflict. */
  postStocktake(id: string, expectedVersion: number): Promise<StocktakePostResult>;
  /** POST /inventory/stocktakes/{id}/cancel. */
  cancelStocktake(id: string, expectedVersion: number): Promise<Stocktake>;

  // ---------- етап 2a (ADR-025 §11, §32.15–§32.25): норми матеріалів і сповіщення ----------
  /** GET /inventory/service-materials?includeInactive: лише послуги, що мають хоч один набір (`inventory.view >= own`). */
  getServiceMaterialsList(includeInactive?: boolean): Promise<ServiceMaterials[]>;
  /** GET /inventory/services/{serviceId}/materials: наборів немає -> `network: null, locations: []`; 404 service_not_found. */
  getServiceMaterials(serviceId: string): Promise<ServiceMaterials>;
  /**
   * PUT /inventory/services/{serviceId}/materials (`inventory.manage = all`, owner/admin; deputy -> 403 forbidden_role): повна заміна набору.
   * 422 product_not_for_salon_use|product_inactive|product_disabled_for_location|invalid_quantity|too_many_lines|duplicate_line|invalid_lines (з `line`),
   * 404 service_not_found|location_not_found|product_not_found. Порожній `lines` - лише для override закладу.
   */
  setServiceMaterials(serviceId: string, r: MaterialSetRequest): Promise<MaterialSet>;
  /** DELETE /inventory/services/{serviceId}/materials?locationId=: без `locationId` - мережевий набір; 404 materials_not_found. */
  deleteServiceMaterials(serviceId: string, locationId: string | null): Promise<void>;
  /** GET /inventory/alerts: `status` за замовчуванням `active` (open|read); сортування за `createdAt DESC`. */
  getInventoryAlerts(q?: AlertQuery): Promise<Page<StaffAlert>>;
  /** GET /inventory/alerts/unread-count: `{count}` (open у scope, ≤ 1000); `module_disabled` -> бейдж ховається. */
  getInventoryAlertsUnreadCount(): Promise<number>;
  /** POST /inventory/alerts/{id}/read: ідемпотентно; 404 alert_not_found. */
  markInventoryAlertRead(id: string): Promise<StaffAlert>;
  /** POST /inventory/alerts/read-all?locationId: `{updated}`. */
  markAllInventoryAlertsRead(locationId?: string): Promise<number>;

  // ---------- ADR-026 / §33.13: замовлення товарів клієнтами (адмін-API CRM) ----------
  /** GET /inventory/product-orders: `status` за замовчуванням `open`; 404 location_not_found; 422 invalid_status|invalid_range|invalid_cursor. */
  getProductOrders(q?: ProductOrderQuery): Promise<Page<ProductOrderSummary>>;
  /** GET /inventory/product-orders/counts: `{paid}` - оплачені до видачі (бейдж, опитування). */
  getProductOrderCounts(): Promise<ProductOrderCounts>;
  /** GET /inventory/product-orders/{id}: 404 product_order_not_found. */
  getProductOrder(id: string): Promise<ProductOrder>;
  /** POST /inventory/product-orders/{id}/fulfill (`inventory.manage`): `paid` -> `fulfilled`; 409 order_not_paid|refund_in_progress. */
  fulfillProductOrder(id: string): Promise<ProductOrder>;
  /** POST /inventory/product-orders/{id}/cancel `{reason?}`: 409 order_fulfilled|order_closed|payment_in_progress|refund_in_progress; 402 refund_failed; 422 invalid_reason. */
  cancelProductOrder(id: string, reason?: string): Promise<CancelProductOrderResult>;
  /** POST /inventory/product-orders/{id}/reconcile-payment: 409 nothing_to_reconcile|payment_in_progress. */
  reconcileProductOrderPayment(id: string): Promise<ProductOrder>;

  // ---------- етап 2b (ADR-025 §12, §32.24): Telegram-сповіщення власнику (лише owner; все `no-store`) ----------
  /** GET /inventory/telegram/link: стан прив'язки (без chat id і токена). */
  getTelegramLink(): Promise<TelegramLinkStatus>;
  /**
   * POST /inventory/telegram/link (5/год): 201 `{linkId, deepLink, expiresAt}`; `deepLink` показується один раз.
   * 403 telegram_alerts_disabled|forbidden_role, 409 telegram_already_linked, 422 consent_required|consent_version_outdated|
   * telegram_channel_unavailable|telegram_channel_required (`channels`), 404 channel_not_found, 503 telegram_bot_unavailable, 429 rate_limited.
   */
  createTelegramLink(r: TelegramLinkCreateRequest): Promise<TelegramLinkIssued>;
  /** DELETE /inventory/telegram/link: 204, ідемпотентно; працює і при вимкненому прапорі. */
  deleteTelegramLink(): Promise<void>;
}
