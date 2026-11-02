import { describe, expect, it } from "vitest";
import { codePointLength, normalizePrompt, promptForSave, promptLength } from "./prompt";

describe("нормалізація промпту (дзеркало сервера §20.6)", () => {
  it("CRLF/CR -> LF, NFC, прибирає керівні й zero-width/bidi, не більше 2 порожніх рядків, trim", () => {
    expect(normalizePrompt("  a\r\nb\rc​‮d\u0007\n\n\n\n\ne  ")).toBe("a\nb\ncd\n\n\ne");
    expect(normalizePrompt("é")).toBe("é");
    expect(normalizePrompt("a\tb")).toBe("a\tb");
  });
  it("довжина в кодових точках: емодзі = 1", () => {
    expect(codePointLength("😀😀")).toBe(2);
    expect(promptLength("😀".repeat(4000))).toBe(4000);
    expect(promptLength("  x \n")).toBe(1);
  });
  it("promptForSave: порожнє або типове -> null, інакше нормалізований текст", () => {
    expect(promptForSave("  ", "Типовий")).toBeNull();
    expect(promptForSave("Типовий\r\n", "Типовий")).toBeNull();
    expect(promptForSave(" Свій ", "Типовий")).toBe("Свій");
  });
});
