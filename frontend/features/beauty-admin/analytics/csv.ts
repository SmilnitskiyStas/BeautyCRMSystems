import { computeDelta } from "./delta";
import type { LocationBreakdown, LocationStat, NetworkAnalytics, NetworkSeries, PromotionStat } from "../types";

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

export type ExportKind = "network" | "locations" | "promotions";

export const csvFileName = (kind: ExportKind, from: string, to: string) => `beauty-analytics-${kind}-${from}_${to}.csv`;

export interface CsvFile {
  fileName: string;
  content: string;
}

/** Δ% за формулою §19.3 (без `—`: `null` -> порожня комірка). */
const deltaPct = (cur: number, prev: number) => computeDelta(cur, prev)?.pct ?? null;

// ---------- Мережа ----------

export function networkCsv(net: NetworkAnalytics, series: NetworkSeries, compare: boolean): CsvFile {
  const headers = ["Від", "До", "Записів", "Завершено", "Скасовано", "Не прийшли", "Виручка, ₴"];
  const rows: CsvCell[][] = series.points.map((p) => [date(p.from), date(p.to), int(p.appointments), int(p.completed), int(p.cancelled), int(p.noShow), money(p.revenue)]);
  const t = net.totals;
  rows.push([text("Разом"), date(net.toDate), int(t.appointments), int(t.completed), int(t.cancelled), int(t.noShow), money(t.revenue)]);
  if (compare && net.previous) {
    const p = net.previous;
    rows.push([text("Попередній період"), date(p.toDate), int(p.appointments), int(p.completed), int(p.cancelled), int(p.noShow), money(p.revenue)]);
    rows.push([
      text("Зміна, %"),
      text(""),
      pct(deltaPct(t.appointments, p.appointments)),
      pct(deltaPct(t.completed, p.completed)),
      pct(deltaPct(t.cancelled, p.cancelled)),
      pct(deltaPct(t.noShow, p.noShow)),
      pct(deltaPct(t.revenue, p.revenue)),
    ]);
  }
  return { fileName: csvFileName("network", net.fromDate, net.toDate), content: serializeCsv(headers, rows) };
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
