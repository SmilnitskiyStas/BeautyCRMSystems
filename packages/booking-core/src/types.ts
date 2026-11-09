import { z } from "zod";

export const reminderOptionSchema = z.enum(["none", "1h", "2h", "24h"]);
export type ReminderOption = z.infer<typeof reminderOptionSchema>;
/** Відповідь API (§22.6.1): запис може мати `custom` (кілька точок, задані адміністратором). Запит приймає лише `reminderOptionSchema`. */
export const appointmentReminderOptionSchema = z.enum(["none", "1h", "2h", "24h", "custom"]);
export type AppointmentReminderOption = z.infer<typeof appointmentReminderOptionSchema>;

export const paymentMethodSchema = z.enum(["card", "cash"]);
export type PaymentMethod = z.infer<typeof paymentMethodSchema>;

/** Заклад (`GET /locations`, §12): адреса й телефон необов'язкові. */
export interface Location {
  id: string;
  name: string;
  address?: string;
  phone?: string;
  /** IANA-зона закладу: від неї рахуємо «сьогодні» для вибору дати. */
  timezone: string;
  /** Щотижневі вихідні закладу (§17): ключі `mon..sun`. Відсутнє = працює 7 днів. */
  closedWeekdays?: string[];
  /** Закриття на дати (§17, повні дні в зоні закладу, включно) на майбутні 366 днів. Причини публічний API не віддає. */
  closures?: { dateFrom: string; dateTo: string }[];
  /** Години роботи закладу (§18.1): `mon..sun` -> інтервали `HH:mm`; `null`/відсутнє = не задано. Слоти вже враховують їх. */
  workingHours?: Partial<Record<string, { from: string; to: string }[]>> | null;
}

export interface Specialist {
  id: string;
  name: string;
  /** Посада (`title` публічного API); може бути порожньою. */
  role: string;
  /** https-URL фото або null/відсутнє. */
  photoUrl?: string | null;
  experienceYears?: number | null;
  bio?: string | null;
  /** Середній рейтинг (1 знак) або null, якщо відгуків немає. */
  rating?: number | null;
  reviewsCount?: number;
  /** Лише для mock: підказка «найближчий запис». Публічний API її не віддає. */
  nextFree?: string;
}

/** Відгук про майстра (`GET /specialists/{id}/reviews`). */
export interface SpecialistReview {
  id: string;
  /** Скорочене ім'я автора, напр. «Анна К.». */
  authorName: string;
  rating: number;
  text: string | null;
  createdAt: string;
}

export interface SpecialistReviewsPage {
  total: number;
  items: SpecialistReview[];
}

export type DayStatus = "available" | "full" | "closed" | "past";

/** День календаря доступності (`GET /availability`). */
export interface DayAvailability {
  date: string;
  status: DayStatus;
  freeSlots: number;
}

export interface AvailabilityQuery {
  locationId: string;
  serviceId: string;
  /** ANY_SPECIALIST_ID/відсутнє = будь-який майстер (без параметра). */
  specialistId?: string;
  /** YYYY-MM-DD */
  from: string;
  /** ≤ 62 */
  days: number;
}

/** Спеціальне значення «будь-який майстер»: для API це відсутність `specialistId`. */
export const ANY_SPECIALIST_ID = "any";

export interface Service {
  id: string;
  name: string;
  description?: string;
  category?: string;
  durationMinutes: number;
  priceOriginal: number;
  priceFinal: number;
  /** Мітка акції (назва + відсоток); є лише для акційних послуг. */
  promoLabel?: string;
  promotionId?: string;
}

/** Умови скасування з API (§11): слоти й записи віддають їх у кожному елементі. Текст у UI будується з цих полів. */
export interface CancellationTerms {
  windowHours: number;
  refundPercentInWindow: number;
  refundPercentOutside: number;
  deductFee: boolean;
  feePercent: number;
  /** Салон може сховати умови від клієнта. Відсутнє в старих відповідях = true. */
  showToClient?: boolean;
  /** Власний текст салону замість автоматичних рядків. Відсутнє/порожнє = null. */
  customText?: string | null;
}

/** Нормалізує умови з API: старі відповіді без нових полів валідні; порожній customText -> null; showToClient=false => customText=null. */
export function normalizeCancellation(c: CancellationTerms | null | undefined): CancellationTerms | undefined {
  if (!c) return undefined;
  const showToClient = c.showToClient !== false;
  const trimmed = typeof c.customText === "string" ? c.customText.trim() : "";
  return { ...c, showToClient, customText: showToClient && trimmed ? trimmed : null };
}

