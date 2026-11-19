/**
 * Фінансові правила §24.5 (чисті функції): виплата майстру, чистий внесок, маржа. Гроші рахуються в копійках (цілі числа),
 * щоб суми не накопичували похибку; округлення - half away from zero, як `round` у PostgreSQL.
 */

export const toCents = (v: number): number => Math.round(v * 100);
export const fromCents = (c: number): number => c / 100;

/** Сума грошових значень без накопичення похибки. */
export const sumMoney = (values: number[]): number => fromCents(values.reduce((a, v) => a + toCents(v), 0));

/** Округлення half away from zero до `digits` знаків. */
export function roundHalfAway(v: number, digits: number): number {
  const f = 10 ** digits;
  const r = Math.round(Math.abs(v) * f + 1e-9) / f;
  return v < 0 ? -r : r;
}

/**
 * Виплата майстру: `round(price_final × master_percent / 100, 2)`. Відсоток має до 2 знаків, тому рахуємо цілими:
 * копійки ціни × соті частки відсотка / 10 000. `null`/відсутній відсоток = 0.
 */
export function payoutOf(priceFinal: number, masterPercent: number | null | undefined): number {
  if (!masterPercent) return 0;
  const cents = toCents(priceFinal);
  const hundredths = Math.round(masterPercent * 100);
  return Math.floor((cents * hundredths + 5000) / 10_000) / 100;
}

/** `net = price_final − coalesce(cost, 0) − coalesce(payout, 0)`; може бути від'ємним. */
export const netOf = (priceFinal: number, cost: number | null | undefined, payout: number | null | undefined): number =>
  fromCents(toCents(priceFinal) - toCents(cost ?? 0) - toCents(payout ?? 0));

/** `revenue = 0 ? null : round(net / revenue × 100, 1)`. */
export const marginOf = (netIncome: number, revenue: number): number | null =>
  revenue === 0 ? null : roundHalfAway((netIncome / revenue) * 100, 1);

/** Попередження редактора: собівартість + виплата (від ціни) більше за ціну. */
export function exceedsPrice(price: number, cost: number, masterPercent: number | null | undefined): boolean {
  return toCents(cost) + toCents(payoutOf(price, masterPercent)) > toCents(price);
}
