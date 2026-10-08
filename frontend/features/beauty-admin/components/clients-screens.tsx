"use client";

import { useState } from "react";
import { useAddClientNote, useClient, useClients } from "../hooks/use-beauty-admin";
import { cancelledByLabel } from "../cancellation";
import { initials } from "../format";
import type { ClientProfile } from "../types";
import {
  Avatar,
  Badge,
  Card,
  CLIENT_TAG_VIEW,
  FIELD_INPUT,
  FIELD_LABEL,
  KeyValue,
  KpiGrid,
  LinkButton,
  PageHeader,
  PrimaryButton,
  ProgressBar,
  QueryState,
  RowLink,
  SecondaryButton,
  Table,
  TD,
  Tabs,
  TextLink,
  type Tone,
} from "./ui";

export function ClientsListScreen() {
  const clients = useClients();
  return (
    <>
      <PageHeader title="Клієнти" subtitle="База клієнтів мережі" />
      <QueryState isPending={clients.isPending} isError={clients.isError} onRetry={() => clients.refetch()}>
        <Card className="flex flex-col">
          {clients.data?.map((c) => {
            const tag = CLIENT_TAG_VIEW[c.tag];
            return (
              <div key={c.id} className="flex items-center gap-3 border-t border-(--line) py-2.5 first:border-t-0">
                <Avatar text={initials(c.name)} />
                <span className="flex min-w-0 flex-1 flex-col">
                  <RowLink href={`/beauty/clients/${c.id}`}>
                    <span className="text-[15px] font-semibold no-underline">{c.name}</span>
                  </RowLink>
                  <span className="text-[13px] text-(--muted)">{c.meta}</span>
                </span>
                <Badge tone={tag.tone} className="flex-none">
                  {tag.label}
                </Badge>
              </div>
            );
          })}
        </Card>
      </QueryState>
    </>
  );
}

type Tab = "visits" | "promos" | "notes";
const VISIT_TONE: Record<ClientProfile["visits"][number]["status"], { label: string; tone: Tone }> = {
  completed: { label: "Завершено", tone: "ok" },
  planned: { label: "Заплановано", tone: "now" },
  cancelled: { label: "Скасовано", tone: "neutral" },
  no_show: { label: "Не прийшов", tone: "neutral" },
};

function NoteForm({ clientId }: { clientId: string }) {
  const [text, setText] = useState("");
  const add = useAddClientNote(clientId);
  return (
    <form
      className="mt-1 flex flex-col gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (!text.trim()) return;
        add.mutate(text.trim(), { onSuccess: () => setText("") });
      }}
    >
      <label htmlFor="note" className={FIELD_LABEL}>
        Нова нотатка
      </label>
      <input
        id="note"
        type="text"
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder="Наприклад: не любить сильні запахи"
        className={FIELD_INPUT}
      />
      <div>
        <PrimaryButton type="submit" disabled={add.isPending || !text.trim()}>
          {add.isPending ? "Зберігаємо…" : "Додати нотатку"}
        </PrimaryButton>
      </div>
      {add.isError ? (
        <p role="alert" className="text-sm text-[#8A1F1F]">
          Не вдалося зберегти нотатку.
        </p>
      ) : null}
    </form>
  );
}

