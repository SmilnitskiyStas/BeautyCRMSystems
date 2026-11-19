import nodemailer from "nodemailer";
import type { ChannelSenderPort, StoredMessage } from "../ports";
import { ChannelSendError } from "./channel-sender";
import type { Logger } from "./pg-support";

export type EmailConfig =
  | { mode: "mock" }
  | { mode: "smtp"; host: string; port: number; secure: boolean; user: string; password: string; from: string };

/** Мінімум від nodemailer-транспорту, який нам потрібен (зручно підміняти в тестах). */
export interface MailTransport {
  sendMail(mail: { from: string; to: string; subject: string; text: string }): Promise<{ messageId?: string }>;
}

/**
 * EMAIL_MODE=mock|smtp (за замовчуванням mock). Поза mock потрібна повна конфігурація
 * (SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASSWORD, EMAIL_FROM; SMTP_SECURE=true|false) — інакше помилка на старті.
 */
export function loadEmailConfig(env: NodeJS.ProcessEnv = process.env): EmailConfig {
  const mode = (env.EMAIL_MODE?.trim().toLowerCase() || "mock");
  if (mode === "mock") return { mode };
  if (mode !== "smtp") throw new Error(`EMAIL_MODE must be 'mock' or 'smtp', got '${mode}'`);
  const missing = ["SMTP_HOST", "SMTP_PORT", "SMTP_USER", "SMTP_PASSWORD", "EMAIL_FROM"].filter((k) => !env[k]?.trim());
  if (missing.length) throw new Error(`EMAIL_MODE=smtp requires: ${missing.join(", ")}`);
  const port = Number(env.SMTP_PORT);
  if (!Number.isInteger(port) || port <= 0 || port > 65535) throw new Error("SMTP_PORT must be a valid port number");
  const secure = (env.SMTP_SECURE ?? "false").trim().toLowerCase();
  if (secure !== "true" && secure !== "false") throw new Error("SMTP_SECURE must be 'true' or 'false'");
  return { mode: "smtp", host: env.SMTP_HOST!.trim(), port, secure: secure === "true", user: env.SMTP_USER!, password: env.SMTP_PASSWORD!, from: env.EMAIL_FROM!.trim() };
}

/** `anna@example.com` -> `a***@e***.com` (для логів; без PII). */
export function maskEmail(address: string): string {
  const at = address.lastIndexOf("@");
  if (at < 1) return "***";
  const dot = address.lastIndexOf(".");
  const domain = address.slice(at + 1);
  const host = dot > at ? domain.slice(0, dot - at - 1) : domain;
  const tld = dot > at ? address.slice(dot) : "";
  return `${address[0]}***@${host[0] ?? ""}***${tld}`;
}

const SUBJECTS: Partial<Record<StoredMessage["kind"], string>> = {
  confirmation: "Підтвердження запису",
  reminder: "Нагадування про візит",
  review: "Дякуємо за візит",
};
export const emailSubject = (kind: StoredMessage["kind"]): string => SUBJECTS[kind] ?? "Повідомлення від салону";

export function createTransport(cfg: Extract<EmailConfig, { mode: "smtp" }>): MailTransport {
  return nodemailer.createTransport({ host: cfg.host, port: cfg.port, secure: cfg.secure, auth: { user: cfg.user, pass: cfg.password } });
}

/** ChannelSenderPort для типу `email`: адресат = msg.address, тема з kind, тіло = msg.text (простий текст). */
export class EmailSender implements ChannelSenderPort {
  constructor(
    private readonly cfg: EmailConfig,
    private readonly logger: Logger,
    private readonly transport?: MailTransport,
  ) {
    if (cfg.mode === "smtp" && !transport) this.transport = createTransport(cfg);
  }

  async send(msg: StoredMessage): Promise<void> {
    if (!msg.address.includes("@")) throw new ChannelSendError("Email recipient address is invalid", true);
    if (this.cfg.mode === "mock") {
      // Лише факт: ні адреси, ні тексту (PII).
      this.logger.info("email queued (mock, not sent)", { messageId: msg.id, kind: msg.kind });
      return;
    }
    try {
      const info = await this.transport!.sendMail({ from: this.cfg.from, to: msg.address, subject: emailSubject(msg.kind), text: msg.text });
      this.logger.info("email sent", { messageId: msg.id, kind: msg.kind, smtpMessageId: info.messageId });
    } catch (err) {
      const e = err as { code?: string; responseCode?: number };
      // 5xx від SMTP (відхилена адреса/автентифікація) — повтор не допоможе; мережа/4xx — повторюємо.
      const permanent = typeof e.responseCode === "number" && e.responseCode >= 500;
      throw new ChannelSendError(`SMTP send failed (code ${e.code ?? "unknown"}, response ${e.responseCode ?? "-"})`, permanent);
    }
  }
}
