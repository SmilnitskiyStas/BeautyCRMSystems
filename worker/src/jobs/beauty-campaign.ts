import type { BeautyDeps, CampaignEnqueueOptions } from "../ports";
import { DEFAULT_CLIENT_INTERVAL_DAYS, DEFAULT_MARKETING_DAILY_LIMIT, UNSUBSCRIBE_NOTE, UNSUBSCRIBE_NOTE_BUTTONS } from "./beauty-common";
import { formatCode } from "./coupon-code";
import { localParts, sanitizeLine, stripFormatChars, zonedTimeToUtc } from "./reminder-time";

export interface CampaignJobData { campaignId: string }

/** §26.2: constant window [09:00, 20:00) in the venue's local time. */
export const CAMPAIGN_WINDOW = { fromHour: 9, toHour: 20 } as const;
export const DEFAULT_CAMPAIGN_BATCH = 50;
export const DEFAULT_FAVORITE_SERVICES_DAYS = 365;
export const COUPON_FALLBACK_DAYS = 30;
const MAX_TEXT_CODEPOINTS = 3500;

const zoneCache = new Map<string, boolean>();
/** L11: is `tz` a usable IANA zone? (Intl throws RangeError otherwise); cached. */
export function isValidTimeZone(tz: string | null | undefined): boolean {
  if (!tz) return false;
  let ok = zoneCache.get(tz);
  if (ok === undefined) {
    try { new Intl.DateTimeFormat("en-US", { timeZone: tz }); ok = true; } catch { ok = false; }
    zoneCache.set(tz, ok);
  }
  return ok;
}

/** Local time of `now` inside [09:00, 20:00) of the IANA zone (DST aware via Intl). */
export function inCampaignWindow(now: Date, timeZone: string): boolean {
  const p = localParts(now, timeZone);
  const m = p.hour * 60 + p.minute;
  return m >= CAMPAIGN_WINDOW.fromHour * 60 && m < CAMPAIGN_WINDOW.toHour * 60;
}

/** ms until the window opens (next 09:00 local); 0 if `now` is inside the window. DST-correct: the target is a wall-clock time. */
export function delayUntilWindow(now: Date, timeZone: string): number {
  if (inCampaignWindow(now, timeZone)) return 0;
  const p = localParts(now, timeZone);
  const before = p.hour < CAMPAIGN_WINDOW.fromHour;
  const day = new Date(Date.UTC(p.year, p.month - 1, p.day + (before ? 0 : 1)));
  const open = zonedTimeToUtc(day.getUTCFullYear(), day.getUTCMonth() + 1, day.getUTCDate(), CAMPAIGN_WINDOW.fromHour, 0, timeZone);
  return Math.max(0, open.getTime() - now.getTime());
}

/** §26.4/§26.5: `full` = everything; `selective` = only `promo` whose services intersect the client's favourites. */
export function levelAllows(level: string, type: "promo" | "news", servicesOverlap: boolean): boolean {
  if (level === "full") return true;
  return level === "selective" && type === "promo" && servicesOverlap;
}

export interface CampaignTextInput {
  template: string;
  type: "promo" | "news";
  clientName: string | null;
  discountType?: "percent" | "fixed";
  discountValue?: number;
  currencyLabel: string;
  serviceNames: string[];
  validFrom: Date | null;
  validTo: Date | null; // exclusive end
  timeZone: string;
  /** Plain (unformatted) code, only when the campaign carries a coupon. */
  code?: string;
  buttons: boolean;
}

const dateOf = (d: Date, tz: string): string => {
  const p = localParts(d, tz);
  return `${String(p.day).padStart(2, "0")}.${String(p.month).padStart(2, "0")}.${p.year}`;
};

export function formatDiscount(type: "percent" | "fixed" | undefined, value: number | undefined, currency: string): string {
  if (type === undefined || value === undefined) return "";
  const n = Number.isInteger(value) ? String(value) : String(Number(value.toFixed(2)));
  return type === "percent" ? `${n}%` : `${n} ${currency}`;
}

export function formatServices(names: string[]): string {
  if (names.length === 0) return "усі послуги";
  const shown = names.slice(0, 3).map((n) => sanitizeLine(n)).join(", ");
  return names.length > 3 ? `${shown} та ще ${names.length - 3}` : shown;
}

/** First word of the full name, sanitized, <= 30 chars; empty / «Клієнт Telegram» -> «Вітаємо». */
export function greetingName(fullName: string | null | undefined): string {
  const clean = sanitizeLine(fullName, 80);
  if (!clean || /^клієнт telegram$/i.test(clean)) return "Вітаємо";
  return [...(clean.split(" ")[0] ?? "")].slice(0, 30).join("") || "Вітаємо";
}

