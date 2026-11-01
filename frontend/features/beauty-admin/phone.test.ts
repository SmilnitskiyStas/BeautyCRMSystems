import { describe, expect, it } from "vitest";
import { contactChip, isCallbackPreview, maskLabel, previewText } from "./inbox-logic";
import { maskPhone, safeMask, telHref } from "./phone";

describe("маска телефону (§25.7)", () => {
  it("плюс і останні 2 цифри, решта крапки", () => {
    expect(maskPhone("+380501112233")).toBe("+••••••••••33");
    expect(maskPhone("+380 50 111 22 33")).toBe("+••••••••••33");
    expect(maskPhone("")).toBe("");
  });
  it("повний номер, що просочився з сервера, перемаскується", () => {
    expect(safeMask("+380501112233")).toBe("+••••••••••33");
    expect(safeMask("+••••••••••33")).toBe("+••••••••••33");
    expect(safeMask(undefined)).toBeNull();
  });
  it("tel: лише з + і цифрами", () => {
    expect(telHref("+380 (50) 111-22-33")).toBe("tel:+380501112233");
  });
});

describe("inbox: події контакту (§25.8)", () => {
  it("чипи за вердиктом; номера в тексті немає", () => {
    expect(contactChip({ kind: "contact", body: "[контакт]", contactVerdict: "own" })).toBe("Клієнт поділився своїм контактом");
    expect(contactChip({ kind: "contact", body: "[контакт]", contactVerdict: "foreign" })).toBe("Надіслано чужий контакт — відхилено");
    expect(contactChip({ kind: "contact", body: "[контакт]", contactVerdict: "invalid" })).toBe("Некоректний номер");
    expect(contactChip({ kind: "text", body: "[номер телефону]" })).toBe("Клієнт написав номер");
    expect(contactChip({ kind: "text", body: "Добрий день" })).toBeNull();
  });
  it("навіть якщо body контакту містить номер, чип його не показує", () => {
    expect(contactChip({ kind: "contact", body: "+380501112233", contactVerdict: "own" })).not.toMatch(/\d/);
  });
  it("превʼю в списку: події без префікса відправника", () => {
    expect(previewText("[контакт]")).toBe("Клієнт поділився контактом");
    expect(previewText("[номер телефону]")).toBe("Клієнт написав номер");
    expect(isCallbackPreview("[контакт]")).toBe(true);
    expect(isCallbackPreview("Привіт")).toBe(false);
  });
  it("підпис маски для зчитувачів", () => {
    expect(maskLabel("+••••••••••33")).toBe("Телефон приховано, закінчується на 33");
  });
});
