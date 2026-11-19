import { DETAILS_CSV_MAX_ROWS } from "./csv";
import type { AnalyticsDetails, AnalyticsDetailsQuery, DetailsItem, LocationId, SeriesGranularity, SeriesPoint } from "../types";

/** Що саме відкриває панель «Деталі»: кошик графіка (§24.8.1). */
export interface DetailsTarget {
  from: string;
  to: string;
  granularity: SeriesGranularity;
}

export const targetOf = (p: Pick<SeriesPoint, "from" | "to">, granularity: SeriesGranularity): DetailsTarget => ({ from: p.from, to: p.to, granularity });

/** День -> `date`; тиждень/місяць -> `from`+`to` (§24.6.3). */
export function detailsParams(t: DetailsTarget, locationId?: LocationId | null): AnalyticsDetailsQuery {
  const base: AnalyticsDetailsQuery = t.granularity === "day" ? { date: t.from } : { from: t.from, to: t.to };
  return locationId ? { ...base, locationId } : base;
}

/** Ключ React Query `['analytics-details', from, to, locationId]` (§24.8.1). */
export const detailsKey = (t: DetailsTarget, locationId: LocationId | null) => ["analytics-details", t.from, t.to, locationId] as const;

export interface CollectedDetails {
  items: DetailsItem[];
  /** Є ще записи понад ліміт CSV. */
  truncated: boolean;
}

/**
 * Послідовно довантажує сторінки до `max` записів (за замовчуванням 10 × 200 = 2000). Сторінки йдуть по одній, щоб не
 * перевищити ліміт запитів. Курсор, що не змінюється, зупиняє цикл (захист від зациклення).
 */
export async function collectDetailsItems(
  loaded: { items: DetailsItem[]; hasMore: boolean; nextCursor?: string },
  fetchPage: (cursor: string) => Promise<Pick<AnalyticsDetails, "items" | "hasMore" | "nextCursor">>,
  max: number = DETAILS_CSV_MAX_ROWS,
): Promise<CollectedDetails> {
  const items = [...loaded.items];
  let hasMore = loaded.hasMore;
  let cursor = loaded.nextCursor;
  while (hasMore && items.length < max && cursor) {
    const page = await fetchPage(cursor);
    items.push(...page.items);
    hasMore = page.hasMore;
    cursor = page.nextCursor && page.nextCursor !== cursor ? page.nextCursor : undefined;
  }
  const truncated = items.length > max || (hasMore && items.length >= max) || (hasMore && !cursor);
  return { items: items.slice(0, max), truncated };
}
