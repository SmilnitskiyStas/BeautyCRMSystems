import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import type {
  AlertKind,
  Batch,
  BatchStatus,
  MovementType,
  ProductRef,
  StaffAlert,
  OperationKind,
  ProductPurpose,
  ProductRequest,
  StockUnit,
  SupplierRequest,
  WriteOffReason,
} from "./types";

/**
 * Чиста логіка складу (§32.4, §32.7, §32.9): без React і без реального годинника - «сьогодні» завжди передається
 * (`yyyy-MM-dd` у зоні закладу), тож усе тестується з фіксованою датою.
 */

// ---------- підписи ----------

export const UNITS: { id: StockUnit; label: string; short: string }[] = [
  { id: "pcs", label: "Штуки", short: "шт" },
  { id: "pack", label: "Упаковки", short: "уп" },
  { id: "ml", label: "Мілілітри", short: "мл" },
  { id: "l", label: "Літри", short: "л" },
  { id: "g", label: "Грами", short: "г" },
  { id: "kg", label: "Кілограми", short: "кг" },
];
export const unitShort = (u: StockUnit) => UNITS.find((x) => x.id === u)?.short ?? u;

export const PURPOSE_LABEL: Record<ProductPurpose, string> = { sale: "Для продажу", salon_use: "Для роботи закладу", both: "І те, і інше" };
export const REASON_LABEL: Record<WriteOffReason, string> = {
  expired: "Термін придатності минув",
  damaged: "Пошкоджено",
  lost: "Втрачено",
  internal_use: "Внутрішнє використання",
  correction: "Виправлення помилки",
  other: "Інше",
};
export const REASONS_NEEDING_NOTE: readonly WriteOffReason[] = ["correction", "other"];
export const MOVEMENT_LABEL: Record<MovementType, string> = {
  receipt: "Надходження",
  write_off: "Списання",
  transfer_out: "Переміщення (відправлено)",
  transfer_in: "Переміщення (отримано)",
  stocktake_adjust: "Коригування інвентаризації",
  service_consumption: "Витрата на послугу",
  sale: "Продаж",
  sale_return: "Повернення продажу",
};
export const KIND_LABEL: Record<OperationKind, string> = {
  receipt: "Надходження",
  write_off: "Списання",
  transfer: "Переміщення",
  stocktake: "Інвентаризація",
  service_consumption: "Витрата на послугу",
  sale: "Продаж",
  sale_return: "Повернення продажу",
};

export const BATCH_STATUS_VIEW: Record<BatchStatus, { label: string; tone: "ok" | "wait" | "promo" | "neutral" }> = {
  active: { label: "Діє", tone: "ok" },
  expiring: { label: "Скоро спливає", tone: "wait" },
  expired: { label: "Прострочено", tone: "promo" },
  empty: { label: "Вичерпано", tone: "neutral" },
};

// ---------- кількості й гроші ----------

export const isIntegerUnit = (u: StockUnit) => u === "pcs" || u === "pack";

/** Кількість у тисячних (без похибки плаваючої коми). */
export const toMilli = (n: number) => Math.round(n * 1000);
export const fromMilli = (m: number) => m / 1000;

/** Десяткова кома, до 3 знаків, без зайвих нулів; тисячі - вузьким пробілом. */
export function formatNumber(n: number, maxDecimals = 3): string {
  const rounded = Number(n.toFixed(maxDecimals));
  const [int, frac] = Math.abs(rounded).toString().split(".");
  const grouped = int!.replace(/\B(?=(\d{3})+(?!\d))/g, " ");
  return `${rounded < 0 ? "−" : ""}${grouped}${frac ? `,${frac}` : ""}`;
}
export const formatQuantity = (n: number, unit: StockUnit) => `${formatNumber(n)} ${unitShort(unit)}`;
/** Рух зі знаком: «+5 шт» / «−2 шт». */
export const formatSigned = (n: number, unit: StockUnit) => `${n > 0 ? "+" : ""}${formatQuantity(n, unit)}`;
export const formatMoney = (n: number) => `${formatNumber(n, 2)} ₴`;

