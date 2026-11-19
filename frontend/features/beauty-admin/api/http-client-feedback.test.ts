import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError, humanizeFeedbackError, readApiError } from "@/features/beauty-auth/errors";

const apiJson = vi.fn<(path: string, init?: { method?: string; body?: string }) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: { method?: string; body?: string }) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

beforeEach(() => apiJson.mockReset());

describe("http: звернення до розробників", () => {
  it("POST /api/beauty/feedback з обрізаним текстом і pageUrl; повертає id", async () => {
    apiJson.mockResolvedValue({ id: "f1" });
    const r = await httpBeautyApi.sendFeedback({ type: "bug", severity: "high", message: "  Не відкривається  календар ", pageUrl: "/beauty/calendar" });
    expect(r).toEqual({ id: "f1" });
    const [path, init] = apiJson.mock.calls[0];
    expect(path).toBe("/api/beauty/feedback");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init!.body!)).toEqual({ type: "bug", severity: "high", message: "Не відкривається  календар", pageUrl: "/beauty/calendar" });
  });

  it("без pageUrl поле не передається", async () => {
    apiJson.mockResolvedValue({ id: "f2" });
    await httpBeautyApi.sendFeedback({ type: "cooperation", severity: "normal", message: "Пропозиція співпраці" });
    expect(JSON.parse(apiJson.mock.calls[0][1]!.body!)).not.toHaveProperty("pageUrl");
  });

  it("429 з Retry-After -> BeautyApiError із секундами та людським текстом", async () => {
    const res = new Response(JSON.stringify({ code: "rate_limited", message: "x" }), { status: 429, headers: { "Retry-After": "120" } });
    const e = await readApiError(res);
    expect(e).toBeInstanceOf(BeautyApiError);
    expect([e.status, e.code, e.retryAfterSeconds]).toEqual([429, "rate_limited", 120]);
    expect(humanizeFeedbackError(e)).toContain("2 хв");
  });
});
