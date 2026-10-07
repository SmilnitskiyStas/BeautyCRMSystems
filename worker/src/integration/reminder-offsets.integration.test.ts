import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { writeCredentials } from "../adapters/channel-crypto";
import { PgBeautyData } from "../adapters/pg-data";
import { PgPollStore } from "../adapters/pg-poll-store";
import { PgAdvisoryIdempotency, PgQueuePort, type Logger } from "../adapters/pg-support";
import { TenantDb } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import type { BeautyDeps, StoredMessage } from "../ports";
import { pollOutbox, pollReminders, type PollContext } from "../scheduler/pollers";

/**
 * TASK-681: доповнення до pipeline.integration.test.ts (там 2h-сценарій і закриття скасованих/none/минулих).
 * Тут: 1h-сценарій, закриті рядки beauty_reminders не відправляються повторно, перенесений запис
 * (старий рядок cancelled + новий scheduled) шле рівно одне нагадування. Рядки пишуться так само, як це робить .NET
 * (status = 'scheduled', reminder_option = '1h'|'2h' у beauty_appointments).
 */
const NOW = new Date("2026-03-10T10:00:00Z");
const KEY = randomBytes(32);
const silent: Logger = { info: () => undefined, error: () => undefined };
const ROLE = "beauty_worker_offsets";

let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;
let lockPool: pg.Pool;

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${ROLE} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ${ROLE}`);
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = "pw";
  db = TenantDb.create(u.toString(), 8);
  lockPool = new pg.Pool({ connectionString: u.toString(), max: 8 });
}, 180_000);

afterAll(async () => {
  await lockPool?.end(); await db?.close(); await admin?.end(); await container?.stop();
});

interface Seed { tenantId: string; apptId: string }

async function seed(option: "1h" | "2h" | "none", starts: Date): Promise<Seed> {
  const tenantId = randomUUID();
  const [loc, spec, svc, client, appt, channel, conv] = Array.from({ length: 7 }, () => randomUUID());
  const q = (sql: string, p: unknown[]) => admin.query(sql, p);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L','Europe/Kyiv',true)", [loc, tenantId]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,'Svc',60,true)", [svc, tenantId]);
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name) VALUES ($1,$2,'Anna')", [client, tenantId]);
  await q(`INSERT INTO beauty_appointments (id, tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
             status, source, price_original, price_final, reminder_option)
           VALUES ($1,$2,$3,$4,$5,$6,$7,60,'confirmed','online',100,100,$8)`, [appt, tenantId, loc, spec, svc, client, starts, option]);
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active) VALUES ($1,$2,'telegram','ch',$3,true)",
    [channel, tenantId, writeCredentials(KEY, { token: "T", webhookSecret: "s" })]);
  await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id) VALUES ($1,$2,$3,$4,'555')", [conv, tenantId, channel, client]);
  return { tenantId, apptId: appt };
}

const addReminder = async (s: Seed, at: Date, status = "scheduled") => {
  const id = randomUUID();
  await admin.query("INSERT INTO beauty_reminders (id, tenant_id, appointment_id, scheduled_at, status) VALUES ($1,$2,$3,$4,$5)", [id, s.tenantId, s.apptId, at, status]);
  return id;
};

function makeCtx(tenantIds: string[]) {
  const sent: StoredMessage[] = [];
  const deps: BeautyDeps = {
    now: () => NOW,
    data: new PgBeautyData(db, () => NOW),
    sender: { send: async (m) => { sent.push(m); } },
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async () => undefined },
    queue: new PgQueuePort(),
  };
  const ctx: PollContext = { deps, store: new PgPollStore(db), tenants: new StaticTenantDirectory(tenantIds), logger: silent, backoffMs: 0 };
  return { ctx, sent };
}

const hour = 3_600_000;

describe("reminder offsets 1h / 2h / none through DB poller", () => {
  it("1h_reminder_is-sent-exactly-once_with-1h-text_and-rerun-does-not-duplicate", async () => {
    const s = await seed("1h", new Date(NOW.getTime() + hour));
    await addReminder(s, NOW); // scheduled_at = start - 1h = now
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollReminders(ctx); await pollOutbox(ctx);
    await pollReminders(ctx); await pollOutbox(ctx);
    await Promise.all([pollReminders(ctx), pollOutbox(ctx)]);
    expect(sent).toHaveLength(1);
    expect(sent[0].text).toContain("1 годину");
    expect(sent[0]).toMatchObject({ kind: "reminder", address: "555" });
  });

  it("none_has-no-reminder-row_so-nothing-is-sent", async () => {
    const s = await seed("none", new Date(NOW.getTime() + 2 * hour)); // .NET не створює beauty_reminders для none
    const { ctx, sent } = makeCtx([s.tenantId]);
    expect(await pollReminders(ctx)).toEqual({ processed: 0, errors: 0 });
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
    expect((await admin.query("SELECT count(*)::int AS n FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows[0].n).toBe(0);
  });

  it("closed-reminder-rows_sent-failed-cancelled_are-never-picked-up-again", async () => {
    const seeds: Seed[] = [];
    for (const status of ["sent", "failed", "cancelled"]) {
      const s = await seed("2h", new Date(NOW.getTime() + 2 * hour));
      await addReminder(s, NOW, status);
      seeds.push(s);
    }
    const { ctx, sent } = makeCtx(seeds.map((s) => s.tenantId));
    expect(await pollReminders(ctx)).toEqual({ processed: 0, errors: 0 });
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
  });

  it("rescheduled-visit_old-cancelled-row-is-ignored_and-new-row-sends-exactly-once", async () => {
    // .NET при перенесенні закриває старий рядок і створює новий: старий не має спрацювати, новий - так
    const s = await seed("2h", new Date(NOW.getTime() + 2 * hour));
    await addReminder(s, new Date(NOW.getTime() - 3 * hour), "cancelled"); // для старого часу візиту
    await addReminder(s, NOW, "scheduled");
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollReminders(ctx); await pollOutbox(ctx);
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
  });

  it("reminder-option-changed-to-other-offset_stale-row-is-closed-not-sent", async () => {
    // клієнт змінив 2h -> 1h, але рядок 2h ще 'scheduled' (запис пішов повз .NET): воркер не шле застарілий
    const s = await seed("1h", new Date(NOW.getTime() + 2 * hour));
    const id = await addReminder(s, NOW);
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollReminders(ctx); await pollOutbox(ctx);
    // до візиту 2 год, опція 1h: нагадування застаріле -> закрите без відправки
    expect(sent).toHaveLength(0);
    const st = (await admin.query("SELECT status FROM beauty_reminders WHERE id=$1", [id])).rows[0].status;
    expect(st).toBe("cancelled");
  });
});
