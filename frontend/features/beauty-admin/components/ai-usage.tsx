"use client";

import { useId } from "react";
import { useAiUsage } from "../hooks/use-beauty-admin";
import { monthIn, nf } from "../ai-settings/messages";
import type { AiRoleId } from "../types";
import { Table, TD } from "./ui";

const money = (n: number, cur: string) => `${cur === "USD" ? "$" : ""}${n.toFixed(2).replace(".", ",")}${cur === "USD" ? "" : ` ${cur}`}`;
const SOURCE_TEXT = { tenant: "Діє ваш ліміт.", platform: "Діє ліміт платформи.", min: "Діє менше з двох: ваш ліміт і ліміт платформи." } as const;

/** Використання за поточний місяць (UTC) і ліміти (§20.9 п. 7). */
export function AiUsageBlock({ roleLabels }: { roleLabels: Record<AiRoleId, string> }) {
  const q = useAiUsage(6);
  const id = useId();
  if (q.isPending) return <p role="status" className="text-sm text-(--muted)">Завантажуємо використання…</p>;
  if (q.isError || !q.data) return <p role="alert" className="text-sm text-[#8A1F1F]">Не вдалося завантажити використання.</p>;
  const u = q.data;
  const tokens = u.current.inputTokens + u.current.outputTokens;
  const tl = u.limits.monthlyTokens;
  const rl = u.limits.monthlyRequests;
  const pct = (n: number, max: number) => Math.min(100, Math.round((n / max) * 100));
  const month = monthIn(u.period.from);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-tok`} className="text-sm">
          {tl !== null
            ? `Використано ${nf(tokens)} з ${nf(tl)} токенів (${pct(tokens, tl)}%) у ${month}`
            : `Використано ${nf(tokens)} токенів у ${month}. Ліміту токенів немає.`}
        </label>
        {tl !== null ? <progress id={`${id}-tok`} value={tokens} max={tl} className="h-3 w-full" /> : null}
      </div>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-req`} className="text-sm">
          {rl !== null
            ? `Запитів: ${nf(u.current.requests)} з ${nf(rl)} (${pct(u.current.requests, rl)}%)`
            : `Запитів: ${nf(u.current.requests)}. Ліміту запитів немає.`}
          {u.current.errors > 0 ? `, з помилками: ${nf(u.current.errors)}` : ""}
        </label>
        {rl !== null ? <progress id={`${id}-req`} value={u.current.requests} max={rl} className="h-3 w-full" /> : null}
      </div>
      {tl !== null || rl !== null ? <p className="text-[13px] text-(--muted)">{SOURCE_TEXT[u.limits.source]}</p> : null}
      {u.current.estimatedCost !== null ? (
        <p className="text-sm">орієнтовно {money(u.current.estimatedCost, u.current.currency)} за довідником цін</p>
      ) : null}

      {u.byModel.length > 0 ? (
        <Table headers={["Сценарій", "Модель", "Запити", "Токени (вх. / вих.)", "Вартість"]} minWidth={520}>
          {u.byModel.map((r) => (
            <tr key={`${r.role}-${r.provider}-${r.model}-${r.keySource}`} className="border-t border-(--line)">
              <td className={`${TD} font-semibold`}>{roleLabels[r.role]}</td>
              <td className={`${TD} break-all`}>{r.model}</td>
              <td className={TD}>{nf(r.requests)}</td>
              <td className={TD}>
                {nf(r.inputTokens)} / {nf(r.outputTokens)}
              </td>
              <td className={TD}>{r.estimatedCost === null ? "—" : money(r.estimatedCost, u.current.currency)}</td>
            </tr>
          ))}
        </Table>
      ) : null}
    </div>
  );
}
