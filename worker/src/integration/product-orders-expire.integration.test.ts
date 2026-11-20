import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { PgBeautyData } from "../adapters/pg-data";
import { TenantDb } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import { EXPIRE_BATCH_SIZE, PgProductOrdersExpire } from "../product-orders/expire";
import type { BeautyDeps } from "../ports";
import { pollProductOrders, type PollContext } from "../scheduler/pollers";

/** TASK-904: pollProductOrders against real PostgreSQL as a role with ONLY the worker GRANTs of scripts/dev/setup-db.ps1. */
const WORKER = "beauty_worker_po";
const APP = "beauty_app_po";
const NOW = new Date("2026-11-20T12:00:00Z");
const ago = (min: number) => new Date(NOW.getTime() - min * 60_000);

let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;

function scriptGrants(role: "worker" | "app"): string {
  const text = readFileSync(new URL("../../../scripts/dev/setup-db.ps1", import.meta.url), "utf8");
  const body = /\$grants = @"\r?\n([\s\S]*?)\r?\n"@/.exec(text)![1]!;
  const [appPart, workerPart] = body.split(/-- Роль воркера[^\n]*\n/);
  return (role === "app" ? appPart! : workerPart!).replace(/beautycrm_app/g, APP).replace(/beautycrm_worker/g, WORKER);
}

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${WORKER} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`CREATE ROLE ${APP} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT USAGE ON SCHEMA public TO ${APP}, ${WORKER}`);
  await admin.query(scriptGrants("app"));
  await admin.query(scriptGrants("worker"));
  const u = new URL(container.getConnectionUri());
  u.username = WORKER; u.password = "pw";
  db = TenantDb.create(u.toString(), 8);
}, 180_000);

afterAll(async () => { await db?.close(); await admin?.end(); await container?.stop(); });

const q = (sql: string, p: unknown[] = []) => admin.query(sql, p);

interface T { id: string; loc: string; n: number }
async function tenant(o: { modules?: string } = {}): Promise<T> {
  const id = randomUUID(), loc = randomUUID();
  await q("INSERT INTO tenants (id, name, slug, modules) VALUES ($1,'T',$2,$3::text[])", [id, `t-${id.slice(0, 8)}`, o.modules ?? "{beauty_inventory}"]);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L','Europe/Kyiv',true)", [loc, id]);
  return { id, loc, n: 0 };
}

interface OrderOpts { status?: string; expiresMin?: number; paymentStartedMin?: number | null }
/** expiresMin = minutes ago the reservation ended (negative = in the future). */
async function order(t: T, o: OrderOpts = {}): Promise<string> {
  const id = randomUUID(), status = o.status ?? "pending_payment";
  const started = o.paymentStartedMin == null ? null : ago(o.paymentStartedMin);
  const paid = status === "paid" || status === "fulfilled";
  const closed = status === "cancelled";
  const c = await admin.connect();
  try {
    await c.query("SET session_replication_role = replica"); // paid/fulfilled rows point to payment/user rows we do not need here
    await c.query(
      `INSERT INTO beauty_product_orders (id, tenant_id, location_id, order_number, public_token_hash, idempotency_key_hash, idempotency_request_hash, status, source,
         guest_name, guest_phone, total_amount, reservation_expires_at, payment_started_at, payment_id, paid_at, fulfilled_at, fulfilled_by_user_id, closed_at, close_reason)
       VALUES ($1,$2,$3,$4,$5,$6,'r',$7,'web','Guest Name','+380501234567',100,$8,$9,$10,$11,$12,$13,$14,$15)`,
      [id, t.id, t.loc, ++t.n, id.replace(/-/g, "").padEnd(64, "a"), id.replace(/-/g, "").padEnd(64, "b"), status,
        ago(o.expiresMin ?? 0), started, paid ? randomUUID() : null, paid ? NOW : null,
        status === "fulfilled" ? NOW : null, status === "fulfilled" ? randomUUID() : null, closed ? NOW : null, closed ? "client" : null]);
  } finally { await c.query("RESET session_replication_role"); c.release(); }
  return id;
}
const row = async (id: string) => (await q("SELECT * FROM beauty_product_orders WHERE id = $1", [id])).rows[0];
const statusCount = async (t: T, status: string) => Number((await q("SELECT count(*) FROM beauty_product_orders WHERE tenant_id = $1 AND status = $2", [t.id, status])).rows[0].count);

function ctx(tenants: T[], over: Partial<PollContext> = {}, lines: { info: string[]; warn: string[] } = { info: [], warn: [] }): PollContext {
  const deps = { now: () => NOW, data: new PgBeautyData(db, () => NOW) } as unknown as BeautyDeps;
  return {
    deps, store: {} as never, tenants: new StaticTenantDirectory(tenants.map((t) => t.id)),
    logger: { info: (m) => { lines.info.push(m); }, error: () => undefined, warn: (m) => { lines.warn.push(m); } },
    productOrders: new PgProductOrdersExpire(db), productOrdersEnabled: true, ...over,
  };
}

