import { API_URL } from "@/features/beauty-auth/types";
import {
  ANY_SPECIALIST_ID,
  type Appointment,
  type AppointmentStatus,
  BookingApiError,
  type CancellationTerms,
  type Location,
  type PaymentMethod,
  type Service,
  type Slot,
  type Specialist,
  reminderOptionSchema,
} from "../types";
import type { BookingApi } from "./client";

/**
 * HTTP-клієнт публічного API (§12). Без JWT/cookie (`credentials: "omit"`): tenant задається slug у шляху.
 * Токен запису (`publicToken`) лише у шляху; відповіді з ним `no-store`, запити без Referer.
 * Помилки: `{code,message}` -> `BookingApiError`; тексти формує `humanizeBookingError`.
 */

// ---------- DTO backend (PublicBookingModels.cs) ----------

interface PublicLocationDto {
  id: string;
  name: string;
  address: string | null;
  phone: string | null;
  timezone: string;
  /** §17 */
  closedWeekdays?: string[] | null;
  closures?: { dateFrom: string; dateTo: string }[] | null;
}
interface PublicSpecialistDto {
  id: string;
  name: string;
  title: string | null;
  photoUrl: string | null;
}
interface PublicServiceDto {
  id: string;
  name: string;
  description: string | null;
  category: string | null;
  durationMinutes: number;
  priceOriginal: number;
  priceFinal: number;
  promotionId: string | null;
  promotionName: string | null;
}
interface FreeSlotDto {
  specialistId: string;
  startsAt: string;
  endsAt: string;
  label: string;
  cancellation: CancellationTerms | null;
}
export interface PublicAppointmentDto {
  locationId: string;
  locationName: string;
  specialistId: string;
  specialistName: string;
  serviceId: string;
  serviceName: string;
  startsAt: string;
  endsAt: string;
  durationMinutes: number;
  status: string;
  priceOriginal: number;
  priceFinal: number;
  promotionId: string | null;
  reminderOption: string;
  paymentMethod: string | null;
  paymentStatus: string | null;
  cancellation: CancellationTerms | null;
}
interface PublicBookingCreatedDto {
  publicToken: string;
  appointment: PublicAppointmentDto;
}
interface PublicCancelResultDto {
  appointment: PublicAppointmentDto;
  refundAmount: number;
  refundPercent: number;
  feePercent: number;
}

// ---------- мапери ----------

const STATUSES: AppointmentStatus[] = ["pending", "confirmed", "in_progress", "completed", "cancelled", "no_show"];

export function toLocation(l: PublicLocationDto): Location {
  return {
    id: l.id,
    name: l.name,
    address: l.address ?? undefined,
    phone: l.phone ?? undefined,
    timezone: l.timezone,
    closedWeekdays: l.closedWeekdays ?? [],
    closures: (l.closures ?? []).map((c) => ({ dateFrom: c.dateFrom.slice(0, 10), dateTo: c.dateTo.slice(0, 10) })),
  };
}

export const toSpecialist = (s: PublicSpecialistDto): Specialist => ({ id: s.id, name: s.name, role: s.title ?? "" });

/** Мітка акції: назва акції + відсоток знижки з різниці цін. */
export function promoLabel(s: Pick<PublicServiceDto, "priceOriginal" | "priceFinal" | "promotionName" | "promotionId">): string | undefined {
  if (!s.promotionId && !s.promotionName) return undefined;
  const pct = s.priceOriginal > 0 ? Math.round((1 - s.priceFinal / s.priceOriginal) * 100) : 0;
  const parts = [s.promotionName, pct > 0 ? `−${pct}%` : undefined].filter(Boolean);
  return parts.length ? parts.join(" · ") : undefined;
}

export function toService(s: PublicServiceDto): Service {
  return {
    id: s.id,
    name: s.name,
    description: s.description ?? undefined,
    category: s.category ?? undefined,
    durationMinutes: s.durationMinutes,
    priceOriginal: s.priceOriginal,
    priceFinal: s.priceFinal,
    promoLabel: promoLabel(s),
    promotionId: s.promotionId ?? undefined,
  };
}

/**
 * Слоти. Для «будь-якого майстра» сервер віддає слоти всіх майстрів: однакові `startsAt` зводимо до одного
 * (перший майстер), сортуємо за часом. Конкретний `specialistId` слота потрібен для POST.
 */
export function toSlots(rows: FreeSlotDto[]): Slot[] {
  const seen = new Set<string>();
  const out: Slot[] = [];
  for (const r of [...rows].sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt))) {
    const key = new Date(r.startsAt).toISOString();
    if (seen.has(key)) continue;
    seen.add(key);
    out.push({ startsAt: r.startsAt, label: r.label, specialistId: r.specialistId, cancellation: r.cancellation ?? undefined });
  }
  return out;
}

