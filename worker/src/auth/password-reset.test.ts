import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import {
  CHANGED_SUBJECT, NOTICE_MAX_ATTEMPTS, changedMailText, MailError, MockPasswordMailer, newResetToken, loadPublicAppUrl, pollPasswordResets, retryDelayMs,
  type ClaimedRow, type OutgoingMail, type PasswordMailer, type PgPasswordResetStore,
} from "./password-reset";

const NOW = new Date("2026-11-18T10:00:00Z");
const EMAIL = "anna.secret@salon.example";

function fakeStore(rows: ClaimedRow[], attach = true) {
  const calls = { attached: [] as Array<{ id: string; hash: Buffer }>, marks: [] as unknown[][] };
  const store = {
    claim: async () => rows,
    attachToken: async (id: string, hash: Buffer) => { calls.attached.push({ id, hash }); return attach; },
    mark: async (...a: unknown[]) => { calls.marks.push(a); },
  } as unknown as PgPasswordResetStore;
  return { store, calls };
}

function logger() {
  const lines: string[] = [];
  const l = { info: (m: string, f?: object) => lines.push(m + JSON.stringify(f ?? {})), error: (m: string, f?: object) => lines.push(m + JSON.stringify(f ?? {})) };
  return { l, lines };
}

const row = (kind: ClaimedRow["kind"] = "reset_link"): ClaimedRow => ({ resetId: "11111111-1111-1111-1111-111111111111", email: EMAIL, kind });

