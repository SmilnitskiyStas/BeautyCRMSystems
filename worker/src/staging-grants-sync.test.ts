import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/** TASK-800: staging GRANT-и (deploy/staging/db/grants.sql) не мають розходитись зі scripts/dev/setup-db.ps1. */
function statements(sql: string): string[] {
  return sql
    .split(/\r?\n/)
    .filter((l) => !l.trim().startsWith("--") && l.trim() !== "")
    .join(" ")
    .split(";")
    .map((s) => s.replace(/\s+/g, " ").trim())
    .filter(Boolean);
}

describe("staging grants", () => {
  it("grants.sql matches the $grants block of setup-db.ps1", () => {
    const ps = readFileSync(new URL("../../scripts/dev/setup-db.ps1", import.meta.url), "utf8");
    const dev = /\$grants = @"\r?\n([\s\S]*?)\r?\n"@/.exec(ps)![1]!;
    const staging = readFileSync(new URL("../../deploy/staging/db/grants.sql", import.meta.url), "utf8");
    expect(statements(staging)).toEqual(statements(dev));
    expect(statements(staging).length).toBeGreaterThan(10);
  });
});
