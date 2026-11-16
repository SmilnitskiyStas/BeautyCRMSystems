"use client";

import { useState } from "react";
import { humanizeError } from "@/features/beauty-auth/errors";
import { presetRange, type RangePreset } from "../analytics/ranges";
import { moneyExact, todayIso } from "../format";
import { useMyRevenue } from "../hooks/use-team";
import { Card, Chip, QueryState } from "./ui";

const PRESETS: { id: Extract<RangePreset, "month" | "prev_month" | "30d">; label: string }[] = [
  { id: "month", label: "Цей місяць" },
  { id: "prev_month", label: "Минулий місяць" },
  { id: "30d", label: "30 днів" },
];

/** «Мій виторг» (`revenue.view = own`, §30.9.7): лише власні завершені візити, без собівартості, виплат і даних інших. */
export function MyRevenueCard() {
  const [preset, setPreset] = useState<(typeof PRESETS)[number]["id"]>("month");
  const range = presetRange(preset, todayIso());
  const q = useMyRevenue(range.from, range.to);
  const d = q.data;
  return (
    <Card className="flex flex-col gap-3" >
      <h2 className="m-0 text-lg font-semibold">Мій виторг</h2>
      <div className="flex flex-wrap gap-2" role="group" aria-label="Період">
        {PRESETS.map((p) => (
          <Chip key={p.id} pressed={preset === p.id} onClick={() => setPreset(p.id)}>
            {p.label}
          </Chip>
        ))}
      </div>
      <div aria-live="polite">
        <QueryState isPending={q.isPending} isError={false} >
          {q.isError ? (
            <p role="alert" className="m-0 text-sm text-[#8A1F1F]">
              {humanizeError(q.error, "Не вдалося завантажити виторг.")}
            </p>
          ) : d ? (
            <div className="flex flex-col gap-2" data-testid="my-revenue">
              {d.specialistId === null ? (
                <p role="note" className="m-0 text-sm text-(--muted)">
                  Ваш обліковий запис ще не прив&apos;язано до профілю майстра, тому візитів немає.
                </p>
              ) : null}
              <div className="flex flex-wrap gap-x-8 gap-y-1">
                <div>
                  <div className="text-[13px] text-(--muted)">Завершених візитів</div>
                  <div className="text-xl font-semibold" data-testid="my-revenue-count">{d.completed}</div>
                </div>
                <div>
                  <div className="text-[13px] text-(--muted)">Виторг</div>
                  <div className="text-xl font-semibold" data-testid="my-revenue-sum">{moneyExact(d.revenue)}</div>
                </div>
              </div>
              {d.byLocation.length > 0 ? (
                <ul className="m-0 list-none p-0 text-sm" aria-label="За закладами">
                  {d.byLocation.map((l) => (
                    <li key={l.locationId} className="flex justify-between gap-3 border-t border-(--line) py-1.5">
                      <span>{l.name}</span>
                      <span>
                        {l.completed} · {moneyExact(l.revenue)}
                      </span>
                    </li>
                  ))}
                </ul>
              ) : null}
              <p className="m-0 text-[13px] text-(--muted)">Лише ваші завершені візити за дату закладу. Без собівартості й виплат.</p>
            </div>
          ) : null}
        </QueryState>
      </div>
    </Card>
  );
}
