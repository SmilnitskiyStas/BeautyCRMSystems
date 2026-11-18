import { describe, expect, it } from "vitest";
import { BeautyApiError, humanizeAuthError, humanizeError } from "./errors";

describe("тексти помилок ідентичності (§31.11)", () => {
  it.each([
    [401, "invalid_credentials", "Невірна пошта або пароль"],
    [423, "account_locked", "Забагато невдалих спроб. Спробуйте за 15 хвилин або скиньте пароль"],
    [401, "login_ticket_invalid", "Час вибору минув, увійдіть ще раз"],
    [404, "membership_not_found", "Доступ до цього бізнесу недоступний"],
    [409, "email_in_use", "Ця пошта вже використовується в Beauty CRM. Вкажіть іншу пошту"],
    [409, "owner_email_in_use", "Ця пошта вже використовується в Beauty CRM. Вкажіть іншу пошту"],
    [410, "reset_invalid", "Посилання недійсне або застаріло. Запросіть нове"],
  ])("%s %s", (status, code, text) => {
    expect(humanizeError(new BeautyApiError(status, code, "english message"))).toBe(text);
  });

  it("rate_limited на екранах входу (§31.11), глобальний текст не змінено", () => {
    const e = new BeautyApiError(429, "rate_limited", "x");
    expect(humanizeAuthError(e)).toBe("Забагато спроб. Спробуйте пізніше");
    expect(humanizeError(e)).toBe("Забагато запитів. Зачекайте хвилину і спробуйте ще раз.");
  });

  it("текст email_in_use не містить назви бізнесу", () => {
    expect(humanizeError(new BeautyApiError(409, "email_in_use", "Salon X"))).not.toMatch(/Salon/);
  });
});
