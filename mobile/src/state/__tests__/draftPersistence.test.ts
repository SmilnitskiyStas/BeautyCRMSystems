import { draftKey, restorableDate, resumeStep, toBookingDraft } from "../draftPersistence";

const location = { publicCode: "7K2M9QXA", tenantSlug: "salon-a", locationId: "loc-1", name: "Salon A", timezone: "Europe/Kyiv" };
const service = { id: "svc-1", name: "Манікюр", durationMinutes: 60, priceOriginal: 500, priceFinal: 450 };
const specialist = { id: "sp-1", name: "Анна", role: "Майстер", nextFree: "mock-only" };

describe("booking draft persistence", () => {
  it("is not a draft until a service is chosen", () => {
    expect(toBookingDraft({ location: null, service: null, specialist: null, date: null })).toBeNull();
    expect(toBookingDraft({ location, service: null, specialist: null, date: null })).toBeNull();
  });

  it("captures venue, service, specialist and date without mock-only fields", () => {
    const d = toBookingDraft({ location, service, specialist, date: "2026-12-01" }, 1000)!;
    expect(d.publicCode).toBe("7K2M9QXA");
    expect(d.locationName).toBe("Salon A");
    expect(d.service.id).toBe("svc-1");
    expect(d.specialist?.id).toBe("sp-1");
    expect(JSON.stringify(d)).not.toContain("mock-only");
    expect(d.date).toBe("2026-12-01");
    expect(d.updatedAt).toBe(1000);
  });

  it("changes key only when a meaningful step changes", () => {
    const a = toBookingDraft({ location, service, specialist: null, date: null }, 1);
    const b = toBookingDraft({ location, service, specialist: null, date: null }, 2);
    const c = toBookingDraft({ location, service, specialist, date: null }, 3);
    expect(draftKey(a)).toBe(draftKey(b));
    expect(draftKey(a)).not.toBe(draftKey(c));
    expect(draftKey(null)).toBe("");
  });

  it("drops a past date and resumes at the specialist step", () => {
    expect(restorableDate("2026-11-20", "2026-11-22")).toBeNull();
    expect(restorableDate("2026-11-22", "2026-11-22")).toBe("2026-11-22");
    expect(resumeStep({ date: "2026-11-20" }, "2026-11-22")).toBe("/specialist");
    expect(resumeStep({ date: null }, "2026-11-22")).toBe("/specialist");
    expect(resumeStep({ date: "2026-11-25" }, "2026-11-22")).toBe("/time");
  });
});

describe("booking draft logo", () => {
  it("carries the location logo and null when absent", () => {
    const withLogo = toBookingDraft({ location: { ...location, logoUrl: "https://cdn.x/a.png" }, service, specialist: null, date: null }, 1)!;
    expect(withLogo.locationLogoUrl).toBe("https://cdn.x/a.png");
    expect(toBookingDraft({ location, service, specialist: null, date: null }, 1)!.locationLogoUrl).toBeNull();
  });
});
