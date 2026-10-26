import { dateRangeLabel } from "../format";
import type { SeriesGranularity, SeriesPoint } from "../types";

/** Підписи осі графіка динаміки за гранулярністю (§19.2): сервер `label` не віддає, підпис формує клієнт (`uk-UA`). */

const utc = (ymd: string) => Date.UTC(+ymd.slice(0, 4), +ymd.slice(5, 7) - 1, +ymd.slice(8, 10));
const dm = (ymd: string) => `${ymd.slice(8, 10)}.${ymd.slice(5, 7)}`;

export function pointLabel(p: Pick<SeriesPoint, "from" | "to">, g: SeriesGranularity): string {
  if (g === "month") {
    const m = new Date(utc(p.from)).toLocaleDateString("uk-UA", { month: "short", timeZone: "UTC" });
    return `${m} ${p.from.slice(0, 4)}`;
  }
  if (g === "week" && p.from !== p.to) return `${dm(p.from)}–${dm(p.to)}`;
  return dm(p.from);
}

/** Повний підпис для зчитувачів з екрана: «5 жовт. – 11 жовт.», «жовтень 2026». */
export function pointFullLabel(p: Pick<SeriesPoint, "from" | "to">, g: SeriesGranularity): string {
  if (g === "month") {
    return new Date(utc(p.from)).toLocaleDateString("uk-UA", { month: "long", year: "numeric", timeZone: "UTC" }).replace(/\s?р\.$/, "");
  }
  return dateRangeLabel(p.from, p.to);
}

export const GRANULARITY_TITLE: Record<SeriesGranularity, string> = { day: "по днях", week: "по тижнях", month: "по місяцях" };

/** Тисячі гривень для підпису стовпця: 4,2 / 38 / 1 204. */
export function thousands(revenue: number): string {
  const k = revenue / 1000;
  return new Intl.NumberFormat("uk-UA", { maximumFractionDigits: k < 100 ? 1 : 0 }).format(k);
}
