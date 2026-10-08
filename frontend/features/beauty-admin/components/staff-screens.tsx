"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState, type FormEvent } from "react";
import { z } from "zod";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import {
  useAbsences,
  useCreateStaff,
  useInviteStaff,
  useLocations,
  useServices,
  useSetStaffSchedule,
  useSetStaffServices,
  useStaff,
  useStaffProfile,
  useUpdateStaff,
} from "../hooks/use-beauty-admin";
import { addDaysIso, initials, todayIso } from "../format";
import type { StaffLocation, StaffMember, WorkingHours } from "../types";
import { WEEKDAYS, defaultHours, hasWorkingDays, normalizeHours, validateHours } from "../working-hours";
import { AbsencesTab } from "./staff-absences";
import {
  ABSENCE_TYPE_LABEL,
  CheckRow,
  ErrorBanner,
  InviteBox,
  InvitePanel,
  ServicesChecklist,
  SuccessBanner,
  TextField,
  WorkingHoursEditor,
  ERROR_TEXT,
} from "./staff-parts";
import {
  Avatar,
  Badge,
  Card,
  EmptyState,
  KeyValue,
  LinkButton,
  PageHeader,
  PrimaryButton,
  QueryState,
  RowLink,
  SecondaryButton,
  Tabs,
  TextLink,
} from "./ui";

const PHONE = /^\+?[\d\s()-]{7,20}$/;
const emailSchema = z.string().trim().min(1, "Вкажіть пошту").email("Некоректна адреса пошти");

// ---------- Створення працівника ----------

type FormErrors = Partial<Record<"name" | "phone" | "position" | "locations" | "email", string>>;

