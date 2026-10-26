"use client";

import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from "react";
import { PrimaryButton, SecondaryButton } from "./ui";

/**
 * Вбудований діалог підтвердження (alertdialog) для налаштувань ШІ: фокус на заголовок при відкритті, Esc = скасувати.
 * Повернення фокуса на кнопку-тригер робить батько в `onCancel`/після підтвердження.
 */
export function AiConfirmDialog({
  title,
  children,
  confirmLabel,
  busy,
  danger = false,
  onConfirm,
  onCancel,
}: {
  title: string;
  children: ReactNode;
  confirmLabel: string;
  busy?: boolean;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const id = useId();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === "Escape") {
      e.stopPropagation();
      onCancel();
    }
  };
  return (
    <div
      role="alertdialog"
      aria-labelledby={`${id}-t`}
      aria-describedby={`${id}-d`}
      onKeyDown={onKeyDown}
      className={`flex flex-col gap-3 rounded-xl p-4 ${danger ? "bg-[#FBE4E4] text-[#5E1414]" : "bg-[#FFF1CC] text-[#5C3900]"}`}
    >
      <h4 id={`${id}-t`} ref={heading} tabIndex={-1} className="text-base font-semibold outline-none">
        {title}
      </h4>
      <div id={`${id}-d`} className="flex flex-col gap-2 text-sm">
        {children}
      </div>
      <div className="flex flex-wrap gap-2">
        <PrimaryButton disabled={busy} onClick={onConfirm}>
          {busy ? "Зачекайте…" : confirmLabel}
        </PrimaryButton>
        <SecondaryButton disabled={busy} onClick={onCancel}>
          Скасувати
        </SecondaryButton>
      </div>
    </div>
  );
}
