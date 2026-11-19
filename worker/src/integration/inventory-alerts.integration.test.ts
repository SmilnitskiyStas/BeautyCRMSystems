import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { PgBeautyData } from "../adapters/pg-data";
import { TenantDb, withTenant } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import { DEFAULT_INVENTORY_CONFIG, PgInventoryAlerts, type InventoryAlertConfig } from "../inventory/alerts";
import type { BeautyDeps } from "../ports";
import { pollInventory, type PollContext } from "../scheduler/pollers";

/** TASK-887: pollInventory against real PostgreSQL as a role with ONLY the worker GRANTs of scripts/dev/setup-db.ps1. */
const WORKER = "beauty_worker_inv";
const APP = "beauty_app_inv";
// Kyiv (UTC+2 in November): 05:00Z = 07:00 (before the 08:00 gate), 07:00Z = 09:00 (after)
const BEFORE = new Date("2026-11-10T05:00:00Z");
const AFTER = new Date("2026-11-10T07:00:00Z");
const TODAY = "2026-11-10";

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

interface T { id: string; user: string; loc: string }
async function tenant(o: { modules?: string; tz?: string } = {}): Promise<T> {
  const id = randomUUID(), user = randomUUID(), loc = randomUUID();
  await q("INSERT INTO tenants (id, name, slug, modules) VALUES ($1,'T',$2,$3::text[])", [id, `t-${id.slice(0, 8)}`, o.modules ?? "{beauty_inventory}"]);
  await q("INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ($1,$2,$3,'U','x','owner')", [user, id, `${user}@x.test`]);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L',$3,true)", [loc, id, o.tz ?? "Europe/Kyiv"]);
  return { id, user, loc };
}
async function location(t: T, tz = "Europe/Kyiv"): Promise<string> {
  const id = randomUUID();
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L2',$3,true)", [id, t.id, tz]);
  return id;
}
async function product(t: T, o: { min?: number | null; active?: boolean } = {}): Promise<string> {
  const id = randomUUID();
  await q("INSERT INTO beauty_products (id, tenant_id, name, unit, purpose, min_stock, is_active, updated_by_user_id) VALUES ($1,$2,'P','pcs','both',$3,$4,$5)",
    [id, t.id, o.min === undefined ? null : o.min, o.active ?? true, t.user]);
  return id;
}
async function level(t: T, loc: string, prod: string, qty: number, shortage = 0): Promise<void> {
  await q("INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id, quantity, shortage_quantity) VALUES ($1,$2,$3,$4,$5)", [t.id, loc, prod, qty, shortage]);
}
async function batch(t: T, loc: string, prod: string, expires: string | null, qty = 5): Promise<string> {
  const op = randomUUID(), id = randomUUID();
  await q("INSERT INTO beauty_stock_operations (id, tenant_id, kind, location_id) VALUES ($1,$2,'receipt',$3)", [op, t.id, loc]);
  await q(`INSERT INTO beauty_stock_batches (id, tenant_id, location_id, product_id, operation_id, received_at, expires_on, initial_quantity, quantity)
           VALUES ($1,$2,$3,$4,$5,now(),$6,10,$7)`, [id, t.id, loc, prod, op, expires, qty]);
  return id;
}
const alerts = async (t: T, kind?: string) =>
  (await q("SELECT * FROM beauty_staff_alerts WHERE tenant_id = $1 AND ($2::text IS NULL OR kind = $2) ORDER BY dedupe_key", [t.id, kind ?? null])).rows;

function ctx(tenants: T[], now: Date, over: Partial<PollContext> = {}, cfg: InventoryAlertConfig = DEFAULT_INVENTORY_CONFIG): PollContext {
  const deps = { now: () => now, data: new PgBeautyData(db, () => now) } as unknown as BeautyDeps;
  return {
    deps, store: {} as never, tenants: new StaticTenantDirectory(tenants.map((t) => t.id)), logger: { info: () => undefined, error: () => undefined },
    inventory: new PgInventoryAlerts(db), inventoryConfig: cfg, inventoryEnabled: true, ...over,
  };
}

