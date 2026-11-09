/** Чисті хелпери картки майстра (без Intl). */

/** «1 рік / 2–4 роки / 5+ років» (11–14 -> років); null/0/від'ємне -> null (приховати). */
export function experienceLabel(years: number | null | undefined): string | null {
  if (years == null || !Number.isFinite(years) || years <= 0) return null;
  const n = Math.floor(years);
  const mod100 = n % 100;
  const mod10 = n % 10;
  const word = mod100 >= 11 && mod100 <= 14 ? "років" : mod10 === 1 ? "рік" : mod10 >= 2 && mod10 <= 4 ? "роки" : "років";
  return `${n} ${word} досвіду`;
}

/** «відгук / відгуки / відгуків». */
export function reviewsWord(n: number): string {
  const mod100 = n % 100;
  const mod10 = n % 10;
  if (mod100 >= 11 && mod100 <= 14) return "відгуків";
  if (mod10 === 1) return "відгук";
  if (mod10 >= 2 && mod10 <= 4) return "відгуки";
  return "відгуків";
}

/** «★ 4.8 · 23 відгуки» або null, якщо рейтингу немає. */
export function ratingLabel(rating: number | null | undefined, count: number | undefined): string | null {
  if (rating == null || !count) return null;
  return `★ ${rating.toFixed(1)} · ${count} ${reviewsWord(count)}`;
}

/** Ініціали для аватара-фолбека: до двох перших літер слів («Анна Коваль» -> «АК»). */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  const first = Array.from(parts[0])[0] ?? "";
  const second = parts.length > 1 ? (Array.from(parts[1])[0] ?? "") : "";
  return (first + second).toUpperCase();
}

/** Зірки відгуку: «★★★★☆». */
export const stars = (rating: number): string => {
  const r = Math.max(0, Math.min(5, Math.round(rating)));
  return "★".repeat(r) + "☆".repeat(5 - r);
};

/** ISO -> «12 жовтня 2026» без Intl. */
export function reviewDate(iso: string, months: readonly string[]): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!m) return iso;
  return `${Number(m[3])} ${months[Number(m[2]) - 1] ?? ""} ${m[1]}`;
}
