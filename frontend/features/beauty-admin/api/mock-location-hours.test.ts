import { beforeEach, describe, expect, it } from "vitest";
import type { BeautyApiError } from "@/features/beauty-auth/errors";
import { mockBeautyApi } from "./mock-client";

async function catchError(p: Promise<unknown>): Promise<BeautyApiError> {
  try {
    await p;
  } catch (e) {
    return e as BeautyApiError;
  }
  throw new Error("очікувалась помилка");
}

beforeEach(() => {
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
});

const hoursOf = async (id: string) => (await mockBeautyApi.getManagedLocations()).find((l) => l.id === id)?.workingHours;

describe("mock API: години роботи закладу (§18.1)", () => {
  it("демо: у Центру години задано, у Поділу - ні", async () => {
    expect(await hoursOf("c")).toBeTruthy();
    expect(await hoursOf("p")).toBeNull();
  });

  it("години, що залишають записи поза собою -> 409 + conflicts[] без клієнтських даних; зміни немає; confirm застосовує", async () => {
    const before = await hoursOf("c");
    const narrow = { mon: [{ from: "03:00", to: "04:00" }] };
    const err = await catchError(mockBeautyApi.setLocationWorkingHours("c", narrow));
    expect(err.status).toBe(409);
    expect(err.code).toBe("has_appointments_outside_hours");
    expect(err.conflicts!.length).toBeGreaterThan(0);
    for (const c of err.conflicts!) expect(Object.keys(c).sort()).toEqual(["appointmentId", "serviceName", "specialistName", "startsAt"]);
    expect(await hoursOf("c")).toEqual(before);

    expect(await mockBeautyApi.setLocationWorkingHours("c", narrow, true)).toEqual(narrow);
    expect(await hoursOf("c")).toEqual(narrow);
  });

  it("null скидає години без підтвердження", async () => {
    expect(await mockBeautyApi.setLocationWorkingHours("k", null)).toBeNull();
    expect(await hoursOf("k")).toBeNull();
  });

  it("некоректні години - 422, невідомий заклад - 404, specialist - 403", async () => {
    const bad = await catchError(mockBeautyApi.setLocationWorkingHours("p", { mon: [{ from: "18:00", to: "09:00" }] }));
    expect(bad.status).toBe(422);
    expect((await catchError(mockBeautyApi.setLocationWorkingHours("nope", null))).status).toBe(404);
    process.env.NEXT_PUBLIC_MOCK_ROLE = "specialist";
    expect((await catchError(mockBeautyApi.setLocationWorkingHours("p", null))).status).toBe(403);
  });
});
