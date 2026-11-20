"use client";

import { useId, useState, type FormEvent, type ReactNode } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canManageInventory } from "@/features/beauty-auth/permissions";
import { dateTimeLabel } from "../format";
import { useCancelProductOrder, useFulfillProductOrder, useProductOrder, useProductOrders, useReconcileProductOrderPayment } from "../hooks/use-product-orders";
import { formatMoney, isYmd } from "../inventory/logic";
import {
  CANCEL_REASON_MAX,
  ORDER_PAYMENT_LABEL,
  ORDER_SOURCE_LABEL,
  ORDER_STATUS_FILTERS,
  ORDER_STATUS_VIEW,
  cancelButtonLabel,
  cancelExplanation,
  closeReasonLabel,
  customerPhone,
  humanizeOrderError,
  orderActions,
  orderIndicators,
  orderLineQuantity,
  orderNumberLabel,
  orderSizeLabel,
  orderStatusLabel,
  orderTotalLabel,
  parseOrderNumber,
  phoneAriaLabel,
  validateCancelReason,
  type ProductOrder,
  type ProductOrderCustomer,
  type ProductOrderStatusFilter,
  type ProductOrderSummary,
} from "../inventory/orders";
import type { InventoryScope } from "../inventory/types";
import { InputField, InventoryPage, LoadMore, LocationChips } from "./inventory-parts";
import { ModalDialog } from "./modal-dialog";
import { Badge, Card, Chip, EmptyState, FIELD_INPUT, FIELD_LABEL, PrimaryButton, QueryState, SecondaryButton, Table, TD } from "./ui";

const ERROR_BOX = "rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]";

/** Телефон: повний або маска від сервера; для скрінрідера маска озвучується словами. */
export function OrderPhone({ customer }: { customer: Pick<ProductOrderCustomer, "phone" | "phoneMasked"> }) {
  const p = customerPhone(customer);
  const label = phoneAriaLabel(customer);
  if (!label) return <span>{p.text}</span>;
  return (
    <span data-testid="order-phone-masked">
      <span aria-hidden="true">{p.text}</span>
      <span className="sr-only">{label}</span>
    </span>
  );
}

/** Статус словами + індикатори «оплата/повернення в процесі» (колір - не єдиний сигнал). */
export function OrderStatusBadges({ order }: { order: Pick<ProductOrderSummary, "status" | "paymentInProgress" | "refundInProgress"> }) {
  const view = ORDER_STATUS_VIEW[order.status];
  return (
    <span className="flex flex-col items-start gap-1.5">
      <Badge tone={view.tone}>{view.label}</Badge>
      {orderIndicators(order).map((i) => (
        <Badge key={i} tone="wait">
          {i}
        </Badge>
      ))}
    </span>
  );
}

// ---------- деталі ----------

type Mode = "view" | "fulfill" | "cancel";

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid gap-0.5 sm:grid-cols-[150px_1fr] sm:gap-3">
      <dt className="text-[13px] font-semibold text-(--muted)">{label}</dt>
      <dd className="m-0">{children}</dd>
    </div>
  );
}

