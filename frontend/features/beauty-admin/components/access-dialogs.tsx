"use client";

import type { AccessChange, AccessClamp } from "../team/types";
import { levelLabel, permTitle, roleTitle } from "../team/logic";
import { ModalDialog } from "./modal-dialog";
import { PrimaryButton, SecondaryButton } from "./ui";

/** Список змін «було -> стало» для діалогу підтвердження й журналу. */
export function ChangeList({ changes, testId }: { changes: AccessChange[]; testId?: string }) {
  return (
    <ul className="m-0 flex list-none flex-col gap-1.5 p-0" data-testid={testId}>
      {changes.map((c) => (
        <li key={c.permission} className="rounded-xl bg-(--page) px-3 py-2 text-sm">
          <span className="font-semibold">{permTitle(c.permission)}:</span> {levelLabel(c.permission, c.from)} <span aria-hidden="true">→</span>
          <span className="sr-only"> змінюється на </span> <strong>{levelLabel(c.permission, c.to)}</strong>
        </li>
      ))}
    </ul>
  );
}

export function ClampNote({ clamped }: { clamped: AccessClamp[] }) {
  if (clamped.length === 0) return null;
  return (
    <div role="note" data-testid="clamped" className="rounded-xl bg-[#FFF1CC] px-3 py-2 text-[13px] text-[#5C3900]">
      <p className="m-0 font-semibold">Частину прав сервер обмежив:</p>
      <ul className="m-0 mt-1 list-disc pl-5">
        {clamped.map((c) => (
          <li key={c.permission}>
            {permTitle(c.permission)}: замість «{levelLabel(c.permission, c.requested)}» діятиме «{levelLabel(c.permission, c.applied)}» (
            {c.reason === "locked" ? "значення видав директор" : "вище за ваш власний рівень"}).
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Діалог підтвердження перед записом: результат `dryRun` (зміни ролі й прав, обмеження сервера). Esc і «Назад» - без запису. */
export function ConfirmChangesDialog({
  title,
  userName,
  changes,
  roleChange,
  clamped,
  confirmLabel,
  busy,
  error,
  onConfirm,
  onClose,
}: {
  title: string;
  userName: string;
  changes: AccessChange[];
  roleChange: { from: string; to: string } | null;
  clamped: AccessClamp[];
  confirmLabel: string;
  busy: boolean;
  error?: string | null;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const nothing = changes.length === 0 && !roleChange;
  return (
    <ModalDialog
      title={title}
      busy={busy}
      onClose={onClose}
      actions={
        <>
          <PrimaryButton onClick={onConfirm} disabled={busy || nothing}>
            {busy ? "Зберігаємо…" : confirmLabel}
          </PrimaryButton>
          <SecondaryButton onClick={onClose} disabled={busy}>
            Назад
          </SecondaryButton>
        </>
      }
    >
      <p className="m-0">
        Користувач: <strong>{userName}</strong>. Зміни застосуються одразу, а користувач побачить їх під час наступного запиту.
      </p>
      {roleChange ? (
        <p className="m-0 rounded-xl bg-(--page) px-3 py-2" data-testid="role-change">
          Роль: {roleTitle(roleChange.from)} <span aria-hidden="true">→</span>
          <span className="sr-only"> змінюється на </span> <strong>{roleTitle(roleChange.to)}</strong>. Права буде скинуто до пресету нової ролі, потім накладено ваші зміни.
        </p>
      ) : null}
      {changes.length > 0 ? <ChangeList changes={changes} testId="confirm-changes" /> : null}
      {nothing ? <p className="m-0 text-(--muted)">Нічого змінювати: права вже збігаються.</p> : null}
      <ClampNote clamped={clamped} />
      <div aria-live="assertive">{error ? <p role="alert" className="m-0 font-semibold text-[#8A1F1F]">{error}</p> : null}</div>
    </ModalDialog>
  );
}

/** Простий діалог підтвердження дії (вимкнення доступу, скасування запрошення). */
export function ConfirmActionDialog({
  title,
  children,
  confirmLabel,
  busy,
  error,
  onConfirm,
  onClose,
}: {
  title: string;
  children: React.ReactNode;
  confirmLabel: string;
  busy: boolean;
  error?: string | null;
  onConfirm: () => void;
  onClose: () => void;
}) {
  return (
    <ModalDialog
      title={title}
      busy={busy}
      onClose={onClose}
      actions={
        <>
          <PrimaryButton onClick={onConfirm} disabled={busy}>
            {busy ? "Виконуємо…" : confirmLabel}
          </PrimaryButton>
          <SecondaryButton onClick={onClose} disabled={busy}>
            Назад
          </SecondaryButton>
        </>
      }
    >
      {children}
      <div aria-live="assertive">{error ? <p role="alert" className="m-0 font-semibold text-[#8A1F1F]">{error}</p> : null}</div>
    </ModalDialog>
  );
}
