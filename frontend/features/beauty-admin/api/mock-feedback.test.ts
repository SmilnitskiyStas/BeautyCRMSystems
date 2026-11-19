import { beforeEach, describe, expect, it } from "vitest";
import { mockBeautyApi, resetMockFeedback } from "./mock-client";

const ok = { type: "bug", severity: "normal", message: "Не відкривається календар", pageUrl: "/beauty/calendar" } as const;

beforeEach(() => {
  resetMockFeedback();
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
});

describe("mock: звернення до розробників", () => {
  it("202 { id } для всіх ролей персоналу", async () => {
    for (const role of ["owner", "admin", "specialist"]) {
      process.env.NEXT_PUBLIC_MOCK_ROLE = role;
      expect((await mockBeautyApi.sendFeedback({ ...ok })).id).toBeTruthy();
    }
  });

  it("422 коди: invalid_type, invalid_severity, invalid_message, invalid_page_url", async () => {
    await expect(mockBeautyApi.sendFeedback({ ...ok, type: "x" as never })).rejects.toMatchObject({ status: 422, code: "invalid_type" });
    await expect(mockBeautyApi.sendFeedback({ ...ok, severity: "" as never })).rejects.toMatchObject({ status: 422, code: "invalid_severity" });
    await expect(mockBeautyApi.sendFeedback({ ...ok, message: "коротко" })).rejects.toMatchObject({ status: 422, code: "invalid_message" });
    await expect(mockBeautyApi.sendFeedback({ ...ok, message: "x".repeat(2001) })).rejects.toMatchObject({ code: "invalid_message" });
    await expect(mockBeautyApi.sendFeedback({ ...ok, pageUrl: "/a?b=1" })).rejects.toMatchObject({ status: 422, code: "invalid_page_url" });
  });

  it("ліміт 5 на годину: 6-те -> 429 з Retry-After; невалідні запити ліміт не витрачають", async () => {
    for (let i = 0; i < 3; i++) await expect(mockBeautyApi.sendFeedback({ ...ok, message: "коротко" })).rejects.toBeDefined();
    for (let i = 0; i < 5; i++) await mockBeautyApi.sendFeedback({ ...ok });
    const err = await mockBeautyApi.sendFeedback({ ...ok }).catch((e) => e);
    expect(err).toMatchObject({ status: 429, code: "rate_limited" });
    expect(err.retryAfterSeconds).toBeGreaterThan(3590);
    expect(err.retryAfterSeconds).toBeLessThanOrEqual(3600);
  });
});
