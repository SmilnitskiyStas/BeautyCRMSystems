import { defineConfig } from "vitest/config";

// Integration tests start a PostgreSQL container (Docker required).
export default defineConfig({ test: { testTimeout: 60_000, hookTimeout: 180_000, setupFiles: ["./src/testing/pool-error-guard.ts"] } });
