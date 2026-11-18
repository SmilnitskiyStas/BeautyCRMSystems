import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { MockPasswordMailer, PgPasswordResetStore, pollPasswordResets } from "../auth/password-reset";

// TASK-865: worker delivery against the real functions. The worker role has EXECUTE on the four worker functions and NO table access
// (contract 31.16): everything the worker needs goes through claim / attach_token / mark / purge_expired.
const ROLE = "beauty_worker_reset";
let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let worker: pg.Pool;
const logs: string[] = [];
const logger = { info: (m: string, f?: object) => logs.push(m + JSON.stringify(f ?? {})), error: (m: string, f?: object) => logs.push(m + JSON.stringify(f ?? {})) };
const key = Buffer.alloc(32, 7);

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${ROLE} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS; GRANT USAGE ON SCHEMA public TO ${ROLE}`);
  for (const f of ["auth_password_reset_claim_delivery(timestamptz,integer,integer)", "auth_password_reset_attach_token(uuid,bytea,timestamptz)",
    "auth_password_reset_mark_delivery(uuid,text,text,text,timestamptz)", "auth_identity_purge_expired(timestamptz)"])
    await admin.query(`GRANT EXECUTE ON FUNCTION public.${f} TO ${ROLE}`);
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = "pw";
  worker = new pg.Pool({ connectionString: u.toString(), max: 4 });
}, 180_000);

afterAll(async () => { await worker?.end(); await admin?.end(); await container?.stop(); });

const sha = (token: string) => createHash("sha256").update(Buffer.from(token, "base64url")).digest();
const tokenOf = (text: string) => /#t=([A-Za-z0-9_-]{43})/.exec(text)![1]!;

async function identity(email: string): Promise<string> {
  const { rows } = await admin.query("INSERT INTO identities (email, password_hash, state) VALUES ($1, 'h', 'active') RETURNING id", [email]);
  return rows[0].id;
}
// The row is due at DB now() (microseconds) while the worker passes JS time (milliseconds): a fast poll can land before it. Back-date it so the test is deterministic.
const issue = async (email: string) => {
  await admin.query("SELECT auth_password_reset_issue($1, $2, now() + interval '30 minutes')", [email, key]);
  await admin.query("UPDATE auth_password_resets SET next_attempt_at = now() - interval '5 seconds' WHERE identity_id = (SELECT id FROM identities WHERE email = $1) AND delivery_status = 'pending'", [email]);
};
const ctx = (mailer: MockPasswordMailer) => ({ store: new PgPasswordResetStore(worker), mailer, publicAppUrl: "https://app.example.test", logger, attempts: new Map<string, number>() });

describe("password reset delivery (real DB)", () => {
  it("claim -> attach -> send -> mark: only sha256 in the DB, link works once with a token nobody stored", async () => {
    const email = "full-cycle@salon.test";
    const id = await identity(email);
    await issue(email);
    const mailer = new MockPasswordMailer(logger);
    expect(await pollPasswordResets(ctx(mailer))).toMatchObject({ sent: 1 });
    expect(mailer.outbox).toHaveLength(1);
    const token = tokenOf(mailer.outbox[0]!.text);
    const { rows } = await admin.query("SELECT * FROM auth_password_resets WHERE identity_id = $1", [id]);
    expect(rows).toHaveLength(1);
    expect(rows[0].delivery_status).toBe("sent");
    expect(rows[0].token_hash.equals(sha(token))).toBe(true);
    expect(JSON.stringify(rows[0])).not.toContain(token); // no raw token in any column
    expect(logs.join("\n")).not.toContain(token);
    expect(logs.join("\n")).not.toContain(email);
    // the token from the mail is consumable exactly once
    const c1 = await admin.query("SELECT auth_password_reset_consume($1, 'new-hash', now()) AS id", [sha(token)]);
    const c2 = await admin.query("SELECT auth_password_reset_consume($1, 'new-hash', now()) AS id", [sha(token)]);
    expect(c1.rows[0].id).toBe(id);
    expect(c2.rows[0].id).toBeNull();
    // only the "password changed" notice is left for this row (no reset_link any more); after it nothing is left
    const notice = new MockPasswordMailer(logger);
    expect(await pollPasswordResets(ctx(notice))).toMatchObject({ sent: 1 });
    expect(notice.outbox[0]!.text).not.toContain("#t=");
    expect(await pollPasswordResets(ctx(new MockPasswordMailer(logger)))).toMatchObject({ sent: 0 });
  }, 60_000);

  it("a retry generates a NEW token: the link of the failed attempt is dead", async () => {
    const email = "retry@salon.test";
    await identity(email);
    await issue(email);
    const seen: string[] = [];
    const failing = { send: async (m: { text: string }) => { seen.push(tokenOf(m.text)); throw new Error("boom"); } };
    expect(await pollPasswordResets({ ...ctx(new MockPasswordMailer(logger)), mailer: failing })).toMatchObject({ retried: 1 });
    const first = await admin.query("SELECT token_hash, last_error, next_attempt_at FROM auth_password_resets r JOIN identities i ON i.id = r.identity_id WHERE i.email = $1", [email]);
    expect(first.rows[0].last_error).toBe("send_failed");
    // make the retry due, then deliver successfully
    await admin.query("UPDATE auth_password_resets SET next_attempt_at = now() WHERE identity_id = (SELECT id FROM identities WHERE email = $1)", [email]);
    const mailer = new MockPasswordMailer(logger);
    expect(await pollPasswordResets(ctx(mailer))).toMatchObject({ sent: 1 });
    const second = tokenOf(mailer.outbox[0]!.text);
    expect(second).not.toBe(seen[0]);
    const dead = await admin.query("SELECT auth_password_reset_consume($1, 'x', now()) AS id", [sha(seen[0]!)]);
    expect(dead.rows[0].id).toBeNull();
    const live = await admin.query("SELECT auth_password_reset_consume($1, 'x', now()) AS id", [sha(second)]);
    expect(live.rows[0].id).not.toBeNull();
    await pollPasswordResets(ctx(new MockPasswordMailer(logger))); // drain the "password changed" notice queued by the consume
  }, 60_000);

  it("two workers in parallel send exactly one mail (SKIP LOCKED + lease)", async () => {
    const email = "parallel@salon.test";
    await identity(email);
    await issue(email);
    const a = new MockPasswordMailer(logger), b = new MockPasswordMailer(logger);
    await Promise.all([pollPasswordResets(ctx(a)), pollPasswordResets(ctx(b))]);
    expect(a.outbox.length + b.outbox.length).toBe(1);
  }, 60_000);

  it("a newer issue between claim and attach: attach is false and no mail is sent", async () => {
    const email = "superseded@salon.test";
    await identity(email);
    await issue(email);
    const store = new PgPasswordResetStore(worker);
    const [claimed] = (await store.claim(new Date(), 300, 50)).filter((r) => r.email === email);
    expect(claimed).toBeDefined();
    await issue(email); // supersedes the claimed row
    expect(await store.attachToken(claimed!.resetId, Buffer.alloc(32, 1), new Date())).toBe(false);
    const mailer = new MockPasswordMailer(logger);
    await pollPasswordResets(ctx(mailer));
    expect(mailer.outbox).toHaveLength(1); // only the new request is delivered
    const { rows } = await admin.query("SELECT count(*)::int AS n FROM auth_password_resets r JOIN identities i ON i.id = r.identity_id WHERE i.email = $1 AND r.superseded_at IS NULL", [email]);
    expect(rows[0].n).toBe(1);
  }, 60_000);

  it("unknown and pending emails create nothing", async () => {
    await issue("nobody@salon.test");
    await admin.query("INSERT INTO identities (email, state) VALUES ('pending@salon.test', 'pending')");
    await issue("pending@salon.test");
    const { rows } = await admin.query("SELECT count(*)::int AS n FROM auth_password_resets r JOIN identities i ON i.id = r.identity_id WHERE i.email = 'pending@salon.test'");
    expect(rows[0].n).toBe(0);
  }, 60_000);

  it("changed_notice: sent exactly once after a successful reset (no token, no business name), purge is blocked only while pending", async () => {
    const email = "notice@salon.test";
    const id = await identity(email);
    await issue(email);
    const mailer = new MockPasswordMailer(logger);
    await pollPasswordResets(ctx(mailer));
    const token = tokenOf(mailer.outbox[0]!.text);
    await admin.query("SELECT auth_password_reset_consume($1, 'n', now())", [sha(token)]);

    // pending notice keeps the row through purge
    await admin.query("UPDATE auth_password_resets SET expires_at = now() - interval '1 hour' WHERE identity_id = $1", [id]);
    await new PgPasswordResetStore(worker).purgeExpired(new Date());
    expect((await admin.query("SELECT count(*)::int AS n FROM auth_password_resets WHERE identity_id = $1", [id])).rows[0].n).toBe(1);

    const n = new MockPasswordMailer(logger);
    expect(await pollPasswordResets(ctx(n))).toMatchObject({ sent: 1, failed: 0 });
    expect(n.outbox).toHaveLength(1);
    expect(n.outbox[0]!.to).toBe(email);
    expect(n.outbox[0]!.text).toContain("https://app.example.test/forgot-password");
    expect(n.outbox[0]!.text).not.toContain(token);
    expect(n.outbox[0]!.text).not.toContain("#t=");
    const row = (await admin.query("SELECT notice_status, notice_attempts, last_error FROM auth_password_resets WHERE identity_id = $1", [id])).rows[0];
    expect(row).toMatchObject({ notice_status: "sent", notice_attempts: 1, last_error: null });
    expect(logs.join("\n")).not.toContain(email);

    // second pass: nothing more to send
    const again = new MockPasswordMailer(logger);
    expect(await pollPasswordResets(ctx(again))).toMatchObject({ sent: 0 });
    expect(again.outbox).toEqual([]);

    // sent notice no longer blocks purge
    expect(await new PgPasswordResetStore(worker).purgeExpired(new Date())).toBeGreaterThanOrEqual(1);
    expect((await admin.query("SELECT count(*)::int AS n FROM auth_password_resets WHERE identity_id = $1", [id])).rows[0].n).toBe(0);
  }, 60_000);

  it("changed_notice: a transient failure retries later with the same pending row; two parallel workers send one mail", async () => {
    const email = "notice-retry@salon.test";
    const id = await identity(email);
    await issue(email);
    const m0 = new MockPasswordMailer(logger);
    await pollPasswordResets(ctx(m0));
    await admin.query("SELECT auth_password_reset_consume($1, 'n', now())", [sha(tokenOf(m0.outbox[0]!.text))]);

    const failing = { send: async () => { throw new Error("boom"); } };
    expect(await pollPasswordResets({ ...ctx(new MockPasswordMailer(logger)), mailer: failing })).toMatchObject({ retried: 1 });
    expect((await admin.query("SELECT notice_status, notice_attempts, last_error FROM auth_password_resets WHERE identity_id = $1", [id])).rows[0])
      .toMatchObject({ notice_status: "pending", notice_attempts: 1, last_error: "send_failed" });

    await admin.query("UPDATE auth_password_resets SET notice_next_attempt_at = now() WHERE identity_id = $1", [id]);
    const a = new MockPasswordMailer(logger), b = new MockPasswordMailer(logger);
    await Promise.all([pollPasswordResets(ctx(a)), pollPasswordResets(ctx(b))]);
    expect(a.outbox.length + b.outbox.length).toBe(1);
    expect((await admin.query("SELECT notice_status FROM auth_password_resets WHERE identity_id = $1", [id])).rows[0].notice_status).toBe("sent");
  }, 60_000);

  it("the worker role cannot read the table directly", async () => {
    await expect(worker.query("SELECT token_hash FROM auth_password_resets")).rejects.toThrow(/permission denied/);
  });
});
