"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { todayInZone } from "../../beauty-shared/zone-time";
import { useBatches, useCreateReceipt, useCreateTransfer, useCreateWriteOff, useInventoryProducts, useSuppliers } from "../hooks/use-inventory";
import {
  REASON_LABEL,
  REASONS_NEEDING_NOTE,
  emptyLine,
  formatQuantity,
  formatYmd,
  humanizeInventoryError,
  nextAttempt,
  parsePrice,
  parseQuantity,
  shortageLines,
  unitShort,
  validateLines,
  validateReceiptHeader,
  validateWriteOffHeader,
  type Attempt,
  type LineDraft,
  type LineErrors,
} from "../inventory/logic";
import type { InventoryScope, Operation, ProductRef, ReceiptRequest, StockLineRequest, StockUnit, WriteOffReason } from "../inventory/types";
import { ModalDialog } from "./modal-dialog";
import { ErrorSummary, InputField, RequiredNote, SelectField, quantityInputMode, useFocusInvalid } from "./inventory-parts";
import { ErrorBanner } from "./staff-parts";
import { PrimaryButton, SecondaryButton } from "./ui";

export type OperationKindForm = "receipt" | "write_off" | "transfer";

const TITLE: Record<OperationKindForm, string> = { receipt: "Надходження товару", write_off: "Списання товару", transfer: "Переміщення між закладами" };
const SUBMIT: Record<OperationKindForm, string> = { receipt: "Зберегти надходження", write_off: "Списати", transfer: "Перемістити" };

/** Ключі рядків для React (лише локальні, на сервер не йдуть). */
let lineSeq = 0;
const newLine = () => emptyLine(`l${++lineSeq}`);

const REASONS = Object.keys(REASON_LABEL) as WriteOffReason[];

/**
 * Форми надходження / списання / переміщення (§32.4–§32.5) у `ModalDialog`. Усі `POST` шлють `Idempotency-Key`: ключ живе, поки
 * тіло запиту не змінилось (повтор після збою - той самий ключ). Списання й переміщення потребують підтвердження.
 */