function OrderDetails({ order, tz }: { order: ProductOrder; tz?: string }) {
  const at = (v: string | null) => (v ? dateTimeLabel(v, tz) : null);
  const reason = closeReasonLabel(order.closeReason);
  return (
    <>
      <dl className="m-0 flex flex-col gap-2">
        <Row label="Статус">
          <OrderStatusBadges order={order} />
        </Row>
        <Row label="Заклад">{order.location.name}</Row>
        <Row label="Клієнт">
          {order.customer.name || "—"}, <OrderPhone customer={order.customer} />
        </Row>
        <Row label="Створено">
          {at(order.createdAt)}
          <span className="text-(--muted)"> · {ORDER_SOURCE_LABEL[order.source]}</span>
        </Row>
        {order.reservationExpiresAt ? <Row label="Резерв до">{at(order.reservationExpiresAt)}</Row> : null}
        <Row label="Оплата">
          {order.payment ? `${ORDER_PAYMENT_LABEL[order.payment.status]}, спроб: ${order.payment.attempts}` : "Оплати ще не було"}
          {order.paidAt ? <span className="text-(--muted)"> · {at(order.paidAt)}</span> : null}
        </Row>
        {order.fulfilledAt ? (
          <Row label="Видано">
            {at(order.fulfilledAt)}
            {order.fulfilledBy ? `, ${order.fulfilledBy.name}` : ""}
          </Row>
        ) : null}
        {order.closedAt ? (
          <Row label="Закрито">
            {at(order.closedAt)}
            {reason ? ` · ${reason}` : ""}
            {order.cancelledBy ? `, ${order.cancelledBy.name}` : ""}
          </Row>
        ) : null}
        {order.refundAmount !== null && order.refundAmount > 0 ? <Row label="Повернено">{formatMoney(order.refundAmount)}</Row> : null}
        {order.cancelNote ? <Row label="Нотатка">{order.cancelNote}</Row> : null}
      </dl>
      <Table caption={`Склад замовлення ${orderNumberLabel(order.number)}`} headers={["Товар", "Кількість", "Ціна", "Сума"]} minWidth={420}>
        {order.lines.map((l, i) => (
          <tr key={`${l.product.id}-${i}`} className="border-t border-(--line)">
            <th scope="row" className={`${TD} text-left font-semibold`}>
              {l.name}
            </th>
            <td className={`${TD} whitespace-nowrap`}>{orderLineQuantity(l)}</td>
            <td className={`${TD} whitespace-nowrap`}>{formatMoney(l.unitPrice)}</td>
            <td className={`${TD} whitespace-nowrap`}>{formatMoney(l.lineTotal)}</td>
          </tr>
        ))}
      </Table>
      <p className="m-0 text-right text-[15px] font-semibold">Разом: {orderTotalLabel(order)}</p>
    </>
  );
}

function OrderDialog({ summary, scope, onClose }: { summary: ProductOrderSummary; scope: InventoryScope; onClose: () => void }) {
  const { user } = useAuth();
  const q = useProductOrder(summary.id);
  const fulfill = useFulfillProductOrder();
  const cancel = useCancelProductOrder();
  const reconcile = useReconcileProductOrderPayment();
  const [mode, setMode] = useState<Mode>("view");
  const [reason, setReason] = useState("");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const reasonId = useId();

  const order = q.data;
  const loc = scope.locations.find((l) => l.id === (order ?? summary).location.id);
  const actions = order ? orderActions(order, canManageInventory(user) && loc?.canManage === true) : null;
  const busy = fulfill.isPending || cancel.isPending || reconcile.isPending;
  const reasonError = validateCancelReason(reason);

  const fail = (e: unknown) => {
    setError(humanizeOrderError(e));
    setNote("");
    setMode("view");
  };
  const doFulfill = () => {
    setError(null);
    fulfill.mutate(summary.id, {
      onSuccess: () => {
        setNote(`Замовлення ${orderNumberLabel(summary.number)} видано клієнту.`);
        setMode("view");
      },
      onError: fail,
    });
  };
  const doCancel = () => {
    if (reasonError) return;
    setError(null);
    cancel.mutate(
      { id: summary.id, ...(reason.trim() ? { reason: reason.trim() } : {}) },
      {
        onSuccess: (r) => {
          setNote(r.refundAmount > 0 ? `Замовлення скасовано. Клієнту буде повернено ${formatMoney(r.refundAmount)}, товар повернено на склад.` : "Замовлення скасовано.");
          setMode("view");
          setReason("");
        },
        onError: fail,
      },
    );
  };
  const doReconcile = () => {
    setError(null);
    reconcile.mutate(summary.id, {
      onSuccess: (o) => setNote(o.status === "paid" ? "Оплату підтверджено: замовлення оплачене й готове до видачі." : `Оплату перевірено. Статус: ${orderStatusLabel(o.status)}.`),
      onError: fail,
    });
  };

  const footer =
    mode === "fulfill" && order ? (
      <>
        <PrimaryButton onClick={doFulfill} disabled={busy}>
          {fulfill.isPending ? "Видаємо…" : "Підтвердити видачу"}
        </PrimaryButton>
        <SecondaryButton onClick={() => setMode("view")} disabled={busy}>
          Назад
        </SecondaryButton>
      </>
    ) : mode === "cancel" && order ? (
      <>
        <PrimaryButton onClick={doCancel} disabled={busy || reasonError !== null}>
          {cancel.isPending ? "Скасовуємо…" : cancelButtonLabel(order)}
        </PrimaryButton>
        <SecondaryButton onClick={() => setMode("view")} disabled={busy}>
          Назад
        </SecondaryButton>
      </>
    ) : (
      <>
        {actions?.fulfill ? <PrimaryButton onClick={() => (setError(null), setNote(""), setMode("fulfill"))}>Видати</PrimaryButton> : null}
        {actions?.reconcile ? (
          <SecondaryButton onClick={doReconcile} disabled={busy}>
            {reconcile.isPending ? "Перевіряємо…" : "Перевірити оплату"}
          </SecondaryButton>
        ) : null}
        {actions?.cancel ? <SecondaryButton onClick={() => (setError(null), setNote(""), setMode("cancel"))}>Скасувати</SecondaryButton> : null}
        <SecondaryButton onClick={onClose} disabled={busy}>
          Закрити
        </SecondaryButton>
      </>
    );

  return (
    <ModalDialog title={`Замовлення ${orderNumberLabel(summary.number)}`} onClose={onClose} busy={busy} actions={footer}>
      <p role="status" className="m-0 text-sm font-semibold text-[#145A32] empty:hidden">
        {note}
      </p>
      {error ? (
        <p role="alert" className={ERROR_BOX}>
          {error}
        </p>
      ) : null}
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {order ? (
          <>
            {mode === "fulfill" ? (
              <p className="m-0 rounded-xl bg-(--tint) px-3.5 py-2.5">Підтвердьте, що товар передано клієнту ({order.customer.name || "клієнт"}). Після видачі замовлення закривається, а залишок списується зі складу.</p>
            ) : null}
            {mode === "cancel" ? (
              <div className="flex flex-col gap-2">
                <p className="m-0 rounded-xl bg-(--tint) px-3.5 py-2.5">{cancelExplanation(order)}</p>
                <label htmlFor={reasonId} className={FIELD_LABEL}>
                  Причина (необов’язково)
                </label>
                <textarea
                  id={reasonId}
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  rows={3}
                  aria-invalid={reasonError ? true : undefined}
                  aria-describedby={`${reasonId}-hint`}
                  className={`${FIELD_INPUT} py-2 aria-[invalid=true]:border-[#8A1F1F]`}
                />
                <p id={`${reasonId}-hint`} className={`m-0 text-[13px] ${reasonError ? "text-[#8A1F1F]" : "text-(--muted)"}`}>
                  {reasonError ?? `До ${CANCEL_REASON_MAX} символів. Бачать лише співробітники. Введено: ${reason.trim().length}.`}
                </p>
              </div>
            ) : null}
            {mode === "view" && order.paymentInProgress ? (
              <p className="m-0 rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-[#7A4B00]">Оплата ще обробляється. Скасувати замовлення можна після перевірки оплати.</p>
            ) : null}
            <OrderDetails order={order} tz={loc?.timezone} />
          </>
        ) : null}
      </QueryState>
    </ModalDialog>
  );
}

