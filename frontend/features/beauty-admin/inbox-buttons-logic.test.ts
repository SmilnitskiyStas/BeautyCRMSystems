import { describe, expect, it } from "vitest";
import { BeautyApiError, humanizeSyncCommandsError } from "@/features/beauty-auth/errors";
import { CLIENT_TG_REASON, isClientTelegramCancel } from "./cancellation";
import { buttonLabels, handoffReasonLabel, previewText, pressedText } from "./inbox-logic";

describe("підписи причин handoff (§23, TASK-741)", () => {
  it.each([
    ["policy_unknown", "питання про політику закладу"],
    ["reschedule_request", "прохання перенести запис"],
    ["cancel_limit", "перевищено ліміт скасувань за добу"],
    ["human_requested", "клієнт попросив людину"],
    ["booking_limit", "ліміт записів"],
    ["cancel_failed", "не вдалося скасувати запис"],
    ["booking_failed", "не вдалося створити запис"],
  ])("%s -> %s", (code, label) => {
    expect(handoffReasonLabel(code)).toBe(label);
  });
  it("невідомий код - «інша причина», не сирий код", () => {
    expect(handoffReasonLabel("brand_new_code")).toBe("інша причина");
  });
});

describe("натискання кнопок", () => {
  it("«Клієнт натиснув: {підпис}» з pressedLabel, інакше з префікса body; не callback - null", () => {
    expect(pressedText({ kind: "callback", body: "[кнопка] Так", pressedLabel: "✅ Так, записати" })).toBe("Клієнт натиснув: ✅ Так, записати");
    expect(pressedText({ kind: "callback", body: "[кнопка] Так" })).toBe("Клієнт натиснув: Так");
    expect(pressedText({ kind: "callback", body: "[кнопка]" })).toBe("Клієнт натиснув кнопку");
    expect(pressedText({ kind: "text", body: "Так" })).toBeNull();
  });
  it("превʼю списку не показує сирий префікс", () => {
    expect(previewText("[кнопка] Підтверджую візит")).toBe("Клієнт натиснув: Підтверджую візит");
    expect(previewText("Звичайний текст")).toBe("Звичайний текст");
  });
  it("підписи кнопок: лише непорожні рядки, до 10", () => {
    expect(buttonLabels({})).toEqual([]);
    expect(buttonLabels({ buttons: ["А", " ", "Б"] })).toEqual(["А", "Б"]);
    expect(buttonLabels({ buttons: Array.from({ length: 14 }, (_, i) => `K${i}`) })).toHaveLength(10);
    expect(buttonLabels({ buttons: [1, null, "ok"] as unknown as string[] })).toEqual(["ok"]);
  });
});

describe("скасування клієнтом у Telegram", () => {
  it("розпізнається за типом client і стандартною причиною", () => {
    expect(isClientTelegramCancel({ cancelledBy: { type: "client" }, cancelReason: CLIENT_TG_REASON })).toBe(true);
    expect(isClientTelegramCancel({ cancelledBy: { type: "staff" }, cancelReason: CLIENT_TG_REASON })).toBe(false);
    expect(isClientTelegramCancel({ cancelledBy: { type: "client" }, cancelReason: "Змінились плани" })).toBe(false);
    expect(isClientTelegramCancel({ cancelledBy: { type: "client" } })).toBe(false);
  });
});

describe("помилки «Оновити меню команд»", () => {
  const e = (status: number, code: string) => new BeautyApiError(status, code, "english server text with bot123:SECRET");
  it("404/409/502 українською, без тексту сервера", () => {
    const texts = [e(404, "channel_not_found"), e(409, "channel_unsupported"), e(409, "channel_inactive"), e(409, "token_missing"), e(502, "telegram_unavailable"), e(502, "")].map(humanizeSyncCommandsError);
    for (const t of texts) {
      expect(t).toMatch(/[а-яіїєґ]/i);
      expect(t).not.toMatch(/english|SECRET|bot123/);
    }
    expect(new Set(texts.slice(0, 5)).size).toBe(5);
    expect(texts[5]).toBe(texts[4]);
    expect(humanizeSyncCommandsError(e(403, "forbidden_role"))).toBe("У вас немає прав на цю дію.");
  });
});