function CreateStaffPanel({ onClose }: { onClose: () => void }) {
  const create = useCreateStaff();
  const invite = useInviteStaff();
  const locations = useLocations();
  const services = useServices();

  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [position, setPosition] = useState("");
  const [locationIds, setLocationIds] = useState<string[]>([]);
  const [serviceIds, setServiceIds] = useState<string[]>([]);
  const [hours, setHours] = useState<WorkingHours>(defaultHours);
  const [wantInvite, setWantInvite] = useState(false);
  const [email, setEmail] = useState("");
  const [errors, setErrors] = useState<FormErrors>({});
  const [hoursInvalid, setHoursInvalid] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const [created, setCreated] = useState<{ member: StaffMember; token: string | null; inviteFailed: boolean } | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    const next: FormErrors = {};
    if (name.trim().length < 2 || name.trim().length > 100) next.name = "Імʼя від 2 до 100 символів";
    if (phone.trim() && !PHONE.test(phone.trim())) next.phone = "Некоректний номер телефону";
    if (position.trim().length > 100) next.position = "Не більше 100 символів";
    if (locationIds.length === 0) next.locations = "Оберіть хоча б один заклад";
    if (wantInvite) {
      const p = emailSchema.safeParse(email);
      if (!p.success) next.email = p.error.issues[0]?.message;
    }
    const badHours = Object.keys(validateHours(hours)).length > 0;
    setErrors(next);
    setHoursInvalid(badHours);
    setServerError(null);
    if (Object.keys(next).length > 0 || badHours) return;

    try {
      const member = await create.mutateAsync({
        name,
        phone,
        position,
        locationIds,
        serviceIds,
        workingHours: normalizeHours(hours),
      });
      let token: string | null = null;
      let inviteFailed = false;
      if (wantInvite) {
        try {
          token = (await invite.mutateAsync({ id: member.id, email })).token;
        } catch {
          inviteFailed = true;
        }
      }
      setCreated({ member, token, inviteFailed });
    } catch (err) {
      setServerError(humanizeError(err, "Не вдалося створити працівника. Спробуйте ще раз."));
    }
  }

  if (created) {
    return (
      <Card as="div" className="flex flex-col gap-4">
        <SuccessBanner>{`Профіль «${created.member.name}» створено.`}</SuccessBanner>
        {created.token ? <InviteBox token={created.token} onClose={() => setCreated({ ...created, token: null })} /> : null}
        {created.inviteFailed ? (
          <ErrorBanner>Профіль створено, але запрошення не вдалося створити. Спробуйте ще раз у профілі працівника.</ErrorBanner>
        ) : null}
        <div className="flex flex-wrap gap-2">
          <LinkButton href={`/beauty/staff/${created.member.id}`}>Відкрити профіль</LinkButton>
          <SecondaryButton onClick={onClose}>Готово</SecondaryButton>
        </div>
      </Card>
    );
  }

  return (
    <Card as="div">
      <form onSubmit={submit} noValidate className="flex flex-col gap-6" aria-label="Новий працівник">
        <h2 className="text-lg font-semibold">Новий працівник</h2>
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField label="Імʼя та прізвище" required autoComplete="off" value={name} onChange={setName} error={errors.name} />
          <TextField label="Телефон" type="tel" autoComplete="off" value={phone} onChange={setPhone} error={errors.phone} hint="Необов’язково" />
          <TextField label="Посада" value={position} onChange={setPosition} error={errors.position} hint="Наприклад: майстер манікюру" />
        </div>

        <fieldset className="m-0 flex flex-col gap-0.5 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">
            Заклади<span aria-hidden="true"> *</span>
          </legend>
          <QueryState isPending={locations.isPending} isError={locations.isError} onRetry={() => locations.refetch()}>
            {(locations.data ?? []).map((l) => (
              <CheckRow
                key={l.id}
                label={l.name}
                checked={locationIds.includes(l.id)}
                onChange={(on) => setLocationIds((cur) => (on ? [...cur, l.id] : cur.filter((x) => x !== l.id)))}
              />
            ))}
          </QueryState>
          {errors.locations ? <p className={ERROR_TEXT}>{errors.locations}</p> : null}
        </fieldset>

        <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Послуги, які виконує працівник</legend>
          <p className="mb-2 text-[13px] text-(--muted)">Без призначених послуг працівник не з’явиться в онлайн-записі.</p>
          <QueryState isPending={services.isPending} isError={services.isError} onRetry={() => services.refetch()}>
            <ServicesChecklist services={services.data ?? []} selected={serviceIds} onChange={setServiceIds} />
          </QueryState>
        </fieldset>

        <fieldset className="m-0 flex flex-col gap-2 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Графік роботи (однаковий для всіх обраних закладів)</legend>
          <WorkingHoursEditor value={hours} onChange={setHours} />
          {hoursInvalid ? <p role="alert" className={ERROR_TEXT}>Виправте помилки в графіку.</p> : null}
        </fieldset>

        <fieldset className="m-0 flex flex-col gap-3 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Доступ до системи</legend>
          <CheckRow
            label="Запросити на вхід"
            hint="Створимо одноразове посилання, яке ви передасте працівнику. Пошта з системи не надсилається."
            checked={wantInvite}
            onChange={setWantInvite}
          />
          {wantInvite ? (
            <TextField label="Пошта працівника" type="email" autoComplete="off" required value={email} onChange={setEmail} error={errors.email} />
          ) : null}
        </fieldset>

        {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
        <div className="flex flex-wrap gap-2">
          <PrimaryButton type="submit" disabled={create.isPending || invite.isPending}>
            {create.isPending || invite.isPending ? "Створюємо…" : "Створити працівника"}
          </PrimaryButton>
          <SecondaryButton onClick={onClose}>Скасувати</SecondaryButton>
        </div>
      </form>
    </Card>
  );
}

// ---------- Список ----------

