import {
  BookingApiError,
  type BookingApi,
  type BookingResult,
  type CreateAppointmentRequest,
} from "@beauty/booking-core";
import type { LocalStore } from "@/storage/tokenStore";

/**
 * Створення запису з ідемпотентністю (ADR-021 §4/§5, §12 контракту).
 *  - ключ прив'язаний до відбитка тіла (`createKeyHolder`): повтор після збою мережі/таймауту = той самий ключ,
 *    сервер повертає той самий результат без другого запису; змінені дані = новий ключ;
 *  - `localId` локального запису = ключ -> повторне збереження оновлює, а не дублює;
 *  - `inFlight`: подвійний тап не відправляє другий запит, а чекає перший.
 *  - ключ, що ще не отримав відповіді, зберігається в SecureStore (`pending-idempotency`): якщо застосунок
 *    перезапустили після відправки, повтор з тим самим тілом використає той самий ключ -> без дубля. Очищається
 *    після успіху або остаточної (не-повторюваної) відмови; TTL 24 год.
 * Запис online-only: офлайн-черги немає (Phase 1) - при збої мережі користувач просто повторює.
 */
export function createBookingSubmitter(deps: {
  api: Pick<BookingApi, "createAppointment">;
  store: Pick<LocalStore, "saveAppointment" | "getPendingKey" | "setPendingKey" | "clearPendingKey">;
  generateKey: () => string;
  now?: () => Date;
}) {
  const now = () => deps.now?.() ?? new Date();

  /** Ключ для тіла: pending із сховища (той самий відбиток), інакше новий - і одразу персиститься ДО відправки. */
  async function keyFor(req: CreateAppointmentRequest): Promise<string> {
    const fingerprint = JSON.stringify(req);
    const pending = await deps.store.getPendingKey(now().getTime());
    if (pending && pending.fingerprint === fingerprint) return pending.key;
    const key = deps.generateKey();
    await deps.store.setPendingKey({ fingerprint, key, createdAt: now().getTime() });
    return key;
  }
  let inFlight: Promise<BookingResult> | null = null;

  return {
    submit(tenantSlug: string, req: CreateAppointmentRequest, names: { locationName: string; serviceName: string; specialistName: string }) {
      if (inFlight) return inFlight;
      inFlight = (async () => {
        try {
          const key = await keyFor(req);
          const result = await deps.api.createAppointment(tenantSlug, req, key);
          await deps.store.saveAppointment(
            {
              localId: key,
              tenantSlug,
              locationName: result.appointment.locationName || names.locationName,
              serviceName: result.appointment.serviceName || names.serviceName,
              specialistName: result.appointment.specialistName || names.specialistName,
              startsAt: result.appointment.startsAt,
              status: result.appointment.status,
              createdAt: now().toISOString(),
            },
            result.token,
          );
          await deps.store.clearPendingKey(); // наступний запис - новий намір, новий ключ
          return result;
        } catch (e) {
          // остаточна відмова (4xx) - ключ більше не потрібен; мережа/5xx - лишаємо для повтору
          if (e instanceof BookingApiError && !isRetryable(e)) await deps.store.clearPendingKey();
          throw e;
        } finally {
          inFlight = null;
        }
      })();
      return inFlight;
    },
  };
}

/** Чи безпечно повторити (той самий ключ): мережа/таймаут/5xx. Для 4xx дані треба виправити. */
export const isRetryable = (e: unknown): boolean =>
  e instanceof BookingApiError ? e.status === 0 || e.status >= 500 : e instanceof TypeError;