describe("pollProductOrders (PostgreSQL)", () => {
  it("closes only expired unpaid orders without a payment in progress", async () => {
    const t = await tenant();
    const expired = await order(t, { expiresMin: 5 });
    const future = await order(t, { expiresMin: -10 });
    const withinGrace = await order(t, { expiresMin: 0.5 });
    const inProgress = await order(t, { expiresMin: 5, paymentStartedMin: 2 });
    const paid = await order(t, { status: "paid", expiresMin: 60 });
    const fulfilled = await order(t, { status: "fulfilled", expiresMin: 60 });
    const cancelled = await order(t, { status: "cancelled", expiresMin: 60 });
    const lines = { info: [] as string[], warn: [] as string[] };
    const res = await pollProductOrders(ctx([t], {}, lines));
    expect(res).toEqual({ processed: 1, errors: 0 });
    const e = await row(expired);
    expect(e).toMatchObject({ status: "expired", close_reason: "expired" });
    expect(e.closed_at).toEqual(e.reservation_expires_at);
    expect(e.updated_at).toEqual(NOW);
    for (const [id, st] of [[future, "pending_payment"], [withinGrace, "pending_payment"], [inProgress, "pending_payment"], [paid, "paid"], [fulfilled, "fulfilled"], [cancelled, "cancelled"]] as const) {
      const r = await row(id);
      expect(r.status).toBe(st);
      if (st !== "cancelled") expect(r.close_reason).toBeNull();
    }
    expect(lines.info).toEqual([`product_orders_expire tenant=${t.id} expired=1 stuck=0`]);
  });

  it("is idempotent: a second run changes nothing", async () => {
    const t = await tenant();
    await order(t, { expiresMin: 5 });
    await pollProductOrders(ctx([t]));
    const res = await pollProductOrders(ctx([t]));
    expect(res.processed).toBe(0);
    expect(await statusCount(t, "expired")).toBe(1);
  });

  it(`handles more than ${EXPIRE_BATCH_SIZE} orders in batches`, async () => {
    const t = await tenant();
    await q(
      `INSERT INTO beauty_product_orders (tenant_id, location_id, order_number, public_token_hash, idempotency_key_hash, idempotency_request_hash, source, guest_name, guest_phone, total_amount, reservation_expires_at)
       SELECT $1, $2, g, md5(g::text) || md5('t' || g::text), md5(g::text) || md5('i' || g::text), 'r', 'web', 'Guest Name', '+380501234567', 100, $3
       FROM generate_series(1, $4) g`, [t.id, t.loc, ago(30), EXPIRE_BATCH_SIZE + 37]);
    const res = await pollProductOrders(ctx([t]));
    expect(res.processed).toBe(EXPIRE_BATCH_SIZE + 37);
    expect(await statusCount(t, "expired")).toBe(EXPIRE_BATCH_SIZE + 37);
    expect(await statusCount(t, "pending_payment")).toBe(0);
  });

  it("RLS: only the current tenant's orders are touched; module-off tenant is skipped", async () => {
    const a = await tenant(), b = await tenant(), off = await tenant({ modules: "{beauty_marketing}" });
    const oa = await order(a, { expiresMin: 5 }), ob = await order(b, { expiresMin: 5 }), oo = await order(off, { expiresMin: 5 });
    const res = await pollProductOrders(ctx([a, off]));
    expect(res).toEqual({ processed: 1, errors: 0 });
    expect((await row(oa)).status).toBe("expired");
    expect((await row(ob)).status).toBe("pending_payment"); // not in the directory
    expect((await row(oo)).status).toBe("pending_payment"); // module off
    // direct store call under tenant scope also never crosses tenants
    const { withTenant } = await import("../db/tenant-db");
    const r = await withTenant(b.id, () => new PgProductOrdersExpire(db).runForTenant(NOW));
    expect(r.expired).toBe(1);
    expect((await row(ob)).status).toBe("expired");
  });

  it("flag off or unset: nothing is closed", async () => {
    const t = await tenant();
    const id = await order(t, { expiresMin: 5 });
    await pollProductOrders(ctx([t], { productOrdersEnabled: false }));
    await pollProductOrders(ctx([t], { productOrdersEnabled: undefined }));
    expect((await row(id)).status).toBe("pending_payment");
  });

  it("stuck payment (> 30 min) is only logged as a warning and left untouched", async () => {
    const t = await tenant();
    const stuck = await order(t, { expiresMin: 120, paymentStartedMin: 45 });
    const fresh = await order(t, { expiresMin: 120, paymentStartedMin: 10 });
    const lines = { info: [] as string[], warn: [] as string[] };
    const res = await pollProductOrders(ctx([t], {}, lines));
    expect(res.processed).toBe(0);
    expect(lines.warn).toEqual([`product_orders_expire tenant=${t.id} expired=0 stuck=1`]);
    expect(lines.warn[0]).not.toMatch(/Guest|\+380|amount/);
    for (const id of [stuck, fresh]) expect((await row(id)).status).toBe("pending_payment");
  });

  it("the worker role cannot read or change other columns (column GRANT)", async () => {
    const t = await tenant();
    await order(t);
    const w = new pg.Pool({ connectionString: (() => { const u = new URL(container.getConnectionUri()); u.username = WORKER; u.password = "pw"; return u.toString(); })() });
    w.on("error", () => undefined);
    try {
      const c = await w.connect();
      try {
        await c.query("BEGIN");
        await c.query("SELECT set_config('app.tenant_id', $1, true)", [t.id]);
        await expect(c.query("SELECT guest_phone FROM beauty_product_orders")).rejects.toMatchObject({ code: "42501" });
        await c.query("ROLLBACK");
        await c.query("BEGIN");
        await c.query("SELECT set_config('app.tenant_id', $1, true)", [t.id]);
        await expect(c.query("UPDATE beauty_product_orders SET total_amount = 1")).rejects.toMatchObject({ code: "42501" });
        await c.query("ROLLBACK");
      } finally { c.release(); }
    } finally { await w.end(); }
  });
});
