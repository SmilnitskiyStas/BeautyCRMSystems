import type {
  BookingResult,
  CouponCheckResult,
  CancelResult,
  Appointment,
  CreateAppointmentRequest,
  Location,
  Service,
  Slot,
  Specialist,
} from "../types";

/**
 * Контракт клієнта публічного запису: `/api/public/{tenantSlug}/...` (`.claude/docs/beauty-contracts.md` §12).
 * Реалізації: `http-client.ts` (за замовчуванням) і `mock-client.ts` (`NEXT_PUBLIC_USE_MOCK=1`); вибір — `api/index.ts`.
 */
export interface BookingApi {
  /** GET /locations */
  getLocations(tenant: string): Promise<Location[]>;
  /** GET /locations/{id}/specialists; «будь-який майстер» додає UI/клієнт як першу позицію */
  getSpecialists(tenant: string, locationId: string): Promise<Specialist[]>;
  /** GET /locations/{id}/services[?specialistId] */
  getServices(tenant: string, locationId: string, specialistId: string): Promise<Service[]>;
  /** GET /slots?locationId&serviceId&date[&specialistId]; `specialistId` = ANY_SPECIALIST_ID -> без параметра */
  getSlots(
    tenant: string,
    q: { locationId: string; specialistId: string; serviceId: string; date: string },
  ): Promise<Slot[]>;
  /** POST /appointments з заголовком Idempotency-Key */
  createAppointment(tenant: string, req: CreateAppointmentRequest, idempotencyKey: string): Promise<BookingResult>;
  /**
   * GET /coupons/check?code&locationId&serviceId (§26.11.4): лише загальні коди. 404 `coupon_invalid` - однакове тіло для всіх причин
   * (нема, прострочено, використано, персональний), 429 `rate_limited` (10 перевірок / 10 хв з IP).
   */
  checkCoupon(tenant: string, q: { code: string; locationId: string; serviceId: string }): Promise<CouponCheckResult>;
  /** GET /appointments/{publicToken} */
  getAppointment(tenant: string, token: string): Promise<Appointment>;
  /** POST /appointments/{publicToken}/cancel */
  cancelAppointment(tenant: string, token: string): Promise<CancelResult>;
}
