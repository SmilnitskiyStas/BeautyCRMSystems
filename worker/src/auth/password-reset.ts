import { createHash, randomBytes } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { createTransport, type EmailConfig, type MailTransport } from "../adapters/email-sender";
import type { Logger } from "../adapters/pg-support";
import { isAllowedBaseUrl } from "../feedback/delivery";

/**
 * TASK-865 (ADR-024, contract 31.7.1): password reset delivery. The WORKER generates the token (32 random bytes, base64url, 43 chars),
 * stores only sha256(raw bytes) via auth_password_reset_attach_token and keeps the raw token in memory until the attempt ends.
 * The link is built ONLY from the worker config (PUBLIC_APP_URL): request headers are never visible here (F-10).
 * Logs and last_error carry reset ids and short codes only - never the token, the link or the address.
 */
export const LEASE_SECONDS = 300;
export const RESET_BATCH = 20;
export const POLL_EVERY_MS = 15_000;
export const PURGE_EVERY_MS = 10 * 60_000;
const RETRY_BASE_MS = 60_000;
const RETRY_CAP_MS = 10 * 60_000;

export interface Queryable { query(sql: string, params?: unknown[]): Promise<{ rows: Record<string, unknown>[]; rowCount: number | null }> }

export interface ClaimedRow { resetId: string; email: string; kind: "reset_link" | "changed_notice" }

export type DeliveryStatus = "pending" | "sent" | "failed";

/** Only the three worker functions + purge (EXECUTE matrix, contract 31.16). No table access. */
export class PgPasswordResetStore {
  constructor(private readonly pool: Queryable) {}

  async claim(now: Date, leaseSeconds: number, batch: number): Promise<ClaimedRow[]> {
    const { rows } = await this.pool.query("SELECT reset_id, email, kind FROM auth_password_reset_claim_delivery($1, $2, $3)", [now, leaseSeconds, batch]);
    return rows.map((r) => ({ resetId: String(r.reset_id), email: String(r.email), kind: r.kind as ClaimedRow["kind"] }));
  }

  async attachToken(resetId: string, tokenHash: Buffer, now: Date): Promise<boolean> {
    const { rows } = await this.pool.query("SELECT auth_password_reset_attach_token($1, $2, $3) AS ok", [resetId, tokenHash, now]);
    return rows[0]?.ok === true;
  }

  async mark(resetId: string, kind: ClaimedRow["kind"], status: DeliveryStatus, errorCode: string | null, nextAt: Date | null): Promise<void> {
    await this.pool.query("SELECT auth_password_reset_mark_delivery($1, $2, $3, $4, $5)", [resetId, kind, status, errorCode, nextAt]);
  }

  async purgeExpired(now: Date): Promise<number> {
    const { rows } = await this.pool.query("SELECT auth_identity_purge_expired($1) AS n", [now]);
    return Number(rows[0]?.n ?? 0);
  }
}

export class MailError extends Error {
  constructor(readonly code: string, readonly permanent: boolean) { super(code); }
}

export interface OutgoingMail { to: string; subject: string; text: string }
export interface PasswordMailer { send(mail: OutgoingMail): Promise<void> }

/** SMTP through the existing EMAIL_* configuration (sender address = EMAIL_FROM, set by ops). */
export class SmtpPasswordMailer implements PasswordMailer {
  private readonly transport: MailTransport;
  constructor(private readonly cfg: Extract<EmailConfig, { mode: "smtp" }>, transport?: MailTransport) {
    this.transport = transport ?? createTransport(cfg);
  }
  async send(mail: OutgoingMail): Promise<void> {
    try {
      await this.transport.sendMail({ from: this.cfg.from, to: mail.to, subject: mail.subject, text: mail.text });
    } catch (err) {
      const code = (err as { responseCode?: number })?.responseCode;
      // 5xx from SMTP (rejected address / auth): retrying cannot help; network / 4xx: retry. Error text is never propagated.
      throw new MailError(typeof code === "number" && code >= 500 ? "smtp_rejected" : "smtp_unavailable", typeof code === "number" && code >= 500);
    }
  }
}

/**
 * EMAIL_MODE=mock: nothing is sent and the log has no body. The composed mail is kept in memory (`outbox`, for tests) and, only when
 * WORKER_MOCK_MAIL_DIR is set in a Development environment, written to that folder as a .txt file (manual local testing).
 */
export class MockPasswordMailer implements PasswordMailer {
  readonly outbox: OutgoingMail[] = [];
  constructor(private readonly logger: Logger, private readonly dir?: string) {}
  async send(mail: OutgoingMail): Promise<void> {
    this.outbox.push(mail);
    if (this.outbox.length > 100) this.outbox.shift();
    if (this.dir) {
      mkdirSync(this.dir, { recursive: true });
      writeFileSync(join(this.dir, `mail-${Date.now()}-${randomBytes(4).toString("hex")}.txt`), `To: ${mail.to}\nSubject: ${mail.subject}\n\n${mail.text}\n`, "utf8");
    }
    this.logger.info("password mail queued (mock, not sent)", {});
  }
}

export function createPasswordMailer(cfg: EmailConfig, env: NodeJS.ProcessEnv, logger: Logger): PasswordMailer {
  if (cfg.mode === "smtp") return new SmtpPasswordMailer(cfg);
  const dev = env["ASPNETCORE_ENVIRONMENT"] === "Development" && env["NODE_ENV"] !== "production";
  return new MockPasswordMailer(logger, dev ? env["WORKER_MOCK_MAIL_DIR"]?.trim() || undefined : undefined);
}

