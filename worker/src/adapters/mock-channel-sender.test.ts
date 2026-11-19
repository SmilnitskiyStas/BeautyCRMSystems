import { describe, expect, it } from "vitest";
import { MockChannelSender, useMockChannels } from "./mock-channel-sender";
import type { StoredMessage } from "../ports";

const msg = { id: "m1", tenantId: "t1", channel: "telegram", address: "555", text: "SECRET reply text", idempotencyKey: "k", kind: "message", status: "queued" } as unknown as StoredMessage;

describe("MockChannelSender / useMockChannels", () => {
  it("send_makes-no-network-call_and-does-not-log-text-or-address", async () => {
    const lines: string[] = [];
    const logger = { info: (m: string, f?: object) => lines.push(JSON.stringify({ m, ...f })), error: () => {} };
    const realFetch = globalThis.fetch;
    globalThis.fetch = (() => { throw new Error("network must not be used"); }) as typeof fetch;
    try { await new MockChannelSender(logger).send(msg); } finally { globalThis.fetch = realFetch; }
    expect(lines).toHaveLength(1);
    expect(lines[0]).not.toContain("SECRET");
    expect(lines[0]).not.toContain("555");
  });

  it("useMockChannels_is-false-by-default_true-in-development_throws-elsewhere", () => {
    expect(useMockChannels({})).toBe(false);
    expect(useMockChannels({ Channels__UseMocks: "false", ASPNETCORE_ENVIRONMENT: "Production" })).toBe(false);
    expect(useMockChannels({ Channels__UseMocks: "true", ASPNETCORE_ENVIRONMENT: "Development" })).toBe(true);
    expect(() => useMockChannels({ Channels__UseMocks: "true", ASPNETCORE_ENVIRONMENT: "Production" })).toThrow();
    expect(() => useMockChannels({ Channels__UseMocks: "true" })).toThrow();
  });
});
