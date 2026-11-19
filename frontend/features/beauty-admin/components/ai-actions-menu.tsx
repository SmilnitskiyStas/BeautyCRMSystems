"use client";

import { useEffect, useId, useRef, useState, type KeyboardEvent, type RefObject } from "react";

export type AiAction = {
  key: string;
  label: string;
  hint: string;
  disabled?: boolean;
  onSelect: () => void;
};

/**
 * Меню «Дії ▾» (menu button): button[aria-haspopup=menu] + role=menu/menuitem.
 * Enter/Space/↓ відкривають, ↑/↓/Home/End рухають, Esc закриває з поверненням фокуса, клік поза меню закриває.
 * Попап позиціонується absolute всередині relative-обгортки (без portal, тому токени теми успадковуються).
 */
export function AiActionsMenu({ actions, busy = false, buttonRef }: { actions: AiAction[]; busy?: boolean; buttonRef: RefObject<HTMLButtonElement | null> }) {
  const id = useId();
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const items = useRef<(HTMLButtonElement | null)[]>([]);

  const enabled = () => actions.map((a, i) => (a.disabled ? -1 : i)).filter((i) => i >= 0);
  const focusItem = (i: number) => items.current[i]?.focus();

  function openAt(where: "first" | "last") {
    setOpen(true);
    const list = enabled();
    const target = where === "first" ? list[0] : list[list.length - 1];
    // Пункти з'являються після рендера.
    requestAnimationFrame(() => focusItem(target ?? 0));
  }
  function close(returnFocus: boolean) {
    setOpen(false);
    if (returnFocus) buttonRef.current?.focus();
  }

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  const onButtonKey = (e: KeyboardEvent<HTMLButtonElement>) => {
    if (busy) return;
    if (e.key === "ArrowDown") {
      e.preventDefault();
      openAt("first");
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      openAt("last");
    } else if (e.key === "Escape" && open) {
      e.stopPropagation();
      close(true);
    }
  };

  const onMenuKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const list = enabled();
    const cur = list.indexOf(items.current.findIndex((el) => el === document.activeElement));
    if (e.key === "Escape") {
      e.preventDefault();
      e.stopPropagation();
      close(true);
    } else if (e.key === "Tab") {
      setOpen(false);
    } else if (list.length > 0 && ["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) {
      e.preventDefault();
      const next =
        e.key === "Home" ? 0 : e.key === "End" ? list.length - 1 : e.key === "ArrowDown" ? (cur + 1) % list.length : (cur - 1 + list.length) % list.length;
      focusItem(list[next]);
    }
  };

  return (
    <div ref={wrap} className="relative w-full sm:w-auto" data-testid="ai-actions">
      <button
        ref={buttonRef}
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? `${id}-menu` : undefined}
        disabled={busy}
        onClick={() => (open ? close(false) : setOpen(true))}
        onKeyDown={onButtonKey}
        className="inline-flex min-h-11 w-full cursor-pointer items-center justify-center gap-2 rounded-[22px] border border-(--line-strong) bg-white px-5 text-sm font-semibold text-(--ink) disabled:cursor-not-allowed disabled:opacity-60 sm:w-auto"
      >
        {busy ? "…" : "Дії"}
        <span aria-hidden="true" className="text-xs">
          ▾
        </span>
      </button>
      {open ? (
        <div
          id={`${id}-menu`}
          role="menu"
          aria-label="Дії з розмовою"
          onKeyDown={onMenuKey}
          className="absolute right-0 z-20 mt-1 flex w-full min-w-64 flex-col gap-0.5 rounded-xl border border-(--line-strong) bg-white p-1.5 shadow-[0_8px_24px_rgba(30,27,46,0.18)] sm:w-72"
        >
          {actions.map((a, i) => (
            <button
              key={a.key}
              ref={(el) => {
                items.current[i] = el;
              }}
              type="button"
              role="menuitem"
              aria-disabled={a.disabled || undefined}
              aria-describedby={`${id}-${a.key}-h`}
              tabIndex={-1}
              onClick={() => {
                if (a.disabled) return;
                setOpen(false);
                a.onSelect();
              }}
              className={`flex min-h-11 w-full flex-col items-start gap-0.5 rounded-lg border-0 bg-transparent px-3 py-2 text-left text-sm font-semibold text-(--ink) hover:bg-(--page) focus-visible:bg-(--page) ${a.disabled ? "cursor-not-allowed opacity-60" : "cursor-pointer"}`}
            >
              {a.label}
              <span id={`${id}-${a.key}-h`} className="text-xs font-normal text-(--muted)">
                {a.hint}
              </span>
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
