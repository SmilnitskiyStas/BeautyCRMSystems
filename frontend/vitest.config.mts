import path from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { "@": path.resolve(import.meta.dirname, ".") } },
  test: {
    environment: "jsdom",
    // Mock-аналітика будує історію на 80 тижнів: на завантаженій машині перший виклик > 5 с.
    testTimeout: 20000,
    globals: true,
    setupFiles: ["./vitest.setup.ts"],
    include: ["features/**/*.test.{ts,tsx}", "lib/**/*.test.{ts,tsx}"],
    env: { NEXT_PUBLIC_USE_MOCK: "1" },
  },
});
