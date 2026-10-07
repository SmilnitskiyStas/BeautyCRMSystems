"use client";

import { useState } from "react";
import { useCreatePromotion, useLocations, usePriceList, usePromoPreview } from "../hooks/use-beauty-admin";
import { durationLabel, money } from "../format";
import type { PromoDraft } from "../types";
import { Card, Chip, FIELD_INPUT, FIELD_LABEL, PageHeader, QueryState, Table, TD } from "./ui";

const PERCENTS = [10, 15, 20, 30];

function toggle(list: string[], id: string): string[] {
  return list.includes(id) ? list.filter((x) => x !== id) : [...list, id];
}

export function PromosScreen() {
  const [name, setName] = useState("Осінній манікюр");
  const [percent, setPercent] = useState(20);
  const [locationIds, setLocationIds] = useState<string[]>(["c", "p"]);
  const [serviceIds, setServiceIds] = useState<string[]>(["mn", "pd"]);
  const [from, setFrom] = useState("07.10.2026");
  const [to, setTo] = useState("14.10.2026");

  const locations = useLocations();
  const priceList = usePriceList();
  // Для перерахунку цін назва й дати не потрібні — не перезапитуємо на кожну літеру.
  const preview = usePromoPreview({ name: "", from: "", to: "", percent, locationIds, serviceIds });
  const create = useCreatePromotion();

  const canLaunch = name.trim().length > 0 && locationIds.length > 0 && serviceIds.length > 0;
  const draft: PromoDraft = { name: name.trim(), percent, locationIds, serviceIds, from, to };

  return (
    <>
      <PageHeader title="Ціни та акції" subtitle="Прайс мережі, власні ціни закладів і нова акція." />
      <div className="flex flex-wrap items-start gap-6">
        <Card className="flex-[999_1_560px]">
          <h2 className="mb-3.5 text-lg font-semibold">Прайс-лист</h2>
          <QueryState isPending={preview.isPending} isError={preview.isError} onRetry={() => preview.refetch()}>
            <Table headers={["Послуга", "Тривалість", "Ціна мережі", "З акцією", "Заклади"]} minWidth={600}>
              {preview.data?.rows.map((r) => (
                <tr key={r.serviceId} className="border-t border-(--line)">
                  <td className={`${TD} font-semibold`}>{r.name}</td>
                  <td className={`${TD} text-(--muted)`}>{durationLabel(r.durationMinutes)}</td>
                  <td className={TD}>{money(r.price)}</td>
                  <td className={`${TD} font-semibold ${r.promoPrice !== null ? "text-(--promo)" : "text-(--muted)"}`}>
                    {r.promoPrice !== null ? money(r.promoPrice) : "—"}
                  </td>
                  <td className={`${TD} text-(--muted)`}>{r.where}</td>
                </tr>
              ))}
            </Table>
          </QueryState>
        </Card>

        <section className="flex min-w-0 flex-[1_1_380px] flex-col gap-5">
          <form
            className="flex min-w-0 flex-col gap-[18px] rounded-2xl bg-white p-5"
            onSubmit={(e) => {
              e.preventDefault();
              if (canLaunch) create.mutate(draft);
            }}
          >
            <h2 className="text-lg font-semibold">Нова акція</h2>
            <div className="flex flex-col gap-1.5">
              <label htmlFor="promo-name" className={FIELD_LABEL}>Назва</label>
              <input id="promo-name" type="text" value={name} onChange={(e) => setName(e.target.value)} className={FIELD_INPUT} />
            </div>

            <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
              <legend className={`${FIELD_LABEL} mb-2 p-0`}>Знижка</legend>
              <div className="flex flex-wrap gap-2">
                {PERCENTS.map((p) => (
                  <Chip key={p} variant="square" pressed={percent === p} onClick={() => setPercent(p)}>
                    −{p}%
                  </Chip>
                ))}
              </div>
            </fieldset>

            <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
              <legend className={`${FIELD_LABEL} mb-2 p-0`}>Де діє</legend>
              <div className="flex flex-wrap gap-2">
                {locations.data?.map((l) => (
                  <Chip key={l.id} pressed={locationIds.includes(l.id)} onClick={() => setLocationIds(toggle(locationIds, l.id))}>
                    {l.name}
                  </Chip>
                ))}
              </div>
            </fieldset>

            <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
              <legend className={`${FIELD_LABEL} mb-2 p-0`}>Для яких послуг</legend>
              <div className="flex flex-wrap gap-2">
                {priceList.data?.map((s) => (
                  <Chip key={s.serviceId} variant="soft" pressed={serviceIds.includes(s.serviceId)} onClick={() => setServiceIds(toggle(serviceIds, s.serviceId))}>
                    {s.name}
                  </Chip>
                ))}
              </div>
            </fieldset>

            <div className="flex flex-wrap gap-3">
              <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
                <label htmlFor="d-from" className={FIELD_LABEL}>Початок</label>
                <input id="d-from" type="text" value={from} onChange={(e) => setFrom(e.target.value)} className={FIELD_INPUT} />
              </div>
              <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
                <label htmlFor="d-to" className={FIELD_LABEL}>Завершення</label>
                <input id="d-to" type="text" value={to} onChange={(e) => setTo(e.target.value)} className={FIELD_INPUT} />
              </div>
            </div>

            <button
              type="submit"
              disabled={!canLaunch || create.isPending}
              className="min-h-[52px] cursor-pointer rounded-[26px] border-0 bg-(--accent) text-base font-semibold text-white disabled:cursor-not-allowed disabled:opacity-60"
            >
              {create.isPending ? "Запускаємо…" : "Запустити акцію"}
            </button>
            {!canLaunch ? (
              <p className="text-[13px] text-(--muted)">Вкажіть назву, хоча б один заклад і одну послугу.</p>
            ) : null}
            {create.isSuccess ? (
              <p role="status" className="rounded-xl bg-[#DDF3E6] px-3 py-2 text-sm text-[#145A32]">
                Акцію «{draft.name}» запущено.
              </p>
            ) : null}
            {create.isError ? (
              <p role="alert" className="text-sm text-[#8A1F1F]">
                Не вдалося запустити акцію. Спробуйте ще раз.
              </p>
            ) : null}
          </form>

          <Card as="div" className="flex flex-col gap-3">
            <div className="text-[13px] font-semibold text-(--muted)">Так акцію побачить клієнт</div>
            <div className="flex flex-col gap-2 rounded-2xl border-2 border-(--promo) bg-[#FFF8F3] p-4">
              <span className="self-start rounded-lg bg-[#FDE9DC] px-2 py-1 text-[13px] font-semibold text-(--promo)">
                −{percent}% до {to}
              </span>
              <span className="text-base font-semibold">Манікюр + гель-лак</span>
              <span className="text-[13px] text-(--muted)">{preview.data?.locationSummary ?? "…"}</span>
              <span className="flex items-baseline gap-2.5">
                <span className="text-xl font-semibold">{preview.data ? money(preview.data.samplePrice) : "…"}</span>
                <span className="text-sm text-(--muted) line-through">{preview.data ? money(preview.data.sampleOriginal) : ""}</span>
              </span>
            </div>
          </Card>
        </section>
      </div>
    </>
  );
}
