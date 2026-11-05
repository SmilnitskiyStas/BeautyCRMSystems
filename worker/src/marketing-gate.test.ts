import { describe, expect, it } from "vitest";
import { assertMarketingLegalGate } from "./marketing-gate";

describe("assertMarketingLegalGate (M2)", () => {
  const PROD = { ASPNETCORE_ENVIRONMENT: "Production" };
  it("flags-off_never-throws_in-any-environment", () => {
    expect(() => assertMarketingLegalGate({})).not.toThrow();
    expect(() => assertMarketingLegalGate({ ...PROD, WORKER_CAMPAIGNS_ENABLED: "false" })).not.toThrow();
  });
  it("campaigns-or-winback_outside-development_without-approval_throw", () => {
    expect(() => assertMarketingLegalGate({ ...PROD, WORKER_CAMPAIGNS_ENABLED: "true" })).toThrow(/WORKER_MARKETING_LEGAL_APPROVED/);
    expect(() => assertMarketingLegalGate({ ...PROD, WORKER_WINBACK_ENABLED: "TRUE" })).toThrow(/WORKER_MARKETING_LEGAL_APPROVED/);
    expect(() => assertMarketingLegalGate({ WORKER_CAMPAIGNS_ENABLED: "true" })).toThrow(); // unset environment counts as non-development
    expect(() => assertMarketingLegalGate({ ASPNETCORE_ENVIRONMENT: "Staging", WORKER_WINBACK_ENABLED: "true", WORKER_MARKETING_LEGAL_APPROVED: "false" })).toThrow();
    expect(() => assertMarketingLegalGate({ ASPNETCORE_ENVIRONMENT: "Development", NODE_ENV: "production", WORKER_CAMPAIGNS_ENABLED: "true" })).toThrow();
  });
  it("development_or-explicit-approval_pass", () => {
    expect(() => assertMarketingLegalGate({ ASPNETCORE_ENVIRONMENT: "Development", WORKER_CAMPAIGNS_ENABLED: "true", WORKER_WINBACK_ENABLED: "true" })).not.toThrow();
    expect(() => assertMarketingLegalGate({ ...PROD, WORKER_CAMPAIGNS_ENABLED: "true", WORKER_WINBACK_ENABLED: "true", WORKER_MARKETING_LEGAL_APPROVED: "true" })).not.toThrow();
  });
});