export interface Slot {
  startsAt: string; // ISO зі зсувом закладу
  label: string; // HH:mm у часі закладу
  /** Майстер слота: для «будь-якого майстра» це фактичний майстер, якого сервер запропонував. */
  specialistId: string;
  cancellation?: CancellationTerms;
}

const EMAIL_RE = /^[^\s@]+@[^\s@.]+(\.[^\s@.]+)+$/;

/** Пошта (необов'язкова): trim, ≤ 254, формат; порожнє -> undefined; значення нормалізується до нижнього регістру. */
export const emailSchema = z
  .string()
  .trim()
  .max(254, "Пошта занадто довга")
  .refine((v) => v === "" || EMAIL_RE.test(v), "Вкажіть коректну пошту")
  .transform((v) => (v === "" ? undefined : v.toLowerCase()))
  .optional();

export const EMAIL_REQUIRED_FOR_REMINDER = "Вкажіть пошту, щоб ми могли надіслати нагадування";

/** Правило форми: нагадування (≠ none) потребує пошти. Повертає текст помилки або null. */
export function reminderEmailError(reminder: ReminderOption, email: string | undefined | null): string | null {
  return reminder !== "none" && !(email ?? "").trim() ? EMAIL_REQUIRED_FOR_REMINDER : null;
}

export const clientSchema = z.object({
  name: z.string().trim().min(2, "Вкажіть імʼя").max(100, "Імʼя занадто довге"),
  phone: z
    .string()
    .trim()
    .regex(/^\+?[0-9\s()-]{9,16}$/, "Вкажіть коректний номер телефону"),
  email: emailSchema,
  /** Згода на маркетинг: у мобільному потоці завжди false (окремої згоди немає). */
  marketingConsent: z.boolean().optional(),
});
export type ClientInput = z.infer<typeof clientSchema>;

export const createAppointmentSchema = z.object({
  locationId: z.string().min(1),
  specialistId: z.string().min(1),
  serviceId: z.string().min(1),
  startsAt: z.string().min(1),
  client: clientSchema,
  reminder: reminderOptionSchema,
  paymentMethod: paymentMethodSchema,
  /** Honeypot: люди його не бачать і не заповнюють; непорожнє значення сервер відхиляє (422). */
  website: z.string().optional(),
  /** §26.11.4: загальний промокод закладу (персональні коди в публічній формі не діють). */
  couponCode: z.string().trim().max(24).optional(),
});
export type CreateAppointmentRequest = z.infer<typeof createAppointmentSchema>;

export type AppointmentStatus = "pending" | "confirmed" | "in_progress" | "completed" | "cancelled" | "no_show";

/** Запис для клієнта (`PublicAppointment`, §12): без id запису/клієнта й без PII. Адресується токеном. */
export interface Appointment {
  locationName: string;
  specialistName: string;
  serviceName: string;
  startsAt: string;
  endsAt: string;
  durationMinutes: number;
  status: AppointmentStatus;
  priceOriginal: number;
  priceFinal: number;
  promotionId?: string;
  reminderOption: AppointmentReminderOption;
  paymentMethod?: PaymentMethod;
  paymentStatus?: string;
  cancellation?: CancellationTerms;
  /** §26.11.4: до запису застосовано промокод. */
  couponApplied?: boolean;
}

/** Результат перевірки промокоду (`GET /coupons/check`): ціни без жодних подробиць про код. */
export interface CouponCheckResult {
  outcome: "applied" | "not_applicable" | "not_better";
  priceOriginal: number;
  priceFinal: number;
}

/** Результат створення: `publicToken` — єдиний ключ доступу до запису (у шляху `/book/appointment/{token}`). */
export interface BookingResult {
  token: string;
  appointment: Appointment;
}

export interface CancelResult {
  appointment: Appointment;
  refundAmount: number;
  refundPercent: number;
  feePercent: number;
}

/** Типізована помилка API: HTTP-статус + код `{code,message}` (message сервера користувачу не показуємо). */
export class BookingApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string = code,
    public readonly retryAfterSeconds?: number,
  ) {
    super(message);
    this.name = "BookingApiError";
  }
}