function StaffCard({ m, absentType, pending }: { m: StaffMember; absentType: string | null; pending: number }) {
  const shown = m.services.slice(0, 4).map((s) => s.name);
  const more = m.services.length - shown.length;
  return (
    <li className="flex min-w-0 flex-col gap-3 rounded-2xl bg-white p-5">
      <div className="flex items-center gap-3">
        <Avatar text={initials(m.name)} size={48} />
        <div className="flex min-w-0 flex-col">
          <RowLink href={`/beauty/staff/${m.id}`}>
            <span className="text-base font-semibold break-words">{m.name}</span>
          </RowLink>
          <span className="text-[13px] text-(--muted)">{m.position ?? "Посада не вказана"}</span>
        </div>
      </div>
      <div className="flex flex-wrap gap-1.5">
        {!m.isActive ? (
          <Badge tone="neutral">Неактивний</Badge>
        ) : absentType ? (
          <Badge tone="wait">Сьогодні відсутній ({absentType})</Badge>
        ) : (
          <Badge tone="ok">Активний</Badge>
        )}
        {pending > 0 ? <Badge tone="now">Запити на відсутність: {pending}</Badge> : null}
      </div>
      <dl className="m-0 flex flex-col gap-1.5 text-sm">
        <div className="flex gap-2">
          <dt className="w-20 flex-none text-(--muted)">Заклади</dt>
          <dd className="m-0 min-w-0">{m.locations.length ? m.locations.map((l) => l.locationName).join(", ") : "—"}</dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-20 flex-none text-(--muted)">Послуги</dt>
          <dd className="m-0 min-w-0">{shown.length ? `${shown.join(", ")}${more > 0 ? ` і ще ${more}` : ""}` : "Не призначено"}</dd>
        </div>
      </dl>
    </li>
  );
}

export function StaffListScreen() {
  const { user } = useAuth();
  const router = useRouter();
  const isManager = user.role !== "specialist";
  const staff = useStaff(isManager);
  const today = todayIso();
  const absences = useAbsences({ from: today, to: addDaysIso(today, 90) }, isManager);
  const [adding, setAdding] = useState(false);

  useEffect(() => {
    if (!isManager) router.replace(user.specialistId ? `/beauty/staff/${user.specialistId}` : "/beauty/calendar");
  }, [isManager, router, user.specialistId]);

  if (!isManager) {
    return (
      <div role="status" className="text-sm text-(--muted)">
        Відкриваємо ваш профіль…
      </div>
    );
  }

  const absentToday = (id: string) =>
    absences.data?.find((a) => a.specialistId === id && a.status === "approved" && a.dateFrom <= today && a.dateTo >= today);
  const pendingFor = (id: string) => absences.data?.filter((a) => a.specialistId === id && a.status === "requested").length ?? 0;
  const sorted = [...(staff.data ?? [])].sort((a, b) => Number(b.isActive) - Number(a.isActive) || a.name.localeCompare(b.name, "uk"));

  return (
    <>
      <PageHeader
        title="Спеціалісти"
        subtitle="Команда мережі"
        actions={
          adding ? null : (
            <PrimaryButton aria-expanded={false} onClick={() => setAdding(true)}>
              Додати працівника
            </PrimaryButton>
          )
        }
      />
      {adding ? <CreateStaffPanel onClose={() => setAdding(false)} /> : null}
      <QueryState isPending={staff.isPending} isError={staff.isError} onRetry={() => staff.refetch()}>
        {sorted.length === 0 ? (
          <Card>
            <EmptyState>Працівників ще немає. Додайте першого.</EmptyState>
          </Card>
        ) : (
          <ul className="m-0 grid list-none gap-4 p-0" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(min(100%, 320px), 1fr))" }} aria-label="Працівники">
            {sorted.map((m) => {
              const a = absentToday(m.id);
              return <StaffCard key={m.id} m={m} absentType={a ? ABSENCE_TYPE_LABEL[a.type] : null} pending={pendingFor(m.id)} />;
            })}
          </ul>
        )}
      </QueryState>
    </>
  );
}

// ---------- Профіль ----------

type Tab = "profile" | "svc" | "sch" | "abs";