export function ClientProfileScreen({ id }: { id: string }) {
  const [tab, setTab] = useState<Tab>("visits");
  const q = useClient(id);
  const c = q.data;

  return (
    <>
      <TextLink href="/beauty/clients">← Усі клієнти</TextLink>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {c === null ? (
          <Card>
            <p className="text-sm">Клієнта не знайдено.</p>
          </Card>
        ) : c ? (
          <>
            <Card className="flex flex-wrap items-center gap-5 p-6">
              <span
                aria-hidden="true"
                className="beauty-display flex size-[72px] flex-none items-center justify-center rounded-full bg-[#EADCF2] text-2xl font-semibold text-[#4A2260]"
              >
                {initials(c.name)}
              </span>
              <div className="flex min-w-0 flex-[1_1_260px] flex-col gap-1.5">
                <div className="flex flex-wrap items-center gap-2.5">
                  <h1 className="beauty-display text-[26px] font-semibold">{c.name}</h1>
                  <Badge tone={CLIENT_TAG_VIEW[c.tag].tone}>{CLIENT_TAG_VIEW[c.tag].label}</Badge>
                </div>
                <div className="text-sm text-(--muted)">{c.contactLine}</div>
              </div>
              <div className="flex flex-wrap gap-2">
                <LinkButton href="/book">Записати</LinkButton>
                <SecondaryButton>Написати</SecondaryButton>
              </div>
            </Card>

            <KpiGrid
              items={[
                ...c.kpis,
                { label: "Скасовано", value: String(c.cancelledCount), note: `з них скасував клієнт: ${c.cancelledByClientCount}` },
              ]}
              min={200}
            />

            <div className="flex flex-wrap items-start gap-6">
              <Card className="flex flex-[999_1_560px] flex-col gap-4">
                <Tabs
                  value={tab}
                  onChange={setTab}
                  tabs={[
                    { id: "visits", label: "Історія візитів" },
                    { id: "promos", label: "Акції клієнта" },
                    { id: "notes", label: "Нотатки" },
                  ]}
                />
                {tab === "visits" ? (
                  <Table headers={["Дата", "Послуга", "Спеціаліст", "Заклад", "Сума", "Статус"]} minWidth={600}>
                    {c.visits.map((v) => (
                      <tr key={v.id} className="border-t border-(--line)">
                        <td className={`${TD} font-semibold`}>{v.dateLabel}</td>
                        <td className={TD}>
                          {v.serviceName}
                          {v.viaPromo ? <Badge tone="promo" className="ml-2 !px-1.5 !py-0.5 !text-xs">акція</Badge> : null}
                        </td>
                        <td className={TD}>
                          <RowLink href={`/beauty/staff/${v.specialistId}`}>{v.specialistName}</RowLink>
                        </td>
                        <td className={`${TD} text-(--muted)`}>{v.locationName}</td>
                        <td className={`${TD} font-semibold`}>{v.sum}</td>
                        <td className={TD}>
                          <Badge tone={VISIT_TONE[v.status].tone}>{VISIT_TONE[v.status].label}</Badge>
                          {v.status === "cancelled" ? (
                            <div className="mt-1.5 flex flex-col gap-0.5 text-[13px] text-(--muted)" data-testid="visit-cancel-info">
                              <span>{cancelledByLabel(v.cancelledBy)}</span>
                              {v.cancelledAtLabel ? <span>{v.cancelledAtLabel}</span> : null}
                              {v.cancelReason ? <span>Причина: {v.cancelReason}</span> : null}
                            </div>
                          ) : null}
                        </td>
                      </tr>
                    ))}
                  </Table>
                ) : null}
                {tab === "promos" ? (
                  <ul className="m-0 flex list-none flex-col gap-2.5 p-0">
                    {c.promos.map((p) => (
                      <li key={p.id} className="flex flex-wrap justify-between gap-3 rounded-xl border border-(--line) p-3.5">
                        <div className="flex flex-col gap-0.5">
                          <span className="text-[15px] font-semibold">{p.name}</span>
                          <span className="text-[13px] text-(--muted)">{p.when}</span>
                        </div>
                        <span className="font-semibold text-(--promo)">{p.saved}</span>
                      </li>
                    ))}
                  </ul>
                ) : null}
                {tab === "notes" ? (
                  <div className="flex flex-col gap-2.5">
                    {c.notes.map((n) => (
                      <div key={n.id} className="flex flex-col gap-1 rounded-xl bg-(--page) p-3.5">
                        <span className="text-sm">{n.text}</span>
                        <span className="text-xs text-(--muted)">{n.meta}</span>
                      </div>
                    ))}
                    <NoteForm clientId={c.id} />
                  </div>
                ) : null}
              </Card>

              <aside className="flex min-w-0 flex-[1_1_300px] flex-col gap-4">
                <Card as="div" className="flex flex-col gap-2.5">
                  <h2 className="text-lg font-semibold">Вподобання</h2>
                  {c.preferences.map((p) => (
                    <KeyValue key={p.label} label={p.label} value={p.value} />
                  ))}
                </Card>
                {c.warning ? (
                  <div role="note" className="rounded-2xl bg-[#FFF1CC] p-4 text-sm leading-snug text-[#5C3900]">
                    <strong>Увага:</strong> {c.warning}
                  </div>
                ) : null}
                <Card as="div" className="flex flex-col gap-2.5">
                  <h2 className="text-lg font-semibold">Лояльність</h2>
                  <KeyValue label="Бонусний баланс" value={c.loyalty.balance} />
                  <div className="text-[13px] text-(--muted)">{c.loyalty.nextLevel}</div>
                  <ProgressBar pct={c.loyalty.progressPct} thin />
                </Card>
              </aside>
            </div>
          </>
        ) : null}
      </QueryState>
    </>
  );
}
