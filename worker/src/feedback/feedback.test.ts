import { readFileSync } from "node:fs";
import { describe, expect, it, vi } from "vitest";
import { MAX_ATTEMPTS, backoffMs, buildBody, deliver, loadFeedbackConfig, resolve, type FeedbackConfig, type FeedbackRow } from "./delivery";
import type { FeedbackStore } from "./feedback-store";
import { sign } from "./hmac";
import { pollFeedback } from "./poller";

const vector = JSON.parse(readFileSync(new URL("../../../.ai/artifacts/contracts/ADR-018-hmac-test-vector.json", import.meta.url), "utf8"));
const TENANT = "11111111-1111-4111-8111-111111111111";
const NOW = new Date(1793700000 * 1000);
const SECRET = "SECRET-MESSAGE-TEXT-CONTACT";
const cfg: FeedbackConfig = { baseUrl: new URL("https://sg.example.test"), kid: vector.key.kid, secretBase64: vector.key.secretBase64 };
const row: FeedbackRow = {
  id: "22222222-2222-4222-8222-222222222222", type: "bug", severity: "high", message: SECRET, pageUrl: "/beauty/calendar", attempts: 1,
  tenantName: "Salon X", tenantRef: "sg-123", userEmail: "owner@x.test", userRole: "admin",
};

describe("HMAC v1 against the ADR-018 vector", () => {
  for (const v of vector.vectors) {
    it(`signs ${v.name}`, () => {
      const input = { kid: vector.key.kid, secretBase64: vector.key.secretBase64, method: v.method, path: v.path, query: v.query,
        body: Buffer.from(v.bodyBase64, "base64"), timestamp: v.timestamp, nonce: v.nonce };
      expect(sign(input)).toBe(v.expectedSignature);
      expect(sign({ ...input, body: Buffer.from(v.bodyUtf8 + " ") })).toBe(v.negative.bodyWithTrailingSpaceSignature);
      expect(sign({ ...input, nonce: v.nonce + "x" })).not.toBe(v.expectedSignature);
    });
  }
});

describe("deliver", () => {
  const respond = (status: number, body?: unknown, headers?: Record<string, string>) =>
    vi.fn(async () => new Response(body === undefined ? null : JSON.stringify(body), { status, headers }));
  const run = (f: typeof fetch) => deliver(cfg, row, { fetch: f, now: () => NOW, nonce: () => "TEST_nonce_0000000000000001" });

  it("sends a signed flat body to the contract path and treats 201 as delivered", async () => {
    const f = respond(201, { leadId: "lead-1" });
    expect(await run(f as unknown as typeof fetch)).toEqual({ kind: "delivered", leadId: "lead-1" });
    const [url, init] = f.mock.calls[0] as unknown as [string, RequestInit & { headers: Record<string, string> }];
    expect(url).toBe("https://sg.example.test/api/integrations/beauty/feedback");
    const sent = init.body as Uint8Array;
    expect(JSON.parse(Buffer.from(sent).toString())).toEqual({
      sourceTenantRef: "sg-123", tenantName: "Salon X", type: "bug", message: SECRET, contact: "owner@x.test", userRole: "admin",
      feedbackId: row.id, severity: "high", pageUrl: "/beauty/calendar",
    });
    expect(init.headers["X-BCRM-Signature"]).toBe(sign({
      kid: cfg.kid, secretBase64: cfg.secretBase64, method: "POST", path: "/api/integrations/beauty/feedback", body: sent,
      timestamp: 1793700000, nonce: "TEST_nonce_0000000000000001",
    }));
    expect(init.headers["X-BCRM-Key-Id"]).toBe("test-k1");
  });

  it("omits optional fields that are absent", () => {
    const b = JSON.parse(Buffer.from(buildBody({ ...row, pageUrl: null, userEmail: null, userRole: null })).toString());
    expect(b).not.toHaveProperty("pageUrl");
    expect(b).not.toHaveProperty("contact");
    expect(b).not.toHaveProperty("userRole");
  });

  it("200 (replayed) is delivered even without a body", async () => {
    expect(await run(respond(200) as unknown as typeof fetch)).toEqual({ kind: "delivered", leadId: null });
  });

  it.each([401, 422])("%i is a permanent failure", async (s) => {
    expect(await run(respond(s) as unknown as typeof fetch)).toEqual({ kind: "permanent", code: `http_${s}` });
  });

  it.each([429, 500, 503])("%i is retried", async (s) => {
    expect((await run(respond(s) as unknown as typeof fetch)).kind).toBe("retry");
  });

  it("honours Retry-After on 429", async () => {
    expect(await run(respond(429, undefined, { "retry-after": "120" }) as unknown as typeof fetch)).toEqual({ kind: "retry", code: "http_429", retryAfterMs: 120_000 });
  });

  it("network errors are retried and never leak details", async () => {
    const out = await run((async () => { throw new Error(`boom ${SECRET}`); }) as unknown as typeof fetch);
    expect(out).toEqual({ kind: "retry", code: "network" });
  });
});