function ProfileTab({ m, canEdit }: { m: StaffMember; canEdit: boolean }) {
  const update = useUpdateStaff(m.id);
  const [name, setName] = useState(m.name);
  const [phone, setPhone] = useState(m.phone ?? "");
  const [position, setPosition] = useState(m.position ?? "");
  const [errors, setErrors] = useState<FormErrors>({});
  const [saved, setSaved] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);

  function validate(): boolean {
    const next: FormErrors = {};
    if (name.trim().length < 2 || name.trim().length > 100) next.name = "Імʼя від 2 до 100 символів";
    if (phone.trim() && !PHONE.test(phone.trim())) next.phone = "Некоректний номер телефону";
    if (position.trim().length > 100) next.position = "Не більше 100 символів";
    setErrors(next);
    return Object.keys(next).length === 0;
  }

  function save(e: FormEvent) {
    e.preventDefault();
    setSaved(null);
    if (!validate()) return;
    update.mutate({ name, phone, position, isActive: m.isActive }, { onSuccess: () => setSaved("Зміни збережено.") });
  }

  function setActive(isActive: boolean) {
    setSaved(null);
    update.mutate(
      { name: m.name, phone: m.phone ?? "", position: m.position ?? "", isActive },
      {
        onSuccess: () => {
          setConfirming(false);
          setSaved(isActive ? "Працівника відновлено." : "Працівника деактивовано. Наявні записи збережено.");
        },
      },
    );
  }

  if (!canEdit) {
    return (
      <div className="flex flex-col gap-2.5">
        <h2 className="text-lg font-semibold">Профіль</h2>
        <KeyValue label="Імʼя" value={m.name} />
        <KeyValue label="Посада" value={m.position ?? "—"} />
        {m.phone ? <KeyValue label="Телефон" value={m.phone} /> : null}
        <KeyValue label="Заклади" value={m.locations.map((l) => l.locationName).join(", ") || "—"} />
        <p className="text-[13px] text-(--muted)">Редагувати профіль може лише керівник.</p>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <form onSubmit={save} noValidate className="flex flex-col gap-4" aria-label="Редагування профілю">
        <h2 className="text-lg font-semibold">Профіль</h2>
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField label="Імʼя та прізвище" required autoComplete="off" value={name} onChange={setName} error={errors.name} />
          <TextField label="Телефон" type="tel" autoComplete="off" value={phone} onChange={setPhone} error={errors.phone} />
          <TextField label="Посада" value={position} onChange={setPosition} error={errors.position} />
        </div>
        {update.isError ? <ErrorBanner>{humanizeError(update.error, "Не вдалося зберегти. Спробуйте ще раз.")}</ErrorBanner> : null}
        {saved ? <SuccessBanner>{saved}</SuccessBanner> : null}
        <div>
          <PrimaryButton type="submit" disabled={update.isPending}>
            {update.isPending ? "Зберігаємо…" : "Зберегти"}
          </PrimaryButton>
        </div>
      </form>

      <section className="flex flex-col gap-3 border-t border-(--line) pt-5" aria-labelledby="status-h">
        <h2 id="status-h" className="text-lg font-semibold">
          Статус
        </h2>
        {m.isActive ? (
          confirming ? (
            <div className="flex flex-col gap-3 rounded-xl bg-[#FFF1CC] p-4 text-[#5C3900]">
              <p className="text-sm">
                Працівник зникне з онлайн-запису й календаря для нових записів. Наявні записи не видаляються — перенесіть їх за потреби.
              </p>
              <p className="text-sm font-semibold">Деактивація забере вхід у систему для прив’язаного користувача.</p>
              <div className="flex flex-wrap gap-2">
                <PrimaryButton disabled={update.isPending} onClick={() => setActive(false)}>
                  Так, деактивувати
                </PrimaryButton>
                <SecondaryButton onClick={() => setConfirming(false)}>Залишити активним</SecondaryButton>
              </div>
            </div>
          ) : (
            <div>
              <SecondaryButton onClick={() => setConfirming(true)}>Деактивувати працівника</SecondaryButton>
            </div>
          )
        ) : (
          <div className="flex flex-col gap-2">
            <p className="text-sm text-(--muted)">Працівник неактивний і не приймає нові записи.</p>
            <div>
              <PrimaryButton disabled={update.isPending} onClick={() => setActive(true)}>
                Відновити працівника
              </PrimaryButton>
            </div>
          </div>
        )}
      </section>

      {m.isActive ? (
        <section className="border-t border-(--line) pt-5">
          <InvitePanel staffId={m.id} />
        </section>
      ) : null}
    </div>
  );
}

