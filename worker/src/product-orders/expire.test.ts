import { describe, expect, it } from "vitest";
import { makeDeps } from "../testing/fakes";
import { pollProductOrders, type PollContext } from "../scheduler/pollers";
import { StaticTenantDirectory } from "../db/tenants";
import { EXPIRE_BATCH_SIZE, PgProductOrdersExpire, isProductOrdersFlagOn, type ProductOrdersExpireStore } from "./expire";

const TENANT = "11111111-1111-4111-8111-111111111111";

function ctxWith(over: Partial<PollContext>, calls: { n: number }, moduleOn = true, result = { expired: 2, stuck: 0 }, lines: { info: string[]; warn: string[] } = { info: [], warn: [] }): PollContext {
  const deps = makeDeps();
  deps.deps.data.isInventoryEnabled = async () => moduleOn;
  const productOrders: ProductOrdersExpireStore = { runForTenant: async () => { calls.n++; return result; } };
  const logger = { info: (m: string) => { lines.info.push(m); }, error: () => undefined, warn: (m: string) => { lines.warn.push(m); } };
  return { deps: deps.deps, store: {} as never, tenants: StaticTenantDirectory.fromEnv(TENANT), logger, productOrders, ...over };
}

describe("isProductOrdersFlagOn", () => {
  it.each([["true", true], ["TRUE", true], ["True", true], [" true ", true], ["1", false], ["yes", false], ["false", false], ["", false], [undefined, false], ["truee", false]])(
    "%j -> %s", (v, expected) => expect(isProductOrdersFlagOn(v as string | undefined)).toBe(expected));
});

describe("pollProductOrders gates", () => {
  it("is idle by default (flag unset)", async () => {
    const calls = { n: 0 };
    expect(await pollProductOrders(ctxWith({}, calls))).toEqual({ processed: 0, errors: 0 });
    expect(calls.n).toBe(0);
  });
  it("is idle with the flag off", async () => {
    const calls = { n: 0 };
    expect(await pollProductOrders(ctxWith({ productOrdersEnabled: false }, calls))).toEqual({ processed: 0, errors: 0 });
    expect(calls.n).toBe(0);
  });
  it("does nothing when the beauty_inventory module is off", async () => {
    const calls = { n: 0 };
    expect(await pollProductOrders(ctxWith({ productOrdersEnabled: true }, calls, false))).toEqual({ processed: 0, errors: 0 });
    expect(calls.n).toBe(0);
  });
  it("does nothing without a store", async () => {
    const calls = { n: 0 };
    expect(await pollProductOrders(ctxWith({ productOrdersEnabled: true, productOrders: undefined }, calls))).toEqual({ processed: 0, errors: 0 });
  });
  it("runs when flag and module are on; info log without stuck", async () => {
    const calls = { n: 0 }, lines = { info: [] as string[], warn: [] as string[] };
    expect(await pollProductOrders(ctxWith({ productOrdersEnabled: true }, calls, true, { expired: 2, stuck: 0 }, lines))).toEqual({ processed: 2, errors: 0 });
    expect(calls.n).toBe(1);
    expect(lines.info).toEqual([`product_orders_expire tenant=${TENANT} expired=2 stuck=0`]);
    expect(lines.warn).toEqual([]);
  });
  it("stuck > 0 is logged as a warning", async () => {
    const calls = { n: 0 }, lines = { info: [] as string[], warn: [] as string[] };
    await pollProductOrders(ctxWith({ productOrdersEnabled: true }, calls, true, { expired: 0, stuck: 3 }, lines));
    expect(lines.warn).toEqual([`product_orders_expire tenant=${TENANT} expired=0 stuck=3`]);
    expect(lines.info).toEqual([]);
  });
});

describe("PgProductOrdersExpire", () => {
  it("batches until a short batch and uses only worker-granted columns", async () => {
    const sqls: string[] = [];
    const counts = [EXPIRE_BATCH_SIZE, EXPIRE_BATCH_SIZE, 7];
    const db = {
      tx: async (fn: (c: unknown) => Promise<unknown>) =>
        fn({ query: async (sql: string) => { sqls.push(sql); return sql.includes("count(*)") ? { rows: [{ n: 4 }] } : { rowCount: counts.shift() ?? 0 }; } }),
    };
    const res = await new PgProductOrdersExpire(db as never).runForTenant(new Date("2026-11-20T10:00:00Z"));
    expect(res).toEqual({ expired: 2 * EXPIRE_BATCH_SIZE + 7, stuck: 4 });
    expect(sqls).toHaveLength(4);
    // column-level GRANT: no column outside SELECT(id, tenant_id, status, reservation_expires_at, payment_started_at) / UPDATE(status, closed_at, close_reason, updated_at)
    expect(sqls.join(" ")).not.toMatch(/guest_|total_amount|payment_id|order_number|public_token|client_id|location_id|created_at|paid_at|refund|cancel|fulfilled/);
  });
});
