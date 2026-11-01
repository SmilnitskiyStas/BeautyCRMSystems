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
}
