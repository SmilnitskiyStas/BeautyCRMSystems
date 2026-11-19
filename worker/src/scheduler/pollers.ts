import type { PgPollStore } from "../adapters/pg-poll-store";
import type { Logger } from "../adapters/pg-support";
import { currentTenantId, withTenant } from "../db/tenant-db";
import type { TenantDirectory } from "../db/tenants";
import { processOutbox } from "../jobs/beauty-outbox";
import { processConfirmation } from "../jobs/beauty-confirmation";
import { processReviewRequest } from "../jobs/beauty-review";
import { processDueReminder } from "../jobs/beauty-reminder";
import { processWinback } from "../jobs/beauty-winback";
import { processCampaign } from "../jobs/beauty-campaign";
import type { BeautyDeps } from "../ports";
import { DEFAULT_INVENTORY_CONFIG, type InventoryAlertConfig, type InventoryAlertStore } from "../inventory/alerts";
import { DEFAULT_TELEGRAM_CONFIG, type InventoryTelegramConfig, type InventoryTelegramStore } from "../inventory/telegram";

export interface PollContext {
  deps: BeautyDeps;
  store: PgPollStore;
  tenants: TenantDirectory;
  logger: Logger;
  batchSize?: number;
  backoffMs?: number;
  /**
   * WORKER_REMINDERS_ENABLED (§22.2). `false` => pollReminders does nothing. main.ts always sets it from the env
   * (default false); an unset value means "enabled" only for in-process callers/tests that build the context by hand.
   */
  remindersEnabled?: boolean;
  /** WORKER_REMINDER_MIN_LEAD_MINUTES (default 15). */
  reminderMinLeadMinutes?: number;
  /** WORKER_REMINDER_BUTTONS_ENABLED (§23.2, default false): inline buttons under the reminder (also needs the channel flag). */
  reminderButtonsEnabled?: boolean;
  /** WORKER_REVIEWS_ENABLED (TASK-760, default false in main.ts). `false` => pollReviews does nothing; unset = enabled (hand-built contexts/tests). */
  reviewsEnabled?: boolean;
  /** WORKER_CAMPAIGNS_ENABLED (§26.2, default false in main.ts). `false` => pollCampaigns does nothing; unset = enabled (hand-built contexts/tests). */
  campaignsEnabled?: boolean;
  /** WORKER_WINBACK_ENABLED (§26.2, default false in main.ts). `false` => pollWinback does nothing; unset = enabled (hand-built contexts/tests). */
  winbackEnabled?: boolean;
  /** WORKER_INVENTORY_ENABLED (§32.20, default false in main.ts). `false` => pollInventory does nothing; unset = enabled (hand-built contexts/tests). */
  inventoryEnabled?: boolean;
  /** Inventory alerts store (PgInventoryAlerts); without it pollInventory is idle. */
  inventory?: InventoryAlertStore;
  inventoryConfig?: InventoryAlertConfig;
  /** WORKER_INVENTORY_TELEGRAM_ENABLED (§32.24.6, default false): ONLY the literal `true` enables build+send; cleanup always runs. */
  inventoryTelegramEnabled?: boolean;
  /** M1 pre-L3 mode: when set, build+send only for these tenant ids (lower-case UUIDs). */
  inventoryTelegramTenantAllowlist?: ReadonlySet<string>;
  inventoryTelegram?: InventoryTelegramStore;
  inventoryTelegramConfig?: InventoryTelegramConfig;
}

export interface PollResult { processed: number; errors: number }

/** Runs `fn` for every tenant; one failing tenant/item is logged and counted, never hides the others. */
async function forEachTenant(ctx: PollContext, fn: () => Promise<PollResult>, what: string): Promise<PollResult> {
  const total: PollResult = { processed: 0, errors: 0 };
  for (const tenantId of await ctx.tenants.listTenantIds()) {
    try {
      const r = await withTenant(tenantId, fn);
      total.processed += r.processed;
      total.errors += r.errors;
    } catch (err) {
      total.errors++;
      ctx.logger.error(`${what} failed for tenant`, { tenantId, errorCode: errorCode(err) });
    }
  }
  return total;
}

/**
 * Due beauty_reminders -> outbox messages (idempotent per reminder row, key `reminder:{id}`), checks of §22.5.
 * Runs every minute, only with WORKER_REMINDERS_ENABLED=true. Logs carry ids and codes only.
 */