const DECIMAL = /^\d+(?:[.,]\d*)?$|^[.,]\d+$/;
const decimalsOf = (text: string) => (text.split(/[.,]/)[1] ?? "").length;
const MAX_AMOUNT = 1_000_000;

export type ParseResult = { value: number; error?: undefined } | { value?: undefined; error: string };

/** Кількість: > 0 (або ≥ 0 для підрахунку), ≤ 1 000 000, ≤ 3 знаків, ціле для шт/уп (§32.7 `invalid_quantity`). */
export function parseQuantity(raw: string, unit: StockUnit, opts: { allowZero?: boolean } = {}): ParseResult {
  const text = raw.trim();
  if (text === "") return { error: "Вкажіть кількість." };
  if (!DECIMAL.test(text)) return { error: "Кількість має бути числом." };
  const value = Number(text.replace(",", "."));
  if (isIntegerUnit(unit) && (!Number.isInteger(value) || /[.,]/.test(text))) return { error: `Для одиниці «${unitShort(unit)}» кількість має бути цілою.` };
  if (decimalsOf(text) > 3) return { error: "Не більше 3 знаків після коми." };
  if (value > MAX_AMOUNT) return { error: "Не більше 1 000 000." };
  if (value < 0 || (!opts.allowZero && value === 0)) return { error: "Кількість має бути більшою за нуль." };
  return { value };
}

/** Необов'язкова кількість (мін. залишок): порожнє = `null`. */
export function parseOptionalMinStock(raw: string, unit: StockUnit): { value: number | null; error?: string } {
  if (raw.trim() === "") return { value: null };
  const r = parseQuantity(raw, unit, { allowZero: true });
  return r.error === undefined ? { value: r.value } : { value: null, error: r.error.replace("Вкажіть кількість.", "Вкажіть число.") };
}

/** Ціна: 0..1 000 000, ≤ 2 знаків (§32.7 `invalid_price`). Порожнє = `null`. */
export function parsePrice(raw: string): { value: number | null; error?: string } {
  const text = raw.trim();
  if (text === "") return { value: null };
  if (!DECIMAL.test(text)) return { value: null, error: "Ціна має бути числом." };
  if (decimalsOf(text) > 2) return { value: null, error: "Не більше 2 знаків після коми." };
  const value = Number(text.replace(",", "."));
  if (value > MAX_AMOUNT) return { value: null, error: "Ціна має бути від 0 до 1 000 000." };
  return { value };
}

/** Роздрібна ціна: обов'язкова (для `sale`/`both`), 0..1 000 000, ≤ 2 знаків. */
export function parseSalePrice(raw: string): { value: number | null; error?: string } {
  if (raw.trim() === "") return { value: null, error: "Вкажіть роздрібну ціну." };
  return parsePrice(raw);
}

/** Число в текст поля (десяткова кома). */
export const numberToField = (n: number | null | undefined) => (n === null || n === undefined ? "" : String(n).replace(".", ","));

// ---------- дати й терміни ----------

const YMD = /^(\d{4})-(\d{2})-(\d{2})$/;
export function isYmd(v: string): boolean {
  const m = YMD.exec(v);
  if (!m) return false;
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])];
  if (y < 2000 || y > 2100) return false;
  const dt = new Date(Date.UTC(y, mo - 1, d));
  return dt.getUTCFullYear() === y && dt.getUTCMonth() === mo - 1 && dt.getUTCDate() === d;
}
const dayNumber = (ymd: string) => {
  const m = YMD.exec(ymd)!;
  return Math.round(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])) / 86_400_000);
};
/** Різниця діб `to - from` (від'ємна, якщо `to` раніше). */
export const daysBetween = (from: string, to: string) => dayNumber(to) - dayNumber(from);

/** Діб до закінчення від `today` (від'ємне для простроченої); `null` без терміну. */
export const daysToExpiry = (expiresOn: string | null, today: string): number | null =>
  expiresOn && isYmd(expiresOn) ? daysBetween(today, expiresOn) : null;

/** Статус партії (§32.4): прострочена `expires_on < today`; закінчується `<= today + soonDays`; порожня має пріоритет. */
export function batchStatus(b: { quantity: number; expiresOn: string | null }, today: string, soonDays: number): BatchStatus {
  if (b.quantity <= 0) return "empty";
  const d = daysToExpiry(b.expiresOn, today);
  if (d === null) return "active";
  if (d < 0) return "expired";
  return d <= soonDays ? "expiring" : "active";
}