// ---------- список ----------

function OrdersList({
  status,
  locationId,
  from,
  to,
  number,
  scope,
  enabled,
}: {
  status: ProductOrderStatusFilter;
  locationId: string | null;
  from: string;
  to: string;
  number: number | null;
  scope: InventoryScope;
  enabled: boolean;
}) {
  const q = useProductOrders(
    {
      status,
      ...(locationId ? { locationId } : {}),
      ...(from ? { from: `${from}T00:00:00Z` } : {}),
      ...(to ? { to: `${to}T23:59:59Z` } : {}),
      ...(number !== null ? { number } : {}),
      take: 50,
    },
    enabled,
  );
  // Діалог тримає знімок рядка: після дії список перезавантажується і замовлення може зникнути з фільтра.
  const [dialog, setDialog] = useState<ProductOrderSummary | null>(null);

  return (
    <>
      <QueryState isPending={q.isPending && enabled} isError={q.isError} onRetry={() => q.refetch()}>
        {q.items.length === 0 ? (
          <Card>
            <EmptyState>
              {number !== null ? `Замовлення ${orderNumberLabel(number)} за цими умовами не знайдено.` : status === "open" ? "Оплачених замовлень до видачі немає." : "Замовлень за цими умовами немає."}
            </EmptyState>
          </Card>
        ) : (
          <Card>
            <Table caption="Замовлення товарів" headers={["№", "Дата", "Заклад", "Клієнт", "Позицій", "Сума", "Статус"]} minWidth={900}>
              {q.items.map((o) => (
                <tr key={o.id} data-testid="order-row" className={`border-t border-(--line) align-top ${o.status === "paid" ? "bg-[#F3F6FF]" : ""}`}>
                  <th scope="row" className={`${TD} text-left font-semibold whitespace-nowrap`}>
                    <button
                      type="button"
                      onClick={() => setDialog(o)}
                      aria-label={`Відкрити замовлення ${orderNumberLabel(o.number)}`}
                      className="inline-flex min-h-11 cursor-pointer items-center text-[15px] font-semibold text-(--accent) underline"
                    >
                      {orderNumberLabel(o.number)}
                    </button>
                  </th>
                  <td className={`${TD} whitespace-nowrap`}>{dateTimeLabel(o.createdAt, scope.locations.find((l) => l.id === o.location.id)?.timezone)}</td>
                  <td className={TD}>{o.location.name}</td>
                  <td className={TD}>
                    {o.customer.name || "—"}
                    <span className="block text-[13px] text-(--muted)">
                      <OrderPhone customer={o.customer} />
                    </span>
                  </td>
                  <td className={`${TD} whitespace-nowrap`}>{orderSizeLabel(o)}</td>
                  <td className={`${TD} whitespace-nowrap font-semibold`}>{orderTotalLabel(o)}</td>
                  <td className={TD}>
                    <OrderStatusBadges order={o} />
                  </td>
                </tr>
              ))}
            </Table>
            <div className="mt-3">
              <LoadMore hasMore={q.hasNextPage} loading={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()} shown={q.items.length} />
            </div>
          </Card>
        )}
      </QueryState>
      {dialog ? <OrderDialog summary={dialog} scope={scope} onClose={() => setDialog(null)} /> : null}
    </>
  );
}

