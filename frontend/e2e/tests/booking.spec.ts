import { expect, test, type Page } from "@playwright/test";

/** Збирає console.error / uncaught exceptions: тест падає, якщо сторінка їх породжує. */
function trackErrors(page: Page) {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(`pageerror: ${e.message}`));
  page.on("console", (m) => {
    if (m.type() === "error") errors.push(`console: ${m.text()}`);
  });
  return errors;
}

/**
 * Mock-клієнт віддає слоти лише на «сьогодні» і відсікає минулі, тож без фіксації часу тест залежить від години доби
 * (увечері вільного часу немає). Фіксуємо локальний час 08:00; таймери лишаються реальними.
 */
async function fixMorning(page: Page) {
  const d = new Date();
  await page.clock.setFixedTime(new Date(d.getFullYear(), d.getMonth(), d.getDate(), 8, 0, 0));
}

const pick = async (page: Page, index = 0) => {
  await page.locator("main button[aria-pressed]").nth(index).click();
};
const next = (page: Page, name: RegExp | string) => page.getByRole("button", { name }).click();

test.describe("публічний запис (mock API)", () => {
  test("головний шлях: заклад -> майстер -> послуга й час -> оформлення -> підтвердження", async ({ page }) => {
    const errors = trackErrors(page);
    await fixMorning(page);
    await page.goto("/book");
    await expect(page.getByText("Оберіть заклад", { exact: false }).first()).toBeVisible();

    await pick(page);
    await next(page, /^Далі$/);
    await expect(page.getByText("Оберіть майстра").first()).toBeVisible();
    await pick(page);
    await next(page, /^Далі$/);

    await expect(page.getByText("Послуга та час").first()).toBeVisible();
    await pick(page, 0); // послуга
    await page.locator("section[aria-labelledby='slots-heading'] button[aria-pressed]:not([data-testid='date-option'])").first().click();
    await next(page, /До оформлення/);

    // умова скасування A2 видна клієнту перед підтвердженням
    await expect(page.getByText("За 12 годин і менше до візиту повертається 50%.")).toBeVisible();

    await page.getByLabel(/Ім.я/).fill("Анна Тест");
    await page.getByLabel(/Телефон/).fill("+380501112233");
    await page.getByLabel(/Нагадування|Нагадати/).selectOption("1h");
    await page.getByRole("button", { name: /Готівкою/ }).click();
    await next(page, /Підтвердити запис/);

    await expect(page.getByText("Готово").first()).toBeVisible();
    expect(errors).toEqual([]);
  });

  test("валідація: порожнє ім'я й телефон показують помилки й не відправляють запис", async ({ page }) => {
    await fixMorning(page);
    await page.goto("/book");
    await pick(page);
    await next(page, /^Далі$/);
    await pick(page);
    await next(page, /^Далі$/);
    await pick(page, 0);
    await page.locator("section[aria-labelledby='slots-heading'] button[aria-pressed]:not([data-testid='date-option'])").first().click();
    await next(page, /До оформлення/);

    await page.getByRole("button", { name: /Підтвердити запис|Оплатити/ }).click();
    await expect(page.locator("[role=alert]").first()).toBeVisible();
    await expect(page.getByText("Готово")).toHaveCount(0);
  });

  test("кнопка «Далі» не пускає без вибору", async ({ page }) => {
    await page.goto("/book");
    const cta = page.getByRole("button", { name: /^Далі$/ });
    await expect(cta).toBeDisabled();
    await pick(page);
    await expect(cta).toBeEnabled();
  });

  test("неіснуючий запис не падає з необробленою помилкою", async ({ page }) => {
    const errors = trackErrors(page);
    const res = await page.goto("/book/appointment/does-not-exist");
    expect(res?.status()).toBeLessThan(500);
    expect(errors.filter((e) => e.startsWith("pageerror"))).toEqual([]);
  });
});