export function OperationDialog({
  kind,
  scope,
  defaultLocationId,
  onClose,
  onDone,
}: {
  kind: OperationKindForm;
  scope: InventoryScope;
  defaultLocationId: string | null;
  onClose: () => void;
  onDone: (op: Operation) => void;
}) {
  const manageable = scope.locations.filter((l) => l.canManage && (kind === "write_off" || l.isActive));
  const fromOptions = scope.locations.filter((l) => l.canManage);
  const [locationId, setLocationId] = useState(
    (fromOptions.find((l) => l.id === defaultLocationId) ?? (kind === "receipt" ? manageable[0] : fromOptions[0]))?.id ?? "",
  );
  const [toLocationId, setToLocationId] = useState("");
  const [supplierId, setSupplierId] = useState("");
  const [documentNumber, setDocumentNumber] = useState("");
  const [receivedOn, setReceivedOn] = useState("");
  const [note, setNote] = useState("");
  const [reason, setReason] = useState("");
  const [lines, setLines] = useState<LineDraft[]>(() => [newLine()]);
  const [headerErrors, setHeaderErrors] = useState<Record<string, string>>({});
  const [lineErrors, setLineErrors] = useState<Record<number, LineErrors>>({});
  const [serverError, setServerError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const attempt = useRef<Attempt | null>(null);
  const [attachInvalid, requestInvalid] = useFocusInvalid<HTMLFormElement>();
  const confirmTitle = useRef<HTMLParagraphElement>(null);
  // Блок підтвердження з'являється на місці кнопки, що мала фокус: переносимо фокус на його заголовок.
  useEffect(() => {
    if (confirming) confirmTitle.current?.focus();
  }, [confirming]);

  const receipt = useCreateReceipt();
  const writeOff = useCreateWriteOff();
  const transfer = useCreateTransfer();
  const pending = receipt.isPending || writeOff.isPending || transfer.isPending;

  const location = scope.locations.find((l) => l.id === locationId);
  const today = todayInZone(location?.timezone, new Date());
  const canEditCosts = scope.costs === "edit";

  const products = useInventoryProducts({ active: "true", take: 200 }, kind === "receipt");
  const suppliers = useSuppliers({ active: "true", take: 200 }, kind === "receipt");
  const batchesQ = useBatches({ locationId, status: "active", take: 200 }, kind !== "receipt" && locationId !== "");

  /** Для списання/переміщення: товари, що є на залишку закладу, з партіями. */
  const stockProducts = useMemo(() => {
    const map = new Map<string, { ref: ProductRef; total: number }>();
    for (const b of batchesQ.items) {
      const cur = map.get(b.product.id) ?? { ref: b.product, total: 0 };
      cur.total += b.quantity;
      map.set(b.product.id, cur);
    }
    return [...map.values()].sort((a, b) => a.ref.name.localeCompare(b.ref.name, "uk"));
  }, [batchesQ.items]);

  const unitOf = (id: string): StockUnit | undefined =>
    kind === "receipt" ? products.items.find((p) => p.id === id)?.unit : stockProducts.find((p) => p.ref.id === id)?.ref.unit;
  const trackExpiry = (id: string) => products.items.find((p) => p.id === id)?.trackExpiry === true;

  const setLine = (key: string, patch: Partial<LineDraft>) => {
    setLines((cur) => cur.map((l) => (l.key === key ? { ...l, ...patch } : l)));
    setServerError(null);
    setConfirming(false);
  };
  const removeLine = (key: string) => {
    setLines((cur) => (cur.length > 1 ? cur.filter((l) => l.key !== key) : cur));
    setLineErrors({});
    setConfirming(false);
  };

  function validate(): boolean {
    const h: Record<string, string> = {};
    if (!locationId) h.locationId = "Оберіть заклад.";
    if (kind === "receipt") Object.assign(h, validateReceiptHeader({ receivedOn, documentNumber, note }, today));
    if (kind === "write_off") Object.assign(h, validateWriteOffHeader({ reason, note }));
    if (kind === "transfer") {
      if (!toLocationId) h.toLocationId = "Оберіть заклад призначення.";
      else if (toLocationId === locationId) h.toLocationId = "Оберіть інший заклад призначення.";
      if (note.trim().length > 500) h.note = "Коментар: до 500 символів.";
    }
    const le = validateLines(lines, { unitOf, trackExpiry }, { mode: kind === "receipt" ? "receipt" : "stock", today, canEditCosts });
    setHeaderErrors(h);
    setLineErrors(le);
    const ok = Object.keys(h).length === 0 && Object.keys(le).length === 0;
    if (!ok) requestInvalid();
    return ok;
  }

  const stockLines = (): StockLineRequest[] =>
    lines.map((l) => ({ productId: l.productId, quantity: parseQuantity(l.quantity, unitOf(l.productId)!).value!, ...(l.batchId ? { batchId: l.batchId } : {}) }));

  async function send() {
    setServerError(null);
    try {
      let op: Operation;
      if (kind === "receipt") {
        const request: ReceiptRequest = {
          locationId,
          ...(supplierId ? { supplierId } : {}),
          ...(documentNumber.trim() ? { documentNumber: documentNumber.trim() } : {}),
          ...(receivedOn ? { receivedOn } : {}),
          ...(note.trim() ? { note: note.trim() } : {}),
          lines: lines.map((l) => ({
            productId: l.productId,
            quantity: parseQuantity(l.quantity, unitOf(l.productId)!).value!,
            ...(canEditCosts && l.unitCost.trim() ? { unitCost: parsePrice(l.unitCost).value! } : {}),
            ...(l.expiresOn.trim() ? { expiresOn: l.expiresOn.trim() } : {}),
            ...(l.lotNumber.trim() ? { lotNumber: l.lotNumber.trim() } : {}),
          })),
        };
        attempt.current = nextAttempt(attempt.current, request);
        op = await receipt.mutateAsync({ request, key: attempt.current.key });
      } else if (kind === "write_off") {
        const request = { locationId, reason: reason as WriteOffReason, ...(note.trim() ? { note: note.trim() } : {}), lines: stockLines() };
        attempt.current = nextAttempt(attempt.current, request);
        op = await writeOff.mutateAsync({ request, key: attempt.current.key });
      } else {
        const request = { fromLocationId: locationId, toLocationId, ...(note.trim() ? { note: note.trim() } : {}), lines: stockLines() };
        attempt.current = nextAttempt(attempt.current, request);
        op = await transfer.mutateAsync({ request, key: attempt.current.key });
      }
      onDone(op);
      onClose();
    } catch (e) {
      setConfirming(false);
      const short = shortageLines(e);
      if (short.length > 0) {
        const name = (id: string) => stockProducts.find((p) => p.ref.id === id)?.ref;
        setServerError(
          "Недостатньо товару на залишку: " +
            short
              .map((s) => {
                const p = name(s.productId);
                const fmt = (n: number) => (p ? formatQuantity(n, p.unit) : String(n));
                return `рядок ${s.line + 1}${p ? ` («${p.name}»)` : ""} - потрібно ${fmt(s.requested)}, доступно ${fmt(s.available)}`;
              })
              .join("; ") +
            ".",
        );
      } else setServerError(humanizeInventoryError(e, "Не вдалося зберегти операцію. Спробуйте ще раз."));
    }
  }

  function submit() {
    if (!validate()) return;
    if (kind === "receipt") void send();
    else setConfirming(true);
  }

  const noteRequired = kind === "write_off" && (REASONS_NEEDING_NOTE as readonly string[]).includes(reason);
  const fromName = location?.name ?? "";
  const toName = scope.locations.find((l) => l.id === toLocationId)?.name ?? "";

  return (
    <ModalDialog
      title={TITLE[kind]}
      onClose={onClose}
      busy={pending}
      actions={
        <>
          <PrimaryButton disabled={pending || confirming} onClick={submit}>
            {pending ? "Зберігаємо…" : SUBMIT[kind]}
          </PrimaryButton>
          <SecondaryButton disabled={pending} onClick={onClose}>
            Скасувати
          </SecondaryButton>
        </>
      }
    >
      <form
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
        noValidate
        ref={attachInvalid}
        aria-label={TITLE[kind]}
        className="flex flex-col gap-4"
      >
        <RequiredNote />
        <ErrorSummary count={Object.keys(headerErrors).length + Object.values(lineErrors).reduce((n, e) => n + Object.keys(e).length, 0)} />
        <div className="grid gap-3 sm:grid-cols-2">
          <SelectField
            label={kind === "transfer" ? "Звідки" : "Заклад"}
            required
            value={locationId}
            onChange={(v) => {
              setLocationId(v);
              setLines([newLine()]);
              setLineErrors({});
            }}
            options={(kind === "receipt" ? manageable : fromOptions).map((l) => ({ value: l.id, label: l.name }))}
            error={headerErrors.locationId}
          />
          {kind === "transfer" ? (
            <SelectField
              label="Куди"
              required
              value={toLocationId}
              onChange={setToLocationId}
              placeholder="Оберіть заклад"
              options={scope.locations.filter((l) => l.canManage && l.isActive && l.id !== locationId).map((l) => ({ value: l.id, label: l.name }))}
              error={headerErrors.toLocationId}
            />
          ) : null}
          {kind === "receipt" ? (
            <>
              <SelectField
                label="Постачальник"
                value={supplierId}
                onChange={setSupplierId}
                placeholder="Не вказано (з картки товару)"
                options={suppliers.items.map((s) => ({ value: s.id, label: s.name }))}
              />
              <InputField label="Номер документа" value={documentNumber} onChange={setDocumentNumber} maxLength={64} error={headerErrors.documentNumber} />
              <InputField label="Дата надходження" type="date" value={receivedOn} onChange={setReceivedOn} error={headerErrors.receivedOn} hint="Порожньо - сьогодні" />
            </>
          ) : null}
          {kind === "write_off" ? (
            <SelectField
              label="Причина"
              required
              value={reason}
              onChange={setReason}
              placeholder="Оберіть причину"
              options={REASONS.map((r) => ({ value: r, label: REASON_LABEL[r] }))}
              error={headerErrors.reason}
            />
          ) : null}
        </div>
        <InputField
          label={noteRequired ? "Коментар" : "Коментар (необов’язково)"}
          required={noteRequired}
          value={note}
          onChange={setNote}
          maxLength={500}
          error={headerErrors.note}
        />

        <fieldset className="m-0 flex min-w-0 flex-col gap-3 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Позиції</legend>
          {kind !== "receipt" && locationId && batchesQ.isPending ? (
            <p role="status" className="text-sm text-(--muted)">
              Завантажуємо залишки закладу…
            </p>
          ) : null}
          {kind !== "receipt" && !batchesQ.isPending && stockProducts.length === 0 ? (
            <p className="text-sm text-(--muted)">У цьому закладі немає товарів на залишку.</p>
          ) : null}
          {lines.map((l, i) => (
            <LineEditor
              key={l.key}
              index={i}
              line={l}
              kind={kind}
              errors={lineErrors[i]}
              unit={unitOf(l.productId)}
              canEditCosts={canEditCosts}
              today={today}
              productOptions={
                kind === "receipt"
                  ? products.items.map((p) => ({ value: p.id, label: p.name }))
                  : stockProducts.map((p) => ({ value: p.ref.id, label: `${p.ref.name} (є: ${formatQuantity(p.total, p.ref.unit)})` }))
              }
              batchOptions={batchesQ.items
                .filter((b) => b.product.id === l.productId)
                .map((b) => {
                  const expired = b.expiresOn !== null && b.expiresOn < today;
                  const label = `${b.lotNumber ? `${b.lotNumber}, ` : ""}${b.expiresOn ? `до ${formatYmd(b.expiresOn)}` : "без терміну"}, ${formatQuantity(b.quantity, b.product.unit)}${expired ? " - прострочена" : ""}`;
                  return { value: b.id, label, disabled: kind === "transfer" && expired };
                })}
              onChange={(patch) => setLine(l.key, patch)}
              onRemove={lines.length > 1 ? () => removeLine(l.key) : undefined}
            />
          ))}
          {lines.length < 100 ? (
            <div>
              <SecondaryButton onClick={() => setLines((cur) => [...cur, newLine()])}>Додати позицію</SecondaryButton>
            </div>
          ) : null}
        </fieldset>

        {confirming ? (
          <div role="group" aria-labelledby="op-confirm-t" aria-describedby="op-confirm-d" className="flex flex-col gap-3 rounded-xl bg-[#FFF1CC] p-4 text-[#5C3900]">
            <p id="op-confirm-t" ref={confirmTitle} tabIndex={-1} className="text-sm font-semibold outline-none">
              Підтвердіть операцію
            </p>
            <p id="op-confirm-d" className="text-sm">
              {kind === "write_off"
                ? `Списати ${lines.length} поз. із закладу «${fromName}»? Рух не редагується: помилку виправляють лише новим списанням з причиною «${REASON_LABEL.correction}».`
                : `Перемістити ${lines.length} поз. із «${fromName}» до «${toName}»? Переміщення відбувається одразу, партії зберігають терміни придатності.`}
            </p>
            <div className="flex flex-wrap gap-2">
              <PrimaryButton disabled={pending} onClick={() => void send()}>
                {kind === "write_off" ? "Так, списати" : "Так, перемістити"}
              </PrimaryButton>
              <SecondaryButton onClick={() => setConfirming(false)}>Назад</SecondaryButton>
            </div>
          </div>
        ) : null}
        {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
      </form>
    </ModalDialog>
  );
}

function LineEditor({
  index,
  line,
  kind,
  errors,
  unit,
  canEditCosts,
  today,
  productOptions,
  batchOptions,
  onChange,
  onRemove,
}: {
  index: number;
  line: LineDraft;
  kind: OperationKindForm;
  errors: LineErrors | undefined;
  unit: StockUnit | undefined;
  canEditCosts: boolean;
  today: string;
  productOptions: { value: string; label: string }[];
  batchOptions: { value: string; label: string; disabled?: boolean }[];
  onChange: (patch: Partial<LineDraft>) => void;
  onRemove?: () => void;
}) {
  const n = index + 1;
  return (
    <div role="group" aria-label={`Позиція ${n}`} className="flex flex-col gap-3 rounded-xl border border-(--line-strong) p-3">
      <SelectField
        label={`Товар (позиція ${n})`}
        required
        value={line.productId}
        onChange={(v) => onChange({ productId: v, batchId: "" })}
        placeholder="Оберіть товар"
        options={productOptions}
        error={errors?.productId}
      />
      <InputField
        label="Кількість"
        required
        inputMode={quantityInputMode(unit)}
        value={line.quantity}
        onChange={(v) => onChange({ quantity: v })}
        suffix={unit ? unitShort(unit) : undefined}
        hint={unit && (unit === "pcs" || unit === "pack") ? "Лише ціле число" : undefined}
        error={errors?.quantity}
      />
      {kind === "receipt" ? (
        <div className="grid gap-3 sm:grid-cols-2">
          <InputField label="Термін придатності" type="date" value={line.expiresOn} onChange={(v) => onChange({ expiresOn: v })} error={errors?.expiresOn} hint={`Не раніше ${formatYmd(today)}`} />
          <InputField label="Номер партії" value={line.lotNumber} onChange={(v) => onChange({ lotNumber: v })} maxLength={64} error={errors?.lotNumber} />
          {canEditCosts ? (
            <InputField label="Закупівельна ціна за одиницю" inputMode="decimal" value={line.unitCost} onChange={(v) => onChange({ unitCost: v })} suffix="₴" error={errors?.unitCost} hint="Порожньо - ціна з картки товару" />
          ) : null}
        </div>
      ) : (
        <SelectField
          label="Партія"
          value={line.batchId}
          onChange={(v) => onChange({ batchId: v })}
          placeholder={kind === "transfer" ? "Автоматично (найближчий термін, без прострочених)" : "Автоматично (найближчий термін)"}
          options={batchOptions}
          disabled={!line.productId}
        />
      )}
      {errors && Object.keys(errors).length > 0 ? (
        <p className="sr-only">У позиції {n} є помилки</p>
      ) : null}
      {onRemove ? (
        <div>
          <SecondaryButton onClick={onRemove} aria-label={`Видалити позицію ${n}`}>
            Видалити позицію
          </SecondaryButton>
        </div>
      ) : null}
    </div>
  );
}