/** Підпис терміну для таблиць: не лише колір (a11y). */
export function expiryLabel(expiresOn: string | null, today: string): string {
  const d = daysToExpiry(expiresOn, today);
  if (d === null) return "Без терміну";
  if (d < 0) return `Прострочено ${-d} ${dayWord(-d)} тому`;
  if (d === 0) return "Спливає сьогодні";
  return `Залишилось ${d} ${dayWord(d)}`;
}
export function dayWord(n: number): string {
  const m100 = n % 100;
  const m10 = n % 10;
  if (m100 >= 11 && m100 <= 14) return "діб";
  if (m10 === 1) return "доба";
  if (m10 >= 2 && m10 <= 4) return "доби";
  return "діб";
}

/** `2026-10-14` -> `14.10.2026`. */
export const formatYmd = (ymd: string | null | undefined) => {
  const m = ymd ? YMD.exec(ymd) : null;
  return m ? `${m[3]}.${m[2]}.${m[1]}` : "—";
};

export const isLow = (quantity: number, effectiveMin: number | null) => effectiveMin !== null && effectiveMin > 0 && quantity < effectiveMin;

// ---------- FEFO (§32.4) ----------

/** FEFO: `expires_on ASC NULLS LAST, received_at ASC, id ASC`; партії з `quantity > 0`. */
export function fefoOrder<T extends Pick<Batch, "id" | "expiresOn" | "receivedAt" | "quantity">>(batches: readonly T[]): T[] {
  return batches
    .filter((b) => b.quantity > 0)
    .sort((a, b) => {
      if (a.expiresOn !== b.expiresOn) {
        if (a.expiresOn === null) return 1;
        if (b.expiresOn === null) return -1;
        return a.expiresOn < b.expiresOn ? -1 : 1;
      }
      if (a.receivedAt !== b.receivedAt) return a.receivedAt < b.receivedAt ? -1 : 1;
      return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
    });
}

export interface FefoTake {
  batchId: string;
  quantity: number;
}
export interface FefoPlan {
  takes: FefoTake[];
  /** Скільки не вдалося покрити (у тих самих одиницях); 0 = достатньо. */
  shortage: number;
  /** Скільки було доступно за правилом (без прострочених, якщо `skipExpired`). */
  available: number;
}

/** План списання `quantity` за FEFO; `skipExpired` - для переміщення (§32.4: прострочені не беруться). Рахує в тисячних. */
export function planFefo(
  batches: readonly Pick<Batch, "id" | "expiresOn" | "receivedAt" | "quantity">[],
  quantity: number,
  today: string,
  opts: { skipExpired?: boolean } = {},
): FefoPlan {
  const pool = fefoOrder(batches).filter((b) => !(opts.skipExpired && b.expiresOn !== null && b.expiresOn < today));
  const available = pool.reduce((s, b) => s + toMilli(b.quantity), 0);
  let left = toMilli(quantity);
  const takes: FefoTake[] = [];
  for (const b of pool) {
    if (left <= 0) break;
    const take = Math.min(left, toMilli(b.quantity));
    takes.push({ batchId: b.id, quantity: fromMilli(take) });
    left -= take;
  }
  return { takes, shortage: fromMilli(Math.max(0, left)), available: fromMilli(available) };
}

// ---------- валідація форм ----------

export type FieldErrors<K extends string = string> = Partial<Record<K, string>>;

const BARCODE = /^[0-9A-Za-z._-]{1,64}$/;
const PHONE = /^[0-9+()\-. ]+$/;

export interface ProductFormValues {
  name: string;
  /** Вузол дерева категорій; `""` - без категорії. */
  categoryId: string;
  unit: StockUnit;
  barcode: string;
  purpose: ProductPurpose;
  supplierId: string;
  purchasePrice: string;
  salePrice: string;
  minStock: string;
  trackExpiry: boolean;
  isActive: boolean;
  note: string;
}
export type ProductErrors = FieldErrors<"name" | "barcode" | "purchasePrice" | "salePrice" | "minStock" | "note">;

