import { describe, expect, it } from "vitest";
import { assertInventoryTelegramGate } from "./telegram-gate";

const T1 = "11111111-1111-4111-8111-111111111111";
const T2 = "22222222-2222-4222-8222-222222222222";
const ON = { WORKER_INVENTORY_TELEGRAM_ENABLED: "true" };

describe("assertInventoryTelegramGate (M1)", () => {
  it("flag_off_never_throws", () => {
    expect(assertInventoryTelegramGate({ ASPNETCORE_ENVIRONMENT: "Production" })).toBeNull();
  });
  it("development_passes_without_restriction", () => {
    expect(assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Development" })).toBeNull();
  });
  it("development_with_node_env_production_counts_as_non_development", () => {
    expect(() => assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Development", NODE_ENV: "production" })).toThrow(/L3/);
  });
  it("staging_without_allowlist_or_l3_throws", () => {
    expect(() => assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Staging" })).toThrow(/PRE_L3_TENANT_IDS/);
    expect(() => assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Staging", WORKER_INVENTORY_TELEGRAM_PRE_L3_TENANT_IDS: " , " })).toThrow();
  });
  it("staging_with_allowlist_returns_the_set", () => {
    const s = assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Staging", WORKER_INVENTORY_TELEGRAM_PRE_L3_TENANT_IDS: `${T1}, ${T2.toUpperCase()}` });
    expect([...s!]).toEqual([T1, T2]);
  });
  it("invalid_allowlist_uuid_throws", () => {
    expect(() => assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Staging", WORKER_INVENTORY_TELEGRAM_PRE_L3_TENANT_IDS: "nope" })).toThrow(/UUID/);
  });
  it("production_with_l3_passes_unrestricted_without_throws", () => {
    expect(assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Production", WORKER_INVENTORY_TELEGRAM_L3_APPROVED: "true" })).toBeNull();
    expect(() => assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Production" })).toThrow();
    expect(() => assertInventoryTelegramGate({ ...ON, ASPNETCORE_ENVIRONMENT: "Production", WORKER_INVENTORY_TELEGRAM_L3_APPROVED: "false" })).toThrow();
    expect(() => assertInventoryTelegramGate({ ...ON, WORKER_INVENTORY_TELEGRAM_L3_APPROVED: "yes" })).toThrow(); // unset env = non-development
  });
});