export function pollReminders(ctx: PollContext): Promise<PollResult> {
  if (ctx.remindersEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const r of await ctx.store.listDueReminders(ctx.deps.now(), ctx.batchSize ?? 100)) {
      try {
        await processDueReminder(ctx.deps, ctx.store, r, { minLeadMinutes: ctx.reminderMinLeadMinutes, buttonsEnabled: ctx.reminderButtonsEnabled === true });
        res.processed++;
      } catch (err) {
        res.errors++;
        ctx.logger.error("reminder failed", { reminderId: r.id, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollReminders");
}

/** Error class/code only: DB and library messages can echo values (personal data). */
function errorCode(err: unknown): string {
  const e = err as { code?: unknown; name?: unknown } | null;
  return typeof e?.code === "string" ? e.code : typeof e?.name === "string" ? e.name : "error";
}

/**
 * Pending outbound messages -> channel send. A failed attempt is stored in beauty_messages
 * (attempts/last_error); after 3 attempts in total the message becomes 'failed'.
 */
export function pollOutbox(ctx: PollContext): Promise<PollResult> {
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const m of await ctx.store.listPendingOutbox(ctx.deps.now(), ctx.batchSize ?? 50, ctx.backoffMs ?? 5_000, { campaigns: ctx.campaignsEnabled !== false, winback: ctx.winbackEnabled !== false })) {
      try {
        await processOutbox(ctx.deps, { messageId: m.id }, m.attempts);
        res.processed++;
      } catch (err) {
        // already persisted by processOutbox (attempt/last_error/log); counted here so the scheduler sees it
        res.errors++;
        ctx.logger.error("outbox send failed", { messageId: m.id, attempt: m.attempts + 1, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollOutbox");
}

/** "Давно не були": daily; keys are per lapse, so extra runs never duplicate. */
export function pollWinback(ctx: PollContext, inactiveDays?: number): Promise<PollResult> {
  if (ctx.winbackEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    if (!(await ctx.deps.data.isMarketingEnabled())) return { processed: 0, errors: 0 }; // M4: module off, silently idle
    const r = await processWinback(ctx.deps, inactiveDays);
    return { processed: r.sent, errors: 0 };
  }, "pollWinback");
}

/**
 * Review request after a completed visit. Idempotent: key `review:{appointmentId}`; appointments that already
 * have the message are excluded by the query, and a concurrent duplicate is rejected by the unique key.
 */
export function pollReviews(ctx: PollContext): Promise<PollResult> {
  if (ctx.reviewsEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const id of await ctx.store.listCompletedWithoutReview(ctx.deps.now(), ctx.batchSize ?? 50)) {
      try {
        if (await processReviewRequest(ctx.deps, { appointmentId: id }) === "queued") res.processed++;
      } catch (err) {
        res.errors++;
        ctx.logger.error("review request failed", { appointmentId: id, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollReviews");
}

/**
 * Due campaigns (§26.8): `scheduled -> sending`, recipients of the frozen audience -> outbox messages, `done` when drained.
 * Runs every minute, only with WORKER_CAMPAIGNS_ENABLED=true. Logs carry ids and result codes only.
 */
export function pollCampaigns(ctx: PollContext): Promise<PollResult> {
  if (ctx.campaignsEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    if (!(await ctx.deps.data.isMarketingEnabled())) return res; // M4: module off, silently idle
    for (const id of await ctx.deps.data.listDueCampaigns(ctx.deps.now())) {
      try {
        res.processed += (await processCampaign(ctx.deps, { campaignId: id })).queued;
      } catch (err) {
        res.errors++;
        ctx.logger.error("campaign failed", { campaignId: id, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollCampaigns");
}

/** Підтвердження запису: нещодавні записи без `confirmation:{appointmentId}` -> outbox (транзакційне, без marketingConsent). */
export function pollConfirmations(ctx: PollContext): Promise<PollResult> {
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const id of await ctx.store.listAppointmentsWithoutConfirmation(ctx.deps.now(), ctx.batchSize ?? 50)) {
      try {
        if (await processConfirmation(ctx.deps, { appointmentId: id }) === "queued") res.processed++;
      } catch (err) {
        res.errors++;
        ctx.logger.error("confirmation failed", { appointmentId: id, error: String(err) });
      }
    }
    return res;
  }, "pollConfirmations");
}

/**
 * Staff alerts of the inventory module (§32.19-§32.20): hourly; resolves stale alerts every run, creates new ones only after the
 * venue's local gate hour, deduplicated by key. Gated by WORKER_INVENTORY_ENABLED and the tenant's `beauty_inventory` module.
 * Logs carry counts only (no product names, quantities or client data).
 */
export function pollInventory(ctx: PollContext): Promise<PollResult> {
  if (ctx.inventoryEnabled === false || !ctx.inventory) return Promise.resolve({ processed: 0, errors: 0 });
  const store = ctx.inventory;
  return forEachTenant(ctx, async () => {
    if (!(await ctx.deps.data.isInventoryEnabled())) return { processed: 0, errors: 0 }; // module off, silently idle
    const r = await store.runForTenant(ctx.deps.now(), ctx.inventoryConfig ?? DEFAULT_INVENTORY_CONFIG);
    ctx.logger.info(`inventory_alerts tenant=${currentTenantId()} locations=${r.locations} created=${r.created} resolved=${r.resolved} capped=${r.capped}`);
    return { processed: r.created + r.resolved, errors: r.errors };
  }, "pollInventory");
}

/**
 * Owner's Telegram inventory alerts (§32.24.5): every 5 min. Cleanup (expired links, tenant_inactive, stale pending, 30-day retention)
 * ALWAYS runs; build + send only with WORKER_INVENTORY_TELEGRAM_ENABLED=true and the tenant's `beauty_inventory` module.
 * Logs carry counts and ids only (never chat_id, tokens, texts).
 */
export function pollInventoryTelegram(ctx: PollContext): Promise<PollResult> {
  const store = ctx.inventoryTelegram;
  if (!store) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const now = ctx.deps.now();
    await store.cleanup(now);
    if (ctx.inventoryTelegramEnabled !== true) return { processed: 0, errors: 0 };
    // M1: pre-L3 staging mode: only allowlisted tenants are processed (cleanup above still runs for all).
    const allow = ctx.inventoryTelegramTenantAllowlist;
    if (allow && !allow.has(String(currentTenantId()).toLowerCase())) return { processed: 0, errors: 0 };
    if (!(await ctx.deps.data.isInventoryEnabled())) return { processed: 0, errors: 0 }; // module off, silently idle
    const r = await store.runForTenant(now, ctx.inventoryTelegramConfig ?? DEFAULT_TELEGRAM_CONFIG);
    ctx.logger.info(`inventory_telegram tenant=${currentTenantId()} built=${r.built} sent=${r.sent} failed=${r.failed} revoked=${r.revoked} capped=${r.capped}`);
    return { processed: r.built + r.sent, errors: r.errors };
  }, "pollInventoryTelegram");
}
