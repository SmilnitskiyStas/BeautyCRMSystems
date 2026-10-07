import { expect, test } from "@playwright/test";

/**
 * Адмінка закрита автентифікацією (TASK-684): без сесії кожен екран веде на логін, а не показує дані.
 * Повний прохід адмінки після логіну потребує реального backend + PostgreSQL + seed користувача - див. README/звіт TASK-681.
 */
const screens = ["", "/calendar", "/clients", "/staff", "/promos", "/analytics", "/ai", "/channels"];

for (const path of screens) {
  test(`без сесії /beauty${path} веде на форму входу й не віддає дані`, async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (e) => errors.push(e.message));
    const res = await page.goto(`/beauty${path}`);
    expect(res?.status(), "HTTP status").toBeLessThan(500);
    await expect(page.getByRole("heading", { name: /Вхід у Beauty CRM/ })).toBeVisible();
    await expect(page.getByRole("main")).toHaveCount(1);
    expect(errors).toEqual([]);
  });
}
