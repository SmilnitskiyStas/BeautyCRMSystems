import { describe, expect, it } from "vitest";
import { isValidTimeZone, timeZoneOptions, validateLocation } from "./locations-logic";

const ok = { name: "Центр", address: "", phone: "", timezone: "Europe/Kyiv", isActive: true };

describe("часові зони", () => {
  it("приймає IANA-зони й відхиляє сміття", () => {
    expect(isValidTimeZone("Europe/Kyiv")).toBe(true);
    expect(isValidTimeZone("America/Argentina/Buenos_Aires")).toBe(true);
    expect(isValidTimeZone("UTC")).toBe(true);
    expect(isValidTimeZone("Kyiv")).toBe(false);
    expect(isValidTimeZone("Mars/Olympus")).toBe(false);
    expect(isValidTimeZone("+03:00")).toBe(false);
    expect(isValidTimeZone("")).toBe(false);
  });
  it("список для пошуку містить Europe/Kyiv", () => {
    expect(timeZoneOptions()).toContain("Europe/Kyiv");
  });
});

describe("validateLocation", () => {
  it("коректні дані - без помилок", () => {
    expect(validateLocation(ok)).toEqual({});
  });
  it("назва 2-100, адреса ≤200, телефон, зона", () => {
    expect(validateLocation({ ...ok, name: "A" }).name).toBeTruthy();
    expect(validateLocation({ ...ok, name: "x".repeat(101) }).name).toBeTruthy();
    expect(validateLocation({ ...ok, address: "x".repeat(201) }).address).toBeTruthy();
    expect(validateLocation({ ...ok, phone: "abc" }).phone).toBeTruthy();
    expect(validateLocation({ ...ok, phone: "+380 44 000 11 22" }).phone).toBeUndefined();
    expect(validateLocation({ ...ok, timezone: "Nowhere" }).timezone).toBeTruthy();
  });
});