/** §26.10 message: manager template + conditions (promo) + code + opt-out line, <= 3500 code points. No `parse_mode`. */
export function renderCampaignText(i: CampaignTextInput): string {
  const discount = formatDiscount(i.discountType, i.discountValue, i.currencyLabel);
  const service = formatServices(i.serviceNames);
  const from = i.validFrom ? dateOf(i.validFrom, i.timeZone) : "";
  const to = i.validTo ? dateOf(new Date(i.validTo.getTime() - 1000), i.timeZone) : "без обмеження строку";
  const code = i.code ? formatCode(i.code) : "";
  const values: Record<string, string> = {
    name: greetingName(i.clientName), discount, service, valid_from: from || "сьогодні", valid_to: to, code,
  };
  const body = stripFormatChars(i.template).replace(/\{(name|discount|service|valid_from|valid_to|code)\}/g, (_m, k: string) => sanitizeLine(values[k]!));
  const tail: string[] = [];
  if (i.type === "promo") {
    const period = i.validTo ? `діє з ${values.valid_from} по ${to}` : `діє з ${values.valid_from}, ${to}`;
    tail.push(`Умови: знижка ${discount} на ${service}, ${period}. Одна знижка на запис, не сумується з іншими акціями.`);
  }
  if (code && !i.template.includes("{code}")) tail.push(`Ваш код: ${code}`);
  if (!i.buttons) tail.push(code ? `Щоб записатися зі знижкою, напишіть сюди код ${code}` : "Щоб записатися, напишіть «Записатися».");
  tail.push(i.buttons ? UNSUBSCRIBE_NOTE_BUTTONS : UNSUBSCRIBE_NOTE);
  const tailText = `\n\n${tail.join("\n")}`;
  const room = Math.max(0, MAX_TEXT_CODEPOINTS - [...tailText].length);
  const head = [...body].length > room ? [...body].slice(0, room).join("").trimEnd() : body;
  return `${head}${tailText}`;
}

/** Keyboard layout for `campaign_offer`: [🎟 Записатися зі знижкою | 📅 Записатися] (`co`), [🔕 Не надсилати акції] (`i:mkoff`). */
export function buildCampaignKeyboard(token: string, withCoupon: boolean) {
  const book = withCoupon ? "🎟 Записатися зі знижкою" : "📅 Записатися";
  const off = "🔕 Не надсилати акції";
  return {
    actions: { rows: [[{ label: book, data: `co:${token}` }], [{ label: off, data: "i:mkoff" }]] },
    options: [{ verb: "co", label: book }, { verb: "i:mkoff", label: off }],
  };
}

export function campaignOptions(deps: BeautyDeps, defaultTimezone = "Europe/Kyiv"): CampaignEnqueueOptions {
  return {
    dailyLimit: deps.marketingDailyLimit ?? DEFAULT_MARKETING_DAILY_LIMIT,
    clientIntervalDays: deps.marketingClientIntervalDays ?? DEFAULT_CLIENT_INTERVAL_DAYS,
    favoriteServicesDays: deps.campaign?.favoriteServicesDays ?? DEFAULT_FAVORITE_SERVICES_DAYS,
    defaultTimezone: deps.campaign?.defaultTimezone ?? defaultTimezone,
    currencyLabel: deps.campaign?.currencyLabel ?? "грн",
  };
}

export interface CampaignRunResult {
  queued: number; skipped: number; deferredWindow: number; deferredLimit: boolean; finished: boolean;
}

/**
 * One pass of a due campaign (`pollCampaigns`): `scheduled -> sending`, then up to `batch` recipients, each in its own transaction
 * (all checks live in `enqueueCampaignMessage`). Recipients outside the window stay `pending`; the daily limit stops the pass.
 * Logs carry ids and result codes only (no text, names, codes).
 */
export async function processCampaign(deps: BeautyDeps, data: CampaignJobData): Promise<CampaignRunResult> {
  const res: CampaignRunResult = { queued: 0, skipped: 0, deferredWindow: 0, deferredLimit: false, finished: false };
  const campaign = await deps.data.getCampaign(data.campaignId);
  if (!campaign) throw new Error(`Campaign not found: ${data.campaignId}`);
  const at = deps.now();
  const logLine = (reason: string, status: "enqueued" | "skipped" = "enqueued") => deps.log.append({
    messageId: campaign.id, idempotencyKey: `campaign:${campaign.id}`, tenantId: campaign.tenantId, kind: "campaign", status, reason, at,
  });
  if (campaign.status === "scheduled") {
    if (!campaign.scheduledAt || campaign.scheduledAt.getTime() > at.getTime()) return res;
    if (await deps.data.startCampaign(campaign.id, at)) await logLine("campaign_state status=sending");
  } else if (campaign.status !== "sending") {
    return res; // draft / done / cancelled: nothing to send (cancelled recipients are closed by the backend)
  }
  const opts = campaignOptions(deps);
  const batch = deps.campaign?.batch ?? DEFAULT_CAMPAIGN_BATCH;
  const handled = () => res.queued + res.skipped;
  let cursor: string | undefined;
  scan: for (;;) {
    const page = await deps.data.listCampaignAudience(campaign.id, batch, cursor);
    if (page.length === 0) break;
    for (const r of page) {
      cursor = r.recipientId;
      const result = await deps.data.enqueueCampaignMessage(r.recipientId, deps.now(), opts);
      await logLine(`campaign_recipient campaign=${campaign.id} recipient=${r.recipientId} result=${result}`, result === "queued" ? "enqueued" : "skipped");
      if (result === "queued") res.queued++;
      else if (result.startsWith("skipped:")) res.skipped++;
      else if (result === "deferred_window") res.deferredWindow++;
      else if (result === "deferred_limit") { res.deferredLimit = true; break scan; }
      else if (result === "deferred_disabled") break scan; // module off: rows stay pending
      if (handled() >= batch) break scan;
    }
    if (page.length < batch) break;
  }
  res.finished = await deps.data.finishCampaignIfDrained(campaign.id, deps.now());
  if (res.finished) await logLine("campaign_state status=done");
  return res;
}
