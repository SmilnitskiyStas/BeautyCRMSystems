import { z } from "zod";

export const reminderOptionSchema = z.enum(["none", "1h", "2h"]);
export type ReminderOption = z.infer<typeof reminderOptionSchema>;

export const paymentMethodSchema = z.enum(["card", "cash"]);
export type PaymentMethod = z.infer<typeof paymentMethodSchema>;

export interface Location {
  id: string;
  name: string;
  address: string;
  hours: string;
  hasPromo: boolean;
}

export interface Specialist {
  id: string;
  name: string;
  role: string;
  nextFree: string;
}

/** Спеціальне значення «будь-який майстер». */
export const ANY_SPECIALIST_ID = "any";

export interface Service {
  id: string;
  name: string;
  durationMinutes: number;
  priceOriginal: number;
  priceFinal: number;
  /** Мітка акції, напр. «−20% до кінця тижня»; є лише для акційних послуг. */
  promoLabel?: string;
  promotionId?: string;
}

export interface Slot {
  startsAt: string; // ISO
  label: string; // HH:mm
}

export const clientSchema = z.object({
  name: z.string().trim().min(2, "Вкажіть імʼя"),
  phone: z
    .string()
    .trim()
    .regex(/^\+?[0-9\s()-]{9,16}$/, "Вкажіть коректний номер телефону"),
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
});
export type CreateAppointmentRequest = z.infer<typeof createAppointmentSchema>;

export interface Appointment {
  id: string;
  locationId: string;
  locationName: string;
  specialistId: string;
  specialistName: string;
  serviceId: string;
  serviceName: string;
  startsAt: string;
  durationMinutes: number;
  status: "pending" | "confirmed" | "completed" | "cancelled" | "no_show";
  source: "online";
  priceOriginal: number;
  priceFinal: number;
  promotionId?: string;
  reminderOption: ReminderOption;
  paymentMethod: PaymentMethod;
}

/** Типізована помилка API (409 перетин слоту, 422 валідація, 404). */
export class BookingApiError extends Error {
  status: 409 | 422 | 404;
  constructor(status: 409 | 422 | 404, message: string) {
    super(message);
    this.name = "BookingApiError";
    this.status = status;
  }
}