export function toAppointment(a: PublicAppointmentDto): Appointment {
  const reminder = reminderOptionSchema.safeParse(a.reminderOption);
  return {
    locationName: a.locationName,
    specialistName: a.specialistName,
    serviceName: a.serviceName,
    startsAt: a.startsAt,
    endsAt: a.endsAt,
    durationMinutes: a.durationMinutes,
    status: (STATUSES as string[]).includes(a.status) ? (a.status as AppointmentStatus) : "pending",
    priceOriginal: a.priceOriginal,
    priceFinal: a.priceFinal,
    promotionId: a.promotionId ?? undefined,
    reminderOption: reminder.success ? reminder.data : "none",
    paymentMethod: a.paymentMethod === "card" || a.paymentMethod === "cash" ? (a.paymentMethod as PaymentMethod) : undefined,
    paymentStatus: a.paymentStatus ?? undefined,
    cancellation: a.cancellation ?? undefined,
  };
}

// ---------- транспорт ----------

const base = (tenant: string) => `${API_URL}/api/public/${encodeURIComponent(tenant)}`;

async function readError(res: Response): Promise<BookingApiError> {
  let code = "";
  try {
    const body = (await res.json()) as { code?: unknown };
    if (typeof body.code === "string") code = body.code;
  } catch {
    /* не JSON */
  }
  const retry = Number(res.headers.get("Retry-After"));
  return new BookingApiError(res.status, code, code || res.statusText, Number.isFinite(retry) && retry > 0 ? retry : undefined);
}

async function request<T>(
  tenant: string,
  path: string,
  init: { method?: "GET" | "POST"; body?: unknown; headers?: Record<string, string> } = {},
): Promise<T> {
  const headers: Record<string, string> = { Accept: "application/json", ...init.headers };
  if (init.body !== undefined) headers["Content-Type"] = "application/json";
  let res: Response;
  try {
    res = await fetch(`${base(tenant)}${path}`, {
      method: init.method ?? "GET",
      headers,
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
      credentials: "omit",
      cache: "no-store",
      referrerPolicy: "no-referrer",
    });
  } catch {
    throw new BookingApiError(0, "api_unreachable", "network");
  }
  if (!res.ok) throw await readError(res);
  return (await res.json()) as T;
}

const q = (params: Record<string, string | undefined>) => {
  const s = Object.entries(params)
    .filter((e): e is [string, string] => e[1] !== undefined)
    .map(([k, v]) => `${k}=${encodeURIComponent(v)}`)
    .join("&");
  return s ? `?${s}` : "";
};

export const httpBookingApi: BookingApi = {
  getLocations: async (tenant) => (await request<PublicLocationDto[]>(tenant, "/locations")).map(toLocation),

  getSpecialists: async (tenant, locationId) => {
    const rows = await request<PublicSpecialistDto[]>(tenant, `/locations/${encodeURIComponent(locationId)}/specialists`);
    const any: Specialist = {
      id: ANY_SPECIALIST_ID,
      name: "Будь-який спеціаліст",
      role: "Підберемо найближчий вільний час",
    };
    return rows.length > 0 ? [any, ...rows.map(toSpecialist)] : [];
  },

  getServices: async (tenant, locationId, specialistId) => {
    const rows = await request<PublicServiceDto[]>(
      tenant,
      `/locations/${encodeURIComponent(locationId)}/services${q({ specialistId: specialistId === ANY_SPECIALIST_ID ? undefined : specialistId })}`,
    );
    return rows.map(toService);
  },

  getSlots: async (tenant, p) =>
    toSlots(
      await request<FreeSlotDto[]>(
        tenant,
        `/slots${q({
          locationId: p.locationId,
          serviceId: p.serviceId,
          date: p.date,
          specialistId: p.specialistId === ANY_SPECIALIST_ID ? undefined : p.specialistId,
        })}`,
      ),
    ),

  async createAppointment(tenant, req, idempotencyKey) {
    const r = await request<PublicBookingCreatedDto>(tenant, "/appointments", {
      method: "POST",
      headers: { "Idempotency-Key": idempotencyKey },
      body: {
        locationId: req.locationId,
        specialistId: req.specialistId,
        serviceId: req.serviceId,
        startsAt: req.startsAt,
        client: { name: req.client.name, phone: req.client.phone },
        reminder: req.reminder,
        paymentMethod: req.paymentMethod,
        website: req.website ?? "",
      },
    });
    return { token: r.publicToken, appointment: toAppointment(r.appointment) };
  },

  getAppointment: async (tenant, token) =>
    toAppointment(await request<PublicAppointmentDto>(tenant, `/appointments/${encodeURIComponent(token)}`)),

  async cancelAppointment(tenant, token) {
    const r = await request<PublicCancelResultDto>(tenant, `/appointments/${encodeURIComponent(token)}/cancel`, {
      method: "POST",
    });
    return {
      appointment: toAppointment(r.appointment),
      refundAmount: r.refundAmount,
      refundPercent: r.refundPercent,
      feePercent: r.feePercent,
    };
  },
};
