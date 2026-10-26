import { durationLabel, money } from "../format";
import type { LocationMetrics, LocationStat, NetworkAnalytics, PromotionStat } from "../types";
import { computeDelta, computeShareDelta, describeDelta, type DeltaKind, type DeltaValue, type DeltaView, type Polarity } from "./delta";

export interface AnalyticsKpi {
  label: string;
  value: string;
  note?: string;
  /** `undefined` - порівняння вимкнено; `DeltaView` (також «—») - увімкнено. */
  delta?: DeltaView;
}

const NBSP = / | /g;
const nf = (n: number) => n.toLocaleString("uk-UA").replace(NBSP, " ");

/** Частка 0..1 -> «12,5%»; `null` -> «—». */
export const percentLabel = (share: number | null | undefined, digits = 0): string =>
  share == null ? "—" : `${(Math.round(share * 100 * 10 ** digits) / 10 ** digits).toLocaleString("uk-UA", { maximumFractionDigits: digits })}%`;

function withDelta(
  on: boolean,
  d: DeltaValue | null,
  kind: DeltaKind,
  polarity: Polarity,
): DeltaView | undefined {
  return on ? describeDelta(d, kind, polarity) : undefined;
}

type Prev<T> = T | null | undefined;
const diff = (cur: number, prev: Prev<number>, on: boolean, kind: DeltaKind, polarity: Polarity = "up-good") =>
  withDelta(on, computeDelta(cur, prev ?? null), kind, polarity);
const diffShare = (cur: number | null, prev: Prev<number | null>, on: boolean, polarity: Polarity) =>
  withDelta(on, computeShareDelta(cur, prev ?? null), "share", polarity);

export function networkKpis(net: NetworkAnalytics, compare: boolean): AnalyticsKpi[] {
  const t = net.totals;
  const p = compare ? net.previous : null;
  const on = compare;
  return [
    { label: "Виручка мережі", value: money(t.revenue), note: `завершено: ${t.completed}`, delta: diff(t.revenue, p?.revenue, on, "money") },
    { label: "Записів", value: nf(t.appointments), note: "усі статуси", delta: diff(t.appointments, p?.appointments, on, "count") },
    { label: "Середній чек", value: money(Math.round(t.averageCheck)), note: "по мережі", delta: diff(t.averageCheck, p?.averageCheck, on, "money") },
    { label: "Частка скасувань", value: percentLabel(t.cancellationRate, 1), note: `скасовано: ${t.cancelled}`, delta: diffShare(t.cancellationRate, p?.cancellationRate, on, "down-good") },
    { label: "Скасовано", value: nf(t.cancelled), delta: diff(t.cancelled, p?.cancelled, on, "count", "down-good") },
    { label: "Не прийшли", value: nf(t.noShow), delta: diff(t.noShow, p?.noShow, on, "count", "down-good") },
    { label: "Нові клієнти", value: nf(t.newClients), delta: diff(t.newClients, p?.newClients, on, "count") },
  ];
}

export function locationKpis(s: LocationStat, compare: boolean): AnalyticsKpi[] {
  const p: LocationMetrics | null = compare ? s.previous : null;
  const on = compare;
  return [
    { label: "Виручка закладу", value: money(s.revenue), note: `завершено: ${s.completed}`, delta: diff(s.revenue, p?.revenue, on, "money") },
    { label: "Записів", value: nf(s.appointments), delta: diff(s.appointments, p?.appointments, on, "count") },
    { label: "Скасовано", value: nf(s.cancelled), delta: diff(s.cancelled, p?.cancelled, on, "count", "down-good") },
    { label: "Не прийшли", value: nf(s.noShow), delta: diff(s.noShow, p?.noShow, on, "count", "down-good") },
    {
      label: "Завантаження",
      value: percentLabel(s.load, 0),
      note: s.availableMinutes > 0 ? `зайнято ${durationLabel(s.busyMinutes) || "0 хв"} з ${durationLabel(s.availableMinutes)}` : "немає доступних годин у графіках",
      delta: diffShare(s.load, p?.load, on, "up-good"),
    },
  ];
}

export function promotionKpis(rows: PromotionStat[], compare: boolean): AnalyticsKpi[] {
  const sum = (pick: (r: PromotionStat) => number) => rows.reduce((a, r) => a + pick(r), 0);
  const prev = (pick: (r: { uses: number; revenue: number; discountTotal: number }) => number) =>
    compare ? rows.reduce((a, r) => a + (r.previous ? pick(r.previous) : 0), 0) : null;
  return [
    { label: "Використань акцій", value: nf(sum((r) => r.uses)), delta: diff(sum((r) => r.uses), prev((r) => r.uses), compare, "count") },
    { label: "Виручка по акціях", value: money(sum((r) => r.revenue)), delta: diff(sum((r) => r.revenue), prev((r) => r.revenue), compare, "money") },
    { label: "Віддано знижками", value: money(sum((r) => r.discountTotal)), note: "вартість знижок", delta: diff(sum((r) => r.discountTotal), prev((r) => r.discountTotal), compare, "money", "neutral") },
  ];
}
