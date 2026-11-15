import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { LocationLanding, LocationLinkInvalid } from "@/features/beauty-booking/components/LocationLanding";
import { normalizePublicCode, resolveLocationCode, toPublicLocationRef } from "./location-code";
import { buildAppleAppSiteAssociation, buildAssetLinks, wellKnownResponse } from "./wellknown";

const ref = {
  publicCode: "7K2M9QXA",
  tenantSlug: "beauty-lab",
  locationId: "11111111-1111-1111-1111-111111111111",
  name: "Beauty Lab",
  address: "Main St 1",
  city: "Київ",
  timezone: "Europe/Kyiv",
};

describe("normalizePublicCode", () => {
  it("нормалізує регістр, дефіс і аліаси", () => {
    expect(normalizePublicCode("7k2m-9qxa")).toBe("7K2M9QXA");
    expect(normalizePublicCode("OIL0OIL0")).toBe("01100110");
  });
  it("відхиляє хибний формат", () => {
    for (const v of ["", "short", "7K2M9QXAA", "7K2M9QXU", "../etc/pw", null, undefined]) {
      expect(normalizePublicCode(v as string)).toBeNull();
    }
  });
});

describe("resolveLocationCode", () => {
  const mk = (status: number, body?: unknown) =>
    vi.fn(async () => new Response(body === undefined ? null : JSON.stringify(body), { status })) as unknown as typeof fetch;

  it("ok: лишає тільки відомі поля", async () => {
    const r = await resolveLocationCode("7K2M9QXA", {
      baseUrl: "http://api/",
      fetchImpl: mk(200, { ...ref, tenantId: "secret", phone: "+380" }),
    });
    expect(r.status).toBe("ok");
    expect(r.status === "ok" && Object.keys(r.location)).not.toContain("tenantId");
  });
  it("404 -> not_found", async () => {
    expect((await resolveLocationCode("7K2M9QXA", { baseUrl: "http://api", fetchImpl: mk(404) })).status).toBe("not_found");
  });
  it("5xx, хибна відповідь, виняток, без baseUrl -> error", async () => {
    expect((await resolveLocationCode("7K2M9QXA", { baseUrl: "http://api", fetchImpl: mk(500) })).status).toBe("error");
    expect((await resolveLocationCode("7K2M9QXA", { baseUrl: "http://api", fetchImpl: mk(200, { x: 1 }) })).status).toBe("error");
    const boom = vi.fn(async () => {
      throw new Error("net");
    }) as unknown as typeof fetch;
    expect((await resolveLocationCode("7K2M9QXA", { baseUrl: "http://api", fetchImpl: boom })).status).toBe("error");
    expect((await resolveLocationCode("7K2M9QXA", { baseUrl: "", fetchImpl: mk(200, ref) })).status).toBe("error");
  });
  it("toPublicLocationRef відхиляє небезпечний slug", () => {
    expect(toPublicLocationRef({ ...ref, tenantSlug: "../x" })).toBeNull();
  });
});

describe("well-known", () => {
  it("AASA: 404 без APPLE_TEAM_ID, default bundle, paths /l/*", () => {
    expect(buildAppleAppSiteAssociation({})).toBeNull();
    const body = JSON.stringify(buildAppleAppSiteAssociation({ APPLE_TEAM_ID: "TEAM123456" }));
    expect(body).toContain("TEAM123456.com.beautycrm.booking");
    expect(body).toContain('"paths":["/l/*"]');
    expect(JSON.stringify(buildAppleAppSiteAssociation({ APPLE_TEAM_ID: "T", IOS_BUNDLE_ID: "x.y" }))).toContain("T.x.y");
  });
  it("assetlinks: потрібні package і відбитки", () => {
    expect(buildAssetLinks({ ANDROID_PACKAGE: "com.a" })).toBeNull();
    expect(buildAssetLinks({ ANDROID_SHA256_CERT_FINGERPRINTS: "AA:BB" })).toBeNull();
    const out = buildAssetLinks({ ANDROID_PACKAGE: "com.a", ANDROID_SHA256_CERT_FINGERPRINTS: "AA:BB, CC:DD" }) as Array<{
      target: { sha256_cert_fingerprints: string[] };
    }>;
    expect(out[0].target.sha256_cert_fingerprints).toEqual(["AA:BB", "CC:DD"]);
  });
  it("відповідь: 404 або JSON", () => {
    expect(wellKnownResponse(null).status).toBe(404);
    const ok = wellKnownResponse({ a: 1 });
    expect(ok.status).toBe(200);
    expect(ok.headers.get("Content-Type")).toBe("application/json");
  });
});

describe("LocationLanding", () => {
  it("показує заклад і всі кнопки", () => {
    render(<LocationLanding location={ref} iosStoreUrl="https://apps.apple.com/x" androidStoreUrl="https://play.google.com/y" />);
    expect(screen.getByRole("heading", { name: "Beauty Lab" })).toBeTruthy();
    expect(screen.getByText("Main St 1, Київ")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Відкрити в застосунку" }).getAttribute("href")).toBe("beautycrm://l/7K2M9QXA");
    expect(screen.getByRole("link", { name: "Завантажити в App Store" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Завантажити в Google Play" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Записатися онлайн" }).getAttribute("href")).toBe("/book/beauty-lab");
  });
  it("ховає кнопки магазинів без env (або не https)", () => {
    render(<LocationLanding location={ref} iosStoreUrl="javascript:alert(1)" />);
    expect(screen.queryByText("Завантажити в App Store")).toBeNull();
    expect(screen.queryByText("Завантажити в Google Play")).toBeNull();
  });
  it("недійсне посилання", () => {
    render(<LocationLinkInvalid />);
    expect(screen.getByRole("heading", { name: "Посилання недійсне" })).toBeTruthy();
  });
});
