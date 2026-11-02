import type { ChannelSenderPort, SendReceipt, StoredMessage } from "../ports";
import type { TenantDb } from "../db/tenant-db";
import { readCredentials } from "./channel-crypto";
import type { ChannelErrorCode } from "../jobs/beauty-common";

export const TELEGRAM_API = "https://api.telegram.org";
export const INSTAGRAM_GRAPH_URL = "https://graph.facebook.com/v21.0/me/messages";
const META_WINDOW_MS = 24 * 3_600_000;

/** `permanent` => retrying cannot help (bad token, closed Meta window): outbox marks the message failed at once. */
export class ChannelSendError extends Error {
  constructor(message: string, readonly permanent: boolean, readonly code?: ChannelErrorCode) { super(message); this.name = "ChannelSendError"; }
}

type Fetch = typeof fetch;

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
    let token: string | undefined;
    try { token = readCredentials(this.key, info.credentials_encrypted).token; }
    catch { throw new ChannelSendError("Cannot decrypt channel credentials (wrong Channels__EncryptionKey?)", true); }
    if (!token) throw new ChannelSendError(`${type} token is not configured`, true, "token_missing");

    if (type === "telegram") {
      const markup = toReplyMarkup(msg.actions);
      const res = await this.post(`Telegram sendMessage`, `${this.urls.telegram}/bot${token}/sendMessage`,
        { chat_id: info.external_chat_id, text: msg.text, ...(markup ? { reply_markup: markup } : {}) }, {}, true);
      // Telegram message_id -> beauty_messages.external_message_id (binds a button press to this very message).
      const body = await res.json().catch(() => null) as { result?: { message_id?: unknown } } | null;
      const id = body?.result?.message_id;
      return { externalMessageId: typeof id === "number" || typeof id === "string" ? String(id) : null };
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

  private async post(what: string, url: string, body: unknown, headers: Record<string, string>, telegram = false): Promise<Response> {
    let res: Response;
    try {
      res = await this.http(url, { method: "POST", headers: { "content-type": "application/json", ...headers }, body: JSON.stringify(body) });
    } catch {
      throw new ChannelSendError(`${what} network error`, false);
    }
    if (res.ok) return res;
    if (telegram && res.status === 403) throw new ChannelSendError(`${what} failed with HTTP 403`, true, "bot_blocked");
    if (telegram && res.status === 400 && /chat not found/i.test(await res.text().catch(() => ""))) {
      throw new ChannelSendError(`${what} failed with HTTP 400`, true, "chat_not_found"); // body is never kept in the error
    }
    throw new ChannelSendError(`${what} failed with HTTP ${res.status}`, !(res.status === 429 || res.status >= 500));
  }
}
