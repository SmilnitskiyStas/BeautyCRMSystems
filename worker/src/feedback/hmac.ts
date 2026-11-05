import { createHash, createHmac, randomBytes } from "node:crypto";

/** ControlPlaneHmac v1 (ADR-018 §3.1, beauty-contracts §27.2); same format in both directions. */
export const HMAC_PREFIX = "BCRM-HMAC-SHA256-v1";

export interface SignInput {
  kid: string;
  /** base64 text of the secret; the HMAC key is the decoded raw bytes. */
  secretBase64: string;
  method: string;
  path: string;
  /** v1: always "" (a non-empty query is rejected by the receiver). */
  query?: string;
  /** Exactly the bytes that will be sent. */
  body: Uint8Array;
  timestamp: number;
  nonce: string;
}

export const MIN_KEY_BYTES = 32;

export function decodeKey(secretBase64: string): Buffer {
  return Buffer.from(secretBase64, "base64");
}

export function canonicalString(i: Omit<SignInput, "secretBase64" | "body"> & { bodySha256Hex: string }): string {
  return [HMAC_PREFIX, i.kid, String(i.timestamp), i.nonce, i.method.toUpperCase(), i.path, i.query ?? "", i.bodySha256Hex].join("\n");
}

export function sign(i: SignInput): string {
  const bodySha256Hex = createHash("sha256").update(i.body).digest("hex");
  const mac = createHmac("sha256", decodeKey(i.secretBase64)).update(canonicalString({ ...i, bodySha256Hex }), "utf8").digest();
  return `v1=${mac.toString("base64url")}`;
}

export function newNonce(): string {
  return randomBytes(16).toString("base64url"); // 22 chars, 128 bits
}

export function signedHeaders(i: SignInput): Record<string, string> {
  return {
    "X-BCRM-Key-Id": i.kid,
    "X-BCRM-Timestamp": String(i.timestamp),
    "X-BCRM-Nonce": i.nonce,
    "X-BCRM-Signature": sign(i),
  };
}
