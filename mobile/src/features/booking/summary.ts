import { ratingLabel } from "@/lib/specialist";

/** Підпис рейтингу майстра в картці: «★ 4.8 · 4 відгуки» або «Ще немає відгуків». */
export function specialistRatingText(rating: number | null | undefined, count: number | undefined, noReviews: string): string {
  return ratingLabel(rating, count) ?? noReviews;
}

/** Адреса закладу в шапці: «адреса, місто» без порожніх частин і дублів. */
export function locationAddress(address?: string | null, city?: string | null): string {
  const a = address?.trim();
  const c = city?.trim();
  if (a && c && !a.toLowerCase().includes(c.toLowerCase())) return `${c}, ${a}`;
  return a || c || "";
}

/** Вибір у випадаючому списку: значення поза списком ігнорується (лишається поточне). */
export function pickOption<T extends string>(options: readonly T[], current: T, next: string): T {
  return (options as readonly string[]).includes(next) ? (next as T) : current;
}
