import { BookingApiError } from "@beauty/booking-core";
import { decideLocationSource, resolveStart, type ResolvedLocation, type StartDeps } from "../locationSource";

const loc = (code: string): ResolvedLocation => ({ publicCode: code, tenantSlug: "salon-a", locationId: "l1", name: "Salon", timezone: "Europe/Kyiv" });

describe("decideLocationSource precedence", () => {
  it("deep link beats everything", () => {
    expect(decideLocationSource({ deepLinkCode: "AAAA0000", savedCode: "BBBB1111", geo: "granted" })).toEqual({ kind: "deeplink", code: "AAAA0000" });
  });
  it("saved beats geolocation", () => {
    expect(decideLocationSource({ deepLinkCode: null, savedCode: "BBBB1111", geo: "undetermined" })).toEqual({ kind: "saved", code: "BBBB1111" });
  });
  it("changing skips saved", () => {
    expect(decideLocationSource({ deepLinkCode: null, savedCode: "BBBB1111", geo: "undetermined", changing: true })).toEqual({ kind: "nearby" });
  });
  it("nearby when no saved and permission not denied", () => {
    expect(decideLocationSource({ deepLinkCode: null, savedCode: null, geo: "undetermined" })).toEqual({ kind: "nearby" });
    expect(decideLocationSource({ deepLinkCode: null, savedCode: null, geo: "granted" })).toEqual({ kind: "nearby" });
  });
  it("manual when permission denied", () => {
    expect(decideLocationSource({ deepLinkCode: null, savedCode: null, geo: "denied" })).toEqual({ kind: "manual" });
  });
});

describe("resolveStart", () => {
  const notFound = new BookingApiError(404, "not_found");
  const make = (resolve: StartDeps["resolve"]) => {
    const clearSaved = jest.fn(async () => {});
    const deps: StartDeps = { resolve, clearSaved, isNotFound: (e) => e instanceof BookingApiError && e.status === 404 };
    return { deps, clearSaved };
  };

  it("resolves a saved location", async () => {
    const { deps, clearSaved } = make(async (c) => loc(c));
    const r = await resolveStart({ deepLinkCode: null, savedCode: "BBBB1111", geo: "undetermined" }, deps);
    expect(r).toMatchObject({ kind: "resolved", via: "saved" });
    expect(clearSaved).not.toHaveBeenCalled();
  });

  it("clears a saved location that no longer resolves and falls through to nearby", async () => {
    const { deps, clearSaved } = make(async () => {
      throw notFound;
    });
    const r = await resolveStart({ deepLinkCode: null, savedCode: "BBBB1111", geo: "undetermined" }, deps);
    expect(clearSaved).toHaveBeenCalledTimes(1);
    expect(r).toEqual({ kind: "nearby" });
  });

  it("falls through to manual when geo is denied", async () => {
    const { deps } = make(async () => {
      throw notFound;
    });
    expect(await resolveStart({ deepLinkCode: null, savedCode: "BBBB1111", geo: "denied" }, deps)).toEqual({ kind: "manual" });
  });

  it("reports an unknown deep link without clearing the saved location", async () => {
    const { deps, clearSaved } = make(async () => {
      throw notFound;
    });
    const r = await resolveStart({ deepLinkCode: "AAAA0000", savedCode: "BBBB1111", geo: "granted" }, deps);
    expect(r).toEqual({ kind: "not_found", code: "AAAA0000" });
    expect(clearSaved).not.toHaveBeenCalled();
  });

  it("does not clear the saved location on network errors", async () => {
    const { deps, clearSaved } = make(async () => {
      throw new BookingApiError(0, "api_unreachable");
    });
    await expect(resolveStart({ deepLinkCode: null, savedCode: "BBBB1111", geo: "granted" }, deps)).rejects.toBeInstanceOf(BookingApiError);
    expect(clearSaved).not.toHaveBeenCalled();
  });
});

describe("needsSwitchConfirmation (L7)", () => {
  const { needsSwitchConfirmation } = require("../locationSource");
  it("no saved location -> no confirmation", () => {
    expect(needsSwitchConfirmation(null, "AAAA0000")).toBe(false);
    expect(needsSwitchConfirmation(undefined, "AAAA0000")).toBe(false);
  });
  it("same location -> no confirmation", () => {
    expect(needsSwitchConfirmation("AAAA0000", "AAAA0000")).toBe(false);
  });
  it("different location -> confirmation", () => {
    expect(needsSwitchConfirmation("BBBB1111", "AAAA0000")).toBe(true);
  });
});
