import { describe, expect, it } from "vitest";
import { cancelledByLabel, cancelledByShort } from "./cancellation";

describe("cancelledByLabel (§16)", () => {
  it("клієнт і система - без імені", () => {
    expect(cancelledByLabel({ type: "client" })).toBe("Скасував: клієнт");
    expect(cancelledByLabel({ type: "system" })).toBe("Скасував: система");
  });
  it("адміністратор: ім'я лише якщо API його віддав", () => {
    expect(cancelledByLabel({ type: "staff", name: "Світлана К." })).toBe("Скасував: адміністратор (Світлана К.)");
    expect(cancelledByLabel({ type: "staff" })).toBe("Скасував: адміністратор");
    expect(cancelledByLabel({ type: "staff", name: "  " })).toBe("Скасував: адміністратор");
  });
  it("без даних - нейтральний підпис; коротка форма без імені", () => {
    expect(cancelledByLabel(undefined)).toBe("Скасовано");
    expect(cancelledByShort({ type: "staff", name: "Світлана К." })).toBe("Скасував: адміністратор");
  });
});
