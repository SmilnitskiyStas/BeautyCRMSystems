import { describe, expect, it } from "vitest";
import { coordinateText, parseCoordinates } from "./locations-logic";

describe("parseCoordinates (§28.11)", () => {
  it("порожня пара - координат немає, помилок немає", () => {
    expect(parseCoordinates("", "  ")).toEqual({ latitude: null, longitude: null, errors: {} });
  });

  it("крапка і кома, округлення до 6 знаків", () => {
    expect(parseCoordinates("50,4501234567", "30.5234")).toMatchObject({ latitude: 50.450123, longitude: 30.5234, errors: {} });
    expect(parseCoordinates("-33.8", "+151.2")).toMatchObject({ latitude: -33.8, longitude: 151.2 });
  });

  it("лише одне поле - помилка на порожньому", () => {
    expect(parseCoordinates("50.4", "").errors.longitude).toBeTruthy();
    expect(parseCoordinates("", "30.5").errors.latitude).toBeTruthy();
  });

  it("поза діапазоном і не число", () => {
    expect(parseCoordinates("91", "30").errors.latitude).toMatch(/−90 до 90/);
    expect(parseCoordinates("50", "-180.1").errors.longitude).toMatch(/−180 до 180/);
    expect(parseCoordinates("abc", "30").errors.latitude).toBeTruthy();
    expect(parseCoordinates("1e3", "30").errors.latitude).toBeTruthy();
    expect(parseCoordinates("Infinity", "30").errors.latitude).toBeTruthy();
  });

  it("межі дозволені", () => {
    expect(parseCoordinates("90", "-180").errors).toEqual({});
  });

  it("coordinateText", () => {
    expect(coordinateText(50.45)).toBe("50.45");
    expect(coordinateText(null)).toBe("");
    expect(coordinateText(undefined)).toBe("");
  });
});
