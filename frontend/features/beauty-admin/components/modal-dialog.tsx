"use client";

import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * Модальний діалог (role=dialog, aria-modal): фокус переходить на заголовок, Tab/Shift+Tab не виходять за межі діалогу,
 * Esc закриває (крім `busy`), після закриття фокус повертається на елемент, що відкрив діалог.
 */
export function ModalDialog({
  title,
  children,
  actions,
  onClose,
  busy = false,
}: {
  title: string;
  children: ReactNode;
  actions: ReactNode;
  onClose: () => void;
  busy?: boolean;
}) {
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const wrapper = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    heading.current?.focus();
    // Решта сторінки недоступна для AT і Tab, поки діалог відкритий (inert); відновлюємо лише те, що поставили самі.
    const made: Element[] = [];
    for (const el of Array.from(document.body.children)) {
      if (el === wrapper.current || el.hasAttribute("inert")) continue;
      el.setAttribute("inert", "");
      made.push(el);
    }
    const onDocKey = (e: globalThis.KeyboardEvent) => {
      const r = root.current;
      if (e.key !== "Tab" || !r || r.contains(document.activeElement)) return;
      const items = [...r.querySelectorAll<HTMLElement>(FOCUSABLE)];
      e.preventDefault();
      (items.length === 0 ? heading.current : e.shiftKey ? items[items.length - 1] : items[0])?.focus();
    };
    document.addEventListener("keydown", onDocKey);
    return () => {
      document.removeEventListener("keydown", onDocKey);
      for (const el of made) el.removeAttribute("inert");
      if (opener && document.contains(opener)) opener.focus();
    };
  }, []);

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === "Escape") {
      e.stopPropagation();
      if (!busy) onClose();
      return;
    }
    if (e.key !== "Tab" || !root.current) return;
    const items = [...root.current.querySelectorAll<HTMLElement>(FOCUSABLE)];
    if (items.length === 0) {
      e.preventDefault();
      heading.current?.focus();
      return;
    }
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    // Фокус поза діалогом (напр. після зникнення елемента) повертаємо всередину.
    if (!active || !root.current.contains(active)) {
      e.preventDefault();
      (e.shiftKey ? last : first).focus();
    } else if (e.shiftKey && (active === first || active === heading.current)) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  };

  // Портал виходить за межі `.beauty-admin`: без обгортки зникають токени теми (--ink, --accent…) і колір тексту стає кольором body (білий на білому у темній темі).
  // `contents` не створює власного блоку: обгортка лише передає змінні й колір нащадкам.
  return createPortal(
    <div ref={wrapper} className="beauty-admin contents">
    <div className="fixed inset-0 z-[60] flex items-end justify-center bg-black/50 p-3 sm:items-center">
      <div
        ref={root}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-t`}
        aria-describedby={`${id}-d`}
        onKeyDown={onKeyDown}
        className="flex max-h-[92vh] w-full max-w-[560px] flex-col gap-4 overflow-y-auto rounded-2xl bg-white p-5 shadow-[0_12px_40px_rgba(30,27,46,0.35)]"
      >
        <h2 id={`${id}-t`} ref={heading} tabIndex={-1} className="beauty-display text-xl font-semibold outline-none">
          {title}
        </h2>
        <div id={`${id}-d`} className="flex flex-col gap-3 text-sm">
          {children}
        </div>
        <div className="sticky bottom-0 -mx-5 -mb-5 flex flex-wrap gap-2 border-t border-(--line) bg-white px-5 py-3">{actions}</div>
      </div>
    </div>
    </div>,
    document.body,
  );
}