function ServicesTab({ m, canEdit }: { m: StaffMember; canEdit: boolean }) {
  const catalog = useServices();
  const save = useSetStaffServices(m.id);
  const [selected, setSelected] = useState<string[]>(() => m.services.map((s) => s.id));
  const [saved, setSaved] = useState(false);

  if (!canEdit) {
    return (
      <div className="flex flex-col gap-2.5">
        <h2 className="text-lg font-semibold">Послуги</h2>
        {m.services.length === 0 ? (
          <EmptyState>Послуги не призначено.</EmptyState>
        ) : (
          <ul className="m-0 flex list-none flex-col gap-1 p-0 text-sm">
            {m.services.map((s) => (
              <li key={s.id}>{s.name}</li>
            ))}
          </ul>
        )}
        <p className="text-[13px] text-(--muted)">Список послуг змінює керівник.</p>
      </div>
    );
  }

  return (
    <form
      className="flex flex-col gap-4"
      aria-label="Послуги працівника"
      onSubmit={(e) => {
        e.preventDefault();
        setSaved(false);
        save.mutate(selected, { onSuccess: () => setSaved(true) });
      }}
    >
      <h2 className="text-lg font-semibold">Послуги, які може виконувати</h2>
      <p className="text-sm text-(--muted)">Працівник пропонується в записі лише для позначених послуг.</p>
      <QueryState isPending={catalog.isPending} isError={catalog.isError} onRetry={() => catalog.refetch()}>
        <ServicesChecklist
          services={catalog.data ?? []}
          selected={selected}
          onChange={(ids) => {
            setSelected(ids);
            setSaved(false);
          }}
        />
      </QueryState>
      {save.isError ? <ErrorBanner>{humanizeError(save.error, "Не вдалося зберегти послуги. Спробуйте ще раз.")}</ErrorBanner> : null}
      {saved ? <SuccessBanner>Послуги збережено.</SuccessBanner> : null}
      <div>
        <PrimaryButton type="submit" disabled={save.isPending || catalog.isPending}>
          {save.isPending ? "Зберігаємо…" : "Зберегти послуги"}
        </PrimaryButton>
      </div>
    </form>
  );
}

function LocationSchedule({ staffId, loc }: { staffId: string; loc: StaffLocation }) {
  const save = useSetStaffSchedule(staffId);
  const [hours, setHours] = useState<WorkingHours>(() => structuredClone(loc.workingHours));
  const [invalid, setInvalid] = useState(false);
  const [saved, setSaved] = useState(false);

  return (
    <form
      className="flex flex-col gap-3"
      aria-label={`Графік: ${loc.locationName}`}
      onSubmit={(e) => {
        e.preventDefault();
        setSaved(false);
        const bad = Object.keys(validateHours(hours)).length > 0;
        setInvalid(bad);
        if (bad) return;
        save.mutate({ locationId: loc.locationId, workingHours: normalizeHours(hours) }, { onSuccess: () => setSaved(true) });
      }}
    >
      <h3 className="text-base font-semibold">{loc.locationName}</h3>
      <WorkingHoursEditor
        value={hours}
        onChange={(h) => {
          setHours(h);
          setSaved(false);
        }}
      />
      {!hasWorkingDays(hours) ? (
        <p className="text-[13px] text-(--muted)">Немає жодного робочого дня — у цьому закладі майстер не буде доступний для запису.</p>
      ) : null}
      {invalid ? <p role="alert" className={ERROR_TEXT}>Виправте помилки в графіку.</p> : null}
      {save.isError ? <ErrorBanner>{humanizeError(save.error, "Не вдалося зберегти графік. Спробуйте ще раз.")}</ErrorBanner> : null}
      {saved ? <SuccessBanner>Графік збережено.</SuccessBanner> : null}
      <div>
        <PrimaryButton type="submit" disabled={save.isPending}>
          {save.isPending ? "Зберігаємо…" : "Зберегти графік"}
        </PrimaryButton>
      </div>
    </form>
  );
}

