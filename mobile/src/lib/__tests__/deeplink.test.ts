import { normalizePublicCode, parseLocationInput, parseLocationLink, formatPublicCode } from "../deeplink";

const cfg = { webDomain: "app.beautycrm.example", scheme: "beautycrm" };

describe("normalizePublicCode", () => {
  it("normalizes case, separators and Crockford aliases", () => {
    expect(normalizePublicCode("7k2m-9qxa")).toBe("7K2M9QXA");
    expect(normalizePublicCode(" 7K2M 9QXA ")).toBe("7K2M9QXA");
    expect(normalizePublicCode("OIL0OIL0")).toBe("01100110");
  });
  it("rejects wrong length, forbidden chars and non-strings", () => {
    for (const bad of ["", "7K2M9QX", "7K2M9QXAA", "7K2M9QXU", "7K2M9Q!A", null, undefined]) {
      expect(normalizePublicCode(bad as string | null | undefined)).toBeNull();
    }
  });
  it("formats for humans", () => expect(formatPublicCode("7K2M9QXA")).toBe("7K2M-9QXA"));
});

describe("parseLocationLink", () => {
  it("parses custom scheme", () => {
    expect(parseLocationLink("beautycrm://l/7K2M9QXA", cfg)).toBe("7K2M9QXA");
    expect(parseLocationLink("beautycrm://l/7k2m-9qxa", cfg)).toBe("7K2M9QXA");
    expect(parseLocationLink("beautycrm://l/7K2M9QXA?utm=x#y", cfg)).toBe("7K2M9QXA");
  });
  it("parses https link on the configured domain only", () => {
    expect(parseLocationLink("https://app.beautycrm.example/l/7K2M9QXA", cfg)).toBe("7K2M9QXA");
    expect(parseLocationLink("https://APP.beautycrm.example/l/7K2M9QXA/", cfg)).toBe("7K2M9QXA");
    expect(parseLocationLink("https://evil.example/l/7K2M9QXA", cfg)).toBeNull();
    expect(parseLocationLink("https://app.beautycrm.example.evil.com/l/7K2M9QXA", cfg)).toBeNull();
    expect(parseLocationLink("https://user@app.beautycrm.example/l/7K2M9QXA", cfg)).toBeNull();
    expect(parseLocationLink("https://app.beautycrm.example:8443/l/7K2M9QXA", cfg)).toBeNull();
  });
  it("rejects http, other schemes, extra segments and bad codes", () => {
    expect(parseLocationLink("http://app.beautycrm.example/l/7K2M9QXA", cfg)).toBeNull();
    expect(parseLocationLink("javascript://l/7K2M9QXA", cfg)).toBeNull();
    expect(parseLocationLink("beautycrm://l/7K2M9QXA/extra", cfg)).toBeNull();
    expect(parseLocationLink("beautycrm://x/7K2M9QXA", cfg)).toBeNull();
    expect(parseLocationLink("beautycrm://l/short", cfg)).toBeNull();
    expect(parseLocationLink("beautycrm://l/%E0%A4%A", cfg)).toBeNull();
    expect(parseLocationLink(undefined, cfg)).toBeNull();
  });
});

describe("parseLocationInput", () => {
  it("accepts a link or a bare code", () => {
    expect(parseLocationInput("https://app.beautycrm.example/l/7K2M9QXA", cfg)).toBe("7K2M9QXA");
    expect(parseLocationInput("7K2M-9QXA", cfg)).toBe("7K2M9QXA");
    expect(parseLocationInput("hello", cfg)).toBeNull();
  });
});
