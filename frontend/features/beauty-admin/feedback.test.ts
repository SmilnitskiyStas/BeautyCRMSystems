import { describe, expect, it } from "vitest";
import { BeautyApiError, humanizeFeedbackError } from "@/features/beauty-auth/errors";
import { buildFeedbackInput, FEEDBACK_DATA_NOTICE, FEEDBACK_DATA_NOTICE_MARKER, messageLength, pageUrlFrom, validateFeedbackMessage } from "./feedback";

describe("текст звернення: 10..2000 символів Unicode після trim", () => {
  it("межі", () => {
    expect(validateFeedbackMessage("123456789")).toMatch(/щонайменше 10/);
    expect(validateFeedbackMessage("1234567890")).toBeUndefined();
    expect(validateFeedbackMessage("   123456789   ")).toMatch(/щонайменше 10/);
    expect(validateFeedbackMessage("x".repeat(2000))).toBeUndefined();
    expect(validateFeedbackMessage("x".repeat(2001))).toMatch(/не більше 2000.*2001/);
  });
  it("рахує символи, а не UTF-16 одиниці", () => {
    expect(messageLength("😀".repeat(2000))).toBe(2000);
    expect(validateFeedbackMessage("😀".repeat(2000))).toBeUndefined();
    expect(validateFeedbackMessage("😀".repeat(2001))).toBeDefined();
  });
  it("службові символи відхиляються, переноси й табуляція дозволені", () => {
    expect(validateFeedbackMessage("рядок один\nрядок\tдва\r\n")).toBeUndefined();
    expect(validateFeedbackMessage("текст з нулем\u0000 ось")).toMatch(/службові/);
    expect(validateFeedbackMessage("текст з ESC\u001b ось")).toMatch(/службові/);
  });
});

describe("pageUrl: лише шлях", () => {
  it("відсікає query і fragment, відхиляє небезпечне", () => {
    expect(pageUrlFrom("/beauty/calendar")).toBe("/beauty/calendar");
    expect(pageUrlFrom("/beauty/calendar?week=1#x")).toBe("/beauty/calendar");
    expect(pageUrlFrom("//evil.com/x")).toBeUndefined();
    expect(pageUrlFrom("beauty/x")).toBeUndefined();
    expect(pageUrlFrom("/a b")).toBeUndefined();
    expect(pageUrlFrom("/" + "a".repeat(500))).toBeUndefined();
    expect(pageUrlFrom("/" + "a".repeat(499))).toHaveLength(500);
    expect(pageUrlFrom("")).toBeUndefined();
    expect(pageUrlFrom(null)).toBeUndefined();
  });
});

describe("buildFeedbackInput", () => {
  it("критичність лише для bug; інакше normal; текст обрізано; pageUrl додається", () => {
    expect(buildFeedbackInput({ type: "bug", severity: "critical", message: "  не працює запис  " }, "/beauty/calendar")).toEqual({
      type: "bug",
      severity: "critical",
      message: "не працює запис",
      pageUrl: "/beauty/calendar",
    });
    expect(buildFeedbackInput({ type: "cooperation", severity: "critical", message: "хочемо співпрацювати" }, "")).toEqual({
      type: "cooperation",
      severity: "normal",
      message: "хочемо співпрацювати",
    });
  });
});

describe("примітка про дані", () => {
  it("лишається явним плейсхолдером Q8 до заміни юридичним текстом", () => {
    expect(FEEDBACK_DATA_NOTICE.startsWith(FEEDBACK_DATA_NOTICE_MARKER)).toBe(true);
  });
});

describe("humanizeFeedbackError", () => {
  const e = (status: number, code: string, retry?: number) => new BeautyApiError(status, code, "m", retry);
  it("429: з часом і без", () => {
    expect(humanizeFeedbackError(e(429, "rate_limited", 45))).toContain("менше хвилини");
    expect(humanizeFeedbackError(e(429, "rate_limited", 600))).toContain("10 хв");
    expect(humanizeFeedbackError(e(429, "rate_limited", 3 * 3600 + 120))).toContain("3 год 2 хв");
    expect(humanizeFeedbackError(e(429, "rate_limited", 7200))).toContain("2 год");
    expect(humanizeFeedbackError(e(429, "rate_limited"))).toContain("Спробуйте пізніше");
  });
  it("403 tenant_suspended / user_inactive, 422 коди власними текстами (invalid_type не «тип розсилки»)", () => {
    expect(humanizeFeedbackError(e(403, "tenant_suspended"))).toContain("призупинено");
    expect(humanizeFeedbackError(e(403, "user_inactive"))).toContain("вимкнено");
    expect(humanizeFeedbackError(e(422, "invalid_type"))).toContain("тип звернення");
    expect(humanizeFeedbackError(e(422, "invalid_severity"))).toContain("критичність");
    expect(humanizeFeedbackError(e(422, "invalid_message"))).toContain("від 10 до 2000");
    expect(humanizeFeedbackError(e(422, "invalid_page_url"))).toContain("сторінку");
    expect(humanizeFeedbackError(e(422, "other"))).toContain("Перевірте");
  });
  it("401, 403 без коду, 5xx і мережа", () => {
    expect(humanizeFeedbackError(e(401, ""))).toContain("Увійдіть знову");
    expect(humanizeFeedbackError(e(403, ""))).toContain("немає прав");
    expect(humanizeFeedbackError(e(500, ""))).toContain("Помилка сервера");
    expect(humanizeFeedbackError(new TypeError("fail"))).toContain("Сервер недоступний");
  });
});
