"use client";

import Link from "next/link";
import { useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { canSeeClosureReason } from "../closed-days";
import { useLocations, useManagedLocations, useSetLocationWorkingHours, useStaff } from "../hooks/use-beauty-admin";
import { closedWeekdaysLabel, mapUrl, staffOfLocation } from "../location-hours";
import type { BeautyLocation, OutsideHoursConflict, WorkingHours } from "../types";
import { defaultHours, normalizeHours, summarizeHours, validateHours, WEEKDAYS } from "../working-hours";
import { ClosedDaysConflictDialog, ClosedDaysSection } from "./closed-days";
import { LocationForm } from "./location-form";
import { CheckRow, ERROR_TEXT, ErrorBanner, SuccessBanner, WorkingHoursEditor } from "./staff-parts";
import { Badge, Card, EmptyState, PrimaryButton, QueryState, SecondaryButton, TextLink } from "./ui";

/** Бекенд просить підтвердження: нові години залишають активні записи поза робочим часом (§18.1). */
const isOutsideHoursConfirm = (e: unknown): e is BeautyApiError =>
  e instanceof BeautyApiError && e.status === 409 && e.code === "has_appointments_outside_hours";

const sameHours = (a: WorkingHours | null, b: WorkingHours | null) =>
  a === null || b === null ? a === b : JSON.stringify(normalizeHours(a)) === JSON.stringify(normalizeHours(b));

/** Посилання «Відкрити на карті» - лише для непорожньої адреси; безпечне відкриття в новій вкладці. */
export function MapLink({ address }: { address: string | null | undefined }) {
  const href = mapUrl(address);
  if (!href) return null;
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex min-h-11 items-center text-sm font-semibold text-(--accent) underline"
    >
      Відкрити на карті
    </a>
  );
}

function ReadOnlyRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-2">
      <dt className="w-28 flex-none text-(--muted)">{label}</dt>
      <dd className="m-0 min-w-0 break-words">{children}</dd>
    </div>
  );
}

function BasicSection({ location, canEdit }: { location: BeautyLocation; canEdit: boolean }) {
  return (
    <Card className="flex min-w-0 flex-col gap-4">
      <h2 className="text-lg font-semibold">Основне</h2>
      {canEdit ? (
        <LocationForm key={location.id} location={location} />
      ) : (
        <dl className="m-0 flex flex-col gap-1.5 text-sm">
          <ReadOnlyRow label="Назва">{location.name}</ReadOnlyRow>
          <ReadOnlyRow label="Адреса">{location.address || "—"}</ReadOnlyRow>
          <ReadOnlyRow label="Телефон">{location.phone || "—"}</ReadOnlyRow>
          <ReadOnlyRow label="Часова зона">{location.timezone ?? "—"}</ReadOnlyRow>
          <ReadOnlyRow label="Статус">{location.isActive !== false ? "Активний" : "Неактивний"}</ReadOnlyRow>
        </dl>
      )}
      <div>
        <MapLink address={location.address} />
      </div>
    </Card>
  );
}

function HoursReadOnly({ hours }: { hours: WorkingHours | null | undefined }) {
  if (hours == null) return <p className="text-sm text-(--muted)">Години не задано: обмежень за часом немає, діють графіки майстрів.</p>;
  return (
    <ul className="m-0 flex list-none flex-col p-0" aria-label="Години роботи закладу">
      {WEEKDAYS.map((d) => {
        const list = hours[d.key] ?? [];
        return (
          <li key={d.key} className="flex flex-wrap gap-3 border-t border-(--line) py-2.5 text-sm first:border-t-0">
            <span className="w-28 font-semibold">{d.label}</span>
            <span className={list.length ? "" : "text-(--muted)"}>
              {list.length ? list.map((i) => `${i.from}–${i.to}`).join(", ") : "Вихідний"}
            </span>
          </li>
        );
      })}
    </ul>
  );
}

function HoursEditorSection({ location }: { location: BeautyLocation }) {
  const save = useSetLocationWorkingHours(location.id);
  const saved = location.workingHours ?? null;
  const [enabled, setEnabled] = useState(saved !== null);
  const [hours, setHours] = useState<WorkingHours>(() => (saved ? structuredClone(saved) : defaultHours()));
  const [invalid, setInvalid] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflicts, setConflicts] = useState<OutsideHoursConflict[] | null>(null);

  const draft: WorkingHours | null = enabled ? hours : null;
  const dirty = !sameHours(draft, saved);

  async function apply(confirm: boolean) {
    setError(null);
    setNotice(null);
    if (draft && Object.keys(validateHours(draft)).length > 0) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    try {
      await save.mutateAsync({ workingHours: draft ? normalizeHours(draft) : null, confirm });
      setConflicts(null);
      setNotice("Години роботи збережено.");
    } catch (e) {
      if (isOutsideHoursConfirm(e)) setConflicts(e.conflicts ?? []);
      else {
        setConflicts(null);
        setError(humanizeError(e, "Не вдалося зберегти години роботи. Спробуйте ще раз."));
      }
    }
  }

  function reset() {
    setEnabled(saved !== null);
    setHours(saved ? structuredClone(saved) : defaultHours());
    setConflicts(null);
    setInvalid(false);
    setError(null);
  }

  const touch = () => {
    setNotice(null);
    setError(null);
  };

  return (
    <form
      className="flex min-w-0 flex-col gap-3"
      aria-label="Години роботи закладу"
      onSubmit={(e) => {
        e.preventDefault();
        void apply(false);
      }}
    >
      <CheckRow
        label="Задати години роботи закладу"
        hint="Без годин обмежень немає: діють лише графіки майстрів і вихідні."
        checked={enabled}
        onChange={(on) => {
          touch();
          setEnabled(on);
          setConflicts(null);
        }}
      />
      {enabled ? (
        <WorkingHoursEditor
          value={hours}
          hint="Час запису — це графік майстра, обмежений годинами закладу. День без інтервалів — заклад у цей день не працює."
          onChange={(h) => {
            touch();
            setHours(h);
            setConflicts(null);
          }}
        />
      ) : null}
      {invalid ? (
        <p role="alert" className={ERROR_TEXT}>
          Виправте помилки в годинах роботи.
        </p>
      ) : null}
      {conflicts ? (
        <ClosedDaysConflictDialog
          conflicts={conflicts}
          timezone={location.timezone}
          busy={save.isPending}
          description="Записи не скасовуються автоматично: після збереження їх потрібно перенести або скасувати вручну. Нові слоти поза годинами закладу зникнуть одразу."
          confirmLabel="Підтвердити й зберегти години"
          listLabel="Записи поза новими годинами"
          onConfirm={() => void apply(true)}
          onCancel={reset}
        />
      ) : null}
      {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      {notice ? <SuccessBanner>{notice}</SuccessBanner> : null}
      <div className="flex flex-wrap gap-2">
        <PrimaryButton type="submit" disabled={!dirty || save.isPending}>
          {save.isPending && !conflicts ? "Зберігаємо…" : "Зберегти години"}
        </PrimaryButton>
        {dirty ? <SecondaryButton onClick={reset}>Скасувати зміни</SecondaryButton> : null}
      </div>
    </form>
  );
}

