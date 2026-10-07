import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";

// Same format as backend AesGcmSecretProtector: base64(nonce[12] | tag[16] | ciphertext), AES-256-GCM,
// key = base64 of 32 bytes (Channels__EncryptionKey). Payload = JSON {Token, WebhookSecret} (.NET default casing).
const NONCE = 12;
const TAG = 16;

export interface ChannelCredentials { token?: string; webhookSecret?: string }

export function loadKey(b64: string | undefined): Buffer {
  if (!b64?.trim()) throw new Error("Channels__EncryptionKey is not configured (base64 of 32 bytes)");
  const key = Buffer.from(b64, "base64");
  if (key.length !== 32) throw new Error("Channels__EncryptionKey must be 32 bytes (base64)");
  return key;
}

export function unprotect(key: Buffer, protectedValue: string): string {
  const all = Buffer.from(protectedValue, "base64");
  const d = createDecipheriv("aes-256-gcm", key, all.subarray(0, NONCE), { authTagLength: TAG });
  d.setAuthTag(all.subarray(NONCE, NONCE + TAG));
  return Buffer.concat([d.update(all.subarray(NONCE + TAG)), d.final()]).toString("utf8");
}

export function protect(key: Buffer, plaintext: string): string {
  const nonce = randomBytes(NONCE);
  const c = createCipheriv("aes-256-gcm", key, nonce, { authTagLength: TAG });
  const cipher = Buffer.concat([c.update(plaintext, "utf8"), c.final()]);
  return Buffer.concat([nonce, c.getAuthTag(), cipher]).toString("base64");
}

export function readCredentials(key: Buffer, encrypted: string | null | undefined): ChannelCredentials {
  if (!encrypted) return {};
  const json = JSON.parse(unprotect(key, encrypted)) as Record<string, string | null>;
  const pick = (a: string, b: string) => json[a] ?? json[b] ?? undefined;
  return { token: pick("Token", "token"), webhookSecret: pick("WebhookSecret", "webhookSecret") };
}

export function writeCredentials(key: Buffer, c: ChannelCredentials): string {
  return protect(key, JSON.stringify({ Token: c.token ?? null, WebhookSecret: c.webhookSecret ?? null }));
}