/** `/beauty/inventory/orders` (§33.13): замовлення товарів клієнтами - видача, скасування з поверненням, перевірка оплати. */
export function InventoryOrdersScreen() {
  const [status, setStatus] = useState<ProductOrderStatusFilter>("open");
  const [location, setLocation] = useState<string | null>(null);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [draft, setDraft] = useState("");
  const [number, setNumber] = useState<number | null>(null);
  const [numberError, setNumberError] = useState<string | undefined>();
  const rangeOk = (from === "" || isYmd(from)) && (to === "" || isYmd(to)) && !(from && to && from > to);

  const search = (e: FormEvent) => {
    e.preventDefault();
    const n = parseOrderNumber(draft);
    if (n === "invalid") {
      setNumberError("Вкажіть номер цифрами, наприклад 1042.");
      return;
    }
    setNumberError(undefined);
    setNumber(n);
  };
  const reset = () => {
    setDraft("");
    setNumber(null);
    setNumberError(undefined);
  };

  return (
    <InventoryPage title="Замовлення" subtitle="Замовлення товарів клієнтами: видача, скасування з поверненням коштів, перевірка оплати">
      {(scope) => (
        <>
          <LocationChips locations={scope.locations} value={location} onChange={setLocation} />
          <div className="flex flex-wrap gap-2" role="group" aria-label="Статус замовлення">
            {ORDER_STATUS_FILTERS.map((f) => (
              <Chip key={f.id} pressed={status === f.id} onClick={() => setStatus(f.id)} variant="soft">
                {f.label}
              </Chip>
            ))}
          </div>
          <div className="grid gap-3 sm:grid-cols-3">
            <InputField label="Створено від" type="date" value={from} onChange={setFrom} />
            <InputField label="Створено до" type="date" value={to} onChange={setTo} error={rangeOk ? undefined : "Перевірте дати: «Від» не пізніше за «До»."} hint="Порожньо - останні 30 діб" />
            <form onSubmit={search} role="search" aria-label="Пошук за номером" className="flex items-end gap-2">
              <div className="min-w-0 flex-1">
                <InputField label="Номер замовлення" value={draft} onChange={setDraft} inputMode="numeric" error={numberError} hint="Працює разом з фільтрами статусу й періоду" />
              </div>
              <SecondaryButton type="submit">Знайти</SecondaryButton>
              {number !== null || draft ? (
                <SecondaryButton type="button" onClick={reset}>
                  Скинути
                </SecondaryButton>
              ) : null}
            </form>
          </div>
          <OrdersList key={`${status}|${location}|${from}|${to}|${number}`} status={status} locationId={location} from={from} to={to} number={number} scope={scope} enabled={rangeOk} />
        </>
      )}
    </InventoryPage>
  );
}
