"use client";

import Link from "next/link";
import { useState } from "react";
import { todayInZone } from "../../beauty-shared/zone-time";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { useCancelStocktake, usePostStocktake, useSaveStocktakeLines, useStocktake } from "../hooks/use-inventory";
import { dateTimeLabel } from "../format";
import { formatQuantity, formatSigned, humanizeInventoryError, isYmd, numberToField, parseQuantity, stocktakeDifference, unitShort } from "../inventory/logic";
import type { InventoryScope, Stocktake, StocktakeLineInput } from "../inventory/types";
import { InputField, InventoryPage, locationName } from "./inventory-parts";
import { ModalDialog } from "./modal-dialog";
import { ErrorBanner, SuccessBanner } from "./staff-parts";
import { STOCKTAKE_STATUS } from "./inventory-stocktakes-screen";
import { Badge, Card, PrimaryButton, QueryState, SecondaryButton, Table, TD } from "./ui";

interface Draft {
  counted: string;
  surplus: string;
}

function Detail({ id, scope }: { id: string; scope: InventoryScope }) {
  const q = useStocktake(id);
  const save = useSaveStocktakeLines(id);
  const post = usePostStocktake(id);
  const cancel = useCancelStocktake(id);
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [errors, setErrors] = useState<Record<string, { counted?: string; surplus?: string }>>({});
  const [serverError, setServerError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [dialog, setDialog] = useState<"post" | "cancel" | null>(null);

  const st = q.data;
  if (!st) {
    return (
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {null}
      </QueryState>
    );
  }
  const location = scope.locations.find((l) => l.id === st.locationId);
  const editable = st.status === "draft" && location?.canManage === true;
  const today = todayInZone(location?.timezone, new Date());
  const busy = save.isPending || post.isPending || cancel.isPending;

  const serverDraft = (productId: string): Draft => {
    const l = st.lines.find((x) => x.product.id === productId)!;
    return { counted: numberToField(l.countedQuantity), surplus: l.surplusExpiresOn ?? "" };
  };
  const draftOf = (productId: string): Draft => drafts[productId] ?? serverDraft(productId);
  const isDirty = (productId: string) => {
    const d = drafts[productId];
    if (!d) return false;
    const s = serverDraft(productId);
    return d.counted !== s.counted || d.surplus !== s.surplus;
  };
  const dirtyIds = st.lines.map((l) => l.product.id).filter(isDirty);

  const setDraft = (productId: string, patch: Partial<Draft>) => {
    setDrafts((cur) => ({ ...cur, [productId]: { ...draftOf(productId), ...patch } }));
    setMessage(null);
  };

  /** Валідація змінених рядків; повертає рядки для `PUT` або `null`. */
  function collect(): StocktakeLineInput[] | null {
    const next: typeof errors = {};
    const out: StocktakeLineInput[] = [];
    for (const pid of dirtyIds) {
      const line = st!.lines.find((l) => l.product.id === pid)!;
      const d = draftOf(pid);
      const e: { counted?: string; surplus?: string } = {};
      let counted: number | null = null;
      if (d.counted.trim() !== "") {
        const r = parseQuantity(d.counted, line.product.unit, { allowZero: true });
        if (r.error !== undefined) e.counted = r.error;
        else counted = r.value;
      }
      const surplus = d.surplus.trim();
      if (surplus && (!isYmd(surplus) || surplus < today)) e.surplus = !isYmd(surplus) ? "Вкажіть коректну дату." : "Термін придатності вже минув.";
      if (Object.keys(e).length > 0) next[pid] = e;
      out.push({ productId: pid, countedQuantity: counted, surplusExpiresOn: surplus || null });
    }
    setErrors(next);
    return Object.keys(next).length > 0 ? null : out;
  }

  const fail = (e: unknown, fallback: string) => {
    setServerError(humanizeInventoryError(e, fallback));
    if (e instanceof BeautyApiError && (e.code === "stocktake_version_conflict" || e.code === "stocktake_closed")) void q.refetch();
  };

  async function saveDrafts(): Promise<Stocktake | null> {
    const lines = collect();
    if (!lines) return null;
    if (lines.length === 0) return st!;
    const saved = await save.mutateAsync({ expectedVersion: st!.version, lines });
    setDrafts({});
    return saved;
  }

  async function onSave() {
    setServerError(null);
    try {
      const saved = await saveDrafts();
      if (saved) setMessage("Підрахунки збережено.");
    } catch (e) {
      fail(e, "Не вдалося зберегти підрахунки. Спробуйте ще раз.");
    }
  }

  function openPost() {
    setServerError(null);
    if (!collect()) return;
    setDialog("post");
  }

  async function onPost() {
    setServerError(null);
    try {
      const saved = await saveDrafts();
      if (!saved) {
        setDialog(null);
        return;
      }
      const res = await post.mutateAsync(saved.version);
      setDialog(null);
      setMessage(res.operation ? "Інвентаризацію проведено: різниці відображено в журналі рухів." : "Інвентаризацію проведено: розбіжностей немає.");
    } catch (e) {
      setDialog(null);
      fail(e, "Не вдалося провести інвентаризацію. Спробуйте ще раз.");
    }
  }

  async function onCancel() {
    setServerError(null);
    try {
      await cancel.mutateAsync(st!.version);
      setDialog(null);
      setMessage("Інвентаризацію скасовано.");
    } catch (e) {
      setDialog(null);
      fail(e, "Не вдалося скасувати інвентаризацію. Спробуйте ще раз.");
    }
  }

  const view = STOCKTAKE_STATUS[st.status];
  const countedNow = st.lines.filter((l) => draftOf(l.product.id).counted.trim() !== "").length;
  const diffs = st.lines.filter((l) => {
    const d = draftOf(l.product.id).counted.trim();
    const parsed = d === "" ? null : parseQuantity(d, l.product.unit, { allowZero: true });
    return parsed && parsed.error === undefined && stocktakeDifference(l.expectedQuantity, parsed.value) !== 0;
  }).length;

  return (
    <>
      <Card as="div" className="flex flex-wrap items-center gap-3">
        <Badge tone={view.tone}>{view.label}</Badge>
        <p className="text-sm">
          Заклад «{locationName(scope.locations, st.locationId)}», створено {dateTimeLabel(st.createdAt, location?.timezone)}
          {st.postedAt ? `, проведено ${dateTimeLabel(st.postedAt, location?.timezone)}` : ""}
        </p>
        {st.note ? <p className="text-sm text-(--muted)">{st.note}</p> : null}
        <p role="status" aria-live="polite" className="ml-auto text-sm text-(--muted)">
          Пораховано {countedNow} із {st.lines.length}
        </p>
      </Card>
      {st.status === "draft" ? (
        <p className="text-sm text-(--muted)">
          Очікуваний залишок - живе значення на зараз. Якщо в закладі були рухи під час підрахунку, різниця рахується до залишку на момент проведення. Порожнє поле - «не рахували».
        </p>
      ) : null}
      <Card>
        <Table headers={["Товар", "Очікується", "Підраховано", "Різниця", ...(editable ? ["Термін надлишку"] : [])]} minWidth={760}>
          {st.lines.map((l) => {
            const pid = l.product.id;
            const d = draftOf(pid);
            const parsed = d.counted.trim() === "" ? null : parseQuantity(d.counted, l.product.unit, { allowZero: true });
            const liveDiff = editable ? (parsed && parsed.error === undefined ? stocktakeDifference(l.expectedQuantity, parsed.value) : null) : l.difference;
            const e = errors[pid];
            return (
              <tr key={pid} className="border-t border-(--line) align-top">
                <th scope="row" className={`${TD} text-left font-semibold`}>
                  {l.product.name}
                </th>
                <td className={TD}>{l.expectedQuantity === null ? "—" : formatQuantity(l.expectedQuantity, l.product.unit)}</td>
                <td className={TD}>
                  {editable ? (
                    <InputField
                      label={`Підраховано: ${l.product.name}`}
                      inputMode="decimal"
                      value={d.counted}
                      onChange={(v) => setDraft(pid, { counted: v })}
                      suffix={unitShort(l.product.unit)}
                      error={e?.counted}
                    />
                  ) : l.countedQuantity === null ? (
                    "—"
                  ) : (
                    formatQuantity(l.countedQuantity, l.product.unit)
                  )}
                </td>
                <td className={`${TD} whitespace-nowrap font-semibold ${liveDiff ? (liveDiff < 0 ? "text-[#8A1F1F]" : "text-[#145A32]") : ""}`}>
                  {liveDiff === null ? "—" : liveDiff === 0 ? "Без різниці" : formatSigned(liveDiff, l.product.unit)}
                </td>
                {editable ? (
                  <td className={TD}>
                    {liveDiff !== null && liveDiff > 0 ? (
                      <InputField
                        label={`Термін придатності надлишку: ${l.product.name}`}
                        type="date"
                        value={d.surplus}
                        onChange={(v) => setDraft(pid, { surplus: v })}
                        error={e?.surplus}
                        hint="Обов’язково для товарів зі строком придатності"
                      />
                    ) : (
                      "—"
                    )}
                  </td>
                ) : null}
              </tr>
            );
          })}
        </Table>
      </Card>
      <div aria-live="polite" className="flex flex-col gap-2">
        {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
        {message ? <SuccessBanner>{message}</SuccessBanner> : null}
      </div>
      {editable ? (
        <div className="flex flex-wrap gap-2">
          <SecondaryButton disabled={busy || dirtyIds.length === 0} onClick={() => void onSave()}>
            {save.isPending ? "Зберігаємо…" : "Зберегти підрахунки"}
          </SecondaryButton>
          <PrimaryButton disabled={busy} onClick={openPost}>
            Провести інвентаризацію
          </PrimaryButton>
          <SecondaryButton disabled={busy} onClick={() => setDialog("cancel")}>
            Скасувати інвентаризацію
          </SecondaryButton>
        </div>
      ) : null}
      {dialog === "post" ? (
        <ModalDialog
          title="Провести інвентаризацію?"
          busy={busy}
          onClose={() => setDialog(null)}
          actions={
            <>
              <PrimaryButton disabled={busy} onClick={() => void onPost()}>
                {busy ? "Проводимо…" : "Так, провести"}
              </PrimaryButton>
              <SecondaryButton disabled={busy} onClick={() => setDialog(null)}>
                Назад
              </SecondaryButton>
            </>
          }
        >
          <p>
            Пораховано позицій: {countedNow}. Різниця є в {diffs}. Залишки й партії буде скориговано рухами «Коригування інвентаризації»; після проведення інвентаризація стане незмінною.
          </p>
          <p>Позиції без підрахунку не змінюються.</p>
        </ModalDialog>
      ) : null}
      {dialog === "cancel" ? (
        <ModalDialog
          title="Скасувати інвентаризацію?"
          busy={busy}
          onClose={() => setDialog(null)}
          actions={
            <>
              <PrimaryButton disabled={busy} onClick={() => void onCancel()}>
                {busy ? "Скасовуємо…" : "Так, скасувати"}
              </PrimaryButton>
              <SecondaryButton disabled={busy} onClick={() => setDialog(null)}>
                Назад
              </SecondaryButton>
            </>
          }
        >
          <p>Підрахунки буде втрачено, рухів не створюється. Розпочати нову інвентаризацію можна будь-коли.</p>
        </ModalDialog>
      ) : null}
    </>
  );
}

/** `/beauty/inventory/stocktakes/{id}`: чернетка з підрахунками, `expectedVersion` при збереженні/проведенні. */
export function InventoryStocktakeDetailScreen({ id }: { id: string }) {
  return (
    <InventoryPage title="Інвентаризація" subtitle={<Link href="/beauty/inventory/stocktakes" className="underline">До списку інвентаризацій</Link>}>
      {(scope) => <Detail id={id} scope={scope} />}
    </InventoryPage>
  );
}
