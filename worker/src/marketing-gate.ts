/**
 * M2: legal gate of marketing (§26.16 p. 1) for the worker, same idea as `useMockChannels`: outside Development the process refuses
 * to start with campaigns or winback enabled unless `WORKER_MARKETING_LEGAL_APPROVED=true`. Development = ASPNETCORE_ENVIRONMENT=Development
 * and NODE_ENV not production; anything else (including unset) counts as non-development.
 */
export function assertMarketingLegalGate(env: NodeJS.ProcessEnv): void {
  const on = (v: string | undefined) => /^true$/i.test(v ?? "");
  if (!on(env.WORKER_CAMPAIGNS_ENABLED) && !on(env.WORKER_WINBACK_ENABLED)) return;
  const development = env["ASPNETCORE_ENVIRONMENT"] === "Development" && env["NODE_ENV"] !== "production";
  if (development || on(env.WORKER_MARKETING_LEGAL_APPROVED)) return;
  throw new Error("WORKER_CAMPAIGNS_ENABLED/WORKER_WINBACK_ENABLED outside Development require WORKER_MARKETING_LEGAL_APPROVED=true (legal gate §26.16)");
}
