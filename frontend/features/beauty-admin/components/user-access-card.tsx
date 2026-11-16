"use client";

import { useMemo, useState } from "react";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { Role } from "@/features/beauty-auth/types";
import { useAccessCatalog } from "../hooks/use-access-catalog";
import { useDryRunAccess, useDryRunReset, useResetAccess, useSaveAccess, useSetUserActive, useUserAccess } from "../hooks/use-team";
import {
  accessErrorMessages,
  currentValue,
  draftChanges,
  findViolations,
  optionState,
  roleTitle,
} from "../team/logic";
import type { AccessSaveResult, PermissionValues, TeamUser } from "../team/types";
import { AccessEditor } from "./access-editor";
import { ClampNote, ConfirmActionDialog, ConfirmChangesDialog } from "./access-dialogs";
import { Badge, Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, QueryState, SecondaryButton } from "./ui";
import { humanizeError } from "@/features/beauty-auth/errors";

type Dialog = { kind: "save" | "reset"; result: AccessSaveResult } | { kind: "toggle" } | null;

/** Картка користувача: роль, блоки прав, збереження через `dryRun` -> підтвердження зі списком змін -> запис. */
export function UserAccessCard({ user, onClose }: { user: TeamUser; onClose: () => void }) {
  const access = useUserAccess(user.id);
  const catalog = useAccessCatalog();
  return (
    <Card className="flex flex-col gap-4" >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="m-0 text-lg font-semibold" id={`ua-${user.id}`}>
            {user.fullName}
          </h2>
          <p className="m-0 text-sm text-(--muted)">{user.email}</p>
        </div>
        <SecondaryButton onClick={onClose}>Закрити картку</SecondaryButton>
      </div>
      <QueryState
        isPending={access.isPending || catalog.isPending}
        isError={access.isError || catalog.isError}
        onRetry={() => {
          void access.refetch();
          void catalog.refetch();
        }}
      >
        {access.data && catalog.data ? <Editor key={user.id} user={user} access={access.data} catalog={catalog.data} refetch={() => void access.refetch()} /> : null}
      </QueryState>
    </Card>
  );
}

