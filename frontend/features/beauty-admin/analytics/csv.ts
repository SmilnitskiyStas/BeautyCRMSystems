import { computeDelta } from "./delta";
import { marginOf } from "./finance";
import type { AnalyticsDetails, DetailsItem, LocationBreakdown, LocationStat, NetworkAnalytics, NetworkSeries, PromotionStat } from "../types";

/**
 * Експорт CSV лише на клієнті (§19.5): UTF-8 з BOM, роздільник `;`, рядки CRLF, екранування RFC 4180,
 * захист від CSV-injection для ТЕКСТОВИХ комірок. Серіалізатор - чиста функція. Персональних даних клієнтів
 * (імена, телефони, email, id) і GUID-ів у файлі немає: лише агрегати й назви закладів/майстрів/послуг/акцій.
 */

export type CsvCell = { k: "text"; v: string } | { k: "int"; v: number | null } | { k: "money"; v: number | null } | { k: "pct"; v: number | null } | { k: "date"; v: string };

export const text = (v: string): CsvCell => ({ k: "text", v });
export const int = (v: number | null | undefined): CsvCell => ({ k: "int", v: v ?? null });
export const money = (v: number | null | undefined): CsvCell => ({ k: "money", v: v ?? null });
/** Відсотки: число (вже ×100), `%` лише в заголовку. */
export const pct = (v: number | null | undefined): CsvCell => ({ k: "pct", v: v ?? null });
export const date = (v: string): CsvCell => ({ k: "date", v });

const BOM = "﻿";
const FORMULA_START = /^[=+\-@\t\r]/;

/** Префікс `'` для текстової комірки, що починається з `=`, `+`, `-`, `@`, TAB або CR. */
export const defuse = (s: string): string => (FORMULA_START.test(s) ? `'${s}` : s);

