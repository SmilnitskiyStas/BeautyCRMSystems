import { describe, expect, it } from "vitest";
import { exceedsPrice, marginOf, netOf, payoutOf, roundHalfAway, sumMoney } from "./finance";
import { moneyExact, percentValue } from "../format";

describe("виплата майстру (§24.5): round half away from zero до копійок", () => {
  it("333.33 × 33.33% = 111.10 (111.0988…)", () => {
    expect(payoutOf(333.33, 33.33)).toBe(111.1);
  });
  it("1.00 × 0.5% = 0.005 -> 0.01", () => {
    expect(payoutOf(1, 0.5)).toBe(0.01);
  });
  it("акція 100%: ціна 0 -> виплата 0, net = −собівартість", () => {
    expect(payoutOf(0, 40)).toBe(0);
    expect(netOf(0, 150, 0)).toBe(-150);
  });
  it("без відсотка (null/0/undefined) виплати немає", () => {
    expect(payoutOf(900, null)).toBe(0);
    expect(payoutOf(900, 0)).toBe(0);
    expect(payoutOf(900, undefined)).toBe(0);
  });
  it("звичайні значення: 900 × 40% = 360", () => {
    expect(payoutOf(900, 40)).toBe(360);
    expect(payoutOf(720, 40)).toBe(288);
  });
});

describe("чистий внесок і маржа", () => {
  it("net = ціна − собівартість − виплата; може бути від'ємним; копійки без похибки", () => {
    expect(netOf(900, 220, 360)).toBe(320);
    expect(netOf(100, 150, 0)).toBe(-50);
    expect(netOf(0.3, 0.1, 0.1)).toBe(0.1);
  });
  it("відсутня собівартість рахується як 0", () => {
    expect(netOf(900, null, null)).toBe(900);
  });
  it("маржа: 1 знак, half away from zero, null при нульовій виручці", () => {
    expect(marginOf(320, 900)).toBe(35.6);
    expect(marginOf(0, 0)).toBeNull();
    expect(marginOf(-50, 100)).toBe(-50);
    expect(marginOf(1, 8)).toBe(12.5);
    expect(roundHalfAway(-2.25, 1)).toBe(-2.3);
    expect(roundHalfAway(2.25, 1)).toBe(2.3);
  });
  it("суми в копійках не накопичують похибку", () => {
    expect(sumMoney([0.1, 0.2, 0.3])).toBe(0.6);
  });
});

describe("попередження редактора", () => {
  it("собівартість + виплата більші за ціну", () => {
    expect(exceedsPrice(500, 300, 50)).toBe(true); // 300 + 250 = 550 > 500
    expect(exceedsPrice(500, 200, 50)).toBe(false); // 450
    expect(exceedsPrice(500, 250, 50)).toBe(false); // рівно 500 - не збитково
  });
});

describe("формат грошей і відсотків", () => {
  it("копійки лише коли є; мінус U+2212", () => {
    expect(moneyExact(900)).toBe("900 ₴");
    expect(moneyExact(111.1)).toBe("111,10 ₴");
    expect(moneyExact(-50)).toBe("−50 ₴");
    expect(moneyExact(-0.004)).toBe("0 ₴");
    expect(moneyExact(1234.5)).toMatch(/^1\s234,50 ₴$/);
  });
  it("відсоток із 1 знаком; null -> «—»", () => {
    expect(percentValue(35.6)).toBe("35,6%");
    expect(percentValue(40)).toBe("40%");
    expect(percentValue(null)).toBe("—");
    expect(percentValue(-12.5)).toBe("−12,5%");
  });
});
