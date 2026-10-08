import { USE_MOCK } from "@/features/beauty-auth/types";
import type { BookingApi } from "./client";
import { httpBookingApi } from "./http-client";
import { mockBookingApi } from "./mock-client";

export type { BookingApi } from "./client";

/** Реальний публічний API (`NEXT_PUBLIC_API_URL`) за замовчуванням; `NEXT_PUBLIC_USE_MOCK=1` - демо без backend. */
export const bookingApi: BookingApi = USE_MOCK ? mockBookingApi : httpBookingApi;