export function validateProduct(v: ProductFormValues, opts: { canEditCosts: boolean }): ProductErrors {
  const e: ProductErrors = {};
  const name = v.name.trim();
  if (name.length < 1 || name.length > 200) e.name = "Назва: від 1 до 200 символів.";
  const barcode = v.barcode.trim();
  if (barcode && !BARCODE.test(barcode)) e.barcode = "Штрихкод: до 64 символів, літери, цифри та . _ -";
  if (opts.canEditCosts) {
    const p = parsePrice(v.purchasePrice);
    if (p.error) e.purchasePrice = p.error;
  }
  if (v.purpose !== "salon_use") {
    const s = parseSalePrice(v.salePrice);
    if (s.error) e.salePrice = s.error;
  }
  const m = parseOptionalMinStock(v.minStock, v.unit);
  if (m.error) e.minStock = m.error;
  if (v.note.trim().length > 500) e.note = "Примітка: до 500 символів.";
  return e;
}

/** Тіло запиту: ціна передається лише якщо є право `edit` (інакше сервер відповість 403). */
export function toProductRequest(v: ProductFormValues, opts: { canEditCosts: boolean; isEdit: boolean }): ProductRequest {
  const price = opts.canEditCosts ? parsePrice(v.purchasePrice).value : null;
  return {
    name: v.name.trim(),
    // Старе текстове `category` з форми не надсилається - категорія обирається в дереві.
    categoryId: v.categoryId || null,
    unit: v.unit,
    barcode: v.barcode.trim() || null,
    purpose: v.purpose,
    supplierId: v.supplierId || null,
    // PUT: відсутнє або null = без змін (очищення ціни не підтримується).
    ...(opts.canEditCosts && price !== null ? { purchasePrice: price } : {}),
    // salon_use: роздрібної ціни немає - завжди null.
    salePrice: v.purpose === "salon_use" ? null : parsePrice(v.salePrice).value,
    minStock: parseOptionalMinStock(v.minStock, v.unit).value,
    trackExpiry: v.trackExpiry,
    ...(opts.isEdit ? { isActive: v.isActive } : {}),
    note: v.note.trim() || null,
  };
}

export type SupplierErrors = FieldErrors<"name" | "contactName" | "phone" | "email" | "note">;
export function validateSupplier(v: { name: string; contactName: string; phone: string; email: string; note: string }): SupplierErrors {
  const e: SupplierErrors = {};
  if (v.name.trim().length < 1 || v.name.trim().length > 200) e.name = "Назва: від 1 до 200 символів.";
  if (v.contactName.trim().length > 200) e.contactName = "Контактна особа: до 200 символів.";
  const phone = v.phone.trim();
  if (phone && (phone.length > 32 || !PHONE.test(phone))) e.phone = "Телефон: до 32 символів, цифри та + ( ) - . пробіл.";
  const email = v.email.trim();
  if (email && (email.length > 320 || !email.includes("@"))) e.email = "Вкажіть коректну пошту.";
  if (v.note.trim().length > 500) e.note = "Примітка: до 500 символів.";
  return e;
}
export function toSupplierRequest(v: { name: string; contactName: string; phone: string; email: string; note: string; isActive: boolean }, isEdit: boolean): SupplierRequest {
  return {
    name: v.name.trim(),
    contactName: v.contactName.trim() || null,
    phone: v.phone.trim() || null,
    email: v.email.trim() || null,
    note: v.note.trim() || null,
    ...(isEdit ? { isActive: v.isActive } : {}),
  };
}

// ---------- рядки операцій ----------

export interface LineDraft {
  /** Стабільний ключ рядка для React. */
  key: string;
  productId: string;
  quantity: string;
  batchId: string;
  unitCost: string;
  expiresOn: string;
  lotNumber: string;
}
export const emptyLine = (key: string): LineDraft => ({ key, productId: "", quantity: "", batchId: "", unitCost: "", expiresOn: "", lotNumber: "" });

export type LineErrors = FieldErrors<"productId" | "quantity" | "unitCost" | "expiresOn" | "lotNumber" | "batchId">;
export interface LineContext {
  unitOf: (productId: string) => StockUnit | undefined;
  trackExpiry: (productId: string) => boolean;
}