function ScheduleTab({ m, canEdit }: { m: StaffMember; canEdit: boolean }) {
  return (
    <div className="flex flex-col gap-6">
      <h2 className="text-lg font-semibold">Графік роботи</h2>
      {m.locations.length === 0 ? <EmptyState>Працівника не прив’язано до жодного закладу.</EmptyState> : null}
      {m.locations.map((loc) =>
        canEdit ? (
          <LocationSchedule key={loc.locationId} staffId={m.id} loc={loc} />
        ) : (
          <section key={loc.locationId} className="flex flex-col gap-1">
            <h3 className="text-base font-semibold">{loc.locationName}</h3>
            <ul className="m-0 flex list-none flex-col p-0">
              {WEEKDAYS.map((d) => {
                const list = loc.workingHours[d.key] ?? [];
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
          </section>
        ),
      )}
    </div>
  );
}

export function StaffProfileScreen({ id }: { id: string }) {
  const { user } = useAuth();
  const router = useRouter();
  const isManager = user.role !== "specialist";
  const isSelf = !isManager && user.specialistId === id;
  const [tab, setTab] = useState<Tab>("profile");
  const [absFormOpen, setAbsFormOpen] = useState(false);
  const q = useStaffProfile(id);
  const m = q.data;

  // Specialist бачить лише власний профіль.
  const blocked = !isManager && !isSelf;
  useEffect(() => {
    if (blocked) router.replace(user.specialistId ? `/beauty/staff/${user.specialistId}` : "/beauty/calendar");
  }, [blocked, router, user.specialistId]);

  if (blocked) {
    return (
      <div role="status" className="text-sm text-(--muted)">
        Відкриваємо ваш профіль…
      </div>
    );
  }

  return (
    <>
      {isManager ? <TextLink href="/beauty/staff">← Усі спеціалісти</TextLink> : null}
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {m === null ? (
          <Card>
            <p className="text-sm">Спеціаліста не знайдено.</p>
          </Card>
        ) : m ? (
          <>
            <Card className="flex flex-wrap items-center gap-5 p-6">
              <span
                aria-hidden="true"
                className="beauty-display flex size-[72px] flex-none items-center justify-center rounded-full bg-[#EADCF2] text-2xl font-semibold text-[#4A2260]"
              >
                {initials(m.name)}
              </span>
              <div className="flex min-w-0 flex-[1_1_260px] flex-col gap-1.5">
                <h1 className="beauty-display text-[26px] font-semibold break-words">{m.name}</h1>
                <div className="text-sm text-(--muted)">{m.position ?? "Посада не вказана"}</div>
                <div className="flex flex-wrap gap-1.5">
                  {m.isActive ? <Badge tone="ok">Активний</Badge> : <Badge tone="neutral">Неактивний</Badge>}
                  {m.locations.map((l) => (
                    <Badge key={l.locationId} tone="vip" className="!text-xs">
                      {l.locationName}
                    </Badge>
                  ))}
                </div>
              </div>
              <div className="flex flex-wrap gap-2">
                <LinkButton href={isManager ? `/beauty/calendar?specialist=${encodeURIComponent(m.id)}` : "/beauty/calendar"}>
                  Відкрити календар
                </LinkButton>
                {isSelf ? (
                  <SecondaryButton
                    onClick={() => {
                      setTab("abs");
                      setAbsFormOpen(true);
                    }}
                  >
                    Повідомити про відсутність
                  </SecondaryButton>
                ) : null}
              </div>
            </Card>

            <Card className="flex flex-col gap-5">
              <Tabs
                value={tab}
                onChange={setTab}
                tabs={[
                  { id: "profile", label: "Профіль" },
                  { id: "svc", label: "Послуги" },
                  { id: "sch", label: "Графік" },
                  { id: "abs", label: "Відсутність" },
                ]}
              />
              {tab === "profile" ? <ProfileTab m={m} canEdit={isManager} /> : null}
              {tab === "svc" ? <ServicesTab m={m} canEdit={isManager} /> : null}
              {tab === "sch" ? <ScheduleTab m={m} canEdit={isManager} /> : null}
              {tab === "abs" ? (
                <AbsencesTab member={m} isManager={isManager} isSelf={isSelf} formOpen={absFormOpen} onFormOpenChange={setAbsFormOpen} />
              ) : null}
            </Card>
          </>
        ) : null}
      </QueryState>
    </>
  );
}
