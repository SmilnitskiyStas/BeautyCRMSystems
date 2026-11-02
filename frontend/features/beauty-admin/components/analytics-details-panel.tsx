"use client";

import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { humanizeError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import { detailsCsv, downloadCsv } from "../analytics/csv";
import { collectDetailsItems, detailsParams, type DetailsTarget } from "../analytics/details";
import { detailsTitle } from "../analytics/series";
import { completedWord, moneyExact, percentValue, shortDate } from "../format";
import { useNetworkDetails } from "../hooks/use-finance";
import type { DetailsItem, DetailsSummary } from "../types";
import { Badge, Chip, PrimaryButton, SecondaryButton, TextLink } from "./ui";

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function Stat({ label, value, strong, tone }: { label: string; value: string; strong?: boolean; tone?: "bad" }) {
  return (
    <div className="flex min-w-0 flex-col gap-0.5 rounded-xl bg-white p-3">
      <dt className="text-xs text-(--muted)">{label}</dt>
      <dd className={`m-0 text-base break-words ${strong ? "font-semibold" : "font-medium"} ${tone === "bad" ? "text-[#9B1C1C]" : ""}`}>{value}</dd>
    </div>
  );
}

/** Підсумок періоду: усі складові окремо (§24.8.1). Від'ємний чистий дохід підсвічується, а не лише мінусом. */
function Summary({ s }: { s: DetailsSummary }) {
  return (
    <dl data-testid="details-summary" className="m-0 grid grid-cols-2 gap-2">
      <Stat label="Виручка" value={moneyExact(s.revenue)} strong />
      <Stat label="Собівартість" value={moneyExact(s.cost)} />
      <Stat label="Виплата майстру" value={moneyExact(s.masterPayout)} />
      <Stat label="Чистий дохід" value={moneyExact(s.netIncome)} strong tone={s.netIncome < 0 ? "bad" : undefined} />
      <Stat label="Маржа" value={percentValue(s.marginPercent)} />
      <Stat label="Завершено" value={String(s.completed)} />
      <Stat label="Знижки" value={moneyExact(s.discountTotal)} />
      <Stat label="Без собівартості" value={String(s.withoutCost)} />
    </dl>
  );
}

function ItemCard({ i, showDate }: { i: DetailsItem; showDate: boolean }) {
  return (
    <li data-testid="details-item" data-cost-missing={i.costMissing || undefined} className="flex flex-col gap-2 rounded-xl bg-white p-3">
      <div className="flex flex-col gap-0.5">
        <span className="text-[15px] font-semibold break-words">
          {showDate ? `${shortDate(i.localDate)}, ` : ""}
          {i.localTime} · {i.service.name}
        </span>
        <span className="text-[13px] break-words text-(--muted)">
          {i.specialist.name} · {i.location.name}
        </span>
      </div>
      <dl className="m-0 grid grid-cols-[1fr_auto] gap-x-3 gap-y-1 text-sm">
        <dt className="text-(--muted)">Ціна продажу</dt>
        <dd className="m-0 text-right font-semibold">{moneyExact(i.priceFinal)}</dd>
        {i.discount > 0 || i.promotion ? (
          <>
            <dt className="text-(--muted)">Знижка{i.promotion ? `: ${i.promotion.name}` : ""}</dt>
            <dd className="m-0 text-right">{i.discount > 0 ? `−${moneyExact(i.discount)}` : moneyExact(0)}</dd>
          </>
        ) : null}
        <dt className="text-(--muted)">Собівартість</dt>
        <dd className="m-0 text-right">{i.costMissing || i.cost === null ? "не задана" : moneyExact(i.cost)}</dd>
        <dt className="text-(--muted)">Виплата майстру</dt>
        <dd className="m-0 text-right">
          {i.costMissing || i.masterPayout === null ? "—" : `${moneyExact(i.masterPayout)}${i.masterPercent ? ` (${percentValue(i.masterPercent)})` : ""}`}
        </dd>
        <dt className="font-semibold">Чистий внесок</dt>
        <dd className={`m-0 text-right font-semibold ${i.net < 0 ? "text-[#9B1C1C]" : ""}`}>{moneyExact(i.net)}</dd>
      </dl>
      {i.costMissing ? (
        <Badge tone="wait" className="self-start">
          без собівартості
        </Badge>
      ) : null}
    </li>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-sm font-semibold">{title}</h3>
      {children}
    </section>
  );
}

/**
 * Панель «Деталі» (§24.8.1): `role="dialog"` `aria-modal`, фокус на заголовку, пастка Tab, Esc закриває, фокус повертається
 * на стовпець графіка. ≥ 768 px - бічна панель 480 px, менше - повноекранна. Записи - картки (без горизонтального скролу на 320 px).
 * Даних клієнтів тут немає за контрактом (§24.6.3).
 */
export function AnalyticsDetailsPanel({ target, returnFocus, onClose }: { target: DetailsTarget; returnFocus: HTMLElement | null; onClose: () => void }) {
  const titleId = useId();
  const root = useRef<HTMLDivElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const [locationId, setLocationId] = useState<string | null>(null);
  const [csv, setCsv] = useState<{ busy: boolean; error: string | null; note: string | null }>({ busy: false, error: null, note: null });

  const base = useNetworkDetails(target, null);
  const filtered = useNetworkDetails(target, locationId, locationId !== null);
  const q = locationId ? filtered : base;
  const pages = q.data?.pages ?? [];
  const first = pages[0];
  const items = pages.flatMap((p) => p.items);
  // Чипи закладів беремо з відповіді БЕЗ фільтра: з фільтром `locations` звужується до одного закладу.
  const chips = base.data?.pages[0]?.locations ?? [];

  useEffect(() => {
    heading.current?.focus();
    return () => {
      if (returnFocus?.isConnected) returnFocus.focus();
    };
  }, [returnFocus]);

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape") {
      e.stopPropagation();
      onClose();
      return;
    }
    if (e.key !== "Tab" || !root.current) return;
    const list = Array.from(root.current.querySelectorAll<HTMLElement>(FOCUSABLE));
    if (list.length === 0) return;
    const firstEl = list[0];
    const lastEl = list[list.length - 1];
    const active = document.activeElement;
    if (e.shiftKey && (active === firstEl || active === heading.current || !root.current.contains(active))) {
      e.preventDefault();
      lastEl.focus();
    } else if (!e.shiftKey && (active === lastEl || !root.current.contains(active))) {
      e.preventDefault();
      firstEl.focus();
    }
  }

  async function exportCsv() {
    if (!first) return;
    const last = pages[pages.length - 1];
    setCsv({ busy: true, error: null, note: null });
    try {
      const params = detailsParams(target, locationId);
      const got = await collectDetailsItems({ items, hasMore: last.hasMore, nextCursor: last.nextCursor }, (cursor) => beautyApi.getNetworkDetails({ ...params, cursor, take: 200 }));
      downloadCsv(detailsCsv(first.summary, got.items, { fromDate: first.fromDate, toDate: first.toDate }, got.truncated));
      setCsv({ busy: false, error: null, note: got.truncated ? `У файлі перші ${got.items.length} із ${first.summary.completed} записів.` : null });
    } catch (e) {
      setCsv({ busy: false, error: humanizeError(e, "Не вдалося підготувати файл. Спробуйте ще раз."), note: null });
    }
  }

  const s = first?.summary;
  const nextError = q.isFetchNextPageError ? humanizeError(q.error, "Не вдалося завантажити наступні записи.") : null;
  return (
    <div className="fixed inset-0 z-50 flex justify-end">
      <div aria-hidden="true" className="absolute inset-0 bg-black/40" onClick={onClose} />
      <div
        ref={root}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={onKeyDown}
        className="relative flex h-full w-full min-w-0 flex-col gap-4 overflow-y-auto overflow-x-hidden bg-(--page) p-4 md:w-[480px] md:shadow-xl"
      >
        <div className="flex items-start justify-between gap-3">
          <h2 id={titleId} ref={heading} tabIndex={-1} className="beauty-display min-w-0 text-xl font-semibold break-words outline-none">
            {detailsTitle(target, target.granularity)}
          </h2>
          <SecondaryButton onClick={onClose} aria-label="Закрити деталі" className="flex-none !px-4">
            Закрити
          </SecondaryButton>
        </div>

        {chips.length > 1 ? (
          <div className="flex flex-wrap gap-2" role="group" aria-label="Заклад">
            <Chip variant="soft" pressed={locationId === null} onClick={() => setLocationId(null)}>
              Усі заклади
            </Chip>
            {chips.map((l) => (
              <Chip key={l.locationId} variant="soft" pressed={locationId === l.locationId} onClick={() => setLocationId(l.locationId)}>
                {l.name}
              </Chip>
            ))}
          </div>
        ) : null}

        {q.isPending ? (
          <div role="status" aria-live="polite" className="rounded-xl bg-white p-4 text-sm text-(--muted)">
            Завантаження…
          </div>
        ) : q.isError ? (
          <div role="alert" className="flex flex-col items-start gap-3 rounded-xl bg-white p-4 text-sm">
            <span>{humanizeError(q.error, "Не вдалося завантажити деталі.")}</span>
            <SecondaryButton onClick={() => void q.refetch()}>Спробувати ще раз</SecondaryButton>
          </div>
        ) : s ? (
          <>
            <Summary s={s} />
            {s.withoutCost > 0 ? (
              <div role="status" data-testid="details-without-cost" className="flex flex-col items-start gap-1 rounded-xl bg-[#FFF1CC] p-3 text-sm text-[#5C3900]">
                <span>
                  {s.withoutCost} {completedWord(s.withoutCost)} без собівартості ({moneyExact(s.revenueWithoutCost)} виручки) — чистий дохід завищено.
                </span>
                <TextLink href="/beauty/services">Задати собівартість →</TextLink>
              </div>
            ) : null}

            {first.locations.length > 0 ? (
              <Section title="По закладах">
                <ul aria-label="Розбивка по закладах" className="m-0 flex list-none flex-col gap-2 p-0">
                  {first.locations.map((l) => (
                    <li key={l.locationId} data-testid="details-location" className="flex flex-col gap-1 rounded-xl bg-white p-3 text-sm">
                      <span className="font-semibold break-words">{l.name}</span>
                      <span className="text-(--muted)">
                        Виручка {moneyExact(l.revenue)} · собівартість {moneyExact(l.cost)} · виплата {moneyExact(l.masterPayout)}
                      </span>
                      <span>
                        Чистий дохід <strong className={l.netIncome < 0 ? "text-[#9B1C1C]" : ""}>{moneyExact(l.netIncome)}</strong> · маржа {percentValue(l.marginPercent)} · завершено {l.completed}
                        {l.withoutCost > 0 ? ` · без собівартості ${l.withoutCost}` : ""}
                      </span>
                    </li>
                  ))}
                </ul>
              </Section>
            ) : null}

            <Section title="Записи">
              {items.length === 0 ? (
                <p className="m-0 rounded-xl bg-white p-3 text-sm text-(--muted)">Завершених записів немає</p>
              ) : (
                <ul aria-label="Завершені записи" className="m-0 flex list-none flex-col gap-2 p-0">
                  {items.map((i) => (
                    <ItemCard key={i.appointmentId} i={i} showDate={target.granularity !== "day"} />
                  ))}
                </ul>
              )}
              {nextError ? (
                <p role="alert" className="m-0 text-sm text-[#9B1C1C]">
                  {nextError}
                </p>
              ) : null}
              {q.hasNextPage ? (
                <div>
                  <SecondaryButton disabled={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
                    {q.isFetchingNextPage ? "Завантажуємо…" : "Показати ще"}
                  </SecondaryButton>
                </div>
              ) : null}
            </Section>

            <div className="flex flex-col items-start gap-2">
              <PrimaryButton disabled={csv.busy} onClick={() => void exportCsv()}>
                {csv.busy ? "Готуємо файл…" : "Експорт CSV"}
              </PrimaryButton>
              {csv.error ? (
                <p role="alert" className="m-0 text-sm text-[#9B1C1C]">
                  {csv.error}
                </p>
              ) : null}
              {csv.note ? (
                <p role="status" className="m-0 text-sm text-(--muted)">
                  {csv.note}
                </p>
              ) : null}
            </div>
          </>
        ) : null}
      </div>
    </div>
  );
}