describe("pollInventory (PostgreSQL)", () => {
  it("gates: flag off and module off create nothing", async () => {
    const on = await tenant(), off = await tenant({ modules: "{beauty_marketing}" });
    for (const t of [on, off]) { const p = await product(t, { min: 10 }); await level(t, t.loc, p, 1); }
    await pollInventory(ctx([on, off], AFTER, { inventoryEnabled: false }));
    expect((await alerts(on)).length + (await alerts(off)).length).toBe(0);
    await pollInventory(ctx([on, off], AFTER));
    expect((await alerts(on)).length).toBe(1);
    expect((await alerts(off)).length).toBe(0);
  });

  it("daily gate: nothing before 08:00 local, created after; no PII columns filled", async () => {
    const t = await tenant();
    const p = await product(t, { min: 10 }); await level(t, t.loc, p, 3);
    await pollInventory(ctx([t], BEFORE));
    expect(await alerts(t)).toHaveLength(0);
    await pollInventory(ctx([t], AFTER));
    const [a] = await alerts(t);
    expect(a).toMatchObject({ kind: "low_stock", status: "open", dedupe_key: `low_stock:${t.loc}:${p}:${TODAY}`, operation_id: null });
    expect(Number(a.quantity)).toBe(3);
    expect(Number(a.threshold)).toBe(10);
  });

  it("daily gate across DST and other zones", async () => {
    const t = await tenant({ tz: "Europe/Kyiv" });
    const ny = await location(t, "America/New_York");
    const p = await product(t, { min: 10 }); await level(t, t.loc, p, 1); await level(t, ny, p, 1);
    // 2026-03-29 is the Kyiv DST day: 04:30Z is already 07:30 (UTC+3) -> still gated, New York 00:30 EDT -> gated
    const dst = ctx([t], new Date("2026-03-29T04:30:00Z"));
    await pollInventory(dst);
    expect(await alerts(t)).toHaveLength(0);
    // 05:30Z = 08:30 Kyiv (UTC+3; with a naive +2 it would be 07:30) -> only Kyiv creates
    await pollInventory(ctx([t], new Date("2026-03-29T05:30:00Z")));
    expect((await alerts(t)).map((a) => a.location_id)).toEqual([t.loc]);
    // 13:30Z = 09:30 EDT -> New York creates too
    await pollInventory(ctx([t], new Date("2026-03-29T13:30:00Z")));
    expect((await alerts(t)).map((a) => a.location_id).sort()).toEqual([t.loc, ny].sort());
  });

  it("low_stock: only pairs with a level row, active, enabled, effective min with override and shortage balance", async () => {
    const t = await tenant();
    const noLevel = await product(t, { min: 10 });
    const ok = await product(t, { min: 10 }); await level(t, t.loc, ok, 5);
    const fine = await product(t, { min: 10 }); await level(t, t.loc, fine, 10); // not below
    const noMin = await product(t, { min: null }); await level(t, t.loc, noMin, 0);
    const inactive = await product(t, { min: 10, active: false }); await level(t, t.loc, inactive, 1);
    const disabled = await product(t, { min: 10 }); await level(t, t.loc, disabled, 1);
    await q("INSERT INTO beauty_product_location_settings (tenant_id, location_id, product_id, is_enabled, updated_by_user_id) VALUES ($1,$2,$3,false,$4)", [t.id, t.loc, disabled, t.user]);
    const override = await product(t, { min: 100 }); await level(t, t.loc, override, 5);
    await q("INSERT INTO beauty_product_location_settings (tenant_id, location_id, product_id, min_stock, updated_by_user_id) VALUES ($1,$2,$3,2,$4)", [t.id, t.loc, override, t.user]); // min 2 -> 5 is fine
    const short = await product(t, { min: 10 }); await level(t, t.loc, short, 12, 5); // balance 7 < 10
    await pollInventory(ctx([t], AFTER));
    const got = (await alerts(t, "low_stock")).map((a) => a.product_id).sort();
    expect(got).toEqual([ok, short].sort());
    expect(got).not.toContain(noLevel);
    const s = (await alerts(t, "low_stock")).find((a) => a.product_id === short)!;
    expect(Number(s.quantity)).toBe(7);
  });

  it("low_stock dedupe between runs and no new alert while an unresolved one exists (next day)", async () => {
    const t = await tenant();
    const p = await product(t, { min: 10 }); await level(t, t.loc, p, 1);
    await pollInventory(ctx([t], AFTER));
    await pollInventory(ctx([t], new Date(AFTER.getTime() + 3_600_000)));
    expect(await alerts(t)).toHaveLength(1);
    await pollInventory(ctx([t], new Date(AFTER.getTime() + 24 * 3_600_000))); // next local day, alert still open
    expect(await alerts(t)).toHaveLength(1);
    // resolved manually, still low on the next local day -> a new key, a new alert
    await q("UPDATE beauty_staff_alerts SET status='resolved', resolved_at=now() WHERE tenant_id=$1", [t.id]);
    await pollInventory(ctx([t], new Date(AFTER.getTime() + 24 * 3_600_000)));
    expect(await alerts(t)).toHaveLength(2);
  });

  it("resolves stale low_stock, expiring and shortage alerts even before the daily gate", async () => {
    const t = await tenant();
    const p = await product(t, { min: 10 }); await level(t, t.loc, p, 1);
    await pollInventory(ctx([t], AFTER));
    const b = await batch(t, t.loc, p, "2026-11-15");
    await pollInventory(ctx([t], AFTER));
    const op = randomUUID();
    await q("INSERT INTO beauty_stock_operations (id, tenant_id, kind, location_id) VALUES ($1,$2,'receipt',$3)", [op, t.id, t.loc]);
    await q(`INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, operation_id, quantity, dedupe_key) VALUES ($1,'shortage',$2,$3,$4,2,$5)`, [t.id, t.loc, p, op, `shortage:${op}:${p}`]);
    expect((await alerts(t)).filter((a) => a.status === "open")).toHaveLength(3);
    await q("UPDATE beauty_stock_levels SET quantity = 50, shortage_quantity = 0 WHERE tenant_id=$1", [t.id]);
    await q("UPDATE beauty_stock_batches SET quantity = 0 WHERE id=$1", [b]);
    await pollInventory(ctx([t], BEFORE)); // before the gate: resolve still runs, nothing new is created
    const all = await alerts(t);
    expect(all).toHaveLength(3);
    expect(all.every((a) => a.status === "resolved" && a.resolved_at)).toBe(true);
  });

  it("does not resolve alerts that are still current", async () => {
    const t = await tenant();
    const p = await product(t, { min: 10 }); await level(t, t.loc, p, 1);
    await batch(t, t.loc, p, "2026-11-15", 4);
    await pollInventory(ctx([t], AFTER));
    await pollInventory(ctx([t], BEFORE));
    expect((await alerts(t)).every((a) => a.status === "open")).toBe(true);
  });

  it("expiring: thresholds 30/14/7, bucket 0 expired, replacement of the previous bucket, no unflagged batches", async () => {
    const t = await tenant();
    const p = await product(t, { min: null });
    const d = (n: number) => new Date(Date.parse(`${TODAY}T00:00:00Z`) + n * 86_400_000).toISOString().slice(0, 10);
    const b31 = await batch(t, t.loc, p, d(31)), b30 = await batch(t, t.loc, p, d(30)), b15 = await batch(t, t.loc, p, d(15)),
      b14 = await batch(t, t.loc, p, d(14)), b3 = await batch(t, t.loc, p, d(3)), b0 = await batch(t, t.loc, p, d(0)),
      bExp = await batch(t, t.loc, p, d(-4)), bNone = await batch(t, t.loc, p, null), bEmpty = await batch(t, t.loc, p, d(2), 0);
    await pollInventory(ctx([t], AFTER));
    const byBatch = new Map((await alerts(t, "expiring")).map((a) => [a.batch_id as string, a.days_bucket as number]));
    expect(byBatch.get(b31)).toBeUndefined();
    expect([byBatch.get(b30), byBatch.get(b15), byBatch.get(b14), byBatch.get(b3), byBatch.get(b0), byBatch.get(bExp)]).toEqual([30, 30, 14, 7, 7, 0]);
    expect(byBatch.has(bNone) || byBatch.has(bEmpty)).toBe(false);
    // a week later b30 reaches bucket 7 -> the 30 alert is replaced (resolved), the new one is open
    const later = new Date(AFTER.getTime() + 23 * 86_400_000);
    await pollInventory(ctx([t], later));
    const rows = (await alerts(t, "expiring")).filter((a) => a.batch_id === b30);
    expect(rows.map((a) => [a.days_bucket, a.status]).sort()).toEqual([[30, "resolved"], [7, "open"]]);
    // idempotent
    const n = (await alerts(t)).length;
    await pollInventory(ctx([t], later));
    expect(await alerts(t)).toHaveLength(n);
  });

  it("a batch received 5 days before expiry gets only bucket 7; inactive product still alerts", async () => {
    const t = await tenant();
    const p = await product(t, { active: false });
    const b = await batch(t, t.loc, p, "2026-11-15");
    await pollInventory(ctx([t], AFTER));
    expect((await alerts(t, "expiring")).map((a) => [a.batch_id, a.days_bucket])).toEqual([[b, 7]]);
  });

  it("caps new alerts at 500 per tenant per run, the rest comes next run; urgent expiry first", async () => {
    const t = await tenant();
    const p = await product(t, { min: null });
    const urgent = await batch(t, t.loc, p, "2026-11-11");
    await q(`INSERT INTO beauty_stock_batches (tenant_id, location_id, product_id, operation_id, received_at, expires_on, initial_quantity, quantity)
             SELECT $1, $2, $3, op.id, now(), DATE '2026-11-20', 10, 5 FROM generate_series(1, 520), (SELECT id FROM beauty_stock_operations WHERE tenant_id = $1 LIMIT 1) op`, [t.id, t.loc, p]);
    const r1 = await withTenant(t.id, () => new PgInventoryAlerts(db).runForTenant(AFTER, DEFAULT_INVENTORY_CONFIG));
    expect(r1).toMatchObject({ created: 500, capped: true });
    expect((await alerts(t, "expiring")).some((a) => a.batch_id === urgent)).toBe(true);
    const r2 = await withTenant(t.id, () => new PgInventoryAlerts(db).runForTenant(AFTER, DEFAULT_INVENTORY_CONFIG));
    expect(r2.created).toBe(21); // 521 batches in total
    expect(await alerts(t)).toHaveLength(521);
  });

  it("tenant isolation and a failing tenant does not stop the others", async () => {
    const a = await tenant(), b = await tenant({ tz: "Not/AZone" });
    const c = await tenant();
    for (const t of [a, b, c]) { const p = await product(t, { min: 10 }); await level(t, t.loc, p, 1); }
    const res = await pollInventory(ctx([a, b, c], AFTER));
    expect(res.errors).toBe(1);
    expect(await alerts(a)).toHaveLength(1);
    expect(await alerts(b)).toHaveLength(0);
    expect(await alerts(c)).toHaveLength(1);
    expect((await alerts(a))[0].tenant_id).toBe(a.id);
  });

  it("worker role: can SELECT/INSERT/UPDATE alerts but not DELETE; cannot write levels/batches", async () => {
    const t = await tenant();
    const p = await product(t, { min: 10 }); await level(t, t.loc, p, 1);
    await pollInventory(ctx([t], AFTER));
    const as = (sql: string, params: unknown[] = []) => withTenant(t.id, () => db.tx(async (c) => { await c.query(sql, params); }));
    await expect(as("DELETE FROM beauty_staff_alerts")).rejects.toMatchObject({ code: "42501" });
    await expect(as("UPDATE beauty_stock_levels SET quantity = 1")).rejects.toMatchObject({ code: "42501" });
    await expect(as("DELETE FROM beauty_stock_batches")).rejects.toMatchObject({ code: "42501" });
    await expect(as("INSERT INTO beauty_stock_operations (tenant_id, kind, location_id) VALUES ($1,'receipt',$2)", [t.id, t.loc])).rejects.toMatchObject({ code: "42501" });
  });
});
