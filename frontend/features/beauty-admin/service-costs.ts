import { exceedsPrice, payoutOf } from "./analytics/finance";
import { moneyExact, percentValue } from "./format";
import type { ServiceCost, ServiceCostInput } from "./types";

/** Валідація полів редактора собівартості (§24.6.1, §24.8.2): ті самі межі, що й у backend (`invalid_cost`, `invalid_master_percent`). */

export const MAX_COST = 1_000_000;

export interface CostFormValues {
  cost: string;
  percent: string;
}
export interface CostFormErrors {
  cost?: string;
  percent?: string;
}

const NUMBER = /^-?\d+(?:[.,]\d+)?$/;

/** Рядок із крапкою чи комою -> число; порожній -> `null`; сміття -> `"invalid"`. */
export function parseDecimal(raw: string): number | null | "invalid" {
  const t = raw.trim().replace(/\s/g, "");
  if (t === "") return null;
  if (!NUMBER.test(t)) return "invalid";
  return Number(t.replace(",", "."));
}

/** Не більше 2 знаків після коми, без похибки плаваючої коми. */
const decimals = (raw: string) => (raw.trim().replace(",", ".").split(".")[1] ?? "").length;

export function validateCostForm(v: CostFormValues): { errors: CostFormErrors; value?: Pick<ServiceCostInput, "costAmount" | "masterPercent"> } {
  const errors: CostFormErrors = {};
  const cost = parseDecimal(v.cost);
  if (cost === null) errors.cost = "Вкажіть собівартість (0, якщо її немає).";
  else if (cost === "invalid") errors.cost = "Введіть число, наприклад 120 або 120,50.";
  else if (cost < 0) errors.cost = "Собівартість не може бути від’ємною.";
  else if (cost > MAX_COST) errors.cost = "Собівартість не може перевищувати 1 000 000 ₴.";
  else if (decimals(v.cost) > 2) errors.cost = "Не більше 2 знаків після коми.";

  const percent = parseDecimal(v.percent);
  if (percent === "invalid") errors.percent = "Введіть число від 0 до 100, наприклад 40 або 33,33.";
  else if (percent !== null && percent < 0) errors.percent = "Відсоток не може бути від’ємним.";
  else if (percent !== null && percent > 100) errors.percent = "Відсоток не може перевищувати 100.";
  else if (percent !== null && decimals(v.percent) > 2) errors.percent = "Не більше 2 знаків після коми.";

  if (errors.cost || errors.percent || cost === null || cost === "invalid") return { errors };
  return { errors, value: { costAmount: cost, masterPercent: percent === null || percent === "invalid" ? null : percent } };
}

export const toFormValues = (c?: Pick<ServiceCost, "costAmount" | "masterPercent">): CostFormValues => ({
  cost: c ? String(c.costAmount).replace(".", ",") : "",
  percent: c && c.masterPercent !== null ? String(c.masterPercent).replace(".", ",") : "",
});

/** Попередження, якщо собівартість + виплата (від ціни) більші за ціну; `null` - усе гаразд або значення ще некоректні. */
export function lossWarning(price: number | undefined, v: CostFormValues): string | null {
  const { value } = validateCostForm(v);
  if (price === undefined || !value || !exceedsPrice(price, value.costAmount, value.masterPercent)) return null;
  const total = value.costAmount + payoutOf(price, value.masterPercent);
  return `Собівартість і виплата майстру (${moneyExact(total)}) більші за ціну ${moneyExact(price)}: кожен візит буде збитковим.`;
}

/** Підпис для колонки списку: «220 ₴ · майстру 40%» / «не задано». */
export function costSummary(c: Pick<ServiceCost, "costAmount" | "masterPercent"> | undefined): string {
  if (!c) return "не задано";
  return `${moneyExact(c.costAmount)}${c.masterPercent !== null ? ` · майстру ${percentValue(c.masterPercent)}` : ""}`;
}