describe("backoff and resolution", () => {
  it("grows exponentially with jitter and is capped", () => {
    expect(backoffMs(1, () => 0.5)).toBe(60_000);
    expect(backoffMs(2, () => 0.5)).toBe(120_000);
    expect(backoffMs(3, () => 0)).toBe(192_000);
    expect(backoffMs(3, () => 0.999)).toBeGreaterThan(280_000);
    expect(backoffMs(30, () => 0.5)).toBe(4 * 3_600_000);
  });

  it("retry schedules next attempt, limit turns it into failed, permanent fails at once", () => {
    const r = resolve({ kind: "retry", code: "http_503" }, 2, NOW, () => 0.5);
    expect(r).toEqual({ status: "pending", nextAttemptAt: new Date(NOW.getTime() + 120_000), error: "http_503" });
    expect(resolve({ kind: "retry", code: "http_503" }, MAX_ATTEMPTS, NOW)).toEqual({ status: "failed", error: "max_attempts:http_503" });
    expect(resolve({ kind: "permanent", code: "http_401" }, 1, NOW)).toEqual({ status: "failed", error: "http_401" });
    const ra = resolve({ kind: "retry", code: "http_429", retryAfterMs: 3_600_000 }, 1, NOW, () => 0.5);
    expect(ra.status === "pending" && ra.nextAttemptAt.getTime()).toBe(NOW.getTime() + 3_600_000);
  });
});

describe("config", () => {
  const good = { SHELFGUARD_FEEDBACK_ENABLED: "true", SHELFGUARD_BASE_URL: "https://sg.example.test", SHELFGUARD_FEEDBACK_KEY_ID: "beauty-k1", SHELFGUARD_FEEDBACK_SECRET: vector.key.secretBase64 };
  it("is disabled by default and unless explicitly true", () => {
    expect(loadFeedbackConfig({})).toEqual({ enabled: false, reason: "disabled" });
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_FEEDBACK_ENABLED: "false" })).toEqual({ enabled: false, reason: "disabled" });
  });
  it("fails closed on a missing or short secret / bad url / bad kid", () => {
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_FEEDBACK_SECRET: "" })).toEqual({ enabled: false, reason: "misconfigured" });
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_FEEDBACK_SECRET: "c2hvcnQ=" })).toEqual({ enabled: false, reason: "misconfigured" });
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: "" })).toEqual({ enabled: false, reason: "misconfigured" });
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_FEEDBACK_KEY_ID: "X" })).toEqual({ enabled: false, reason: "misconfigured" });
  });
  it("accepts a valid config", () => {
    expect(loadFeedbackConfig(good).enabled).toBe(true);
  });
  it("L2: outside Development only https; http only on loopback", () => {
    const mis = { enabled: false, reason: "misconfigured" };
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: "http://sg.example.test" })).toEqual(mis);
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: "http://10.0.0.5:8080" })).toEqual(mis);
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: "ftp://sg.example.test" })).toEqual(mis);
    // Development claim with NODE_ENV=production does not count as Development
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: "http://sg.example.test", ASPNETCORE_ENVIRONMENT: "Development", NODE_ENV: "production" })).toEqual(mis);
    for (const u of ["http://localhost:5001", "http://127.0.0.1:5001", "http://[::1]:5001"])
      expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: u }).enabled).toBe(true);
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: "https://sg.example.test" }).enabled).toBe(true);
  });
  it("L2: Development allows any http", () => {
    expect(loadFeedbackConfig({ ...good, SHELFGUARD_BASE_URL: "http://sg.dev.test", ASPNETCORE_ENVIRONMENT: "Development" }).enabled).toBe(true);
  });
});

describe("pollFeedback", () => {
  function fakeStore(rows: FeedbackRow[]) {
    const recorded: Array<[string, unknown]> = [];
    const store: FeedbackStore = {
      claimDue: async () => rows,
      record: async (id, r) => { recorded.push([id, r]); },
    };
    return { store, recorded };
  }
  const logs: string[] = [];
  const logger = { info: (m: string, f?: object) => logs.push(m + JSON.stringify(f)), error: (m: string, f?: object) => logs.push(m + JSON.stringify(f)) };
  const ctx = (store: FeedbackStore, f: typeof fetch) => ({
    store, tenants: { listTenantIds: async () => [TENANT] }, logger, config: cfg, fetch: f, now: () => NOW, rand: () => 0.5,
  });

  it("records delivered, retry and failed without logging message/contact", async () => {
    for (const [status, expected] of [[201, "delivered"], [503, "pending"], [422, "failed"]] as const) {
      const { store, recorded } = fakeStore([row]);
      await pollFeedback(ctx(store, (async () => new Response(null, { status })) as unknown as typeof fetch));
      expect((recorded[0]![1] as { status: string }).status).toBe(expected);
    }
    const all = logs.join("\n");
    expect(all).not.toContain(SECRET);
    expect(all).not.toContain("owner@x.test");
    expect(all).not.toContain(vector.key.secretBase64);
  });

  it("does nothing when nothing is due", async () => {
    const { store, recorded } = fakeStore([]);
    const f = vi.fn();
    expect(await pollFeedback(ctx(store, f as unknown as typeof fetch))).toEqual({ processed: 0, errors: 0 });
    expect(f).not.toHaveBeenCalled();
    expect(recorded).toEqual([]);
  });
});
