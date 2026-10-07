import type { ChannelSenderPort, StoredMessage } from "../ports";
import type { TenantDb } from "../db/tenant-db";
import { readCredentials } from "./channel-crypto";

export const TELEGRAM_API = "https://api.telegram.org";
export const INSTAGRAM_GRAPH_URL = "https://graph.facebook.com/v21.0/me/messages";
const META_WINDOW_MS = 24 * 3_600_000;

/** `permanent` => retrying cannot help (bad token, closed Meta window): outbox marks the message failed at once. */
export class ChannelSendError extends Error {
  constructor(message: string, readonly permanent: boolean) { super(message); this.name = "ChannelSendError"; }
}

type Fetch = typeof fetch;

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

  async send(msg: StoredMessage): Promise<void> {
    const info = await this.db.tx(async (c) => (await c.query(
      `SELECT ch.type, ch.is_active, ch.credentials_encrypted, conv.external_chat_id,
              (SELECT max(x.sent_at) FROM beauty_messages x WHERE x.conversation_id = conv.id AND x.direction = 'inbound') AS last_inbound
       FROM beauty_messages m
       JOIN beauty_conversations conv ON conv.id = m.conversation_id
       JOIN beauty_channels ch ON ch.id = conv.channel_id
       WHERE m.id = $1`, [msg.id])).rows[0]);
    if (!info) throw new ChannelSendError("Channel for message not found", true);
    if (!info.is_active) throw new ChannelSendError("Channel is inactive", true);
    const type = String(info.type).toLowerCase();
    let token: string | undefined;
    try { token = readCredentials(this.key, info.credentials_encrypted).token; }
    catch { throw new ChannelSendError("Cannot decrypt channel credentials (wrong Channels__EncryptionKey?)", true); }
    if (!token) throw new ChannelSendError(`${type} token is not configured`, true);

    if (type === "telegram") {
      await this.post(`Telegram sendMessage`, `${this.urls.telegram}/bot${token}/sendMessage`,
        { chat_id: info.external_chat_id, text: msg.text }, {});
    } else if (type === "instagram") {
      const last = info.last_inbound ? new Date(info.last_inbound).getTime() : null;
      if (last === null || this.now().getTime() - last > META_WINDOW_MS) {
        throw new ChannelSendError("Meta 24h messaging window is closed", true);
      }
      await this.post("Instagram send", this.urls.instagram,
        { recipient: { id: info.external_chat_id }, message: { text: msg.text }, messaging_type: "RESPONSE" },
        { Authorization: `Bearer ${token}` });
    } else {
      throw new ChannelSendError(`Channel type '${type}' is not supported by the worker`, true);
    }
  }

  private async post(what: string, url: string, body: unknown, headers: Record<string, string>): Promise<void> {
    let res: Response;
    try {
      res = await this.http(url, { method: "POST", headers: { "content-type": "application/json", ...headers }, body: JSON.stringify(body) });
    } catch {
      throw new ChannelSendError(`${what} network error`, false);
    }
    if (res.ok) return;
    throw new ChannelSendError(`${what} failed with HTTP ${res.status}`, !(res.status === 429 || res.status >= 500));
  }
}