function HoursSection({ location, canEdit }: { location: BeautyLocation; canEdit: boolean }) {
  return (
    <Card className="flex min-w-0 flex-col gap-4">
      <h2 className="text-lg font-semibold">Години роботи</h2>
      {canEdit ? <HoursEditorSection key={location.id} location={location} /> : <HoursReadOnly hours={location.workingHours} />}
    </Card>
  );
}

function ClosedSection({ location, canEdit, showReason }: { location: BeautyLocation; canEdit: boolean; showReason: boolean }) {
  return (
    <Card className="flex min-w-0 flex-col gap-4">
      <h2 className="text-lg font-semibold">Вихідні й закриття</h2>
      <p className="text-sm text-(--muted)">{closedWeekdaysLabel(location.closedWeekdays)}</p>
      <ClosedDaysSection location={location} canEdit={canEdit} showReason={showReason} className="" />
    </Card>
  );
}

function StaffSection({ location, role, selfId }: { location: BeautyLocation; role: string; selfId: string | null }) {
  const q = useStaff();
  const rows = staffOfLocation(q.data ?? [], location.id);
  const manager = role !== "specialist";
  return (
    <Card className="flex min-w-0 flex-col gap-4">
      <h2 className="text-lg font-semibold">Спеціалісти закладу</h2>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {rows.length === 0 ? (
          <EmptyState>До цього закладу ще не прив’язано спеціалістів.</EmptyState>
        ) : (
          <ul className="m-0 flex list-none flex-col gap-2 p-0" aria-label="Спеціалісти закладу">
            {rows.map(({ member: m, workingHours }) => {
              const href = `/beauty/staff/${encodeURIComponent(m.id)}?from=location&locationId=${encodeURIComponent(location.id)}`;
              const linkable = manager || m.id === selfId;
              return (
                <li key={m.id} className="flex min-w-0 flex-col gap-1.5 rounded-xl bg-(--page) px-4 py-3 text-sm">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="min-w-0">
                      <div className="font-semibold break-words">{m.name}</div>
                      <div className="text-(--muted)">{m.position ?? "Посада не вказана"}</div>
                    </div>
                    <div className="flex flex-wrap items-center gap-2">
                      {m.isActive ? <Badge tone="ok">Активний</Badge> : <Badge tone="neutral">Неактивний</Badge>}
                      {linkable ? (
                        <Link
                          href={href}
                          aria-label={`Профіль: ${m.name}`}
                          className="inline-flex min-h-11 items-center text-sm font-semibold text-(--accent) underline"
                        >
                          Профіль
                        </Link>
                      ) : null}
                    </div>
                  </div>
                  <div className="break-words">
                    <span className="text-(--muted)">Графік у закладі: </span>
                    {summarizeHours(workingHours)}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </QueryState>
    </Card>
  );
}

export function LocationDetailScreen({ id }: { id: string }) {
  const { user } = useAuth();
  const isManager = user.role !== "specialist";
  // Керівники бачать і неактивні заклади; specialist - довідник активних (§16).
  const managed = useManagedLocations(isManager);
  const active = useLocations(!isManager);
  const q = isManager ? managed : active;
  const location = q.data?.find((l) => l.id === id);

  return (
    <>
      {isManager ? <TextLink href="/beauty/locations">← Усі заклади</TextLink> : null}
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {!location ? (
          <Card>
            <p className="text-sm">Заклад не знайдено.</p>
          </Card>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <h1 className="beauty-display min-w-0 text-[26px] font-semibold break-words">{location.name}</h1>
              {location.isActive !== false ? <Badge tone="ok">Активний</Badge> : <Badge tone="neutral">Неактивний</Badge>}
            </div>
            {!isManager ? <p className="text-sm text-(--muted)">Перегляд: змінювати налаштування закладу можуть власник і адміністратор.</p> : null}
            <BasicSection location={location} canEdit={isManager} />
            <HoursSection location={location} canEdit={isManager} />
            <ClosedSection location={location} canEdit={isManager} showReason={canSeeClosureReason(user.role)} />
            <StaffSection location={location} role={user.role} selfId={user.specialistId ?? null} />
          </>
        )}
      </QueryState>
    </>
  );
}
