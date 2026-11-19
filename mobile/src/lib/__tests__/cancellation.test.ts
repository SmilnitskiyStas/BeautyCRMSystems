import { normalizeCancellation, type CancellationTerms } from "@beauty/booking-core";
import { cancellationLines } from "../format";

const t = (key: string, o?: Record<string, unknown>) => `${key}${o ? JSON.stringify(o) : ""}`;
const base: CancellationTerms = { windowHours: 12, refundPercentInWindow: 50, refundPercentOutside: 100, deductFee: false, feePercent: 0 };

describe("cancellationLines", () => {
  it("showToClient=false -> блок не показується", () => {
    expect(cancellationLines({ ...base, showToClient: false, customText: null }, t)).toEqual([]);
  });
  it("showToClient=false ігнорує навіть непорожній customText", () => {
    expect(cancellationLines({ ...base, showToClient: false, customText: "x" }, t)).toEqual([]);
  });
  it("власний текст замість автоматичних рядків, переноси збережено", () => {
    expect(cancellationLines({ ...base, customText: "Рядок 1\r\nРядок 2\n\nhttp://a.b" }, t)).toEqual(["Рядок 1", "Рядок 2", "", "http://a.b"]);
  });
  it("автоматичні рядки, якщо customText null", () => {
    const l = cancellationLines({ ...base, showToClient: true, customText: null }, t);
    expect(l).toHaveLength(2);
    expect(l[0]).toContain("cancellation.freeOutside");
  });
  it("старі відповіді без нових полів -> автоматичні рядки", () => {
    expect(cancellationLines(base, t)).toHaveLength(2);
  });
  it("порожній/пробільний customText -> автоматичні рядки", () => {
    expect(cancellationLines({ ...base, customText: "   \n " }, t)).toHaveLength(2);
    expect(cancellationLines({ ...base, customText: "" }, t)).toHaveLength(2);
  });
  it("cancellation відсутнє -> «уточнюйте в закладі»", () => {
    expect(cancellationLines(undefined, t)).toEqual(["cancellation.none"]);
    expect(cancellationLines(null, t)).toEqual(["cancellation.none"]);
  });
});

describe("normalizeCancellation", () => {
  it("старі відповіді: showToClient=true, customText=null", () => {
    expect(normalizeCancellation(base)).toEqual({ ...base, showToClient: true, customText: null });
  });
  it("trim і порожній -> null", () => {
    expect(normalizeCancellation({ ...base, customText: "  hi  " })?.customText).toBe("hi");
    expect(normalizeCancellation({ ...base, customText: "  " })?.customText).toBeNull();
  });
  it("showToClient=false -> customText=null", () => {
    expect(normalizeCancellation({ ...base, showToClient: false, customText: "x" })).toMatchObject({ showToClient: false, customText: null });
  });
  it("null/undefined -> undefined", () => {
    expect(normalizeCancellation(null)).toBeUndefined();
    expect(normalizeCancellation(undefined)).toBeUndefined();
  });
});
