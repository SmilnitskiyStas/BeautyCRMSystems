import { describe, expect, it } from "vitest";
import { codeHint, formatCode, generatePersonalCode, hashCode, normalizeCode } from "./coupon-code";

// TASK-779: cross-check with the .NET CouponCodes (backend/BeautyCrm.Tests: CampaignTests.coupon_hash_golden_vector_matches_worker
// and CouponTests). The same fixed code must give the same hash on both sides, otherwise the API cannot find worker-issued coupons.
describe("coupon code golden vector (.NET <-> Node)", () => {
  it("hashes the fixed code to the value pinned in the .NET test", () => {
    expect(hashCode("abcde-12345")).toBe("60ca79e65ce75a6d75b70701e1316a2687f621fe41be01cd4fda45234af1987b");
    expect(hashCode("ABCDE12345")).toBe(hashCode(" abcde-12345 "));
  });

  it("normalizes like .NET (upper case, no spaces or dashes, O->0, I|L->1)", () => {
    expect(normalizeCode(" abcde-12345 ")).toBe("ABCDE12345");
    expect(normalizeCode("o-i-l")).toBe("011");
    expect(codeHint("ABCDE-12345")).toBe("345");
    expect(formatCode("ABCDEFGHJK")).toBe("ABCDE-FGHJK");
  });

  it("generates 10 Crockford symbols that survive normalization unchanged", () => {
    for (let i = 0; i < 50; i++) {
      const code = generatePersonalCode();
      expect(code).toMatch(/^[0-9A-HJKMNP-TV-Z]{10}$/);
      expect(normalizeCode(code)).toBe(code);
    }
  });
});
