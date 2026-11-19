import { locationAddress, pickOption, specialistRatingText } from "../summary";
import { slotLabel } from "@/lib/dates";
import { REMINDER_OPTIONS } from "../checkoutForm";

describe("summary helpers", () => {
  it("рейтинг і відгуки", () => {
    expect(specialistRatingText(4.8, 4, "Ще немає відгуків")).toBe("★ 4.8 · 4 відгуки");
    expect(specialistRatingText(null, 0, "Ще немає відгуків")).toBe("Ще немає відгуків");
    expect(specialistRatingText(undefined, undefined, "Ще немає відгуків")).toBe("Ще немає відгуків");
  });
  it("адреса", () => {
    expect(locationAddress("вул. Хрещатик, 1", "Київ")).toBe("Київ, вул. Хрещатик, 1");
    expect(locationAddress("Київ, Хрещатик 1", "Київ")).toBe("Київ, Хрещатик 1");
    expect(locationAddress(null, "Львів")).toBe("Львів");
    expect(locationAddress(undefined, undefined)).toBe("");
  });
  it("вибір нагадування", () => {
    expect(pickOption(REMINDER_OPTIONS, "none", "2h")).toBe("2h");
    expect(pickOption(REMINDER_OPTIONS, "1h", "bogus")).toBe("1h");
  });
  it("підпис дати й часу", () => {
    expect(slotLabel("2026-11-11T10:00:00+02:00")).toBe("ср, 11 листопада · 10:00");
    expect(slotLabel("bad")).toBe("bad");
  });
});
