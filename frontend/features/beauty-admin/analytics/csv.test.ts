import { describe, expect, it } from "vitest";
import { csvFileName, date, defuse, escapeField, int, locationCsv, money, networkCsv, pct, promotionsCsv, serializeCsv, text } from "./csv";
import type { LocationBreakdown, LocationStat, NetworkAnalytics, NetworkSeries, PromotionStat } from "../types";

describe("серіалізатор CSV (§19.5)", () => {
  it("BOM, роздільник `;`, CRLF у кінці кожного рядка", () => {
    const out = serializeCsv(["А", "Б"], [[text("x"), int(1)]]);
    expect(out.charCodeAt(0)).toBe(0xfeff);
    expect(out).toBe("﻿А;Б\r\nx;1\r\n");
    expect(Array.from(new TextEncoder().encode(out).slice(0, 3))).toEqual([0xef, 0xbb, 0xbf]);
  });
  it("порожні дані - лише заголовки", () => {
    expect(serializeCsv(["А"], [])).toBe("﻿А\r\n");
  });
  it("екранування лапок, `;`, переносу рядка та пробілів по краях", () => {
    expect(escapeField('a"b')).toBe('"a""b"');
    expect(escapeField("a;b")).toBe('"a;b"');
    expect(escapeField("a\nb")).toBe('"a\nb"');
    expect(escapeField(" a")).toBe('" a"');
    expect(escapeField("a ")).toBe('"a "');
    expect(escapeField("звичайний")).toBe("звичайний");
  });
  it("CSV-injection: = + - @ TAB CR на початку тексту -> префікс апостроф", () => {
    for (const v of ["=SUM(A1)", "+1", "-x", "@x", "\tx", "\rx"]) {
      expect(defuse(v)).toBe(`'${v}`);
    }
    expect(serializeCsv(["Н"], [[text("=SUM(A1)")]])).toBe("﻿Н\r\n'=SUM(A1)\r\n");
    // TAB усередині поля не потребує лапок, CR - потребує
    expect(serializeCsv(["Н"], [[text("\tx")]])).toBe("﻿Н\r\n'\tx\r\n");
    expect(serializeCsv(["Н"], [[text("\rx")]])).toBe("﻿Н\r\n\"'\rx\"\r\n");
    expect(defuse("Манікюр")).toBe("Манікюр");
    expect(defuse("a=b")).toBe("a=b");
  });
  it("префікс додається до екранування (текст з лапкою й `;`)", () => {
    expect(serializeCsv(["Н"], [[text('=HYPERLINK("x";"y")')]])).toBe('﻿Н\r\n"\'=HYPERLINK(""x"";""y"")"\r\n');
  });
  it("числа: від'ємне лишається числом без префікса, гроші з комою, відсотки 1 знак, null порожньо", () => {
    expect(serializeCsv(["a"], [[int(-5), money(1234.5), pct(12.34), pct(null), int(null), money(null), date("2026-10-01")]])).toBe("﻿a\r\n-5;1234,50;12,3;;;;2026-10-01\r\n");
  });
});

const net: NetworkAnalytics = {
  fromDate: "2026-10-01",
  toDate: "2026-10-02",
  timezone: "Europe/Kyiv",
  totals: { appointments: 4, completed: 2, cancelled: 1, noShow: 1, revenue: 1500, averageCheck: 750, cancellationRate: 0.25, newClients: 1 },
  previous: { fromDate: "2026-09-29", toDate: "2026-09-30", appointments: 2, completed: 0, cancelled: 2, noShow: 0, revenue: 0, averageCheck: 0, cancellationRate: 1, newClients: 0 },
};
const series: NetworkSeries = {
  fromDate: "2026-10-01",
  toDate: "2026-10-02",
  timezone: "Europe/Kyiv",
  granularity: "day",
  points: [
    { from: "2026-10-01", to: "2026-10-01", appointments: 3, completed: 2, cancelled: 1, noShow: 0, revenue: 1500 },
    { from: "2026-10-02", to: "2026-10-02", appointments: 1, completed: 0, cancelled: 0, noShow: 1, revenue: 0 },
  ],
};