/** PUBLIC_APP_URL: absolute https URL (http only in Development / loopback), no query or fragment. null = not configured / invalid. */
export function loadPublicAppUrl(env: NodeJS.ProcessEnv): string | null {
  try {
    const u = new URL(env["PUBLIC_APP_URL"] ?? "");
    if (!isAllowedBaseUrl(u, env) || u.search || u.hash || u.username || u.password) return null;
    return (u.origin + u.pathname).replace(/\/+$/, "");
  } catch { return null; }
}

export const newResetToken = (): { token: string; hash: Buffer } => {
  const raw = randomBytes(32);
  return { token: raw.toString("base64url"), hash: createHash("sha256").update(raw).digest() };
};

export const RESET_SUBJECT = "Скидання пароля Beauty CRM";

/** PLACEHOLDER text: final wording is approved by a human before release (contract 31.7.1). No business name. */
export function resetMailText(link: string, ttlMinutes: number): string {
  return `Ми отримали запит на скидання пароля до акаунта Beauty CRM.\n\nЩоб задати новий пароль, відкрийте посилання (діє ${ttlMinutes} хв, лише один раз):\n${link}\n\nЯкщо ви не запитували скидання, проігноруйте лист.`;
}

export const resetLink = (publicAppUrl: string, token: string): string => `${publicAppUrl}/reset-password#t=${token}`;

export const CHANGED_SUBJECT = "Пароль Beauty CRM змінено";

/** PLACEHOLDER text (contract 31.7.3): final wording is approved by a human. No business name, no token, no IP/device. */
export function changedMailText(forgotUrl: string): string {
  return `Пароль вашого акаунта Beauty CRM змінено.\n\nЯкщо це були не ви — скиньте пароль:\n${forgotUrl}`;
}

export const forgotPasswordLink = (publicAppUrl: string): string => `${publicAppUrl}/forgot-password`;

export function retryDelayMs(attempt: number): number {
  return Math.min(RETRY_BASE_MS * 2 ** Math.max(0, attempt - 1), RETRY_CAP_MS);
}

/**
 * changed_notice is retried up to ~24 h (contract 31.7.3): claim() does not return the row age, so the budget is the cumulative
 * backoff (1+2+4+8 min, then 10 min steps) counted per process; a worker restart restarts the count (accepted, bounded by purge).
 */
export const NOTICE_MAX_ATTEMPTS = 147;

export interface ResetPollContext {
  store: PgPasswordResetStore;
  mailer: PasswordMailer;
  /** null = PUBLIC_APP_URL missing/invalid: nothing is claimed (rows wait until TTL), error logged once per pass. */
  publicAppUrl: string | null;
  logger: Logger;
  ttlMinutes?: number;
  now?: () => Date;
  batch?: number;
  newToken?: typeof newResetToken;
  /** attempts per reset id for the backoff (claim does not return it); bounded, process-local. */
  attempts?: Map<string, number>;
}

const defaultAttempts = new Map<string, number>();

/** One pass: claim -> (reset_link: new token -> attach -> send; changed_notice: send, no token) -> mark. */
export async function pollPasswordResets(ctx: ResetPollContext): Promise<{ sent: number; retried: number; failed: number }> {
  const out = { sent: 0, retried: 0, failed: 0 };
  if (!ctx.publicAppUrl) {
    ctx.logger.error("password reset delivery disabled: PUBLIC_APP_URL is missing or invalid", {});
    return out;
  }
  const now = ctx.now ?? (() => new Date());
  const attempts = ctx.attempts ?? defaultAttempts;
  for (const row of await ctx.store.claim(now(), LEASE_SECONDS, ctx.batch ?? RESET_BATCH)) {
    try {
      const notice = row.kind === "changed_notice";
      // attempts are keyed per kind: both kinds of one reset id may be in flight
      const akey = (notice ? "n:" : "r:") + row.resetId;
      let mail: OutgoingMail;
      if (notice) {
        mail = { to: row.email, subject: CHANGED_SUBJECT, text: changedMailText(forgotPasswordLink(ctx.publicAppUrl)) }; // no token, attach_token is not called
      } else {
        // every attempt gets a NEW token: the link of a failed attempt never works (contract 31.7.1 step 2)
        const { token, hash } = (ctx.newToken ?? newResetToken)();
        if (!(await ctx.store.attachToken(row.resetId, hash, now()))) { attempts.delete(akey); continue; } // superseded / expiring: no mail
        mail = { to: row.email, subject: RESET_SUBJECT, text: resetMailText(resetLink(ctx.publicAppUrl, token), ctx.ttlMinutes ?? 30) };
      }
      const n = (attempts.get(akey) ?? 0) + 1;
      try {
        await ctx.mailer.send(mail);
        await ctx.store.mark(row.resetId, row.kind, "sent", null, null);
        attempts.delete(akey);
        out.sent++;
      } catch (err) {
        const code = err instanceof MailError ? err.code : "send_failed";
        const permanent = err instanceof MailError && err.permanent;
        if (permanent || (notice && n >= NOTICE_MAX_ATTEMPTS)) {
          await ctx.store.mark(row.resetId, row.kind, "failed", permanent ? code : "retries_exhausted", null);
          attempts.delete(akey);
          out.failed++;
          ctx.logger.error("password reset delivery failed permanently", { resetId: row.resetId, kind: row.kind, errorCode: code });
        } else {
          if (attempts.size > 5000) attempts.clear();
          attempts.set(akey, n);
          await ctx.store.mark(row.resetId, row.kind, "pending", code, new Date(now().getTime() + retryDelayMs(n)));
          out.retried++;
          ctx.logger.info("password reset delivery will retry", { resetId: row.resetId, kind: row.kind, attempt: n, errorCode: code });
        }
      }
    } catch (err) {
      ctx.logger.error("password reset delivery error", { resetId: row.resetId, errorCode: (err as { code?: string })?.code ?? "error" }); // lease expires, retried
    }
  }
  return out;
}
