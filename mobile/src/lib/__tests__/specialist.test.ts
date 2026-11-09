import { experienceLabel, initials, ratingLabel, reviewsWord } from "../specialist";

describe("specialist helpers", () => {
  it("experienceLabel: правильна форма", () => {
    expect(experienceLabel(1)).toBe("1 рік досвіду");
    expect(experienceLabel(2)).toBe("2 роки досвіду");
    expect(experienceLabel(4)).toBe("4 роки досвіду");
    expect(experienceLabel(5)).toBe("5 років досвіду");
    expect(experienceLabel(11)).toBe("11 років досвіду");
    expect(experienceLabel(21)).toBe("21 рік досвіду");
    expect(experienceLabel(22)).toBe("22 роки досвіду");
    expect(experienceLabel(112)).toBe("112 років досвіду");
  });
  it("experienceLabel: null/0 ховається", () => {
    expect(experienceLabel(null)).toBeNull();
    expect(experienceLabel(undefined)).toBeNull();
    expect(experienceLabel(0)).toBeNull();
  });
  it("ratingLabel / reviewsWord", () => {
    expect(ratingLabel(4.8, 23)).toBe("★ 4.8 · 23 відгуки");
    expect(ratingLabel(5, 1)).toBe("★ 5.0 · 1 відгук");
    expect(ratingLabel(null, 0)).toBeNull();
    expect(reviewsWord(12)).toBe("відгуків");
  });
  it("initials: фолбек аватара", () => {
    expect(initials("Анна Коваль")).toBe("АК");
    expect(initials("  олена ")).toBe("О");
    expect(initials("")).toBe("?");
  });
});