describe("pollPasswordResets", () => {
  it("builds the link only from the configured URL, stores only sha256 and keeps token/email out of logs and marks", async () => {
    const { store, calls } = fakeStore([row()]);
    const sent: OutgoingMail[] = [];
    const { l, lines } = logger();
    const mailer: PasswordMailer = { send: async (m) => { sent.push(m); } };
    const out = await pollPasswordResets({ store, mailer, publicAppUrl: "https://app.example.test", logger: l, now: () => NOW, attempts: new Map() });
    expect(out).toEqual({ sent: 1, retried: 0, failed: 0 });
    const m = sent[0]!;
    const token = /#t=([A-Za-z0-9_-]{43})\b/.exec(m.text)?.[1];
    expect(token).toBeDefined();
    expect(m.text).toContain("https://app.example.test/reset-password#t=" + token);
    expect(m.text).toContain("30 хв");
    expect(m.text).toContain("Якщо ви не запитували скидання, проігноруйте лист.");
    expect(m.to).toBe(EMAIL);
    expect(calls.attached[0]!.hash.equals(createHash("sha256").update(Buffer.from(token!, "base64url")).digest())).toBe(true);
    expect(calls.marks).toEqual([[row().resetId, "reset_link", "sent", null, null]]);
    const dump = lines.join("\n") + JSON.stringify(calls.marks);
    expect(dump).not.toContain(token!);
    expect(dump).not.toContain(EMAIL);
    expect(dump).not.toContain("salon.example");
  });

  it("does not send when attach_token says the request was superseded", async () => {
    const { store, calls } = fakeStore([row()], false);
    const sent: OutgoingMail[] = [];
    const out = await pollPasswordResets({ store, mailer: { send: async (m) => { sent.push(m); } }, publicAppUrl: "https://a.test", logger: logger().l, attempts: new Map() });
    expect(sent).toEqual([]);
    expect(calls.marks).toEqual([]);
    expect(out).toEqual({ sent: 0, retried: 0, failed: 0 });
  });

  it("retries with backoff and a NEW token on each attempt; permanent SMTP rejection fails the row", async () => {
    const attempts = new Map<string, number>();
    const tokens: string[] = [];
    const mailer: PasswordMailer = { send: async (m) => { tokens.push(/#t=(\S{43})/.exec(m.text)![1]!); throw new MailError("smtp_unavailable", false); } };
    const a = fakeStore([row()]);
    const ctx = { mailer, publicAppUrl: "https://a.test", logger: logger().l, now: () => NOW, attempts };
    expect(await pollPasswordResets({ ...ctx, store: a.store })).toMatchObject({ retried: 1 });
    expect(a.calls.marks[0]).toEqual([row().resetId, "reset_link", "pending", "smtp_unavailable", new Date(NOW.getTime() + retryDelayMs(1))]);
    const b = fakeStore([row()]);
    await pollPasswordResets({ ...ctx, store: b.store });
    expect(b.calls.marks[0]![4]).toEqual(new Date(NOW.getTime() + retryDelayMs(2)));
    expect(tokens[0]).not.toBe(tokens[1]);
    expect(b.calls.attached[0]!.hash.equals(a.calls.attached[0]!.hash)).toBe(false);

    const c = fakeStore([row()]);
    const rejecting: PasswordMailer = { send: async () => { throw new MailError("smtp_rejected", true); } };
    expect(await pollPasswordResets({ ...ctx, mailer: rejecting, store: c.store })).toMatchObject({ failed: 1 });
    expect(c.calls.marks[0]).toEqual([row().resetId, "reset_link", "failed", "smtp_rejected", null]);
  });

  it("sends the changed_notice mail: no token, no business name, link to /forgot-password, no attach_token, nothing sensitive in logs", async () => {
    const { store, calls } = fakeStore([row("changed_notice")]);
    const sent: OutgoingMail[] = [];
    const { l, lines } = logger();
    const out = await pollPasswordResets({ store, mailer: { send: async (m) => { sent.push(m); } }, publicAppUrl: "https://app.example.test", logger: l, attempts: new Map() });
    expect(out).toEqual({ sent: 1, retried: 0, failed: 0 });
    expect(calls.attached).toEqual([]);
    expect(sent).toHaveLength(1);
    expect(sent[0]!.to).toBe(EMAIL);
    expect(sent[0]!.subject).toBe(CHANGED_SUBJECT);
    expect(sent[0]!.text).toContain("https://app.example.test/forgot-password");
    expect(sent[0]!.text).not.toContain("#t=");
    expect(sent[0]!.text).not.toContain("reset-password");
    expect(calls.marks).toEqual([[row().resetId, "changed_notice", "sent", null, null]]);
    const dump = lines.join("\n") + JSON.stringify(calls.marks);
    expect(dump).not.toContain(EMAIL);
    expect(dump).not.toContain("forgot-password");
  });

  it("changed_notice: transient failure retries with backoff, permanent fails, budget exhaustion fails with retries_exhausted", async () => {
    const attempts = new Map<string, number>();
    const base = { publicAppUrl: "https://a.test", logger: logger().l, now: () => NOW, attempts };
    const flaky: PasswordMailer = { send: async () => { throw new MailError("smtp_unavailable", false); } };
    const a = fakeStore([row("changed_notice")]);
    expect(await pollPasswordResets({ ...base, mailer: flaky, store: a.store })).toMatchObject({ retried: 1 });
    expect(a.calls.marks[0]).toEqual([row().resetId, "changed_notice", "pending", "smtp_unavailable", new Date(NOW.getTime() + retryDelayMs(1))]);
    expect(a.calls.attached).toEqual([]);

    const b = fakeStore([row("changed_notice")]);
    const rejecting: PasswordMailer = { send: async () => { throw new MailError("smtp_rejected", true); } };
    expect(await pollPasswordResets({ ...base, mailer: rejecting, store: b.store })).toMatchObject({ failed: 1 });
    expect(b.calls.marks[0]).toEqual([row().resetId, "changed_notice", "failed", "smtp_rejected", null]);

    attempts.set("n:" + row().resetId, NOTICE_MAX_ATTEMPTS - 1);
    const c = fakeStore([row("changed_notice")]);
    expect(await pollPasswordResets({ ...base, mailer: flaky, store: c.store })).toMatchObject({ failed: 1 });
    expect(c.calls.marks[0]).toEqual([row().resetId, "changed_notice", "failed", "retries_exhausted", null]);
  });

  it("attempt counters of reset_link and changed_notice for one reset id do not mix", async () => {
    const attempts = new Map<string, number>();
    const flaky: PasswordMailer = { send: async () => { throw new Error("x"); } };
    const ctx = { mailer: flaky, publicAppUrl: "https://a.test", logger: logger().l, now: () => NOW, attempts };
    await pollPasswordResets({ ...ctx, store: fakeStore([row()]).store });
    const n = fakeStore([row("changed_notice")]);
    await pollPasswordResets({ ...ctx, store: n.store });
    expect(n.calls.marks[0]![4]).toEqual(new Date(NOW.getTime() + retryDelayMs(1)));
  });

  it("changed notice text is plain: no business name placeholders", () => {
    const t = changedMailText("https://a.test/forgot-password");
    expect(t).toContain("Пароль вашого акаунта Beauty CRM змінено");
    expect(t).toContain("Якщо це були не ви");
  });

  it("claims nothing when PUBLIC_APP_URL is not configured", async () => {
    let claimed = false;
    const store = { claim: async () => { claimed = true; return []; } } as unknown as PgPasswordResetStore;
    await pollPasswordResets({ store, mailer: { send: async () => {} }, publicAppUrl: null, logger: logger().l });
    expect(claimed).toBe(false);
  });
});

describe("helpers", () => {
  it("generates 43-char base64url tokens with sha256 of the raw 32 bytes", () => {
    const { token, hash } = newResetToken();
    expect(token).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(hash.equals(createHash("sha256").update(Buffer.from(token, "base64url")).digest())).toBe(true);
    expect(newResetToken().token).not.toBe(token);
  });

  it("loadPublicAppUrl accepts https (and http only in Development/loopback), rejects query, fragment and junk", () => {
    expect(loadPublicAppUrl({ PUBLIC_APP_URL: "https://app.example.test/" })).toBe("https://app.example.test");
    expect(loadPublicAppUrl({ PUBLIC_APP_URL: "http://app.example.test" })).toBeNull();
    expect(loadPublicAppUrl({ PUBLIC_APP_URL: "http://localhost:3000" })).toBe("http://localhost:3000");
    expect(loadPublicAppUrl({ PUBLIC_APP_URL: "http://x.test", ASPNETCORE_ENVIRONMENT: "Development" })).toBe("http://x.test");
    expect(loadPublicAppUrl({ PUBLIC_APP_URL: "https://a.test/?x=1" })).toBeNull();
    expect(loadPublicAppUrl({ PUBLIC_APP_URL: "https://a.test/#f" })).toBeNull();
    expect(loadPublicAppUrl({ PUBLIC_APP_URL: "https://u:p@a.test" })).toBeNull();
    expect(loadPublicAppUrl({})).toBeNull();
  });

  it("mock mailer keeps the mail readable for tests but logs no body", async () => {
    const { l, lines } = logger();
    const m = new MockPasswordMailer(l);
    await m.send({ to: EMAIL, subject: "s", text: "secret-body" });
    expect(m.outbox).toHaveLength(1);
    expect(lines.join("")).not.toContain("secret-body");
    expect(lines.join("")).not.toContain(EMAIL);
  });

  it("retry delay doubles and is capped", () => {
    expect(retryDelayMs(1)).toBe(60_000);
    expect(retryDelayMs(3)).toBe(240_000);
    expect(retryDelayMs(20)).toBe(600_000);
  });
});
