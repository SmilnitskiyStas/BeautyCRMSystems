import { describe, expect, it, vi } from "vitest";
import { detailsCsv } from "./csv";
import { collectDetailsItems, detailsKey, detailsParams, targetOf } from "./details";
import { detailsTitle, pointFullLabel } from "./series";
import type { DetailsItem, DetailsSummary } from "../types";

const item = (n: number, over: Partial<DetailsItem> = {}): DetailsItem => ({
  appointmentId: `a${n}`,
  localDate: "2026-10-24",
  localTime: "10:00",
  location: { id: "L1", name: "Центр" },
  service: { id: "s1", name: "Манікюр" },
  specialist: { id: "m1", name: "Марина" },
  priceOriginal: 900,
  discount: 0,
  priceFinal: 900,
  cost: 220,
  masterPercent: 40,
  masterPayout: 360,
  net: 320,
  costMissing: false,
  ...over,
});

const summary: DetailsSummary = {
  appointments: 5, completed: 3, cancelled: 1, noShow: 1, revenue: 2000, discountTotal: 100, cost: 440, masterPayout: 720, netIncome: 840, marginPercent: 42, withoutCost: 1, revenueWithoutCost: 400,
};

describe("параметри деталей (§24.6.3)", () => {
  it("день -> date; тиждень/місяць -> from+to; locationId лише коли обрано", () => {
    expect(detailsParams(targetOf({ from: "2026-10-24", to: "2026-10-24" }, "day"))).toEqual({ date: "2026-10-24" });
    expect(detailsParams(targetOf({ from: "2026-10-26", to: "2026-11-01" }, "week"), "L1")).toEqual({ from: "2026-10-26", to: "2026-11-01", locationId: "L1" });
    expect(detailsParams(targetOf({ from: "2026-10-01", to: "2026-10-31" }, "month"), null)).toEqual({ from: "2026-10-01", to: "2026-10-31" });
  });
  it("ключ React Query: ['analytics-details', from, to, locationId]", () => {
    expect(detailsKey({ from: "a", to: "b", granularity: "week" }, null)).toEqual(["analytics-details", "a", "b", null]);
  });
  it("заголовки панелі", () => {
    expect(detailsTitle({ from: "2026-10-23", to: "2026-10-23" }, "day")).toBe("Деталі дня пт, 23.10");
    expect(detailsTitle({ from: "2026-10-27", to: "2026-11-02" }, "week")).toBe("Деталі тижня 27.10–02.11");
    expect(detailsTitle({ from: "2026-10-01", to: "2026-10-31" }, "month")).toBe("Деталі місяця жовтень");
    expect(pointFullLabel({ from: "2026-10-01", to: "2026-10-31" }, "month")).toBe("жовтень 2026");
  });
});

describe("довантаження сторінок для CSV (§24.7)", () => {
  it("послідовно за курсором, до кінця", async () => {
    const fetchPage = vi.fn().mockResolvedValueOnce({ items: [item(3)], hasMore: true, nextCursor: "c2" }).mockResolvedValueOnce({ items: [item(4)], hasMore: false });
    const r = await collectDetailsItems({ items: [item(1), item(2)], hasMore: true, nextCursor: "c1" }, fetchPage);
    expect(fetchPage.mock.calls.map((c) => c[0])).toEqual(["c1", "c2"]);
    expect(r.items.map((i) => i.appointmentId)).toEqual(["a1", "a2", "a3", "a4"]);
    expect(r.truncated).toBe(false);
  });
  it("понад ліміт: обрізається, truncated = true", async () => {
    const page = (from: number) => ({ items: Array.from({ length: 200 }, (_, k) => item(from + k)), hasMore: true, nextCursor: `c${from}` });
    const fetchPage = vi.fn(async (c: string) => page(Number(c.slice(1)) + 200));
    const r = await collectDetailsItems(page(0), fetchPage);
    expect(r.items).toHaveLength(2000);
    expect(r.truncated).toBe(true);
    expect(fetchPage).toHaveBeenCalledTimes(9); // 200 вже є + 9 × 200 = 2000
  });
  it("курсор, що не змінюється, не зациклює", async () => {
    const fetchPage = vi.fn().mockResolvedValue({ items: [item(9)], hasMore: true, nextCursor: "same" });
    const r = await collectDetailsItems({ items: [item(1)], hasMore: true, nextCursor: "same" }, fetchPage);
    expect(fetchPage).toHaveBeenCalledTimes(1);
    expect(r.truncated).toBe(true);
  });
  it("без hasMore сторінки не запитуються", async () => {
    const fetchPage = vi.fn();
    const r = await collectDetailsItems({ items: [item(1)], hasMore: false }, fetchPage);
    expect(fetchPage).not.toHaveBeenCalled();
    expect(r.truncated).toBe(false);
  });
});

describe("CSV деталей (§24.7)", () => {
  const lines = (c: ReturnType<typeof detailsCsv>) => c.content.split("\r\n");
  it("колонки, ім'я файлу, costMissing: порожні комірки + «так», рядок «Разом» з summary", () => {
    const f = detailsCsv(
      summary,
      [
        item(1, { discount: 100, priceOriginal: 1000, priceFinal: 900, promotion: { id: "p", name: "Осінь" } }),
        item(2, { cost: null, masterPercent: null, masterPayout: null, net: 900, costMissing: true }),
      ],
      { fromDate: "2026-10-24", toDate: "2026-10-24" },
    );
    expect(f.fileName).toBe("beauty-analytics-details-2026-10-24_2026-10-24.csv");
    const l = lines(f);
    expect(l[0]).toBe("﻿Дата;Час;Заклад;Послуга;Майстер;Ціна до знижки, ₴;Знижка, ₴;Акція;Ціна продажу, ₴;Собівартість, ₴;Виплата майстру, ₴;Чистий внесок, ₴;Без собівартості");
    expect(l[1]).toBe("2026-10-24;10:00;Центр;Манікюр;Марина;1000,00;100,00;Осінь;900,00;220,00;360,00;320,00;");
    expect(l[2]).toBe("2026-10-24;10:00;Центр;Манікюр;Марина;900,00;0,00;;900,00;;;900,00;так");
    expect(l[3]).toBe("Разом;;;;;2100,00;100,00;;2000,00;440,00;720,00;840,00;1");
  });
  it("захист від CSV-injection для назв; від'ємний чистий внесок без префікса", () => {
    const f = detailsCsv(summary, [item(1, { service: { id: "s", name: "=SUM(A1)" }, specialist: { id: "m", name: "@x" }, net: -50 })], { fromDate: "2026-10-24", toDate: "2026-10-24" });
    const row = lines(f)[1];
    expect(row).toContain("'=SUM(A1)");
    expect(row).toContain("'@x");
    expect(row).toContain(";-50,00;");
  });
  it("неповний список: останній рядок «Неповний список: n з m»", () => {
    const f = detailsCsv({ ...summary, completed: 2500 }, [item(1), item(2)], { fromDate: "2026-10-01", toDate: "2026-10-31" }, true);
    expect(lines(f).at(-2)!.startsWith("Неповний список: 2 з 2500;")).toBe(true);
  });
  it("без персональних даних і GUID-ів: appointmentId не експортується", () => {
    const f = detailsCsv(summary, [item(1, { appointmentId: "11111111-2222-3333-4444-555555555555" })], { fromDate: "2026-10-24", toDate: "2026-10-24" });
    expect(f.content).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/);
    expect(lines(f)[0]).not.toMatch(/клієнт|телефон|email/i);
  });
});
