import { describe, expect, it } from "vitest";
import type { ClientInfo } from "../ports";
import { makeDeps } from "../testing/fakes";
import { processOutbox } from "./beauty-outbox";

const client: ClientInfo = { id: "c1", tenantId: "t1", channel: "telegram", address: "555", marketingConsent: false };

async function queue(d: ReturnType<typeof makeDeps>, key = "ai-reply:x:1") {
  const { message } = await d.deps.data.upsertMessage({
    tenantId: "t1", clientId: client.id, channel: "telegram", address: client.address, text: "hi", idempotencyKey: key, kind: "message",
  });
  return message;
}

describe("processOutbox CAS claim (L3)", () => {
  it("claim-lost_row-is-skipped_sender-not-called_and-idempotency-claim-released", async () => {
    const d = makeDeps({ clients: [client] });
    const m = await queue(d);
    d.deps.data.claimForSend = async () => false; // the .NET sweeper already moved the row to `sending`
    expect(await processOutbox(d.deps, { messageId: m.id })).toBe("skipped");
    expect(d.sent).toHaveLength(0);
    expect(d.logs.at(-1)).toMatchObject({ status: "skipped" });
    expect(await d.deps.idempotency.claim(m.idempotencyKey)).toBe(true); // released, not held forever
  });

  it("two-parallel-runs_on-one-row_send-once_even-when-idempotency-always-grants", async () => {
    const d = makeDeps({ clients: [client] });
    const m = await queue(d);
    d.deps.idempotency.claim = async () => true;
    const outcomes = await Promise.all([processOutbox(d.deps, { messageId: m.id }), processOutbox(d.deps, { messageId: m.id })]);
    expect(outcomes.slice().sort()).toEqual(["sent", "skipped"]);
    expect(d.sent).toHaveLength(1);
  });

  it("failed-attempt_releases-the-claim_so-the-retry-can-send", async () => {
    const d = makeDeps({ clients: [client], sendFailures: 1 });
    const m = await queue(d);
    await expect(processOutbox(d.deps, { messageId: m.id }, 0)).rejects.toThrow("channel down");
    expect(await processOutbox(d.deps, { messageId: m.id }, 1)).toBe("sent");
    expect(d.sent).toHaveLength(1);
  });

  it("row-not-pending_failed-or-sent_is-never-claimed", async () => {
    const d = makeDeps({ clients: [client] });
    const m = await queue(d);
    d.messages.get(m.id)!.status = "failed";
    expect(await processOutbox(d.deps, { messageId: m.id })).toBe("skipped");
    expect(d.sent).toHaveLength(0);
  });
});