/** Помилки рядків (за індексом); пусто = усе гаразд. `today` - дата закладу надходження. */
export function validateLines(
  lines: readonly LineDraft[],
  ctx: LineContext,
  opts: { mode: "receipt" | "stock"; today: string; canEditCosts?: boolean },
): Record<number, LineErrors> {
  const out: Record<number, LineErrors> = {};
  const seen = new Set<string>();
  lines.forEach((l, i) => {
    const e: LineErrors = {};
    const unit = ctx.unitOf(l.productId);
    if (!l.productId || !unit) e.productId = "Оберіть товар.";
    else {
      const q = parseQuantity(l.quantity, unit);
      if (q.error) e.quantity = q.error;
      if (opts.mode === "stock") {
        const key = `${l.productId}|${l.batchId}`;
        if (seen.has(key)) e.productId = "Цей товар (партія) уже додано.";
        seen.add(key);
      } else {
        if (opts.canEditCosts) {
          const p = parsePrice(l.unitCost);
          if (p.error) e.unitCost = p.error;
        }
        const exp = l.expiresOn.trim();
        if (exp) {
          if (!isYmd(exp)) e.expiresOn = "Вкажіть коректну дату.";
          else if (exp < opts.today) e.expiresOn = "Термін придатності вже минув.";
        } else if (ctx.trackExpiry(l.productId)) e.expiresOn = "Вкажіть термін придатності.";
        if (l.lotNumber.trim().length > 64) e.lotNumber = "Номер партії: до 64 символів.";
      }
    }
    if (Object.keys(e).length > 0) out[i] = e;
  });
  return out;
}

export function validateReceiptHeader(v: { receivedOn: string; documentNumber: string; note: string }, today: string): FieldErrors<"receivedOn" | "documentNumber" | "note"> {
  const e: FieldErrors<"receivedOn" | "documentNumber" | "note"> = {};
  if (v.receivedOn) {
    if (!isYmd(v.receivedOn)) e.receivedOn = "Вкажіть коректну дату.";
    else if (v.receivedOn > today || daysBetween(v.receivedOn, today) > 365) e.receivedOn = "Дата надходження: не в майбутньому й не старіша за рік.";
  }
  if (v.documentNumber.trim().length > 64) e.documentNumber = "Номер документа: до 64 символів.";
  if (v.note.trim().length > 500) e.note = "Коментар: до 500 символів.";
  return e;
}

export function validateWriteOffHeader(v: { reason: string; note: string }): FieldErrors<"reason" | "note"> {
  const e: FieldErrors<"reason" | "note"> = {};
  if (!v.reason) e.reason = "Оберіть причину.";
  else if ((REASONS_NEEDING_NOTE as readonly string[]).includes(v.reason) && !v.note.trim()) e.note = "Додайте коментар до списання.";
  if (v.note.trim().length > 500) e.note = "Коментар: до 500 символів.";
  return e;
}

// ---------- інвентаризація ----------

/** Різниця «підраховано - очікувано» (у тисячних, без похибки); `null`, якщо не рахували. */
export function stocktakeDifference(expected: number | null, counted: number | null, expectedShortage: number | null = null): number | null {
  if (counted === null) return null;
  // §32.17: різниця - до сальдо (`expected − expectedShortage`).
  return fromMilli(toMilli(counted) - (toMilli(expected ?? 0) - toMilli(expectedShortage ?? 0)));
}

// ---------- сумарні значення ----------

/** Чи відома вартість рядка («неповна», якщо частина партій без ціни). */
export const valueLabel = (value: number | undefined, incomplete: boolean | undefined) =>
  value === undefined ? null : `${formatMoney(value)}${incomplete ? " (неповна)" : ""}`;

// ---------- ідемпотентність ----------

