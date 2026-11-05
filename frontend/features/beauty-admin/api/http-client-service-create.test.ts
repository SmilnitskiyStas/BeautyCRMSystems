import { beforeEach, describe, expect, it, vi } from "vitest";
import { newProgress, PartialCreateError, runCreateService, type ServiceCreatePlan } from "../service-create";

const apiJson = vi.fn<(path: string, init?: { method?: string; body?: string }) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: { method?: string; body?: string }) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

let calls: { method: string; path: string; body: unknown }[] = [];
let respond: (method: string, path: string) => unknown;

beforeEach(() => {
  calls = [];
  respond = () => ({});
  apiJson.mockReset().mockImplementation(async (path, init) => {
    const method = init?.method ?? "GET";
    calls.push({ method, path, body: init?.body ? JSON.parse(init.body) : undefined });
    return respond(method, path);
  });
});

const spec = (id: string, serviceIds: string[]) => ({
  id,
  name: `М ${id}`,
  phone: null,
  position: null,
  isActive: true,
  services: serviceIds.map((s) => ({ id: s, name: s })),
  locations: [],
});

describe("http: створення послуги", () => {
  it("POST /services з обрізаними полями; порожні опис і категорія -> null; повертає id", async () => {
    respond = () => ({ id: "s1", name: "Стрижка", description: null, category: null, durationMinutes: 45, isActive: true, networkPrice: null });
    const r = await httpBeautyApi.createService({ name: "  Стрижка ", description: " ", category: "", durationMinutes: 45, isActive: true });
    expect(calls[0]).toEqual({
      method: "POST",
      path: "/api/beauty/services",
      body: { name: "Стрижка", description: null, category: null, durationMinutes: 45, isActive: true },
    });
    expect(r).toMatchObject({ id: "s1", name: "Стрижка", durationMinutes: 45 });
  });

  it("PUT /services/{id}/prices: мережева без locationId, заклад - з locationId", async () => {
    await httpBeautyApi.setServicePrice("s1", 500);
    await httpBeautyApi.setServicePrice("s1", 450.5, "L1");
    await httpBeautyApi.setServicePrice("s1", 300, null);
    expect(calls.map((c) => [c.method, c.path, c.body])).toEqual([
      ["PUT", "/api/beauty/services/s1/prices", { price: 500 }],
      ["PUT", "/api/beauty/services/s1/prices", { locationId: "L1", price: 450.5 }],
      ["PUT", "/api/beauty/services/s1/prices", { price: 300 }],
    ]);
  });
});

describe("послідовність створення (runCreateService поверх http)", () => {
  const plan: ServiceCreatePlan = {
    input: { name: "Фарбування", description: "", category: "Волосся", durationMinutes: 120, isActive: true },
    networkPrice: 1500,
    locationPrices: [{ locationId: "L2", locationName: "Лівий берег", price: 1700 }],
    specialists: [{ id: "m1", name: "Анна" }],
  };

  it("послуга -> мережева ціна -> ціни закладів -> послуги майстра, з merge наявних послуг", async () => {
    respond = (method, path) => {
      if (method === "POST" && path.endsWith("/services")) return { id: "new", name: "Фарбування", description: null, category: "Волосся", durationMinutes: 120, isActive: true, networkPrice: null };
      if (method === "GET" && path.endsWith("/specialists/m1")) return { ...spec("m1", ["a", "b"]) };
      return {};
    };
    await runCreateService(httpBeautyApi, plan, newProgress());
    expect(calls.map((c) => `${c.method} ${c.path.replace("/api/beauty", "")}`)).toEqual([
      "POST /services",
      "PUT /services/new/prices",
      "PUT /services/new/prices",
      "GET /specialists/m1",
      "PUT /specialists/m1/services",
    ]);
    expect(calls[1].body).toEqual({ price: 1500 });
    expect(calls[2].body).toEqual({ locationId: "L2", price: 1700 });
    expect(calls[4].body).toEqual({ serviceIds: ["a", "b", "new"] });
  });

  it("збій після створення -> PartialCreateError; повтор не створює послугу вдруге й не повторює виконане", async () => {
    let failPrice = true;
    respond = (method, path) => {
      if (method === "POST" && path.endsWith("/services")) return { id: "new", name: "Фарбування", description: null, category: null, durationMinutes: 120, isActive: true, networkPrice: null };
      if (method === "GET") return spec("m1", ["a"]);
      if (method === "PUT" && path.includes("/prices") && failPrice) throw new Error("boom");
      return {};
    };
    const progress = newProgress();
    await expect(runCreateService(httpBeautyApi, plan, progress)).rejects.toBeInstanceOf(PartialCreateError);
    expect(progress.serviceId).toBe("new");
    failPrice = false;
    calls = [];
    await runCreateService(httpBeautyApi, plan, progress);
    expect(calls.filter((c) => c.method === "POST")).toHaveLength(0);
    expect(calls.filter((c) => c.path.endsWith("/prices"))).toHaveLength(2);
  });

  it("збій самого створення - звичайна помилка, прогрес порожній", async () => {
    respond = () => {
      throw new Error("down");
    };
    const progress = newProgress();
    await expect(runCreateService(httpBeautyApi, plan, progress)).rejects.not.toBeInstanceOf(PartialCreateError);
    expect(progress.serviceId).toBeNull();
  });
});
