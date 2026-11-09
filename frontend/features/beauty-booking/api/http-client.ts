import { createHttpBookingApi } from "@beauty/booking-core";
import { API_URL } from "@/features/beauty-auth/types";

export * from "@beauty/booking-core";

/** HTTP-клієнт публічного API (§12) для web: `NEXT_PUBLIC_API_URL` + глобальний `fetch` (шукається в момент виклику). */
export const httpBookingApi = createHttpBookingApi({
  baseUrl: API_URL,
  fetch: (url, init) => fetch(url, init),
});
