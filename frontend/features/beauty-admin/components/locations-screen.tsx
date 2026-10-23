"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { useManagedLocations, useStaff } from "../hooks/use-beauty-admin";
import { closedWeekdaysLabel, countActiveStaff, locationHoursLabel } from "../location-hours";
import type { BeautyLocation, StaffMember } from "../types";
import { LocationForm } from "./location-form";
import { Badge, Card, EmptyState, LinkButton, PageHeader, PrimaryButton, QueryState } from "./ui";

/** Картка закладу у списку: лише перегляд (§18.3), налаштування - на сторінці закладу. */
function LocationCard({ l, staff }: { l: BeautyLocation; staff: StaffMember[] | undefined }) {
  const active = l.isActive !== false;
  const hasHours = l.workingHours != null;
  return (
    <li className="flex min-w-0 flex-col gap-3 rounded-2xl bg-white p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="min-w-0 text-base font-semibold break-words">{l.name}</h2>
        {active ? <Badge tone="ok">Активний</Badge> : <Badge tone="neutral">Неактивний</Badge>}
      </div>
      <dl className="m-0 flex flex-col gap-1.5 text-sm">
        <div className="flex gap-2">
          <dt className="w-28 flex-none text-(--muted)">Адреса</dt>
          <dd className="m-0 min-w-0 break-words">{l.address || "—"}</dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-28 flex-none text-(--muted)">Телефон</dt>
          <dd className="m-0 min-w-0 break-words">{l.phone || "—"}</dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-28 flex-none text-(--muted)">Спеціалістів</dt>
          <dd className="m-0 min-w-0">{staff ? countActiveStaff(staff, l.id) : "—"}</dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-28 flex-none text-(--muted)">Години</dt>
          <dd className={`m-0 min-w-0 break-words ${hasHours ? "" : "text-(--muted)"}`}>
            {hasHours ? <>Працює: {locationHoursLabel(l)}</> : locationHoursLabel(l)}
          </dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-28 flex-none text-(--muted)">Вихідні</dt>
          <dd className="m-0 min-w-0">{closedWeekdaysLabel(l.closedWeekdays)}</dd>
        </div>
      </dl>
      <div>
        <LinkButton href={`/beauty/locations/${encodeURIComponent(l.id)}`} ariaLabel={`Відкрити заклад «${l.name}»`}>
          Відкрити
        </LinkButton>
      </div>
    </li>
  );
}

export function LocationsScreen() {
  const { user } = useAuth();
  const router = useRouter();
  const isManager = user.role !== "specialist";
  const q = useManagedLocations(isManager);
  const staff = useStaff(isManager);
  const [adding, setAdding] = useState(false);

  if (!isManager) {
    return (
      <>
        <PageHeader title="Заклади" />
        <Card>
          <p className="text-sm">Керувати закладами можуть лише власник і адміністратор.</p>
        </Card>
      </>
    );
  }

  const sorted = [...(q.data ?? [])].sort(
    (a, b) => Number(b.isActive !== false) - Number(a.isActive !== false) || a.name.localeCompare(b.name, "uk"),
  );

  return (
    <>
      <PageHeader
        title="Заклади"
        subtitle="Філії мережі: адреса, години роботи й вихідні. Налаштування — на сторінці закладу"
        actions={adding ? null : <PrimaryButton onClick={() => setAdding(true)}>Додати заклад</PrimaryButton>}
      />
      {adding ? (
        <LocationForm
          location={null}
          onClose={() => setAdding(false)}
          onCreated={(created) => router.push(`/beauty/locations/${encodeURIComponent(created.id)}`)}
        />
      ) : null}
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {sorted.length === 0 ? (
          <Card>
            <EmptyState>Закладів ще немає. Додайте перший.</EmptyState>
          </Card>
        ) : (
          <ul className="m-0 grid list-none gap-4 p-0" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(min(100%, 320px), 1fr))" }} aria-label="Заклади">
            {sorted.map((l) => (
              <LocationCard key={l.id} l={l} staff={staff.data} />
            ))}
          </ul>
        )}
      </QueryState>
    </>
  );
}
