import type { BookingDraft } from "@/storage/tokenStore";
import type { ResolvedLocation } from "@/lib/locationSource";
import type { Service, Specialist } from "@beauty/booking-core";

interface DraftSnapshot {
  location: ResolvedLocation | null;
  service: Service | null;
  specialist: Specialist | null;
  date: string | null;
}

/** Незавершеним вважаємо запис з обраною послугою: лише заклад без послуги покриває список «Нещодавні заклади». */
export function toBookingDraft(d: DraftSnapshot, now: number = Date.now()): BookingDraft | null {
  if (!d.location || !d.service) return null;
  const { id, name, durationMinutes, priceOriginal, priceFinal, description, category, promoLabel, promotionId } = d.service;
  const sp = d.specialist;
  return {
    publicCode: d.location.publicCode,
    locationName: d.location.name,
    service: { id, name, durationMinutes, priceOriginal, priceFinal, description, category, promoLabel, promotionId },
    // nextFree - підказка mock, у сховище не потрапляє
    specialist: sp
      ? { id: sp.id, name: sp.name, role: sp.role, photoUrl: sp.photoUrl, experienceYears: sp.experienceYears, bio: sp.bio, rating: sp.rating, reviewsCount: sp.reviewsCount }
      : null,
    date: d.date,
    updatedAt: now,
  };
}

/** Ключ змін, що варті запису: зміна лише слота чи часу оновлення не переписує сховище. */
export function draftKey(d: BookingDraft | null): string {
  return d ? [d.publicCode, d.service.id, d.specialist?.id ?? "any", d.date ?? ""].join("|") : "";
}

/** Дата в минулому (відносно сьогодні в закладі) при відновленні відкидається. */
export function restorableDate(date: string | null, today: string): string | null {
  return date && date >= today ? date : null;
}

/** Куди вести при «Продовжити запис»: обрана дата -> крок часу, інакше -> крок майстра. */
export function resumeStep(d: Pick<BookingDraft, "date">, today: string): "/specialist" | "/time" {
  return restorableDate(d.date, today) ? "/time" : "/specialist";
}
