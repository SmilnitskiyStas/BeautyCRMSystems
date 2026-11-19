import { describe, expect, it } from "vitest";
import { makeDeps } from "../testing/fakes";
import { pollInventory, type PollContext } from "../scheduler/pollers";
import { StaticTenantDirectory } from "../db/tenants";
import { DEFAULT_INVENTORY_CONFIG, expiryBucket, loadInventoryConfig, localParts, type InventoryAlertStore } from "./alerts";

const TENANT = "11111111-1111-4111-8111-111111111111";
const logger = { info: () => undefined, error: () => undefined };

function ctxWith(over: Partial<PollContext>, calls: { n: number }, moduleOn = true): PollContext {
  const deps = makeDeps();
  deps.deps.data.isInventoryEnabled = async () => moduleOn;
  const inventory: InventoryAlertStore = { runForTenant: async () => { calls.n++; return { locations: 1, created: 2, resolved: 1, capped: false, errors: 0 }; } };
  return { deps: deps.deps, store: {} as never, tenants: StaticTenantDirectory.fromEnv(TENANT), logger, inventory, ...over };
}

describe("pollInventory gates", () => {
  it("does nothing with WORKER_INVENTORY_ENABLED=false", async () => {
    const calls = { n: 0 };
    expect(await pollInventory(ctxWith({ inventoryEnabled: false }, calls))).toEqual({ processed: 0, errors: 0 });
    expect(calls.n).toBe(0);
  });
  it("does nothing when the tenant module is off", async () => {
    const calls = { n: 0 };
    expect(await pollInventory(ctxWith({ inventoryEnabled: true }, calls, false))).toEqual({ processed: 0, errors: 0 });
    expect(calls.n).toBe(0);
  });
  it("runs when flag and module are on", async () => {
    const calls = { n: 0 };
    expect(await pollInventory(ctxWith({ inventoryEnabled: true }, calls))).toEqual({ processed: 3, errors: 0 });
    expect(calls.n).toBe(1);
  });
});

describe("expiryBucket", () => {
  const t = [30, 14, 7];
  it.each([[-1, 0], [-100, 0], [0, 7], [5, 7], [7, 7], [8, 14], [14, 14], [15, 30], [30, 30], [31, null]])("days %i -> %s", (d, b) => {
    expect(expiryBucket(d, t)).toBe(b);
  });
});

describe("loadInventoryConfig", () => {
  it("defaults", () => expect(loadInventoryConfig({})).toEqual(DEFAULT_INVENTORY_CONFIG));
  it("sorts thresholds descending", () => expect(loadInventoryConfig({ WORKER_INVENTORY_EXPIRY_DAYS: "7, 60,14" }).expiryDays).toEqual([60, 14, 7]));
  it.each(["", "abc", "0", "366", "7,7", "1,2,3,4,5,6", "7,,14", "-5"].filter((v) => v !== ""))("rejects expiry days %s", (v) => {
    expect(() => loadInventoryConfig({ WORKER_INVENTORY_EXPIRY_DAYS: v })).toThrow();
  });
  it.each(["24", "-1", "x", "7.5"])("rejects local hour %s", (v) => {
    expect(() => loadInventoryConfig({ WORKER_INVENTORY_LOCAL_HOUR: v })).toThrow();
  });
  it("accepts local hour 0", () => expect(loadInventoryConfig({ WORKER_INVENTORY_LOCAL_HOUR: "0" }).localHour).toBe(0));
});

describe("localParts", () => {
  it("Kyiv winter and summer offsets, DST day", () => {
    expect(localParts(new Date("2026-11-10T06:30:00Z"), "Europe/Kyiv")).toEqual({ hour: 8, date: "2026-11-10" });
    expect(localParts(new Date("2026-07-10T05:30:00Z"), "Europe/Kyiv")).toEqual({ hour: 8, date: "2026-07-10" });
    expect(localParts(new Date("2026-03-29T04:30:00Z"), "Europe/Kyiv")).toEqual({ hour: 7, date: "2026-03-29" }); // after the jump: UTC+3
    expect(localParts(new Date("2026-03-29T05:30:00Z"), "Europe/Kyiv")).toEqual({ hour: 8, date: "2026-03-29" });
  });
  it("local date differs from UTC date", () => {
    expect(localParts(new Date("2026-11-10T23:30:00Z"), "Europe/Kyiv")).toEqual({ hour: 1, date: "2026-11-11" });
    expect(localParts(new Date("2026-11-10T01:00:00Z"), "America/New_York")).toEqual({ hour: 20, date: "2026-11-09" });
  });
  it("midnight is hour 0, not 24", () => {
    expect(localParts(new Date("2026-11-09T22:10:00Z"), "Europe/Kyiv").hour).toBe(0);
  });
});