/** `Idempotency-Key` на відкриття форми; повторне надсилання того самого запиту використовує той самий ключ. */
export const newIdempotencyKey = (): string =>
  typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
    ? crypto.randomUUID()
    : `k-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;

/** Спроба надсилання: ключ лишається, поки тіло запиту те саме (повтор після збою), і змінюється з іншим тілом (інакше `idempotency_key_reused`). */
export interface Attempt {
  hash: string;
  key: string;
}
export function nextAttempt(prev: Attempt | null, request: unknown, make: () => string = newIdempotencyKey): Attempt {
  const hash = JSON.stringify(request);
  return prev && prev.hash === hash ? prev : { hash, key: make() };
}

// ---------- помилки (§32.8) ----------

/**
 * Тексти помилок складу. Окрема мапа, а не `BY_CODE`: коди `invalid_note|invalid_reason|invalid_date|invalid_range|
 * invalid_cursor|invalid_name` вже мають в `errors.ts` інший зміст (закриття закладу, нотатка дзвінка, розсилки).
 */
const INVENTORY_BY_CODE: Record<string, string> = {
  module_disabled: "Модуль «Склад» вимкнено для вашого бізнесу.",
  forbidden_role: "Недостатньо прав для цієї дії.",
  product_not_found: "Товар не знайдено.",
  supplier_not_found: "Постачальника не знайдено.",
  location_not_found: "Заклад не знайдено.",
  batch_not_found: "Партію не знайдено.",
  operation_not_found: "Операцію не знайдено.",
  stocktake_not_found: "Інвентаризацію не знайдено.",
  setting_not_found: "Налаштування закладу для товару немає.",
  barcode_taken: "Товар з таким штрихкодом уже є.",
  supplier_name_taken: "Постачальник з такою назвою вже є.",
  stocktake_in_progress: "У закладі вже триває інвентаризація.",
  stocktake_closed: "Інвентаризацію вже проведено або скасовано.",
  stocktake_version_conflict: "Інвентаризацію змінив інший користувач. Оновіть сторінку.",
  insufficient_stock: "Недостатньо товару на залишку.",
  product_inactive: "Товар неактивний.",
  product_disabled_for_location: "Товар вимкнено для цього закладу.",
  location_inactive: "Заклад неактивний.",
  supplier_inactive: "Постачальник неактивний.",
  same_location: "Оберіть інший заклад призначення.",
  batch_expired: "Партія прострочена, її не можна переміщувати.",
  unit_locked: "Одиницю виміру не можна змінити: товар має рух або входить у норми матеріалів.",
  // Етап 2a (§32.21)
  service_not_found: "Послугу не знайдено.",
  materials_not_found: "Для цього закладу норм немає.",
  alert_not_found: "Сповіщення не знайдено.",
  product_not_for_salon_use: "Товар призначено лише для продажу — його не можна додати в норму.",
  purpose_locked: "Товар використовується в нормах матеріалів — спершу приберіть його з норм.",
  invalid_alert_status: "Технічна помилка запиту. Спробуйте ще раз.",
  invalid_alert_kind: "Технічна помилка запиту. Спробуйте ще раз.",
  expiry_required: "Вкажіть термін придатності.",
  invalid_expiry: "Термін придатності вже минув.",
  note_required: "Додайте коментар до списання.",
  stocktake_empty: "Внесіть хоча б один підрахунок.",
  idempotency_key_required: "Технічна помилка запиту. Спробуйте ще раз.",
  invalid_idempotency_key: "Технічна помилка запиту. Спробуйте ще раз.",
  idempotency_key_reused: "Цю операцію вже надіслано з іншими даними. Оновіть сторінку.",
  rate_limited: "Забагато дій. Зачекайте хвилину.",
  invalid_name: "Назва: від 1 до 200 символів.",
  invalid_category: "Категорія: до 100 символів.",
  invalid_unit: "Оберіть одиницю виміру зі списку.",
  invalid_barcode: "Штрихкод: до 64 символів, літери, цифри та . _ -",
  invalid_purpose: "Оберіть призначення зі списку.",
  sale_price_required: "Вкажіть роздрібну ціну для товару, призначеного на продаж.",
  invalid_price: "Ціна: від 0 до 1 000 000, до 2 знаків після коми.",
  invalid_min_stock: "Мінімальний залишок: від 0 до 1 000 000, до 3 знаків; для штук і упаковок - ціле.",
  invalid_quantity: "Кількість: більше нуля, до 1 000 000, до 3 знаків; для штук і упаковок - ціле.",
  invalid_lines: "Додайте від 1 до 100 рядків.",
  too_many_lines: "Забагато рядків.",
  duplicate_line: "Цей товар (партія) повторюється.",
  invalid_date: "Вкажіть коректну дату.",
  invalid_reason: "Оберіть причину зі списку.",
  invalid_note: "Коментар: до 500 символів.",
  invalid_document_number: "Номер документа: до 64 символів.",
  invalid_lot_number: "Номер партії: до 64 символів.",
  invalid_contact_name: "Контактна особа: до 200 символів.",
  invalid_phone: "Телефон: до 32 символів, цифри та + ( ) - . пробіл.",
  invalid_email: "Вкажіть коректну пошту.",
  location_required: "Оберіть заклад.",
  invalid_range: "Некоректний діапазон дат (до 366 днів).",
  invalid_cursor: "Список змінився. Оновіть сторінку.",
  expected_version_required: "Технічна помилка запиту. Оновіть сторінку.",
};

export function humanizeInventoryError(e: unknown, fallback = "Не вдалося виконати дію. Спробуйте ще раз."): string {
  if (e instanceof BeautyApiError) {
    const base = INVENTORY_BY_CODE[e.code];
    if (base) return withLineHint(e, base);
  }
  return humanizeError(e, fallback);
}

/** «Рядок N: …», якщо сервер вказав індекс рядка (`extras.line`, 0-based). */
function withLineHint(e: BeautyApiError, text: string): string {
  const raw = e.body?.line;
  if (typeof raw === "number" && Number.isInteger(raw) && raw >= 0) return `Рядок ${raw + 1}: ${text}`;
  return text;
}

export interface ShortageLine {
  line: number;
  productId: string;
  batchId?: string;
  requested: number;
  available: number;
}
/** Розбір `extras.lines` з `422 insufficient_stock`. */
export function shortageLines(e: unknown): ShortageLine[] {
  if (!(e instanceof BeautyApiError) || e.code !== "insufficient_stock") return [];
  const raw = e.body?.lines;
  if (!Array.isArray(raw)) return [];
  return raw.flatMap((x): ShortageLine[] => {
    const o = (x && typeof x === "object" ? x : {}) as Record<string, unknown>;
    if (typeof o.line !== "number" || typeof o.productId !== "string") return [];
    return [
      {
        line: o.line,
        productId: o.productId,
        ...(typeof o.batchId === "string" ? { batchId: o.batchId } : {}),
        requested: typeof o.requested === "number" ? o.requested : 0,
        available: typeof o.available === "number" ? o.available : 0,
      },
    ];
  });
}

// ---------- етап 2a: нестача й сальдо (§32.17) ----------

/** Облікове сальдо `quantity − shortage` (у тисячних, без похибки); може бути від'ємним. */
export const balanceOf = (quantity: number, shortage: number) => fromMilli(toMilli(quantity) - toMilli(shortage));
/** `isLow` за сальдо (§32.17): `effectiveMinStock > 0 AND balance < effectiveMinStock`. */
export const isLowBalance = (balance: number, effectiveMin: number | null) => isLow(balance, effectiveMin);

/** Залишок із сальдо: мінус завжди позначений текстом, не лише знаком чи кольором. */
export function balanceLabel(balance: number, unit: StockUnit): string {
  return balance < 0 ? `${formatQuantity(balance, unit)} (у мінусі)` : formatQuantity(balance, unit);
}

/** Підпис руху з урахуванням `isShortage` (§32.17). */
export function movementLabel(type: MovementType, isShortage: boolean): string {
  if (!isShortage) return MOVEMENT_LABEL[type];
  if (type === "service_consumption") return "Нестача (витрата без залишку)";
  if (type === "receipt" || type === "transfer_in" || type === "stocktake_adjust") return "Погашення нестачі";
  return MOVEMENT_LABEL[type];
}

// ---------- етап 2a: сповіщення (§32.19) ----------

export const ALERT_KIND_LABEL: Record<AlertKind, string> = { low_stock: "Низький залишок", expiring: "Спливає термін", shortage: "Нестача" };
export const ALERT_STATUS_LABEL = { open: "Нове", read: "Прочитане", resolved: "Розв’язане" } as const;

/** Число для бейджа: контракт обмежує лічильник 1000, UI показує «99+» вище 99. */
export const alertBadgeText = (count: number) => (count > 99 ? "99+" : String(count));

/** Поріг сповіщення про термін (§32.20 п.4): `0` - прострочено; інакше найменший поріг ≥ днів до терміну; `null` - ще рано. */
export function daysBucketFor(days: number, thresholds: readonly number[]): number | null {
  if (days < 0) return 0;
  const fit = thresholds.filter((t) => t >= days).sort((a, b) => a - b);
  return fit[0] ?? null;
}

/** Короткий опис порогу терміну: `0` = прострочено. */
export function daysBucketLabel(bucket: number | null): string {
  if (bucket === null) return "";
  if (bucket === 0) return "прострочено";
  return `до ${bucket} діб`;
}

/** Опис сповіщення одним рядком (без ПД; лише товар, кількості, терміни). */
export function alertSummary(a: StaffAlert): string {
  const u = a.product.unit;
  switch (a.kind) {
    case "low_stock":
      return a.threshold !== null ? `Сальдо ${balanceLabel(a.quantity, u)}, мінімум ${formatQuantity(a.threshold, u)}` : `Сальдо ${balanceLabel(a.quantity, u)}`;
    case "expiring": {
      const when = a.daysBucket === 0 ? "Термін минув" : `Термін спливає (${daysBucketLabel(a.daysBucket)})`;
      return `${when}: ${formatYmd(a.expiresOn)}, у партії ${formatQuantity(a.quantity, u)}${a.batch?.lotNumber ? `, партія ${a.batch.lotNumber}` : ""}`;
    }
    case "shortage":
      return `Не вистачило ${formatQuantity(a.quantity, u)} для витрати на послугу`;
  }
}

// ---------- етап 2a: норми матеріалів (§32.16) ----------

export const MAX_MATERIAL_LINES = 50;
/** Товар може бути в нормі: активний і `purpose ∈ salon_use|both`. */
export const canBeNorm = (p: Pick<ProductRef, "purpose" | "isActive">) => p.isActive && p.purpose !== "sale";

export interface MaterialDraftLine {
  productId: string;
  quantity: string;
}
export interface MaterialDraftErrors {
  /** Помилка рядка за індексом. */
  lines: Record<number, string>;
  form?: string;
}
export interface MaterialDraftResult {
  lines?: { productId: string; quantity: number }[];
  errors: MaterialDraftErrors;
}

/**
 * Валідація чернетки норми (дзеркало §32.16): ≤ 50 рядків, товар обраний і не повторюється, кількість у одиниці товару.
 * Порожній набір дозволено лише для override закладу (`allowEmpty`): «у цьому закладі матеріали не списуються».
 */
export function validateMaterialDraft(draft: MaterialDraftLine[], products: Map<string, ProductRef>, allowEmpty: boolean): MaterialDraftResult {
  const errors: MaterialDraftErrors = { lines: {} };
  if (draft.length === 0 && !allowEmpty) return { errors: { lines: {}, form: "Додайте хоча б один матеріал." } };
  if (draft.length > MAX_MATERIAL_LINES) return { errors: { lines: {}, form: `Не більше ${MAX_MATERIAL_LINES} матеріалів у наборі.` } };
  const seen = new Set<string>();
  const out: { productId: string; quantity: number }[] = [];
  draft.forEach((l, i) => {
    const p = products.get(l.productId);
    if (!l.productId || !p) {
      errors.lines[i] = "Оберіть товар.";
      return;
    }
    if (seen.has(l.productId)) {
      errors.lines[i] = "Цей товар уже є в наборі.";
      return;
    }
    seen.add(l.productId);
    if (p.purpose === "sale") {
      errors.lines[i] = "Товар призначено лише для продажу.";
      return;
    }
    const q = parseQuantity(l.quantity, p.unit);
    if (q.error !== undefined) {
      errors.lines[i] = q.error;
      return;
    }
    if (q.value > 100_000) {
      errors.lines[i] = "Не більше 100 000.";
      return;
    }
    out.push({ productId: l.productId, quantity: q.value });
  });
  if (Object.keys(errors.lines).length > 0) return { errors };
  return { lines: out, errors };
}