describe("файли експорту", () => {
  it("ім'я файлу з діапазоном", () => {
    expect(csvFileName("network", "2026-10-01", "2026-10-24")).toBe("beauty-analytics-network-2026-10-01_2026-10-24.csv");
  });
  it("мережа: точки + Разом; з compare - Попередній період і Зміна %", () => {
    const plain = networkCsv(net, series, false);
    expect(plain.fileName).toBe("beauty-analytics-network-2026-10-01_2026-10-02.csv");
    const lines = plain.content.split("\r\n");
    expect(lines[0]).toBe("﻿Від;До;Записів;Завершено;Скасовано;Не прийшли;Виручка, ₴;Собівартість, ₴;Виплата майстрам, ₴;Чистий дохід, ₴;Маржа, %;Без собівартості");
    // Фінансових полів у фікстурі немає -> порожні комірки (не нулі).
    expect(lines[1]).toBe("2026-10-01;2026-10-01;3;2;1;0;1500,00;;;;;");
    expect(lines[3]).toBe("Разом;2026-10-02;4;2;1;1;1500,00;;;;;");
    expect(lines).toHaveLength(5); // + порожній хвіст після останнього CRLF

    const cmp = networkCsv(net, series, true).content.split("\r\n");
    expect(cmp[4]).toBe("Попередній період;2026-09-30;2;0;2;0;0,00;;;;;");
    // appointments 4 vs 2 = +100; completed 2 vs 0 = порожньо (ділення на 0); cancelled 1 vs 2 = -50; noShow 1 vs 0; revenue 1500 vs 0
    expect(cmp[5]).toBe("Зміна, %;;100,0;;-50,0;;;;;;;");
  });
  it("заклад: спеціалісти + Разом, без персональних даних і GUID-ів", () => {
    const b: LocationBreakdown = {
      locationId: "11111111-1111-1111-1111-111111111111",
      name: "Центр",
      timezone: "Europe/Kyiv",
      fromDate: "2026-10-01",
      toDate: "2026-10-31",
      busyMinutes: 120,
      availableMinutes: 480,
      load: 0.25,
      specialists: [{ specialistId: "22222222-2222-2222-2222-222222222222", name: "=Марина", isActive: true, appointments: 2, completed: 1, cancelled: 1, noShow: 0, revenue: 900, busyMinutes: 120, availableMinutes: 480, load: 0.25 }],
      services: [],
    };
    const stat: LocationStat = { locationId: b.locationId, name: "Центр", timezone: "Europe/Kyiv", isActive: true, appointments: 2, completed: 1, cancelled: 1, noShow: 0, revenue: 900, busyMinutes: 120, availableMinutes: 480, load: 0.25, previous: null };
    const f = locationCsv(stat, b);
    expect(f.fileName).toBe("beauty-analytics-locations-2026-10-01_2026-10-31.csv");
    const lines = f.content.split("\r\n");
    expect(lines[1]).toBe("'=Марина;2;1;1;0;900,00;120;480;25,0");
    expect(lines[2]).toBe("Разом;2;1;1;0;900,00;120;480;25,0");
    expect(f.content).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/);
    expect(lines[0]).not.toMatch(/клієнт|телефон|email/i);
  });
  it("load = null -> порожня комірка", () => {
    const b: LocationBreakdown = { locationId: "x", name: "Ц", timezone: "UTC", fromDate: "2026-10-01", toDate: "2026-10-01", busyMinutes: 0, availableMinutes: 0, load: null, specialists: [], services: [] };
    expect(locationCsv(null, b).content.split("\r\n")[1]).toBe("Разом;0;0;0;0;0,00;0;0;");
  });
  it("акції: колонки порівняння додаються лише з compare", () => {
    const rows: PromotionStat[] = [{ id: "g", name: "Осінь", period: "постійна", where: "Центр", uses: 3, discountTotal: 300, revenue: 1200, previous: { uses: 1, discountTotal: 100, revenue: 600 } }];
    expect(promotionsCsv(rows, "2026-10-01", "2026-10-31", false).content.split("\r\n")[0]).toBe("﻿Акція;Період дії;Заклади;Використань;Виручка, ₴;Віддано знижками, ₴");
    const c = promotionsCsv(rows, "2026-10-01", "2026-10-31", true).content.split("\r\n");
    expect(c[0]).toContain("Використань (попер.);Виручка, ₴ (попер.);Зміна виручки, %");
    expect(c[1]).toBe("Осінь;постійна;Центр;3;1200,00;300,00;1;600,00;100,0");
    expect(promotionsCsv([], "2026-10-01", "2026-10-31", false).content).toBe("﻿Акція;Період дії;Заклади;Використань;Виручка, ₴;Віддано знижками, ₴\r\n");
  });
});

describe("§24.7: мережевий CSV з фінансами", () => {
  const fin = { cost: 440, masterPayout: 720, netIncome: 840, marginPercent: 42, withoutCost: 1, revenueWithoutCost: 400 };
  const finNet: NetworkAnalytics = {
    ...net,
    totals: { ...net.totals, revenue: 2000, ...fin },
    previous: { ...net.previous!, revenue: 1000, cost: 400, masterPayout: 300, netIncome: 300, marginPercent: 30, withoutCost: 0, revenueWithoutCost: 0 },
  };
  const finSeries: NetworkSeries = {
    ...series,
    points: [
      { ...series.points[0], revenue: 2000, cost: 440, masterPayout: 720, netIncome: 840, withoutCost: 1 },
      { ...series.points[1], revenue: 0, cost: 0, masterPayout: 0, netIncome: 0, withoutCost: 0 },
    ],
  };
  it("точки, «Разом» і «Попередній період» мають нові колонки; маржа точки рахується клієнтом; нульова виручка -> порожня маржа", () => {
    const l = networkCsv(finNet, finSeries, true).content.split("\r\n");
    expect(l[0]).toContain("Виручка, ₴;Собівартість, ₴;Виплата майстрам, ₴;Чистий дохід, ₴;Маржа, %;Без собівартості");
    expect(l[1]).toBe("2026-10-01;2026-10-01;3;2;1;0;2000,00;440,00;720,00;840,00;42,0;1");
    expect(l[2]).toBe("2026-10-02;2026-10-02;1;0;0;1;0,00;0,00;0,00;0,00;;0");
    expect(l[3]).toBe("Разом;2026-10-02;4;2;1;1;2000,00;440,00;720,00;840,00;42,0;1");
    expect(l[4]).toBe("Попередній період;2026-09-30;2;0;2;0;1000,00;400,00;300,00;300,00;30,0;0");
  });
  it("«Зміна, %»: гроші - відсотки, маржа - п.п., «Без собівартості» порожньо", () => {
    const l = networkCsv(finNet, finSeries, true).content.split("\r\n");
    // 440 vs 400 = +10%; 720 vs 300 = +140%; 840 vs 300 = +180%; маржа 42 - 30 = 12,0 п.п.
    expect(l[5]).toBe("Зміна, %;;100,0;;-50,0;;100,0;10,0;140,0;180,0;12,0;");
  });
});
