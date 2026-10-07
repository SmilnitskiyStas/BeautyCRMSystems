import { readdirSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/**
 * TASK-681: worker-інтеграційні тести працюють на `src/testing/schema.sql`, згенерованій з EF-міграцій .NET.
 * Якщо схему не перегенерувати після нової міграції, worker-тести проходять на застарілій схемі, а реальна БД
 * (де .NET уже змінив таблиці) може розійтись із SQL воркера. Цей тест (без Docker) ловить розсинхрон.
 * Перегенерація: `dotnet ef migrations script --idempotent` (див. TASK-686).
 */
const migrationsDir = new URL("../../../backend/BeautyCrm.Infrastructure/Data/Migrations/", import.meta.url);
const schema = readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8");

describe("worker test schema vs .NET migrations", () => {
  const ids = readdirSync(migrationsDir)
    .filter((f) => /^\d{14}_.+\.cs$/.test(f) && !f.endsWith(".Designer.cs") && !f.endsWith(".Sql.cs"))
    .map((f) => f.replace(/\.cs$/, ""));

  it("migrations_are_discovered", () => {
    expect(ids.length).toBeGreaterThan(0);
  });

  it("every_ef_migration_is_present_in_schema_sql", () => {
    const missing = ids.filter((id) => !schema.includes(`'${id}'`));
    expect(missing, `schema.sql is stale, regenerate it: missing ${missing.join(", ")}`).toEqual([]);
  });

  it("worker_contract_columns_exist_with_expected_values", () => {
    // pg-poll-store.ts: reminders.status = 'scheduled'; appointments.reminder_option '1h' | '2h'
    expect(schema).toMatch(/ck_beauty_reminders_status CHECK \(status IN \('scheduled', 'sent', 'failed', 'cancelled'\)\)/);
    expect(schema).toMatch(/reminder_option[^\n]*\n?/);
    expect(schema).toContain("idempotency_key"); // outbox dedup: unique (tenant_id, idempotency_key)
  });
});
