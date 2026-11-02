import { money } from "../format";

/**
 * Порівняння з попереднім періодом (§19.3). Сервер `delta` не віддає; клієнт рахує за однією формулою:
 * `abs = cur − prev`; `pct = prev == 0 ? null : round((cur − prev) / prev × 100, 1)`.
 * Для часток (`cancellationRate`, `load`) показується лише `abs` у п.п. (×100).
 */

export interface DeltaValue {
  abs: number;
  /** `null` - ділення на 0. */
  pct: number | null;
}

const round1 = (n: number) => Math.round(n * 10) / 10;

export function computeDelta(cur: number | null | undefined, prev: number | null | undefined): DeltaValue | null {
  if (cur == null || prev == null) return null;
  return { abs: cur - prev, pct: prev === 0 ? null : round1(((cur - prev) / prev) * 100) };
}

/** Різниця часток 0..1 у п.п. (×100, 1 знак). */
export function computeShareDelta(cur: number | null | undefined, prev: number | null | undefined): DeltaValue | null {
  if (cur == null || prev == null) return null;
  return { abs: round1((cur - prev) * 100), pct: null };
}

export type DeltaKind = "count" | "money" | "share";
/** `up-good`: зростання - добре (виручка); `down-good`: зростання - погано (скасування, no-show). */
/** `neutral`: напрямок не оцінюється (напр. віддані знижки). */
export type Polarity = "up-good" | "down-good" | "neutral";

export interface DeltaView {
  arrow: "▲" | "▼" | "▬" | "";
  /** Видимий текст: стрілка не єдина ознака напрямку - є знак числа. */
  text: string;
  /** Текст для зчитувачів з екрана. */
  sr: string;
  tone: "good" | "bad" | "neutral";
}

const MINUS = "−";
const num = (n: number, digits = 0) => new Intl.NumberFormat("uk-UA", { maximumFractionDigits: digits, minimumFractionDigits: 0 }).format(Math.abs(n));
const sign = (n: number) => (n > 0 ? "+" : n < 0 ? MINUS : "");

export const EMPTY_DELTA: DeltaView = { arrow: "", text: "—", sr: "порівняння недоступне", tone: "neutral" };

export function describeDelta(d: DeltaValue | null, kind: DeltaKind, polarity: Polarity = "up-good"): DeltaView {
  if (!d) return EMPTY_DELTA;
  const dir = d.abs > 0 ? 1 : d.abs < 0 ? -1 : 0;
  const arrow = dir > 0 ? "▲" : dir < 0 ? "▼" : "▬";
  const tone = dir === 0 || polarity === "neutral" ? "neutral" : (dir > 0) === (polarity === "up-good") ? "good" : "bad";
  const word = dir > 0 ? "Більше на" : dir < 0 ? "Менше на" : "Без змін";

  if (kind === "share") {
    const v = `${sign(d.abs)}${num(d.abs, 1)} п.п.`;
    return { arrow, text: `${arrow} ${v}`, sr: dir === 0 ? word : `${word} ${num(d.abs, 1)} п.п.`, tone };
  }
  const absText = kind === "money" ? `${sign(d.abs)}${money(Math.abs(d.abs))}` : `${sign(d.abs)}${num(d.abs)}`;
  const pctText = d.pct === null ? "—" : `${sign(d.pct)}${num(d.pct, 1)}%`;
  return {
    arrow,
    text: `${arrow} ${pctText} (${absText})`,
    sr: dir === 0 ? word : `${word} ${d.pct === null ? absText.replace(/^[+−]/, "") : `${num(d.pct, 1)}%`}${d.pct === null ? "" : ` (${absText})`}`,
    tone,
  };
}
