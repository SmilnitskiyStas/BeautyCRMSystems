import type { ChannelSenderPort, SendReceipt, StoredMessage } from "../ports";
import type { TenantDb } from "../db/tenant-db";
import { readCredentials } from "./channel-crypto";
import type { ChannelErrorCode } from "../jobs/beauty-common";

export const TELEGRAM_API = "https://api.telegram.org";
export const INSTAGRAM_GRAPH_URL = "https://graph.facebook.com/v21.0/me/messages";
const META_WINDOW_MS = 24 * 3_600_000;

/** `permanent` => retrying cannot help (bad token, closed Meta window): outbox marks the message failed at once. */
export class ChannelSendError extends Error {
  constructor(message: string, readonly permanent: boolean, readonly code?: ChannelErrorCode, readonly extra: { httpStatus?: number; retryAfterSec?: number } = {}) {
    super(message); this.name = "ChannelSendError";
  }
}

type Fetch = typeof fetch;

/**
 * Shared Telegram `sendMessage` transport (client messages and the owner's inventory alerts). Errors never contain URL, token or body.
 * `disablePreview` adds `disable_web_page_preview: true` (staff alerts only; client messages are sent exactly as before).
 */
export async function sendTelegramText(
  http: Fetch, baseUrl: string, token: string, chatId: string, text: string,
  opts: { replyMarkup?: ReturnType<typeof toReplyMarkup>; disablePreview?: boolean } = {},
): Promise<{ externalMessageId: string | null }> {
  const body = { chat_id: chatId, text, ...(opts.replyMarkup ? { reply_markup: opts.replyMarkup } : {}), ...(opts.disablePreview ? { disable_web_page_preview: true } : {}) };
  const res = await postJson(http, "Telegram sendMessage", `${baseUrl}/bot${token}/sendMessage`, body, {}, true);
  // Telegram message_id -> beauty_messages.external_message_id (binds a button press to this very message).
  const json = await res.json().catch(() => null) as { result?: { message_id?: unknown } } | null;
  const id = json?.result?.message_id;
  return { externalMessageId: typeof id === "number" || typeof id === "string" ? String(id) : null };
}

async function postJson(http: Fetch, what: string, url: string, body: unknown, headers: Record<string, string>, telegram = false): Promise<Response> {
  let res: Response;
  try {
    res = await http(url, { method: "POST", headers: { "content-type": "application/json", ...headers }, body: JSON.stringify(body) });
  } catch {
    throw new ChannelSendError(`${what} network error`, false);
  }
  if (res.ok) return res;
  const httpStatus = res.status;
  if (telegram && res.status === 403) throw new ChannelSendError(`${what} failed with HTTP 403`, true, "bot_blocked", { httpStatus });
  if (telegram && res.status === 400 && /chat not found/i.test(await res.text().catch(() => ""))) {
    throw new ChannelSendError(`${what} failed with HTTP 400`, true, "chat_not_found", { httpStatus }); // body is never kept in the error
  }
  let retryAfterSec: number | undefined;
  if (telegram && res.status === 429) {
    const j = await res.json().catch(() => null) as { parameters?: { retry_after?: unknown } } | null;
    const ra = Number(j?.parameters?.retry_after);
    if (Number.isFinite(ra) && ra > 0) retryAfterSec = ra;
  }
  throw new ChannelSendError(`${what} failed with HTTP ${res.status}`, !(res.status === 429 || res.status >= 500), undefined, { httpStatus, retryAfterSec });
}

/** Telegram limits: callback_data 1..64 bytes (UTF-8). */
const MAX_CALLBACK_BYTES = 64;

/**
 * Neutral layout -> `{inline_keyboard: [[{text, callback_data}]]}` (same mapping as the .NET adapter, no parse_mode).
 * An invalid layout (empty label, callback_data over 64 bytes) sends the plain text: a broken keyboard must not block delivery.
 */
export function toReplyMarkup(actions: StoredMessage["actions"]): { inline_keyboard: { text: string; callback_data: string }[][] } | null {
  if (!actions || actions.rows.length === 0) return null;
  for (const b of actions.rows.flat()) {
    if (!b.label || !b.data || Buffer.byteLength(b.data, "utf8") > MAX_CALLBACK_BYTES) return null;
  }
  return { inline_keyboard: actions.rows.map((row) => row.map((b) => ({ text: b.label, callback_data: b.data }))) };
}

/**
 * ChannelSenderPort: loads the message's channel (tenant-scoped), decrypts the token (AES-GCM, key from
 * Channels__EncryptionKey) and calls the Telegram / Instagram HTTP API. Errors never contain URL or token.
 */
export class HttpChannelSender implements ChannelSenderPort {
  constructor(
    private readonly db: TenantDb,
    private readonly key: Buffer,
    private readonly http: Fetch = fetch,
    private readonly now: () => Date = () => new Date(),
    private readonly urls = { telegram: TELEGRAM_API, instagram: INSTAGRAM_GRAPH_URL },
    /** Відправник для типу `email` (SMTP/mock; конфіг з env, не з beauty_channels). */
    private readonly email?: ChannelSenderPort,
  ) {}

  async send(msg: StoredMessage): Promise<void | SendReceipt> {
    const info = await this.db.tx(async (c) => (await c.query(
      `SELECT ch.type, ch.is_active, ch.credentials_encrypted, conv.external_chat_id,
              (SELECT max(x.sent_at) FROM beauty_messages x WHERE x.conversation_id = conv.id AND x.direction = 'inbound') AS last_inbound
       FROM beauty_messages m
       JOIN beauty_conversations conv ON conv.id = m.conversation_id
       JOIN beauty_channels ch ON ch.id = conv.channel_id
       WHERE m.id = $1`, [msg.id])).rows[0]);
    if (!info) throw new ChannelSendError("Channel for message not found", true);
    if (!info.is_active) throw new ChannelSendError("Channel is inactive", true, "channel_inactive");
    const type = String(info.type).toLowerCase();
    if (type === "email") {
      if (!this.email) throw new ChannelSendError("Email sender is not configured", true);
      return this.email.send(msg); // адреса = msg.address; токенів каналу немає, SMTP-секрети лише з env
    }
    let token: string | undefined;
    try { token = readCredentials(this.key, info.credentials_encrypted).token; }
    catch { throw new ChannelSendError("Cannot decrypt channel credentials (wrong Channels__EncryptionKey?)", true); }
    if (!token) throw new ChannelSendError(`${type} token is not configured`, true, "token_missing");

    if (type === "telegram") {
      const markup = toReplyMarkup(msg.actions);
      return sendTelegramText(this.http, this.urls.telegram, token, info.external_chat_id, msg.text, { replyMarkup: markup });
    } else if (type === "instagram") {
      const last = info.last_inbound ? new Date(info.last_inbound).getTime() : null;
      if (last === null || this.now().getTime() - last > META_WINDOW_MS) {
        throw new ChannelSendError("Meta 24h messaging window is closed", true);
      }
      await this.post("Instagram send", this.urls.instagram,
        { recipient: { id: info.external_chat_id }, message: { text: msg.text }, messaging_type: "RESPONSE" },
        { Authorization: `Bearer ${token}` }); // actions are ignored: quick replies are V2 (§23.9)
    } else {
      throw new ChannelSendError(`Channel type '${type}' is not supported by the worker`, true);
    }
  }

  private post(what: string, url: string, body: unknown, headers: Record<string, string>): Promise<Response> {
    return postJson(this.http, what, url, body, headers);
  }
}
