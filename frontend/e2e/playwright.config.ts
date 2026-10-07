import { defineConfig, devices } from "@playwright/test";

/**
 * TASK-681: e2e-каркас Beauty CRM. Автономний пакет (frontend/e2e/package.json), щоб не чіпати спільний package.json.
 * Запуск без ручних кроків:  cd frontend/e2e && npm install && npx playwright install chromium && npm test
 * (браузер ставиться один раз; dev-сервер піднімається сам). Існуючий сервер: E2E_BASE_URL=http://localhost:3000 npm test.
 * Frontend працює на mock API (features/<name>/api/index.ts), backend/БД для цих тестів не потрібні.
 */
const PORT = Number(process.env.E2E_PORT ?? 3107);
const baseURL = process.env.E2E_BASE_URL ?? `http://localhost:${PORT}`;

export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"]],
  use: { baseURL, trace: "retain-on-failure", locale: "uk-UA" },
  projects: [
    { name: "desktop-chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "mobile-chromium", use: { ...devices["Pixel 7"] } },
  ],
  webServer: process.env.E2E_BASE_URL
    ? undefined
    : { command: `npm run dev -- --port ${PORT}`, cwd: "..", url: `${baseURL}/book`, reuseExistingServer: true, timeout: 180_000 },
});