/** RFC 4180: у лапки, якщо є `;`, `"`, CR/LF або пробіл на початку/в кінці; `"` подвоюється. */
export function escapeField(s: string): string {
  return /[;"\r\n]/.test(s) || /^\s|\s$/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

function cellText(c: CsvCell): string {
  switch (c.k) {
    case "text":
      return escapeField(defuse(c.v));
    case "date":
      return c.v;
    case "int":
      return c.v === null ? "" : String(Math.round(c.v));
    case "money":
      return c.v === null ? "" : c.v.toFixed(2).replace(".", ",");
    case "pct":
      return c.v === null ? "" : (Math.round(c.v * 10) / 10).toFixed(1).replace(".", ",");
  }
}

/** Повний файл: BOM + заголовки + рядки, кожен рядок завершується CRLF. */
export function serializeCsv(headers: string[], rows: CsvCell[][]): string {
  const lines = [headers.map((h) => escapeField(h)).join(";"), ...rows.map((r) => r.map(cellText).join(";"))];
  return `${BOM}${lines.join("\r\n")}\r\n`;
}

export type ExportKind = "network" | "locations" | "promotions" | "details";

export const csvFileName = (kind: ExportKind, from: string, to: string) => `beauty-analytics-${kind}-${from}_${to}.csv`;

export interface CsvFile {
  fileName: string;
  content: string;
}

/** Δ% за формулою §19.3 (без `—`: `null` -> порожня комірка). */
const deltaPct = (cur: number, prev: number) => computeDelta(cur, prev)?.pct ?? null;

// ---------- Мережа ----------

const FINANCE_HEADERS = ["Собівартість, ₴", "Виплата майстрам, ₴", "Чистий дохід, ₴", "Маржа, %", "Без собівартості"];

interface FinanceCols {
  cost?: number;
  masterPayout?: number;
  netIncome?: number;
  marginPercent?: number | null;
  withoutCost?: number;
}
const financeCells = (f: FinanceCols): CsvCell[] => [money(f.cost), money(f.masterPayout), money(f.netIncome), pct(f.marginPercent), int(f.withoutCost)];
/** Маржа точки series: у відповіді її немає, клієнт рахує за формулою §24.5. */
const pointMargin = (net: number | undefined, revenue: number) => (net === undefined ? null : marginOf(net, revenue));

export function networkCsv(net: NetworkAnalytics, series: NetworkSeries, compare: boolean): CsvFile {
  const headers = ["Від", "До", "Записів", "Завершено", "Скасовано", "Не прийшли", "Виручка, ₴", ...FINANCE_HEADERS];
  const rows: CsvCell[][] = series.points.map((p) => [
    date(p.from), date(p.to), int(p.appointments), int(p.completed), int(p.cancelled), int(p.noShow), money(p.revenue),
    ...financeCells({ ...p, marginPercent: pointMargin(p.netIncome, p.revenue) }),
  ]);
  const t = net.totals;
  rows.push([text("Разом"), date(net.toDate), int(t.appointments), int(t.completed), int(t.cancelled), int(t.noShow), money(t.revenue), ...financeCells(t)]);
  if (compare && net.previous) {
    const p = net.previous;
    rows.push([text("Попередній період"), date(p.toDate), int(p.appointments), int(p.completed), int(p.cancelled), int(p.noShow), money(p.revenue), ...financeCells(p)]);
    // Гроші - Δ%; маржа - різниця в п.п. (число, не відсоток від відсотка); «Без собівартості» не порівнюється.
    const moneyDelta = (cur: number | undefined, prev: number | undefined) => (cur === undefined || prev === undefined ? pct(null) : pct(deltaPct(cur, prev)));
    const marginDelta = t.marginPercent == null || p.marginPercent == null ? pct(null) : pct(Math.round((t.marginPercent - p.marginPercent) * 10) / 10);
    rows.push([
      text("Зміна, %"),
      text(""),
      pct(deltaPct(t.appointments, p.appointments)),
      pct(deltaPct(t.completed, p.completed)),
      pct(deltaPct(t.cancelled, p.cancelled)),
      pct(deltaPct(t.noShow, p.noShow)),
      pct(deltaPct(t.revenue, p.revenue)),
      moneyDelta(t.cost, p.cost),
      moneyDelta(t.masterPayout, p.masterPayout),
      moneyDelta(t.netIncome, p.netIncome),
      marginDelta,
      int(null),
    ]);
  }
  return { fileName: csvFileName("network", net.fromDate, net.toDate), content: serializeCsv(headers, rows) };
}

// ---------- Деталі періоду (§24.7) ----------

/** Максимум рядків CSV деталей: 10 сторінок × 200. */
export const DETAILS_CSV_MAX_ROWS = 2000;
const DETAILS_HEADERS = [
  "Дата", "Час", "Заклад", "Послуга", "Майстер", "Ціна до знижки, ₴", "Знижка, ₴", "Акція", "Ціна продажу, ₴",
  "Собівартість, ₴", "Виплата майстру, ₴", "Чистий внесок, ₴", "Без собівартості",
];

const itemRow = (i: DetailsItem): CsvCell[] => [
  date(i.localDate), text(i.localTime), text(i.location.name), text(i.service.name), text(i.specialist.name),
  money(i.priceOriginal), money(i.discount), text(i.promotion?.name ?? ""), money(i.priceFinal),
  // costMissing: порожні комірки собівартості й виплати (не 0), «так» у останній.
  i.costMissing ? text("") : money(i.cost), i.costMissing ? text("") : money(i.masterPayout), money(i.net), text(i.costMissing ? "так" : ""),
];

/**
 * CSV деталей: лише назви закладів/послуг/майстрів/акцій - без імен, телефонів, `clientId` і GUID-ів (`appointmentId` не експортується).
 * `truncated` - вивантажено лише перші {DETAILS_CSV_MAX_ROWS} записів: додається рядок «Неповний список: n з m».
 */
export function detailsCsv(summary: AnalyticsDetails["summary"], items: DetailsItem[], range: { fromDate: string; toDate: string }, truncated = false): CsvFile {
  const rows: CsvCell[][] = items.map(itemRow);
  const blank = (n: number): CsvCell[] => Array.from({ length: n }, () => text(""));
  rows.push([
    text("Разом"), text(""), text(""), text(""), text(""),
    money(summary.revenue + summary.discountTotal), money(summary.discountTotal), text(""), money(summary.revenue),
    money(summary.cost), money(summary.masterPayout), money(summary.netIncome), int(summary.withoutCost),
  ]);
  if (truncated) rows.push([text(`Неповний список: ${items.length} з ${summary.completed}`), ...blank(DETAILS_HEADERS.length - 1)]);
  return { fileName: csvFileName("details", range.fromDate, range.toDate), content: serializeCsv(DETAILS_HEADERS, rows) };
}

// ---------- Заклади ----------

export function locationCsv(stat: LocationStat | null, b: LocationBreakdown): CsvFile {
  const headers = ["Спеціаліст", "Записів", "Завершено", "Скасовано", "Не прийшли", "Виручка, ₴", "Зайнято, хв", "Доступно, хв", "Завантаження, %"];
  const loadPct = (l: number | null) => (l === null ? null : l * 100);
  const rows: CsvCell[][] = b.specialists.map((s) => [
    text(s.name), int(s.appointments), int(s.completed), int(s.cancelled), int(s.noShow), money(s.revenue), int(s.busyMinutes), int(s.availableMinutes), pct(loadPct(s.load)),
  ]);
  const sum = (k: "appointments" | "completed" | "cancelled" | "noShow" | "revenue") => b.specialists.reduce((a, s) => a + s[k], 0);
  const m = stat ?? { appointments: sum("appointments"), completed: sum("completed"), cancelled: sum("cancelled"), noShow: sum("noShow"), revenue: sum("revenue") };
  rows.push([text("Разом"), int(m.appointments), int(m.completed), int(m.cancelled), int(m.noShow), money(m.revenue), int(b.busyMinutes), int(b.availableMinutes), pct(loadPct(b.load))]);
  return { fileName: csvFileName("locations", b.fromDate, b.toDate), content: serializeCsv(headers, rows) };
}

// ---------- Акції ----------

export function promotionsCsv(list: PromotionStat[], from: string, to: string, compare: boolean): CsvFile {
  const headers = ["Акція", "Період дії", "Заклади", "Використань", "Виручка, ₴", "Віддано знижками, ₴", ...(compare ? ["Використань (попер.)", "Виручка, ₴ (попер.)", "Зміна виручки, %"] : [])];
  const rows: CsvCell[][] = list.map((r) => [
    text(r.name), text(r.period), text(r.where), int(r.uses), money(r.revenue), money(r.discountTotal),
    ...(compare ? [int(r.previous?.uses), money(r.previous?.revenue), pct(r.previous ? deltaPct(r.revenue, r.previous.revenue) : null)] : []),
  ]);
  return { fileName: csvFileName("promotions", from, to), content: serializeCsv(headers, rows) };
}

/** Blob + `URL.createObjectURL`; після кліку `revokeObjectURL`. */
export function downloadCsv(file: CsvFile): void {
  const url = URL.createObjectURL(new Blob([file.content], { type: "text/csv;charset=utf-8" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = file.fileName;
  a.style.display = "none";
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}