function Editor({
  user,
  access: a,
  catalog,
  refetch,
}: {
  user: TeamUser;
  access: NonNullable<ReturnType<typeof useUserAccess>["data"]>;
  catalog: NonNullable<ReturnType<typeof useAccessCatalog>["data"]>;
  refetch: () => void;
}) {
  const [draft, setDraft] = useState<PermissionValues>({});
  const [role, setRole] = useState<Role>(a.role);
  const [dialog, setDialog] = useState<Dialog>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [dialogError, setDialogError] = useState<string | null>(null);
  const [notice, setNotice] = useState<AccessSaveResult | "ok" | null>(null);

  const dryRun = useDryRunAccess(user.id);
  const save = useSaveAccess(user.id);
  const dryReset = useDryRunReset(user.id);
  const reset = useResetAccess(user.id);
  const toggle = useSetUserActive(user.id);

  const roleChanged = role !== a.role;
  const changes = useMemo(() => draftChanges(a, draft), [a, draft]);
  const merged = useMemo(() => ({ ...(a.notEditableReason === "owner" ? a.effective : a.granted), ...draft }), [a, draft]);
  // Після зміни ролі сервер спершу застосує пресет, тому локально перевіряємо лише без неї (решту покаже dryRun).
  const violations = useMemo(() => (roleChanged ? [] : findViolations(catalog.constraints, merged, a.role)), [roleChanged, catalog.constraints, merged, a.role]);
  const invalid = useMemo(() => new Map(violations.map((v) => [v.permission, v.text])), [violations]);
  const dirty = changes.length > 0 || roleChanged;

  const input = () => ({ expectedVersion: a.accessVersion, ...(roleChanged ? { role } : {}), ...(changes.length > 0 ? { permissions: Object.fromEntries(changes.map((c) => [c.permission, c.to])) } : {}) });

  /** 409: оновлюємо дані й показуємо, що змінилося; чернетку скидаємо (вона була до чужих змін). */
  function onConflict() {
    setDialog(null);
    setDraft({});
    setRole(a.role);
    setNotice(null);
    setErrors([humanizeError(new BeautyApiError(409, "access_version_conflict", ""))]);
    refetch();
  }

  function fail(e: unknown, inDialog: boolean) {
    if (e instanceof BeautyApiError && e.status === 409) return onConflict();
    const msgs = accessErrorMessages(e);
    if (inDialog) setDialogError(msgs.join(" "));
    else setErrors(msgs);
  }

  function start(kind: "save" | "reset") {
    setErrors([]);
    setNotice(null);
    setDialogError(null);
    const opts = { onSuccess: (result: AccessSaveResult) => setDialog({ kind, result }), onError: (e: unknown) => fail(e, false) };
    if (kind === "save") dryRun.mutate(input(), opts);
    else dryReset.mutate(a.accessVersion, opts);
  }

  function confirm() {
    if (!dialog || dialog.kind === "toggle") return;
    setDialogError(null);
    const opts = {
      onSuccess: (result: AccessSaveResult) => {
        setDialog(null);
        setDraft({});
        setRole(result.role);
        setNotice(result.clamped.length > 0 ? result : "ok");
      },
      onError: (e: unknown) => fail(e, true),
    };
    if (dialog.kind === "save") save.mutate(input(), opts);
    else reset.mutate(a.accessVersion, opts);
  }

  function confirmToggle() {
    setDialogError(null);
    toggle.mutate(
      { isActive: !a.isActive, expectedVersion: a.accessVersion },
      {
        onSuccess: () => {
          setDialog(null);
          setNotice("ok");
        },
        onError: (e) => fail(e, true),
      },
    );
  }

  const roleOptions = Array.from(new Set<Role>([a.role, ...a.roleOptions]));
  const canChangeRole = a.editable && roleOptions.length > 1;
  const busy = dryRun.isPending || dryReset.isPending;
  const presetName = roleTitle(a.role);

  return (
    <>
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={a.isActive ? "ok" : "neutral"}>{a.isActive ? "Активний" : "Доступ вимкнено"}</Badge>
        <span className="text-sm text-(--muted)">Версія доступу: {a.accessVersion}</span>
      </div>

      {!a.editable ? (
        <p role="note" data-testid="not-editable" className="m-0 rounded-xl bg-(--page) px-3 py-2 text-sm">
          {a.notEditableReason === "owner"
            ? "Директор: права не змінюються й не обмежуються. Нижче показано їхні максимальні рівні."
            : a.notEditableReason === "self"
              ? "Це ваш власний рядок: змінювати власні права не можна, це робить керівник вище за рангом."
              : "Цей користувач має однаковий або вищий ранг: змінювати його права не можна."}
        </p>
      ) : null}
      {a.ownWithoutSpecialist ? (
        <p role="note" className="m-0 rounded-xl bg-[#FFF1CC] px-3 py-2 text-sm text-[#5C3900]">
          Для рівня «лише свої» потрібен прив&apos;язаний профіль майстра, а його немає: такий доступ дасть порожній результат.
        </p>
      ) : null}

      <div className="flex max-w-sm flex-col gap-1.5">
        <label htmlFor={`role-${user.id}`} className={FIELD_LABEL}>
          Роль
        </label>
        <select
          id={`role-${user.id}`}
          className={FIELD_INPUT}
          value={role}
          disabled={!canChangeRole}
          aria-describedby={`role-hint-${user.id}`}
          onChange={(e) => setRole(e.target.value as Role)}
        >
          {roleOptions.map((r) => (
            <option key={r} value={r}>
              {roleTitle(r)}
            </option>
          ))}
        </select>
        <p id={`role-hint-${user.id}`} className="m-0 text-[13px] text-(--muted)">
          {!canChangeRole
            ? a.editable
              ? "Роль змінити не можна: доступна лише пара «Адміністратор» і «Заступник». Майстру розширюють доступ правами."
              : "Роль не змінюється."
            : roleChanged
              ? "Після збереження застосується пресет нової ролі, потім ваші зміни прав."
              : "Зміна ролі застосовує пресет нової ролі."}
        </p>
      </div>

      <AccessEditor
        name={`u-${user.id}`}
        catalog={catalog}
        value={(k) => currentValue(a, k, draft)}
        state={(k, l) => optionState(a, k, l, draft)}
        onChange={(k, l) => setDraft((d) => ({ ...d, [k]: l }))}
        changed={new Set(changes.map((c) => c.permission))}
        invalid={invalid}
        moduleDisabled={a.moduleDisabled}
      />

      <div aria-live="polite" className="flex flex-col gap-2">
        {violations.length > 0 ? (
          <div role="alert" className="rounded-xl bg-[#FBE4E4] px-3 py-2 text-sm text-[#8A1F1F]" data-testid="violations">
            <p className="m-0 font-semibold">Збереження неможливе, поки права суперечать одне одному:</p>
            <ul className="m-0 mt-1 list-disc pl-5">
              {violations.map((v) => (
                <li key={v.permission}>{v.text}</li>
              ))}
            </ul>
          </div>
        ) : null}
        {errors.length > 0 ? (
          <div role="alert" className="rounded-xl bg-[#FBE4E4] px-3 py-2 text-sm text-[#8A1F1F]" data-testid="save-errors">
            {errors.map((t, i) => (
              <p key={i} className="m-0">
                {t}
              </p>
            ))}
          </div>
        ) : null}
        {notice ? (
          <div role="status" className="rounded-xl bg-[#DDF3E6] px-3 py-2 text-sm text-[#145A32]" data-testid="saved">
            Збережено.
            {notice !== "ok" ? <ClampNote clamped={notice.clamped} /> : null}
          </div>
        ) : null}
      </div>

      {a.editable || user.manageable ? (
        <div className="flex flex-wrap gap-2">
          {a.editable ? (
            <>
              <PrimaryButton onClick={() => start("save")} disabled={!dirty || violations.length > 0 || busy}>
                {dryRun.isPending ? "Перевіряємо…" : "Зберегти зміни…"}
              </PrimaryButton>
              <SecondaryButton
                onClick={() => {
                  setDraft({});
                  setRole(a.role);
                  setErrors([]);
                }}
                disabled={!dirty}
              >
                Скасувати зміни
              </SecondaryButton>
              <SecondaryButton onClick={() => start("reset")} disabled={busy}>
                {`Скинути до пресету «${presetName}»…`}
              </SecondaryButton>
            </>
          ) : null}
          {user.manageable ? (
            <SecondaryButton
              onClick={() => {
                setDialogError(null);
                setDialog({ kind: "toggle" });
              }}
            >
              {a.isActive ? "Вимкнути доступ…" : "Відновити доступ…"}
            </SecondaryButton>
          ) : null}
        </div>
      ) : null}

      {dialog && dialog.kind !== "toggle" ? (
        <ConfirmChangesDialog
          title={dialog.kind === "save" ? "Підтвердьте зміни доступу" : "Скинути права до пресету?"}
          userName={user.fullName}
          changes={dialog.result.changes}
          roleChange={dialog.result.roleChange}
          clamped={dialog.result.clamped}
          confirmLabel={dialog.kind === "save" ? "Підтвердити і зберегти" : "Скинути права"}
          busy={save.isPending || reset.isPending}
          error={dialogError}
          onConfirm={confirm}
          onClose={() => setDialog(null)}
        />
      ) : null}
      {dialog && dialog.kind === "toggle" ? (
        <ConfirmActionDialog
          title={a.isActive ? "Вимкнути доступ?" : "Відновити доступ?"}
          confirmLabel={a.isActive ? "Вимкнути доступ" : "Відновити доступ"}
          busy={toggle.isPending}
          error={dialogError}
          onConfirm={confirmToggle}
          onClose={() => setDialog(null)}
        >
          <p className="m-0">
            {a.isActive
              ? `${user.fullName} одразу втратить вхід у систему, активні сесії завершаться, а запрошення, які він видав, буде скасовано.`
              : `${user.fullName} знову зможе входити. Права лишаються такими, як були.`}
          </p>
        </ConfirmActionDialog>
      ) : null}
    </>
  );
}
