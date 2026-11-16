"use client";

import { useId, useMemo, useState } from "react";
import { useAccess } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import type { Role } from "@/features/beauty-auth/types";
import { useAccessCatalog } from "../hooks/use-access-catalog";
import { useStaff } from "../hooks/use-beauty-admin";
import { useCreateTeamInvite } from "../hooks/use-team";
import { accessErrorMessages, findViolations, inviteAssignable, roleTitle, type OptionState } from "../team/logic";
import type { InviteRole, PermissionValues, TeamInviteCreated } from "../team/types";
import { AccessEditor } from "./access-editor";
import { ClampNote } from "./access-dialogs";
import { InviteBox } from "./staff-parts";
import { Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, QueryState, SecondaryButton } from "./ui";

const RANK: Record<string, number> = { owner: 4, admin: 3, deputy: 2, specialist: 1 };
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Запрошення з вибором ролі й прав (§30.9.5): пресет ролі + перевизначення; сервер обрізає права стелею запрошувача. */
export function TeamInvitePanel({ onClose }: { onClose: () => void }) {
  const me = useAccess();
  const catalog = useAccessCatalog();
  const staff = useStaff();
  const create = useCreateTeamInvite();
  const ids = useId();
  const roles = (["admin", "deputy", "specialist"] as InviteRole[]).filter((r) => RANK[r]! < RANK[me.role]!);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<InviteRole>(roles[0] ?? "specialist");
  const [specialistId, setSpecialistId] = useState("");
  const [custom, setCustom] = useState(false);
  const [draft, setDraft] = useState<PermissionValues>({});
  const [errors, setErrors] = useState<string[]>([]);
  const [created, setCreated] = useState<TeamInviteCreated | null>(null);

  const presetOf = (r: InviteRole) => catalog.data?.presets[r] ?? {};
  const assignable = useMemo(() => inviteAssignable((k) => me.level(k as never), role), [me, role]);
  const values = useMemo(() => ({ ...presetOf(role), ...draft }), [catalog.data, role, draft]); // eslint-disable-line react-hooks/exhaustive-deps
  const violations = useMemo(() => (catalog.data ? findViolations(catalog.data.constraints, values, role as Role) : []), [catalog.data, values, role]);
  const invalid = useMemo(() => new Map(violations.map((v) => [v.permission, v.text])), [violations]);

  const state = (key: string, level: string): OptionState => {
    if (values[key] === level) return { disabled: false };
    if (key === "team.manage" && role !== "admin") return { disabled: true, reason: "Лише для ролі «Адміністратор»" };
    return (assignable[key] ?? []).includes(level) ? { disabled: false } : { disabled: true, reason: "Вище за ваш власний рівень" };
  };
  const emailOk = EMAIL.test(email.trim());
  const needsProfile = role === "specialist";

  function submit() {
    setErrors([]);
    if (!emailOk) return setErrors(["Вкажіть коректну адресу пошти."]);
    // Надсилаємо лише відмінності від пресету: решту сервер візьме з пресету ролі.
    const overrides = Object.fromEntries(Object.entries(draft).filter(([k, v]) => v !== presetOf(role)[k]));
    create.mutate(
      { email, role, ...(needsProfile && specialistId ? { specialistId } : {}), ...(custom ? { permissions: overrides } : {}) },
      {
        onSuccess: (r) => setCreated(r),
        onError: (e) => setErrors(accessErrorMessages(e).length ? accessErrorMessages(e) : [humanizeError(e)]),
      },
    );
  }

  if (created) {
    return (
      <Card className="flex flex-col gap-3">
        <h2 className="m-0 text-lg font-semibold">Запрошення створено</h2>
        <p className="m-0 text-sm" role="status">
          Запрошення для {created.invite.email} ({roleTitle(created.invite.role)}) діє до {new Date(created.invite.expiresAt).toLocaleDateString("uk-UA")}.
        </p>
        <ClampNote clamped={created.clamped} />
        <InviteBox token={created.token} onClose={onClose} />
      </Card>
    );
  }

  return (
    <Card className="flex flex-col gap-4">
      <h2 className="m-0 text-lg font-semibold">Нове запрошення</h2>
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${ids}-email`} className={FIELD_LABEL}>
            Пошта
          </label>
          <input id={`${ids}-email`} type="email" autoComplete="off" className={FIELD_INPUT} value={email} onChange={(e) => setEmail(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${ids}-role`} className={FIELD_LABEL}>
            Роль
          </label>
          <select
            id={`${ids}-role`}
            className={FIELD_INPUT}
            value={role}
            onChange={(e) => {
              setRole(e.target.value as InviteRole);
              setDraft({});
            }}
          >
            {roles.map((r) => (
              <option key={r} value={r}>
                {roleTitle(r)}
              </option>
            ))}
          </select>
        </div>
        {needsProfile ? (
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${ids}-sp`} className={FIELD_LABEL}>
              Профіль майстра
            </label>
            <select id={`${ids}-sp`} className={FIELD_INPUT} value={specialistId} onChange={(e) => setSpecialistId(e.target.value)}>
              <option value="">Оберіть профіль</option>
              {(staff.data ?? []).filter((s) => s.isActive).map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </div>
        ) : null}
      </div>

      <label className="flex min-h-11 cursor-pointer items-center gap-2.5 text-sm font-semibold">
        <input type="checkbox" checked={custom} onChange={(e) => setCustom(e.target.checked)} className="size-4 accent-(--accent)" />
        Налаштувати права (інакше діє пресет ролі «{roleTitle(role)}»)
      </label>
      {custom ? (
        <QueryState isPending={catalog.isPending} isError={catalog.isError} onRetry={() => void catalog.refetch()}>
          {catalog.data ? (
            <AccessEditor
              name="invite"
              catalog={catalog.data}
              value={(k) => values[k] ?? ""}
              state={state}
              onChange={(k, l) => setDraft((d) => ({ ...d, [k]: l }))}
              changed={new Set(Object.keys(draft).filter((k) => draft[k] !== presetOf(role)[k]))}
              invalid={invalid}
            />
          ) : null}
        </QueryState>
      ) : null}

      <div aria-live="polite" className="flex flex-col gap-2">
        {violations.length > 0 && custom ? (
          <div role="alert" className="rounded-xl bg-[#FBE4E4] px-3 py-2 text-sm text-[#8A1F1F]">
            {violations.map((v) => (
              <p key={v.permission} className="m-0">
                {v.text}
              </p>
            ))}
          </div>
        ) : null}
        {errors.length > 0 ? (
          <div role="alert" data-testid="invite-errors" className="rounded-xl bg-[#FBE4E4] px-3 py-2 text-sm text-[#8A1F1F]">
            {errors.map((t, i) => (
              <p key={i} className="m-0">
                {t}
              </p>
            ))}
          </div>
        ) : null}
      </div>

      <div className="flex flex-wrap gap-2">
        <PrimaryButton onClick={submit} disabled={create.isPending || (custom && violations.length > 0) || (needsProfile && !specialistId)}>
          {create.isPending ? "Створюємо…" : "Створити запрошення"}
        </PrimaryButton>
        <SecondaryButton onClick={onClose}>Скасувати</SecondaryButton>
      </div>
    </Card>
  );
}
